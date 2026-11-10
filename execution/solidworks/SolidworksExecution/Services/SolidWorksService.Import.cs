using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidworksExecution.Models;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Services
{
    // import_step: a neutral file (STEP / IGES / Parasolid ...) -> a native part saved on disk and
    // left open as the active document. open_document alone leaves the import document active
    // (an assembly-like tree for a multi-body file), on which part queries fail.
    public partial class SolidWorksService
    {
        public ExecutionResponse ImportStep(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string filePath = p?.Value<string>("file_path");
                string saveAs = p?.Value<string>("save_as") ?? "";
                bool overwrite = p?.Value<bool?>("overwrite") ?? false;

                if (string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path is required.");
                filePath = Path.GetFullPath(filePath);
                if (!File.Exists(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", "No file at '" + filePath + "'.");
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".sldprt" || ext == ".sldasm" || ext == ".slddrw")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "import_step is for neutral files (.step / .stp / .iges / .igs / .x_t / .x_b / .sat); open native files with open_document.");

                string target = string.IsNullOrEmpty(saveAs)
                    ? Path.Combine(Path.GetDirectoryName(filePath), Path.GetFileNameWithoutExtension(filePath) + ".SLDPRT")
                    : Path.GetFullPath(saveAs);
                if (!target.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "save_as must be a .SLDPRT path.");
                if (File.Exists(target) && !overwrite)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_EXISTS", "'" + target + "' already exists. Open it with open_document, or pass overwrite=true to import again.");
                string targetTitle = Path.GetFileName(target);
                if (_solidWorks.GetOpenDocumentByName(target) != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_OPEN", "'" + targetTitle + "' is open in SolidWorks. Close it before importing over it.");

                // 1. Import, through the same staged path as open_document.
                var open = OpenDocument(new ToolRequest
                {
                    OperationId = Guid.NewGuid().ToString(),
                    StateVersion = _guard.GetCurrentStateVersion(),
                    Params = new JObject { ["file_path"] = filePath },
                });
                if (open.Status != "COMPLETED")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        open.Error?.Code ?? "OPEN_FAILED", open.Error?.Message ?? "The file could not be imported.");

                var import = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (import == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "OPEN_FAILED", "The import left no active document.");
                string importTitle = import.GetTitle();
                string importedAs = import is IAssemblyDoc ? "ASSEMBLY" : "PART";

                // 2. Save it as a part (an imported assembly becomes one multi-body part).
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                int saveErrors = 0, saveWarnings = 0;
                bool saved = import.Extension.SaveAs3(target, 0 /* swSaveAsCurrentVersion */, 1 /* swSaveAsOptions_Silent */,
                    null, null, ref saveErrors, ref saveWarnings);
                if (!saved || !File.Exists(target))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SAVE_FAILED", "Imported '" + importTitle + "' but could not save it as '" + target
                        + "' (errors=" + saveErrors + ", warnings=" + saveWarnings + "). The import document is still open.");

                // 3. Close the import and open the saved part: the import document does not become the part.
                try { _solidWorks.CloseDoc(importTitle); } catch { }
                try { _solidWorks.CloseDoc(targetTitle); } catch { }
                int errors = 0, warnings = 0;
                var part = _solidWorks.OpenDoc6(target, 1 /* swDocPART */, 1 /* silent */, "", ref errors, ref warnings) as IModelDoc2;
                if (part == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "OPEN_FAILED", "Saved '" + target + "' but could not reopen it (errors=" + errors + ").");
                int actErr = 0;
                _solidWorks.ActivateDoc3(part.GetTitle(), false, 1 /* swDontRebuildActiveDoc */, ref actErr);

                var result = new JObject
                {
                    ["source"] = filePath,
                    ["part"] = target,
                    ["imported_as"] = importedAs,
                };
                var partDoc = part as IPartDoc;
                if (partDoc != null)
                {
                    int solids = (partDoc.GetBodies2(0 /* swSolidBody */, true) as object[])?.Length ?? 0;
                    int sheets = (partDoc.GetBodies2(1 /* swSheetBody */, true) as object[])?.Length ?? 0;
                    result["solid_bodies"] = solids;
                    if (sheets > 0) result["surface_bodies"] = sheets;
                    if (solids == 0)
                        result["warning"] = sheets > 0
                            ? "The import holds surface bodies only (no solid): faces did not knit into a closed volume."
                            : "The import holds no body.";
                    if (partDoc.GetPartBox(true) is double[] bb && bb.Length >= 6)
                    {
                        result["box_min"] = Point(bb[0], bb[1], bb[2]);
                        result["box_max"] = Point(bb[3], bb[4], bb[5]);
                        result["size"] = Point(bb[3] - bb[0], bb[4] - bb[1], bb[5] - bb[2]);
                    }
                }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = part.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }
    }
}
