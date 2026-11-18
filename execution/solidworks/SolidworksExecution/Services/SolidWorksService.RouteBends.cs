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

        // Smooth fan-out legs. Each wire leaves its cavity straight along the cavity's exit direction
        // (no kink at the stub end), bends once with leg_radius at a corner c_k, and runs straight to
        // the join, arriving within maxLegDeg of the bundle direction b (the bundle then leaves the
        // join along b). Axial fan (b = mean exit direction): the corner sits just past the stub end,
        // the bend is gentle. Turn fan (b across the exit direction, e.g. a plug facing a wall): the
        // legs go straight out, bend about 90 deg and converge; cavities further upstream along b bend
        // deeper by layerStep, so a leg passing over a downstream cavity runs beyond its corner
        // instead of through its wire (layered wires, as a real bundle forms).
        // Returns the legs [stub end, corner, join] with their radius, and fills join / report.
        private static List<Tuple<int, List<double[]>, double[]>> SmoothFanLegs(
            List<double[]> ends, List<double[]> dirs, double[] b, double[] givenJoin, double r, double maxLegDeg,
            double layerStep, double lead, out double[] join, JObject report, bool? concentricOpt = null, double autoSpan = 0.008,
            bool? convergeOpt = null, double throatRadius = 0, double convergeMaxDeg = 45)
        {
            int n = ends.Count;
            var rk = Enumerable.Repeat(r, n).ToArray();
            bool concentric = false;
            double[] dm = Unit(new[] { dirs.Average(q => q[0]), dirs.Average(q => q[1]), dirs.Average(q => q[2]) });
            b = Unit(b ?? dm);
            bool turn = Dot(b, dm) < Math.Cos(30 * Math.PI / 180);
            if (throatRadius <= 0) throatRadius = Math.Max(0.002, 0.0009 * Math.Sqrt(n));
            if (givenJoin == null && (convergeOpt ?? false))
                return ConvergeFanLegs(ends, dirs, b, r, maxLegDeg, lead, throatRadius, convergeMaxDeg, out join, report);
            double tanMax = Math.Tan(maxLegDeg * Math.PI / 180);
            double Ang(double[] u, double[] v) => Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(Unit(u), Unit(v))))) * 180 / Math.PI;

            // Depth of each corner along its exit direction.
            var s = new double[n];
            if (turn)
            {
                // Rank by position along b: the most downstream cavities bend first (rank 0).
                var proj = ends.Select(e => Dot(e, b)).ToList();
                var levels = new List<double>();
                foreach (var pv in proj.OrderByDescending(x => x))
                    if (levels.Count == 0 || levels.Last() - pv > 5e-4) levels.Add(pv);
                // Concentric unless the cavities spread wide along the turn (a long row turning edgewise
                // would need radii up to its width): auto = span along b <= autoSpan.
                double span = proj.Max() - proj.Min();
                concentric = concentricOpt ?? span <= autoSpan + 1e-9;
                report["span_along_turn_mm"] = Math.Round(span * 1000, 2);
                if (concentric)
                {
                    // Concentric ribbon: every leg starts its bend right after the stub (lead), the
                    // radius growing by the cavity's offset from the inside of the turn, so the wires
                    // turn together and leave the bend side by side instead of running out staggered.
                    double pmax = proj.Max();
                    for (int k = 0; k < n; k++)
                    {
                        rk[k] = r + Math.Max(0, pmax - proj[k]);
                        s[k] = lead + rk[k] * Math.Tan((90 + maxLegDeg) / 2 * Math.PI / 180);
                    }
                }
                else
                {
                    double s0 = r * Math.Tan((90 + maxLegDeg) / 2 * Math.PI / 180) + lead;
                    for (int k = 0; k < n; k++)
                    {
                        int rank = levels.FindIndex(l => Math.Abs(l - proj[k]) <= 5e-4);
                        s[k] = s0 + Math.Max(0, rank) * layerStep;
                    }
                }
            }
            else for (int k = 0; k < n; k++) s[k] = lead;
            bool exact = turn && concentric;

            double[][] corners = new double[n][];
            join = givenJoin;
            for (int iter = 0; iter < (exact ? 12 : 4); iter++)
            {
                for (int k = 0; k < n; k++) corners[k] = Add(ends[k], Scale(dirs[k], s[k]));
                if (givenJoin == null)
                {
                    // On the line through the corners' centre along b, past the most downstream corner,
                    // far enough that every leg arrives within maxLegDeg of b and has room for its bend.
                    double[] mc = { corners.Average(q => q[0]), corners.Average(q => q[1]), corners.Average(q => q[2]) };
                    double front = corners.Max(c => Dot(Sub(c, mc), b));
                    double Worst(double dd)
                    {
                        var jn = Add(mc, Scale(b, front + dd));
                        return corners.Max(c => Ang(Sub(jn, c), b));
                    }
                    double lo = 0.0, hi = 0.3;
                    if (Worst(hi) > maxLegDeg) lo = hi;
                    else for (int it = 0; it < 50; it++) { double mid = (lo + hi) / 2; if (Worst(mid) > maxLegDeg) lo = mid; else hi = mid; }
                    // Room on each leg for its bend before the join (0.5 mm steps).
                    double d = hi;
                    for (int guard = 0; guard < 600; guard++)
                    {
                        var jn = Add(mc, Scale(b, front + d));
                        bool ok = true;
                        for (int k = 0; k < n && ok; k++)
                        {
                            var toJ = Sub(jn, corners[k]);
                            double phi = Ang(dirs[k], toJ);
                            ok = Math.Sqrt(Dot(toJ, toJ)) >= rk[k] * Math.Tan(phi / 2 * Math.PI / 180) + lead;
                        }
                        if (ok) break;
                        d += 0.0005;
                    }
                    join = Add(mc, Scale(b, front + d));
                }
                // Each corner must leave room for its own fillet on the straight out of the cavity.
                bool changed = false;
                for (int k = 0; k < n; k++)
                {
                    double phi = Ang(dirs[k], Sub(join, corners[k]));
                    double t = rk[k] * Math.Tan(phi / 2 * Math.PI / 180);
                    if (exact) { if (Math.Abs(s[k] - (t + lead)) > 1e-7) { s[k] = t + lead; changed = true; } }
                    else if (s[k] < t + lead - 1e-7) { s[k] = t + lead; changed = true; }
                }
                if (!changed) break;
            }

            var legs = new List<Tuple<int, List<double[]>, double[]>>();
            var lj = new JArray();
            double steepest = 0, worstBend = 0, longest = 0, shortest = double.MaxValue;
            for (int k = 0; k < n; k++)
            {
                var c = Add(ends[k], Scale(dirs[k], s[k]));
                var toJ = Sub(join, c);
                if (Math.Sqrt(Dot(toJ, toJ)) < 1e-6) continue;
                double arrive = Ang(toJ, b), bend = Ang(dirs[k], toJ);
                double t = rk[k] * Math.Tan(bend / 2 * Math.PI / 180);
                double len = s[k] + Math.Sqrt(Dot(toJ, toJ));
                steepest = Math.Max(steepest, arrive); worstBend = Math.Max(worstBend, bend); longest = Math.Max(longest, len);
                shortest = Math.Min(shortest, len);
                legs.Add(Tuple.Create(k, new List<double[]> { ends[k], c, join }, new[] { rk[k] }));
                lj.Add(new JObject
                {
                    ["k"] = k,
                    ["straight_out_mm"] = Math.Round(s[k] * 1000, 2),
                    ["bend_starts_mm"] = Math.Round((s[k] - t) * 1000, 2),
                    ["radius_mm"] = Math.Round(rk[k] * 1000, 2),
                    ["bend_deg"] = Math.Round(bend, 1),
                    ["arrival_deg"] = Math.Round(arrive, 1),
                    ["fillet_fits"] = t <= s[k] + 1e-7 && t <= Math.Sqrt(Dot(toJ, toJ)) + 1e-7,
                });
            }
            report["shape"] = turn ? "turn" : "axial";
            report["bundle_direction"] = J3(b, 6);
            report["leg_radius_mm"] = Math.Round(r * 1000, 2);
            report["concentric"] = exact;
            if (exact)
            {
                report["leg_radius_max_mm"] = Math.Round(rk.Max() * 1000, 2);
            }
            report["max_arrival_deg"] = Math.Round(steepest, 1);
            report["max_bend_deg"] = Math.Round(worstBend, 1);
            report["longest_leg_mm"] = Math.Round(longest * 1000, 2);
            report["leg_length_spread_mm"] = Math.Round((longest - shortest) * 1000, 2);
            report["legs"] = lj;
            if (turn)
                report["note"] = "turn fan: wires further from the bundle side run longer by their offset along the turn; " +
                    "for a wide row an axial fan with converge=true (bundle formed inline, then the bundle path turns) keeps the lengths close";
            else if (givenJoin == null && convergeOpt == null)
            {
                // Would two-bend legs into a tight throat form the bundle closer to the plug?
                var probe = new JObject();
                ConvergeFanLegs(ends, dirs, b, r, maxLegDeg, lead, throatRadius, convergeMaxDeg, out double[] cj, probe);
                double[] mc = { ends.Average(q => q[0]), ends.Average(q => q[1]), ends.Average(q => q[2]) };
                double dNow = Dot(Sub(join, mc), b), dConv = Dot(Sub(cj, mc), b);
                if (dConv < dNow - 0.005)
                    report["converge_hint"] = $"converge=true would join {Math.Round(dConv * 1000, 1)} mm out instead of {Math.Round(dNow * 1000, 1)} mm";
            }
            return legs;
        }

        // Converging axial fan: each wire leaves its cavity straight, bends (up to convergeMaxDeg) toward
        // the fan axis, bends back at a throat point (its cavity offset scaled down to throatRadius, so
        // the wires keep their order and form a round bundle) and runs nearly parallel to the join,
        // arriving within maxLegDeg. The bundle forms within a few centimetres of a wide plug instead
        // of offset / tan(maxLegDeg) out; legs close to the axis keep a single gentle bend.
        // Legs [stub end, c1, throat, join] with two radii (or [stub end, c, join]).
        private static List<Tuple<int, List<double[]>, double[]>> ConvergeFanLegs(
            List<double[]> ends, List<double[]> dirs, double[] b, double r, double maxLegDeg, double lead,
            double rho, double convMaxDeg, out double[] join, JObject report)
        {
            int n = ends.Count;
            double Ang(double[] u, double[] v) => Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(Unit(u), Unit(v))))) * 180 / Math.PI;
            double Rad(double deg) => deg * Math.PI / 180;
            double[] m = { ends.Average(q => q[0]), ends.Average(q => q[1]), ends.Average(q => q[2]) };
            // Turn fan (bundle direction across the exit): every wire first turns out of its cavity
            // (an elbow lead + r out), so the throat line runs through the elbows, not the stub ends.
            double[] dmean = Unit(new[] { dirs.Average(q => q[0]), dirs.Average(q => q[1]), dirs.Average(q => q[2]) });
            bool turn = Dot(b, dmean) < Math.Cos(30 * Math.PI / 180);
            double elbow = turn ? lead + r : 0.0;
            m = Add(m, Scale(dmean, elbow));
            var off = new double[n][]; var ax = new double[n];
            for (int k = 0; k < n; k++)
            {
                var d = Sub(Add(ends[k], Scale(dirs[k], elbow)), m); ax[k] = Dot(d, b);
                off[k] = Sub(d, Scale(b, ax[k]));
            }
            double omax = off.Max(o => Math.Sqrt(Dot(o, o)));
            double kappa = omax > rho ? rho / omax : 1.0;
            double front = ax.Max();
            double tanLeg = Math.Tan(Rad(maxLegDeg * 0.98));
            double lastRun = Math.Max(rho, kappa * omax) / tanLeg + lead;   // throat -> join
            // Throat distance a (from the front stub end, along b): the steepest diagonal within
            // convMaxDeg, each fillet with room, then the join lastRun further.
            bool[] two = new bool[n];
            double[][] c1 = new double[n][], th = new double[n][];
            double a = 0.0; double[] jn = null;
            for (int guard = 0; guard < 800; guard++, a += 0.0005)
            {
                jn = Add(m, Scale(b, front + a + lastRun));
                bool ok = true;
                for (int k = 0; k < n && ok; k++)
                {
                    var o = off[k]; double ol = Math.Sqrt(Dot(o, o));
                    // Single bend when the leg straight to the join already arrives within maxLegDeg.
                    double s1 = lead + r * Math.Tan(Rad(Math.Max(0, Ang(dirs[k], Sub(jn, ends[k]))) / 2));
                    var cs = Add(ends[k], Scale(dirs[k], s1));
                    if (Ang(Sub(jn, cs), b) <= maxLegDeg * 0.98 && ol <= rho * 1.5)
                    {
                        two[k] = false; c1[k] = cs; th[k] = null;
                        double phi = Ang(dirs[k], Sub(jn, cs));
                        ok = Math.Sqrt(Dot(Sub(jn, cs), Sub(jn, cs))) >= r * Math.Tan(Rad(phi / 2)) + lead;
                        continue;
                    }
                    two[k] = true;
                    var tp = Add(Add(m, Scale(b, front + a)), Scale(o, kappa));
                    // corner 1 just past the stub end, iterated for its own fillet
                    double s = lead;
                    for (int it = 0; it < 8; it++)
                    {
                        var cc = Add(ends[k], Scale(dirs[k], s));
                        s = lead + r * Math.Tan(Rad(Ang(dirs[k], Sub(tp, cc)) / 2));
                    }
                    var cA = Add(ends[k], Scale(dirs[k], s));
                    var diag = Sub(tp, cA);
                    double b1 = Ang(dirs[k], diag), b2 = Ang(diag, Sub(jn, tp));
                    double t1 = r * Math.Tan(Rad(b1 / 2)), t2 = r * Math.Tan(Rad(b2 / 2));
                    ok = Dot(diag, b) > 0 && Ang(diag, b) <= convMaxDeg + 1e-6
                        && Math.Sqrt(Dot(diag, diag)) >= t1 + t2 + lead
                        && Math.Sqrt(Dot(Sub(jn, tp), Sub(jn, tp))) >= t2 + lead;
                    c1[k] = cA; th[k] = tp;
                }
                if (ok) break;
            }
            join = jn;
            var legs = new List<Tuple<int, List<double[]>, double[]>>();
            var lj = new JArray();
            double steepest = 0, worstBend = 0, longest = 0, shortest = double.MaxValue;
            for (int k = 0; k < n; k++)
            {
                List<double[]> p; double[] rr;
                if (two[k]) { p = new List<double[]> { ends[k], c1[k], th[k], join }; rr = new[] { r, r }; }
                else { p = new List<double[]> { ends[k], c1[k], join }; rr = new[] { r }; }
                double len = 0; for (int i = 1; i < p.Count; i++) len += Math.Sqrt(Dot(Sub(p[i], p[i - 1]), Sub(p[i], p[i - 1])));
                double arrive = Ang(Sub(join, p[p.Count - 2]), b);
                double bend = Ang(dirs[k], Sub(p[2], p[1]));
                double t1 = r * Math.Tan(Rad(bend / 2));
                steepest = Math.Max(steepest, arrive); worstBend = Math.Max(worstBend, bend);
                longest = Math.Max(longest, len); shortest = Math.Min(shortest, len);
                legs.Add(Tuple.Create(k, p, rr));
                lj.Add(new JObject
                {
                    ["k"] = k,
                    ["bends"] = two[k] ? 2 : 1,
                    ["bend_starts_mm"] = Math.Round((Dot(Sub(c1[k], ends[k]), dirs[k]) - t1) * 1000, 2),
                    ["radius_mm"] = Math.Round(r * 1000, 2),
                    ["bend_deg"] = Math.Round(bend, 1),
                    ["arrival_deg"] = Math.Round(arrive, 1),
                    ["length_mm"] = Math.Round(len * 1000, 2),
                });
            }
            report["shape"] = turn ? "turn" : "axial";
            report["converge"] = true;
            report["throat_radius_mm"] = Math.Round(rho * 1000, 2);
            report["join_distance_mm"] = Math.Round(Dot(Sub(join, m), b) * 1000, 2);
            report["bundle_direction"] = J3(b, 6);
            report["leg_radius_mm"] = Math.Round(r * 1000, 2);
            report["max_arrival_deg"] = Math.Round(steepest, 1);
            report["max_bend_deg"] = Math.Round(worstBend, 1);
            report["longest_leg_mm"] = Math.Round(longest * 1000, 2);
            report["leg_length_spread_mm"] = Math.Round((longest - shortest) * 1000, 2);
            report["legs"] = lj;
            return legs;
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
