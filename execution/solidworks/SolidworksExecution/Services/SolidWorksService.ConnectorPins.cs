using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: per-cavity connection points for SOLIDWORKS Routing connectors.
    //   connector_cavities  read-only, active part: the openings (inner loops) of the wire-exit face,
    //                       sorted into a grid as seen from the wire side, optionally numbered.
    //   add_connector_pins  active part: one connection point per pin at its cavity, each carrying
    //                       its pin number (IConnectionPointFeatureData.ElectricalPinID), so a from-to
    //                       import attaches each wire to its own cavity instead of one shared point.
    // ConnectionPointsOf / ConnectorPointsWorld are shared with routing_status (connector stub ends).
    public partial class SolidWorksService
    {
        private static JArray J3(double[] v, int digits = 7) =>
            new JArray(Math.Round(v[0], digits), Math.Round(v[1], digits), Math.Round(v[2], digits));

        private static double[] ParseVec(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            try
            {
                var a = t.Type == JTokenType.String ? (string.IsNullOrWhiteSpace((string)t) ? null : JArray.Parse((string)t)) : t as JArray;
                if (a == null || a.Count != 3) return null;
                return new[] { (double)a[0], (double)a[1], (double)a[2] };
            }
            catch { return null; }
        }

        // Planar faces of the part's solid bodies whose outward normal is dir, within depth of the
        // outermost one: (face index as analyze_model(faces) numbers it, face, height along dir).
        private static List<(int i, IFace2 face, double h)> ExitFaces(IModelDoc2 doc, double[] dir, double depth)
        {
            var list = new List<(int, IFace2, double)>();
            var bodies = (doc as IPartDoc)?.GetBodies2(0 /* swSolidBody */, true) as object[];
            if (bodies == null) return list;
            int idx = 0;
            foreach (var b in bodies)
            {
                if (!((b as IBody2)?.GetFaces() is object[] fs)) continue;
                foreach (var fo in fs)
                {
                    var face = fo as IFace2;
                    int i = idx++;
                    if (face == null) continue;
                    try
                    {
                        var surf = face.IGetSurface();
                        if (surf == null || !surf.IsPlane()) continue;
                        if (!(face.Normal is double[] n) || n.Length < 3 || Dot(Unit(n), dir) < 0.99) continue;
                        if (!(surf.PlaneParams is double[] pp) || pp.Length < 6) continue;
                        list.Add((i, face, Dot(new[] { pp[3], pp[4], pp[5] }, dir)));
                    }
                    catch { }
                }
            }
            if (list.Count == 0) return list;
            double top = list.Max(t => t.Item3);
            return list.Where(t => t.Item3 > top - depth).ToList();
        }

        // Points along a loop's edges (tessellated; full circles sampled from CircleParams).
        private static List<double[]> LoopPoints(ILoop2 loop)
        {
            var pts = new List<double[]>();
            if (!(loop.GetEdges() is object[] edges)) return pts;
            foreach (var eo in edges)
            {
                var edge = eo as IEdge;
                var curve = edge?.GetCurve() as ICurve;
                if (curve == null) continue;
                double[] s = null, e = null;
                try
                {
                    if (edge.GetCurveParams3() is CurveParamData cp)
                    {
                        s = cp.StartPoint as double[];
                        e = cp.EndPoint as double[];
                    }
                }
                catch { }
                bool closed = s == null || e == null || Math.Sqrt(Dot(Sub(s, e), Sub(s, e))) < 1e-9;
                if (closed && curve.IsCircle() && curve.CircleParams is double[] c && c.Length >= 7)
                {
                    var ax = Unit(new[] { c[3], c[4], c[5] });
                    var u = Math.Abs(ax[0]) < 0.9 ? Unit(Cross(ax, new[] { 1.0, 0, 0 })) : Unit(Cross(ax, new[] { 0, 1.0, 0 }));
                    var v = Cross(ax, u);
                    for (int k = 0; k < 24; k++)
                    {
                        double a = 2 * Math.PI * k / 24;
                        pts.Add(new[] { c[0] + c[6] * (Math.Cos(a) * u[0] + Math.Sin(a) * v[0]),
                                        c[1] + c[6] * (Math.Cos(a) * u[1] + Math.Sin(a) * v[1]),
                                        c[2] + c[6] * (Math.Cos(a) * u[2] + Math.Sin(a) * v[2]) });
                    }
                    continue;
                }
                if (s != null) pts.Add(s);
                if (e != null) pts.Add(e);
                try
                {
                    if (s != null && e != null && curve.GetTessPts(1e-5, 1e-5, s, e) is double[] t)
                        for (int k = 0; k + 2 < t.Length; k += 3) pts.Add(new[] { t[k], t[k + 1], t[k + 2] });
                }
                catch { }
            }
            return pts;
        }

        // The frame a person looking at the exit face from outside (wire side) sees: right, up, out.
        private static (double[] right, double[] up) FaceViewFrame(double[] outward, double[] upHint)
        {
            var n = Unit(outward);
            double[] up = upHint == null ? null : Sub(upHint, Scale(n, Dot(upHint, n)));
            if (up == null || Dot(up, up) < 1e-12)
            {
                // No usable hint: the part axis least aligned with the normal, projected.
                double[][] axes = { new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, new[] { 1.0, 0, 0 } };
                var best = axes.OrderBy(a => Math.Abs(Dot(a, n))).First();
                up = Sub(best, Scale(n, Dot(best, n)));
            }
            up = Unit(up);
            var right = Unit(Cross(Scale(n, -1), up));    // viewer looks along -n
            return (right, up);
        }

        // Cluster sorted values: a new group starts after a gap larger than tol.
        private static List<List<int>> Cluster(IList<double> vals, double tol)
        {
            var order = Enumerable.Range(0, vals.Count).OrderBy(i => vals[i]).ToList();
            var groups = new List<List<int>>();
            foreach (int i in order)
            {
                if (groups.Count == 0 || vals[i] - vals[groups.Last().Last()] > tol) groups.Add(new List<int>());
                groups.Last().Add(i);
            }
            return groups;
        }

        public ExecutionResponse ConnectorCavities(ToolRequest request)
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
                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (!(doc is IPartDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "connector_cavities needs an active part (open the connector part).");
                var dir = ParseVec(p?["direction"]);
                if (dir == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "direction must be [x, y, z]: the outward normal of the wire-exit (rear) face, part coordinates.");
                dir = Unit(dir);
                var upHint = ParseVec(p?["up"]);
                double depth = p?.Value<double?>("exit_depth") ?? 0.001;
                double minSize = p?.Value<double?>("min_size") ?? 0.0005;
                double sizeTol = p?.Value<double?>("size_tolerance") ?? 0.2;
                int expected = p?.Value<int?>("expected_count") ?? 0;
                JObject numbering = null;
                try
                {
                    var nt = p?["numbering"];
                    if (nt != null && nt.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)nt)) numbering = JObject.Parse((string)nt);
                    else if (nt is JObject njo) numbering = njo;
                }
                catch (Exception ex)
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "numbering: " + ex.Message);
                }

                var faces = ExitFaces(doc, dir, depth);
                if (faces.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "FACE_NOT_FOUND",
                        "No planar face has its outward normal along direction: check direction (part coordinates).");
                double faceH = faces.OrderByDescending(f => f.face.GetArea()).First().h;
                var (right, up) = FaceViewFrame(dir, upHint);

                // Every inner loop of those faces: centre (projected on the main face plane), size in
                // the view frame.
                var loops = new List<JObject>();
                foreach (var (fi, face, h) in faces)
                {
                    if (!(face.GetLoops() is object[] ls)) continue;
                    foreach (var lo in ls)
                    {
                        var loop = lo as ILoop2;
                        if (loop == null || loop.IsOuter()) continue;
                        var pts = LoopPoints(loop);
                        if (pts.Count < 2) continue;
                        double u0 = pts.Min(q => Dot(q, right)), u1 = pts.Max(q => Dot(q, right));
                        double v0 = pts.Min(q => Dot(q, up)), v1 = pts.Max(q => Dot(q, up));
                        double uc = (u0 + u1) / 2, vc = (v0 + v1) / 2;
                        var centre = Add(Add(Scale(right, uc), Scale(up, vc)), Scale(dir, faceH));
                        loops.Add(new JObject
                        {
                            ["face_index"] = fi,
                            ["u_mm"] = Math.Round(uc * 1000, 3),
                            ["v_mm"] = Math.Round(vc * 1000, 3),
                            ["width_mm"] = Math.Round((u1 - u0) * 1000, 3),
                            ["height_mm"] = Math.Round((v1 - v0) * 1000, 3),
                            ["point"] = J3(centre),
                        });
                    }
                }

                // Cavities: openings at least min_size in both directions whose size matches the most
                // common size (marks, latch windows and moulded text differ); the rest is reported.
                var big = loops.Where(l => (double)l["width_mm"] >= minSize * 1000 && (double)l["height_mm"] >= minSize * 1000).ToList();
                var sizeGroups = big.GroupBy(l => (Math.Round((double)l["width_mm"] / 0.1), Math.Round((double)l["height_mm"] / 0.1)))
                                    .OrderByDescending(g => g.Count()).ToList();
                var cav = new List<JObject>();
                if (sizeGroups.Count > 0)
                {
                    double w = sizeGroups[0].Average(l => (double)l["width_mm"]), hh = sizeGroups[0].Average(l => (double)l["height_mm"]);
                    cav = big.Where(l => Math.Abs((double)l["width_mm"] - w) <= sizeTol * w && Math.Abs((double)l["height_mm"] - hh) <= sizeTol * hh).ToList();
                }
                var others = loops.Except(cav).ToList();

                // Grid: rows by v (top first), columns by u (left to right as seen from the wire side).
                var warnings = new JArray();
                var result = new JObject
                {
                    ["document"] = doc.GetTitle(),
                    ["direction"] = J3(dir, 6),
                    ["view_right"] = J3(right, 6),
                    ["view_up"] = J3(up, 6),
                    ["face_indices"] = new JArray(faces.Select(f => f.i)),
                    ["face_height_mm"] = Math.Round(faceH * 1000, 3),
                    ["cavity_count"] = cav.Count,
                };
                if (cav.Count > 0)
                {
                    double cw = cav.Average(l => (double)l["width_mm"]), ch = cav.Average(l => (double)l["height_mm"]);
                    var rows = Cluster(cav.Select(l => -(double)l["v_mm"]).ToList(), Math.Max(0.2, 0.4 * ch));
                    var cols = Cluster(cav.Select(l => (double)l["u_mm"]).ToList(), Math.Max(0.2, 0.4 * cw));
                    for (int r = 0; r < rows.Count; r++) foreach (int i in rows[r]) cav[i]["row"] = r + 1;
                    for (int c = 0; c < cols.Count; c++) foreach (int i in cols[c]) cav[i]["col"] = c + 1;
                    result["rows"] = rows.Count;
                    result["columns"] = cols.Count;
                    result["cavity_size_mm"] = new JArray(Math.Round(cw, 3), Math.Round(ch, 3));
                    if (rows.Count > 1)
                    {
                        var rv = rows.Select(g => g.Average(i => (double)cav[i]["v_mm"])).ToList();
                        result["row_pitch_mm"] = Math.Round(Math.Abs(rv.First() - rv.Last()) / (rows.Count - 1), 3);
                    }
                    if (cols.Count > 1)
                    {
                        var cu = cols.Select(g => g.Average(i => (double)cav[i]["u_mm"])).ToList();
                        result["column_pitch_mm"] = Math.Round(Math.Abs(cu.Last() - cu.First()) / (cols.Count - 1), 3);
                    }
                    if (cav.Count != rows.Count * cols.Count)
                        warnings.Add($"{cav.Count} cavities in a {rows.Count} x {cols.Count} grid: staggered or missing positions (numbering counts every grid position)");

                    if (numbering != null)
                    {
                        // first: corner of pin `start` as seen from the wire side; along: 'row' (pins
                        // run along a row, then the next row) or 'column'.
                        string first = (numbering.Value<string>("first") ?? "top_left").ToLowerInvariant();
                        string along = (numbering.Value<string>("along") ?? "row").ToLowerInvariant();
                        int start = numbering.Value<int?>("start") ?? 1;
                        bool fromBottom = first.StartsWith("bottom"), fromRight = first.EndsWith("right");
                        int nr = rows.Count, nc = cols.Count;
                        foreach (var c in cav)
                        {
                            int r = (int)c["row"] - 1, k = (int)c["col"] - 1;
                            if (fromBottom) r = nr - 1 - r;
                            if (fromRight) k = nc - 1 - k;
                            c["pin"] = (start + (along.StartsWith("col") ? k * nr + r : r * nc + k)).ToString();
                        }
                        result["numbering"] = numbering;
                    }
                }
                if (expected > 0 && cav.Count != expected) warnings.Add($"expected {expected} cavities, found {cav.Count}");
                var ordered = cav.OrderBy(c => c["pin"] != null ? int.Parse((string)c["pin"]) : 0)
                                 .ThenBy(c => (int?)c["row"] ?? 0).ThenBy(c => (int?)c["col"] ?? 0).ToList();
                result["cavities"] = new JArray(ordered);
                if (numbering != null)
                    result["pins"] = new JArray(ordered.Select(c => new JObject { ["pin"] = c["pin"], ["point"] = c["point"] }));
                result["other_openings"] = new JArray(others.Take(30));
                if (others.Count > 30) result["other_openings_count"] = others.Count;
                if (warnings.Count > 0) result["warnings"] = warnings;
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

        // Connection points of a part document: name, pin, port, location / direction (part
        // coordinates, m), stub length (m).
        private static List<JObject> ConnectionPointsOf(IModelDoc2 partDoc)
        {
            var list = new List<JObject>();
            var f = partDoc?.FirstFeature() as IFeature;
            while (f != null)
            {
                string t = "";
                try { t = f.GetTypeName2() ?? ""; } catch { }
                if (t == "ConnectionPoint")
                {
                    var j = new JObject { ["name"] = f.Name };
                    try
                    {
                        if (f.GetDefinition() is IConnectionPointFeatureData d)
                        {
                            j["pin"] = d.ElectricalPinID ?? "";
                            string port = d.PortID ?? "";
                            if (port != "") j["port"] = port;
                            if (d.Location is double[] loc && loc.Length >= 3) j["location"] = J3(loc);
                            if (d.Direction is double[] dv && dv.Length >= 3) j["direction"] = J3(Unit(dv), 6);
                            j["stub_m"] = Math.Round(d.StubLength, 6);
                        }
                    }
                    catch (Exception ex) { j["error"] = ex.Message; }
                    list.Add(j);
                }
                f = f.GetNextFeature() as IFeature;
            }
            return list;
        }

        // A component's connection points in the coordinates of the assembly that holds the
        // component tree (row vectors: world = p R + T, IComponent2.Transform2), with the stub end
        // (location + stub along the direction): where a route branch to that cavity starts.
        private static JObject ConnectorPointsWorld(IComponent2 comp)
        {
            var o = new JObject { ["component"] = ComponentPath(comp) };
            try { string rf = comp.ComponentReference; if (!string.IsNullOrEmpty(rf)) o["reference"] = rf; } catch { }
            var md = comp.GetModelDoc2() as IModelDoc2;
            if (md == null) { o["error"] = "not loaded (lightweight or suppressed): resolve it"; return o; }
            var cps = ConnectionPointsOf(md);
            if (cps.Count == 0) return null;
            double[] R = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, T = { 0, 0, 0 };
            try
            {
                if (comp.Transform2?.ArrayData is double[] a && a.Length >= 12)
                {
                    R = a.Take(9).ToArray();
                    T = new[] { a[9], a[10], a[11] };
                }
            }
            catch { }
            double[] Map(double[] v, bool point) => new[]
            {
                v[0] * R[0] + v[1] * R[3] + v[2] * R[6] + (point ? T[0] : 0),
                v[0] * R[1] + v[1] * R[4] + v[2] * R[7] + (point ? T[1] : 0),
                v[0] * R[2] + v[1] * R[5] + v[2] * R[8] + (point ? T[2] : 0),
            };
            var arr = new JArray();
            foreach (var c in cps)
            {
                var j = new JObject { ["name"] = c["name"], ["pin"] = c["pin"] };
                if (c["location"] is JArray l && c["direction"] is JArray dv)
                {
                    var loc = Map(new[] { (double)l[0], (double)l[1], (double)l[2] }, true);
                    var dir = Unit(Map(new[] { (double)dv[0], (double)dv[1], (double)dv[2] }, false));
                    double stub = c.Value<double?>("stub_m") ?? 0;
                    j["location_mm"] = J3(Scale(loc, 1000), 3);
                    j["direction"] = J3(dir, 6);
                    j["stub_mm"] = Math.Round(stub * 1000, 3);
                    j["stub_end_mm"] = J3(Scale(Add(loc, Scale(dir, stub)), 1000), 3);
                }
                if (c["error"] != null) j["error"] = c["error"];
                arr.Add(j);
            }
            o["connection_points"] = arr;
            return o;
        }

        public ExecutionResponse AddConnectorPins(ToolRequest request)
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
                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (!(doc is IPartDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "add_connector_pins needs an active part (open the connector part).");
                var dir = ParseVec(p?["direction"]);
                if (dir == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "direction must be [x, y, z]: the outward normal of the wire-exit face (part coordinates).");
                dir = Unit(dir);
                JArray pins;
                try
                {
                    var pt = p?["pins"];
                    pins = pt?.Type == JTokenType.String ? JArray.Parse((string)pt) : pt as JArray;
                }
                catch (Exception ex)
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "pins: " + ex.Message);
                }
                if (pins == null || pins.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "pins must be a JSON list of {\"pin\": \"1\", \"point\": [x, y, z]} (part coordinates, m), e.g. connector_cavities(numbering=...).pins.");
                var wanted = new List<(string pin, double[] pt)>();
                foreach (var t in pins)
                {
                    string pin = t?.Value<string>("pin");
                    var pt = ParseVec(t?["point"]);
                    if (string.IsNullOrWhiteSpace(pin) || pt == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "every pins entry needs pin and point [x, y, z]: " + t?.ToString(Newtonsoft.Json.Formatting.None));
                    wanted.Add((pin.Trim(), pt));
                }
                if (wanted.Select(w => w.pin).Distinct(StringComparer.OrdinalIgnoreCase).Count() != wanted.Count)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "pin numbers must be unique.");
                double stub = p?.Value<double?>("stub_length") ?? 0.005;
                string prefix = p?.Value<string>("name_prefix") ?? "CPoint_";
                bool replace = p?.Value<bool?>("replace_existing") ?? false;
                double depth = p?.Value<double?>("exit_depth") ?? 0.001;
                int faceIndex = p?.Value<int?>("face_index") ?? -1;

                var warnings = new JArray();
                var result = new JObject { ["document"] = doc.GetTitle() };

                // 1. Existing connection points: refuse duplicates, or delete them (with the sketches
                //    that only held their points) when replace_existing.
                var existing = new List<IFeature>();
                for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                {
                    string t = "";
                    try { t = f.GetTypeName2() ?? ""; } catch { }
                    if (t == "ConnectionPoint") existing.Add(f);
                }
                if (existing.Count > 0 && !replace)
                {
                    var have = ConnectionPointsOf(doc).Select(c => (string)c["pin"]).ToList();
                    var clash = wanted.Where(w => have.Contains(w.pin, StringComparer.OrdinalIgnoreCase)).Select(w => w.pin).ToList();
                    if (clash.Count > 0 || have.Any(h => string.IsNullOrEmpty(h)))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "ALREADY_EXISTS",
                            $"The part already has {existing.Count} connection point(s) ({string.Join(", ", existing.Select(e => e.Name))}); " +
                            "a point without a pin, or with one of these pins, would compete for the wires: pass replace_existing=True to delete them first.");
                }
                var deleted = new JArray();
                if (replace && existing.Count > 0)
                {
                    // The point sketches: 3D sketches holding only points, whose children are among
                    // the connection points (a connection point's GetParents did not list its sketch).
                    var names = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                    var sketches = new List<IFeature>();
                    for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                    {
                        if ((f.GetTypeName2() ?? "") != "3DProfileFeature") continue;
                        var sk0 = f.GetSpecificFeature2() as ISketch;
                        int segs = (sk0?.GetSketchSegments() as object[])?.Length ?? 0;
                        var kids = (f.GetChildren() as object[] ?? new object[0]).OfType<IFeature>().ToList();
                        if (segs == 0 && kids.Count > 0 && kids.All(k => names.Contains(k.Name))) sketches.Add(f);
                    }
                    foreach (var f in existing)
                    {
                        string n = f.Name;
                        doc.ClearSelection2(true);
                        bool ok = f.Select2(false, 0) && doc.Extension.DeleteSelection2(0);
                        deleted.Add(n + (ok ? "" : " (not deleted)"));
                    }
                    foreach (var sf in sketches)
                    {
                        string sn = sf.Name;
                        doc.ClearSelection2(true);
                        if (sf.Select2(false, 0) && doc.Extension.DeleteSelection2(0)) deleted.Add(sn);
                    }
                    doc.ClearSelection2(true);
                    result["deleted"] = deleted;
                }

                // 2. The exit face: given, or the largest outermost face along direction.
                double faceH;
                if (faceIndex < 0)
                {
                    var faces = ExitFaces(doc, dir, depth);
                    if (faces.Count == 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "FACE_NOT_FOUND",
                            "No planar face has its outward normal along direction: check direction (part coordinates) or give face_index.");
                    var best = faces.OrderByDescending(f => f.face.GetArea()).First();
                    faceIndex = best.i;
                    faceH = best.h;
                }
                else
                {
                    var all = ExitFaces(doc, dir, 1.0);
                    var hit = all.FirstOrDefault(f => f.i == faceIndex);
                    if (hit.face == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER",
                            $"face_index {faceIndex} is not a planar face with its normal along direction.");
                    faceH = hit.h;
                }
                result["exit_face"] = faceIndex;
                result["face_height_mm"] = Math.Round(faceH * 1000, 3);

                // 3. One 3D sketch with every pin point, on the exit face plane.
                var onFace = wanted.Select(w => (w.pin, pt: Add(w.pt, Scale(dir, faceH - Dot(w.pt, dir))), off: faceH - Dot(w.pt, dir))).ToList();
                foreach (var w in onFace)
                    if (Math.Abs(w.off) > 0.0002) warnings.Add($"pin {w.pin}: point moved {Math.Round(w.off * 1000, 3)} mm along direction onto the exit face");
                var sm = doc.SketchManager;
                doc.ClearSelection2(true);
                sm.Insert3DSketch(true);
                bool oldDb = sm.AddToDB;
                sm.AddToDB = true;
                foreach (var w in onFace) sm.CreatePoint(w.pt[0], w.pt[1], w.pt[2]);
                sm.AddToDB = oldDb;
                sm.Insert3DSketch(true);
                var sketchFeat = doc.FeatureByPositionReverse(0) as IFeature;
                var sk = sketchFeat?.GetSpecificFeature2() as ISketch;
                if (sk == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "SKETCH_FAILED", "Could not create the 3D sketch of the pin points.");
                try { sketchFeat.Name = prefix + "Pins_Sketch"; } catch { }
                result["sketch"] = sketchFeat.Name;
                var skPts = (sk.GetSketchPoints2() as object[] ?? new object[0]).OfType<ISketchPoint>().ToList();

                // 4. A connection point per pin, then its pin number (ElectricalPinID).
                var selMgr = doc.SelectionManager as ISelectionMgr;
                var made = new JArray();
                foreach (var w in onFace)
                {
                    var spt = skPts.OrderBy(s => { var d = Sub(new[] { s.X, s.Y, s.Z }, w.pt); return Dot(d, d); }).FirstOrDefault();
                    var pj = new JObject { ["pin"] = w.pin, ["point"] = J3(w.pt) };
                    if (spt == null) { pj["error"] = "sketch point not found"; made.Add(pj); continue; }
                    IFeature cpFeat = null;
                    foreach (bool reverse in new[] { false, true })
                    {
                        doc.ClearSelection2(true);
                        if (!SelectFaceByIndexAppend(doc, faceIndex, 1)) { pj["error"] = $"face {faceIndex} not selectable"; break; }
                        var sd = selMgr.CreateSelectData();
                        sd.Mark = 1;
                        spt.Select4(true, sd);
                        int before = 0;
                        for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature) before++;
                        bool ok = doc.FeatureManager.InsertConnectionPoint(3 /* electrical */, 2 /* cable or wire */, reverse,
                            0, stub, 0, 0, 0, "", 0, 0, false, "", "");
                        var nf = doc.FeatureByPositionReverse(0) as IFeature;
                        if (!ok || nf == null || (nf.GetTypeName2() ?? "") != "ConnectionPoint") { pj["error"] = "InsertConnectionPoint created nothing"; break; }
                        // The route must leave along direction (away from the housing).
                        var d0 = nf.GetDefinition() as IConnectionPointFeatureData;
                        if (d0?.Direction is double[] dv && dv.Length >= 3 && Dot(Unit(dv), dir) < 0 && !reverse)
                        {
                            doc.ClearSelection2(true);
                            nf.Select2(false, 0);
                            doc.Extension.DeleteSelection2(0);
                            continue;    // again, reversed
                        }
                        cpFeat = nf;
                        break;
                    }
                    if (cpFeat == null) { made.Add(pj); continue; }
                    try { cpFeat.Name = prefix + w.pin; } catch { }
                    try
                    {
                        var def = cpFeat.GetDefinition() as IConnectionPointFeatureData;
                        def.ElectricalPinID = w.pin;
                        if (!cpFeat.ModifyDefinition(def, doc, null)) warnings.Add($"pin {w.pin}: ModifyDefinition returned false");
                        var back = cpFeat.GetDefinition() as IConnectionPointFeatureData;
                        pj["pin_id"] = back?.ElectricalPinID ?? "";
                        if (back?.Direction is double[] dv2 && dv2.Length >= 3) pj["direction"] = J3(Unit(dv2), 6);
                        if (back?.Location is double[] lv && lv.Length >= 3) pj["location"] = J3(lv);
                        pj["stub_mm"] = Math.Round((back?.StubLength ?? 0) * 1000, 3);
                        if ((string)pj["pin_id"] != w.pin) warnings.Add($"pin {w.pin}: ElectricalPinID reads back '{pj["pin_id"]}'");
                    }
                    catch (Exception ex) { warnings.Add($"pin {w.pin}: pin ID: {ex.Message}"); }
                    pj["feature"] = cpFeat.Name;
                    made.Add(pj);
                }
                doc.ClearSelection2(true);

                // 5. Routing component type: connector, electrical.
                try
                {
                    var rcm = doc.Extension.GetRoutingComponentManager() as SolidWorks.Interop.SWRoutingLib.IRoutingComponentManager;
                    if (rcm != null)
                    {
                        rcm.SetComponentType(2 /* connector */);
                        rcm.SetComponentRouteTypeToCustomProperty(3 /* electrical */);
                    }
                    else warnings.Add("no RoutingComponentManager (Routing add-in not loaded?): component type not set");
                }
                catch (Exception ex) { warnings.Add("component type: " + ex.Message); }

                result["connection_points"] = made;
                result["created"] = made.Count(m => m["feature"] != null);
                if (warnings.Count > 0) result["warnings"] = warnings;
                bool success = made.All(m => m["feature"] != null);
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = success ? "COMPLETED" : "FAILED",
                    Verified = success,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = doc.GetTitle(),
                        DocumentType = "PART",
                        Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = success ? null : new ExecutionError { Code = "PARTIAL", Message = "Some connection points were not created: see connection_points." }
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
