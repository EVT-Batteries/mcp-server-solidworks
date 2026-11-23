using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.SWRoutingLib;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: route_covering - coverings (sleeving, tape, conduit) on the route
    // segments of an electrical route, through the Routing API (IRouteProperty.CreateCovering /
    // AddCovering / GetCovering / RemoveCovering; ICovering Name, PartNumber, OuterDiameter, Color).
    // A covering applies to a whole route segment, as the Covering PropertyManager does. A covering
    // on part of a segment needs SOLIDWORKS' Split Route or Fixed Length Covering first; neither is
    // in the Routing API. Segments are chosen by id, by their wire set, or by a minimum wire count.
    public partial class SolidWorksService
    {
        public ExecutionResponse RouteCovering(ToolRequest request)
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
                string routeName = p?.Value<string>("route") ?? "";
                string action = (p?.Value<string>("action") ?? "list").ToLowerInvariant();
                if (action != "list" && action != "add" && action != "remove" && action != "export" && action != "import")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "action must be list, add, remove, export or import.");
                // export / import: the coverings keyed by the exact wire set of their segments, so a
                // route that was redrawn (new segment ids) gets them back where the wire sets agree.
                string filePath = p?.Value<string>("file_path") ?? "";
                if ((action == "export" || action == "import") && string.IsNullOrWhiteSpace(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", action + " needs file_path (a .json file).");
                var imported = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                if (action == "import")
                {
                    JArray entries;
                    try
                    {
                        var root = JToken.Parse(System.IO.File.ReadAllText(filePath));
                        entries = root is JArray ra ? ra : (root["coverings"] as JArray ?? new JArray());
                    }
                    catch (Exception ex)
                    {
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "file_path: " + ex.Message);
                    }
                    foreach (var e in entries.OfType<JObject>())
                    {
                        var ws = (e["wires"] as JArray)?.Select(t => (string)t).ToList();
                        if (ws == null || ws.Count == 0) continue;
                        imported[WireSetKey(ws)] = e;
                    }
                    if (imported.Count == 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER",
                            "file_path holds no covering entries ({wires: [...], name, part_number, outer_diameter_mm, color?}).");
                }
                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = doc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "route_covering needs an assembly active.");
                var routeComp = FindComponentByName(asm, routeName);
                if (routeComp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COMPONENT_NOT_FOUND", ComponentNotFoundMessage(asm, routeName));
                var routeAsm = routeComp.GetModelDoc2() as IAssemblyDoc;
                var rm = routeAsm?.GetRouteManager() as IRouteManager;
                if (rm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ROUTING_NOT_LOADED", "No RouteManager for the route (Routing add-in loaded? route resolved?).");

                // Selection parameters.
                var wantIds = p?["segments"] is JToken st && st.Type != JTokenType.Null
                    ? new HashSet<int>((st.Type == JTokenType.String ? JArray.Parse((string)st) : (JArray)st).Select(t => (int)t))
                    : null;
                var wantWires = p?["wires"] is JToken wt && wt.Type != JTokenType.Null
                    ? new HashSet<string>((wt.Type == JTokenType.String ? JArray.Parse((string)wt) : (JArray)wt).Select(t => (string)t), StringComparer.OrdinalIgnoreCase)
                    : null;
                if (wantIds != null && wantIds.Count == 0) wantIds = null;
                if (wantWires != null && wantWires.Count == 0) wantWires = null;
                int minWires = p?.Value<int?>("min_wires") ?? 0;
                if ((action == "add" || action == "remove") && wantIds == null && wantWires == null && minWires <= 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER",
                        "Choose the segments: segments (ids), wires (the exact wire set of the segments) or min_wires.");
                double od = p?.Value<double?>("outer_diameter") ?? 0;
                if (action == "add" && od <= 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER",
                        "add needs outer_diameter (m): the covered bundle's outside diameter.");
                string name = p?.Value<string>("name") ?? "";
                string pn = p?.Value<string>("part_number") ?? "";
                int? color = null;
                if (p?["color"] is JArray ca && ca.Count == 3) color = (int)ca[0] | ((int)ca[1] << 8) | ((int)ca[2] << 16);
                else if (p?["color"] != null && p["color"].Type == JTokenType.Integer) color = (int)p["color"];

                var steps = new JArray();
                var warnings = new JArray();
                var segOut = new JArray();
                var exported = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                var usedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uncovered = new JArray();
                int changed = 0;
                doc.ClearSelection2(true);
                routeComp.Select4(false, null, false);
                asm.EditAssembly();
                try
                {
                    var er = rm.GetElectricalRoute() as IElectricalRoute;
                    if (er == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NO_ELECTRICAL_ROUTE",
                            "No electrical route in " + ComponentPath(routeComp) + ".");
                    // Wire set of every segment.
                    var wiresOf = new Dictionary<int, SortedSet<string>>();
                    if (er.GetWires() is object[] ws)
                        foreach (var o in ws)
                            if (o is IWire w)
                            {
                                string wn = SafeStr(() => w.UserName);
                                foreach (int id in SegmentIdsOf(w))
                                {
                                    if (!wiresOf.TryGetValue(id, out var set)) wiresOf[id] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                                    set.Add(wn);
                                }
                            }
                    var allIds = new List<int>();
                    object idsObj = rm.GetAllRouteSegmentIDs();
                    if (idsObj is int[] ia) allIds.AddRange(ia);
                    else if (idsObj is object[] oa) allIds.AddRange(oa.Select(Convert.ToInt32));
                    foreach (int id in allIds)
                    {
                        wiresOf.TryGetValue(id, out var wset);
                        wset = wset ?? new SortedSet<string>();
                        string key = WireSetKey(wset);
                        JObject imp = null;
                        if (action == "import") imported.TryGetValue(key, out imp);
                        bool pick = (wantIds != null && wantIds.Contains(id))
                            || (wantWires != null && wset.SetEquals(wantWires))
                            || (minWires > 0 && wset.Count >= minWires);
                        if ((action == "add" || action == "remove") && !pick) continue;
                        var rp = er.GetRouteProperty(id) as IRouteProperty;
                        double len = 0;
                        if (er.GetSketchSegments(id) is object[] ss)
                            foreach (var so in ss) if (so is ISketchSegment sg) { try { len += sg.GetLength(); } catch { } }
                        var sj = new JObject
                        {
                            ["id"] = id,
                            ["length_mm"] = Math.Round(len * 1000, 2),
                            ["wires"] = new JArray(wset),
                        };
                        if (rp == null) { sj["error"] = "no route property"; segOut.Add(sj); continue; }
                        if (action == "remove" && rp.HasCovering)
                        {
                            bool ok = false;
                            try { rp.RemoveCovering(); ok = !rp.HasCovering; } catch (Exception ex) { sj["error"] = ex.Message; }
                            sj["removed"] = ok;
                            if (ok) changed++;
                        }
                        else if (action == "add")
                        {
                            bool ok = AddCoveringTo(rp, sj, name, pn, od, color);
                            sj["added"] = ok;
                            if (ok) changed++;
                        }
                        else if (action == "import" && imp != null)
                        {
                            int? ic = null;
                            if (imp["color"] is JArray ica && ica.Count == 3) ic = (int)ica[0] | ((int)ica[1] << 8) | ((int)ica[2] << 16);
                            else if (imp["color"] != null && imp["color"].Type == JTokenType.Integer) ic = (int)imp["color"];
                            bool ok = AddCoveringTo(rp, sj, imp.Value<string>("name") ?? "", imp.Value<string>("part_number") ?? "",
                                (imp.Value<double?>("outer_diameter_mm") ?? 0) / 1000.0, ic);
                            sj["added"] = ok;
                            if (ok) { changed++; usedImports.Add(key); }
                        }
                        bool covered = false;
                        try
                        {
                            covered = rp.HasCovering;
                            sj["has_covering"] = covered;
                            if (covered && rp.GetCovering() is ICovering c)
                            {
                                var cj = new JObject
                                {
                                    ["name"] = SafeStr(() => c.Name),
                                    ["part_number"] = SafeStr(() => c.PartNumber),
                                    ["outer_diameter_mm"] = Math.Round(c.OuterDiameter * 1000, 3),
                                };
                                try { int cc = c.Color; cj["color"] = new JArray(cc & 255, (cc >> 8) & 255, (cc >> 16) & 255); } catch { }
                                sj["covering"] = cj;
                                if (action == "export" && wset.Count > 0 && !exported.ContainsKey(key))
                                {
                                    var ej = (JObject)cj.DeepClone();
                                    ej.AddFirst(new JProperty("wires", new JArray(wset)));
                                    exported[key] = ej;
                                }
                            }
                        }
                        catch { }
                        if (action == "import")
                        {
                            if (!covered && wset.Count >= 2)
                                uncovered.Add(new JObject { ["id"] = id, ["length_mm"] = sj["length_mm"], ["wire_count"] = wset.Count, ["wires"] = new JArray(wset) });
                            if (imp == null) continue;
                        }
                        if (action == "export") continue;
                        segOut.Add(sj);
                    }
                    if (action == "export")
                    {
                        var doc0 = new JObject
                        {
                            ["route"] = ComponentPath(routeComp),
                            ["exported_utc"] = DateTime.UtcNow.ToString("s") + "Z",
                            ["coverings"] = new JArray(exported.Values),
                        };
                        var dirName = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
                        if (!string.IsNullOrEmpty(dirName)) System.IO.Directory.CreateDirectory(dirName);
                        System.IO.File.WriteAllText(filePath, doc0.ToString(Newtonsoft.Json.Formatting.Indented));
                        steps.Add($"export: {exported.Count} covering(s) by wire set written to {filePath}");
                    }
                    else steps.Add($"{action}: {segOut.Count} segment(s) listed, {changed} changed");
                    if ((action == "add" || action == "remove") && segOut.Count == 0) warnings.Add("no segment matched the selection");
                }
                finally
                {
                    try { asm.EditAssembly(); } catch { }
                    doc.ClearSelection2(true);
                }
                if (action == "add" || action == "remove" || action == "import")
                {
                    try { doc.EditRebuild3(); } catch { }
                    if (changed > 0)
                    {
                        // IRouteProperty.AddCovering / RemoveCovering do not set SOLIDWORKS' dirty flag on the
                        // route sub-assembly, so save_document() on the parent would otherwise skip saving it.
                        try { (routeAsm as IModelDoc2)?.SetSaveFlag(); } catch { }
                        try { doc.SetSaveFlag(); } catch { }
                    }
                }
                var result = new JObject
                {
                    ["route"] = ComponentPath(routeComp),
                    ["segments"] = segOut,
                    ["note"] = "A covering applies to whole route segments. For a covering on part of a segment (e.g. a manufacturer's free zone at a housing), split the segment first in SOLIDWORKS (Split Route) or use Fixed Length Covering; neither is in the Routing API.",
                    ["steps"] = steps,
                };
                if (action == "export")
                {
                    result.Remove("segments");
                    result["file_path"] = filePath;
                    result["exported"] = exported.Count;
                    result["coverings"] = new JArray(exported.Values);
                }
                if (action == "import")
                {
                    result["applied_segments"] = changed;
                    result["unmatched_entries"] = new JArray(imported.Where(kv => !usedImports.Contains(kv.Key))
                        .Select(kv => new JObject { ["wires"] = kv.Value["wires"], ["part_number"] = kv.Value["part_number"] }));
                    result["uncovered_segments"] = uncovered;
                    result["note_import"] = "Applied where a segment's wire set equals an entry's exactly. uncovered_segments (2+ wires, no covering) are new wire sets: cover them with action='add' (segments=[ids]).";
                }
                if (warnings.Count > 0) result["warnings"] = warnings;
                if (action == "list" || action == "export") return ReadOnlyResponse(request, doc, result);
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
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // The order-independent key of a wire set (export / import match segments by it).
        private static string WireSetKey(IEnumerable<string> wires) =>
            string.Join("|", wires.Select(w => (w ?? "").Trim()).OrderBy(w => w, StringComparer.OrdinalIgnoreCase));

        // Put a covering on a route segment (replacing one it has). The added covering is a copy:
        // its properties are set again on the attached one (as the API's Edit Coverings example does);
        // OuterDiameter set before AddCovering read back 0.
        private static bool AddCoveringTo(IRouteProperty rp, JObject sj, string name, string pn, double od, int? color)
        {
            if (rp.HasCovering) { try { rp.RemoveCovering(); sj["replaced"] = true; } catch { } }
            var cov = rp.CreateCovering() as Covering;
            if (cov == null) { sj["error"] = "CreateCovering returned nothing"; return false; }
            var ic = (ICovering)cov;
            if (!string.IsNullOrEmpty(name)) ic.Name = name;
            if (!string.IsNullOrEmpty(pn)) ic.PartNumber = pn;
            ic.OuterDiameter = od;
            if (color.HasValue) ic.Color = color.Value;
            bool ok = rp.AddCovering(cov);
            if (ok && rp.GetCovering() is ICovering att)
            {
                if (!string.IsNullOrEmpty(name)) att.Name = name;
                if (!string.IsNullOrEmpty(pn)) att.PartNumber = pn;
                att.OuterDiameter = od;
                if (color.HasValue) att.Color = color.Value;
            }
            return ok;
        }
    }
}
