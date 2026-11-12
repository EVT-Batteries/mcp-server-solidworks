using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidworksExecution.Models;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Services
{
    // check_interference: SolidWorks Interference Detection for the active assembly, or for named
    // components against the parts around them, plus an optional minimum-clearance check.
    // Read-only: the model is not changed (lightweight components it needs are resolved).
    public partial class SolidWorksService
    {
        public ExecutionResponse CheckInterference(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            IModelDoc2 modelDoc = null;
            try
            {
                _autoResolved.Clear();
                var p = request.Params as JObject;
                var wanted = ParseNameList(p?["components"]);
                bool coincident = p?.Value<bool?>("treat_coincidence_as_interference") ?? false;
                bool subAsmAsComponents = p?.Value<bool?>("treat_subassemblies_as_components") ?? false;
                bool includeHidden = p?.Value<bool?>("include_hidden") ?? false;
                bool resolveAll = p?.Value<bool?>("resolve_lightweight") ?? false;
                double clearance = p?.Value<double?>("clearance") ?? 0.0;
                int limit = p?.Value<int?>("limit") ?? 100;
                // compact: counts instead of the long lists (pairs that could not be measured, components
                // resolved from lightweight); ignore_same_parent: drop interferences / clearance
                // violations between components of one sub-assembly (e.g. a harness's own bundles at
                // their split points), reported as a count instead.
                bool compact = p?.Value<bool?>("compact") ?? false;
                bool ignoreSameParent = p?.Value<bool?>("ignore_same_parent") ?? false;
                int ignoredSameParent = 0;

                modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "check_interference requires an active assembly.");
                if (clearance < 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "clearance must be >= 0 (meters).");
                if (clearance > 0 && wanted.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "A clearance check needs `components`: the parts to check against their neighbours.");

                int resolvedAll = resolveAll ? ResolveAllLightweightComponents(asm) : 0;

                // The named components, with their models loaded.
                var targets = new List<IComponent2>();
                foreach (var n in wanted)
                {
                    var c = FindComponentByName(asm, n);
                    if (c == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "COMPONENT_NOT_FOUND", ComponentNotFoundMessage(asm, n));
                    string ec, em;
                    c = ResolveForGeometry(asm, c, out ec, out em);
                    if (c == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), ec, em);
                    targets.Add(c);
                }
                var targetPaths = targets.Select(ComponentPath).ToList();

                var result = new JObject { ["assembly"] = modelDoc.GetTitle() };
                var near = new List<IComponent2>();
                var skippedLight = new List<string>();
                if (targets.Count > 0)
                {
                    // Neighbours: the leaf components whose bounding box comes within the clearance
                    // of a named component's box. Only those are handed to SolidWorks.
                    double margin = Math.Max(clearance, 1e-6);
                    var boxes = new List<double[]>();
                    foreach (var t in targets)
                    {
                        var b = ComponentBox(t);
                        if (b == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "NO_GEOMETRY", "'" + ComponentPath(t) + "' has no bounding box (no visible solid geometry).");
                        boxes.Add(b);
                    }
                    if (asm.GetComponents(true) is object[] tops)
                        foreach (var o in tops)
                            if (o is IComponent2 c)
                                CollectNear(asm, c, boxes, margin, targetPaths, includeHidden, near, skippedLight, 0);
                    result["checked"] = new JArray(targetPaths.ToArray());
                    result["neighbours_checked"] = near.Count;
                    if (!compact) result["neighbours"] = new JArray(near.Select(ComponentPath).ToArray());
                }
                else result["checked"] = "all components";

                // Interference Detection. With a selection, SolidWorks checks the selected components.
                var found = new List<JObject>();
                var interfering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int total = 0;
                if (targets.Count == 0 || near.Count > 0)
                {
                    modelDoc.ClearSelection2(true);
                    foreach (var c in targets.Concat(near))
                        try { c.Select4(true, null, false); } catch { }

                    var mgr = asm.InterferenceDetectionManager;
                    try
                    {
                        mgr.TreatCoincidenceAsInterference = coincident;
                        mgr.TreatSubAssembliesAsComponents = subAsmAsComponents;
                        mgr.IncludeMultibodyPartInterferences = false;
                        mgr.MakeInterferingPartsTransparent = false;
                        mgr.CreateFastenersFolder = false;
                        mgr.ShowIgnoredInterferences = false;
                        mgr.IgnoreHiddenBodies = !includeHidden;
                        if (mgr.GetInterferences() is object[] ints)
                        {
                            foreach (var o in ints)
                            {
                                if (!(o is IInterference inf)) continue;
                                var paths = new List<string>();
                                if (inf.Components is object[] ics)
                                    foreach (var ic in ics)
                                        if (ic is IComponent2 icc) paths.Add(ComponentPath(icc));
                                // Named components: keep only what touches one of them from outside.
                                if (targets.Count > 0)
                                {
                                    bool inside = paths.Any(x => IsUnder(x, targetPaths));
                                    bool outside = paths.Any(x => !IsUnder(x, targetPaths));
                                    bool twoTargets = paths.Select(x => targetPaths.FirstOrDefault(t => IsUnder(x, new List<string> { t })))
                                        .Where(t => t != null).Distinct().Count() > 1;
                                    if (!inside || !(outside || twoTargets)) continue;
                                }
                                if (ignoreSameParent && SameParent(paths)) { ignoredSameParent++; continue; }
                                total++;
                                for (int i = 0; i < paths.Count; i++)
                                    for (int k = 0; k < paths.Count; k++)
                                        if (i != k) interfering.Add(paths[i] + "|" + paths[k]);
                                var ij = new JObject { ["components"] = new JArray(paths.ToArray()) };
                                double vol = 0;
                                try { vol = inf.Volume; } catch { }
                                ij["volume_m3"] = vol;
                                ij["volume_mm3"] = Math.Round(vol * 1e9, 3);
                                try
                                {
                                    if (inf.GetInterferenceBody() is IBody2 body && body.GetBodyBox() is double[] bb && bb.Length >= 6)
                                    {
                                        ij["box_min"] = Point(bb[0], bb[1], bb[2]);
                                        ij["box_max"] = Point(bb[3], bb[4], bb[5]);
                                    }
                                }
                                catch { }
                                try { if (inf.IsPossibleInterference) ij["possible_only"] = true; } catch { }
                                found.Add(ij);
                            }
                        }
                    }
                    finally
                    {
                        try { mgr.Done(); } catch { }
                        modelDoc.ClearSelection2(true);
                    }
                }
                found = found.OrderByDescending(j => j.Value<double>("volume_m3")).ToList();
                result["interference_count"] = total;
                result["interferences"] = new JArray(found.Take(limit).ToArray());
                if (found.Count > limit) result["interferences_truncated_at"] = limit;

                // Clearance: the minimum distance from each named component to each neighbour.
                bool clearanceOk = true;
                if (clearance > 0)
                {
                    var violations = new List<JObject>();
                    var unmeasured = new JArray();
                    var distances = new JObject();
                    double nearest = double.MaxValue;
                    string nearestPair = null;
                    foreach (var t in targets)
                    {
                        string tp = ComponentPath(t);
                        foreach (var n in near)
                        {
                            string np = ComponentPath(n);
                            // Measure fails on intersecting bodies: an interfering pair is at 0.
                            if (ignoreSameParent && SameParent(new List<string> { tp, np })) continue;
                            double d = interfering.Contains(tp + "|" + np) ? 0.0 : MinimumDistance(modelDoc, t, n);
                            if (!compact) distances[tp + " | " + np] = d < 0 ? (JToken)null : Math.Round(d * 1000.0, 3);
                            if (d < 0) { unmeasured.Add(tp + " | " + np); continue; }
                            if (d < nearest) { nearest = d; nearestPair = tp + " | " + np; }
                            if (d < clearance)
                                violations.Add(new JObject
                                {
                                    ["a"] = tp,
                                    ["b"] = np,
                                    ["distance_m"] = Math.Round(d, 6),
                                    ["distance_mm"] = Math.Round(d * 1000.0, 3),
                                });
                        }
                    }
                    modelDoc.ClearSelection2(true);
                    violations = violations.OrderBy(j => j.Value<double>("distance_m")).ToList();
                    clearanceOk = violations.Count == 0 && unmeasured.Count == 0;
                    var cj = new JObject
                    {
                        ["required_m"] = clearance,
                        ["violation_count"] = violations.Count,
                        ["violations"] = new JArray(violations.Take(limit).ToArray()),
                    };
                    if (!compact) cj["distances_mm"] = distances;
                    if (nearestPair != null)
                    {
                        cj["nearest_m"] = Math.Round(nearest, 6);
                        cj["nearest_pair"] = nearestPair;
                    }
                    if (unmeasured.Count > 0)
                    {
                        if (compact)
                        {
                            // Usually surface-only models (no solid to measure): name the neighbours once.
                            cj["not_measured_count"] = unmeasured.Count;
                            cj["not_measured_neighbours"] = new JArray(unmeasured
                                .Select(u => u.ToString().Split(new[] { " | " }, StringSplitOptions.None).Last())
                                .Distinct().Take(20).ToArray());
                        }
                        else cj["not_measured"] = unmeasured;
                    }
                    result["clearance"] = cj;
                }
                if (ignoredSameParent > 0) result["ignored_same_parent"] = ignoredSameParent;

                int stillLight = LightweightCount(asm);
                if (resolvedAll > 0) result["lightweight_resolved"] = resolvedAll;
                if (compact) { if (_autoResolved.Count > 0) result["resolved_from_lightweight_count"] = _autoResolved.Count; }
                else AddAutoResolved(result);
                if (skippedLight.Count > 0)
                    result["lightweight_not_checked"] = new JArray(skippedLight.Take(compact ? 10 : 50).ToArray());
                if (targets.Count == 0 && stillLight > 0)
                    result["warning"] = stillLight + " components are lightweight: SolidWorks may leave them out. Pass resolve_lightweight=true for a complete check.";
                else if (skippedLight.Count > 0)
                    result["warning"] = skippedLight.Count + " lightweight components have no bounding box and were not checked. Pass resolve_lightweight=true to include them.";
                result["clear"] = total == 0 && clearanceOk && !result.ContainsKey("warning");

                return ReadOnlyResponse(request, modelDoc, result);
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

        private static JArray Point(double x, double y, double z)
        {
            return new JArray(Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6));
        }

        // True when every path has the same, non-empty parent instance path ("A-1/B-1/x-1" and
        // "A-1/B-1/y-2" share "A-1/B-1"): components of one sub-assembly.
        private static bool SameParent(List<string> paths)
        {
            if (paths.Count < 2) return false;
            string Parent(string s) { int i = s.LastIndexOf('/'); return i > 0 ? s.Substring(0, i) : ""; }
            string first = Parent(paths[0]);
            return first.Length > 0 && paths.All(x => string.Equals(Parent(x), first, StringComparison.OrdinalIgnoreCase));
        }

        // True when `path` is one of `roots` or lies below one.
        private static bool IsUnder(string path, List<string> roots)
        {
            foreach (var r in roots)
                if (string.Equals(path, r, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // A component's bounding box in assembly coordinates [xmin,ymin,zmin,xmax,ymax,zmax], or null.
        private static double[] ComponentBox(IComponent2 c)
        {
            try
            {
                var b = c.GetBox(false, false) as double[];
                if (b == null || b.Length < 6) return null;
                if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0) return null;
                return b;
            }
            catch { return null; }
        }

        private static bool BoxesWithin(double[] a, double[] b, double margin)
        {
            for (int i = 0; i < 3; i++)
                if (a[i] - margin > b[i + 3] || b[i] - margin > a[i + 3]) return false;
            return true;
        }

        // Collects the leaf components under `c` whose box comes within `margin` of one of `boxes`.
        // Skips suppressed (and, by default, hidden) components and the named components themselves.
        private void CollectNear(IAssemblyDoc asm, IComponent2 c, List<double[]> boxes, double margin,
            List<string> targetPaths, bool includeHidden, List<IComponent2> near, List<string> skippedLight, int depth)
        {
            if (c == null || depth > 30) return;
            int state = SafeSuppression(c);
            if (state == 0) return;
            if (!includeHidden && SafeVisible(c) == 0) return;
            string path = ComponentPath(c);
            if (IsUnder(path, targetPaths)) return;
            // An ancestor of a named component: look at its other children.
            bool ancestor = targetPaths.Any(t => t.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

            var box = ComponentBox(c);
            if (box == null)
            {
                if (IsLightweightState(state)) { skippedLight.Add(path); return; }
                if (!ancestor) return;
            }
            else if (!ancestor && !boxes.Any(b => BoxesWithin(b, box, margin))) return;

            object[] kids = null;
            try { kids = c.GetChildren() as object[]; } catch { }
            bool isAssembly = SafeComponentPath(c).EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            if ((kids == null || kids.Length == 0) && isAssembly && IsLightweightState(state))
            {
                // A lightweight sub-assembly near the named parts: load it to see its parts.
                string ec, em;
                var r = ResolveForGeometry(asm, c, out ec, out em);
                if (r == null) { skippedLight.Add(path); return; }
                c = r;
                try { kids = c.GetChildren() as object[]; } catch { }
            }
            if (kids != null && kids.Length > 0)
            {
                foreach (var k in kids)
                    CollectNear(asm, k as IComponent2, boxes, margin, targetPaths, includeHidden, near, skippedLight, depth + 1);
                return;
            }
            if (isAssembly) return;   // an empty sub-assembly
            if (IsLightweightState(state))
            {
                string ec, em;
                var r = ResolveForGeometry(asm, c, out ec, out em);
                if (r == null) { skippedLight.Add(path); return; }
                c = r;
            }
            near.Add(c);
        }

        // Minimum distance between two components, in meters; -1 when SolidWorks cannot measure
        // the pair. Measure on two selected components only looks at part of a multi-body
        // component (a SOLIDWORKS Routing tube, one body per route segment, measured 151 mm from a
        // bundle lying 0.04 mm from it), so components with several solid bodies are measured
        // body to body and the smallest distance wins.
        private static double MinimumDistance(IModelDoc2 doc, IComponent2 a, IComponent2 b)
        {
            double d = ComponentDistance(doc, a, b);
            var ba = SolidBodies(a);
            var bb = SolidBodies(b);
            if ((ba.Count > 1 || bb.Count > 1) && ba.Count > 0 && bb.Count > 0 && ba.Count * bb.Count <= 2500)
            {
                var sm = doc.SelectionManager as ISelectionMgr;
                foreach (var x in ba)
                    foreach (var y in bb)
                    {
                        double e = BodyDistance(doc, sm, x, y);
                        if (e >= 0 && (d < 0 || e < d)) d = e;
                    }
                doc.ClearSelection2(true);
            }
            return d;
        }

        private static List<IBody2> SolidBodies(IComponent2 c)
        {
            var list = new List<IBody2>();
            try
            {
                object info;
                if (c.GetBodies3(0 /* swSolidBody */, out info) is object[] bodies)
                    foreach (var o in bodies)
                        if (o is IBody2 body) list.Add(body);
            }
            catch { }
            return list;
        }

        private static double BodyDistance(IModelDoc2 doc, ISelectionMgr sm, IBody2 x, IBody2 y)
        {
            try
            {
                doc.ClearSelection2(true);
                if (!x.Select2(false, sm?.CreateSelectData() as SelectData)
                    || !y.Select2(true, sm?.CreateSelectData() as SelectData)) return -1;
                var m = doc.Extension.CreateMeasure() as IMeasure;
                if (m == null) return -1;
                m.ArcOption = 1;   // minimum distance
                if (!m.Calculate(null)) return -1;
                double d = m.Distance;
                return d < 0 ? -1 : d;
            }
            catch { return -1; }
        }

        private static double ComponentDistance(IModelDoc2 doc, IComponent2 a, IComponent2 b)
        {
            try
            {
                doc.ClearSelection2(true);
                if (!a.Select4(false, null, false) || !b.Select4(true, null, false)) return -1;
                var m = doc.Extension.CreateMeasure() as IMeasure;
                if (m == null) return -1;
                m.ArcOption = 1;   // minimum distance
                if (!m.Calculate(null)) return -1;
                double d = m.Distance;
                return d < 0 ? -1 : d;
            }
            catch { return -1; }
        }
    }
}
