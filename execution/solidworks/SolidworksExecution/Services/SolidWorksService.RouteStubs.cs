using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Services
{
    // routing_status(route, check_stubs): the connection points the route's wires use, with their
    // stub ends as the connectors are NOW, and whether the route sketch still starts there. A
    // connector moved after the from-to import keeps its old stub in the route sketch: the route
    // shows no error, but its wires and every branch drawn from that stub stay at the old place
    // (detached). Re-import the route (import_from_to replaces=...) to give it new stubs.
    public partial class SolidWorksService
    {
        private static JObject RouteStubs(IAssemblyDoc asm, IComponent2 routeComp, JObject routeJson, double tolMm)
        {
            var o = new JObject();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (routeJson["wires"] is JArray ws)
                foreach (var w in ws)
                    foreach (var k in new[] { "from", "to" })
                    {
                        string s = (string)w[k];
                        if (!string.IsNullOrEmpty(s) && s != ".") used.Add(s);
                    }
            string RefOf(string end) { int i = end.IndexOf('.'); return i < 0 ? end : end.Substring(0, i); }
            var refs = new HashSet<string>(used.Select(RefOf), StringComparer.OrdinalIgnoreCase);

            // Route sketch end points in the active assembly's coordinates (the sketch is in the
            // route assembly's frame: map it with the route component's transform).
            double[] R = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, T = { 0, 0, 0 };
            try
            {
                if (routeComp.Transform2?.ArrayData is double[] a && a.Length >= 12)
                { R = a.Take(9).ToArray(); T = new[] { a[9], a[10], a[11] }; }
            }
            catch { }
            var pts = new List<double[]>();
            void AddPt(ISketchPoint sp)
            {
                if (sp == null) return;
                pts.Add(new[]
                {
                    sp.X * R[0] + sp.Y * R[3] + sp.Z * R[6] + T[0],
                    sp.X * R[1] + sp.Y * R[4] + sp.Z * R[7] + T[1],
                    sp.X * R[2] + sp.Y * R[5] + sp.Z * R[8] + T[2],
                });
            }
            var sk = RouteSketchOf(routeComp);
            if (sk?.GetSketchSegments() is object[] ss)
                foreach (var s in ss)
                {
                    var seg = s as ISketchSegment;
                    if (seg == null) continue;
                    bool constr = false;
                    try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    if (seg is ISketchLine ln) { AddPt(ln.GetStartPoint2() as ISketchPoint); AddPt(ln.GetEndPoint2() as ISketchPoint); }
                    else if (seg is ISketchArc arc) { AddPt(arc.GetStartPoint2() as ISketchPoint); AddPt(arc.GetEndPoint2() as ISketchPoint); }
                }
            if (sk == null) o["error"] = "route sketch not found";

            var stubs = new JArray();
            var detached = new JArray();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (asm.GetComponents(false) is object[] all)
                foreach (var co in all)
                {
                    var c = co as IComponent2;
                    if (c == null) continue;
                    string rf = null;
                    try { rf = c.ComponentReference; } catch { }
                    if (string.IsNullOrEmpty(rf) || !refs.Contains(rf)) continue;
                    try { if (c.GetSuppression2() == 0) continue; } catch { continue; }
                    if (!(c.GetModelDoc2() is IPartDoc)) continue;
                    var cj = ConnectorPointsWorld(c);
                    if (!(cj?["connection_points"] is JArray cps)) continue;
                    foreach (var cpt in cps)
                    {
                        if (!(cpt is JObject cp) || !(cp["location_mm"] is JArray l)) continue;
                        string pin = (string)cp["pin"] ?? "";
                        string key = rf + "." + pin;
                        // A connector with one connection point for all its wires (no pin set).
                        if (pin == "" && cps.Count == 1)
                        {
                            if (!used.Any(u => RefOf(u).Equals(rf, StringComparison.OrdinalIgnoreCase))) continue;
                            key = rf;
                            foreach (var u in used.Where(u => RefOf(u).Equals(rf, StringComparison.OrdinalIgnoreCase))) found.Add(u);
                        }
                        else if (!used.Contains(key)) continue;
                        else found.Add(key);
                        var loc = new[] { (double)l[0] / 1000.0, (double)l[1] / 1000.0, (double)l[2] / 1000.0 };
                        double best = pts.Count == 0 ? double.NaN : pts.Min(q => Dist(q, loc));
                        JToken off = double.IsNaN(best) ? JValue.CreateNull() : (JToken)Math.Round(best * 1000.0, 3);
                        stubs.Add(new JObject
                        {
                            ["end"] = key,
                            ["component"] = cj["component"],
                            ["location_mm"] = l,
                            ["stub_end_mm"] = cp["stub_end_mm"],
                            ["direction"] = cp["direction"],
                            ["offset_mm"] = off,
                        });
                        if (double.IsNaN(best) || best * 1000.0 > tolMm)
                            detached.Add(new JObject { ["end"] = key, ["component"] = cj["component"], ["offset_mm"] = off });
                    }
                }
            o["tolerance_mm"] = tolMm;
            o["stubs"] = stubs;
            o["detached"] = detached;
            o["detached_count"] = detached.Count;
            var missing = used.Where(u => !found.Contains(u)).OrderBy(u => u).ToList();
            if (missing.Count > 0) o["not_found"] = new JArray(missing);
            return o;
        }
    }
}
