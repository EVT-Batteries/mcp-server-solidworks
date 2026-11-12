using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: replace_component — swap the file a component uses for another one
    // (IAssemblyDoc.ReplaceComponents2), e.g. a surface-only supplier model for a solid one, without
    // deleting and re-inserting (which would lose the instances' transforms). The component must belong
    // to the ACTIVE assembly (open a sub-assembly to replace its own components).
    public partial class SolidWorksService
    {
        public ExecutionResponse ReplaceComponent(ToolRequest request)
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
                string name = p?.Value<string>("component") ?? "";
                string file = p?.Value<string>("file_path") ?? "";
                string config = p?.Value<string>("configuration") ?? "";
                bool all = p?.Value<bool?>("all_instances") ?? true;
                bool reattach = p?.Value<bool?>("reattach_mates") ?? true;

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = doc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "replace_component requires an active assembly.");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "component and file_path are required.");
                if (!System.IO.File.Exists(file))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", "No file at " + file);

                // Only components of the active assembly itself (top level): ReplaceComponents2 acts there.
                IComponent2 comp = null;
                if (asm.GetComponents(true) is object[] top)
                    foreach (var o in top)
                        if (o is IComponent2 c && string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase)) { comp = c; break; }
                if (comp == null)
                {
                    var any = FindComponentByName(asm, name);
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COMPONENT_NOT_FOUND", any != null
                            ? $"'{ComponentPath(any)}' is in a sub-assembly: open that sub-assembly (open_document) and replace it there."
                            : ComponentNotFoundMessage(asm, name));
                }
                string oldFile = comp.GetPathName();

                // Instances of the old file at this level, with their transforms, to check afterwards.
                var before = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                    if (o is IComponent2 c && string.Equals(c.GetPathName(), oldFile, StringComparison.OrdinalIgnoreCase))
                        before[c.Name2] = (c.Transform2?.ArrayData as double[]) ?? new double[0];

                doc.ClearSelection2(true);
                bool sel = comp.Select4(false, null, false);
                bool ok = sel && asm.ReplaceComponents2(file, config, all, 0 /* swReplaceComponentsConfiguration_MatchName */, reattach);
                doc.ClearSelection2(true);
                if (!ok)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "REPLACE_FAILED", $"ReplaceComponents2 failed (selected={sel}) for '{name}' -> {file}.");
                try { doc.ForceRebuild3(false); } catch { }

                // Read back: the replaced instances now use the new file, at the same transforms.
                var replaced = new JArray();
                int moved = 0;
                foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                {
                    if (!(o is IComponent2 c) || !string.Equals(c.GetPathName(), file, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = (c.Transform2?.ArrayData as double[]) ?? new double[0];
                    double dmax = -1;
                    // SOLIDWORKS renames a replaced instance after the new file and may renumber it:
                    // match by name, else by identical rotation + nearest translation.
                    double[] tb = null;
                    if (!before.TryGetValue(c.Name2, out tb) && t.Length >= 12)
                    {
                        double best = double.MaxValue;
                        foreach (var kv in before)
                        {
                            if (kv.Value.Length < 12) continue;
                            double d = 0;
                            for (int i = 0; i < 12; i++) d = Math.Max(d, Math.Abs(t[i] - kv.Value[i]));
                            if (d < best) { best = d; tb = kv.Value; }
                        }
                    }
                    if (tb != null && tb.Length >= 12 && t.Length >= 12)
                    {
                        dmax = 0;
                        for (int i = 0; i < 12; i++) dmax = Math.Max(dmax, Math.Abs(t[i] - tb[i]));
                        if (dmax > 1e-6) moved++;
                    }
                    replaced.Add(new JObject
                    {
                        ["component"] = c.Name2,
                        ["transform_change"] = dmax < 0 ? null : (JToken)Math.Round(dmax, 9)
                    });
                }
                var still = before.Keys.Where(k => FindComponentByName(asm, k) is IComponent2 c
                    && string.Equals(c.GetPathName(), oldFile, StringComparison.OrdinalIgnoreCase)).ToList();

                var result = new JObject
                {
                    ["assembly"] = doc.GetTitle(),
                    ["old_file"] = oldFile,
                    ["new_file"] = file,
                    ["replaced_count"] = replaced.Count,
                    ["replaced"] = replaced,
                    ["moved_count"] = moved,
                    ["still_old_file"] = new JArray(still),
                    ["note"] = "Not saved. Mates on the old geometry may now be broken: run check_rebuild, then save the assembly."
                };
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = doc.GetTitle(),
                        DocumentType = "ASSEMBLY",
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
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INTERNAL_ERROR", ex.Message);
            }
        }

        // ---- save helpers (save_document) -------------------------------------------------

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { return System.IO.Path.GetFullPath(p).TrimEnd('\\'); } catch { return p.TrimEnd('\\'); }
        }

        private static string NormalizeDir(string d) => NormalizePath(d) + "\\";

        /// <summary>Open documents SOLIDWORKS marks as modified (IModelDoc2.GetSaveFlag), virtual
        /// components (route tubes, stored inside their assembly) excluded.</summary>
        private List<IModelDoc2> DirtyOpenDocuments()
        {
            var list = new List<IModelDoc2>();
            if (_solidWorks.GetDocuments() is object[] docs)
                foreach (var o in docs)
                {
                    if (!(o is IModelDoc2 d)) continue;
                    bool dirty = false;
                    try { dirty = d.GetSaveFlag(); } catch { }
                    if (!dirty) continue;
                    string p = d.GetPathName() ?? "";
                    if (p.IndexOf("\\VC~~\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;  // virtual
                    list.Add(d);
                }
            return list;
        }

        /// <summary>Why a file must not be saved, or null. Read-only locations (e.g. released CAD
        /// in a synced document library) come from the environment variable
        /// SOLIDPILOT_READONLY_PATHS: ';'-separated path fragments, case-insensitive (set it in
        /// adapters/claude/.env or the MCP server's env; the execution server inherits it).
        /// With a root, only files under it.</summary>
        private static string SaveBlockReason(string path, string rootDir)
        {
            if (string.IsNullOrEmpty(path)) return "never saved (no path)";
            string full = NormalizePath(path);
            string ro = System.Environment.GetEnvironmentVariable("SOLIDPILOT_READONLY_PATHS") ?? "";
            foreach (var frag in ro.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string f = frag.Trim();
                if (f.Length > 0 && full.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"read-only location ('{f}' in SOLIDPILOT_READONLY_PATHS)";
            }
            if (rootDir != null && !full.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                return "outside reference_root " + rootDir;
            return null;
        }

        // insert_part: bring another part's bodies into the ACTIVE part (IPartDoc.InsertPart3), e.g.
        // the solid bodies of a supplier model into its surface-only version: the surfaces the
        // assembly mates reference stay, the solids make interference / face queries work.
        public ExecutionResponse InsertPart(ToolRequest request)
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
                string file = p?.Value<string>("file_path") ?? "";
                string config = p?.Value<string>("configuration") ?? "";
                bool solids = p?.Value<bool?>("solids") ?? true;
                bool surfaces = p?.Value<bool?>("surfaces") ?? false;
                bool breakLink = p?.Value<bool?>("break_link") ?? true;
                bool hideSurf = p?.Value<bool?>("hide_surface_bodies") ?? false;

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var part = doc as IPartDoc;
                if (part == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "insert_part requires an active part.");
                if (!System.IO.File.Exists(file))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", "No file at " + file);

                int CountBodies(int type) => (part.GetBodies2(type, false) as object[])?.Length ?? 0;
                int solidsBefore = CountBodies(0 /* swSolidBody */), sheetsBefore = CountBodies(1 /* swSheetBody */);

                int opts = (solids ? 1 : 0) | (surfaces ? 2 : 0) | (breakLink ? 512 : 0)
                         | 1048576 /* propagate visual properties */ | 262144 /* don't zoom */;
                var feat = part.InsertPart3(file, opts, config) as IFeature;
                if (feat == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INSERT_FAILED", "InsertPart3 returned no feature for " + file);
                try { doc.ForceRebuild3(false); } catch { }

                int hidden = 0;
                if (hideSurf && part.GetBodies2(1, true) is object[] sheets)
                    foreach (var o in sheets)
                        if (o is IBody2 b) { b.HideBody(true); hidden++; }

                var result = new JObject
                {
                    ["feature"] = feat.Name,
                    ["feature_type"] = feat.GetTypeName2(),
                    ["source"] = file,
                    ["link_broken"] = breakLink,
                    ["solid_bodies_before"] = solidsBefore,
                    ["solid_bodies_after"] = CountBodies(0),
                    ["surface_bodies_before"] = sheetsBefore,
                    ["surface_bodies_after"] = CountBodies(1),
                    ["surface_bodies_hidden"] = hidden,
                    ["note"] = "Not saved (save_document). Hidden bodies still carry the mates that reference them."
                };
                try
                {
                    if (part.GetPartBox(true) is double[] bx && bx.Length >= 6)
                        result["box_m"] = new JArray(bx.Take(6).Select(v => Math.Round(v, 6)));
                }
                catch { }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = doc.GetTitle(),
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
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INTERNAL_ERROR", ex.Message);
            }
        }
    }
}
