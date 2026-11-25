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
| b | `route_path`: `dry_run` (snap, check, `geometry` with per-corner tangents and per-leg straight rest; reads the route sketch through the component, without `EditRoute`); `END_NOT_ON_ROUTE` when an end sits on a corner a fillet removed (a branch started at a filleted corner); corners deflecting less than `min_corner_deg` drawn sharp | the corner case fails before drawing with the miss distance and the nearest route point; a dry run leaves no file changed |
| c | `check_rebuild` rebuilds out-of-date features only (`EditRebuild3`) unless `force`; `import_from_to` and `route_path` (on exit) also no longer `ForceRebuild3`; `save_document(documents=[...])` saves exactly the named open documents | no more "every reference modified" after a check |
| d | `component_status` / `set_component_state`: `include_mates` off by default, `time_budget_s` (default 60) with `truncated`; `FindComponentByName` walks an instance path down the tree instead of building the path of every component | 3 nested names: 47.6 s -> 4.0 s; a mate scan stops at the budget |
| e | Counts by default: `check_rebuild` (`components_not_loaded` per reason), `save_document` (lists cut to 20), `routing_status` / `add_routing_point` (only add-in state and cable / component library unless `verbose`) | `check_rebuild` on the large assembly: 449 characters (was ~240 kB) |
| f | `component_status(boxes=True)` and `check_interference(boxes=True)`: world bounding boxes (mm) | matches the boxes computed by hand from part boxes and transforms |
| g | `import_from_to(replaces=..., configurations=[...])`: deletes the replaced route before the import and its `Harness_<n>` files after, shows the new route only in the listed configurations | one call, 10.6 s, instead of delete + import + suppression per configuration + manual file deletion |
| h | `add_routing_point(direction=[...], exit_depth=0.001)`: the exit face found from its outward normal (largest face, or nearest to `point`, within `exit_depth` of the outermost one); reports `body_extent_beyond_mm` and the candidates | two plugs: the rear faces picked by hand before, with the 0.2 mm terminal tips reported |
| i | `screenshot(focus=[...], isolate=True)`: zoom on the components' world box, SolidWorks' `Isolate` / `ExitIsolate` for the capture, sketches and curves hidden while isolated (`hide_sketches`). `zoom_box`: `ViewZoomTo2` takes the rectangle in VIEW coordinates (projected through `IModelView.Orientation3`), and where the box lands (`IModelView.Transform`) is measured and corrected, because the centre depended on the pan before the zoom | `zoom_box` / focus centred within 2 px; isolated captures show only the named components (route sketches of other harnesses had stayed visible) |

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
- Fixed: `route_path`'s `END_NOT_ON_ROUTE` first flagged every new end within 20 mm of a route
  point, which refused a normal 7.5 mm bundle between two breakouts. It now flags only an end
  on a corner a fillet removed (the tangent intersection of a route arc, within
  `free_end_tolerance`, default 0.5 mm).
- New `route_path(wire_paths={wire: [segment ids] | "<twin wire>"})` sets wires' paths on exit
  (`IWire.SetRoutePathForWire`). Open: SOLIDWORKS refused it (code 2) for the case it was written
  for, wires left `mismatched_to_component` by `RouteWires` (four wires on pins 1-4 of one
  12-way connector, whose twins to the same connectors route correctly). Root cause of that
  mismatch not found: from-to list, connector part (one connection point, no pins) and component
  library all checked; next: renumber the pins in a test list, or import those wires alone.

### 6. Improvement ideas from rerouting harnesses (2026-11-13, session 2)

Difficulties met while making two harness positions identical and redrawing a four-configuration
route network, with how they were worked around and what would remove them. Most valuable first.

