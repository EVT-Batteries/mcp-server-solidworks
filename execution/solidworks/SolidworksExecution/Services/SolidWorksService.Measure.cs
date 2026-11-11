using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: measure_curves — read-only curve LENGTH measurement of sketches (2D and
    // 3D: lines, arcs, splines, ellipses, parabolas) and sweep paths, in the active part or across every
    // component of the active assembly. Lengths come from ISketchSegment.GetLength / ICurve.GetLength3, so
    // they are true 3D arc lengths (a spline is measured along the curve, not chord to chord).
    // Never modifies a document and never bumps state_version (same pattern as AnalyzeModel).
    public partial class SolidWorksService
    {
        private static readonly Dictionary<int, string> SegmentKinds = new Dictionary<int, string>
        {
            { (int)swSketchSegments_e.swSketchLINE, "line" },
            { (int)swSketchSegments_e.swSketchARC, "arc" },
            { (int)swSketchSegments_e.swSketchELLIPSE, "ellipse" },
            { (int)swSketchSegments_e.swSketchSPLINE, "spline" },
            { (int)swSketchSegments_e.swSketchTEXT, "text" },
            { (int)swSketchSegments_e.swSketchPARABOLA, "parabola" },
        };

        private sealed class MeasureOptions
        {
            public string Name;              // only this sketch / sweep feature (empty = all)
            public bool Include2D;           // also report 2D sketches (default: 3D sketches + sweeps)
            public bool IncludeConstruction; // add construction geometry to the totals
            public bool PerSegment;          // list every segment's kind + length
            public bool IncludeGeometry;     // per segment: 3D start / end / center / spline points
            public bool AsRoute;             // per sketch: route_points + route_bend_radii (create_3d_path input)
            public IMathTransform Frame;     // component -> assembly transform while measuring a component
        }

        public ExecutionResponse MeasureCurves(ToolRequest request)
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
                var opt = new MeasureOptions
                {
                    Name = p?.Value<string>("name") ?? "",
                    Include2D = p?.Value<bool?>("include_2d") ?? false,
                    IncludeConstruction = p?.Value<bool?>("include_construction") ?? false,
                    PerSegment = p?.Value<bool?>("per_segment") ?? false,
                    IncludeGeometry = p?.Value<bool?>("include_geometry") ?? false,
                    AsRoute = p?.Value<bool?>("as_route") ?? false,
                };
                if (opt.IncludeGeometry) opt.PerSegment = true;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                int docType = modelDoc.GetType();
                if (docType != (int)swDocumentTypes_e.swDocPART && docType != (int)swDocumentTypes_e.swDocASSEMBLY)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "measure_curves requires an active part or assembly.");

                var result = new JObject();
                result["document"] = modelDoc.GetTitle();
                result["units"] = "m";
                if (opt.IncludeGeometry || opt.AsRoute)
                    result["frame"] = docType == (int)swDocumentTypes_e.swDocPART
                        ? "part model coordinates"
                        : "assembly coordinates (component transforms applied)";

                if (docType == (int)swDocumentTypes_e.swDocPART)
                {
                    var docJ = MeasureDocument(modelDoc, opt);
                    foreach (var prop in docJ.Properties()) result[prop.Name] = prop.Value;
                }
                else
                {
                    if (p?.Value<bool?>("resolve_lightweight") ?? false)
                    {
                        int light = ResolveAllLightweightComponents(modelDoc as IAssemblyDoc);
                        if (light > 0) result["lightweight_resolved"] = light;
                    }
                    result["root"] = MeasureDocument(modelDoc, opt);
                    result["components"] = MeasureAssemblyComponents(modelDoc as IAssemblyDoc, opt, result);
                }

                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = docType == (int)swDocumentTypes_e.swDocPART ? "PART" : "ASSEMBLY",
                        ActiveSketch = null,
                        Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // One entry per distinct (file, configuration), listing its instances. Suppressed and
        // lightweight components (no loaded model) are reported under `skipped`.
        private JArray MeasureAssemblyComponents(IAssemblyDoc asm, MeasureOptions opt, JObject result)
        {
            var entries = new JArray();
            var byKey = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var skipped = new JArray();
            double totalSweep = 0.0;

            object[] comps = null;
            try { comps = asm?.GetComponents(false) as object[]; } catch { }
            if (comps != null)
            {
                foreach (var o in comps)
                {
                    var comp = o as IComponent2;
                    if (comp == null) continue;

                    string name = comp.Name2;
                    bool suppressed = false;
                    suppressed = SafeSuppression(comp) == 0;   // IsSuppressed() is also true for some lightweight components
                    if (suppressed) { skipped.Add(new JObject { ["name"] = name, ["reason"] = "suppressed" }); continue; }

                    var doc = comp.GetModelDoc2() as IModelDoc2;
                    if (doc == null)
                    {
                        skipped.Add(new JObject
                        {
                            ["name"] = name,
                            ["reason"] = IsLightweightState(SafeSuppression(comp))
                                ? "lightweight (pass resolve_lightweight=true to measure it)" : "not loaded",
                        });
                        continue;
                    }

                    string path = "";
                    try { path = comp.GetPathName(); } catch { }
                    string refConfig = "";
                    try { refConfig = comp.ReferencedConfiguration; } catch { }

                    string key = path + "|" + refConfig;
                    if (byKey.TryGetValue(key, out var existing))
                    {
                        ((JArray)existing["instances"]).Add(name);
                        totalSweep += existing.Value<double?>("sweep_path_length_m") ?? 0.0;
                        continue;
                    }

                    var entry = new JObject();
                    entry["path"] = path;
                    entry["config"] = refConfig;
                    string activeConfig = "";
                    try { activeConfig = (doc.ConfigurationManager?.ActiveConfiguration as IConfiguration)?.Name ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(refConfig) && !string.IsNullOrEmpty(activeConfig) &&
                        !string.Equals(refConfig, activeConfig, StringComparison.OrdinalIgnoreCase))
                        entry["warning"] = $"measured the document's active configuration '{activeConfig}', " +
                                           $"not the referenced '{refConfig}'";
                    entry["instances"] = new JArray(name);

                    try { opt.Frame = (opt.IncludeGeometry || opt.AsRoute) ? comp.Transform2 as IMathTransform : null; } catch { opt.Frame = null; }
                    JObject measured;
                    try { measured = MeasureDocument(doc, opt); }
                    finally { opt.Frame = null; }
                    bool hasContent = ((JArray)measured["sketches"]).Count > 0 || ((JArray)measured["sweeps"]).Count > 0;
                    if (!hasContent) continue;
                    foreach (var prop in measured.Properties()) entry[prop.Name] = prop.Value;

                    totalSweep += entry.Value<double?>("sweep_path_length_m") ?? 0.0;
                    byKey[key] = entry;
                    entries.Add(entry);
                }
            }

            result["total_sweep_path_length_m"] = Math.Round(totalSweep, 6);
            if (skipped.Count > 0) result["skipped"] = skipped;
            return entries;
        }

        // Sketches and sweeps of ONE document (part or assembly-level features).
        private JObject MeasureDocument(IModelDoc2 doc, MeasureOptions opt)
        {
            var sketches = new JArray();
            var sweeps = new JArray();
            double sweepTotal = 0.0;
            var seen = new HashSet<string>();

            foreach (var feat in EnumerateFeatures(doc))
            {
                string fname = feat.Name;
                if (!seen.Add(fname)) continue;
                string type = feat.GetTypeName2();

                if (type == "Sweep" || type == "SweepCut" || type == "SweepThread")
                {
                    if (!string.IsNullOrEmpty(opt.Name) && fname != opt.Name) continue;
                    var sj = MeasureSweep(doc, feat, opt);
                    sweepTotal += sj.Value<double?>("path_length_m") ?? 0.0;
                    sweeps.Add(sj);
                    continue;
                }

                var sketch = SketchOf(feat);
                if (sketch == null) continue;
                if (!string.IsNullOrEmpty(opt.Name) && fname != opt.Name) continue;
                bool is3D = false;
                try { is3D = sketch.Is3D(); } catch { }
                if (!is3D && !opt.Include2D && string.IsNullOrEmpty(opt.Name)) continue;

                var kj = MeasureSketch(sketch, opt);
                var named = new JObject { ["name"] = fname, ["kind"] = is3D ? "3d" : "2d" };
                if (IsFeatureSuppressed(feat)) named["suppressed"] = true;
                foreach (var prop in kj.Properties()) named[prop.Name] = prop.Value;
                sketches.Add(named);
            }

            var outJ = new JObject();
            outJ["sweeps"] = sweeps;
            outJ["sweep_path_length_m"] = Math.Round(sweepTotal, 6);
            outJ["sketches"] = sketches;
            return outJ;
        }

        private JObject MeasureSweep(IModelDoc2 doc, IFeature feat, MeasureOptions opt)
        {
            var sj = new JObject { ["name"] = feat.Name, ["type"] = feat.GetTypeName2() };
            if (IsFeatureSuppressed(feat)) sj["suppressed"] = true;

            object path = null, profile = null;
            var def = feat.GetDefinition() as ISweepFeatureData;
            bool accessed = false;
            try
            {
                if (def != null) accessed = def.AccessSelections(doc, null);
                if (accessed)
                {
                    try { path = def.Path; } catch { }
                    try { profile = def.Profile; } catch { }
                    try
                    {
                        if (def.CircularProfile)
                        {
                            sj["profile"] = "circular";
                            sj["profile_diameter_m"] = Math.Round(def.CircularProfileDiameter, 6);
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                if (accessed) try { def.ReleaseSelectionAccess(); } catch { }
            }

            // Profile: name + diameter when it is a single full circle (a wire / cable cross-section).
            var profSketch = SketchOfObject(profile);
            if (profSketch != null)
            {
                sj["profile"] = FeatureNameOfSketch(profSketch);
                double? dia = SingleCircleDiameter(profSketch);
                if (dia.HasValue) sj["profile_diameter_m"] = Math.Round(dia.Value, 6);
            }

            // Path: a sketch (2D/3D), or model edges / a curve.
            var pathSketch = SketchOfObject(path);
            if (pathSketch != null)
            {
                var kj = MeasureSketch(pathSketch, opt);
                sj["path"] = FeatureNameOfSketch(pathSketch);
                sj["path_length_m"] = kj["length_m"];
                sj["path_segments"] = kj["segments"];
                if (opt.PerSegment && kj["per_segment"] != null) sj["per_segment"] = kj["per_segment"];
                foreach (var key in new[] { "route_points", "route_bend_radii", "route_warning" })
                    if (kj[key] != null) sj[key] = kj[key];
                return sj;
            }

            var edges = new List<IEdge>();
            if (path is IEdge e1) edges.Add(e1);
            else if (path is object[] arr) foreach (var o in arr) if (o is IEdge e) edges.Add(e);
            if (edges.Count > 0)
            {
                double len = 0.0;
                foreach (var e in edges) len += EdgeLength(e);
                sj["path"] = "edges";
                sj["path_edge_count"] = edges.Count;
                sj["path_length_m"] = Math.Round(len, 6);
                return sj;
            }

            if (path is IFeature pf)
            {
                // e.g. a composite / projected / helix curve feature: measure through its edges is not
                // possible from the feature itself, so report what it is.
                sj["path"] = pf.Name;
                sj["path_type"] = pf.GetTypeName2();
            }
            sj["warning"] = path == null
                ? "sweep path could not be read (AccessSelections failed)"
                : "sweep path is not a sketch or edges; length not measured";
            return sj;
        }

        // Length of every non-construction segment of a sketch (true 3D length for a 3D sketch).
        private JObject MeasureSketch(ISketch sketch, MeasureOptions opt)
        {
            double total = 0.0, construction = 0.0;
            var counts = new Dictionary<string, int>();
            var lengthsByKind = new Dictionary<string, double>();
            var perSeg = opt.PerSegment ? new JArray() : null;

            object[] segs = null;
            try { segs = sketch.GetSketchSegments() as object[]; } catch { }
            if (segs != null)
            {
                foreach (var o in segs)
                {
                    var seg = o as ISketchSegment;
                    if (seg == null) continue;
                    int t = seg.GetType();
                    string kind = SegmentKinds.TryGetValue(t, out var k) ? k : "type" + t;
                    if (kind == "text") continue;

                    bool isConstr = false;
                    try { isConstr = seg.ConstructionGeometry; } catch { }
                    double len = 0.0;
                    try { len = seg.GetLength(); } catch { }

                    if (perSeg != null)
                    {
                        var sj = new JObject
                        {
                            ["kind"] = kind,
                            ["length_m"] = Math.Round(len, 6),
                            ["construction"] = isConstr,
                        };
                        if (opt.IncludeGeometry) AddSegmentGeometry(sj, sketch, seg, opt.Frame);
                        perSeg.Add(sj);
                    }

                    if (isConstr && !opt.IncludeConstruction) { construction += len; continue; }
                    total += len;
                    counts[kind] = (counts.TryGetValue(kind, out var c) ? c : 0) + 1;
                    lengthsByKind[kind] = (lengthsByKind.TryGetValue(kind, out var l) ? l : 0.0) + len;
                }
            }

            var j = new JObject();
            j["length_m"] = Math.Round(total, 6);
            var segJ = new JObject();
            foreach (var kv in counts)
                segJ[kv.Key] = new JObject { ["count"] = kv.Value, ["length_m"] = Math.Round(lengthsByKind[kv.Key], 6) };
            j["segments"] = segJ;
            if (!opt.IncludeConstruction && construction > 0) j["construction_length_m"] = Math.Round(construction, 6);
            if (perSeg != null) j["per_segment"] = perSeg;
            if (opt.AsRoute) AddRoute(j, sketch, segs, opt.Frame);
            return j;
        }

        // Route form of a line / arc chain: the corner points (where each arc's two tangents meet, and
        // the junctions of lines meeting without an arc) and one bend radius per corner (0 = sharp),
        // i.e. exactly what create_3d_path(points, bend_radii) takes, so a route can be read, edited
        // and redrawn. Construction geometry is ignored; splines or a broken chain give route_warning.
        private void AddRoute(JObject j, ISketch sketch, object[] segs, IMathTransform frame)
        {
            var pieces = new List<RoutePiece>();
            bool unsupported = false;
            IMathTransform sketchToModel = null;
            bool is3D = false;
            try { is3D = sketch.Is3D(); } catch { }
            if (!is3D) { try { sketchToModel = sketch.ModelToSketchTransform?.Inverse() as IMathTransform; } catch { } }
            if (segs != null)
                foreach (var o in segs)
                {
                    var seg = o as ISketchSegment;
                    if (seg == null) continue;
                    bool constr = false;
                    try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    if (seg is ISketchLine ln)
                        pieces.Add(new RoutePiece
                        {
                            S = Pt(PointArray(ln.GetStartPoint2() as ISketchPoint, sketchToModel, frame)),
                            E = Pt(PointArray(ln.GetEndPoint2() as ISketchPoint, sketchToModel, frame)),
                        });
                    else if (seg is ISketchArc ar)
                        pieces.Add(new RoutePiece
                        {
                            S = Pt(PointArray(ar.GetStartPoint2() as ISketchPoint, sketchToModel, frame)),
                            E = Pt(PointArray(ar.GetEndPoint2() as ISketchPoint, sketchToModel, frame)),
                            C = Pt(PointArray(ar.GetCenterPoint2() as ISketchPoint, sketchToModel, frame)),
                            R = ar.GetRadius(),
                        });
                    else if (seg.GetType() != (int)swSketchSegments_e.swSketchTEXT) unsupported = true;
                }
            if (pieces.Count == 0 || pieces.Exists(pc => pc.S == null || pc.E == null)) return;

            string warning = unsupported ? "the sketch has segments other than lines and arcs (left out)" : null;
            var chain = OrderChain(pieces, out bool broken);
            if (broken) warning = (warning == null ? "" : warning + "; ") + "the segments do not form one connected chain";

            var points = new JArray();
            var radii = new JArray();
            points.Add(RoundPt(chain[0].S));
            for (int i = 0; i < chain.Count; i++)
            {
                var pc = chain[i];
                if (pc.C != null)
                {
                    double[] a = V(pc.S, pc.C), b = V(pc.E, pc.C);
                    double r = pc.R, cosPhi = Math.Max(-1, Math.Min(1, Dot3(a, b) / (r * r)));
                    double phi = Math.Acos(cosPhi);
                    double[] n = Unit3(Cross(a, b));
                    if (phi > Math.PI - 1e-4 || Norm(n) < 0.5)
                    {
                        warning = (warning == null ? "" : warning + "; ") + $"arc {i} spans ~180°: its corner is undefined";
                        points.Add(RoundPt(pc.E)); radii.Add(0.0);
                        continue;
                    }
                    double[] t = Unit3(Cross(n, a));
                    double tl = r * Math.Tan(phi / 2.0);
                    points.Add(RoundPt(new[] { pc.S[0] + t[0] * tl, pc.S[1] + t[1] * tl, pc.S[2] + t[2] * tl }));
                    radii.Add(Math.Round(r, 6));
                }
                else if (i + 1 < chain.Count && chain[i + 1].C == null)
                {
                    // two lines meeting without an arc: a sharp corner, unless they are collinear
                    double[] d1 = Unit3(V(pc.E, pc.S)), d2 = Unit3(V(chain[i + 1].E, chain[i + 1].S));
                    if (Dot3(d1, d2) < 1 - 1e-9) { points.Add(RoundPt(pc.E)); radii.Add(0.0); }
                }
            }
            points.Add(RoundPt(chain[chain.Count - 1].E));
            j["route_points"] = points;
            j["route_bend_radii"] = radii;
            if (warning != null) j["route_warning"] = warning;
        }

        private sealed class RoutePiece { public double[] S, E, C; public double R; }

        // Orders the pieces into one chain from a free end, flipping pieces as needed.
        private static List<RoutePiece> OrderChain(List<RoutePiece> pieces, out bool broken)
        {
            const double tol = 1e-6;
            bool Same(double[] p, double[] q) => Norm(V(p, q)) < tol;
            int Degree(double[] p) { int d = 0; foreach (var pc in pieces) { if (Same(pc.S, p)) d++; if (Same(pc.E, p)) d++; } return d; }

            var left = new List<RoutePiece>(pieces);
            RoutePiece first = left.Find(pc => Degree(pc.S) == 1) ?? left.Find(pc => Degree(pc.E) == 1) ?? left[0];
            if (Degree(first.S) != 1 && Degree(first.E) == 1) Flip(first);
            var chain = new List<RoutePiece> { first };
            left.Remove(first);
            while (left.Count > 0)
            {
                var end = chain[chain.Count - 1].E;
                var next = left.Find(pc => Same(pc.S, end) || Same(pc.E, end));
                if (next == null) break;
                if (!Same(next.S, end)) Flip(next);
                chain.Add(next);
                left.Remove(next);
            }
            broken = left.Count > 0;
            return chain;
        }

        private static void Flip(RoutePiece pc) { var t = pc.S; pc.S = pc.E; pc.E = t; }
        private static double[] Pt(JArray a) => a == null ? null : new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() };
        private static JArray RoundPt(double[] p) => new JArray(Math.Round(p[0], 6), Math.Round(p[1], 6), Math.Round(p[2], 6));
        private static double[] V(double[] p, double[] q) => new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] };
        private static double Dot3(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Norm(double[] a) => Math.Sqrt(Dot3(a, a));
        private static double[] Unit3(double[] a) { double l = Norm(a); return l < 1e-15 ? a : new[] { a[0] / l, a[1] / l, a[2] / l }; }
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        // Segment geometry in model coordinates (a 2D sketch's points are mapped out of the sketch
        // plane), then into assembly coordinates when `frame` (a component transform) is given.
        private void AddSegmentGeometry(JObject sj, ISketch sketch, ISketchSegment seg, IMathTransform frame)
        {
            try
            {
                IMathTransform sketchToModel = null;
                bool is3D = false;
                try { is3D = sketch.Is3D(); } catch { }
                if (!is3D)
                {
                    try { sketchToModel = sketch.ModelToSketchTransform?.Inverse() as IMathTransform; } catch { }
                }

                if (seg is ISketchLine line)
                {
                    sj["start"] = PointArray(line.GetStartPoint2() as ISketchPoint, sketchToModel, frame);
                    sj["end"] = PointArray(line.GetEndPoint2() as ISketchPoint, sketchToModel, frame);
                }
                else if (seg is ISketchArc arc)
                {
                    sj["start"] = PointArray(arc.GetStartPoint2() as ISketchPoint, sketchToModel, frame);
                    sj["end"] = PointArray(arc.GetEndPoint2() as ISketchPoint, sketchToModel, frame);
                    sj["center"] = PointArray(arc.GetCenterPoint2() as ISketchPoint, sketchToModel, frame);
                    sj["radius"] = Math.Round(arc.GetRadius(), 6);
                }
                else if (seg is ISketchSpline spline)
                {
                    var pts = new JArray();
                    if (spline.GetPoints2() is object[] arr)
                        foreach (var o in arr) pts.Add(PointArray(o as ISketchPoint, sketchToModel, frame));
                    sj["points"] = pts;
                    if (pts.Count > 0) { sj["start"] = pts[0]; sj["end"] = pts[pts.Count - 1]; }
                }
            }
            catch { /* geometry is best effort; the length is already recorded */ }
        }

        private JArray PointArray(ISketchPoint sp, IMathTransform sketchToModel, IMathTransform frame)
        {
            if (sp == null) return null;
            double[] v = { sp.X, sp.Y, sp.Z };
            if (sketchToModel != null || frame != null)
            {
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                var mp = mu?.CreatePoint(v) as IMathPoint;
                if (mp != null)
                {
                    if (sketchToModel != null) mp = mp.MultiplyTransform(sketchToModel) as IMathPoint;
                    if (frame != null) mp = mp.MultiplyTransform(frame) as IMathPoint;
                    if (mp?.ArrayData is double[] d && d.Length >= 3) v = new[] { d[0], d[1], d[2] };
                }
            }
            return new JArray(Math.Round(v[0], 6), Math.Round(v[1], 6), Math.Round(v[2], 6));
        }

        private static double EdgeLength(IEdge edge)
        {
            try
            {
                var curve = edge.GetCurve() as ICurve;
                var cp = edge.GetCurveParams3() as ICurveParamData;
                if (curve == null || cp == null) return 0.0;
                return curve.GetLength3(cp.UMinValue, cp.UMaxValue);
            }
            catch { return 0.0; }
        }

        private static double? SingleCircleDiameter(ISketch sketch)
        {
            try
            {
                var segs = sketch.GetSketchSegments() as object[];
                ISketchArc only = null;
                int real = 0;
                if (segs == null) return null;
                foreach (var o in segs)
                {
                    var seg = o as ISketchSegment;
                    if (seg == null || seg.ConstructionGeometry) continue;
                    real++;
                    only = seg as ISketchArc;
                }
                if (real != 1 || only == null || only.IsCircle() != 1) return null;
                return 2.0 * only.GetRadius();
            }
            catch { return null; }
        }

        private static ISketch SketchOf(IFeature feat)
        {
            string t = feat.GetTypeName2();
            if (t != "ProfileFeature" && t != "3DProfileFeature") return null;
            try { return feat.GetSpecificFeature2() as ISketch; } catch { return null; }
        }

        private static ISketch SketchOfObject(object o)
        {
            if (o is ISketch s) return s;
            if (o is IFeature f) return SketchOf(f);
            return null;
        }

        private static string FeatureNameOfSketch(ISketch sketch)
        {
            // A SolidWorks Sketch object can be cast to the Feature that owns it.
            try { return (sketch as IFeature)?.Name ?? "?"; }
            catch { return "?"; }
        }

        private static bool IsFeatureSuppressed(IFeature feat)
        {
            try
            {
                var flags = feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
                return flags != null && flags.Length > 0 && flags[0];
            }
            catch { return false; }
        }

        // Top-level features followed by their sub-features (absorbed sketches), in tree order.
        private static IEnumerable<IFeature> EnumerateFeatures(IModelDoc2 doc)
        {
            var feat = doc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                yield return feat;
                var sub = feat.GetFirstSubFeature() as IFeature;
                int subGuard = 0;
                while (sub != null && subGuard++ < 2000)
                {
                    yield return sub;
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                feat = feat.GetNextFeature() as IFeature;
            }
        }
    }
}
