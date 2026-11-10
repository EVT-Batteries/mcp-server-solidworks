using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: component_status (read-only) and set_component_state (suppress /
    // resolve / lightweight / show / hide components of the active assembly, nested ones by instance
    // path). The status of a component: its suppression state, visibility, fixed flag, referenced
    // configuration, file (and whether it exists) and, on request, its mates with their rebuild errors,
    // so that suppressed parts can be brought back and their broken mates found in one pass.
    public partial class SolidWorksService
    {
        // swComponentSuppressionState_e, inlined.
        private static readonly Dictionary<int, string> SuppressionNames = new Dictionary<int, string>
        {
            { 0, "suppressed" }, { 1, "lightweight" }, { 2, "fully_resolved" }, { 3, "resolved" },
            { 4, "fully_lightweight" }, { 5, "internal_id_mismatch" },
        };

        // swMateType_e, inlined.
        private static readonly string[] MateTypeNames =
        {
            "coincident", "concentric", "perpendicular", "parallel", "tangent", "distance", "angle",
            "unknown", "symmetric", "cam_follower", "gear", "width", "lock_to_sketch", "rack_pinion",
            "max_mates", "path", "lock", "screw", "linear_coupler", "universal_joint", "coordinate",
            "slot", "hinge", "slider", "profile_center", "magnetic",
        };

        public ExecutionResponse ComponentStatus(ToolRequest request)
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
                var wanted = ParseNameList(p?["components"]);
                string match = p?.Value<string>("match") ?? "";
                string stateFilter = (p?.Value<string>("state") ?? "").Trim().ToLowerInvariant();
                bool recursive = p?.Value<bool?>("recursive") ?? true;
                bool? includeMatesParam = p?["include_mates"] == null || p["include_mates"].Type == JTokenType.Null
                    ? (bool?)null : p.Value<bool>("include_mates");
                int limit = p?.Value<int?>("limit") ?? 500;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "component_status requires an active assembly.");

                // Mates are read per component (slow on big assemblies): by default only when the
                // components are named or filtered.
                bool includeMates = includeMatesParam ??
                    (wanted.Count > 0 || !string.IsNullOrEmpty(match) || !string.IsNullOrEmpty(stateFilter));

                var list = new JArray();
                var counts = new JObject();
                var missing = new JArray();
                int total = 0;
                if (wanted.Count > 0)
                {
                    foreach (var n in wanted)
                    {
                        var c = FindComponentByName(asm, n);
                        if (c == null) { missing.Add(n); continue; }
                        total++;
                        list.Add(ComponentStatusJson(c, includeMates));
                    }
                }
                else if (asm.GetComponents(!recursive) is object[] comps)
                {
                    foreach (var o in comps)
                    {
                        if (!(o is IComponent2 c)) continue;
                        total++;
                        string state = SuppressionName(c);
                        counts[state] = (counts.Value<int?>(state) ?? 0) + 1;
                        string path = ComponentPath(c);
                        if (!string.IsNullOrEmpty(match)
                            && path.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0
                            && SafeComponentPath(c).IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!StateMatches(c, state, stateFilter)) continue;
                        if (list.Count >= limit) continue;
                        list.Add(ComponentStatusJson(c, includeMates));
                    }
                }

                var result = new JObject
                {
                    ["assembly"] = modelDoc.GetTitle(),
                    ["components_scanned"] = total,
                    ["returned"] = list.Count,
                    ["components"] = list,
                };
                if (counts.Count > 0) result["counts_by_state"] = counts;
                if (missing.Count > 0) result["not_found"] = missing;
                if (wanted.Count == 0 && list.Count >= limit) result["truncated_at"] = limit;

                return ReadOnlyResponse(request, modelDoc, result);
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

        public ExecutionResponse SetComponentState(ToolRequest request)
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
                var wanted = ParseNameList(p?["components"]);
                string suppression = (p?.Value<string>("suppression") ?? "").Trim().ToLowerInvariant();
                bool? visible = p?["visible"] == null || p["visible"].Type == JTokenType.Null
                    ? (bool?)null : p.Value<bool>("visible");
                string refConfig = p?.Value<string>("referenced_configuration") ?? "";

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "set_component_state requires an active assembly.");
                if (wanted.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "components (JSON list of names or instance paths) is required.");

                int target;
                switch (suppression)
                {
                    case "": target = -1; break;
                    case "suppressed": case "suppress": target = 0; break;
                    case "lightweight": target = 1; break;
                    case "resolved": case "resolve": case "unsuppress": case "unsuppressed": target = 3; break;
                    case "fully_resolved": target = 2; break;
                    default:
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "suppression must be 'suppressed', 'resolved', 'lightweight' or 'fully_resolved'.");
                }
                if (target < 0 && !visible.HasValue && string.IsNullOrEmpty(refConfig))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "Give suppression, visible and / or referenced_configuration.");

                // Resolve every name first, so a typo changes nothing.
                var comps = new List<IComponent2>();
                var missing = new JArray();
                foreach (var n in wanted)
                {
                    var c = FindComponentByName(asm, n);
                    if (c == null) missing.Add(n); else comps.Add(c);
                }
                if (missing.Count > 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COMPONENT_NOT_FOUND", "Not found (nothing changed): " + string.Join(", ", missing.ToObject<string[]>())
                        + ". Use component_status(match=...) to list instance paths.");

                var changes = new JArray();
                bool anyFailed = false;
                foreach (var c in comps)
                {
                    string path = ComponentPath(c);
                    var cj = new JObject { ["component"] = path, ["before"] = SuppressionName(c) };
                    if (target >= 0)
                    {
                        // A component inside a suppressed sub-assembly cannot change on its own.
                        var parent = c.GetParent() as IComponent2;
                        if (target != 0 && parent != null && SafeSuppression(parent) == 0)
                        {
                            cj["result"] = "failed";
                            cj["reason"] = "parent '" + ComponentPath(parent) + "' is suppressed: resolve it first";
                            anyFailed = true;
                            changes.Add(cj);
                            continue;
                        }
                        int rc = c.SetSuppression2(target);
                        // swSuppressionError_e: 0 bad component, 1 bad state, 2 ok, 3 failed.
                        cj["result"] = rc == 2 ? "ok" : rc == 0 ? "bad_component" : rc == 1 ? "bad_state" : "failed";
                        if (rc != 2) anyFailed = true;
                    }
                    if (!string.IsNullOrEmpty(refConfig))
                    {
                        // The component's file must have that configuration; it applies to the active
                        // configuration of the assembly holding the component.
                        try
                        {
                            c.ReferencedConfiguration = refConfig;
                            string now = c.ReferencedConfiguration;
                            cj["referenced_configuration"] = now;
                            if (!string.Equals(now, refConfig, StringComparison.OrdinalIgnoreCase))
                            { cj["configuration_error"] = "not applied (does the component's file have that configuration?)"; anyFailed = true; }
                        }
                        catch (Exception ex) { cj["configuration_error"] = ex.Message; anyFailed = true; }
                    }
                    if (visible.HasValue && SafeSuppression(c) != 0)
                    {
                        try { c.Visible = visible.Value ? 1 : 0; cj["visible_set"] = visible.Value; }
                        catch (Exception ex) { cj["visible_error"] = ex.Message; anyFailed = true; }
                    }
                    changes.Add(cj);
                }

                modelDoc.EditRebuild3();

                // State after the rebuild, mates included: a resolved component shows its broken mates.
                var after = new JArray();
                foreach (var c in comps) after.Add(ComponentStatusJson(c, true));

                var result = new JObject
                {
                    ["assembly"] = modelDoc.GetTitle(),
                    ["changes"] = changes,
                    ["components"] = after,
                    ["all_ok"] = !anyFailed,
                    ["configuration"] = (modelDoc.GetActiveConfiguration() as IConfiguration)?.Name,
                    ["note"] = "Not saved. Applies to the active configuration only. Changing a nested component edits its sub-assembly's active configuration. Run check_rebuild before saving.",
                };

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = !anyFailed,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
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
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        private ExecutionResponse ReadOnlyResponse(ToolRequest request, IModelDoc2 doc, JObject result)
        {
            return new ExecutionResponse
            {
                OperationId = request.OperationId,
                Status = "COMPLETED",
                Verified = true,
                StateVersion = _guard.GetCurrentStateVersion(),
                CadState = new CadState
                {
                    StateVersion = _guard.GetCurrentStateVersion(),
                    ActiveDocument = doc.GetTitle(),
                    DocumentType = doc is IAssemblyDoc ? "ASSEMBLY" : "PART",
                    ActiveSketch = null,
                    Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                    Dimensions = new List<string>()
                },
                Error = null
            };
        }

        // components: a JSON array, a JSON-array string, or one name.
        private static List<string> ParseNameList(JToken t)
        {
            var list = new List<string>();
            if (t == null || t.Type == JTokenType.Null) return list;
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.Length == 0 || s == "[]") return list;
                if (s.StartsWith("[")) t = JArray.Parse(s);
                else { list.Add(s); return list; }
            }
            if (t is JArray a)
                foreach (var x in a)
                {
                    string s = ((string)x)?.Trim();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            return list;
        }

        private static int SafeSuppression(IComponent2 c)
        {
            try { return c.GetSuppression2(); } catch { return -1; }
        }

        private static string SuppressionName(IComponent2 c)
        {
            int s = SafeSuppression(c);
            return SuppressionNames.TryGetValue(s, out var n) ? n : "unknown";
        }

        private static bool StateMatches(IComponent2 c, string state, string filter)
        {
            switch (filter)
            {
                case "": return true;
                case "suppressed": return state == "suppressed";
                case "lightweight": return state.Contains("lightweight");
                case "resolved": return state.Contains("resolved");
                case "hidden": return state != "suppressed" && SafeVisible(c) == 0;
                case "missing": return !FileExists(c);
                default: return state == filter;
            }
        }

        private static int SafeVisible(IComponent2 c)
        {
            try { return c.Visible; } catch { return -1; }
        }

        private static bool FileExists(IComponent2 c)
        {
            string f = SafeComponentPath(c);
            try { return string.IsNullOrEmpty(f) || File.Exists(f); } catch { return true; }
        }

        private JObject ComponentStatusJson(IComponent2 c, bool includeMates)
        {
            string state = SuppressionName(c);
            var j = new JObject
            {
                ["component"] = ComponentPath(c),
                ["suppression"] = state,
                ["file"] = SafeComponentPath(c),
            };
            if (!FileExists(c)) j["file_missing"] = true;
            try { j["configuration"] = c.ReferencedConfiguration; } catch { }
            if (state != "suppressed")
            {
                int v = SafeVisible(c);
                if (v == 0) j["hidden"] = true;
                try { if (c.IsFixed()) j["fixed"] = true; } catch { }
                try { if (c.IsEnvelope()) j["envelope"] = true; } catch { }
            }
            var parent = c.GetParent() as IComponent2;
            if (parent != null && SafeSuppression(parent) == 0) j["parent_suppressed"] = ComponentPath(parent);
            try { if (c.ExcludeFromBOM) j["excluded_from_bom"] = true; } catch { }
            if (state != "suppressed")
            {
                var tj = TransformJson(c);
                if (tj != null) j["transform"] = tj;
            }

            if (includeMates)
            {
                int broken = 0;
                var mates = MatesOfComponent(c, ref broken);
                j["mates"] = mates;
                if (broken > 0) j["broken_mates"] = broken;
            }
            return j;
        }

        // The mates that reference a component, read from the mate groups of the active assembly and of
        // every sub-assembly above the component (IComponent2.GetMates returns nothing for nested or
        // lightweight components). Each mate: the assembly holding it (instance path, "" = the active
        // one), its name, type, suppression, rebuild error and the full instance paths it references.
        private JArray MatesOfComponent(IComponent2 c, ref int broken)
        {
            var mates = new JArray();
            var top = _solidWorks.IActiveDoc2 as IModelDoc2;

            // Components are compared by their chain of component IDs (IComponent2.GetID), not by
            // name: inside a sub-assembly's own feature tree the instance names can differ from the
            // names the top assembly shows for the same components (seen after Pack and Go).
            var chain = new List<IComponent2>();          // root-level ancestor ... c
            for (var a = c; a != null; a = a.GetParent() as IComponent2) chain.Insert(0, a);
            var ids = new List<int>();
            foreach (var a in chain) ids.Add(SafeId(a));

            // Owners: the active assembly (level -1), then each ancestor sub-assembly (level k).
            var owners = new List<KeyValuePair<IFeature, int>> { new KeyValuePair<IFeature, int>(top?.FirstFeature() as IFeature, -1) };
            for (int k = 0; k < chain.Count - 1; k++)
            {
                IFeature first = null;
                try { first = chain[k].FirstFeature() as IFeature; } catch { }
                if (first == null) { try { first = (chain[k].GetModelDoc2() as IModelDoc2)?.FirstFeature() as IFeature; } catch { } }
                if (first != null) owners.Add(new KeyValuePair<IFeature, int>(first, k));
            }

            foreach (var kv in owners)
            {
                int level = kv.Value;
                var targetIds = ids.GetRange(level + 1, ids.Count - level - 1);
                IComponent2 owner = level >= 0 ? chain[level] : null;
                foreach (var f in MateFeatures(kv.Key))
                {
                    var mate = f.GetSpecificFeature2() as IMate2;
                    if (mate == null) continue;
                    var ents = new JArray();
                    bool hit = false;
                    try
                    {
                        int n = mate.GetMateEntityCount();
                        for (int k = 0; k < n; k++)
                        {
                            IComponent2 rc = null;
                            try { rc = mate.MateEntity(k)?.ReferenceComponent; } catch { }
                            if (rc == null) { ents.Add(JValue.CreateNull()); continue; }
                            var rIds = new List<int>();
                            for (var a = rc; a != null; a = a.GetParent() as IComponent2) rIds.Insert(0, SafeId(a));
                            if (SameIds(rIds, targetIds)) hit = true;
                            // Name it as the active assembly shows it.
                            IComponent2 shown = owner == null ? rc : ChildByIds(owner, rIds);
                            ents.Add(shown != null ? ComponentPath(shown)
                                : (owner != null ? ComponentPath(owner) + "/" : "") + ComponentPath(rc));
                        }
                    }
                    catch { }
                    if (!hit) continue;

                    var mj = new JObject { ["name"] = f.Name, ["in"] = owner != null ? ComponentPath(owner) : "" };
                    try
                    {
                        int t = mate.Type;
                        mj["type"] = t >= 0 && t < MateTypeNames.Length ? MateTypeNames[t] : "type " + t;
                    }
                    catch { }
                    try { if (f.IsSuppressed()) mj["suppressed"] = true; } catch { }
                    try
                    {
                        bool warn;
                        int code = f.GetErrorCode2(out warn);
                        if (code != 0)
                        {
                            mj["severity"] = warn ? "warning" : "error";
                            mj["code"] = code;
                            mj["meaning"] = FeatureErrors.TryGetValue(code, out var m) ? m : "code " + code;
                            if (!warn) broken++;
                        }
                    }
                    catch { }
                    mj["components"] = ents;
                    mates.Add(mj);
                }
            }
            return mates;
        }

        private static int SafeId(IComponent2 c)
        {
            try { return c.GetID(); } catch { return int.MinValue; }
        }

        private static bool SameIds(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // The component below `owner` reached by a chain of component IDs, or null.
        private static IComponent2 ChildByIds(IComponent2 owner, List<int> ids)
        {
            var cur = owner;
            foreach (int id in ids)
            {
                IComponent2 next = null;
                if (cur.GetChildren() is object[] kids)
                    foreach (var o in kids)
                        if (o is IComponent2 k && SafeId(k) == id) { next = k; break; }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        // Every mate feature of an assembly, from its first feature: the sub-features of its MateGroup, folders included.
        private static List<IFeature> MateFeatures(IFeature first)
        {
            var list = new List<IFeature>();
            var f = first;
            while (f != null)
            {
                if (f.GetTypeName2() == "MateGroup") CollectMates(f, list, 0);
                f = f.GetNextFeature() as IFeature;
            }
            return list;
        }

        private static void CollectMates(IFeature parent, List<IFeature> list, int depth)
        {
            if (depth > 5) return;
            var s = parent.GetFirstSubFeature() as IFeature;
            while (s != null)
            {
                if (s.GetSpecificFeature2() is IMate2) list.Add(s);
                else CollectMates(s, list, depth + 1);   // a mate folder
                s = s.GetNextSubFeature() as IFeature;
            }
        }
    }
}
