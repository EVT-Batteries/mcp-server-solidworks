# Handover: harness-routing tool improvements

Open improvements found while routing wire harnesses with SolidPilot (3D-sketch bundles swept as
circular profiles, equal-length variants, plugs fixed in a harness sub-assembly). Each one replaces
a throw-away client script that called `POST /api/tool/execute` in a loop. Done so far:

- **Busy gate** (`Infrastructure/StaExecutor.cs`): a call that cannot start within 20 s fails
  with `SOLIDWORKS_BUSY` instead of queuing behind a hung COM call; `/health` reports `busy`.
- **`create_3d_path` without snapping** (`Services/SolidWorksService.Path3D.cs`): sketch inference,
  automatic relations and point snaps are off while drawing. With only `AddToDB`,
  `Create3PointArc` still snapped arc ends onto points a few mm away in a zoomed-out view (a loop
  crossing over itself, two short legs); the result was an arc of the wrong length, flagged only
  by the `solved length ... differs` warning.
- **`measure_curves(as_route=True)`**: `route_points` + `route_bend_radii` per sketch and sweep
  path, the exact input of `create_3d_path`, so a route is read, edited and redrawn without
  rebuilding corners from tangent points by hand.
- **`check_interference(compact=True, ignore_same_parent=True)`**: counts instead of the long
  `not_measured` / `resolved_from_lightweight` lists (which ran to tens of kB on a large assembly
  with surface-only models), and pairs inside one sub-assembly (a harness's own bundles at their
  split points) dropped and counted.

## To do

### 1. Equal-length service loops in `create_3d_path`

Variants of one harness (left / right hand, several identical boards) must have the same bundle
length, so the shorter routes carry a service loop sized to the longest route. Today the caller
bisects a loop radius with repeated `create_3d_path` calls (about 30 draws per bundle).

Proposal: optional `service_loop` parameter, e.g.
`{"after_point": 4, "station_m": 0.03, "turn_toward": [0,0,1], "step": [0,0.005,0], "target_length_m": 1.042547}`:

- insert a flat full circle on leg `after_point` -> `after_point + 1`, `station_m` from its
  start: four corners of a square of side 2R, rounded with radius 0.95 R, turning toward
  `turn_toward` (in the plane of the leg and that vector), coming back and crossing over the
  incoming leg offset by `step` (at least the bundle diameter, so it does not intersect itself);
- solve R in C# so the solved sketch length equals `target_length_m`: the theoretical length
  from `DrawFilletedPolyline` is monotonic in R (about 6.9 m of route per m of R for this loop
  shape), so bisect on it without drawing, draw once, and check the solved sketch length;
- fail with a clear error when R would fall below a minimum (`min_radius_m`, the bundle's bend
  radius): the slack is too small for a full circle.

Return the solved R with the usual segment list. Test: draw a straight 1 m route with a loop for
1.05 m and check `length_m` to 1e-6 and that no two non-adjacent segments come closer than `step`.

### 2. `component_status` across configurations

Checking that a component is present in every configuration of an assembly needs one
`manage_configurations(activate)` + `component_status` per configuration, which changes the
active configuration (and dirties the document). Proposal: `configurations: "all" | [names]`
(read-only): per component, its suppression state in each listed configuration, read with
`IConfiguration`/`IComponent2.GetSuppression2` through `IConfigurationManager` without activating
(or activate and restore the original active configuration inside the call, and say so).

### 3. Batch screenshots of parts

Documenting a harness takes one isometric screenshot per bundle part with a caption: open, shoot,
close, repeated. Proposal: `screenshot_files(files: [{path, caption, out}], view)` that opens each
file, captures it and closes it again without saving (never closing documents the user had open
before the call).

### 5. SOLIDWORKS Routing workflow: time and token savers (open, 2026-11-13)

Found while routing two equal-length harnesses in a large assembly (about 1000 components) with
`import_from_to` + `route_path`. Ranked by the time they would have saved. Start with a, c, d and
e. Each needs a rebuild of the execution server and an MCP restart (skill
`solidpilot-mcp-development`).

**a. Check a path without drawing a route.** Each candidate path cost several minutes:
re-import the from-to, `route_path`, a rebuild of the top assembly, `check_interference` on the
tube. One route took five candidates to clear the parts around it.

Proposal: a read-only `check_path_clearance(points, bend_radii, diameter, margin)`. It builds a
temporary swept tube on the filleted polyline (in a scratch part inserted at the origin, or as a
temporary body), runs the interference check against the active assembly, deletes the tube and
returns:
- each hit: component, volume, box;
- the **leg or corner index** of the path it falls on, so the caller moves the right point
  instead of matching boxes to the path by hand.

**b. Preview lengths and check geometry in `route_path`.** The caller kept its own model of
`route_path`'s geometry (per corner t = r·tan(θ/2), arc r·θ, plus the stubs; it matched
SOLIDWORKS cut lengths to 0.01 mm) and bisection solvers to equalize wires. Proposal:
- `dry_run=True` returns, without drawing:
  - the predicted cut length of every wire of the route, with the already-drawn segments and the
    stubs;
  - the slack of each leg: its length minus the tangents at both ends; negative means the radii
    do not fit.
