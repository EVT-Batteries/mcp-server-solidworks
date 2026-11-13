using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // check_path_clearance: is a candidate path free, before a route or a bundle part is drawn on it?
    // Builds the path(s) as circular-profile sweeps in a temporary part, inserts it at the origin of
    // the active assembly, runs the same interference / clearance check as check_interference on it,
    // maps every hit to the path leg it lies on, then deletes the component and the temporary file.
    public partial class SolidWorksService
    {
        private class ProbePath
        {
            public List<double[]> Points;
            public double[] Radii;
            public double Diameter;
        }

        public ExecutionResponse CheckPathClearance(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);
            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");
            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            IModelDoc2 asmDoc = null;
            IModelDoc2 probeDoc = null;
            IComponent2 probeComp = null;
            string probePath = null;
            bool keep = false;
            var cleanup = new JArray();
            try
            {
                var p = request.Params as JObject;
                double clearance = p?.Value<double?>("clearance") ?? 0.0;
                int limit = p?.Value<int?>("limit") ?? 50;
                bool includeHidden = p?.Value<bool?>("include_hidden") ?? false;
                bool ignoreSameParent = p?.Value<bool?>("ignore_same_parent") ?? false;
                keep = p?.Value<bool?>("keep_probe") ?? false;
                var ignore = ParseNameList(p?["ignore"]);

                // One path (points / bend_radii / bend_radius / diameter) or several (paths: a JSON
                // list of {points, bend_radii, bend_radius, diameter}), e.g. a trunk and its branches.
                var paths = new List<ProbePath>();
                string perr = ParseProbePaths(p, paths);
                if (perr != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", perr);
                if (clearance < 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "clearance must be >= 0 (meters).");

                asmDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = asmDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "check_path_clearance needs the assembly to check against as the active document (the points are in its coordinates).");
                string asmTitle = asmDoc.GetTitle();
                bool asmWasDirty = false;
                try { asmWasDirty = asmDoc.GetSaveFlag(); } catch { }

                // 1. The probe part: one sweep per path, in assembly coordinates.
                string template = _solidWorks.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                if (string.IsNullOrEmpty(template) || !File.Exists(template))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "TEMPLATE_NOT_FOUND", "No default part template (Tools > Options > Default Templates).");
                probeDoc = _solidWorks.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (probeDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CREATION_FAILED", "Could not create the temporary probe part.");
                var buildWarnings = new JArray();
                var expected = new List<double>();
                for (int i = 0; i < paths.Count; i++)
                {
                    string err = BuildProbeSweep(probeDoc, paths[i], i, buildWarnings, out double len);
                    if (err != null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "PROBE_FAILED", $"path {i}: {err}");
                    expected.Add(len);
                }
                string dir = Path.Combine(Path.GetTempPath(), "solidpilot_probe");
                Directory.CreateDirectory(dir);
                // SolidWorks keeps a removed component's model in memory, so the previous probe file
                // usually stays locked until a later call: delete the leftovers now.
                foreach (var old in Directory.GetFiles(dir, "path_probe_*.SLDPRT"))
                    try { File.Delete(old); } catch { }
                probePath = Path.Combine(dir, "path_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".SLDPRT");
                int se = 0, sw = 0;
                if (!probeDoc.Extension.SaveAs3(probePath, 0 /* current version */, 1 /* silent */, null, null, ref se, ref sw))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "PROBE_FAILED", $"Could not save the probe part (errors {se}).");

                // 2. Insert it at the assembly origin (AddComponent5 centres the bounding box: set the
                // transform to the identity afterwards).
                int actErr = 0;
                _solidWorks.ActivateDoc3(asmTitle, false, 1 /* swDontRebuildActiveDoc */, ref actErr);
                probeComp = asm.AddComponent5(probePath, 0, "", false, "", 0, 0, 0);
                if (probeComp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "PROBE_FAILED", "AddComponent5 returned null for the probe part.");
                var mu = _solidWorks.IGetMathUtility();
                probeComp.Transform2 = mu.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }) as MathTransform;
                string probeName = probeComp.Name2;

                // 3. The check itself: check_interference on the probe component.
                var ci = new JObject
                {
                    ["components"] = new JArray(probeName),
                    ["clearance"] = clearance,
                    ["compact"] = true,
                    ["include_hidden"] = includeHidden,
                    ["ignore_same_parent"] = ignoreSameParent,
                    ["limit"] = 500,
                };
                var inner = CheckInterference(new ToolRequest
                {
                    OperationId = Guid.NewGuid().ToString(),
                    Tool = "check_interference",
                    StateVersion = _guard.GetCurrentStateVersion(),
                    Params = ci,
                });
                if (inner.Status != "COMPLETED")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        inner.Error?.Code ?? "CHECK_FAILED", "Interference check on the probe failed: " + inner.Error?.Message);
                var ir = JObject.Parse(inner.CadState.Features[0]);

                // 4. Hits mapped onto the path: the leg nearest to the centre of each overlap box.
                var hits = new List<JObject>();
                int ignoredCount = 0;
                if (ir["interferences"] is JArray ints)
                    foreach (var t in ints)
                    {
                        var other = (t["components"] as JArray)?.Select(x => (string)x)
                            .FirstOrDefault(x => !x.StartsWith(probeName, StringComparison.OrdinalIgnoreCase)) ?? "?";
                        if (ignore.Any(g => other.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0)) { ignoredCount++; continue; }
                        var h = new JObject { ["component"] = other, ["volume_mm3"] = t["volume_mm3"] };
                        if (t["box_min"] is JArray bmin && t["box_max"] is JArray bmax)
                        {
                            var c = new[] { ((double)bmin[0] + (double)bmax[0]) / 2, ((double)bmin[1] + (double)bmax[1]) / 2, ((double)bmin[2] + (double)bmax[2]) / 2 };
                            h["box_mm"] = new JArray(new[] { bmin[0], bmin[1], bmin[2], bmax[0], bmax[1], bmax[2] }.Select(v => Math.Round((double)v * 1000, 1)));
                            LocateOnPaths(paths, c, h);
                        }
                        hits.Add(h);
                    }
                var violations = new JArray();
                if (ir["clearance"]?["violations"] is JArray vs)
                    foreach (var v in vs)
                    {
                        string other = (string)v["b"];
                        if (ignore.Any(g => other.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0)) { ignoredCount++; continue; }
                        violations.Add(new JObject { ["component"] = other, ["distance_mm"] = v["distance_mm"] });
                    }

                var result = new JObject
                {
                    ["assembly"] = asmTitle,
                    ["paths"] = paths.Count,
                    ["path_length_mm"] = new JArray(expected.Select(l => Math.Round(l * 1000, 2))),
                    ["neighbours_checked"] = ir["neighbours_checked"],
                    ["interference_count"] = hits.Count,
                    ["interferences"] = new JArray(hits.OrderByDescending(h => h.Value<double>("volume_mm3")).Take(limit)),
                };
                if (clearance > 0)
                {
                    result["clearance_mm"] = Math.Round(clearance * 1000, 3);
                    result["clearance_violations"] = violations;
                    if (ir["clearance"]?["nearest_m"] != null)
                    {
                        result["nearest_mm"] = Math.Round((double)ir["clearance"]["nearest_m"] * 1000, 3);
                        result["nearest"] = ((string)ir["clearance"]["nearest_pair"])?.Split(new[] { " | " }, StringSplitOptions.None).Last();
                    }
                    if (ir["clearance"]?["not_measured_neighbours"] != null) result["not_measured_neighbours"] = ir["clearance"]["not_measured_neighbours"];
                }
                if (ignoredCount > 0) result["ignored"] = ignoredCount;
                foreach (var k in new[] { "warning", "lightweight_not_checked", "resolved_from_lightweight_count" })
                    if (ir[k] != null) result[k] = ir[k];
                if (buildWarnings.Count > 0) result["path_warnings"] = buildWarnings;
                result["clear"] = hits.Count == 0 && violations.Count == 0 && ir["warning"] == null;
                if (keep) result["probe_component"] = probeName;
                if (!asmWasDirty && !keep)
                    result["note"] = "The assembly is now marked modified (a component was added and removed); its geometry is unchanged.";
                result["cleanup"] = cleanup;

                // Clean up before answering, so the response describes the final state.
                if (!keep) CleanupProbe(asmDoc, ref probeComp, ref probeDoc, probePath, cleanup);
                return ReadOnlyResponse(request, asmDoc, result);
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
            finally
            {
                if (!keep) CleanupProbe(asmDoc, ref probeComp, ref probeDoc, probePath, cleanup);
            }
        }

        private void CleanupProbe(IModelDoc2 asmDoc, ref IComponent2 comp, ref IModelDoc2 probeDoc, string probePath, JArray log)
        {
            if (comp != null && asmDoc != null)
            {
                try
                {
                    int e = 0;
                    _solidWorks.ActivateDoc3(asmDoc.GetTitle(), false, 1, ref e);
                    asmDoc.ClearSelection2(true);
                    bool sel = comp.Select4(false, null, false);
                    bool del = sel && asmDoc.Extension.DeleteSelection2(0);
                    asmDoc.ClearSelection2(true);
                    log.Add($"probe component removed: {del}");
                }
                catch (Exception ex) { log.Add("probe component not removed: " + ex.Message); }
                comp = null;
            }
            if (probeDoc != null)
            {
                try { _solidWorks.CloseDoc(probeDoc.GetTitle()); log.Add("probe part closed"); }
                catch (Exception ex) { log.Add("probe part not closed: " + ex.Message); }
                probeDoc = null;
                if (asmDoc != null)
                {
                    int e = 0;
                    try { _solidWorks.ActivateDoc3(asmDoc.GetTitle(), false, 1, ref e); } catch { }
                }
            }
            if (!string.IsNullOrEmpty(probePath) && File.Exists(probePath))
            {
                try { File.Delete(probePath); log.Add("probe file deleted"); }
                catch { log.Add("probe file still loaded by SolidWorks: deleted by the next call"); }
            }
        }

        private static string ParseProbePaths(JObject p, List<ProbePath> paths)
        {
            JArray list = null;
            var pt = p?["paths"];
            if (pt != null && pt.Type != JTokenType.Null)
            {
                try { list = pt.Type == JTokenType.String ? JArray.Parse((string)pt) : pt as JArray; }
                catch (Exception ex) { return "paths is not valid JSON: " + ex.Message; }
            }
            if (list == null || list.Count == 0)
            {
                list = new JArray(new JObject
                {
                    ["points"] = p?["points"],
                    ["bend_radii"] = p?["bend_radii"],
                    ["bend_radius"] = p?["bend_radius"],
                    ["diameter"] = p?["diameter"],
                });
            }
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i] as JObject;
                if (o == null) return $"paths[{i}] must be an object {{points, bend_radii, diameter}}.";
                string label = list.Count > 1 ? $"paths[{i}]: " : "";
                var ptsTok = o["points"];
                string ptsJson = ptsTok == null ? null : ptsTok.Type == JTokenType.String ? (string)ptsTok : ptsTok.ToString();
                string err = ParsePoints3D(ptsJson, out List<double[]> pts);
                if (err != null) return label + err;
                double d = o.Value<double?>("diameter") ?? p?.Value<double?>("diameter") ?? 0;
                if (d <= 0) return label + "diameter must be > 0 (meters): the bundle diameter.";
                double r0 = o.Value<double?>("bend_radius") ?? p?.Value<double?>("bend_radius") ?? 0;
                var radii = Enumerable.Repeat(r0, Math.Max(0, pts.Count - 2)).ToArray();
                var rt = o["bend_radii"];
                if (rt != null && rt.Type != JTokenType.Null)
                {
                    JArray ra;
                    try { ra = rt.Type == JTokenType.String ? JArray.Parse((string)rt) : (JArray)rt; }
                    catch (Exception ex) { return label + "bend_radii is not valid JSON: " + ex.Message; }
                    if (ra.Count > 0)
                    {
                        if (ra.Count != radii.Length) return label + $"bend_radii needs one radius per corner: {radii.Length} for {pts.Count} points.";
                        for (int k = 0; k < radii.Length; k++) radii[k] = (double)ra[k];
                    }
                }
                paths.Add(new ProbePath { Points = pts, Radii = radii, Diameter = d });
            }
            return null;
        }

        // One filleted polyline in a new 3D sketch and a circular sweep along it.
        private string BuildProbeSweep(IModelDoc2 doc, ProbePath path, int index, JArray warnings, out double length)
        {
            length = 0;
            var sm = doc.SketchManager;
            sm.Insert3DSketch(true);
            var active = sm.ActiveSketch;
            if (active == null) return "could not open a 3D sketch";
            bool prevDb = sm.AddToDB;
            int[] toggles = { 249, 9, 266, 271, 270 };
            var prev = new bool[toggles.Length];
            for (int ti = 0; ti < toggles.Length; ti++)
            { prev[ti] = _solidWorks.GetUserPreferenceToggle(toggles[ti]); _solidWorks.SetUserPreferenceToggle(toggles[ti], false); }
            sm.AddToDB = true;
            var w = new JArray();
            try
            {
                length = DrawFilletedPolyline(sm, path.Points, path.Radii, w);
                try { active.MergePoints(1e-7); } catch { }
            }
            finally
            {
                sm.AddToDB = prevDb;
                for (int ti = 0; ti < toggles.Length; ti++) _solidWorks.SetUserPreferenceToggle(toggles[ti], prev[ti]);
            }
            foreach (var x in w) warnings.Add($"path {index}: {x}");
            string sketchName = (active as IFeature)?.Name;
            sm.Insert3DSketch(true);
            doc.ClearSelection2(true);
            var feat = string.IsNullOrEmpty(sketchName) ? null : FindFeatureByName(doc, sketchName, null);
            if (feat == null) return "the path sketch was not created";
            var fm = doc.FeatureManager;
            var def = fm.CreateDefinition(SwFmSweep) as ISweepFeatureData;
            if (def == null) return "CreateDefinition(sweep) returned null";
            def.CircularProfile = true;
            def.CircularProfileDiameter = path.Diameter;
            feat.Select2(false, 4);
            try { def.Path = feat; } catch { }
            var sweep = fm.CreateFeature(def) as IFeature;
            doc.ClearSelection2(true);
            if (sweep == null) return "the circular sweep failed (a bend radius below the bundle radius, or the path crosses itself)";
            return null;
        }

        // The leg of the probe paths nearest to point c (meters): path index, leg k (points k -> k+1),
        // station along the path from its first point, and the corner when the point is at a leg end.
        private static void LocateOnPaths(List<ProbePath> paths, double[] c, JObject h)
        {
            double best = double.MaxValue; int bp = -1, bk = -1; double bt = 0, bstation = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                var pts = paths[i].Points;
                double run = 0;
                for (int k = 0; k + 1 < pts.Count; k++)
                {
                    var a = pts[k]; var b = pts[k + 1];
                    var ab = Sub(b, a);
                    double L2 = Dot(ab, ab);
                    double t = L2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, Dot(Sub(c, a), ab) / L2));
                    var q = Add(a, Scale(ab, t));
                    double d = Dist(c, q);
                    if (d < best) { best = d; bp = i; bk = k; bt = t; bstation = run + t * Math.Sqrt(L2); }
                    run += Math.Sqrt(L2);
                }
            }
            if (bp < 0) return;
            if (paths.Count > 1) h["path"] = bp;
            h["leg"] = bk;
            h["leg_points"] = $"{bk} -> {bk + 1}";
            h["at_mm_along_path"] = Math.Round(bstation * 1000, 1);
            var lp = paths[bp].Points;
            double legLen = Dist(lp[bk], lp[bk + 1]);
            if (bt * legLen < 0.01 && bk > 0) h["near_corner"] = bk;
            else if ((1 - bt) * legLen < 0.01 && bk + 1 < lp.Count - 1) h["near_corner"] = bk + 1;
        }
    }
}
