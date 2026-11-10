using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Services
{
    // Lightweight components: a lightweight component has no loaded model, so it has no bodies,
    // faces or sketches to read, and its instance name can change once it is resolved. The tools
    // that need geometry resolve the components they are asked about (never the whole assembly,
    // unless asked) and say so in their result.
    public partial class SolidWorksService
    {
        // Names of the components the current request resolved from lightweight (reset per request).
        private readonly List<string> _autoResolved = new List<string>();

        private static bool IsLightweightState(int s) { return s == 1 || s == 4; }

        // Component IDs from the top-level component down to `c`.
        private static List<int> ComponentIdChain(IComponent2 c)
        {
            var ids = new List<int>();
            int guard = 0;
            while (c != null && guard++ < 50)
            {
                ids.Insert(0, SafeId(c));
                c = c.GetParent() as IComponent2;
            }
            return ids;
        }

        private static IComponent2 ComponentByIdChain(IAssemblyDoc asm, List<int> ids)
        {
            if (ids == null || ids.Count == 0) return null;
            if (!(asm.GetComponents(true) is object[] tops)) return null;
            foreach (var o in tops)
            {
                if (!(o is IComponent2 t) || SafeId(t) != ids[0]) continue;
                return ids.Count == 1 ? t : ChildByIds(t, ids.GetRange(1, ids.Count - 1));
            }
            return null;
        }

        // Makes sure a component's model is loaded: resolves it (and the lightweight sub-assemblies
        // above it) when lightweight, and fails clearly when it or a parent is suppressed. Returns
        // the component again, re-found by its ID chain, because resolving can rename the instance.
        private IComponent2 ResolveForGeometry(IAssemblyDoc asm, IComponent2 comp, out string errCode, out string errMsg)
        {
            errCode = null; errMsg = null;
            var chain = new List<IComponent2>();
            for (var c = comp; c != null && chain.Count < 50; c = c.GetParent() as IComponent2) chain.Insert(0, c);

            bool anyLight = false;
            foreach (var c in chain)
            {
                int s = SafeSuppression(c);
                if (s == 0)
                {
                    errCode = "COMPONENT_SUPPRESSED";
                    errMsg = "'" + ComponentPath(c) + "' is suppressed, so '" + ComponentPath(comp)
                        + "' has no geometry. Resolve it first: set_component_state(components, suppression='resolved').";
                    return null;
                }
                if (IsLightweightState(s)) anyLight = true;
            }
            if (!anyLight) return comp;

            var ids = ComponentIdChain(comp);
            string before = ComponentPath(comp);
            foreach (var c in chain)
            {
                if (!IsLightweightState(SafeSuppression(c))) continue;
                try { c.SetSuppression2(3 /* swComponentResolved */); } catch { }
            }
            var again = ComponentByIdChain(asm, ids) ?? comp;
            int after = SafeSuppression(again);
            if (IsLightweightState(after) || after == 0)
            {
                errCode = "RESOLVE_FAILED";
                errMsg = "'" + before + "' is lightweight and could not be resolved (state: " + SuppressionName(again)
                    + "). Check that its file exists (component_status) and resolve it with set_component_state.";
                return null;
            }
            string now = ComponentPath(again);
            _autoResolved.Add(string.Equals(now, before, StringComparison.OrdinalIgnoreCase) ? now : before + " -> " + now);
            return again;
        }

        // Resolves every lightweight component of an assembly. Returns how many were lightweight.
        private static int ResolveAllLightweightComponents(IAssemblyDoc asm)
        {
            int n = LightweightCount(asm);
            if (n > 0) { try { asm.ResolveAllLightWeightComponents(false); } catch { } }
            return n;
        }

        private static int LightweightCount(IAssemblyDoc asm)
        {
            try { return asm.GetLightWeightComponentCount(); } catch { return 0; }
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        // "dtm04-6p_Felix-2" -> "dtm04-6p_felix" (instance number dropped, lower case).
        private static string InstanceBase(string leaf)
        {
            leaf = (leaf ?? "").ToLowerInvariant();
            int i = leaf.LastIndexOf('-');
            if (i > 0 && i < leaf.Length - 1)
            {
                bool digits = true;
                for (int k = i + 1; k < leaf.Length; k++) if (!char.IsDigit(leaf[k])) { digits = false; break; }
                if (digits) return leaf.Substring(0, i);
            }
            return leaf;
        }

        // Error text for a name that matches no component, with the closest instance paths: an
        // instance can be named differently once its sub-assembly is resolved or made lightweight.
        private string ComponentNotFoundMessage(IAssemblyDoc asm, string name)
        {
            var close = new List<string>();
            string bas = InstanceBase(LeafName(name));
            try
            {
                if (bas.Length > 0 && asm.GetComponents(false) is object[] all)
                {
                    var same = new List<string>();
                    var near = new List<string>();
                    foreach (var o in all)
                    {
                        if (!(o is IComponent2 c)) continue;
                        string path = ComponentPath(c);
                        string b = InstanceBase(LeafName(path));
                        if (b == bas) same.Add(path);
                        else if (b.Contains(bas) || bas.Contains(b)
                                 || EditDistance(b, bas) <= Math.Max(1, Math.Min(b.Length, bas.Length) / 4)) near.Add(path);
                    }
                    close.AddRange(same);
                    close.AddRange(near);
                }
            }
            catch { }
            string msg = "No component '" + name + "' in the active assembly.";
            if (close.Count > 0)
            {
                int n = Math.Min(close.Count, 8);
                msg += " Closest instance paths: " + string.Join(", ", close.GetRange(0, n))
                    + (close.Count > n ? " (+" + (close.Count - n) + " more)" : "")
                    + ". Instance names can change when a sub-assembly is resolved or made lightweight.";
            }
            else msg += " List instance paths with component_status(match=...).";
            return msg;
        }

        private static int EditDistance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        // What's Wrong often reports code 1 ("unknown") for a mate; the feature's own error code
        // says more (46 over-defined, 47 ill-defined, 48 broken ...).
        private static int BetterErrorCode(IFeature f, int code)
        {
            if (code > 1 || f == null) return code;
            try
            {
                bool warning;
                int own = f.GetErrorCode2(out warning);
                if (own > 1) return own;
            }
            catch { }
            return code;
        }

        private void AddAutoResolved(JObject result)
        {
            if (_autoResolved.Count == 0) return;
            result["resolved_from_lightweight"] = new JArray(_autoResolved.ToArray());
        }
    }
}
