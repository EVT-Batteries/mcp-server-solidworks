using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: 3D paths for routed geometry (cables, wire bundles, tubes, rods).
    //  - create_3d_path: a 3D sketch from XYZ points, either a polyline whose corners are rounded with
    //    exact tangent arcs of a bend radius, or a spline through the points. Given the name of an
    //    existing 3D sketch, it REDRAWS that sketch in place (same feature, so a sweep built on it
    //    follows) — the update path when the parts a route connects move.
    //  - sweep_circular: a circular-profile sweep along a path sketch (only a diameter, no profile
    //    sketch / plane needed, so any 3D start direction works). Given an existing sweep's name, it
    //    changes that sweep's diameter instead.
    public partial class SolidWorksService
    {
        private const int SwFmSweep = 17;   // swFeatureNameID_e.swFmSweep, inlined

        public ExecutionResponse CreatePath3D(ToolRequest request)
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
                string kind = (p?.Value<string>("kind") ?? "polyline").ToLowerInvariant();
                double bendRadius = p?.Value<double?>("bend_radius") ?? 0.0;
                string sketchName = p?.Value<string>("sketch_name") ?? "";

                List<double[]> pts;
                string parseErr = ParsePoints3D(p?.Value<string>("points"), out pts);
                if (parseErr != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", parseErr);
                if (kind != "polyline" && kind != "spline")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "kind must be 'polyline' or 'spline'.");
                if (bendRadius < 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "bend_radius must be >= 0.");

                // Per-corner radii: bend_radii overrides bend_radius corner by corner.
                var radii = new double[Math.Max(0, pts.Count - 2)];
                for (int i = 0; i < radii.Length; i++) radii[i] = bendRadius;
                string radiiJson = p?.Value<string>("bend_radii");
                if (!string.IsNullOrWhiteSpace(radiiJson) && radiiJson.Trim() != "[]")
                {
                    JArray ra;
                    try { ra = JArray.Parse(radiiJson); }
                    catch (Exception ex)
                    {
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "bend_radii is not valid JSON: " + ex.Message);
                    }
                    if (ra.Count != radii.Length)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", $"bend_radii needs one radius per corner: {radii.Length} for {pts.Count} points.");
                    for (int i = 0; i < radii.Length; i++)
                    {
                        radii[i] = ra[i].Value<double>();
                        if (radii[i] < 0)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "INVALID_PARAMETER", "bend_radii values must be >= 0.");
                    }
                }

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null || !(modelDoc is IPartDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "create_3d_path requires an active part document.");

                var sm = modelDoc.SketchManager;
                if (sm.ActiveSketch != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_ACTIVE", "A sketch is being edited; exit it before create_3d_path.");

                // Enter the sketch: redraw an existing 3D sketch, or start a new one.
                bool redraw = false;
                if (!string.IsNullOrEmpty(sketchName))
                {
                    var existing = FindFeatureByName(modelDoc, sketchName, null);
                    if (existing != null)
                    {
                        if (existing.GetTypeName2() != "3DProfileFeature")
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "WRONG_FEATURE_TYPE", $"'{sketchName}' exists but is not a 3D sketch ({existing.GetTypeName2()}).");
                        modelDoc.ClearSelection2(true);
                        if (!existing.Select2(false, 0))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "SKETCH_NOT_FOUND", $"Could not select 3D sketch '{sketchName}'.");
                        modelDoc.EditSketch();
                        redraw = true;
                    }
                }
                if (!redraw) sm.Insert3DSketch(true);

                var active = sm.ActiveSketch;
                if (active == null || !active.Is3D())
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_FAILED", "Could not open a 3D sketch for editing.");

                var warnings = new JArray();
                int deleted = 0;
                double expectedLength = -1.0;
                bool prevAddToDb = sm.AddToDB;
                sm.AddToDB = true;   // exact coordinates: no inference snapping, no automatic relations
                // AddToDB alone did not stop Create3PointArc from snapping an arc end onto a nearby
                // point of the same sketch: in a zoomed-out view of a long route, points a few mm
                // apart fall within the screen-space inference tolerance (a loop crossing over itself,
                // two short legs). Switch inference, automatic relations and point snaps off for the
                // draw and restore the user's settings afterwards.
                var swApp = _solidWorks;
                int[] toggles = { 249 /* swSketchInference */, 9 /* swSketchAutomaticRelations */,
                                  266 /* swSketchSnapsPoints */, 271 /* swSketchSnapsNearest */,
                                  270 /* swSketchSnapsIntersections */ };
                var prevToggles = new bool[toggles.Length];
                for (int ti = 0; ti < toggles.Length; ti++)
                {
                    prevToggles[ti] = swApp.GetUserPreferenceToggle(toggles[ti]);
                    swApp.SetUserPreferenceToggle(toggles[ti], false);
                }
                try
                {
                    if (redraw)
                    {
                        // Clear the old route: its segments AND its sketch points (a hand-drawn sketch keeps
                        // dimensioned / related points after its segments go, and the solver would pull
                        // the new geometry onto them), with the dimensions and relations on them.
                        modelDoc.ClearSelection2(true);
                        if (active.GetSketchSegments() is object[] old)
                            foreach (var o in old)
                                if (o is ISketchSegment s && s.Select4(true, null)) deleted++;
                        if (deleted > 0) modelDoc.EditDelete();
                        modelDoc.ClearSelection2(true);
                        int pointsLeft = 0;
                        if (active.GetSketchPoints2() is object[] oldPts)
                            foreach (var o in oldPts)
                                if (o is ISketchPoint sp && sp.Select4(true, null)) pointsLeft++;
                        if (pointsLeft > 0) modelDoc.EditDelete();
                        modelDoc.ClearSelection2(true);
                        if (active.GetSketchPoints2() is object[] stillThere && stillThere.Length > 0)
                            warnings.Add($"{stillThere.Length} old sketch point(s) could not be deleted");
                    }

                    if (kind == "spline")
                    {
                        var flat = new double[pts.Count * 3];
                        for (int i = 0; i < pts.Count; i++)
                        { flat[3 * i] = pts[i][0]; flat[3 * i + 1] = pts[i][1]; flat[3 * i + 2] = pts[i][2]; }
                        if (sm.CreateSpline2(flat, true) == null)
                            warnings.Add("CreateSpline2 returned null");
                    }
                    else
                    {
                        expectedLength = DrawFilletedPolyline(sm, pts, radii, warnings);
                    }

                    // AddToDB leaves the shared endpoints of consecutive segments as separate sketch
                    // points; merge them so the route is one connected chain (a sweep path must be).
                    try { active.MergePoints(1e-7); }   // false = nothing left to merge
                    catch (Exception ex) { warnings.Add("MergePoints failed: " + ex.Message); }
                }
                finally
                {
                    sm.AddToDB = prevAddToDb;
                    for (int ti = 0; ti < toggles.Length; ti++)
                        swApp.SetUserPreferenceToggle(toggles[ti], prevToggles[ti]);
                }

                string name = (active as IFeature)?.Name;
                sm.Insert3DSketch(true);   // exit the 3D sketch (new or edited)
                modelDoc.ClearSelection2(true);

                IFeature sketchFeat = !string.IsNullOrEmpty(name) ? FindFeatureByName(modelDoc, name, null) : null;
                if (!redraw && sketchFeat != null && !string.IsNullOrEmpty(sketchName) && sketchFeat.Name != sketchName)
                {
                    sketchFeat.Name = sketchName;
                    name = sketchFeat.Name;
                }
                modelDoc.ForceRebuild3(false);

                var info = new JObject
                {
                    ["sketch"] = name,
                    ["action"] = redraw ? "redrawn" : "created",
                    ["kind"] = kind,
                };
                if (redraw) info["deleted_segments"] = deleted;
                var sk = (sketchFeat ?? FindFeatureByName(modelDoc, name, null))?.GetSpecificFeature2() as ISketch;
                if (sk != null)
                {
                    var m = MeasureSketch(sk, new MeasureOptions { PerSegment = true, IncludeGeometry = true });
                    info["length_m"] = m["length_m"];
                    info["segments"] = m["segments"];
                    info["per_segment"] = m["per_segment"];
                    if (expectedLength >= 0)
                    {
                        info["expected_length_m"] = Math.Round(expectedLength, 6);
                        double got = m.Value<double>("length_m");
                        if (Math.Abs(got - expectedLength) > 2e-6)
                            warnings.Add($"solved length {Math.Round(got, 6)} m differs from the drawn route's " +
                                         $"{Math.Round(expectedLength, 6)} m: leftover sketch geometry or relations moved it");
                    }
                }
                if (warnings.Count > 0) info["warnings"] = warnings;

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = new List<string> { info.ToString(Newtonsoft.Json.Formatting.None) },
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

        public ExecutionResponse SweepCircular(ToolRequest request)
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
                string pathSketch = p?.Value<string>("path_sketch") ?? "";
                double diameter = p?.Value<double?>("diameter") ?? 0.0;
                string name = p?.Value<string>("name") ?? "";

                if (diameter <= 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "diameter must be > 0 (meters).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null || !(modelDoc is IPartDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "sweep_circular requires an active part document.");
                if (modelDoc.SketchManager.ActiveSketch != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_ACTIVE", "A sketch is being edited; exit it before sweep_circular.");

                var names = new List<string>();
                IFeature existing = string.IsNullOrEmpty(name) ? null : FindFeatureByName(modelDoc, name, names);
                IFeature result;
                string action;

                if (existing != null)
                {
                    // Update an existing sweep's circular diameter.
                    var def = existing.GetDefinition() as ISweepFeatureData;
                    if (def == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_FEATURE_TYPE", $"'{name}' exists but is not a sweep ({existing.GetTypeName2()}).");
                    if (!def.AccessSelections(modelDoc, null))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDIT_FAILED", $"Could not access the definition of '{name}'.");
                    bool ok;
                    try
                    {
                        if (!def.CircularProfile)
                        {
                            def.ReleaseSelectionAccess();
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "WRONG_FEATURE_TYPE", $"'{name}' is a profile-sketch sweep, not a circular-profile sweep.");
                        }
                        def.CircularProfileDiameter = diameter;
                        ok = existing.ModifyDefinition(def, modelDoc, null);
                    }
                    catch { def.ReleaseSelectionAccess(); throw; }
                    if (!ok)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDIT_FAILED", $"ModifyDefinition failed for '{name}'.");
                    result = existing;
                    action = "updated";
                }
                else
                {
                    if (string.IsNullOrEmpty(pathSketch))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "path_sketch is required to create a sweep.");
                    var pathFeat = FindFeatureByName(modelDoc, pathSketch, null);
                    if (pathFeat == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_NOT_FOUND", $"Path sketch '{pathSketch}' not found.");
                    var fm = modelDoc.FeatureManager;
                    var def = fm.CreateDefinition(SwFmSweep) as ISweepFeatureData;
                    if (def == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SWEEP_FAILED", "CreateDefinition(swFmSweep) returned null.");
                    def.CircularProfile = true;
                    def.CircularProfileDiameter = diameter;
                    modelDoc.ClearSelection2(true);
                    pathFeat.Select2(false, 4);   // the path, selection mark 4
                    try { def.Path = pathFeat; } catch { /* the mark-4 selection carries the path */ }
                    result = fm.CreateFeature(def) as IFeature;
                    modelDoc.ClearSelection2(true);
                    if (result == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SWEEP_FAILED", $"Circular sweep along '{pathSketch}' failed (path not contiguous, or self-intersecting at this diameter?).");
                    if (!string.IsNullOrEmpty(name)) result.Name = name;
                    action = "created";
                }

                modelDoc.ForceRebuild3(false);
                var info = new JObject
                {
                    ["sweep"] = result.Name,
                    ["action"] = action,
                    ["diameter_m"] = Math.Round(diameter, 6),
                };
                try
                {
                    var measured = MeasureSweep(modelDoc, result, new MeasureOptions());
                    info["path"] = measured["path"];
                    info["path_length_m"] = measured["path_length_m"];
                }
                catch { }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = new List<string> { info.ToString(Newtonsoft.Json.Formatting.None) },
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

        // "[[x,y,z],...]" (meters) -> list of points; >= 2 points, no two consecutive equal.
        private static string ParsePoints3D(string json, out List<double[]> pts)
        {
            pts = new List<double[]>();
            if (string.IsNullOrWhiteSpace(json)) return "points is required: a JSON array of [x,y,z] in meters.";
            JArray arr;
            try { arr = JArray.Parse(json); }
            catch (Exception ex) { return "points is not valid JSON: " + ex.Message; }
            foreach (var t in arr)
            {
                var a = t as JArray;
                if (a == null || a.Count != 3) return "every point must be [x,y,z].";
                pts.Add(new[] { a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>() });
            }
            if (pts.Count < 2) return "at least 2 points are needed.";
            for (int i = 1; i < pts.Count; i++)
                if (Dist(pts[i - 1], pts[i]) < 1e-9) return $"points {i - 1} and {i} coincide.";
            return null;
        }

        // Lines between the points, corner i (1..n-2) rounded with a tangent arc of radii[i-1]. Where two
        // bends share a leg too short for both, both are scaled down together (with a warning); an end
        // leg may be used up entirely by its bend. Straight-through (collinear) corners and radius 0
        // stay sharp.
        // Returns the route's theoretical length (legs + arcs), to check the solved sketch against.
        private static double DrawFilletedPolyline(ISketchManager sm, List<double[]> pts, double[] radii, JArray warnings)
        {
            double expected = 0.0;
            int n = pts.Count;
            var tIn = new double[n][];
            var tOut = new double[n][];
            var mid = new double[n][];
            var t = new double[n];
            var half = new double[n];

            for (int i = 1; i < n - 1; i++)
            {
                double r = radii[i - 1];
                if (r <= 0) continue;
                double[] u = Unit(Sub(pts[i - 1], pts[i]));
                double[] v = Unit(Sub(pts[i + 1], pts[i]));
                double theta = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(u, v))));   // angle between the legs
                if (Math.PI - theta < 1e-6) continue;                                   // straight through
                half[i] = theta / 2.0;
                t[i] = r / Math.Tan(half[i]);
            }

            // Fit the tangent lengths into the legs (1e-9 m slack for exact fits).
            var scale = new double[n];
            for (int i = 0; i < n; i++) scale[i] = 1.0;
            for (int k = 0; k < n - 1; k++)
            {
                double leg = Dist(pts[k], pts[k + 1]) + 1e-9;
                double need = (k > 0 ? t[k] : 0) + (k + 1 < n - 1 ? t[k + 1] : 0);
                if (need > leg)
                {
                    double s = leg / need;
                    if (k > 0) scale[k] = Math.Min(scale[k], s);
                    if (k + 1 < n - 1) scale[k + 1] = Math.Min(scale[k + 1], s);
                }
            }

            for (int i = 1; i < n - 1; i++)
            {
                if (t[i] <= 0) continue;
                double ti = t[i] * scale[i];
                double r = ti * Math.Tan(half[i]);
                if (scale[i] < 1.0)
                    warnings.Add($"corner {i}: bend radius reduced to {Math.Round(r, 6)} m (legs too short)");
                double[] u = Unit(Sub(pts[i - 1], pts[i]));
                double[] v = Unit(Sub(pts[i + 1], pts[i]));
                double[] w = Unit(Add(u, v));
                double[] c = Add(pts[i], Scale(w, r / Math.Sin(half[i])));
                tIn[i] = Add(pts[i], Scale(u, ti));
                tOut[i] = Add(pts[i], Scale(v, ti));
                mid[i] = Sub(c, Scale(w, r));
                expected += r * (Math.PI - 2.0 * half[i]) - 2.0 * ti;   // arc replaces two tangent stubs
            }
            for (int k = 0; k < n - 1; k++) expected += Dist(pts[k], pts[k + 1]);

            if (sm == null) return expected;   // geometry only (dry run)
            double[] cur = pts[0];
            for (int i = 1; i < n; i++)
            {
                bool bent = i < n - 1 && tIn[i] != null;
                double[] lineEnd = bent ? tIn[i] : pts[i];
                if (Dist(cur, lineEnd) > 1e-9)
                    sm.CreateLine(cur[0], cur[1], cur[2], lineEnd[0], lineEnd[1], lineEnd[2]);
                if (bent)
                {
                    // Create3PointArc(start, end, point on arc).
                    var arc = sm.Create3PointArc(tIn[i][0], tIn[i][1], tIn[i][2],
                                                 tOut[i][0], tOut[i][1], tOut[i][2],
                                                 mid[i][0], mid[i][1], mid[i][2]);
                    if (arc == null) warnings.Add($"corner {i}: Create3PointArc returned null");
                    cur = tOut[i];
                }
                else cur = pts[i];
            }
            return expected;
        }

        // Per leg of a filleted polyline: length, tangent lengths taken by the bends at its two ends
        // and the straight rest (slack < 0: the radii do not fit and get scaled down); per corner:
        // deflection angle, radius, tangent length. Same construction as DrawFilletedPolyline.
        private static JObject PolylineGeometry(List<double[]> pts, double[] radii)
        {
            int n = pts.Count;
            var t = new double[n];
            var corners = new JArray();
            for (int i = 1; i < n - 1; i++)
            {
                double[] u = Unit(Sub(pts[i - 1], pts[i]));
                double[] v = Unit(Sub(pts[i + 1], pts[i]));
                double theta = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Dot(u, v))));
                double defl = (Math.PI - theta) * 180.0 / Math.PI;
                double r = radii[i - 1];
                if (r > 0 && Math.PI - theta >= 1e-6) t[i] = r / Math.Tan(theta / 2.0);
                corners.Add(new JObject
                {
                    ["corner"] = i,
                    ["deflection_deg"] = Math.Round(defl, 2),
                    ["radius_mm"] = Math.Round(r * 1000, 3),
                    ["tangent_mm"] = Math.Round(t[i] * 1000, 3),
                });
            }
            var legs = new JArray();
            double minSlack = double.MaxValue;
            for (int k = 0; k < n - 1; k++)
            {
                double L = Dist(pts[k], pts[k + 1]);
                double slack = L - t[k] - t[k + 1];
                minSlack = Math.Min(minSlack, slack);
                legs.Add(new JObject
                {
                    ["leg"] = k,
                    ["length_mm"] = Math.Round(L * 1000, 3),
                    ["straight_mm"] = Math.Round(slack * 1000, 3),
                });
            }
            return new JObject
            {
                ["length_mm"] = Math.Round(DrawFilletedPolyline(null, pts, radii, new JArray()) * 1000, 3),
                ["min_straight_mm"] = Math.Round(minSlack * 1000, 3),
                ["corners"] = corners,
                ["legs"] = legs,
            };
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Dist(double[] a, double[] b) => Math.Sqrt(Dot(Sub(a, b), Sub(a, b)));
        private static double[] Unit(double[] a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-15 ? a : Scale(a, 1.0 / l); }
    }
}
