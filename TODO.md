# Fork of SolidPilot: what was added, and what to do next

This fork adds tools for routed geometry (cables, wire
bundles, tubes), measurement and assembly health to upstream SolidPilot. They were built and
tested against SolidWorks 2026 SP2 (34.2.1) between 2026-11-09 and 2026-11-10. Development rules,
build steps and API lessons: [`.agents/skills/solidpilot-mcp-development/SKILL.md`](.agents/skills/solidpilot-mcp-development/SKILL.md).

## Added

| Tool | What it does |
|---|---|
| `measure_curves` | Read-only true 3D lengths of sweep paths and 3D sketches (lines, arcs, splines, ellipses), in a part or across every component of an assembly. With `include_geometry`, also each segment's 3D points, in assembly coordinates in an assembly. |
| `screenshot` | PNG of the active view (`SaveBMP`; the document is never written), with an optional standard orientation, zoom to fit and a caption band. |
| `create_3d_path` | 3D sketch route from XYZ points: a polyline with exact tangent bends and one radius per corner, or a spline. Given an existing sketch, it redraws it in place and its sweep follows. It reports the expected length and warns when the solved sketch differs from it. |
| `sweep_circular` | Circular-profile sweep of a diameter along a path sketch. Re-running it on an existing sweep changes its diameter. |
| `place_component` | Sets an existing component's transform and fixed state. |
| `check_rebuild` | SolidWorks' "What's Wrong" for the active document and every loaded sub-assembly and part. For each error or warning: the code and its meaning, and for a broken mate, the components it references. |
| `component_status` | Read-only status of components, nested ones by instance path: suppressed / lightweight / resolved, hidden, fixed, missing file, suppressed parent, referenced configuration, transform, and the mates referencing them with their errors. Filters by path substring and state. |
| `set_component_state` | Suppresses, resolves or makes lightweight components, shows or hides them, and sets the configuration they reference, in the active configuration; returns their status and mates after the rebuild. |
| `manage_configurations` | Lists, adds, activates, renames or deletes configurations, and sets their description and "suppress new components". |
| `check_interference` | Interference detection (`InterferenceDetectionManager`) for named components against the leaf parts whose bounding box comes near them, or for the whole assembly: overlapping pairs with volume and overlap box. With `clearance`, the minimum distance (SolidWorks Measure) to each neighbour and every pair closer than it (an interfering pair counts as 0). |
| `import_step` | STEP / IGES / Parasolid to a `.SLDPRT`: imports, saves, closes the import document and reopens the saved part, so part queries work at once; reports bodies and bounding box, refuses to overwrite unless asked. |

Changes to upstream tools:
- `insert_component` also inserts sub-assemblies (`.sldasm`). Its description now says that
  x / y / z place the part's bounding-box centre, not its origin. It normalizes the file path
  (AddComponent5 returned null for `C:/dir/part.sldprt` when the part was not loaded).
- `add_mate`, `analyze_assembly(faces|edges)` and `place_component` find components inside
  sub-assemblies by instance path (`Sub-1/Part-1`), or by a unique tail of it. An unknown name
  fails with the closest instance paths (same base name, substring or a small edit distance).
- `add_mate` compares What's Wrong before and after the mate: when AddMate5 reports an error
  (5 = over-defined) or the rebuild shows new problems, it deletes the new mate, rebuilds and
  fails with `MATE_OVER_DEFINED` / `ADD_MATE_ERROR` / `MATE_REBUILD_ERROR` naming them
  (`keep_on_error=True` keeps the mate and reports it).
- **Lightweight support**: `open_document(lightweight=True|False)` (OpenDoc7 with a document
  specification) reports `lightweight_components=N`; `analyze_assembly(faces|edges)`, `add_mate`
  and `check_interference` resolve the lightweight components they need (and their lightweight
  parents) and list them as `resolved_from_lightweight`; `analyze_assembly` components carry
  `lightweight: true`; `check_rebuild` and `measure_curves` say which components were not
  checked because they are lightweight and take `resolve_lightweight=True`; component
  suppression is read with `GetSuppression2` (`IsSuppressed()` is true for some lightweight parts).
