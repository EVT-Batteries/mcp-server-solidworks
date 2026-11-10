---
name: solidpilot-mcp-development
description: >-
  How to change, build, test and debug the SolidPilot SolidWorks MCP server itself: the Python
  adapter (adapters/claude/server.py), the C# execution server (execution/solidworks, SolidWorks
  COM API) and the tool contract (tool-schemas.json). Use it whenever you add or modify a
  SolidPilot tool, rebuild the execution server, call a tool that the running MCP does not expose
  yet, investigate a tool that fails or returns wrong geometry, look up a SolidWorks API signature,
  or commit changes to the fork - even if the user only says "add a tool to the SolidWorks MCP",
  "the sweep failed" or "the MCP doesn't see the new tool".
---

# SolidPilot MCP Development

## Architecture

- **Adapter** `adapters/claude/server.py`: the MCP server. Each `@mcp.tool` forwards to
  `_call(tool, params)` → HTTP POST `http://localhost:5000/api/tool/execute`. It does not
  hot-reload: a new or changed tool appears only after the MCP server is restarted.
- **Execution server** `execution/solidworks/SolidworksExecution` (C#, .NET Framework 4.8, OWIN):
  `Controllers/ToolController.cs` dispatches on the tool name to `SolidWorksService` methods, a
  partial class split by family under `Services/`. All COM work runs on one STA thread. The adapter
  starts `bin/Debug/SolidworksExecution.exe` on demand (`ensure_ready`, or the next tool call).
- **Contract** `execution/solidworks/contracts/tool-schemas.json`: one entry per tool;
  `adapters/claude/tests/test_schema_contract.py` fails on any drift between it and `server.py`.
- **state_version**: every request carries it and must match the server's (`GET /api/tool/state`).
  Read-only tools return the current version; tools that change the model return version + 1 and
  call `_guard.RegisterCompleted(...)`. The version resets to 0 when the server restarts.

## Adding or changing a tool

1. **Look up every SolidWorks API you use in the installed interop assembly** before writing code
   (names and overloads that look plausible often do not exist):
   ```powershell
   $sw = [Reflection.Assembly]::LoadFrom("C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll")
   $sw.GetType("SolidWorks.Interop.sldworks.ISketchManager").GetMembers() | Where-Object Name -match "3D|Spline" | ForEach-Object { "$_" }
   $c = [Reflection.Assembly]::LoadFrom("C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.swconst.dll")
   [int][Enum]::Parse($c.GetType("SolidWorks.Interop.swconst.swFeatureNameID_e"), "swFmSweep")
   ```
   Use swconst enum values as inlined `int` constants (`(int)swX_e.swY` casts are fine; never a
   swconst type at runtime: that DLL is not shipped in `bin/Debug`).
2. **C#**: put a new tool in its own partial file (`Services/SolidWorksService.<Name>.cs`) to keep
   merges with upstream simple; add `<Compile Include>` (and any new `<Reference>`, e.g.
   `System.Drawing`) to the `.csproj`; add the `case` in `ToolController.Dispatch`. Follow the
   existing method shape: duplicate check, state-version check, `EnsureConnected`, try / catch
   `COMException` → `BuildFailed(code, message)`.
3. **Contract**: add the tool to `tool-schemas.json` (description, params, output shape).
4. **Adapter**: add the `@mcp.tool` in `server.py`. The docstring is all the model sees: units
   (meters), what changes, what the result holds. Variable-length arrays are JSON strings
   (`points: str = "[]"`). Run the contract test:
   `uv run --no-project --python 3.13 --with-requirements adapters/claude/requirements.txt --with pytest python -m pytest -q adapters/claude/tests`
   (contract, HTTP client and error-passing tests).
5. **README**: tool count and the tool's line in its section.

## Building

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$sln = "<fork>\execution\solidworks\SolidworksExecution.sln"
& $msb $sln /t:Restore /p:RestorePackagesConfig=true          # once per clone (packages/ is ignored)
& $msb $sln /t:Build /p:Configuration=Debug /p:OutDir=<scratch dir>\   # compile check, server keeps running
Get-Process SolidworksExecution -ErrorAction SilentlyContinue | Stop-Process   # it locks its exe
& $msb $sln /t:Build /p:Configuration=Debug                     # into bin/Debug
```
MSBuild ships with Visual Studio Build Tools; building needs the .NET Framework 4.8 targeting
pack. The commands run from any directory. New or changed tools appear only after an MCP restart
(the adapter does not hot-reload).
After stopping it the server is down: call `ensure_ready` (or any MCP tool) before direct HTTP
calls. `bin/Debug` is tracked in git despite `.gitignore`: commit the rebuilt `.exe` / `.pdb` with
the source change (a rebuild may also refresh `SolidWorks.Interop.sldworks.dll` from the local
SolidWorks install).

## Testing before the MCP is restarted

Call the execution server directly. Fetch the version first, and do not name the helper `sp`
(a built-in PowerShell alias) or print with `Write-Output` inside it (that ends up in the return
value):
```powershell
function SwCall([string]$tool, [hashtable]$params = @{}) {
  $sv = (Invoke-RestMethod http://localhost:5000/api/tool/state).stateVersion
  $body = @{ operationId = [guid]::NewGuid().ToString(); tool = $tool; stateVersion = $sv; params = $params } | ConvertTo-Json -Depth 10
  $r = Invoke-RestMethod -Uri http://localhost:5000/api/tool/execute -Method Post -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($body))
  if ($r.status -ne "COMPLETED") { Write-Host "!! $tool $($r.status): $($r.error.code) $($r.error.message)" }
  $r
}
```
Results are in `$r.cadState.features` (often one JSON string).

## Testing safely

- Test new geometry tools on a **new unsaved part** (`open_new_part`, then
  `close_document(save=False)`), or on local copies. Never save over shared or released CAD;
  check the active document's title before `save_document`.
- **Verify numerically, not just "COMPLETED"**: read back what SolidWorks built. Examples: a swept
  tube's volume must equal π·r²·L for its measured path length; a polyline route's solved length
  must equal the length computed from its points (`create_3d_path` reports both); compare
  `measure_curves` / `analyze_model` before and after an edit.
- `screenshot(view=..., caption=...)` and read the PNG to inspect the result.
- After any structural change in an assembly (deleted features, moved, replaced or removed
  components) run `check_rebuild` on the top assembly: it reports every rebuild error and warning
  in every loaded sub-assembly and part, and for a broken mate the components it references.
  Fix, re-check, and only then save. Mates of a deleted component are deleted with it; mates whose
  geometry changed stay behind as errors (code 48 "broken") and must be deleted or re-made.

## SolidWorks API lessons

- **Selecting features**: `Extension.SelectByID2(name, "3DSKETCH", ...)` failed on 3D sketches
  that exist; select the `IFeature` object itself with `feature.Select2(append, mark)` (mark 4 =
  sweep path).
- **Exact sketch geometry**: wrap creation in `SketchManager.AddToDB = true` (no inference
  snapping, no automatic relations), then `ISketch.MergePoints(1e-7)` to join shared endpoints.
- **Redrawing an existing sketch**: delete its segments **and its sketch points**
  (`GetSketchPoints2`); dimensioned or related points left behind pull the new geometry onto them
  when the sketch solves, with no error.
- **3D sketch**: `Insert3DSketch(true)` enters and exits; `EditSketch()` on the selected feature
  re-enters; `CreateLine(x1,y1,z1,x2,y2,z2)`, `Create3PointArc(start, end, point on arc)`,
  `CreateSpline2(flat xyz[], true)` take model coordinates in meters.
- **Circular-profile sweep**: `FeatureManager.CreateDefinition(17 /* swFmSweep */)` as
  `ISweepFeatureData`, `CircularProfile = true`, `CircularProfileDiameter = d`, path feature
  selected with mark 4, `CreateFeature(def)`. To edit: `GetDefinition`, `AccessSelections`,
  change, `ModifyDefinition`.
- **Reading sweeps**: `ISweepFeatureData.AccessSelections` → `Path` / `Profile` (always
  `ReleaseSelectionAccess`); segment lengths `ISketchSegment.GetLength()`, edge lengths
  `ICurve.GetLength3(UMin, UMax)` from `IEdge.GetCurveParams3()`.
- **2D sketch point coordinates** are sketch-local: map them with
  `ISketch.ModelToSketchTransform.Inverse()`, then a component's `Transform2` for assembly
  coordinates. `analyze_model(sketch)` reports 2D coordinates only; use
  `measure_curves(include_geometry=True)` for 3D.
- **Deleting a body's base feature** (e.g. a sweep) deletes every feature merged into that body.
- **Inserting components**: `x/y/z` place the part's bounding-box centre; pass an identity
  `transform_json` to put the part origin on the assembly origin. A `.sldasm` is preloaded as an
  assembly (`OpenDoc6` with `swDocASSEMBLY`), so sub-assemblies insert too. Mates referencing
  deleted geometry show as errors in "What's Wrong": delete them and fix the component
  (`place_component`).
- **Nested components**: `add_mate`, `analyze_assembly(faces|edges)` and `place_component` accept
  a component inside a sub-assembly by its instance path from the active assembly
  (`Sub-1/Part-1`, as `components_flat` and `check_rebuild` print it), so a top-level mate can tie
  parts of two different sub-assemblies.
- **Face indices differ between a multi-body part and its component**: `analyze_model(faces)` on
  the part and `analyze_assembly(faces, component)` enumerate bodies in different orders when a
  part has several bodies (an imported board with its connector models), so an index read on the
  part document selects another face in the assembly. Always take mate indices from
  `analyze_assembly(faces, component=...)` and identify the face there by normal, area and point
  (component-local coordinates). Single-body parts number their faces the same in both.
- **STEP imports**: a multi-body STEP opens as an assembly; saving it to a `.sldprt` path makes a
  part, but the open document stays the import. `import_step` saves, closes the import
  (`CloseDoc`) and opens the saved part (`OpenDoc6`). Face queries on large imports (thousands of
  faces) take minutes: run them once and reuse the indices.
- **Add-mate errors**: `AddMate5` returns a mate even when it reports an error through its
  out-parameter (swAddMateError_e: 1 no error, 5 over-defined assembly, ...), and an accepted mate
  can still turn other mates into errors. `add_mate` diffs `GetWhatsWrong` before and after and
  deletes the new mate (select its feature, `DeleteSelection2`) on any problem. A second set of
  mates on a rigid sub-assembly already located by mates over-defines it even when the geometry
  agrees exactly. What's Wrong and `GetErrorCode2` both give code 1 for such a mate.
- **Lightweight components**: `IComponent2.IsSuppressed()` returned true for a lightweight part
  (one of three in a test assembly): read suppression with `GetSuppression2` only. A lightweight
  component has no bodies (`GetBodies3` empty, `GetModelDoc2` often null); `SetSuppression2(3)`
  resolves it and the other instances of its file, and can rename instances, so re-find
  components by `GetID` chain afterwards (`ResolveForGeometry`). Open an assembly lightweight or
  resolved with `GetOpenDocSpec`, `LightWeight`, `UseLightWeightDefault = false`, `OpenDoc7`;
  `GetLightWeightComponentCount` / `ResolveAllLightWeightComponents(false)` for the whole tree.
- **Interference**: `IAssemblyDoc.InterferenceDetectionManager` checks the SELECTED components
  (`IComponent2.Select4`) or the whole assembly; set its options, read `GetInterferences()`
  (`IInterference.Components`, `Volume`, `GetInterferenceBody`), then call `Done()`. Minimum
  distance: select two components, `Extension.CreateMeasure()`, `ArcOption = 1`,
  `Calculate(null)`, `Distance`; it fails on intersecting bodies.
- **Adapter errors**: the MCP SDK (v2) shows the model only the text of a `ToolError`; any other
  exception becomes "Error executing tool <name>". `server.py` wraps `mcp.tool` so every tool
  raises `ToolError`, and `ExecutionLayerError` / `CadOperationError` are `ToolError`s. New tools
  on `@mcp.tool(...)` get the wrapper on their own; add a long-running tool to `LONG_TOOLS`.
- **Moving a sub-assembly up the tree**: read its world transform (`components_flat` of a part it
  contains at identity), delete it from the old parent (its mates go with it), insert it into the
  new parent with that `transform_json`, then mate it; `check_rebuild` every assembly touched.
- **Component state**: `IComponent2.GetSuppression2` / `SetSuppression2` (swComponentSuppressionState_e:
  0 suppressed, 1 lightweight, 2 fully resolved, 3 resolved; SetSuppression2 returns
  swSuppressionError_e, 2 = ok), `Visible` (0 / 1), `ReferencedConfiguration`; all act on the
  active configuration of the assembly that holds the component. Resolving one nested component
  resolves its sub-assembly, not its siblings.
- **Mates of a component**: `IComponent2.GetMates()` returned nothing for nested and lightweight
  components; walk the `MateGroup` sub-features (mate folders included) of the active assembly
  and test each `IMate2.MateEntity(k).ReferenceComponent`. Compare components by their chain of
  `GetID()` values, not by name: a sub-assembly's own tree can name the same instance differently
  from the top (seen after Pack and Go).
- **Configurations**: `AddConfiguration3(name, comment, alt, options)` copies the active one
  (option 128 = don't activate); `ShowConfiguration2` returns false for the configuration that is
  already active; a component inserted in one configuration appears in all of them unless the
  others have `SuppressNewComponentModels`.
- **File paths**: `AddComponent5` returned null for a forward-slash path (`C:/dir/part.sldprt`)
  of a part that was not loaded; normalize with `Path.GetFullPath`.
- **Screenshots**: `IModelDoc2.SaveBMP(path, w, h)` captures the document window (it must be
  visible: activate it first); it never writes the document.

## Committing

Commit to the fork only when the user asks. Include the source, contract, adapter, README and the
rebuilt `bin/Debug` files; end the message with the session's attribution lines.
