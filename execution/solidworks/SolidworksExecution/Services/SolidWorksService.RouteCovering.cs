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
                if (action != "list" && action != "add" && action != "remove")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "action must be list, add or remove.");
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
                if (action != "list" && wantIds == null && wantWires == null && minWires <= 0)
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
                    int changed = 0;
                    foreach (int id in allIds)
                    {
                        wiresOf.TryGetValue(id, out var wset);
                        wset = wset ?? new SortedSet<string>();
                        bool pick = (wantIds != null && wantIds.Contains(id))
                            || (wantWires != null && wset.SetEquals(wantWires))
                            || (minWires > 0 && wset.Count >= minWires);
                        if (action != "list" && !pick) continue;
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
                            if (rp.HasCovering) { try { rp.RemoveCovering(); sj["replaced"] = true; } catch { } }
                            var cov = rp.CreateCovering() as Covering;
                            if (cov == null) { sj["error"] = "CreateCovering returned nothing"; segOut.Add(sj); continue; }
                            var ic = (ICovering)cov;
                            if (!string.IsNullOrEmpty(name)) ic.Name = name;
                            if (!string.IsNullOrEmpty(pn)) ic.PartNumber = pn;
                            ic.OuterDiameter = od;
                            if (color.HasValue) ic.Color = color.Value;
                            bool ok = rp.AddCovering(cov);
                            // The added covering is a copy: set its properties on the attached one
                            // (as the API's Edit Coverings example does); OuterDiameter set before
                            // AddCovering read back 0.
                            if (ok && rp.GetCovering() is ICovering att)
                            {
                                if (!string.IsNullOrEmpty(name)) att.Name = name;
                                if (!string.IsNullOrEmpty(pn)) att.PartNumber = pn;
                                att.OuterDiameter = od;
                                if (color.HasValue) att.Color = color.Value;
                            }
                            sj["added"] = ok;
                            if (ok) changed++;
                        }
                        try
                        {
                            sj["has_covering"] = rp.HasCovering;
                            if (rp.HasCovering && rp.GetCovering() is ICovering c)
                                sj["covering"] = new JObject
                                {
                                    ["name"] = SafeStr(() => c.Name),
                                    ["part_number"] = SafeStr(() => c.PartNumber),
                                    ["outer_diameter_mm"] = Math.Round(c.OuterDiameter * 1000, 3),
                                };
                        }
                        catch { }
                        segOut.Add(sj);
                    }
                    steps.Add($"{action}: {segOut.Count} segment(s) listed, {changed} changed");
                    if (action != "list" && segOut.Count == 0) warnings.Add("no segment matched the selection");
                }
                finally
                {
                    try { asm.EditAssembly(); } catch { }
                    doc.ClearSelection2(true);
                }
                if (action != "list") { try { doc.EditRebuild3(); } catch { } }
                var result = new JObject
                {
                    ["route"] = ComponentPath(routeComp),
                    ["segments"] = segOut,
                    ["note"] = "A covering applies to whole route segments. For a covering on part of a segment (e.g. a manufacturer's free zone at a housing), split the segment first in SOLIDWORKS (Split Route) or use Fixed Length Covering; neither is in the Routing API.",
                    ["steps"] = steps,
                };
                if (warnings.Count > 0) result["warnings"] = warnings;
                if (action == "list") return ReadOnlyResponse(request, doc, result);
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
    }
}
