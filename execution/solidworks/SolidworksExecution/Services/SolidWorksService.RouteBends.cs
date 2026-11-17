using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.SWRoutingLib;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: bends of the wires of an electrical route that SOLIDWORKS does not
    // check. IWire.GetRouteErrors (violates_bend_radius) compares only the ARCS of the route sketch
    // with the wire's MinBendRadius. Where two route segments meet (a connector stub end, a fan-out
    // join, a breakout, one bundle continuing into the next, e.g. where a covering starts) the
    // sketch has a node, not an arc: a wire passing there turns with a radius of zero, whatever the
    // angle, and nothing reports it. The same holds for corners drawn without a fillet inside a
    // segment.
    //   kink of a wire at a node = the angle between the direction it arrives in and the direction
    //   it leaves in (0 = straight through).
    // - WireBends (routing_status / route_path after routing): the exact kink of every wire at every
    //   node of its own path, from its route segments (IWire.GetRouteSegmentIDs).
    // - JunctionKinks (route_path before drawing): for each end of a polyline that lands on a node,
    //   the smallest kink a wire can have there (against the straightest other segment at that node).
    public partial class SolidWorksService
    {
        private const double NodeTolerance = 2e-5;   // m: route points closer than this are one node

        // Kink (deg) between two segment ends meeting at a node, each tangent pointing away from the
        // node into its own segment: 0 when the second continues the first straight on.
        private static double KinkDeg(double[] tA, double[] tB) =>
            Math.Acos(Math.Max(-1.0, Math.Min(1.0, -Dot(tA, tB)))) * 180.0 / Math.PI;

        // The two ends of a sketch line or arc, each with the unit tangent pointing from that end into
        // the segment. Arcs: the part of the chord perpendicular to the radius (route fillets sweep
        // less than 180 deg). Null for other segment types.
        private static List<Tuple<double[], double[]>> SegmentEnds(ISketchSegment seg)
        {
            double[] P(ISketchPoint sp) => sp == null ? null : new[] { sp.X, sp.Y, sp.Z };
            if (seg is ISketchLine ln)
            {
                var a = P(ln.GetStartPoint2() as ISketchPoint);
                var b = P(ln.GetEndPoint2() as ISketchPoint);
                if (a == null || b == null || Dist(a, b) < 1e-12) return null;
                return new List<Tuple<double[], double[]>>
                {
                    Tuple.Create(a, Unit(Sub(b, a))),
                    Tuple.Create(b, Unit(Sub(a, b))),
                };
            }
            if (seg is ISketchArc arc)
            {
                var a = P(arc.GetStartPoint2() as ISketchPoint);
                var b = P(arc.GetEndPoint2() as ISketchPoint);
                var c = P(arc.GetCenterPoint2() as ISketchPoint);
                if (a == null || b == null || c == null) return null;
                double[] Tan(double[] p, double[] q)
                {
                    double[] rad = Unit(Sub(p, c));
                    double[] chord = Sub(q, p);
                    return Unit(Sub(chord, Scale(rad, Dot(chord, rad))));
                }
                return new List<Tuple<double[], double[]>> { Tuple.Create(a, Tan(a, b)), Tuple.Create(b, Tan(b, a)) };
            }
            return null;
        }

        private static List<List<int>> GroupPoints(List<double[]> pts, double tol)
        {
            var groups = new List<List<int>>();
            var reps = new List<double[]>();
            for (int i = 0; i < pts.Count; i++)
            {
                int g = -1;
                for (int k = 0; k < reps.Count; k++) if (Dist(reps[k], pts[i]) <= tol) { g = k; break; }
                if (g < 0) { reps.Add(pts[i]); groups.Add(new List<int> { i }); }
                else groups[g].Add(i);
            }
            return groups;
        }

        // One route segment as a chain of sketch lines / arcs: its terminal ends (point + tangent into
        // the segment), the sharp joints inside it and its smallest arc radius.
        private sealed class RouteChain
        {
            public int Id;
            public List<Tuple<double[], double[]>> Terminals = new List<Tuple<double[], double[]>>();
            public List<Tuple<double[], double>> Joints = new List<Tuple<double[], double>>();   // point, kink deg
            public double MinArcRadius = double.MaxValue;
            public int Unsupported;
        }

        private static RouteChain ChainOf(int id, object sketchSegments)
        {
            var ch = new RouteChain { Id = id };
            var ends = new List<Tuple<double[], double[]>>();
            if (sketchSegments is object[] ss)
                foreach (var o in ss)
                {
                    var seg = o as ISketchSegment;
                    if (seg == null) continue;
                    if (seg is ISketchArc arc) { try { ch.MinArcRadius = Math.Min(ch.MinArcRadius, arc.GetRadius()); } catch { } }
                    var e = SegmentEnds(seg);
                    if (e == null) { ch.Unsupported++; continue; }
                    ends.AddRange(e);
                }
            foreach (var g in GroupPoints(ends.Select(e => e.Item1).ToList(), NodeTolerance))
            {
                if (g.Count == 1) ch.Terminals.Add(ends[g[0]]);
                else if (g.Count == 2) ch.Joints.Add(Tuple.Create(ends[g[0]].Item1, KinkDeg(ends[g[0]].Item2, ends[g[1]].Item2)));
            }
            return ch;
        }

        // Per wire: every node of its path (a joint inside one of its route segments, or the point
        // where two of its segments meet) with the kink there; the wires and nodes whose kink exceeds
        // maxKinkDeg. Must run while the route component is edited (IElectricalRoute readable).
        // map: route-sketch point -> assembly coordinates (for at_mm).
        private static JObject WireBends(IElectricalRoute er, IEnumerable<IWire> wires, double maxKinkDeg, Func<double[], double[]> map)
        {
            var chains = new Dictionary<int, RouteChain>();
            RouteChain Chain(int id)
            {
                if (!chains.TryGetValue(id, out var c))
                {
                    object ss = null;
                    try { ss = er.GetSketchSegments(id); } catch { }
                    c = ChainOf(id, ss);
                    chains[id] = c;
                }
                return c;
            }
            JArray At(double[] p) => J3(Scale(map(p), 1000), 2);

            var perWire = new JObject();
            var nodes = new Dictionary<string, JObject>();   // worst kinks by location, all wires
            foreach (var w in wires)
            {
                string name = SafeStr(() => w.UserName);
                var ids = SegmentIdsOf(w);
                if (ids.Length == 0) continue;
                var bends = new List<Tuple<double[], double, string>>();   // point, kink, kind
                double minR = double.MaxValue;
                var terms = new List<Tuple<double[], double[], int>>();
                foreach (int id in ids.Distinct())
                {
                    var c = Chain(id);
                    minR = Math.Min(minR, c.MinArcRadius);
                    foreach (var j in c.Joints) bends.Add(Tuple.Create(j.Item1, j.Item2, "corner in segment " + id));
                    foreach (var t in c.Terminals) terms.Add(Tuple.Create(t.Item1, t.Item2, id));
                }
                foreach (var g in GroupPoints(terms.Select(t => t.Item1).ToList(), NodeTolerance))
                {
                    if (g.Count != 2) continue;   // a path end (connector) or not a simple path
                    var a = terms[g[0]]; var b = terms[g[1]];
                    bends.Add(Tuple.Create(a.Item1, KinkDeg(a.Item2, b.Item2), $"junction of segments {a.Item3} and {b.Item3}"));
                }
                double worst = bends.Count > 0 ? bends.Max(b => b.Item2) : 0;
                var sharp = new JArray();
                foreach (var b in bends.Where(b => b.Item2 > maxKinkDeg).OrderByDescending(b => b.Item2))
                {
                    var at = At(b.Item1);
                    sharp.Add(new JObject { ["at_mm"] = at, ["kink_deg"] = Math.Round(b.Item2, 1), ["where"] = b.Item3 });
                    string key = at.ToString(Newtonsoft.Json.Formatting.None);
                    if (!nodes.TryGetValue(key, out var nj))
                        nodes[key] = nj = new JObject { ["at_mm"] = at, ["kink_deg"] = 0.0, ["where"] = b.Item3, ["wires"] = new JArray() };
                    if (b.Item2 > (double)nj["kink_deg"]) nj["kink_deg"] = Math.Round(b.Item2, 1);
                    ((JArray)nj["wires"]).Add(name);
                }
                var wj = new JObject { ["max_kink_deg"] = Math.Round(worst, 1) };
                if (minR < double.MaxValue) wj["min_arc_radius_mm"] = Math.Round(minR * 1000, 2);
                if (sharp.Count > 0) wj["sharp_bends"] = sharp;
                perWire[name] = wj;
            }
            var list = new JArray(nodes.Values.OrderByDescending(n => (double)n["kink_deg"]));
            int unsupported = chains.Values.Sum(c => c.Unsupported);
            var o = new JObject
            {
                ["max_kink_deg_allowed"] = maxKinkDeg,
                ["sharp_bend_nodes"] = list.Count,
                ["sharp_bends"] = list,
                ["per_wire"] = perWire,
                ["note"] = "SOLIDWORKS violates_bend_radius checks arcs only; a kink is a node where the wire turns with no arc (stub end, fan-out join, breakout, bundle-to-bundle). Fix: lead each bundle out of a node within max_kink_deg of the segment it continues, then turn with a filleted corner.",
            };
            if (unsupported > 0) o["unsupported_sketch_segments"] = unsupported;
            return o;
        }

        // route_path, before drawing: for each end of each polyline of the call that lands on a node
        // (a route point, a stub end, or an end of another polyline of the call), the smallest kink a
        // wire can have there: against the straightest other segment at that node. Also every corner
        // drawn sharp (radius 0) inside a polyline. Returns the report; violations = those over max.
        private static JArray JunctionKinks(ISketch sk, List<Tuple<string, List<double[]>, double[]>> jobs,
                                            double maxKinkDeg, List<string> violations)
        {
            var pts = new List<double[]>(); var tans = new List<double[]>(); var labels = new List<string>(); var own = new List<int>();
            if (sk?.GetSketchSegments() is object[] ss)
                foreach (var o in ss)
                {
                    var seg = o as ISketchSegment;
                    if (seg == null) continue;
                    bool constr = false;
                    try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    var e = SegmentEnds(seg);
                    if (e == null) continue;
                    foreach (var x in e) { pts.Add(x.Item1); tans.Add(x.Item2); labels.Add("route"); own.Add(-1); }
                }
            double[] LegDir(List<double[]> p, bool atStart)
            {
                if (atStart) { for (int i = 1; i < p.Count; i++) if (Dist(p[i], p[0]) > 1e-9) return Unit(Sub(p[i], p[0])); }
                else { for (int i = p.Count - 2; i >= 0; i--) if (Dist(p[i], p[p.Count - 1]) > 1e-9) return Unit(Sub(p[i], p[p.Count - 1])); }
                return null;
            }
            var jobEnds = new List<int>();
            for (int k = 0; k < jobs.Count; k++)
            {
                var p = jobs[k].Item2;
                if (p.Count < 2) continue;
                foreach (bool atStart in new[] { true, false })
                {
                    var t = LegDir(p, atStart);
                    if (t == null) continue;
                    jobEnds.Add(pts.Count);
                    pts.Add(atStart ? p[0] : p[p.Count - 1]); tans.Add(t);
                    labels.Add(jobs[k].Item1 + (atStart ? " start" : " end")); own.Add(k);
                }
            }
            var report = new JArray();
            var groups = GroupPoints(pts, NodeTolerance);
            foreach (var g in groups)
                foreach (int i in g.Where(jobEnds.Contains))
                {
                    double best = double.MaxValue; string partner = null;
                    // Sibling legs of the same fan-out converge on its join: a wire never passes
                    // from one into the other, so they are not partners.
                    string fanOf = FanPrefix(labels[i]);
                    foreach (int j in g)
                    {
                        if (j == i || (fanOf != null && FanPrefix(labels[j]) == fanOf)) continue;
                        double kd = KinkDeg(tans[i], tans[j]);
                        if (kd < best) { best = kd; partner = labels[j]; }
                    }
                    if (partner == null) continue;   // a new free node: nothing to continue from yet
                    var rj = new JObject
                    {
                        ["path"] = labels[i],
                        ["at_mm"] = J3(Scale(pts[i], 1000), 2),
                        ["kink_deg"] = Math.Round(best, 1),
                        ["straightest"] = partner,
                        ["segments_at_node"] = g.Count,
                    };
                    report.Add(rj);
                    if (best > maxKinkDeg)
                        violations.Add($"{labels[i]} at [{string.Join(", ", ((JArray)rj["at_mm"]).Select(x => (double)x))}] mm leaves the node {Math.Round(best, 1)} deg off its straightest neighbour ({partner})");
                }
            // Corners drawn sharp inside a polyline.
            foreach (var jb in jobs)
            {
                var p = jb.Item2; var r = jb.Item3;
                for (int i = 1; i < p.Count - 1; i++)
                {
                    if (r[i - 1] > 0) continue;
                    var u = Sub(p[i], p[i - 1]); var v = Sub(p[i + 1], p[i]);
                    if (Dot(u, u) < 1e-18 || Dot(v, v) < 1e-18) continue;
                    double defl = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(Unit(u), Unit(v))))) * 180.0 / Math.PI;
                    if (defl > maxKinkDeg)
                        violations.Add($"{jb.Item1} corner {i} deflects {Math.Round(defl, 1)} deg with no bend radius");
                }
            }
            return report;
        }

        // "fan_out <connector>" for a fan-out leg label ("fan_out <connector> pin <pin> start"), else null.
        private static string FanPrefix(string label)
        {
            if (label == null || !label.StartsWith("fan_out ")) return null;
            int k = label.LastIndexOf(" pin ", StringComparison.Ordinal);
            return k > 0 ? label.Substring(0, k) : null;
        }

        // Join distance along the mean exit direction so that every fan-out leg leaves its stub within
        // maxDeg of the stub's direction (bisection: the angles fall as the join moves out).
        private static double FanJoinDistance(List<double[]> ends, List<double[]> dirs, double maxDeg, out double reached)
        {
            double[] m = { ends.Average(q => q[0]), ends.Average(q => q[1]), ends.Average(q => q[2]) };
            double[] dm = Unit(new[] { dirs.Average(q => q[0]), dirs.Average(q => q[1]), dirs.Average(q => q[2]) });
            double MaxAngle(double d)
            {
                var join = Add(m, Scale(dm, d));
                double worst = 0;
                for (int k = 0; k < ends.Count; k++)
                {
                    var leg = Sub(join, ends[k]);
                    if (Dot(leg, leg) < 1e-18) continue;
                    worst = Math.Max(worst, Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(Unit(leg), Unit(dirs[k]))))) * 180 / Math.PI);
                }
                return worst;
            }
            double lo = 0.002, hi = 0.25;
            if (MaxAngle(lo) <= maxDeg) { reached = MaxAngle(lo); return lo; }
            if (MaxAngle(hi) > maxDeg) { reached = MaxAngle(hi); return hi; }
            for (int it = 0; it < 40; it++)
            {
                double mid = (lo + hi) / 2;
                if (MaxAngle(mid) > maxDeg) lo = mid; else hi = mid;
            }
            reached = MaxAngle(hi);
            return hi;
        }
    }
}