| # | Difficulty | Workaround used | Proposed improvement |
|---|---|---|---|
| a | Redrawing a route with the same network (to change one branch) meant reading the sketch, rebuilding branches as corner points, turning connector branches into stub-end starts, and replaying 13 `route_path` calls per route (external scripts). | Scripts: sketch geometry -> branches -> `route_path` plans | `routing_status(route, as_branches=True)`: each branch between nodes as `route_path` input (stub-end start, corners, radii, wires carried). Then `route_path(bundles=[...])`: draw a whole network in one call, any order, exit once. |
| b | `routing_status` returns segments and per-wire segment IDs only for routes whose wires were routed in the session; older routes gave none. | Branch lengths and wire sets computed from `measure_curves(include_geometry)` and wire cut lengths (graph search, connector tips matched by cut length) | Compute segments from the route sketch when the Routing API has none; report per branch: length, wire set, end nodes. |
| c | A route of a configuration that was not active when its connector moved kept the old stub: the plan built from it started 2.5 mm off, and 10 wires stayed unrouted. | Shift the start by the plug move | `routing_status` / `route_path` report each connector's current stub end (CPoint position + stub) in assembly coordinates; `route_path` can snap a start to the nearest stub end within a tolerance and say so. |
| d | Equal-length solving (loop radius, bump height, corner positions) by bisection in a filleted-polyline model, outside the tool. | Python model + bisection; confirmed by cut lengths | `route_path(target_length_mm, vary=<point index, axis> | loop_radius)` (item 5b, still open): solve one free parameter to a target branch length. |
| e | `check_path_clearance(ignore=[...])` with a broad fragment hid a real clash with the harness's own receptacle (14 mm3). | Re-ran with narrower ignores | Report ignored hits apart (`ignored_hits`) instead of dropping them; warn when an ignored component overlaps the path. |
| f | `save_document(documents=[...])` on a large top assembly still rewrote rebuild-only references (bolts, valves, other harnesses), and read-only `routing_status` dumps left other routes modified (they enter edit focus). | Close all without saving, restore from version control, reopen | Save with options that do not save references (check `swSaveAsOptions_SaveReferenced`); have read-only route readers leave no modified flag, or report `marked_modified`. |
| g | Moving holes in a sheet-metal part: no tool deletes sketch entities or relations, so two of three holes on a shared construction line could not move. | Suppress the cut, new sketch with all holes, new cut, delete the old cut, re-mate what used the old hole | `edit_sketch_entities(sketch, delete=[...], move=[...])`, and a report of mates that reference faces a feature edit removes. |
| h | Face queries (`analyze_model(faces, near)`) on a 4000-face part exceeded the adapter's 30 s timeout. | Direct HTTP call with a long timeout | Prefilter faces by bounding box before computing per-face data; or treat `analyze_model` as a long tool. |
| i | No tool reads `IComponent2.ComponentReference` (reference designators matter for from-to imports). | COM probe from Windows PowerShell | Add `reference` to `component_status`. |
| j | `measure_curves(as_route=True)` fails on branched route sketches ("not one connected chain"). | Graph reconstruction outside | Return one route per branch for branched sketches. |
| k | `RouteWires` attached four wires to the wrong connector (`mismatched_to_component`); `SetRoutePathForWire` refused the fix. | Lengths taken from the layout instead | Report `mismatched_*` wires with the component SOLIDWORKS attached them to (needs an API to read it), so the cause can be found. |

