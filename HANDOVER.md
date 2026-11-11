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

### 4. Smaller things noticed

- `measure_curves` on a large assembly with `include_geometry` returns well over 100 kB; with
  `as_route` available, consider omitting `per_segment` geometry unless asked.
- `check_rebuild(recursive=True)` on a large assembly returns more than 100 kB; a `compact`
  summary (counts + problems only) would help the same way.
- A failed `create_3d_path` can leave the 3D sketch open in edit mode, and the next call fails
  with `SKETCH_ACTIVE`; there is no tool to leave a sketch. Either exit the sketch on every error
  path, or add an `exit_sketch` tool.
- The `T50RFT6`-style tie-mount and battery-module models seen in the field were surface bodies:
  `analyze_model(faces)` and `check_interference` cannot use them. A `analyze_model` mode that
  reads surface bodies (bounding box per face, planes) would help locate saddles and connector
  openings without exporting to STEP.
