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
   `uv run --no-project --python 3.13 --with-requirements adapters/claude/requirements.txt python adapters/claude/tests/test_schema_contract.py`.
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
  `transform_json` to put the part origin on the assembly origin. Mates referencing deleted
  geometry show as errors in "What's Wrong": delete them and fix the component
  (`place_component`).
- **Screenshots**: `IModelDoc2.SaveBMP(path, w, h)` captures the document window (it must be
  visible: activate it first); it never writes the document.

## Committing

Commit to the fork only when the user asks. Include the source, contract, adapter, README and the
rebuilt `bin/Debug` files; end the message with the session's attribution lines.
