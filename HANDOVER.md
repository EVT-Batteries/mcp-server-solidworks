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

### 5. SOLIDWORKS Routing workflow: time and token savers (done 2026-11-13, two items open)

Found while routing two equal-length harnesses in a large assembly (about 1000 components) with
`import_from_to` + `route_path`. Implemented and tested live on that assembly (numbers below are
from those tests):

| Item | Change | Effect measured |
|---|---|---|
| a | New tool `check_path_clearance` (`Services/SolidWorksService.PathClearance.cs`): the path(s) as circular sweeps in a temporary part, inserted at the origin, `check_interference` on it, each hit mapped to `leg` / `at_mm_along_path` / `near_corner`, then the component, part and file removed. `paths` checks a trunk and its branches together; `ignore` drops the connectors a path ends in | 27-37 s per candidate (was an import + draw + rebuild + check, several minutes); a known clashing path gave the same parts, on the right legs; the final route came back `clear` |
| b | `route_path`: `dry_run` (snap, check, `geometry` with per-corner tangents and per-leg straight rest; reads the route sketch through the component, without `EditRoute`); `END_NOT_ON_ROUTE` when an end misses a route point by less than `free_end_tolerance` (a branch started at a filleted corner); corners deflecting less than `min_corner_deg` drawn sharp | the corner case fails before drawing with the miss distance and the nearest route point; a dry run leaves no file changed |
| c | `check_rebuild` rebuilds out-of-date features only (`EditRebuild3`) unless `force`; `import_from_to` and `route_path` (on exit) also no longer `ForceRebuild3`; `save_document(documents=[...])` saves exactly the named open documents | no more "every reference modified" after a check |
| d | `component_status` / `set_component_state`: `include_mates` off by default, `time_budget_s` (default 60) with `truncated`; `FindComponentByName` walks an instance path down the tree instead of building the path of every component | 3 nested names: 47.6 s -> 4.0 s; a mate scan stops at the budget |
| e | Counts by default: `check_rebuild` (`components_not_loaded` per reason), `save_document` (lists cut to 20), `routing_status` / `add_routing_point` (only add-in state and cable / component library unless `verbose`) | `check_rebuild` on the large assembly: 449 characters (was ~240 kB) |
| f | `component_status(boxes=True)` and `check_interference(boxes=True)`: world bounding boxes (mm) | matches the boxes computed by hand from part boxes and transforms |
| g | `import_from_to(replaces=..., configurations=[...])`: deletes the replaced route before the import and its `Harness_<n>` files after, shows the new route only in the listed configurations | one call, 10.6 s, instead of delete + import + suppression per configuration + manual file deletion |
| h | `add_routing_point(direction=[...], exit_depth=0.001)`: the exit face found from its outward normal (largest face, or nearest to `point`, within `exit_depth` of the outermost one); reports `body_extent_beyond_mm` and the candidates | two plugs: the rear faces picked by hand before, with the 0.2 mm terminal tips reported |
| i | `screenshot(focus=[...], isolate=True)`: `ViewZoomToSelection` on the components, SolidWorks' `Isolate` / `ExitIsolate` for the capture. `zoom_box` bug fixed: `ViewZoomTo2` takes the rectangle in VIEW coordinates; the box is projected through `IModelView.Orientation3` | `zoom_box` now frames the same region as zoom-to-selection on that component |

Still open:

- **b, `target`**: solve one free parameter (a service-loop radius, a node station) for a wire
  length target inside `route_path`. Today the caller still models the filleted polyline itself
  (`dry_run` gives the per-corner tangents and lengths to build on). See §1 for the loop shape.
- **Predicted wire lengths in a dry run**: a wire's length is the route segments it runs through
  plus both stubs. `dry_run` returns the new geometry only; summing it with the existing segments
  (from `routing_status`) per wire is left to the caller.
- `check_path_clearance` leaves the assembly marked modified (a component added and removed) and
  the temporary part file locked until a later call deletes it (SolidWorks keeps the removed
  component's model in memory).
- `route_path`'s `END_NOT_ON_ROUTE` also flags a deliberate free end within `free_end_tolerance`
  (20 mm) of the route: pass `allow_free_ends=true` for those.

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