Done 2026-11-14 (session 3, tested live): **c** (`routing_status(connectors=...)`: each
connection point's pin, location, direction and stub end in assembly coordinates), **e**
(`check_path_clearance` reports `ignored_hits` / `ignored_violations`, located on the path),
**h** (`analyze_model(faces|edges, near=...)` ranks faces by bounding box first and builds the
full record only for the nearest max(4k, 200); `analyze_model` is a long tool in the adapter),
**i** (`component_status` gives `reference`). Still open: a, b, d, f, g, j, k.

### 7. Per-cavity connectors and cut lengths (2026-11-14, session 3)

A connector modelled with one connection point makes all its wires meet in one point: no
fan-out, no bend or clearance check behind the housing, and SOLIDWORKS has no pin to match a
wire end to (it attached four wires of a 12-way connector to other connectors). Done:

| Tool | Change | Tested |
|---|---|---|
| `connector_cavities` (new, read-only) | the inner loops of the planar wire-exit face(s) along `direction` (edges tessellated, circles sampled), the openings of the most common size taken as cavities (latch windows, key slots, moulded text left in `other_openings`), sorted into rows / columns in the frame of a viewer at the wire side (`up` hint), numbered with `numbering={first, along, start}`; returns `pins` for `add_connector_pins` | an 8-way 2 x 4 housing: 8 cavities 1.7 x 2.25 mm, pitches 2.0 / 2.5 mm, pin 1 where the moulded digits put it |
| `add_connector_pins` (new) | one 3D sketch of the pin points (projected onto the exit face), one `InsertConnectionPoint` per pin (recreated reversed if it points into the housing), `ElectricalPinID` = pin through `GetDefinition` / `ModifyDefinition`, feature `<prefix><pin>`; `replace_existing` deletes the old points and the point-only sketches they used; sets the component type | 8 points, pin IDs read back after a rebuild; a from-to import made one stub per USED cavity, and the wires routed through their own cavities with no error |
| `routing_status(terminations=...)` | per-end allowances (`"<REF>.<pin>"` / `"<REF>"` / `"*"`) added to each wire: `termination {from, to}`, `total_cut_length_mm`, `terminations_missing` | 632.49 mm routed + 2 x 7.6 mm = 647.69 mm |
| `routing_status` on a part | `connection_points` with pin, port, location, direction, stub | |

Lessons: `ModifyDefinition` of a connection point returns false, yet the pin ID is kept (read it
back after a rebuild); SOLIDWORKS also sets `PortID` to the pin. A connection point's
`GetParents` does not list its point sketch: find point-only sketches whose children are the
points. The point may lie in a cavity opening (off the face material); the face only gives the
direction.

Session 4 (2026-11-14, a 40-wire, 8-connector harness in four configurations):

| Tool | Change | Tested |
|---|---|---|
| `route_path(paths=[...])` | several polylines `{name?, points, bend_radii / bend_radius}` in one route edit: all checked first (snap, fillet-corner ends, near-straight corners), then drawn; later ones snap onto the ends of earlier ones; `paths` in the result lists each with its length; `dry_run` accepts paths / fan_out alone | a whole route (13 bundles + 80 fan-out legs = 93 polylines) in one call per configuration, 40 wires routed, 0 errors, x4 |
| `route_path(fan_out=[...])` | per connector `{connector, join? / join_distance?, pins?}`: one straight leg from each USED cavity's stub end (the stubs the from-to import put on the route, found by position; or the pins named) to the join node, drawn before `points` / `paths`; reports pins, join, longest leg, steepest leg angle | 8 connectors (2-22 used cavities) per route, legs 7-29 mm, steepest 50 deg |
| `connector_cavities(numbering.count='cavities')` | numbers only the cavities in row / column order: staggered rows (each cavity its own grid column) | a 12-way staggered 2 x 6 housing numbered as its datasheet |
| `add_connector_pins` | no longer warns "ModifyDefinition returned false" (always false; the pin ID read-back is the check) | |

Lessons: the first leg out of a plug must hold the fan-out (join distance) plus the first bend's
tangent (r tan(theta/2)); on short legs move the first bend out a few mm. A datasheet cavity
view may be drawn from the mating face (mirror it), and some numbering is counter-clockwise
(1-3 down one column, 4-6 up the other): give such pins to `add_connector_pins` explicitly.
Seating a plug: compare the plug's shoulder distance (mating face to flange) with the header
pocket depth and align the header pins with the plug's pin entries; a solid supplier mating face
stops the plug at the pin tips, several mm short.

Session 5 (2026-11-16, wire bends at route nodes):

| Tool | Change | Tested |
|---|---|---|
| `routing_status(route=...)`, `route_path` (after routing) | per wire `max_kink_deg`, `min_arc_radius_mm`, `sharp_bends` [{at_mm, kink_deg, where}] (turns over `max_kink_deg`, default 15, at nodes of its own path: junctions of two of its segments, radius-0 corners inside one); `bends` lists the nodes with their wires | 12 existing routes (2-50 wires): 0-99 sharp nodes each, up to 147 deg, none flagged by SOLIDWORKS |
| `route_path(max_kink_deg, allow_sharp_junctions)` | before drawing, each polyline end on a node (route point, stub end, other polyline end) must leave within `max_kink_deg` of the straightest other segment there (fan-out siblings excluded), radius-0 corners must deflect less: else `SHARP_JUNCTION`, nothing drawn; `dry_run` returns `junctions` and `sharp_junctions` | compile + contract tests; draw not yet exercised on a live route |
| `route_path(fan_out=[{max_leg_angle_deg}])` | without `join` / `join_distance`, the join sits on the mean exit axis at the shortest distance keeping every leg within `max_leg_angle_deg` (default `max_kink_deg`; bisection, 2-250 mm) instead of 8 mm | offline equivalent: 6-way 12.5 mm, 16 of 28 cavities 56 mm |

| `route_path(fan_out)` smooth legs (default) | each leg [stub end, corner, join]: straight along the cavity axis, one fillet of `leg_radius`, arriving within `max_leg_angle_deg` of the bundle direction; `turn` gives a turn fan (legs layered by `layer_step` per upstream cavity row); `straight_legs` keeps the old legs | 2-wire pair: legs bend 14 deg at r 7, 0 sharp nodes, no route errors; dry runs on 6-way (turn), 4-way and 16-of-28 (axial) fans |
| `route_covering` (new) | list / add / remove coverings on route segments by id, wire set or wire count | covering added on a pair's trunk segment, OD read back |

Lessons: `IRouteProperty.AddCovering` stores a copy: properties set on the covering before the
call (OuterDiameter) read back 0; set them on `GetCovering()` afterwards (as the API's Edit
Coverings example does). `RemoveCovering()` takes no argument and returns nothing. The Routing API
has no Add Bends, Repair Route, Split Route or Fixed Length Covering (interactive only). A large
assembly opened in large-assembly mode keeps components lightweight even with
`open_document(lightweight=False)`; lightweight connectors make Edit Wires report "'From' or 'To'
component was not found" and drop the wire paths: resolve (`check_rebuild(resolve_lightweight=True)`)
before routing.
Lessons: SOLIDWORKS Routing's bend check covers arcs only; wires routed one by one still follow
the segment centrelines, so every node (stub end, join, breakout, bundle-to-bundle, covering
boundary) needs a planned shape: the leaving segment within a small angle of the arriving one,
then a filleted corner.

Ideas (open):

- **Turn-fans:** `fan_out` legs are straight. A plug whose wires exit toward a nearby wall needs
  each leg to leave its cavity straight, bend with the wire's radius and converge on the bundle
  line (`{"turn": [dx,dy,dz], "leg_radius": r}`: legs [stub end, corner, join]). Watch for
  collinear legs (cavities in one row turning along that row overlap).
- **Implicit-fillet criterion:** besides the angle limit, accept a node kink theta when the
  wire's minimum bend radius r fits as a fillet in both adjacent straights (r tan(theta/2) each);
  the length error 2 r tan(theta/2) - r theta stays under 0.3 mm up to 45 deg at r 7. That would
  let fan-outs converge faster where room is short.
- **check_interference with clearance:** a call on 6 components with clearance 0.001 took
  seconds once, then ran over 30 min on the same components after they moved a few mm (aborted
  by stopping the execution server). Suspect the per-neighbour Measure on a surface-only or
  large neighbour: add a time budget like component_status.
- **Smoothing in the tool:** `route_path` could insert the lead-in points itself (rotate a
  sharp end toward the segment its wires continue into, fillet the new corner), as the
  workspace planning script does today; it needs the wire set per polyline to pick the right
  partner at a breakout.

- **Fan-out end assignment for bundle lengths:** `routing_status` could return, per wire, the
  length of its connector ends (stub + fan-out) apart from the shared segments; the bundle
  mapping is a workspace script today.
- **Coverings:** a SOLIDWORKS Routing covering (`ICovering`, covering library) on route
  segments, so the bundle OD and the sleeve's start / end offsets are in the model; today only
  `check_path_clearance(diameter=...)` uses the finished OD.
- **Placement check for connectors:** report, per mated connector, which face of the plug sits
  in the header (the face with the connection points must point away): two plugs sat backwards
  for weeks because their single point was on the mating face.
- **Stale adapter:** a new parameter is dropped silently by an adapter started before it
  existed (`routing_status(connectors=...)` returned nothing); have the adapter warn about
  unknown parameters, or compare its contract hash with the execution server's.

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

## Session 6 (clash checks that cannot see surface models, concentric fans, save warning)

- `check_interference` / `check_path_clearance`: neighbours with sheet bodies but no solid body are
  invisible to Interference Detection, and a check that met one still returned `clear: true`.
  They now return `surface_only_count`, `surface_only_neighbours` and `surface_only_warning`
  (naming a configuration of that model whose name contains "solid", if any) and `clear: false`.
  Before: the warning only appeared with `clearance > 0` (`not_measured_neighbours`).
- `route_path` fan_out turn fans: `concentric` (default auto: true when the used cavities span at
  most `concentric_max_span`, 0.008 m, along the turn). Every leg starts its bend right after its
  stub, radius leg_radius + its offset from the inside of the turn; legs report `bend_starts_mm`,
  `radius_mm`; the fan reports `concentric`, `span_along_turn_mm`, `leg_radius_max_mm`. A long row
  turning edgewise keeps the staggered fan (`layer_step`): concentric radii there grow to the row
  width (a 22-way header: 50 mm, legs over 200 mm).
- `save_document`: `warning` when other open documents stay modified (typically the parent / top
  assembly a probe or configuration switch dirtied).

## Session 7 (converging axial fans)

- `route_path` fan_out `converge` (axial fans without join; default false): outer legs get two
  bends, toward the axis (at most `converge_max_deg`, default 45) right after the stub and back
  at a throat point (the cavity offset scaled to `throat_radius`, default 0.9 mm * sqrt(wires),
  min 2 mm), arriving within max_leg_angle_deg; legs near the axis keep one bend. A 22-way
  2-row header (42 mm wide) joins 39 mm behind its stubs with legs within 7.3 mm of each other;
  the single-bend axial fan would join `offset / tan(14 deg)` = 84 mm out, and a turn fan along
  the row made the wires differ by about the row width. Draw the bundle's turn after the join as
  a filleted path corner.
- Every smooth fan reports `leg_length_spread_mm`; turn fans carry a `note` on why their spread
  is large; an axial fan without converge reports `converge_hint` when converge would join more
  than 5 mm closer. Leg tuples now carry one radius per corner (`double[]`).
- Edit Route could block the server: `route_path` selected the route feature and called
  `IRouteManager.EditRoute()` without checking the selection. Right after `import_from_to` (which
  leaves the new route in edit), the selection can fail silently; EditRoute then opens its
  PropertyManager ("Select an existing route assembly") and waits for a pick, so the call hung
  until someone cancelled it in the GUI. Every EditRoute call site (route_path, guideline
  conversion, `edit_route_first`, the covering path) now checks `Select2` and refuses with
  `SELECTION_FAILED` (or skips with a warning), and refuses `WRONG_DOCUMENT` when the route is not
  a direct child of the active assembly (`RouteNotEditableHere`).
- Monitoring: a long call cannot be watched with the MCP `screenshot` (single-threaded server, it
  queues). Grab the SOLIDWORKS window from Windows (PrintWindow on the main window handle).
  Idea: a watchdog in the execution server that reports an open PropertyManager / modal dialog
  when a call exceeds a time limit.
- Ideas: a `throat_shape` (round instead of the scaled flat row) for a rounder bundle start;
  converge for turn fans (converge axially, then a bundle bend inside the fan).
- `converge` now also works on turn fans (bundle direction across the exit): each leg turns out of
  its cavity (elbow lead + leg_radius), tilts toward the axis and bends back at the throat. A
  26-way 13 x 2 header facing up, turning toward the wall: join ~31 mm out, legs within 10 mm.
  Raise `lead` on one fan to pass its elbow over a neighbouring fan's legs.
- `check_interference(clearance=...)`: distances come back null when the named component is a
  route cable inside a sub-assembly (Measure fails silently), so `violations` stays empty: report
  that as not measured instead of "no violation".

## Session 8 (a SOLIDWORKS crash during a probe, tools driving a stale instance, fan fillet room)

- **Crash**: `check_path_clearance` with 30 thin fan legs that all end in one join point. Two
  calls first failed with `PROBE_FAILED` ("the circular sweep failed") on legs whose tubes overlap
  the others at the join (the sweeps merged into one body), and the third call (two legs left out)
  killed SOLIDWORKS during the interference check ("The RPC server is unavailable"). Now:
  - every probe sweep is its own body (`ISweepFeatureData.Merge = false`), so overlapping tubes
    no longer fail the boolean;
  - a path whose sweep still fails is deleted and listed in `unswept_paths` (`clear` false) instead
    of failing the whole call (callers retried once per bad path);
  - more than `max_paths` (default 24) paths in one probe are refused (`INVALID_PARAMETER`, "split
    it"); probe a fan's outer legs and its bundle, not every leg.
- **Stale instance**: after the crash `ensure_ready` launched SOLIDWORKS through COM. The user
  later closed that window and started SOLIDWORKS again. The closed instance stayed alive (the
  execution server held a reference), kept the `SldWorks.Application` active-object registration
  and lost its `SolidWorks_PID_<pid>` ROT entry; `Marshal.GetActiveObject` kept returning it, so
  every tool drove an instance the user could not see (no Routing add-in: `ROUTING_NOT_LOADED`;
  documents opened there). `SolidWorksService.Attach.cs`: `AttachPreferred()` checks the
  GetActiveObject instance's PID against the `SolidWorks_PID_<pid>` ROT entries and, when it has
  none while another instance has one, binds the most recently started registered instance through
  its ROT moniker and releases the stale reference. `ensure_ready` / `/health` report
  `attachedPid`, `instanceWarning` (more than one SLDWORKS process) and `attachNote`; the adapter
  prints them (after an MCP restart).
- **Fan fillet room**: in `ConvergeFanLegs` a single-bend leg sized its fillet from the angle seen
  from the stub end, not from the corner. On a turn fan tilted out of 90 deg (bend 60-85 deg) the
  fillet overran the first straight by up to 1 mm (`min_straight_mm` -1.0 in the dry run). The
  corner distance is now iterated from the corner's own deflection, like corner 1 of a two-bend
  leg (every leg keeps >= `lead`).
- Tool gap: no tool moves or deletes a sketch entity. Moving two undimensioned hole circles of a
  cut sketch (to keep the mates on the cut's faces) needed a pywin32 script: bind the instance
  through its `SolidWorks_PID_<pid>` ROT entry (`GetActiveObject` can return a stale one), early
  bound wrappers from `gencache.EnsureModule` of the SldWorks type library (late binding fails on
  returned objects), `EditSketch`, `ISketchPoint.SetCoords`, `InsertSketch2`. Idea: an
  `edit_sketch_geometry` tool (move points / circle centres, delete entities).
- Ideas: `fan_out` legs in the dry run could carry a ready-made probe list (outer legs only);
  `converge_max_deg` has no effect on a small 2 x 3 header (its legs join on the arrival-angle limit).

## Session 9 (flattened routes and 2D harness drawings: flatten_route, route_drawing)

- `IRouteManager.CreateFlattenRoute(flattenType, connectorOption, formboardWidth, formboardHeight,
  segmentOrientation, drawingOption, sheetFormat, elecBOM, cutList, connectorTable, autoBalloons)`
  (select the route component in its parent assembly first). With `drawingOption=true` SOLIDWORKS
  makes the drawing and its view, then throws RPC_E_SERVERFAULT ("The server threw an exception")
  before the first table, with any templates (the connector table must be a `.sldbomtbt`, not the
  `.sldtbt`), whenever the connectors are components of the PARENT assembly (the normal Routing
  layout); its connector blocks then read "External Connector". With `drawingOption=false` the
  flatten works (return 0; `swFlattenRouteErrorType_e` 1-4 are errors): a configuration
  `<Type>FlattenedRoute<n> - <config>` in the route assembly, a planar 3D sketch (Z = 0), a
  FlattenedRoute feature and a virtual `Flattened_Cable^<route>` part; the 3D route is untouched.
  SOLIDWORKS leaves the route assembly active in the flat configuration and saves it.
- `flatten_route` = that flatten + the route assembly saved (a drawing view shows the flattened
  cable only once saved) + every route segment's 3D length (`IElectricalRoute.GetSketchSegments`
  while the route is edited in context) against its flat length
  (`IRouteManager.GetElectricalFlatRoute(<FlattenedRoute feature name>)`, not the configuration
  name; the route assembly must be active in the flat configuration). First harness: 0.002 mm max.
- `route_drawing` builds the drawing itself:
  - the view must be created while the route assembly SHOWS the flat configuration
    (`ShowConfiguration2`, restored after); a view made otherwise and switched with
    `ReferencedConfiguration` kept the 3D cable component (empty-looking view);
  - connector positions: `IElectricalFlatRoute.GetConnectorData(name)` found no connector by any
    name tried (component name, reference, instance path) for parent-assembly connectors, so each
    wire end is located through its stub: the route segment whose 3D sketch touches the connector's
    connection point (`ConnectorPointsWorld`, mapped into the route frame), then the end of that
    segment's flat sketch FARTHEST from the wire's other segments (flat legs meet the bundle through
    short splines that may belong to another segment, so "not coincident" picked the wrong end);
  - circuit summary: `IView.InsertBomTable4(false, x, y, 1, 2, flatConfig, bom-circuit-summary.sldbomtbt,
    ...)` (with `UseAnchorPoint=true` it jumps to the sheet format's BOM anchor) lists every wire with
    colour, length and from / to pins; the standard BOM template listed only some wires;
  - connector tables: general tables (`IDrawingDoc.InsertTableAnnotation2`, `MergeCells`, `Text`)
    from the route's wires; labels and bundle lengths as notes (`InsertNote` +
    `IAnnotation.SetPosition2`; a note's anchor is its top-left corner);
  - auto sheet: smallest ISO landscape sheet that holds the flat view at 1:1 plus the tables, sheet
    format `<size> - landscape.slddrt` from the sheet format folders (`swFileLocationsSheetFormat`).
- Test 2026-11-18 (then undone): connectors moved INTO the route sub-assembly
  (`IAssemblyDoc.ReorganizeComponents`, positions kept, route still routed) do not fix it:
  CreateFlattenRoute(drawingOption=true) still throws before the first table, the flat harness ends
  still carry "External Connector" boxes and the connectors show as a separate row of 3D models
  away from the harness. The 2022+ "external connectors" selection of the Flatten Route
  PropertyManager has no API parameter (CreateFlattenRoute is the 2011 signature). Decision (user):
  no SOLIDWORKS 2D flatten for now; the harness 2D drawings are made in the ECAD layout from the 3D
  route data. flatten_route / route_drawing stay as tools (flat-vs-3D length check, quick drawing).
- Ideas: fill the title block (drawing custom properties); wire colours on the flat legs; connector
  drawing blocks for the "blocks" display; dimensions instead of notes for bundle lengths; place
  each connector table next to its connector; a formboard sheet split for long harnesses.

## Session 10 (re-routing after a mechanical release: route_follow, world placement, summaries)

Found while moving the connectors of four harnesses onto a new mechanical release and redrawing
their routes. Each item replaced a client script written three times in a day. Adapter-side
(Python, `server.py` + `assembly_math.py`) except the stub check (C#, `RouteStubs.cs`).

| Item | Change | Why |
|---|---|---|
| a | `route_follow(route, plan, from_to, ...)`: coverings exported, `import_from_to(replaces)`, free plan ends moved onto the new stub ends / fan joins with their lead points, dry run (sharp junctions, cycles, `no_room`), draw, coverings re-applied, status / CSV written; returns a short summary | the same 100-line driver per harness; the ends to move were found by hand |
| b | `routing_status(check_stubs)` (default on with `route`): every used connection point, its offset from the route sketch, `detached` | a stub stays where the connector was at the import; nothing reported it (fans failed later with `NO_USED_CAVITIES`) |
| c | `place_component(world_transform | world_offset, configurations)`: target in the active assembly's frame, converted to the parent's frame and set in the parent's document, in each configuration, read back per configuration and from the top | fixed components of multi-configuration harnesses keep one position per configuration: moves reverted |
| d | `seat_on_stud(component, stud_component, face_component, gap, apply)`: hole coaxial with the nearest parallel stud, contact face on the terminal face | ring lugs were seated with a face dump + script per device |
| e | `analyze_assembly(faces)` filters (`kind`, `axis`, `radius_min/max`, `near` / `within`, `world`) | a contactor model has 2,890 faces |
| f | `check_interference(group_by_pair)`, `component_status(fields)`, `compact` on `set_component_state` / `import_from_to` / `route_covering` / `routing_status`, `check_path_clearance(frame_component)` | results echoed every component, wire and segment |

Tested live (2026-11-25, ~1000-component assembly): stub check (20 stubs, 0 detached, compact
status ~1 kB), `route_follow` dry run on an 18-end plan (all ends found, 0 moves, dry run clean),
`place_component(world_offset)` on a part one and two levels down (5 configurations held, world
check 0.0 mm), `seat_on_stud` on two ring lugs seated by hand before (0.000 / 0.002 mm), face
filters, `group_by_pair`, `frame_component`. The first `seat_on_stud` picked a face on the wrong
side of the lug (one "up" direction for every candidate): each candidate face is now paired with
the lug face looking at it. Not yet run: `route_follow` with `reimport` / `draw` (its steps are
the tools the drivers it replaces called).
