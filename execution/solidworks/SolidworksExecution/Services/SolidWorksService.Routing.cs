using System;
using System.Collections.Generic;
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

        private JObject RoutingAddinInfo()
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
                var result = new JObject { ["routing"] = RoutingAddinInfo() };
                if (doc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "routing_status needs an open part or assembly.");
                result["document"] = doc.GetTitle();

                var asm = doc as IAssemblyDoc;
                if (asm == null)
                {
                    result["routing_points"] = RoutingFeaturesOf(doc);
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

                if (!string.IsNullOrEmpty(routeName))
                {
                    var comp = FindComponentByName(asm, routeName);
                    if (comp == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "COMPONENT_NOT_FOUND", ComponentNotFoundMessage(asm, routeName));
                    result["route"] = ReadElectricalRoute(doc, asm, comp);
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

                var result = new JObject { ["routing"] = RoutingAddinInfo() };
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
                    if (pts.Count != 1)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "point must be one JSON [x, y, z] in meters (part coordinates).");
                    if (kind == "connection" && faceIndex < 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "a connection point needs face_index: the face the cable leaves through (analyze_model(faces)).");

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
        private void RouteWiresOn(IModelDoc2 doc, IAssemblyDoc asm, IComponent2 routeComp, JArray steps, JArray warnings)
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
                else steps.Add("RouteWires: " + er.RouteWires());
            }
            finally { try { asm.EditAssembly(); } catch { } doc.ClearSelection2(true); }
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
                if (routeComp == null)
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
                var sk = sm.ActiveSketch as ISketch;
                steps.Add("active sketch: " + (sk != null ? ((sk as IFeature)?.Name ?? "(route sketch)") : "none"));

                double expected = -1;
                if (!string.IsNullOrWhiteSpace(pointsJson) && pointsJson.Trim() != "[]")
                {
                    if (sk == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_FAILED", "The route sketch is not active; cannot draw the path.");
                    List<double[]> pts;
                    string perr = ParsePoints3D(pointsJson, out pts);
                    if (perr != null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", perr);
                    var radii = new double[Math.Max(0, pts.Count - 2)];
                    for (int i = 0; i < radii.Length; i++) radii[i] = bendRadius;
                    string radiiJson = p?.Value<string>("bend_radii");
                    if (!string.IsNullOrWhiteSpace(radiiJson) && radiiJson.Trim() != "[]")
                    {
                        var ra = JArray.Parse(radiiJson);
                        if (ra.Count != radii.Length)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "INVALID_PARAMETER", $"bend_radii needs one radius per corner: {radii.Length} for {pts.Count} points.");
                        for (int i = 0; i < radii.Length; i++) radii[i] = (double)ra[i];
                    }
                    bool prevDb = sm.AddToDB;
                    int[] toggles = { 249, 9, 266, 271, 270 };
                    var prev = new bool[toggles.Length];
                    for (int ti = 0; ti < toggles.Length; ti++)
                    { prev[ti] = _solidWorks.GetUserPreferenceToggle(toggles[ti]); _solidWorks.SetUserPreferenceToggle(toggles[ti], false); }
                    sm.AddToDB = true;
                    try
                    {
                        expected = DrawFilletedPolyline(sm, pts, radii, warnings);
                        try { sk.MergePoints(1e-6); } catch (Exception ex) { warnings.Add("MergePoints: " + ex.Message); }
                    }
                    finally
                    {
                        sm.AddToDB = prevDb;
                        for (int ti = 0; ti < toggles.Length; ti++) _solidWorks.SetUserPreferenceToggle(toggles[ti], prev[ti]);
                    }
                    steps.Add($"drew {pts.Count} points");
                }

                var result = new JObject { ["route"] = ComponentPath(routeComp) };
                var tf = TransformJson(routeComp);
                if (tf != null) result["route_transform"] = tf;
                if (sk != null) result["sketch"] = RouteSketchGeometry(sk);
                if (expected >= 0) result["drawn_length_mm"] = Math.Round(expected * 1000.0, 3);

                if (exit)
                {
                    try { rm.ExitRoute(); steps.Add("ExitRoute"); } catch (Exception ex) { steps.Add("ExitRoute failed: " + ex.Message); }
                    try { asm.EditAssembly(); steps.Add("EditAssembly"); } catch (Exception ex) { steps.Add("EditAssembly failed: " + ex.Message); }
                    try { doc.ForceRebuild3(false); } catch { }
                    // Wires (from a from-to import) follow the new geometry: route them, report them.
                    if (p?.Value<bool?>("route_wires") ?? true)
                    {
                        RouteWiresOn(doc, asm, routeComp, steps, warnings);
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
                        Note(hspec, "spec"); Note(hoth, "other");
                        if (filled.Count > 0)
                        {
                            bool set = rs.SetFromToListHeaderDefinitions(Fill(hw, "wire"), Fill(hfr, "from ref"), Fill(hfp, "pin"),
                                Fill(hfpn, "partno"), Fill(htr, "to ref"), Fill(htp, "pin"), Fill(htpn, "partno"), Fill(hcab, "cable"),
                                Fill(hcore, "core"), Fill(hcol, "color"), Fill(hspec, "spec"), Fill(hoth, "other"));
                            steps.Add($"filled empty from-to header definitions ({string.Join(", ", filled)}): {set}");
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
                result["steps"] = steps;
                if (warnings.Count > 0) result["warnings"] = warnings;
                try { doc.ForceRebuild3(false); } catch { }

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