- **Errors reach the model**: every adapter tool is registered through a wrapper that turns any
  exception into an MCP `ToolError` with its text (the SDK reduced all others to "Error executing
  tool <name>"); FAILED responses carry their code and message; a timeout names the tool and the
  budget and says SolidWorks may still finish the call; a lost connection says so. Assembly-wide
  tools wait `LONG_HTTP_TIMEOUT` (900 s default, `.env`) instead of `HTTP_TIMEOUT` (30 s).
  Covered by `adapters/claude/tests/test_error_passing.py`.

Each tool is registered in the contract (`tool-schemas.json`), the adapter and the README, and the
adapter tests pass (58 tools).

## Known limits

- **No automated tests for the new CAD tools.** They were verified against live SolidWorks only:
  sweep volumes equal to pi * r^2 * L, solved route lengths equal to the expected ones,
  `check_rebuild` clean after the changes; `check_interference` on two plugs overlapping by
  3.24 mm (888 mm3 found) and a 32.4 mm gap (measured); `add_mate` on two fixed plugs (error 5,
  mate deleted, assembly clean); `import_step` on a part STEP and an assembly STEP; a scratch
  assembly opened lightweight (faces, interference, rebuild and measure paths). Only the error
  passing has offline tests.
- **Fixed-coordinate routes.** A route is drawn from fixed coordinates; it is not linked to the
  parts it connects. When a part moves, the route has to be redrawn with `create_3d_path`.
- **Slow face queries on large parts.** `analyze_model(faces)` and `analyze_assembly(faces)`
  enumerate every face, which takes minutes on large imported parts (thousands of faces), even
  with `near`.
- **Face indices differ between part and component.** On a multi-body part, the indices from
  `analyze_model(faces)` do not match those from `analyze_assembly(faces, component)`, so a mate
  needs the assembly-side index.
- **Paths are polyline or spline only.** `create_3d_path` cannot mix splines and straight legs in
  one sketch, and it adds no dimensions or relations: a redrawn route is not parametric.
- **`check_rebuild` skips suppressed components.** It cannot check them, because they are not
  loaded.
- **Mates held by a sub-assembly are not seen from the top.** `component_status` finds the mates
  of the active assembly (including top-level mates to nested parts), but walking a
  sub-assembly's mate group through the component (`IComponent2.FirstFeature`) returned none,
  even when resolved; opening the sub-assembly works. Inside the sub-assembly's own tree the
  instance names can also differ from the names the top shows (seen after Pack and Go:
  `plug_copy-5` vs `plug-1`), so the tool compares component ID chains (`GetID`).
  `fixed` is likewise only reliable for components of the active assembly.
- **Configurations**: `set_component_state` acts on the active configuration only, and a component
  inserted in one configuration appears in all of them unless `suppress_new_components` is set.
  There is no per-configuration dimension or feature suppression tool yet.
- **Slow on large assemblies**: `component_status` with mates on a fully resolved sub-assembly of
  a ~1000-component assembly took over 10 minutes; the MCP adapter's timeout is shorter, so run
  such calls over HTTP (see the skill) or narrow them with `components`.
- **Timeouts do not cancel.** After an adapter timeout the execution server keeps working on the
  call (one STA thread, calls queue behind it); the error says so, but the caller has to check the
  model state before retrying a model-changing call.
- **Instance names are not stable.** The same component was `Sub-1/plug-2` while its
  sub-assembly was lightweight and `Sub-1/plug_copy-2` once resolved (a Pack and Go copy with
  renamed files).
  Names taken from an earlier listing can stop resolving after a state change; the not-found
  error lists the closest paths, and tools that resolve components re-find them by ID chain.
- **Resolving one instance resolves its file.** Resolving a lightweight component also resolves
  the other instances of the same file (SolidWorks loads the model once).
- **What's Wrong gives code 1 ("unknown") for a rejected mate**, and so does
  `IFeature.GetErrorCode2`; the AddMate5 error status carries the reason (5 = over-defined).
- **`open_document` on a STEP** still leaves the import document active (part queries fail
  with `WRONG_DOCUMENT_TYPE`): use `import_step`.
- **Inconsistent parameter names.** `edit_feature` takes `feature_name`, other tools `name` or
  `component`; a wrong name fails with MISSING_PARAMETER only.
- **Interference check limits.** Neighbours are found by bounding box, so a long diagonal
  bundle has a large box and many neighbours (slower, not wrong). The clearance distance uses
  SolidWorks Measure, one pair at a time (slow with many neighbours). A whole-assembly check on
  ~1000 components takes minutes: name the components.

## Next improvements

Most useful first:

1. **Find faces by geometry.** Let `add_mate` and the face readers select a face by normal, a
   point on it and a tolerance, instead of an index. That removes the slow full face dumps and
   the part / component index mismatch.
2. **Insert a plug at a mating connector.** One tool that takes a plug model and a mating
   connector, finds both mating faces, inserts the plug flush on the header (or computes its
   pose) and returns its wire-exit point. Done by hand four times so far: read both models'
   faces, screenshot both to tell the mating end and the latch side, build the plug-to-receptacle
   rotation, chain it with the receptacle's world transform and the harness instance's inverse
   (row-vector transforms), insert fixed, verify the world pose. The pose needs no mate (a rigid
   harness located elsewhere cannot take one).
2b. **Interference per bundle segment.** `check_interference` works per component; for a long
   bundle, report where along the path a clearance violation is (closest points from Measure),
   so the route can be moved at the right corner.
3. **Route by named points.** Feed `create_3d_path` named points (a connection-point table), or
   anchor routes to sketch points or reference points on the connector components, so that
   moving a part updates its routes without redrawing.
4. **Tests.** Offline tests for the fillet geometry in `create_3d_path` (the tangent points and
   the expected length are pure math) and for the response shapes of the new tools, in the
   existing contract test suite.
5. **Lengths per instance.** `measure_curves` could report the sum along a chain of bundles
   (connector to connector), so wire lengths come out directly.
5b. **Target length / service loop in `create_3d_path`.** A `target_length` option that inserts
   a U loop of the depth needed (2d - 4r(2 - pi/2) added for four 90° bends of radius r) on a
   chosen leg, so equal-length variants need no hand calculation.
5d. **Change report.** Compare a local copy against the released files (read-only) and list per
   document the added / removed / suppressed components, changed transforms, mates and
   configurations: the input for handing changes back to the CAD owner (building on
   `compare_assemblies` / `compare_parts`).
6. **Fast `check_rebuild` mode.** Report counts only and stop at the first error, for large
   assemblies.
7. **Delete components and mates by name in one call**, and mark a mate as suppressed instead of
   deleting it. Align parameter names across tools (`feature_name` / `name` / `component`).
8. **Sub-assembly mates from the top.** Make `component_status` read mates held inside
   sub-assemblies without opening them (e.g. open the sub-assembly document invisibly, map its
   components to the top by ID chain), and speed up the mate scan (cache the mate list per
   assembly per call).
9. **Configuration-aware status.** Report a component's suppression in every configuration of
   its parent at once, and set it for several configurations in one call.
10. **Upstream.** The tools are generic (no project, company or user names in the code). Consider proposing them to
   `eyfel/mcp-server-solidworks` (AGPL-3.0; upstream also asks for a CLA).
