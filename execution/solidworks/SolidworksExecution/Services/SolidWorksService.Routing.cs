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
    // SolidWorksService partial: SOLIDWORKS Routing (electrical) through the Routing add-in API
    // (SolidWorks.Interop.SWRoutingLib, embedded interop types: no extra DLL ships).
    //   routing_status     read-only: add-in state, routing file locations, the routing points and
    //                      component type of the active part, or the route sub-assemblies of the
    //                      active assembly with their wires (cut lengths, route errors) and segments.
    //   add_routing_point  active part: a connection point (CPoint: cable exit face + point) or a
    //                      route point (RPoint), and optionally the Routing component type
    //                      (connector / clip / splice).
    //   start_route        active assembly: starts an electrical route at a component's CPoint
    //                      (RouteManager.StartRoute), accepts the Route Properties page
    //                      (RunCommand PmOK) and reports where SolidWorks left the edit focus.
    // Routing needs the add-in loaded (Tools > Add-Ins > SOLIDWORKS Routing); every call reports it.
    public partial class SolidWorksService
    {
        private const string RoutingAddinClsid = "{ceac97a6-01f7-11d3-9398-00c04f79bffe}";

        // swRouteComponentTypeID_e, inlined (only the electrical ones we use).
        private static readonly Dictionary<string, int> RoutingComponentTypes = new Dictionary<string, int>
        {
            { "connector", 2 }, { "clip", 14 }, { "splice", 27 }, { "equipment", 16 },
        };

        // swWireRouteError_e, inlined.
        private static string WireRouteErrorName(int e)
        {
            switch (e)
            {
                case 1: return "not_routed";
                case 2: return "segments_branching";
                case 3: return "segments_disjoint";
                case 4: return "missing_from_to_component";
                case 5: return "mismatched_from_component";
                case 6: return "mismatched_to_component";
                case 7: return "mismatched_from_pin";
                case 8: return "mismatched_to_pin";
                case 9: return "violates_bend_radius";
                default: return "error_" + e;
            }
        }

        // verbose: every routing file location and the from-to header definitions; otherwise only
        // whether the add-in is loaded and the cable / component libraries (the rest repeated the
        // same ~1 kB block on every routing call).
        private JObject RoutingAddinInfo(bool verbose)
        {
            var o = new JObject();
            object addin = null;
            try { addin = _solidWorks.GetAddInObject(RoutingAddinClsid); } catch { }
            o["addin_object"] = addin != null;
            try
            {
                var rs = _solidWorks.GetRoutingSettings() as IRoutingSettings;
                if (rs != null)
                {
                    // swUserPreferenceRoutingFileLocations_e 1..10, inlined.
                    string[] names = { "", "routing_library", "routing_assembly_template", "standard_tubes",
                        "pipe_tube_covering_library", "cable_library", "component_library", "standard_cable",
                        "covering_library", "tag_schemes", "interconnects_library" };
                    var loc = new JObject();
                    for (int i = 1; i <= 10; i++)
                    {
                        try { loc[names[i]] = rs.GetRoutingUserPreferenceStringValue(i, false) ?? ""; }
                        catch { }
                    }
                    if (!verbose)
                    {
                        o["cable_library"] = loc["cable_library"];
                        o["component_library"] = loc["component_library"];
                        return o;
                    }
                    o["file_locations"] = loc;
                    try
                    {
                        rs.GetFromToListHeaderDefinitions(out string w, out string fr, out string fp, out string fpn, out string tr,
                            out string tp, out string tpn, out string cab, out string core, out string col, out string spec, out string oth);
                        o["from_to_headers"] = new JObject
                        {
                            ["wire"] = w, ["from_ref"] = fr, ["from_pin"] = fp, ["from_partno"] = fpn,
                            ["to_ref"] = tr, ["to_pin"] = tp, ["to_partno"] = tpn, ["cable"] = cab,
                            ["core"] = core, ["color"] = col, ["spec"] = spec, ["other"] = oth,
                        };
                    }
                    catch (Exception ex) { o["from_to_headers_error"] = ex.Message; }
                }
            }
            catch (Exception ex) { o["file_locations_error"] = ex.Message; }
            return o;
        }

        // Features of a part that look like routing points: type name and feature name, so the
        // CPoint / RPoint type names can be learned from a live part.
        private static JArray RoutingFeaturesOf(IModelDoc2 doc)
        {
            var arr = new JArray();
            var f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                string t = "";
                try { t = f.GetTypeName2() ?? ""; } catch { }
                string n = f.Name ?? "";
                string tl = t.ToLowerInvariant();
                if (tl.Contains("connectionpoint") || tl.Contains("routepoint") || tl.Contains("cpoint")
                    || tl.Contains("rpoint") || tl.Contains("routing") || tl.Contains("clip"))
                    arr.Add(new JObject { ["name"] = n, ["type"] = t });
                f = f.GetNextFeature() as IFeature;
            }
            return arr;
        }

        private static JObject RoutingComponentInfo(IModelDoc2 doc)
        {
            var o = new JObject();
            try
            {
                var rcm = doc.Extension.GetRoutingComponentManager() as IRoutingComponentManager;
                if (rcm == null) { o["manager"] = false; return o; }
                o["manager"] = true;
                try { o["component_type"] = rcm.GetComponentType(); } catch (Exception ex) { o["component_type_error"] = ex.Message; }
                try { o["route_type"] = rcm.GetComponentRouteType(); } catch { }
                try { o["sub_type"] = rcm.GetRouteComponentSubType(); } catch { }
                try { o["description"] = rcm.GetRoutingComponentDescription(); } catch { }
            }
            catch (Exception ex) { o["error"] = ex.Message; }
            return o;
        }

        public ExecutionResponse RoutingStatus(ToolRequest request)
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

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var result = new JObject { ["routing"] = RoutingAddinInfo(p?.Value<bool?>("verbose") ?? false) };
                if (doc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "routing_status needs an open part or assembly.");
                result["document"] = doc.GetTitle();

                var asm = doc as IAssemblyDoc;
                if (asm == null)
                {
                    result["routing_points"] = RoutingFeaturesOf(doc);
                    result["connection_points"] = new JArray(ConnectionPointsOf(doc));
                    result["routing_component"] = RoutingComponentInfo(doc);
                    return ReadOnlyResponse(request, doc, result);
                }

                object rmObj = null;
                try { rmObj = asm.GetRouteManager(); } catch (Exception ex) { result["route_manager_error"] = ex.Message; }
                result["route_manager"] = rmObj != null;
                result["is_route_assembly"] = asm.IsRouteAssembly();
                try
                {
                    var edit = asm.GetEditTarget() as IModelDoc2;
                    result["edit_target"] = edit?.GetTitle();
                }
                catch { }

                var routes = new JArray();
                if (asm.GetComponents(false) is object[] all)
                    foreach (var c in all)
                    {
                        var comp = c as IComponent2;
                        if (comp == null) continue;
                        var sub = comp.GetModelDoc2() as IAssemblyDoc;
                        if (sub == null) continue;
                        bool isRoute = false;
                        try { isRoute = sub.IsRouteAssembly(); } catch { }
                        if (!isRoute) continue;
                        routes.Add(new JObject
                        {
                            ["component"] = ComponentPath(comp),
                            ["file"] = comp.GetPathName(),
                        });
                    }
                result["route_assemblies"] = routes;

                // connectors: their connection points (pin, location, direction, stub end) in this
                // assembly's coordinates, for starting route branches on the stub ends as they are
                // NOW (a plug moved in another configuration once left a plan 2.5 mm off).
                var connTok = p?["connectors"];
                if (connTok != null && connTok.Type != JTokenType.Null && !(connTok.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)connTok)))
                {
                    var conns = new JArray();
                    var names = new List<string>();
                    string fragment = null;
                    if (connTok.Type == JTokenType.String && ((string)connTok).TrimStart().StartsWith("["))
                        names = JArray.Parse((string)connTok).Select(t => (string)t).ToList();
                    else if (connTok is JArray ca) names = ca.Select(t => (string)t).ToList();
                    else fragment = (string)connTok;
                    var comps = new List<IComponent2>();
                    foreach (var n in names)
                    {
                        var c = FindComponentByName(asm, n);
                        if (c == null) conns.Add(new JObject { ["component"] = n, ["error"] = "not found" });
                        else comps.Add(c);
                    }
                    if (fragment != null && asm.GetComponents(false) is object[] allc)
                        foreach (var co in allc)
                            if (co is IComponent2 c2 && (c2.Name2 ?? "").IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0
                                && c2.GetSuppression2() != 0 && c2.GetModelDoc2() is IPartDoc)
                                comps.Add(c2);
                    foreach (var c in comps)
                    {
                        var cj = ConnectorPointsWorld(c);
                        if (cj != null) conns.Add(cj);
                    }
                    result["connectors"] = conns;
                }

                if (!string.IsNullOrEmpty(routeName))
                {
                    var comp = FindComponentByName(asm, routeName);
                    if (comp == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "COMPONENT_NOT_FOUND", ComponentNotFoundMessage(asm, routeName));
                    var rj = ReadElectricalRoute(doc, asm, comp);
                    var terms = ParseTerminations(p?["terminations"]);
                    if (terms != null) ApplyTerminations(rj, terms);
                    result["route"] = rj;
                }
                return ReadOnlyResponse(request, doc, result);
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

        // Wires, cables and route segments of a route sub-assembly. The Routing API gives route
        // properties only while the route component is being edited: edit it, read, and return the
        // edit focus to the top assembly.
        private JObject ReadElectricalRoute(IModelDoc2 top, IAssemblyDoc topAsm, IComponent2 routeComp)
        {
            var o = new JObject { ["component"] = ComponentPath(routeComp) };
            var routeAsm = routeComp.GetModelDoc2() as IAssemblyDoc;
            if (routeAsm == null) { o["error"] = "component is not an assembly (or not loaded)"; return o; }
            var rm = routeAsm.GetRouteManager() as IRouteManager;
            if (rm == null) { o["error"] = "no RouteManager (Routing add-in not loaded?)"; return o; }

            top.ClearSelection2(true);
            routeComp.Select4(false, null, false);
            topAsm.EditAssembly();
            try
            {
                var er = rm.GetElectricalRoute() as IElectricalRoute;
                if (er == null) { o["error"] = "no electrical route in this route assembly"; return o; }

                var wires = new JArray();
                if (er.GetWires() is object[] ws)
                    foreach (var w in ws)
                    {
                        var wire = w as IWire;
                        if (wire == null) continue;
                        var wj = new JObject
                        {
                            ["name"] = SafeStr(() => wire.UserName),
                            ["from"] = SafeStr(() => wire.FromComponentReference) + "." + SafeStr(() => wire.FromPinReference),
                            ["to"] = SafeStr(() => wire.ToComponentReference) + "." + SafeStr(() => wire.ToPinReference),
                        };
                        try { wj["cut_length_mm"] = Math.Round(wire.GetCuttingLength() * 1000.0, 2); } catch { }
                        try
                        {
                            if (wire.GetRouteSegmentIDs() is int[] ids) wj["segments"] = new JArray(ids);
                            else if (wire.GetRouteSegmentIDs() is object[] oids) wj["segments"] = new JArray(oids);
                        }
                        catch { }
                        try
                        {
                            var errs = new JArray();
                            object eo = wire.GetRouteErrors();
                            if (eo is int[] ei) foreach (var e in ei) errs.Add(WireRouteErrorName(e));
                            else if (eo is object[] eob) foreach (var e in eob) errs.Add(WireRouteErrorName(Convert.ToInt32(e)));
                            if (errs.Count > 0) wj["errors"] = errs;
                        }
                        catch { }
                        try
                        {
                            var wp = wire.GetWireProperty() as IWireProperty;
                            if (wp != null)
                            {
                                wj["wire_name"] = SafeStr(() => wp.Name);
                                try { wj["color"] = wp.Color; } catch { }
                                wj["diameter_mm"] = Math.Round(wp.Diameter * 1000.0, 3);
                            }
                        }
                        catch { }
                        wires.Add(wj);
                    }
                o["wires"] = wires;
                try { o["cables_count"] = er.GetCablesCount(); } catch { }

                var segs = new JArray();
                try
                {
                    object idsObj = rm.GetAllRouteSegmentIDs();
                    var ids = new List<int>();
                    if (idsObj is int[] ia) ids.AddRange(ia);
                    else if (idsObj is object[] oa) foreach (var x in oa) ids.Add(Convert.ToInt32(x));
                    foreach (int id in ids)
                    {
                        var sj = new JObject { ["id"] = id };
                        double len = 0;
                        int n = 0;
                        if (er.GetSketchSegments(id) is object[] ss)
                            foreach (var s in ss)
                            {
                                var seg = s as ISketchSegment;
                                if (seg == null) continue;
                                try { len += seg.GetLength(); n++; } catch { }
                            }
                        sj["sketch_segments"] = n;
                        sj["length_mm"] = Math.Round(len * 1000.0, 2);
                        try
                        {
                            var rp = er.GetRouteProperty(id) as IRouteProperty;
                            if (rp != null)
                            {
                                sj["bend_radius_mm"] = Math.Round(rp.BendRadius * 1000.0, 2);
                                var erp = rp as IElectricalRouteProperty;
                                if (erp != null) sj["bundle_diameter_mm"] = Math.Round(erp.BundleDiameter * 1000.0, 3);
                            }
                        }
                        catch { }
                        segs.Add(sj);
                    }
                }
                catch (Exception ex) { o["segments_error"] = ex.Message; }
                o["segments"] = segs;
            }
            finally
            {
                // Back to editing the top assembly.
                try { topAsm.EditAssembly(); } catch { }
                top.ClearSelection2(true);
            }
            return o;
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        // terminations: {"<ref>.<pin>" | "<ref>" | "*": allowance_mm | {"allowance_mm": a,
        // "strip_mm": s, ...}}. The allowance is the wire length beyond the route end at a
        // connection point (housing rear face -> conductor end in the crimp: insulation inside the
        // housing + the stripped length); the most specific key wins.
        private static JObject ParseTerminations(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            JObject o = tok.Type == JTokenType.String
                ? (string.IsNullOrWhiteSpace((string)tok) ? null : JObject.Parse((string)tok))
                : tok as JObject;
            if (o == null || o.Count == 0) return null;
            var norm = new JObject();
            foreach (var kv in o)
                norm[kv.Key] = kv.Value.Type == JTokenType.Object ? kv.Value : new JObject { ["allowance_mm"] = kv.Value };
            return norm;
        }

        private static JObject TerminationFor(JObject terms, string end)
        {
            if (string.IsNullOrEmpty(end)) return null;
            int dot = end.LastIndexOf('.');
            string reference = dot > 0 ? end.Substring(0, dot) : end;
            foreach (var key in new[] { end, reference, "*" })
                foreach (var kv in terms)
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return (JObject)kv.Value;
            return null;
        }

        // Adds to each wire of a route read: termination {from, to} and total_cut_length_mm = SOLIDWORKS
        // cut length (route end to route end, stubs included) + both allowances; wires with an end
        // that has no termination entry are listed in terminations_missing.
        private static void ApplyTerminations(JObject route, JObject terms)
        {
            if (!(route["wires"] is JArray wires)) return;
            var missing = new JArray();
            foreach (var w in wires.OfType<JObject>())
            {
                var tf = TerminationFor(terms, (string)w["from"]);
                var tt = TerminationFor(terms, (string)w["to"]);
                var tj = new JObject();
                if (tf != null) tj["from"] = tf;
                if (tt != null) tj["to"] = tt;
                w["termination"] = tj;
                if (tf == null || tt == null) { missing.Add((string)w["name"]); continue; }
                double cut = w.Value<double?>("cut_length_mm") ?? 0;
                if (cut <= 0) continue;   // unrouted: no total
                double a = tf.Value<double?>("allowance_mm") ?? 0, b = tt.Value<double?>("allowance_mm") ?? 0;
                w["total_cut_length_mm"] = Math.Round(cut + a + b, 2);
            }
            route["terminations"] = terms;
            if (missing.Count > 0) route["terminations_missing"] = missing;
        }

        public ExecutionResponse AddRoutingPoint(ToolRequest request)
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
                string kind = (p?.Value<string>("kind") ?? "connection").Trim().ToLowerInvariant();
                int faceIndex = p?.Value<int?>("face_index") ?? -1;
                string name = p?.Value<string>("name") ?? "";
                string compType = (p?.Value<string>("component_type") ?? "").Trim().ToLowerInvariant();
                int subType = p?.Value<int?>("sub_type") ?? 2;          // swElectricalConnectionPoint_CableOrWire
                double diameter = p?.Value<double?>("diameter") ?? 0.0;
                double stub = p?.Value<double?>("stub_length") ?? 0.0;
                bool reverse = p?.Value<bool?>("reverse") ?? false;

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (!(doc is IPartDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "add_routing_point needs an active part (open the connector or clip part).");
                if (kind != "connection" && kind != "route" && kind != "none")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "kind must be 'connection', 'route' or 'none' (only set component_type).");
                if (compType != "" && !RoutingComponentTypes.ContainsKey(compType))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "component_type must be one of: " + string.Join(", ", RoutingComponentTypes.Keys));

                var result = new JObject { ["routing"] = RoutingAddinInfo(p?.Value<bool?>("verbose") ?? false) };
                var warnings = new JArray();

                if (kind != "none")
                {
                    var pts = new List<double[]>();
                    try
                    {
                        var pa = JArray.Parse(p?.Value<string>("point") ?? "");
                        if (pa.Count == 3) pts.Add(new[] { (double)pa[0], (double)pa[1], (double)pa[2] });
                    }
                    catch { }
                    // direction (instead of face_index): the cable exit is the outermost planar face
                    // whose outward normal is this direction; the point goes onto that face (projected
                    // along the direction, or the face centre when no point is given). A point chosen
                    // by hand once sat on an internal face, 8.7 mm inside the plug body.
                    double[] exitDir = null;
                    try
                    {
                        var da = JArray.Parse(p?.Value<string>("direction") ?? "");
                        if (da.Count == 3) exitDir = Unit(new[] { (double)da[0], (double)da[1], (double)da[2] });
                    }
                    catch { }
                    if (kind == "connection" && faceIndex < 0 && exitDir != null)
                    {
                        var exit = FindExitFace(doc, exitDir, pts.Count == 1 ? pts[0] : null, p?.Value<double?>("exit_depth") ?? 0.001);
                        if (exit == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "FACE_NOT_FOUND",
                                "No planar face of a solid body has its outward normal along direction: check the direction (part coordinates) or give face_index.");
                        faceIndex = exit.Value<int>("face_index");
                        var ep = (JArray)exit["point"];
                        pts.Clear();
                        pts.Add(new[] { (double)ep[0], (double)ep[1], (double)ep[2] });
                        result["exit_face"] = exit;
                        if (exit.Value<double>("body_extent_beyond_mm") > 0.5)
                            warnings.Add($"solid geometry reaches {exit.Value<double>("body_extent_beyond_mm")} mm beyond the exit face along direction (box estimate): check the face is the wire exit");
                    }
                    if (pts.Count != 1)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "point must be one JSON [x, y, z] in meters (part coordinates); optional for a connection point given by direction.");
                    if (kind == "connection" && faceIndex < 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "a connection point needs face_index (the face the cable leaves through, analyze_model(faces)) or direction (its outward normal).");

                    // A 3D sketch holding the single point that marks the routing point.
                    var sm = doc.SketchManager;
                    doc.ClearSelection2(true);
                    sm.Insert3DSketch(true);
                    bool oldDb = sm.AddToDB;
                    sm.AddToDB = true;
                    var spt = sm.CreatePoint(pts[0][0], pts[0][1], pts[0][2]) as ISketchPoint;
                    sm.AddToDB = oldDb;
                    sm.Insert3DSketch(true);
                    var sketchFeat = doc.FeatureByPositionReverse(0) as IFeature;
                    if (spt == null || sketchFeat == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_FAILED", "Could not create the 3D sketch point for the routing point.");
                    if (!string.IsNullOrEmpty(name))
                        try { sketchFeat.Name = name + "_Sketch"; } catch { }
                    result["sketch"] = sketchFeat.Name;

                    // Re-find the point through the saved sketch (the creation object goes stale on exit).
                    ISketchPoint ptObj = null;
                    var sk = sketchFeat.GetSpecificFeature2() as ISketch;
                    if (sk?.GetSketchPoints2() is object[] skp && skp.Length > 0) ptObj = skp[0] as ISketchPoint;
                    if (ptObj == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_FAILED", "The routing-point sketch has no point after exit.");

                    var selMgr = doc.SelectionManager as ISelectionMgr;
                    doc.ClearSelection2(true);
                    bool ok;
                    if (kind == "connection")
                    {
                        if (!SelectFaceByIndexAppend(doc, faceIndex, 1))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "INVALID_PARAMETER", $"face_index {faceIndex} could not be selected (analyze_model(faces)).");
                        var sd = selMgr.CreateSelectData();
                        sd.Mark = 1;
                        ok = ptObj.Select4(true, sd);
                        if (!ok) warnings.Add("sketch point could not be selected with mark 1");
                        ok = doc.FeatureManager.InsertConnectionPoint(3 /* electrical */, subType, reverse,
                            diameter, stub, 0, 0, 0, "", 0, 0, false, "", "");
                    }
                    else
                    {
                        var sd = selMgr.CreateSelectData();
                        sd.Mark = 0;
                        ptObj.Select4(false, sd);
                        doc.InsertRoutePoint();
                        ok = true;
                    }
                    var newFeat = doc.FeatureByPositionReverse(0) as IFeature;
                    string newType = "";
                    try { newType = newFeat?.GetTypeName2() ?? ""; } catch { }
                    result["inserted"] = ok;
                    result["feature"] = newFeat?.Name;
                    result["feature_type"] = newType;
                    if (newFeat != null && newFeat.Name == sketchFeat.Name)
                    {
                        result["inserted"] = false;
                        warnings.Add("no new feature after the sketch: SolidWorks did not create the routing point " +
                            "(Routing add-in loaded? selection accepted?)");
                    }
                    else if (newFeat != null && !string.IsNullOrEmpty(name))
                        try { newFeat.Name = name; result["feature"] = newFeat.Name; } catch { }
                    doc.ClearSelection2(true);
                }

                if (compType != "")
                {
                    var rcm = doc.Extension.GetRoutingComponentManager() as IRoutingComponentManager;
                    if (rcm == null) warnings.Add("no RoutingComponentManager: component type not set");
                    else
                    {
                        try { rcm.SetComponentType(RoutingComponentTypes[compType]); } catch (Exception ex) { warnings.Add("SetComponentType: " + ex.Message); }
                        try { rcm.SetComponentRouteTypeToCustomProperty(3 /* swElectrical */); } catch (Exception ex) { warnings.Add("SetComponentRouteTypeToCustomProperty: " + ex.Message); }
                    }
                }
                result["routing_points"] = RoutingFeaturesOf(doc);
                result["routing_component"] = RoutingComponentInfo(doc);
                if (warnings.Count > 0) result["warnings"] = warnings;

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
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // The route sub-assembly being edited, or null.
        private static IComponent2 RouteBeingEdited(IAssemblyDoc asm)
        {
            try
            {
                var target = asm.GetEditTarget() as IModelDoc2;
                if (!(target is IAssemblyDoc ta) || !ta.IsRouteAssembly()) return null;
                if (asm.GetComponents(false) is object[] all)
                    foreach (var c in all)
                    {
                        var cc = c as IComponent2;
                        if (cc != null && string.Equals(cc.GetPathName(), target.GetPathName(), StringComparison.OrdinalIgnoreCase))
                            return cc;
                    }
            }
            catch { }
            return null;
        }

        // Route sub-assemblies (IsRouteAssembly) anywhere under the assembly.
        private static List<IComponent2> RouteComponents(IAssemblyDoc asm)
        {
            var list = new List<IComponent2>();
            if (asm.GetComponents(false) is object[] all)
                foreach (var c in all)
                {
                    var cc = c as IComponent2;
                    var sub = cc?.GetModelDoc2() as IAssemblyDoc;
                    bool isRoute = false;
                    try { isRoute = sub != null && sub.IsRouteAssembly(); } catch { }
                    if (isRoute) list.Add(cc);
                }
            return list;
        }

        // Route the wires of a route sub-assembly through its segments (IElectricalRoute.RouteWires,
        // available while the route component is edited), back to the top assembly after.
        private void RouteWiresOn(IModelDoc2 doc, IAssemblyDoc asm, IComponent2 routeComp, JArray steps, JArray warnings,
                                  JObject wirePaths = null, bool routeFirst = true)
        {
            var routeAsm = routeComp.GetModelDoc2() as IAssemblyDoc;
            var rrm = routeAsm?.GetRouteManager() as IRouteManager;
            doc.ClearSelection2(true);
            routeComp.Select4(false, null, false);
            asm.EditAssembly();
            try
            {
                var er = rrm?.GetElectricalRoute() as IElectricalRoute;
                if (er == null) warnings.Add("no electrical route in " + ComponentPath(routeComp));
                else
                {
                    if (routeFirst) steps.Add("RouteWires: " + er.RouteWires());
                    if (wirePaths != null && wirePaths.Count > 0) SetWirePaths(er, wirePaths, steps, warnings);
                }
            }
            finally { try { asm.EditAssembly(); } catch { } doc.ClearSelection2(true); }
        }

        // wire_paths parameter: a JSON object (or its string form) {wire: [segment ids] | "<wire>"}.
        private static JObject ParseWirePaths(JObject p)
        {
            var tok = p?["wire_paths"];
            if (tok == null || tok.Type == JTokenType.Null) return null;
            JObject o = tok.Type == JTokenType.String
                ? (string.IsNullOrWhiteSpace((string)tok) ? null : JObject.Parse((string)tok))
                : tok as JObject;
            return o != null && o.Count > 0 ? o : null;
        }

        private static int[] SegmentIdsOf(IWire w)
        {
            object o = null;
            try { o = w.GetRouteSegmentIDs(); } catch { }
            if (o is int[] ia) return ia;
            if (o is object[] oa) return oa.Select(Convert.ToInt32).ToArray();
            return new int[0];
        }

        // Set wires' paths explicitly (IWire.SetRoutePathForWire): {wire: [segment ids]} or
        // {wire: "<other wire>"} = the path of another wire. RouteWires can attach a wire end to the
        // wrong connector or leave it unrouted although its from-to data are right (seen on four
        // wires ending on pins 1-4 of one 12-way connector, whose twins to the same connectors
        // routed correctly); copying the twin's path fixes them.
        private static void SetWirePaths(IElectricalRoute er, JObject wirePaths, JArray steps, JArray warnings)
        {
            var byName = new Dictionary<string, IWire>(StringComparer.OrdinalIgnoreCase);
            if (er.GetWires() is object[] ws)
                foreach (var o in ws)
                    if (o is IWire w) { string n = SafeStr(() => w.UserName); if (!string.IsNullOrEmpty(n)) byName[n] = w; }
            foreach (var kv in wirePaths)
            {
                if (!byName.TryGetValue(kv.Key, out var target)) { warnings.Add($"wire_paths: wire {kv.Key} not found"); continue; }
                int[] ids;
                string source;
                if (kv.Value.Type == JTokenType.String)
                {
                    string other = (string)kv.Value;
                    if (!byName.TryGetValue(other, out var tw)) { warnings.Add($"wire_paths: wire {other} (path of {kv.Key}) not found"); continue; }
                    ids = SegmentIdsOf(tw);
                    source = "path of " + other;
                }
                else
                {
                    ids = (kv.Value as JArray)?.Select(t => (int)t).ToArray() ?? new int[0];
                    source = "segments";
                }
                if (ids.Length == 0) { warnings.Add($"wire_paths: no segments for {kv.Key} ({source})"); continue; }
                int rc = -1;
                try { rc = target.SetRoutePathForWire(ids); }
                catch (Exception ex) { warnings.Add($"wire_paths: {kv.Key}: " + ex.Message); continue; }
                steps.Add($"wire {kv.Key} <- {source} [{string.Join(",", ids)}]: SetRoutePathForWire {rc}");
            }
        }

        // Edit the route, turn its guidelines (the connector pairs a from-to import still has to
        // wire) into route geometry, leave route editing.
        private void ConvertGuidelinesOn(IModelDoc2 doc, IAssemblyDoc asm, IRouteManager rm, IComponent2 routeComp,
                                         JArray steps, JArray warnings)
        {
            var rf = RouteFeatureOf(routeComp);
            if (rf == null) { warnings.Add("convert guidelines: no route feature in " + ComponentPath(routeComp)); return; }
            doc.ClearSelection2(true);
            steps.Add($"select {rf.Name}: " + rf.Select2(false, 0));
            rm.EditRoute();
            try
            {
                var ar = rm.GetAutoRoute() as IAutoRoute;
                if (ar == null) { warnings.Add("convert guidelines: GetAutoRoute returned nothing"); return; }
                steps.Add("ShowGuidelines: " + ar.ShowGuidelines());
                doc.ClearSelection2(true);
                steps.Add("SelectGuidelines: " + ar.SelectGuidelines());
                int sel = 0;
                try { sel = (doc.SelectionManager as ISelectionMgr).GetSelectedObjectCount2(-1); } catch { }
                steps.Add("guidelines selected: " + sel);
                if (sel > 0) steps.Add("ConvertGuidelinesToRoute: " + ar.ConvertGuidelinesToRoute());
                doc.ClearSelection2(true);
            }
            finally
            {
                try { rm.ExitRoute(); } catch { }
                try { asm.EditAssembly(); } catch { }
            }
        }

        // The route feature (Route1) of a route sub-assembly, in the context of the top assembly.
        // The 3D sketch of a route sub-assembly, read through the component (route-assembly
        // coordinates = top coordinates: the route sits at the identity), without editing it.
        private static ISketch RouteSketchOf(IComponent2 routeComp)
        {
            var f = routeComp.FirstFeature() as IFeature;
            while (f != null)
            {
                string t = "";
                try { t = f.GetTypeName2() ?? ""; } catch { }
                if (t == "3DProfileFeature" && f.GetSpecificFeature2() is ISketch sk) return sk;
                var sub = f.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    string st = "";
                    try { st = sub.GetTypeName2() ?? ""; } catch { }
                    if (st == "3DProfileFeature" && sub.GetSpecificFeature2() is ISketch ssk) return ssk;
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        private static IFeature RouteFeatureOf(IComponent2 routeComp)
        {
            var f = routeComp.FirstFeature() as IFeature;
            while (f != null)
            {
                string t = "";
                try { t = f.GetTypeName2() ?? ""; } catch { }
                // "Route Parts" etc. are folders (FtrFolder); the route feature is "Route1".
                if (t != "FtrFolder" && t != "Reference"
                    && (t.IndexOf("route", StringComparison.OrdinalIgnoreCase) >= 0
                        || System.Text.RegularExpressions.Regex.IsMatch(f.Name ?? "", @"^Route\d+$")))
                    return f;
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        // Segments of the active (route) 3D sketch: each line / arc with its end points, total
        // length, and the open ends (end points used by one segment only).
        /// <summary>Current [x, y, z] (m, sketch = route coordinates) of every non-construction
        /// line / arc end of the sketch.</summary>
        private static List<double[]> SketchPointsOf(ISketch sk)
        {
            var list = new List<double[]>();
            void Add(ISketchPoint sp) { if (sp != null) list.Add(new[] { sp.X, sp.Y, sp.Z }); }
            if (!(sk?.GetSketchSegments() is object[] segs)) return list;
            foreach (var o in segs)
            {
                var seg = o as ISketchSegment;
                if (seg == null || seg.ConstructionGeometry) continue;
                if (seg is ISketchLine ln) { Add(ln.GetStartPoint2() as ISketchPoint); Add(ln.GetEndPoint2() as ISketchPoint); }
                else if (seg is ISketchArc arc) { Add(arc.GetStartPoint2() as ISketchPoint); Add(arc.GetEndPoint2() as ISketchPoint); }
            }
            return list;
        }

        // The corners a fillet removed: for every arc of the sketch, the point where its two
        // tangents meet (centre + bisector * r / cos(sweep / 2)). A bundle started there is drawn
        // but not connected to the route (its wires stay unrouted).
        private static List<double[]> FilletCornersOf(ISketch sk)
        {
            var list = new List<double[]>();
            if (!(sk?.GetSketchSegments() is object[] segs)) return list;
            foreach (var o in segs)
            {
                if (!(o is ISketchArc arc) || ((ISketchSegment)arc).ConstructionGeometry) continue;
                var s = arc.GetStartPoint2() as ISketchPoint;
                var e = arc.GetEndPoint2() as ISketchPoint;
                var c = arc.GetCenterPoint2() as ISketchPoint;
                if (s == null || e == null || c == null) continue;
                double[] a = { s.X - c.X, s.Y - c.Y, s.Z - c.Z }, b = { e.X - c.X, e.Y - c.Y, e.Z - c.Z };
                double r = Math.Sqrt(Dot(a, a));
                if (r < 1e-9) continue;
                double cos = Math.Max(-1.0, Math.Min(1.0, Dot(a, b) / (r * Math.Sqrt(Dot(b, b)))));
                double half = Math.Acos(cos) / 2;
                if (half < 1e-6 || half > Math.PI / 2 - 1e-3) continue;
                double[] bis = Unit(new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] });
                double d = r / Math.Cos(half);
                list.Add(new[] { c.X + bis[0] * d, c.Y + bis[1] * d, c.Z + bis[2] * d });
            }
            return list;
        }

        private static JObject RouteSketchGeometry(ISketch sk)
        {
            var o = new JObject();
            var segs = new JArray();
            var ends = new Dictionary<string, (double[] p, int n)>();
            double total = 0;
            void AddEnd(ISketchPoint sp)
            {
                if (sp == null) return;
                var pt = new[] { sp.X, sp.Y, sp.Z };
                string key = $"{Math.Round(pt[0], 6)},{Math.Round(pt[1], 6)},{Math.Round(pt[2], 6)}";
                ends[key] = ends.TryGetValue(key, out var e) ? (e.p, e.n + 1) : (pt, 1);
            }
            JArray P(ISketchPoint sp) => sp == null ? null : new JArray(Math.Round(sp.X, 6), Math.Round(sp.Y, 6), Math.Round(sp.Z, 6));
            if (sk?.GetSketchSegments() is object[] ss)
                foreach (var s in ss)
                {
                    var seg = s as ISketchSegment;
                    if (seg == null) continue;
                    bool constr = false;
                    try { constr = seg.ConstructionGeometry; } catch { }
                    double len = 0;
                    try { len = seg.GetLength(); } catch { }
                    var sj = new JObject { ["length_mm"] = Math.Round(len * 1000.0, 3) };
                    if (seg is ISketchLine ln)
                    {
                        sj["kind"] = "line";
                        sj["start"] = P(ln.GetStartPoint2() as ISketchPoint);
                        sj["end"] = P(ln.GetEndPoint2() as ISketchPoint);
                        if (!constr) { AddEnd(ln.GetStartPoint2() as ISketchPoint); AddEnd(ln.GetEndPoint2() as ISketchPoint); }
                    }
                    else if (seg is ISketchArc arc)
                    {
                        sj["kind"] = "arc";
                        sj["start"] = P(arc.GetStartPoint2() as ISketchPoint);
                        sj["end"] = P(arc.GetEndPoint2() as ISketchPoint);
                        try { sj["radius_mm"] = Math.Round(arc.GetRadius() * 1000.0, 3); } catch { }
                        if (!constr) { AddEnd(arc.GetStartPoint2() as ISketchPoint); AddEnd(arc.GetEndPoint2() as ISketchPoint); }
                    }
                    else sj["kind"] = "other";
                    if (constr) sj["construction"] = true;
                    else total += len;
                    segs.Add(sj);
                }
            var open = new JArray();
            foreach (var kv in ends)
                if (kv.Value.n == 1)
                    open.Add(new JArray(Math.Round(kv.Value.p[0], 6), Math.Round(kv.Value.p[1], 6), Math.Round(kv.Value.p[2], 6)));
            o["segments"] = segs;
            o["length_mm"] = Math.Round(total * 1000.0, 3);
            o["open_ends"] = open;
            return o;
        }

        // The cable exit: a planar face of the part's solid bodies whose outward normal is dir (within
        // ~8 deg), indexed like SelectFaceByIndexAppend / analyze_model(faces), within `depth` (m) of
        // the outermost such face: the one whose centre is nearest to near (projected), else the
        // largest.
        // Returns {face_index, point (on the face), height_mm, area_mm2, body_extent_beyond_mm,
        // candidates}.
        private static JObject FindExitFace(IModelDoc2 doc, double[] dir, double[] near, double depth = 0.001)
        {
            var part = doc as IPartDoc;
            var bodies = part?.GetBodies2(0 /* swSolidBody */, true) as object[];
            if (bodies == null) return null;
            var cands = new List<Tuple<int, double, double, double[]>>();   // index, height, area, centre
            double bodyMax = double.MinValue;
            int idx = 0;
            foreach (var b in bodies)
            {
                var body = b as IBody2;
                if (body == null) continue;
                if (body.GetBodyBox() is double[] bb && bb.Length >= 6)
                    for (int m = 0; m < 8; m++)
                    {
                        var c = new[] { (m & 1) == 0 ? bb[0] : bb[3], (m & 2) == 0 ? bb[1] : bb[4], (m & 4) == 0 ? bb[2] : bb[5] };
                        bodyMax = Math.Max(bodyMax, Dot(c, dir));
                    }
                if (!(body.GetFaces() is object[] fs)) continue;
                foreach (var fo in fs)
                {
                    var face = fo as IFace2;
                    int i = idx++;
                    if (face == null) continue;
                    try
                    {
                        var surf = face.IGetSurface();
                        if (surf == null || !surf.IsPlane()) continue;
                        if (!(face.Normal is double[] n) || n.Length < 3) continue;
                        if (Dot(Unit(n), dir) < 0.99) continue;
                        if (!(face.GetBox() is double[] fb) || fb.Length < 6) continue;
                        var centre = new[] { (fb[0] + fb[3]) / 2, (fb[1] + fb[4]) / 2, (fb[2] + fb[5]) / 2 };
                        var uv = face.GetUVBounds() as double[];
                        double h = Dot(centre, dir);
                        if (uv != null && uv.Length >= 4 && surf.Evaluate((uv[0] + uv[1]) / 2, (uv[2] + uv[3]) / 2, 0, 0) is double[] sp && sp.Length >= 3)
                            h = Dot(new[] { sp[0], sp[1], sp[2] }, dir);
                        cands.Add(Tuple.Create(i, h, face.GetArea(), centre));
                    }
                    catch { }
                }
            }
            if (cands.Count == 0) return null;
            // Within `depth` of the outermost: terminal tips or ribs often stand a fraction of a mm
            // proud of the housing's rear face (a 0.29 mm2 tip 0.2 mm beyond a 59 mm2 face).
            double top = cands.Max(c => c.Item2);
            var outer = cands.Where(c => c.Item2 > top - depth).ToList();
            Tuple<int, double, double, double[]> pick;
            if (near != null)
                pick = outer.OrderBy(c => { var d = Sub(c.Item4, near); var a = Sub(d, Scale(dir, Dot(d, dir))); return Dot(a, a); }).First();
            else pick = outer.OrderByDescending(c => c.Item3).First();
            var basePt = near ?? pick.Item4;
            var onFace = Add(basePt, Scale(dir, pick.Item2 - Dot(basePt, dir)));
            var cj = new JArray(cands.OrderByDescending(c => c.Item2).Take(5).Select(c => new JObject
            {
                ["face_index"] = c.Item1,
                ["height_mm"] = Math.Round(c.Item2 * 1000, 3),
                ["area_mm2"] = Math.Round(c.Item3 * 1e6, 2),
            }));
            return new JObject
            {
                ["face_index"] = pick.Item1,
                ["point"] = new JArray(Math.Round(onFace[0], 7), Math.Round(onFace[1], 7), Math.Round(onFace[2], 7)),
                ["height_mm"] = Math.Round(pick.Item2 * 1000, 3),
                ["area_mm2"] = Math.Round(pick.Item3 * 1e6, 2),
                ["body_extent_beyond_mm"] = Math.Round(Math.Max(0, bodyMax - pick.Item2) * 1000, 3),
                ["candidates"] = cj,
            };
        }

        public ExecutionResponse RoutePath(ToolRequest request)
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
                string addJson = p?.Value<string>("add_cpoints") ?? "[]";
                string pointsJson = p?.Value<string>("points") ?? "";
                double bendRadius = p?.Value<double?>("bend_radius") ?? 0.0;
                bool exit = p?.Value<bool?>("exit_route") ?? true;
                // dry_run: snap and check the points against the route as it is, report the lengths,
                // draw nothing. Checks that refuse a draw that would leave wires unrouted:
                // - an end that does not snap onto a route point but lies within free_end_tolerance
                //   of a corner a fillet removed (a branch started at such a corner, 3.9 mm off the
                //   arc), unless allow_free_ends;
                // - corners deflecting less than min_corner_deg are drawn sharp: with a fillet, a 4 deg
                //   corner split the route into disconnected segments.
                bool dryRun = p?.Value<bool?>("dry_run") ?? false;
                bool allowFree = p?.Value<bool?>("allow_free_ends") ?? false;
                double freeTol = p?.Value<double?>("free_end_tolerance") ?? 0.0005;
                double minCornerDeg = p?.Value<double?>("min_corner_deg") ?? 10.0;
                bool enteredHere = false;
                bool Given(string k) { string v = p?.Value<string>(k); return !string.IsNullOrWhiteSpace(v) && v.Trim() != "[]"; }
                if (dryRun && !Given("points") && !Given("paths") && !Given("fan_out"))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "dry_run needs points, paths or fan_out: the geometry to check.");

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = doc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "route_path needs the top assembly active.");
                var rm = asm.GetRouteManager() as IRouteManager;
                if (rm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ROUTING_NOT_LOADED", "No RouteManager: load the SOLIDWORKS Routing add-in.");

                var steps = new JArray();
                var warnings = new JArray();
                var routeComp = RouteBeingEdited(asm);
                ISketch dryRunSketch = null;
                if (routeComp == null && dryRun)
                {
                    // A dry run reads the route sketch through the component, without EditRoute:
                    // leaving route edit makes SOLIDWORKS rewrite the route's segmentList.xml.
                    routeComp = string.IsNullOrEmpty(routeName) ? null : FindComponentByName(asm, routeName);
                    if (routeComp == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "COMPONENT_NOT_FOUND", "Name the route sub-assembly in 'route' (routing_status lists them).");
                    dryRunSketch = RouteSketchOf(routeComp);
                    if (dryRunSketch == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_FAILED", $"No route sketch found in {ComponentPath(routeComp)}.");
                    steps.Add("dry run: route sketch read without editing the route");
                }
                else if (routeComp == null)
                {
                    routeComp = string.IsNullOrEmpty(routeName) ? null : FindComponentByName(asm, routeName);
                    if (routeComp == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "COMPONENT_NOT_FOUND", "No route is being edited; name the route sub-assembly in 'route' (routing_status lists them).");
                    var rf = RouteFeatureOf(routeComp);
                    if (rf == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FEATURE_NOT_FOUND", $"No route feature in {ComponentPath(routeComp)}.");
                    doc.ClearSelection2(true);
                    steps.Add($"select {rf.Name} ({rf.GetTypeName2()}): " + rf.Select2(false, 0));
                    rm.EditRoute();
                    steps.Add("EditRoute");
                    enteredHere = true;
                }
                else steps.Add("already editing " + ComponentPath(routeComp));
                var routeAsm = routeComp.GetModelDoc2() as IModelDoc2;

                // Connection points to add (each [component, cpoint]).
                JArray adds;
                try { adds = JArray.Parse(addJson); }
                catch (Exception ex)
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "add_cpoints must be a JSON list of [component, cpoint]: " + ex.Message);
                }
                if (dryRun && adds.Count > 0)
                {
                    warnings.Add("dry run: add_cpoints not applied");
                    adds = new JArray();
                }
                foreach (var a in adds)
                {
                    string cn = (string)a[0], cp = (string)a[1];
                    var c = FindComponentByName(asm, cn);
                    if (c == null) { warnings.Add("component not found: " + cn); continue; }
                    var f = c.FeatureByName(cp) as IFeature;
                    if (f == null) { warnings.Add($"{cp} is not a feature of {cn}"); continue; }
                    doc.ClearSelection2(true);
                    bool sel = f.Select2(false, 0);
                    bool added = rm.AddToRoute();
                    steps.Add($"AddToRoute {cn}/{cp}: selected={sel} added={added}");
                }
                doc.ClearSelection2(true);

                var sm = doc.SketchManager;
                var sk = dryRunSketch ?? sm.ActiveSketch as ISketch;
                steps.Add("active sketch: " + (sk != null ? ((sk as IFeature)?.Name ?? "(route sketch)") : "none"));

                double expected = -1;
                JArray drawnPaths = null;
                var fanReport = new JArray();
                double snapTol = p?.Value<double?>("snap_tolerance") ?? 0.0005;
                double mergeTol = p?.Value<double?>("merge_tolerance") ?? 1e-5;
                // The polylines of this call: 'points' (one bundle), each of 'paths'
                // [{name?, points, bend_radii | bend_radius}], and the fan-out legs of 'fan_out'
                // [{connector, join?, join_distance?, pins?}]: one straight leg from each used
                // cavity's stub end of a connector (one connection point per cavity) to a join node.
                // Fan-out legs are drawn first, so a branch can start on a join node. Every polyline
                // is checked before any is drawn; nothing is drawn when one fails.
                var jobs = new List<Tuple<string, List<double[]>, double[]>>();
                string pathsJson = p?.Value<string>("paths") ?? "";
                string fanJson = p?.Value<string>("fan_out") ?? "";
                bool hasPoints = !string.IsNullOrWhiteSpace(pointsJson) && pointsJson.Trim() != "[]";
                bool hasPaths = !string.IsNullOrWhiteSpace(pathsJson) && pathsJson.Trim() != "[]";
                bool hasFan = !string.IsNullOrWhiteSpace(fanJson) && fanJson.Trim() != "[]";
                if ((hasPoints || hasPaths || hasFan) && sk == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_FAILED", "The route sketch is not active; cannot draw the path.");
                string RadiiFor(string label, int nPts, string rJson, double rOne, out double[] rr)
                {
                    rr = new double[Math.Max(0, nPts - 2)];
                    for (int i = 0; i < rr.Length; i++) rr[i] = rOne;
                    if (!string.IsNullOrWhiteSpace(rJson) && rJson.Trim() != "[]")
                    {
                        JArray ra;
                        try { ra = JArray.Parse(rJson); } catch (Exception ex) { return label + ": bend_radii: " + ex.Message; }
                        if (ra.Count != rr.Length)
                            return $"{label}: bend_radii needs one radius per corner: {rr.Length} for {nPts} points.";
                        for (int i = 0; i < rr.Length; i++) rr[i] = (double)ra[i];
                    }
                    return null;
                }
                if (hasPoints)
                {
                    string perr = ParsePoints3D(pointsJson, out List<double[]> pts0);
                    if (perr != null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", perr);
                    string rerr = RadiiFor("points", pts0.Count, p?.Value<string>("bend_radii"), bendRadius, out double[] r0);
                    if (rerr != null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", rerr);
                    jobs.Add(Tuple.Create("points", pts0, r0));
                }
                if (hasPaths)
                {
                    JArray pa;
                    try { pa = JArray.Parse(pathsJson); }
                    catch (Exception ex)
                    {
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "paths must be a JSON list of {points, bend_radii | bend_radius}: " + ex.Message);
                    }
                    for (int k = 0; k < pa.Count; k++)
                    {
                        var po = pa[k] as JObject;
                        string label = po?.Value<string>("name") ?? $"paths[{k}]";
                        if (po?["points"] == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", label + ": no points.");
                        string perr = ParsePoints3D(po["points"].ToString(Newtonsoft.Json.Formatting.None), out List<double[]> pk);
                        if (perr != null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", label + ": " + perr);
                        string rerr = RadiiFor(label, pk.Count, po["bend_radii"]?.ToString(Newtonsoft.Json.Formatting.None),
                            po.Value<double?>("bend_radius") ?? bendRadius, out double[] rk);
                        if (rerr != null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", rerr);
                        jobs.Add(Tuple.Create(label, pk, rk));
                    }
                }
                if (hasFan)
                {
                    JArray fa;
                    try { fa = JArray.Parse(fanJson); }
                    catch (Exception ex)
                    {
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "fan_out must be a JSON list of {connector, join?, join_distance?, pins?}: " + ex.Message);
                    }
                    var routePts = SketchPointsOf(sk);
                    double onRoute = Math.Max(snapTol, 2e-5);
                    var fanJobs = new List<Tuple<string, List<double[]>, double[]>>();
                    foreach (var fo in fa.OfType<JObject>())
                    {
                        string cn = fo.Value<string>("connector") ?? "";
                        var c = FindComponentByName(asm, cn);
                        if (c == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COMPONENT_NOT_FOUND",
                                "fan_out: " + ComponentNotFoundMessage(asm, cn));
                        var cw = ConnectorPointsWorld(c);
                        var cps = cw?["connection_points"] as JArray;
                        if (cps == null || cps.Count == 0)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NO_CONNECTION_POINTS",
                                $"fan_out: {cn} has no connection points" + (cw?["error"] != null ? $" ({cw["error"]})" : "") + ".");
                        var wantPins = fo["pins"] is JArray wp ? new HashSet<string>(wp.Select(x => (string)x)) : null;
                        // Used cavities: those whose stub end is a point of the route (the from-to
                        // import draws a stub only for cavities that carry a wire), or the pins named.
                        var ends = new List<double[]>(); var dirs = new List<double[]>(); var pins = new JArray();
                        foreach (var cp in cps.OfType<JObject>())
                        {
                            if (!(cp["stub_end_mm"] is JArray se) || !(cp["direction"] is JArray dv)) continue;
                            var e = new[] { (double)se[0] / 1000, (double)se[1] / 1000, (double)se[2] / 1000 };
                            string pin = (string)cp["pin"];
                            if (string.IsNullOrEmpty(pin)) pin = (string)cp["name"];
                            double best = double.MaxValue; double[] bq = null;
                            foreach (var q in routePts) { double[] dd = Sub(q, e); double d = Math.Sqrt(Dot(dd, dd)); if (d < best) { best = d; bq = q; } }
                            bool use = wantPins != null ? wantPins.Contains(pin) : best <= onRoute;
                            if (!use) continue;
                            if (bq != null && best <= onRoute) e = bq;
                            else warnings.Add($"fan_out {cn} pin {pin}: its stub end is not on the route (nearest point {Math.Round(best * 1000, 3)} mm): the leg starts there anyway");
                            ends.Add(e); dirs.Add(new[] { (double)dv[0], (double)dv[1], (double)dv[2] }); pins.Add(pin);
                        }
                        if (ends.Count == 0)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NO_USED_CAVITIES",
                                $"fan_out: no stub of {cn} is on the route (import the from-to list first, or name the pins).");
                        double[] join;
                        if (fo["join"] is JArray jj && jj.Count == 3) join = new[] { (double)jj[0], (double)jj[1], (double)jj[2] };
                        else
                        {
                            double jd = fo.Value<double?>("join_distance") ?? 0.008;
                            double[] m = { ends.Average(q => q[0]), ends.Average(q => q[1]), ends.Average(q => q[2]) };
                            double[] dm = Unit(new[] { dirs.Average(q => q[0]), dirs.Average(q => q[1]), dirs.Average(q => q[2]) });
                            join = Add(m, Scale(dm, jd));
                        }
                        double longest = 0, steepest = 0;
                        for (int k = 0; k < ends.Count; k++)
                        {
                            double[] leg = Sub(join, ends[k]);
                            double len = Math.Sqrt(Dot(leg, leg));
                            if (len < 1e-6) continue; // the join is this stub end: no leg
                            longest = Math.Max(longest, len);
                            double ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(Unit(leg), Unit(dirs[k]))))) * 180 / Math.PI;
                            steepest = Math.Max(steepest, ang);
                            fanJobs.Add(Tuple.Create($"fan_out {cn} pin {pins[k]}", new List<double[]> { ends[k], join }, new double[0]));
                        }
                        fanReport.Add(new JObject
                        {
                            ["connector"] = ComponentPath(c),
                            ["pins"] = pins,
                            ["join"] = J3(join, 7),
                            ["join_mm"] = J3(Scale(join, 1000), 3),
                            ["longest_leg_mm"] = Math.Round(longest * 1000, 3),
                            ["max_leg_angle_deg"] = Math.Round(steepest, 1),
                        });
                    }
                    jobs.InsertRange(0, fanJobs);
                }

                if (jobs.Count > 0)
                {
                    // Snap both ends of each polyline onto the route as it is NOW (the route sketch is
                    // under-constrained: adding stubs or bundles lets the solver move earlier geometry
                    // by a few um, so a bundle drawn to the coordinates an earlier call returned no
                    // longer meets it; found 2026-11-12: a 7 um gap at a breakout), or onto the ends of
                    // the polylines before it in this call.
                    var existing = snapTol > 0 ? SketchPointsOf(sk) : new List<double[]>();
                    var filletCorners = snapTol > 0 ? FilletCornersOf(sk) : new List<double[]>();
                    var looseEnds = new List<string>();
                    foreach (var job in jobs)
                    {
                        var pts = job.Item2; var radii = job.Item3;
                        string pre = jobs.Count > 1 ? job.Item1 + ": " : "";
                        if (snapTol > 0)
                        {
                            foreach (int end in new[] { 0, pts.Count - 1 })
                            {
                                double best = double.MaxValue; double[] bp = null;
                                foreach (var q in existing)
                                {
                                    double dx = q[0] - pts[end][0], dy = q[1] - pts[end][1], dz = q[2] - pts[end][2];
                                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                    if (d < best) { best = d; bp = q; }
                                }
                                string which = end == 0 ? "start" : "end";
                                if (bp != null && best <= snapTol)
                                {
                                    if (best > 1e-9) steps.Add($"{pre}{which} snapped {Math.Round(best * 1e6, 1)} um onto an existing route point");
                                    else if (jobs.Count == 1) steps.Add($"{which} on an existing route point");
                                    pts[end] = new[] { bp[0], bp[1], bp[2] };
                                }
                                else
                                {
                                    steps.Add($"{pre}{which} is a new route point (nearest existing {(bp == null ? "none" : Math.Round(best * 1000, 3) + " mm")})");
                                    // Refuse only an end placed on a corner that a fillet removed: a new
                                    // node a few mm past a route point is a normal extension (a short
                                    // bundle between two breakouts).
                                    foreach (var fc in filletCorners)
                                    {
                                        double fx = fc[0] - pts[end][0], fy = fc[1] - pts[end][1], fz = fc[2] - pts[end][2];
                                        double fd = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                                        if (fd > freeTol) continue;
                                        looseEnds.Add($"{pre}{which} point {(end == 0 ? 0 : pts.Count - 1)} is the corner of a filleted bend "
                                            + $"({Math.Round(fd * 1000, 3)} mm), not on the route: the fillet removed that vertex "
                                            + $"(nearest route point {Math.Round(best * 1000, 3)} mm away at [{Math.Round(bp[0], 6)}, {Math.Round(bp[1], 6)}, {Math.Round(bp[2], 6)}])");
                                        break;
                                    }
                                }
                            }
                            existing.Add(pts[0]); existing.Add(pts[pts.Count - 1]);
                        }
                        for (int i = 1; i < pts.Count - 1; i++)
                        {
                            if (radii[i - 1] <= 0) continue;
                            double[] u0 = Unit(Sub(pts[i], pts[i - 1])), v0 = Unit(Sub(pts[i + 1], pts[i]));
                            double defl = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(u0, v0)))) * 180.0 / Math.PI;
                            if (defl < minCornerDeg)
                            {
                                warnings.Add($"{pre}corner {i} deflects {Math.Round(defl, 2)} deg (< min_corner_deg {minCornerDeg}): drawn sharp, radius {Math.Round(radii[i - 1] * 1000, 3)} mm ignored");
                                radii[i - 1] = 0;
                            }
                        }
                    }
                    if (looseEnds.Count > 0 && !allowFree)
                    {
                        if (enteredHere) { try { rm.ExitRoute(); asm.EditAssembly(); } catch { } }
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "END_NOT_ON_ROUTE",
                            "Nothing drawn: " + string.Join("; ", looseEnds) + ". Wires cannot cross a gap. A branch that starts at a corner of an earlier "
                            + "call misses it because the corner's fillet removed that vertex: put branch nodes on straight legs, at least "
                            + "r*tan(deflection/2) from a corner, or start on a point the route has (the sketch points route_path returns). "
                            + "allow_free_ends=true draws it anyway.");
                    }
                    if (dryRun)
                    {
                        if (enteredHere) { try { rm.ExitRoute(); asm.EditAssembly(); steps.Add("ExitRoute (dry run)"); } catch { } }
                        var dr = new JObject { ["route"] = ComponentPath(routeComp), ["dry_run"] = true };
                        if (jobs.Count == 1)
                        {
                            dr["points"] = new JArray(jobs[0].Item2.Select(q => new JArray(Math.Round(q[0], 6), Math.Round(q[1], 6), Math.Round(q[2], 6))));
                            dr["geometry"] = PolylineGeometry(jobs[0].Item2, jobs[0].Item3);
                        }
                        else
                        {
                            dr["paths"] = new JArray(jobs.Select(jb => new JObject
                            {
                                ["name"] = jb.Item1,
                                ["points"] = new JArray(jb.Item2.Select(q => new JArray(Math.Round(q[0], 6), Math.Round(q[1], 6), Math.Round(q[2], 6)))),
                                ["geometry"] = PolylineGeometry(jb.Item2, jb.Item3),
                            }));
                            dr["length_mm"] = Math.Round(jobs.Sum(jb => DrawFilletedPolyline(null, jb.Item2, jb.Item3, new JArray())) * 1000, 3);
                        }
                        if (fanReport.Count > 0) dr["fan_out"] = fanReport;
                        dr["steps"] = steps;
                        if (warnings.Count > 0) dr["warnings"] = warnings;
                        return ReadOnlyResponse(request, doc, dr);
                    }
                    bool prevDb = sm.AddToDB;
                    int[] toggles = { 249, 9, 266, 271, 270 };
                    var prev = new bool[toggles.Length];
                    for (int ti = 0; ti < toggles.Length; ti++)
                    { prev[ti] = _solidWorks.GetUserPreferenceToggle(toggles[ti]); _solidWorks.SetUserPreferenceToggle(toggles[ti], false); }
                    sm.AddToDB = true;
                    var drawn = new JArray();
                    try
                    {
                        expected = 0;
                        foreach (var job in jobs)
                        {
                            double len = DrawFilletedPolyline(sm, job.Item2, job.Item3, warnings);
                            expected += len;
                            if (jobs.Count > 1) drawn.Add(new JObject { ["name"] = job.Item1, ["points"] = job.Item2.Count, ["length_mm"] = Math.Round(len * 1000, 3) });
                            // Merge after each polyline so that the next one's snapped ends join it.
                            if (jobs.Count > 1 && mergeTol > 0) { try { sk.MergePoints(mergeTol); } catch { } }
                        }
                    }
                    finally
                    {
                        sm.AddToDB = prevDb;
                        for (int ti = 0; ti < toggles.Length; ti++) _solidWorks.SetUserPreferenceToggle(toggles[ti], prev[ti]);
                    }
                    steps.Add(jobs.Count == 1 ? $"drew {jobs[0].Item2.Count} points" : $"drew {jobs.Count} polylines");
                    if (drawn.Count > 0) drawnPaths = drawn;
                }
                // Merge coincident points (consecutive segments, bundle ends at breakouts, stub ends),
                // also on a call without points: route_path(points='[]') repairs a gap.
                if (sk != null && mergeTol > 0)
                {
                    try { steps.Add($"MergePoints({mergeTol}): " + sk.MergePoints(mergeTol)); }
                    catch (Exception ex) { warnings.Add("MergePoints: " + ex.Message); }
                }

                var result = new JObject { ["route"] = ComponentPath(routeComp) };
                var tf = TransformJson(routeComp);
                if (tf != null) result["route_transform"] = tf;
                if (sk != null) result["sketch"] = RouteSketchGeometry(sk);
                if (expected >= 0) result["drawn_length_mm"] = Math.Round(expected * 1000.0, 3);
                if (drawnPaths != null) result["paths"] = drawnPaths;
                if (fanReport.Count > 0) result["fan_out"] = fanReport;

                if (exit)
                {
                    try { rm.ExitRoute(); steps.Add("ExitRoute"); } catch (Exception ex) { steps.Add("ExitRoute failed: " + ex.Message); }
                    try { asm.EditAssembly(); steps.Add("EditAssembly"); } catch (Exception ex) { steps.Add("EditAssembly failed: " + ex.Message); }
                    try { doc.EditRebuild3(); } catch { }
                    // Wires (from a from-to import) follow the new geometry: route them, report them.
                    JObject wirePaths = ParseWirePaths(p);
                    bool doRoute = p?.Value<bool?>("route_wires") ?? true;
                    if (doRoute || wirePaths != null)
                    {
                        RouteWiresOn(doc, asm, routeComp, steps, warnings, wirePaths, doRoute);
                        var er = ReadElectricalRoute(doc, asm, routeComp);
                        if (er["wires"] is JArray w && w.Count > 0) result["wires"] = w;
                        if (er["segments"] != null) result["segments"] = er["segments"];
                    }
                }
                result["steps"] = steps;
                if (warnings.Count > 0) result["warnings"] = warnings;

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

        // import_from_to: wires from a from-to list into the routes of the active assembly.
        // references: {component instance: reference designator} set first (Routing matches the
        // list's From/To Ref to IComponent2.ComponentReference). Accepts the PropertyManager pages
        // the import opens (Route Properties, Auto Route) with up to press_ok RunCommand(PmOK),
        // optionally RouteWires on the named route, and reports its wires with cut lengths.
        public ExecutionResponse ImportFromTo(ToolRequest request)
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
                string fromTo = p?.Value<string>("from_to") ?? "";
                string compLib = p?.Value<string>("component_library") ?? "";
                string cableLib = p?.Value<string>("cable_library") ?? "";
                bool useExisting = p?.Value<bool?>("use_existing_assembly") ?? true;
                bool overwrite = p?.Value<bool?>("overwrite") ?? true;
                bool searchSubs = p?.Value<bool?>("search_sub_assemblies") ?? true;
                int pressOk = p?.Value<int?>("press_ok") ?? 3;
                string routeName = p?.Value<string>("route") ?? "";
                bool routeWires = p?.Value<bool?>("route_wires") ?? true;
                string refsJson = p?.Value<string>("references") ?? "{}";
                // replaces: the route sub-assembly this import replaces (deleted before the import,
                // its Harness_<n> files after); configurations: the configurations of the active
                // assembly that show the new route (the first is active during the import); it is
                // suppressed in all the others (a component added in one configuration shows in all).
                string replaces = p?.Value<string>("replaces") ?? "";
                var showIn = ParseNameList(p?["configurations"]);

                foreach (var f in new[] { fromTo, compLib, cableLib })
                    if (!string.IsNullOrEmpty(f) && !System.IO.File.Exists(f))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FILE_NOT_FOUND", "Not found: " + f);
                if (string.IsNullOrEmpty(fromTo))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "from_to (the from-to list .xlsx) is required.");

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = doc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "import_from_to needs the top assembly active.");
                var rm = asm.GetRouteManager() as IRouteManager;
                if (rm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ROUTING_NOT_LOADED", "No RouteManager: load the SOLIDWORKS Routing add-in.");

                var steps = new JArray();
                var warnings = new JArray();
                string[] cfgNames = (doc.GetConfigurationNames() as object[])?.Select(o => o.ToString()).ToArray() ?? new string[0];
                foreach (var cn in showIn)
                    if (!cfgNames.Contains(cn, StringComparer.OrdinalIgnoreCase))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "CONFIGURATION_NOT_FOUND",
                            $"'{cn}' is not a configuration of {doc.GetTitle()} ({string.Join(", ", cfgNames)}).");
                if (showIn.Count > 0)
                {
                    string act = (doc.GetActiveConfiguration() as IConfiguration)?.Name;
                    if (!string.Equals(act, showIn[0], StringComparison.OrdinalIgnoreCase))
                        steps.Add($"activate configuration {showIn[0]}: " + doc.ShowConfiguration2(showIn[0]));
                }
                var oldFiles = new List<string>();
                if (!string.IsNullOrEmpty(replaces))
                {
                    var old = FindComponentByName(asm, replaces);
                    if (old == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COMPONENT_NOT_FOUND",
                            "replaces: " + ComponentNotFoundMessage(asm, replaces));
                    bool oldIsRoute = false;
                    try { oldIsRoute = (old.GetModelDoc2() as IAssemblyDoc)?.IsRouteAssembly() ?? false; } catch { }
                    if (!oldIsRoute)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "WRONG_COMPONENT_TYPE",
                            $"replaces: {ComponentPath(old)} is not a route sub-assembly (or it is suppressed / lightweight: resolve it first).");
                    string op = old.GetPathName();
                    if (!string.IsNullOrEmpty(op))
                    {
                        string b0 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(op), System.IO.Path.GetFileNameWithoutExtension(op));
                        oldFiles.AddRange(new[] { op, b0 + "-fromToList.xml", b0 + "-segmentList.xml" });
                    }
                    string oldPath = ComponentPath(old);
                    doc.ClearSelection2(true);
                    bool sel = old.Select4(false, null, false);
                    bool del = sel && doc.Extension.DeleteSelection2(0);
                    doc.ClearSelection2(true);
                    steps.Add($"delete replaced route {oldPath}: {del}");
                    if (!del)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "DELETE_FAILED",
                            $"Could not delete the replaced route {oldPath} (nothing imported).");
                }
                var refs = JObject.Parse(refsJson);
                foreach (var kv in refs)
                {
                    var c = FindComponentByName(asm, kv.Key);
                    if (c == null) { warnings.Add("component not found: " + kv.Key); continue; }
                    c.ComponentReference = (string)kv.Value;
                    steps.Add($"{ComponentPath(c)} reference = {c.ComponentReference}");
                }

                // The import (GUI and API) refuses to run while any from-to header definition is
                // empty ("Some header definition(s) are not specified", found 2026-11-11; the API
                // just returns false). Fill only the empty ones; names already set are kept.
                try
                {
                    var rs = _solidWorks.GetRoutingSettings() as IRoutingSettings;
                    if (rs != null && rs.GetFromToListHeaderDefinitions(out string hw, out string hfr, out string hfp, out string hfpn,
                            out string htr, out string htp, out string htpn, out string hcab, out string hcore, out string hcol,
                            out string hspec, out string hoth))
                    {
                        string Fill(string v, string d) => string.IsNullOrWhiteSpace(v) ? d : v;
                        var filled = new JArray();
                        void Note(string v, string name) { if (string.IsNullOrWhiteSpace(v)) filled.Add(name); }
                        Note(hw, "wire"); Note(hfr, "from ref"); Note(hfp, "pin"); Note(hfpn, "partno"); Note(htr, "to ref");
                        Note(htp, "pin"); Note(htpn, "partno"); Note(hcab, "cable"); Note(hcore, "core"); Note(hcol, "color");
                        // "other" is not required by the API import and SetFromToListHeaderDefinitions
                        // does not keep it (it reads back empty, 2026-11-12): don't count it.
                        Note(hspec, "spec");
                        if (filled.Count > 0)
                        {
                            bool set = rs.SetFromToListHeaderDefinitions(Fill(hw, "wire"), Fill(hfr, "from ref"), Fill(hfp, "pin"),
                                Fill(hfpn, "partno"), Fill(htr, "to ref"), Fill(htp, "pin"), Fill(htpn, "partno"), Fill(hcab, "cable"),
                                Fill(hcore, "core"), Fill(hcol, "color"), Fill(hspec, "spec"), Fill(hoth, "other"));
                            // Read back: report what is still empty instead of trusting the call.
                            var still = new JArray();
                            if (rs.GetFromToListHeaderDefinitions(out hw, out hfr, out hfp, out hfpn, out htr, out htp, out htpn,
                                    out hcab, out hcore, out hcol, out hspec, out hoth))
                            {
                                void Still(string v, string name) { if (string.IsNullOrWhiteSpace(v)) still.Add(name); }
                                Still(hw, "wire"); Still(hfr, "from ref"); Still(hfp, "pin"); Still(hfpn, "partno"); Still(htr, "to ref");
                                Still(htp, "pin"); Still(htpn, "partno"); Still(hcab, "cable"); Still(hcore, "core"); Still(hcol, "color");
                                Still(hspec, "spec");
                            }
                            steps.Add($"filled empty from-to header definitions ({string.Join(", ", filled)}): {set}"
                                + (still.Count > 0 ? $"; still empty: {string.Join(", ", still)}" : ""));
                            if (still.Count > 0)
                                warnings.Add("from-to header definitions still empty (the import will fail): " + string.Join(", ", still));
                        }
                    }
                }
                catch (Exception ex) { warnings.Add("from-to header definitions: " + ex.Message); }

                bool editRouteFirst = p?.Value<bool?>("edit_route_first") ?? false;
                doc.ClearSelection2(true);
                if (editRouteFirst && !string.IsNullOrEmpty(routeName))
                {
                    var rc = FindComponentByName(asm, routeName);
                    var rf = rc != null ? RouteFeatureOf(rc) : null;
                    if (rf == null) warnings.Add("edit_route_first: route feature not found");
                    else
                    {
                        steps.Add($"select {rf.Name}: " + rf.Select2(false, 0));
                        rm.EditRoute();
                        steps.Add("EditRoute");
                        doc.ClearSelection2(true);
                    }
                }
                bool convertGuidelines = p?.Value<bool?>("convert_guidelines") ?? false;
                var routesBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rc in RouteComponents(asm)) routesBefore.Add(ComponentPath(rc));

                bool imported = rm.ImportFromToList(fromTo, compLib, cableLib, useExisting, overwrite, searchSubs);
                steps.Add("ImportFromToList: " + imported);
                if (!imported)
                    warnings.Add("ImportFromToList returned false: check the from-to header definitions " +
                        "(routing_status from_to_headers), the file paths, and that every Spec is a WireName " +
                        "of the cable library and every From/To Ref a component reference in the assembly.");
                for (int i = 0; imported && i < pressOk; i++)
                {
                    bool ok = _solidWorks.RunCommand(-2 /* swCommands_PmOK */, "");
                    steps.Add($"RunCommand(PmOK) #{i + 1}: {ok}");
                    if (!ok) break;
                }
                try { steps.Add("edit_target: " + (asm.GetEditTarget() as IModelDoc2)?.GetTitle()); } catch { }

                var result = new JObject { ["imported"] = imported };
                // The import builds a new route sub-assembly (Harness_<n>) even with existing
                // connectors; prefer it over the one named in 'route'.
                IComponent2 routeComp = null;
                foreach (var rc in RouteComponents(asm))
                    if (!routesBefore.Contains(ComponentPath(rc))) { routeComp = rc; result["new_route"] = ComponentPath(rc); break; }
                if (routeComp == null)
                    routeComp = string.IsNullOrEmpty(routeName) ? RouteBeingEdited(asm) : FindComponentByName(asm, routeName);
                // Leave any route / component editing before reading.
                try { rm.ExitRoute(); } catch { }
                try { if (asm.GetEditTarget() as IModelDoc2 != doc) asm.EditAssembly(); } catch { }

                if (routeComp != null && convertGuidelines)
                    ConvertGuidelinesOn(doc, asm, rm, routeComp, steps, warnings);

                if (routeComp != null)
                {
                    if (routeWires) RouteWiresOn(doc, asm, routeComp, steps, warnings);
                    result["route"] = ReadElectricalRoute(doc, asm, routeComp);
                }
                var routes = new JArray();
                if (asm.GetComponents(false) is object[] all)
                    foreach (var c in all)
                    {
                        var cc = c as IComponent2;
                        var sub = cc?.GetModelDoc2() as IAssemblyDoc;
                        bool isRoute = false;
                        try { isRoute = sub != null && sub.IsRouteAssembly(); } catch { }
                        if (isRoute) routes.Add(ComponentPath(cc));
                    }
                result["route_assemblies"] = routes;

                // The new route only in its configurations.
                if (showIn.Count > 0 && routeComp != null && result["new_route"] != null)
                {
                    string newPath = ComponentPath(routeComp);
                    var hidden = new JArray();
                    foreach (var cn in cfgNames.Where(c => !showIn.Contains(c, StringComparer.OrdinalIgnoreCase)))
                    {
                        doc.ShowConfiguration2(cn);
                        var rc = FindComponentByName(asm, newPath);
                        int rcode = rc == null ? -1 : rc.SetSuppression2(0);
                        if (rcode == 2) hidden.Add(cn); else warnings.Add($"could not suppress {newPath} in configuration {cn} (code {rcode})");
                    }
                    doc.ShowConfiguration2(showIn[0]);
                    result["suppressed_in"] = hidden;
                    result["active_configuration"] = showIn[0];
                }
                result["steps"] = steps;
                // Rebuild what is out of date only: ForceRebuild3 marked every loaded reference of a
                // large assembly modified, and the next save rewrote all of them.
                try { doc.EditRebuild3(); } catch { }
                if (oldFiles.Count > 0)
                {
                    var gone = new JArray();
                    foreach (var f in oldFiles.Where(System.IO.File.Exists))
                    {
                        try { System.IO.File.Delete(f); gone.Add(System.IO.Path.GetFileName(f)); }
                        catch (Exception ex) { warnings.Add($"replaced route file not deleted ({ex.Message}): {f}"); }
                    }
                    result["deleted_files"] = gone;
                }
                if (warnings.Count > 0) result["warnings"] = warnings;

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

        // route_guidelines: after a from-to import, the connector pairs to wire show as guidelines;
        // turn them into route geometry (IAutoRoute: ShowGuidelines, SelectGuidelines,
        // ConvertGuidelinesToRoute) while the route is edited (entered here when 'route' is given
        // and no route is being edited), and report the route sketch.
        public ExecutionResponse RouteGuidelines(ToolRequest request)
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
                bool exit = p?.Value<bool?>("exit_route") ?? true;

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = doc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "route_guidelines needs the top assembly active.");
                var rm = asm.GetRouteManager() as IRouteManager;
                if (rm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ROUTING_NOT_LOADED", "No RouteManager: load the SOLIDWORKS Routing add-in.");

                var steps = new JArray();
                var routeComp = RouteBeingEdited(asm);
                if (routeComp == null)
                {
                    routeComp = string.IsNullOrEmpty(routeName) ? null : FindComponentByName(asm, routeName);
                    var rf = routeComp != null ? RouteFeatureOf(routeComp) : null;
                    if (rf == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "COMPONENT_NOT_FOUND", "No route is being edited; name the route sub-assembly in 'route'.");
                    doc.ClearSelection2(true);
                    steps.Add($"select {rf.Name}: " + rf.Select2(false, 0));
                    rm.EditRoute();
                    steps.Add("EditRoute");
                }
                else steps.Add("already editing " + ComponentPath(routeComp));

                var ar = rm.GetAutoRoute() as IAutoRoute;
                if (ar == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "AUTOROUTE_UNAVAILABLE", "GetAutoRoute returned nothing (is a route being edited?).");
                steps.Add("ShowGuidelines: " + ar.ShowGuidelines());
                doc.ClearSelection2(true);
                steps.Add("SelectGuidelines: " + ar.SelectGuidelines());
                try { steps.Add("selected objects: " + (doc.SelectionManager as ISelectionMgr).GetSelectedObjectCount2(-1)); } catch { }
                steps.Add("ConvertGuidelinesToRoute: " + ar.ConvertGuidelinesToRoute());
                doc.ClearSelection2(true);

                var result = new JObject { ["route"] = ComponentPath(routeComp) };
                var sk = doc.SketchManager.ActiveSketch as ISketch;
                if (sk != null) result["sketch"] = RouteSketchGeometry(sk);
                if (exit)
                {
                    try { rm.ExitRoute(); steps.Add("ExitRoute"); } catch (Exception ex) { steps.Add("ExitRoute failed: " + ex.Message); }
                    try { asm.EditAssembly(); steps.Add("EditAssembly"); } catch (Exception ex) { steps.Add("EditAssembly failed: " + ex.Message); }
                    try { doc.ForceRebuild3(false); } catch { }
                }
                result["steps"] = steps;

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

        public ExecutionResponse StartRoute(ToolRequest request)
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
                string compName = p?.Value<string>("component") ?? "";
                string cpName = p?.Value<string>("cpoint") ?? "";
                bool pressOk = p?.Value<bool?>("press_ok") ?? true;
                bool exitRoute = p?.Value<bool?>("exit_route") ?? true;

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = doc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "start_route needs an active assembly.");
                var rm = asm.GetRouteManager() as IRouteManager;
                if (rm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ROUTING_NOT_LOADED", "No RouteManager: load the SOLIDWORKS Routing add-in (Tools > Add-Ins).");
                var comp = FindComponentByName(asm, compName);
                if (comp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COMPONENT_NOT_FOUND", ComponentNotFoundMessage(asm, compName));

                var result = new JObject();
                var steps = new JArray();
                doc.ClearSelection2(true);
                bool selected;
                if (!string.IsNullOrEmpty(cpName))
                {
                    var f = comp.FeatureByName(cpName) as IFeature;
                    if (f == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FEATURE_NOT_FOUND", $"'{cpName}' is not a feature of {ComponentPath(comp)}.");
                    selected = f.Select2(false, 0);
                    steps.Add("selected cpoint " + cpName + ": " + selected);
                }
                else
                {
                    selected = comp.Select4(false, null, false);
                    steps.Add("selected component: " + selected);
                }

                bool started = rm.StartRoute("", "");
                steps.Add("StartRoute: " + started);
                if (pressOk)
                {
                    bool r1 = _solidWorks.RunCommand(-2 /* swCommands_PmOK */, "");
                    steps.Add("RunCommand(PmOK): " + r1);
                }
                try { steps.Add("edit_target after start: " + (asm.GetEditTarget() as IModelDoc2)?.GetTitle()); } catch { }
                try
                {
                    var ask = doc.SketchManager.ActiveSketch as ISketch;
                    steps.Add("active sketch: " + (ask != null ? ((ask as IFeature)?.Name ?? "(unnamed)") : "none"));
                }
                catch { }
                if (exitRoute)
                {
                    try { rm.ExitRoute(); steps.Add("ExitRoute"); } catch (Exception ex) { steps.Add("ExitRoute failed: " + ex.Message); }
                    try { asm.EditAssembly(); steps.Add("EditAssembly"); } catch (Exception ex) { steps.Add("EditAssembly failed: " + ex.Message); }
                }
                result["steps"] = steps;

                var routes = new JArray();
                if (asm.GetComponents(false) is object[] all)
                    foreach (var c in all)
                    {
                        var cc = c as IComponent2;
                        var sub = cc?.GetModelDoc2() as IAssemblyDoc;
                        bool isRoute = false;
                        try { isRoute = sub != null && sub.IsRouteAssembly(); } catch { }
                        if (isRoute) routes.Add(new JObject { ["component"] = ComponentPath(cc), ["file"] = cc.GetPathName() });
                    }
                result["route_assemblies"] = routes;

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
