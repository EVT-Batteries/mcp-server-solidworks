using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: check_rebuild — SolidWorks' "What's Wrong" for the active document and,
    // in an assembly, for every sub-assembly and part document it loads: each feature or mate with a
    // rebuild error or warning, its swFeatureError_e code and, for mates, the components it references.
    // Optionally rebuilds first. Never saves; never bumps state_version.
    public partial class SolidWorksService
    {
        // swFeatureError_e, inlined (values read from the interop assembly).
        private static readonly Dictionary<int, string> FeatureErrors = new Dictionary<int, string>
        {
            { 0, "none" }, { 1, "unknown" },
            { 10, "fillet: no loop" }, { 11, "fillet: no face" }, { 12, "fillet: invalid radius" },
            { 13, "fillet: no edge" }, { 14, "fillet: model geometry" }, { 15, "fillet: radius too small" },
            { 16, "fillet: cannot extend" }, { 17, "fillet: radius eliminates element" },
            { 18, "fillet: radius too big" }, { 19, "fillet: radius too big" },
            { 30, "extrusion: disjoint" }, { 31, "extrusion: no end found" },
            { 32, "extrusion: bad geometric conditions" }, { 33, "cut: contour open and closed" },
            { 34, "cut: contour invalid" }, { 35, "cut: open contour invalid" },
            { 36, "boss: contour open and closed" }, { 37, "boss: contour invalid" },
            { 38, "mate: invalid edge" }, { 39, "mate: invalid face" }, { 40, "mate: failed creating surface" },
            { 41, "mate: invalid entity (missing, suppressed or no longer present)" },
            { 42, "mate: unknown tangent" }, { 43, "mate: dangling geometry" },
            { 44, "mate: entity not linear" }, { 45, "mate: entity failed" },
            { 46, "mate: over-defined" }, { 47, "mate: ill-defined (cannot be solved)" }, { 48, "mate: broken" },
            { 49, "feature deprecated" }, { 50, "feature obsolete" }, { 51, "sketch: external reference failed" },
            { 69, "swept flange: invalid profile or path" }, { 70, "swept flange: self-intersecting" },
            { 71, "cut does not intersect the model" }, { 72, "sketch contains a self-intersecting contour" },
            { 73, "missing items in feature" },
        };

        public ExecutionResponse CheckRebuild(ToolRequest request)
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
                bool rebuild = p?.Value<bool?>("rebuild") ?? true;
                bool recursive = p?.Value<bool?>("recursive") ?? true;
                bool includeWarnings = p?.Value<bool?>("include_warnings") ?? true;

                var top = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (top == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (rebuild) top.ForceRebuild3(false);

                // The documents to inspect: the active one, then every distinct loaded component document.
                var docs = new List<KeyValuePair<IModelDoc2, string>> { new KeyValuePair<IModelDoc2, string>(top, "") };
                var notLoaded = new JArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SafePath(top) };
                if (recursive && top is IAssemblyDoc asm && asm.GetComponents(false) is object[] comps)
                {
                    foreach (var o in comps)
                    {
                        if (!(o is IComponent2 c)) continue;
                        bool suppressed = false;
                        try { suppressed = c.IsSuppressed(); } catch { }
                        if (suppressed) continue;
                        var d = c.GetModelDoc2() as IModelDoc2;
                        if (d == null)
                        {
                            notLoaded.Add(new JObject { ["component"] = ComponentPath(c), ["path"] = SafeComponentPath(c) });
                            continue;
                        }
                        if (seen.Add(SafePath(d))) docs.Add(new KeyValuePair<IModelDoc2, string>(d, ComponentPath(c)));
                    }
                }

                var report = new JArray();
                int errors = 0, warnings = 0;
                foreach (var kv in docs)
                {
                    var items = WhatsWrong(kv.Key, includeWarnings, ref errors, ref warnings);
                    if (items.Count == 0) continue;
                    var dj = new JObject
                    {
                        ["document"] = kv.Key.GetTitle(),
                        ["path"] = SafePath(kv.Key),
                        ["items"] = items,
                    };
                    if (!string.IsNullOrEmpty(kv.Value)) dj["first_instance"] = kv.Value;
                    report.Add(dj);
                }

                var result = new JObject
                {
                    ["document"] = top.GetTitle(),
                    ["documents_checked"] = docs.Count,
                    ["errors"] = errors,
                    ["warnings"] = warnings,
                    ["clean"] = errors == 0 && (warnings == 0 || !includeWarnings) && notLoaded.Count == 0,
                    ["problems"] = report,
                };
                if (notLoaded.Count > 0) result["components_not_loaded"] = notLoaded;

                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = top.GetTitle(),
                        DocumentType = top is IAssemblyDoc ? "ASSEMBLY" : "PART",
                        ActiveSketch = null,
                        Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
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

        // One document's What's Wrong list.
        private static JArray WhatsWrong(IModelDoc2 doc, bool includeWarnings, ref int errors, ref int warnings)
        {
            var items = new JArray();
            object feats = null, codes = null, warns = null;
            bool any = false;
            try { any = doc.Extension.GetWhatsWrong(out feats, out codes, out warns); } catch { }
            if (!any || !(feats is object[] fa)) return items;
            var ca = codes as int[];
            var wa = warns as bool[];
            for (int i = 0; i < fa.Length; i++)
            {
                var f = fa[i] as IFeature;
                int code = ca != null && i < ca.Length ? ca[i] : -1;
                bool isWarning = wa != null && i < wa.Length && wa[i];
                if (isWarning && !includeWarnings) continue;
                if (isWarning) warnings++; else errors++;

                var ij = new JObject
                {
                    ["severity"] = isWarning ? "warning" : "error",
                    ["feature"] = f?.Name,
                    ["type"] = f?.GetTypeName2(),
                    ["code"] = code,
                    ["meaning"] = FeatureErrors.TryGetValue(code, out var m) ? m : "code " + code,
                };
                // A mate: which components it references (null = the reference is gone).
                try
                {
                    if (f?.GetSpecificFeature2() is IMate2 mate)
                    {
                        var ents = new JArray();
                        int n = mate.GetMateEntityCount();
                        for (int k = 0; k < n; k++)
                        {
                            IComponent2 rc = null;
                            try { rc = mate.MateEntity(k)?.ReferenceComponent; } catch { }
                            ents.Add(rc != null ? (JToken)ComponentPath(rc) : JValue.CreateNull());
                        }
                        ij["mate_components"] = ents;
                    }
                }
                catch { }
                items.Add(ij);
            }
            return items;
        }

        private static string SafePath(IModelDoc2 d)
        {
            try { var s = d.GetPathName(); return string.IsNullOrEmpty(s) ? d.GetTitle() : s; } catch { return "?"; }
        }

        private static string SafeComponentPath(IComponent2 c)
        {
            try { return c.GetPathName(); } catch { return ""; }
        }

        // Instance path from the top assembly, e.g. "Sub-1/Part-2".
        private static string ComponentPath(IComponent2 c)
        {
            var parts = new List<string>();
            int guard = 0;
            while (c != null && guard++ < 50)
            {
                string n = c.Name2 ?? "?";
                int slash = n.LastIndexOf('/');
                parts.Insert(0, slash >= 0 ? n.Substring(slash + 1) : n);
                c = c.GetParent() as IComponent2;
            }
            return string.Join("/", parts);
        }
    }
}
