using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.SWRoutingLib;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // Electrical harness 2D outputs: flatten_route (IRouteManager.CreateFlattenRoute without its own
    // drawing) and route_drawing (a drawing of the flattened configuration built here: the flat view,
    // a circuit summary table, one connector table and a label per connector).
    //
    // Why not CreateFlattenRoute(drawingOption=true): SOLIDWORKS creates the drawing and its view, then
    // throws (RPC_E_SERVERFAULT) before the first table when the route's connectors are components of
    // the PARENT assembly (the usual SOLIDWORKS Routing layout); its connector blocks then also read
    // "External Connector". The flatten itself (drawingOption=false) works: a configuration
    // "<Type>FlattenedRoute<n> - <config>" in the route assembly with a planar 3D sketch (Z = 0) and a
    // FlattenedRoute feature; the 3D route stays in the original configuration.
    public partial class SolidWorksService
    {
        private ExecutionResponse ChangedResponse(ToolRequest request, IModelDoc2 doc, string docType, JObject result)
        {
            var response = new ExecutionResponse
            {
                OperationId = request.OperationId,
                Status = "COMPLETED",
                Verified = true,
                StateVersion = _guard.GetCurrentStateVersion() + 1,
                CadState = new CadState
                {
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    ActiveDocument = doc?.GetTitle(),
                    DocumentType = docType,
                    ActiveSketch = null,
                    Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                    Dimensions = new List<string>()
                },
                Error = null
            };
            _guard.RegisterCompleted(request.OperationId, response);
            return response;
        }

        // Ends of a line, arc or spline (model coordinates of the sketch: a 3D sketch is in model space).
        private static double[][] FlatEnds(ISketchSegment seg)
        {
            double[] P(ISketchPoint sp) => sp == null ? null : new[] { sp.X, sp.Y, sp.Z };
            try
            {
                if (seg is ISketchLine ln) return new[] { P(ln.GetStartPoint2() as ISketchPoint), P(ln.GetEndPoint2() as ISketchPoint) };
                if (seg is ISketchArc ar) return new[] { P(ar.GetStartPoint2() as ISketchPoint), P(ar.GetEndPoint2() as ISketchPoint) };
                if (seg is ISketchSpline sp && sp.GetPoints2() is object[] pts && pts.Length >= 2)
                    return new[] { P(pts[0] as ISketchPoint), P(pts[pts.Length - 1] as ISketchPoint) };
            }
            catch { }
            return null;
        }

        private static List<ISketchSegment> SegList(object o)
        {
            var l = new List<ISketchSegment>();
            if (o is object[] a) foreach (var x in a) if (x is ISketchSegment s) l.Add(s);
            return l;
        }

        private static double SegLen(IEnumerable<ISketchSegment> segs)
        {
            double t = 0;
            foreach (var s in segs) try { t += s.GetLength(); } catch { }
            return t;
        }

        private static List<string> FlattenedRouteFeatures(IModelDoc2 routeDoc)
        {
            var names = new List<string>();
            var f = routeDoc.FirstFeature() as IFeature;
            while (f != null)
            {
                try { if (f.GetTypeName2() == "FlattenedRoute") names.Add(f.Name); } catch { }
                f = f.GetNextFeature() as IFeature;
            }
            return names;
        }

        // The configuration of the route assembly that holds a flattened route feature.
        private static string FlatConfigOf(IModelDoc2 routeDoc, string flatFeature)
        {
            if (routeDoc.GetConfigurationNames() is object[] cn)
                foreach (var c in cn.Select(x => x as string).Where(x => x != null))
                    if (c.StartsWith(flatFeature + " ", StringComparison.OrdinalIgnoreCase) || c == flatFeature) return c;
            return null;
        }

        // Route segment ids and the 3D sketch segments of each (route assembly coordinates), read while
        // the route is edited in context. Also wires (raw objects stay valid only during the edit:
        // their data is copied out).
        private class RouteWireData
        {
            public string Name, FromRef, FromPin, ToRef, ToPin, Spec;
            public double CutMm;
            public int Color;
            public int[] SegIds = new int[0];
        }

        private void ReadRouteForFlat(IModelDoc2 top, IAssemblyDoc topAsm, IComponent2 routeComp,
            out List<RouteWireData> wires, out Dictionary<int, List<double[]>> segEnds3d, out Dictionary<int, double> segLen3d)
        {
            wires = new List<RouteWireData>();
            segEnds3d = new Dictionary<int, List<double[]>>();
            segLen3d = new Dictionary<int, double>();
            var routeAsm = routeComp.GetModelDoc2() as IAssemblyDoc;
            var rm = routeAsm?.GetRouteManager() as IRouteManager;
            if (rm == null) throw new InvalidOperationException("no RouteManager for the route (Routing add-in not loaded?)");
            top.ClearSelection2(true);
            routeComp.Select4(false, null, false);
            topAsm.EditAssembly();
            try
            {
                var er = rm.GetElectricalRoute() as IElectricalRoute;
                if (er == null) throw new InvalidOperationException("no electrical route in this route assembly");
                if (er.GetWires() is object[] ws)
                    foreach (var w in ws.OfType<IWire>())
                    {
                        var d = new RouteWireData
                        {
                            Name = SafeStr(() => w.UserName),
                            FromRef = SafeStr(() => w.FromComponentReference),
                            FromPin = SafeStr(() => w.FromPinReference),
                            ToRef = SafeStr(() => w.ToComponentReference),
                            ToPin = SafeStr(() => w.ToPinReference),
                        };
                        try { d.CutMm = Math.Round(w.GetCuttingLength() * 1000.0, 2); } catch { }
                        try { d.SegIds = SegmentIdsOf(w); } catch { }
                        try
                        {
                            object wpo = w.GetWireProperty();
                            if (wpo is IWireProperty wp) { d.Spec = wp.Name; d.Color = wp.Color; }
                        }
                        catch { }
                        wires.Add(d);
                    }
                object idsObj = rm.GetAllRouteSegmentIDs();
                var ids = new List<int>();
                if (idsObj is int[] ia) ids.AddRange(ia);
                else if (idsObj is object[] oa) foreach (var x in oa) ids.Add(Convert.ToInt32(x));
                foreach (int id in ids)
                {
                    var segs = SegList((object)er.GetSketchSegments(id));
                    var ends = new List<double[]>();
                    foreach (var s in segs) { var e = FlatEnds(s); if (e != null) ends.AddRange(e.Where(v => v != null)); }
                    segEnds3d[id] = ends;
                    segLen3d[id] = SegLen(segs);
                }
            }
            finally
            {
                try { topAsm.EditAssembly(); } catch { }
                top.ClearSelection2(true);
            }
        }

        private static double[] RouteFrameToTop(IComponent2 routeComp, double[] v, bool point)
        {
            double[] R = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, T = { 0, 0, 0 };
            try { if (routeComp.Transform2?.ArrayData is double[] a && a.Length >= 12) { R = a.Take(9).ToArray(); T = new[] { a[9], a[10], a[11] }; } } catch { }
            return new[]
            {
                v[0] * R[0] + v[1] * R[3] + v[2] * R[6] + (point ? T[0] : 0),
                v[0] * R[1] + v[1] * R[4] + v[2] * R[7] + (point ? T[1] : 0),
                v[0] * R[2] + v[1] * R[5] + v[2] * R[8] + (point ? T[2] : 0),
            };
        }

        public ExecutionResponse FlattenRoute(ToolRequest request)
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
                string type = (p?.Value<string>("type") ?? "manufacture").ToLowerInvariant();
                string conn = (p?.Value<string>("connector_display") ?? "3d").ToLowerInvariant();
                string orient = (p?.Value<string>("segment_orientation") ?? "horizontal").ToLowerInvariant();
                double fw = p?.Value<double?>("formboard_width") ?? 1.0;
                double fh = p?.Value<double?>("formboard_height") ?? 0.6;

                var top = _solidWorks.IActiveDoc2 as IModelDoc2;
                var topAsm = top as IAssemblyDoc;
                if (topAsm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "WRONG_DOCUMENT_TYPE",
                        "flatten_route needs the assembly that holds the route sub-assembly as the active document.");
                var comp = FindComponentByName(topAsm, routeName);
                if (comp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COMPONENT_NOT_FOUND",
                        ComponentNotFoundMessage(topAsm, routeName));
                var routeDoc = comp.GetModelDoc2() as IModelDoc2;
                if (!(routeDoc is IAssemblyDoc ra) || !ra.IsRouteAssembly())
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NOT_A_ROUTE",
                        $"'{routeName}' is not a (resolved) route sub-assembly.");
                var rm = topAsm.GetRouteManager() as IRouteManager;
                if (rm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "ROUTING_NOT_LOADED",
                        "No RouteManager: load the SOLIDWORKS Routing add-in.");

                // 3D route data first (route edit in context), for the flat-vs-3D length check.
                ReadRouteForFlat(top, topAsm, comp, out var wires, out var ends3d, out var len3d);

                var before = new HashSet<string>(FlattenedRouteFeatures(routeDoc));
                string prevConfig = null;
                try { prevConfig = (routeDoc.GetActiveConfiguration() as IConfiguration)?.Name; } catch { }
                top.ClearSelection2(true);
                if (!comp.Select4(false, null, false))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "SELECTION_FAILED",
                        $"Could not select the route component '{routeName}'.");
                int code = rm.CreateFlattenRoute(type == "annotation" ? 1 : 2, conn == "blocks" ? 2 : 1, fw, fh,
                    orient == "vertical" ? 1 : 2, false, "", "", "", "", false);
                var result = new JObject { ["route"] = ComponentPath(comp), ["route_file"] = comp.GetPathName(), ["code"] = code };
                if (code >= 1 && code <= 4)
                {
                    string[] why = { "", "a virtual (unsaved) component in the route must be saved externally first",
                        "problem in the route assembly", "duplicate component", "route assembly not selected" };
                    int e2 = 0; try { _solidWorks.ActivateDoc3(top.GetTitle(), false, 1, ref e2); } catch { }
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "FLATTEN_FAILED",
                        $"CreateFlattenRoute returned {code}: {why[code]}.");
                }
                var after = FlattenedRouteFeatures(routeDoc);
                string flat = after.FirstOrDefault(n => !before.Contains(n)) ?? after.LastOrDefault();
                if (flat == null)
                {
                    int e3 = 0; try { _solidWorks.ActivateDoc3(top.GetTitle(), false, 1, ref e3); } catch { }
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "FLATTEN_FAILED",
                        $"CreateFlattenRoute returned {code} but the route assembly has no FlattenedRoute feature.");
                }
                string flatConfig = FlatConfigOf(routeDoc, flat);
                result["flattened_route"] = flat;
                result["configuration"] = flatConfig;
                result["type"] = type == "annotation" ? "annotation" : "manufacture";

                // Flat lengths per route segment (SOLIDWORKS leaves the route assembly active in the flat
                // configuration; GetElectricalFlatRoute needs that).
                var segs = new JArray();
                double maxDiff = 0;
                double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
                try
                {
                    int e1 = 0;
                    _solidWorks.ActivateDoc3(routeDoc.GetTitle(), false, 1, ref e1);
                    if (flatConfig != null) routeDoc.ShowConfiguration2(flatConfig);
                    var frm = (routeDoc as IAssemblyDoc).GetRouteManager() as IRouteManager;
                    var fr = frm?.GetElectricalFlatRoute(flat) as IElectricalFlatRoute;
                    if (fr == null) result["warning"] = "GetElectricalFlatRoute returned nothing: flat lengths not read.";
                    else
                        foreach (var kv in len3d.OrderBy(k => k.Key))
                        {
                            var fs = SegList((object)fr.GetSketchSegments(kv.Key));
                            double fl = SegLen(fs);
                            foreach (var s in fs)
                                foreach (var e in FlatEnds(s) ?? new double[0][])
                                    if (e != null) for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], e[i]); hi[i] = Math.Max(hi[i], e[i]); }
                            double d = (fl - kv.Value) * 1000.0;
                            maxDiff = Math.Max(maxDiff, Math.Abs(d));
                            segs.Add(new JObject
                            {
                                ["id"] = kv.Key,
                                ["length_3d_mm"] = Math.Round(kv.Value * 1000.0, 2),
                                ["length_flat_mm"] = Math.Round(fl * 1000.0, 2),
                            });
                        }
                }
                finally
                {
                    try { if (!string.IsNullOrEmpty(prevConfig)) routeDoc.ShowConfiguration2(prevConfig); } catch { }
                    int e4 = 0; try { _solidWorks.ActivateDoc3(top.GetTitle(), false, 1, ref e4); } catch { }
                }
                // The flattened cable is a virtual part of the route assembly: a drawing view shows it only
                // once the route assembly is saved (SOLIDWORKS' own flatten-with-drawing saves it too).
                string block = SaveBlockReason(routeDoc.GetPathName(), null);
                if (block == null)
                {
                    int se = 0, sw2 = 0;
                    bool ok = routeDoc.Save3(1 /* silent */, ref se, ref sw2);
                    result["route_saved"] = ok;
                    if (!ok) result["save_warning"] = $"Save3 of the route assembly failed (errors {se}, warnings {sw2}): save it before route_drawing.";
                }
                else result["save_warning"] = "route assembly not saved: " + block + ". route_drawing needs it saved with the flattened configuration.";
                result["segments"] = segs;
                result["max_segment_length_diff_mm"] = Math.Round(maxDiff, 3);
                if (lo[0] < double.MaxValue)
                    result["flat_box_mm"] = new JArray(lo.Concat(hi).Select(v => Math.Round(v * 1000.0, 1)));
                result["wires"] = wires.Count;
                result["note"] = "The flattened route is a configuration of the route assembly (the 3D route stays in "
                    + (prevConfig ?? "its configuration") + "). Save the assembly (save_document) to keep it; route_drawing makes the 2D drawing.";
                return ChangedResponse(request, top, "ASSEMBLY", result);
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

        private static readonly (string name, int enumVal, double w, double h)[] IsoSheets =
        {
            ("A4", 6, 0.297, 0.210), ("A3", 8, 0.420, 0.297), ("A2", 9, 0.594, 0.420), ("A1", 10, 0.841, 0.594), ("A0", 11, 1.189, 0.841),
        };

        private string FindSheetFormat(string sheet)
        {
            string file = sheet.ToLowerInvariant() + " - landscape.slddrt";
            var dirs = new List<string>();
            try
            {
                string pref = _solidWorks.GetUserPreferenceStringValue(7 /* swFileLocationsSheetFormat */);
                if (!string.IsNullOrEmpty(pref)) dirs.AddRange(pref.Split(';').Where(d => d.Trim().Length > 0));
            }
            catch { }
            foreach (var d in dirs)
                try { var f = Path.Combine(d.Trim(), file); if (File.Exists(f)) return f; } catch { }
            return null;
        }

        private string FindInstallTemplate(string file)
        {
            try
            {
                string exe = _solidWorks.GetExecutablePath();
                foreach (var lang in new[] { "english" })
                {
                    var f = Path.Combine(exe, "lang", lang, file);
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
            return null;
        }

        public ExecutionResponse RouteDrawing(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);
            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");
            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");
            var warnings = new JArray();
            try
            {
                var p = request.Params as JObject;
                string routeName = p?.Value<string>("route") ?? "";
                string flat = p?.Value<string>("flattened_route") ?? "";
                string filePath = p?.Value<string>("file_path") ?? "";
                string sheetReq = (p?.Value<string>("sheet") ?? "auto").ToUpperInvariant();
                double scaleReq = p?.Value<double?>("scale") ?? 0;
                string pdfPath = p?.Value<string>("pdf_path") ?? "";
                string title = p?.Value<string>("title") ?? "";
                bool circuit = p?.Value<bool?>("circuit_summary") ?? true;
                bool connTables = p?.Value<bool?>("connector_tables") ?? true;
                string drwTemplate = p?.Value<string>("drawing_template") ?? "";
                string circuitTpl = p?.Value<string>("circuit_summary_template") ?? "";
                if (string.IsNullOrWhiteSpace(filePath) || !filePath.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER",
                        "file_path must be the full path of the drawing to write (.SLDDRW).");
                if (SaveBlockReason(filePath, null) != null || (!string.IsNullOrEmpty(pdfPath) && SaveBlockReason(pdfPath, null) != null))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "SAVE_REFUSED",
                        "file_path / pdf_path is in a read-only location (SOLIDPILOT_READONLY_PATHS).");

                var top = _solidWorks.IActiveDoc2 as IModelDoc2;
                var topAsm = top as IAssemblyDoc;
                if (topAsm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "WRONG_DOCUMENT_TYPE",
                        "route_drawing needs the assembly that holds the route sub-assembly (and its connectors) as the active document.");
                var comp = FindComponentByName(topAsm, routeName);
                if (comp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COMPONENT_NOT_FOUND",
                        ComponentNotFoundMessage(topAsm, routeName));
                var routeDoc = comp.GetModelDoc2() as IModelDoc2;
                if (routeDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NOT_A_ROUTE", $"'{routeName}' is not loaded.");
                var flats = FlattenedRouteFeatures(routeDoc);
                if (string.IsNullOrEmpty(flat)) flat = flats.LastOrDefault();
                if (flat == null || !flats.Contains(flat))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "NO_FLATTENED_ROUTE",
                        $"The route has no flattened route{(string.IsNullOrEmpty(flat) ? "" : " named '" + flat + "'")}: run flatten_route first. Found: [{string.Join(", ", flats)}].");
                string flatConfig = FlatConfigOf(routeDoc, flat);
                string routePath = routeDoc.GetPathName();
                if (string.IsNullOrEmpty(routePath) || !File.Exists(routePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "ROUTE_NOT_SAVED",
                        "The route assembly is not saved to disk: save it (save_document on the assembly) and retry.");

                // 1. 3D data: wires and the 3D ends of every route segment (route assembly frame).
                ReadRouteForFlat(top, topAsm, comp, out var wires, out var ends3d, out var len3d);

                // 2. Connectors: reference -> component, part and cavity locations (route frame).
                var refs = wires.SelectMany(w => new[] { w.FromRef, w.ToRef }).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
                var connComp = new Dictionary<string, IComponent2>();
                if (topAsm.GetComponents(false) is object[] allc)
                    foreach (var c in allc.OfType<IComponent2>())
                    {
                        string r = null;
                        try { r = c.ComponentReference; } catch { }
                        if (string.IsNullOrEmpty(r) || !refs.Contains(r) || c.GetSuppression2() == 0) continue;
                        if (!connComp.ContainsKey(r)) connComp[r] = c;
                    }
                // pin location in the route frame: top coords -> route frame (inverse of the route transform)
                double[] Rr = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, Tr = { 0, 0, 0 };
                try { if (comp.Transform2?.ArrayData is double[] a && a.Length >= 12) { Rr = a.Take(9).ToArray(); Tr = new[] { a[9], a[10], a[11] }; } } catch { }
                double[] ToRoute(double[] w)
                {
                    var d = Sub(w, Tr);
                    return new[] { d[0] * Rr[0] + d[1] * Rr[1] + d[2] * Rr[2], d[0] * Rr[3] + d[1] * Rr[4] + d[2] * Rr[5], d[0] * Rr[6] + d[1] * Rr[7] + d[2] * Rr[8] };
                }
                var pinLoc = new Dictionary<string, double[]>();
                var partOf = new Dictionary<string, string>();
                foreach (var kv in connComp)
                {
                    try { partOf[kv.Key] = Path.GetFileNameWithoutExtension(kv.Value.GetPathName()); } catch { }
                    var cj = ConnectorPointsWorld(kv.Value);
                    if (cj?["connection_points"] is JArray cps)
                        foreach (var cp in cps.OfType<JObject>())
                            if (cp["location_mm"] is JArray l)
                                pinLoc[kv.Key + "." + (string)cp["pin"]] = ToRoute(new[] { (double)l[0] / 1000, (double)l[1] / 1000, (double)l[2] / 1000 });
                }

                // 3. Wire end -> route segment that starts at its cavity (the stub).
                int SegAt(RouteWireData w, string end)
                {
                    if (!pinLoc.TryGetValue(end, out var L)) return -1;
                    int best = -1; double bd = 0.0008;
                    foreach (int id in w.SegIds)
                        if (ends3d.TryGetValue(id, out var es))
                            foreach (var e in es) { double d = Dist(e, L); if (d < bd) { bd = d; best = id; } }
                    return best;
                }

                // 4. Flat geometry: the free end of each wire end's stub, per connector.
                var connFlat = new Dictionary<string, List<double[]>>();
                var flatLen = new Dictionary<int, double>();
                var flatMid = new Dictionary<int, double[][]>();
                var connInner = new Dictionary<string, List<double[]>>();
                double[] lo = { double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue };
                string prevConfig = null;
                try { prevConfig = (routeDoc.GetActiveConfiguration() as IConfiguration)?.Name; } catch { }
                try
                {
                    int e1 = 0;
                    _solidWorks.ActivateDoc3(routeDoc.GetTitle(), false, 1, ref e1);
                    if (flatConfig != null) routeDoc.ShowConfiguration2(flatConfig);
                    var frm = (routeDoc as IAssemblyDoc).GetRouteManager() as IRouteManager;
                    var fr = frm?.GetElectricalFlatRoute(flat) as IElectricalFlatRoute;
                    if (fr == null) throw new InvalidOperationException("GetElectricalFlatRoute('" + flat + "') returned nothing.");
                    var flatEnds = new Dictionary<int, List<double[]>>();
                    foreach (int id in len3d.Keys)
                    {
                        var l = new List<double[]>();
                        var fsegs = SegList((object)fr.GetSketchSegments(id));
                        flatLen[id] = SegLen(fsegs);
                        double bl = -1;
                        foreach (var s in fsegs)
                        {
                            double sl = 0; try { sl = s.GetLength(); } catch { }
                            var ee = FlatEnds(s);
                            if (ee != null && ee[0] != null && ee[1] != null && sl > bl) { bl = sl; flatMid[id] = new[] { ee[0], ee[1] }; }
                        }
                        foreach (var s in fsegs)
                            foreach (var e in FlatEnds(s) ?? new double[0][])
                                if (e != null) { l.Add(e); lo[0] = Math.Min(lo[0], e[0]); lo[1] = Math.Min(lo[1], e[1]); hi[0] = Math.Max(hi[0], e[0]); hi[1] = Math.Max(hi[1], e[1]); }
                        flatEnds[id] = l;
                    }
                    foreach (var w in wires)
                        foreach (var (rf, pin) in new[] { (w.FromRef, w.FromPin), (w.ToRef, w.ToPin) })
                        {
                            int s = SegAt(w, rf + "." + pin);
                            if (s < 0 || !flatEnds.ContainsKey(s)) continue;
                            var others = w.SegIds.Where(i => i != s && flatEnds.ContainsKey(i)).SelectMany(i => flatEnds[i]).ToList();
                            var mine = flatEnds[s];
                            // flat legs meet the bundle through short splines that may belong to another
                            // segment: the connector end is the end farthest from the wire's other segments
                            double Near(double[] e) => others.Count == 0 ? 0 : others.Min(o => Dist(o, e));
                            var ordered = mine.OrderByDescending(Near).ToList();
                            var free = ordered.Take(1).ToList();
                            var inner = ordered.Skip(1).Reverse().Take(1).ToList();
                            if (free.Count == 0) continue;
                            if (!connFlat.ContainsKey(rf)) { connFlat[rf] = new List<double[]>(); connInner[rf] = new List<double[]>(); }
                            connFlat[rf].Add(free[0]);
                            if (inner.Count > 0) connInner[rf].Add(inner[0]);
                        }
                }
                finally
                {
                    try { if (!string.IsNullOrEmpty(prevConfig)) routeDoc.ShowConfiguration2(prevConfig); } catch { }
                    int e4 = 0; try { _solidWorks.ActivateDoc3(top.GetTitle(), false, 1, ref e4); } catch { }
                }
                if (lo[0] == double.MaxValue)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "FLAT_EMPTY", "The flattened route has no sketch segments.");
                double fw = hi[0] - lo[0], fhgt = hi[1] - lo[1];

                // 5. Sheet and scale: smallest ISO landscape sheet that holds the flat view at 1:1 (else a
                // standard reduction) with room for the tables under it.
                int maxRows = wires.Count + 3;
                double tablesH = Math.Max(0.006 * (wires.Count + 3), 0.006 * (refs.Select(r => wires.Count(w => w.FromRef == r || w.ToRef == r)).DefaultIfEmpty(0).Max() + 3));
                double[] scales = scaleReq > 0 ? new[] { scaleReq } : new[] { 1.0, 0.5, 0.2, 0.1 };
                var sheets = sheetReq == "AUTO" ? IsoSheets : IsoSheets.Where(s => s.name == sheetReq).ToArray();
                if (sheets.Length == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "sheet must be auto, A4, A3, A2, A1 or A0.");
                var pick = sheets.Last(); double scale = scales.Last(); bool fits = false;
                foreach (var sc in scales)
                {
                    foreach (var sh in sheets)
                        if (fw * sc + 0.08 <= sh.w && fhgt * sc + tablesH + 0.13 <= sh.h) { pick = sh; scale = sc; fits = true; break; }
                    if (fits) break;
                }
                if (!fits) warnings.Add($"The flat view ({Math.Round(fw * 1000)} x {Math.Round(fhgt * 1000)} mm) and its tables do not fit any sheet at the scales tried: using {pick.name} at 1:{Math.Round(1 / scale)}.");

                // 6. Drawing document, sheet format, flat view.
                if (string.IsNullOrEmpty(drwTemplate)) drwTemplate = _solidWorks.GetUserPreferenceStringValue(10 /* swDefaultTemplateDrawing */);
                if (string.IsNullOrEmpty(drwTemplate) || !File.Exists(drwTemplate))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "TEMPLATE_NOT_FOUND",
                        "No drawing template: pass drawing_template or set Tools > Options > Default Templates.");
                var ddoc = _solidWorks.NewDocument(drwTemplate, 12 /* user defined */, pick.w, pick.h) as IModelDoc2;
                var dr = ddoc as IDrawingDoc;
                if (dr == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "DOCUMENT_CREATION_FAILED", "NewDocument returned no drawing.");
                string fmt = FindSheetFormat(pick.name);
                if (fmt == null) warnings.Add($"No sheet format '{pick.name.ToLowerInvariant()} - landscape.slddrt' in the sheet format folders: sheet without a format.");
                double s1 = scale >= 1 ? scale : 1, s2 = scale >= 1 ? 1 : Math.Round(1 / scale);
                dr.SetupSheet5((dr.GetCurrentSheet() as ISheet)?.GetName() ?? "Sheet1", pick.enumVal, fmt != null ? 12 : 13, s1, s2, true, fmt ?? "", pick.w, pick.h, "", true);
                double vx = pick.w / 2, vy = pick.h - 0.04 - fhgt * scale / 2 - 0.02;
                string prevCfg2 = null;
                try { prevCfg2 = (routeDoc.GetActiveConfiguration() as IConfiguration)?.Name; } catch { }
                if (flatConfig != null) try { routeDoc.ShowConfiguration2(flatConfig); } catch { }
                var view = dr.CreateDrawViewFromModelView3(routePath, "*Front", vx, vy, 0) as IView;
                if (!string.IsNullOrEmpty(prevCfg2)) try { routeDoc.ShowConfiguration2(prevCfg2); } catch { }
                if (view == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "VIEW_CREATION_FAILED",
                        "CreateDrawViewFromModelView3 returned null for the route assembly.");
                if (flatConfig != null) view.ReferencedConfiguration = flatConfig;
                view.ScaleDecimal = scale;
                ddoc.EditRebuild3();
                // centre the flat geometry on (vx, vy) and above the table band
                var mu = _solidWorks.IGetMathUtility();
                double[] ToSheet(double[] m)
                {
                    var mp = mu.CreatePoint(new[] { m[0], m[1], m.Length > 2 ? m[2] : 0 }) as MathPoint;
                    var xf = view.ModelToViewTransform;
                    var sp = mp?.MultiplyTransform(xf) as MathPoint;
                    var d = sp?.ArrayData as double[];
                    return d == null ? null : new[] { d[0], d[1] };
                }
                var c0 = ToSheet(new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, 0 });
                if (c0 != null && view.Position is double[] pos && pos.Length >= 2)
                {
                    view.Position = new[] { pos[0] + (vx - c0[0]), pos[1] + (vy - c0[1]) };
                    ddoc.EditRebuild3();
                }

                // 7. Connector labels at the flat connector ends.
                var connOut = new JArray();
                double txt = 0.0035;
                foreach (var r in refs.OrderBy(x => x))
                {
                    var cj = new JObject { ["reference"] = r, ["part"] = partOf.TryGetValue(r, out var pn) ? pn : null };
                    cj["wires"] = wires.Count(w => w.FromRef == r || w.ToRef == r);
                    if (connFlat.TryGetValue(r, out var fe) && fe.Count > 0)
                    {
                        double[] m = { fe.Average(e => e[0]), fe.Average(e => e[1]), 0 };
                        double[] dir = { 0, 0, 0 };
                        if (connInner[r].Count > 0) dir = Unit(new[] { m[0] - connInner[r].Average(e => e[0]), m[1] - connInner[r].Average(e => e[1]), 0 });
                        var sp = ToSheet(m);
                        if (sp != null)
                        {
                            // a note grows right and down from its anchor (top-left): on an end pointing left,
                            // shift it left by its text width (about 0.6 x the 3.5 mm text height per character)
                            string label = r + (pn != null ? "\n" + pn : "");
                            double textW = label.Split('\n').Max(x => x.Length) * 0.0022;
                            double off = 0.010;
                            double lx = sp[0] + dir[0] * off - (dir[0] < -0.3 ? textW : 0), ly = sp[1] + dir[1] * off + 0.004;
                            var note = ddoc.InsertNote(label) as INote;
                            if (note?.GetAnnotation() is IAnnotation an)
                            {
                                an.SetPosition2(lx, ly, 0);
                                cj["label_at_mm"] = new JArray(Math.Round(lx * 1000, 1), Math.Round(ly * 1000, 1));
                            }
                            cj["flat_end_mm"] = new JArray(Math.Round(m[0] * 1000, 1), Math.Round(m[1] * 1000, 1));
                        }
                    }
                    else warnings.Add($"{r}: its wire ends were not found in the flat route (no label / position).");
                    connOut.Add(cj);
                }

                // 7b. Bundle lengths: a note on every route segment that carries two wires or more (the
                // flat length = the 3D length, flatten_route checks it), beside its longest straight.
                var lengthNotes = new JArray();
                if (p?.Value<bool?>("segment_lengths") ?? true)
                    foreach (var kv in flatMid)
                    {
                        int nw = wires.Count(w => w.SegIds.Contains(kv.Key));
                        if (nw < 2 || !flatLen.ContainsKey(kv.Key)) continue;
                        var a0 = kv.Value[0]; var b0 = kv.Value[1];
                        var sa = ToSheet(a0); var sb = ToSheet(b0);
                        if (sa == null || sb == null) continue;
                        double mx = (sa[0] + sb[0]) / 2, my = (sa[1] + sb[1]) / 2;
                        double dx = sb[0] - sa[0], dy = sb[1] - sa[1], dl = Math.Sqrt(dx * dx + dy * dy);
                        double nx = dl > 1e-9 ? -dy / dl : 0, ny = dl > 1e-9 ? dx / dl : 1;
                        if (ny < 0) { nx = -nx; ny = -ny; }
                        string lenTxt = (flatLen[kv.Key] * 1000.0).ToString("0.00");
                        var ln = ddoc.InsertNote(lenTxt) as INote;
                        if (ln?.GetAnnotation() is IAnnotation la)
                        {
                            // clear of the bundle (radius up to ~5 mm at 1:1); anchor = the note's top-left corner
                            la.SetPosition2(mx + nx * 0.010 - lenTxt.Length * 0.0011, my + ny * 0.010 + 0.004, 0);
                            lengthNotes.Add(new JObject { ["segment"] = kv.Key, ["wires"] = nw, ["length_mm"] = Math.Round(flatLen[kv.Key] * 1000.0, 2) });
                        }
                    }

                // 8. Tables under the view: circuit summary (BOM type, from the route), then one general table
                // per connector (pin, wire, spec, other end, cut length).
                var tables = new JArray();
                double ty = pick.h - 0.04 - fhgt * scale - 0.04;   // top of the table band
                double tx = 0.02;
                if (circuit)
                {
                    if (string.IsNullOrEmpty(circuitTpl)) circuitTpl = FindInstallTemplate("bom-circuit-summary.sldbomtbt") ?? "";
                    if (!File.Exists(circuitTpl)) warnings.Add("No circuit summary template (bom-circuit-summary.sldbomtbt): table skipped.");
                    else
                    {
                        var t = view.InsertBomTable4(false, tx, ty, 1 /* top left */, 2 /* top level */, flatConfig ?? "", circuitTpl, false, 0, false) as IBomTableAnnotation;
                        var ta = t as ITableAnnotation;
                        if (ta == null) warnings.Add("Circuit summary table not inserted.");
                        else
                        {
                            double w = 0; for (int c = 0; c < ta.ColumnCount; c++) w += ta.GetColumnWidth(c);
                            tables.Add(new JObject { ["kind"] = "circuit_summary", ["rows"] = ta.RowCount, ["columns"] = ta.ColumnCount, ["at_mm"] = new JArray(Math.Round(tx * 1000), Math.Round(ty * 1000)) });
                            tx += w + 0.01;
                        }
                    }
                }
                if (connTables)
                {
                    string[] head = { "PIN", "WIRE", "SPEC", "TO", "LENGTH" };
                    double[] cw = { 0.010, 0.012, 0.030, 0.017, 0.020 };
                    double rowH = 0.0055;
                    double band = 0.0;
                    foreach (var r in refs.OrderBy(x => x))
                    {
                        var rows = new List<string[]>();
                        foreach (var w in wires)
                        {
                            if (w.FromRef == r) rows.Add(new[] { w.FromPin, w.Name, w.Spec, w.ToRef + "." + w.ToPin, w.CutMm.ToString("0.0") });
                            else if (w.ToRef == r) rows.Add(new[] { w.ToPin, w.Name, w.Spec, w.FromRef + "." + w.FromPin, w.CutMm.ToString("0.0") });
                        }
                        rows = rows.OrderBy(x => int.TryParse(x[0], out var n) ? n : int.MaxValue).ThenBy(x => x[0]).ToList();
                        double tw = cw.Sum();
                        if (tx + tw > pick.w - 0.02) { tx = 0.02; ty -= band + 0.01; band = 0; }
                        var ta = dr.InsertTableAnnotation2(false, tx, ty, 1 /* top left */, "", rows.Count + 2, head.Length) as ITableAnnotation;
                        if (ta == null) { warnings.Add($"{r}: connector table not inserted."); continue; }
                        ta.MergeCells(0, 0, 0, head.Length - 1);
                        ta.Text[0, 0] = r + (partOf.TryGetValue(r, out var pn2) ? " - " + pn2 : "");
                        for (int c = 0; c < head.Length; c++) { ta.Text[1, c] = head[c]; ta.SetColumnWidth(c, cw[c], 0); }
                        for (int i = 0; i < rows.Count; i++)
                            for (int c = 0; c < head.Length; c++) ta.Text[i + 2, c] = rows[i][c] ?? "";
                        tables.Add(new JObject { ["kind"] = "connector", ["reference"] = r, ["rows"] = rows.Count, ["at_mm"] = new JArray(Math.Round(tx * 1000), Math.Round(ty * 1000)) });
                        tx += tw + 0.01;
                        band = Math.Max(band, (rows.Count + 2) * rowH);
                    }
                }
                if (!string.IsNullOrEmpty(title))
                {
                    var tn = ddoc.InsertNote(title) as INote;
                    (tn?.GetAnnotation() as IAnnotation)?.SetPosition2(0.02, pick.h - 0.015, 0);
                }
                ddoc.ForceRebuild3(false);
                try
                {
                    var vis = view.GetVisibleComponents() as object[];
                    if (vis == null || !vis.OfType<IComponent2>().Any(c => (c.Name2 ?? "").IndexOf("Flattened", StringComparison.OrdinalIgnoreCase) >= 0))
                        warnings.Add("The view does not show the flattened cable: save the route assembly (flatten_route does) and run route_drawing again.");
                }
                catch { }

                // 9. Save (and PDF).
                var outDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                int se = 0, sw = 0;
                bool saved = ddoc.Extension.SaveAs3(filePath, 0, 1 /* silent */, null, null, ref se, ref sw);
                if (!saved) warnings.Add($"SaveAs3('{filePath}') failed (errors {se}, warnings {sw}).");
                string pdfOut = null;
                if (!string.IsNullOrEmpty(pdfPath))
                {
                    var pdir = Path.GetDirectoryName(pdfPath);
                    if (!string.IsNullOrEmpty(pdir)) Directory.CreateDirectory(pdir);
                    int pe = 0, pw = 0;
                    var exportData = _solidWorks.GetExportFileData(1 /* swExportPdfData */) as ExportPdfData;
                    if (ddoc.Extension.SaveAs3(pdfPath, 0, 1, exportData, null, ref pe, ref pw)) pdfOut = pdfPath;
                    else warnings.Add($"PDF export failed (errors {pe}, warnings {pw}).");
                }

                var outline = view.GetOutline() as double[];
                var result = new JObject
                {
                    ["drawing"] = saved ? filePath : null,
                    ["pdf"] = pdfOut,
                    ["route"] = ComponentPath(comp),
                    ["flattened_route"] = flat,
                    ["configuration"] = flatConfig,
                    ["sheet"] = pick.name,
                    ["scale"] = scale >= 1 ? $"{s1}:1" : $"1:{s2}",
                    ["flat_size_mm"] = new JArray(Math.Round(fw * 1000, 1), Math.Round(fhgt * 1000, 1)),
                    ["view_outline_mm"] = outline == null ? null : new JArray(outline.Take(4).Select(v => Math.Round(v * 1000, 1))),
                    ["connectors"] = connOut,
                    ["tables"] = tables,
                    ["bundle_length_notes"] = lengthNotes,
                    ["wires"] = wires.Count,
                };
                if (warnings.Count > 0) result["warnings"] = warnings;
                result["note"] = "The flat view shows SOLIDWORKS' 'External Connector' blocks where the connectors are components of the parent assembly; the labels and connector tables carry their references.";
                return ChangedResponse(request, ddoc, "DRAWING", result);
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