- Fail with a clear error on the two shapes that broke routes:
  - a corner of a few degrees with a radius, which split the route into disconnected segments
    and left wires unrouted;
  - a branch start inside a corner fillet of the existing route: it does not snap ("start is a
    new route point (nearest existing 3.9 mm)"), and the branch's wires stay unrouted.
- Optional `target`: {wire group, length}, solved over one named free parameter (a service-loop
  radius, a node station along a leg). See §1 for the loop shape.

**c. Do not dirty every reference.** After `check_rebuild(rebuild=True)` (ForceRebuild3) on the
top assembly, the next `save_document` rewrote 136 referenced files (214 MB) with no intended
change. Undoing it meant closing without saving, restoring the files from git and reopening.
Proposal:
- `check_rebuild` rebuilds only what is out of date (EditRebuild3) unless `force=True`.
- `save_document(documents=[...])` saves only the named open documents, and
  `save_document(references="none")` saves only the active one.
- The result reports `dirty_references` as a count, with names on request.

**d. Bound slow calls.** `component_status` with named components reads their mates by default.
On the large assembly it held SolidWorks for more than 25 minutes, and nothing could cancel it
(the busy gate only rejects later calls). Proposal:
- `include_mates` defaults to false;
- the mate scan caches the mate list per assembly (TODO "Next improvements" 8);
- a `time_budget_s` (default 60) on every whole-assembly loop: stop and return partial results
  with `truncated: true`.

**e. Compact default outputs.** Proposal: counts by default for the long lists below, the full
lists behind `verbose=True` (as `check_interference(compact=True)` already does):

| Call | Default output today |
|---|---|
| `save_document` | `saved_references`: about 150 paths |
| `check_rebuild` | 240 kB of `components_not_loaded`: every lightweight component |
| `add_routing_point`, `routing_status` | the full routing-library `file_locations` and `from_to_headers` block on every call |

**f. World bounding boxes in `component_status`.** Finding free space meant opening each nearby
part, reading `analyze_model(geometry)` `box_m` and transforming it by hand (7 parts, 3 calls
each). `Interference.cs` already has `ComponentBox` (`IComponent2.GetBox`). Proposal:
`boxes=True` returns each component's world box (and per-body boxes for multi-body parts), and
`check_interference` returns its neighbours' boxes.

**g. Replace a route in one call.** Each redraw was:
1. `edit_feature(delete)` on the old route;
2. `import_from_to`;
3. `route_path` calls;
4. `set_component_state` in every configuration, to show the route only in its own;
5. a manual delete of the old route's `Harness_<n>` files (`.SLDASM`, `-fromToList.xml`,
   `-segmentList.xml`); 15 files were left over.

Proposal: `import_from_to(replaces="<old route>", configuration="<name>")` deletes the old route
feature and its files, and suppresses the new route in the other configurations of the assembly.

**h. Connection point at the real wire exit.** One plug's connection point sat on an internal
face, so every route ended 8.7 mm inside the plug body (found only by a clash check). Proposal:
`add_routing_point(direction=[x,y,z])` without `face_index` picks the outermost planar face
whose normal is that direction (from the bodies' extent along it) and puts the point at its
centre. The result reports the body extent beyond the point, which should be 0.

**i. Screenshots of named components.** `screenshot(zoom_box)` framed the wrong region in a large
assembly (`ViewZoomtofit2` then `ViewZoomTo2` with model-coordinate corners). Check how
`ViewZoomTo2` maps its corners (it may take view coordinates). Proposal:
- `screenshot(focus=[components])` zooms to the union of their world boxes;
- `isolate=True` hides the rest and restores it afterwards.

Until then, pictures of a harness come from opening its assembly on its own.

### 4. Smaller things noticed

- **Clearance of multi-body components (fixed).** `IMeasure` on two selected components looked
  at only part of a SOLIDWORKS Routing tube (one body per route segment): it gave 151.5 mm
  between two bundles 0.04 mm apart. `check_interference` now measures a component with several
  solid bodies body to body (`IComponent2.GetBodies3`, `IBody2.Select2`, up to 2500 body pairs)
  and keeps the smallest distance: 0.039 / 0.041 mm, as the route centrelines give.
  `compact=False` also lists `neighbours` and every pair's `distances_mm`. Large multi-body
  neighbours make it slower (one Measure per body pair).

- `measure_curves` on a large assembly with `include_geometry` returns well over 100 kB; with
  `as_route` available, consider omitting `per_segment` geometry unless asked.
- `check_rebuild(recursive=True)` on a large assembly returns more than 100 kB; a `compact`
  summary (counts + problems only) would help the same way.
- A failed `create_3d_path` can leave the 3D sketch open in edit mode, and the next call fails
  with `SKETCH_ACTIVE`; there is no tool to leave a sketch. Either exit the sketch on every error
  path, or add an `exit_sketch` tool.
- Supplier models are often surface bodies only (e.g. an assembly saved as part with "exterior
  faces"): `analyze_model(faces)` and `check_interference` cannot use them (the latter lists them
  as not measured). `analyze_model(geometry)` now reports `surface_bodies`, and `insert_part` can
  add the solid bodies of the same model; an `analyze_model` mode that reads surface bodies
  (bounding box per face, planes) would still help locate saddles and connector openings without
  re-importing.
