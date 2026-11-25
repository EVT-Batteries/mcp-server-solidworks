using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace SolidworksExecution.Services
{
    // analyze_assembly(faces, prefilter={near, within, kind, radius_min, radius_max}): which faces
    // get a full record. All values in the COMPONENT's coordinates (m). Tests use only cheap
    // calls (the face box, the surface type, cylinder parameters) so a part with thousands of faces
    // is read in seconds; every face keeps its index (counted whether returned or not).
    public partial class SolidWorksService
    {
        private sealed class FaceFilter
        {
            public double[] Near;
            public double Within;
            public string Kind;
            public double RadiusMin;
            public double RadiusMax;
            public bool NearInAssembly;

            public static FaceFilter FromParams(JObject p)
            {
                var tok = p?["prefilter"];
                if (tok == null || tok.Type == JTokenType.Null) return null;
                JObject o = tok.Type == JTokenType.String ? JObject.Parse((string)tok) : tok as JObject;
                if (o == null) return null;
                var f = new FaceFilter
                {
                    Within = o.Value<double?>("within") ?? 0,
                    Kind = (o.Value<string>("kind") ?? "").ToLowerInvariant(),
                    RadiusMin = o.Value<double?>("radius_min") ?? 0,
                    RadiusMax = o.Value<double?>("radius_max") ?? 0,
                };
                if (o["near"] is JArray n && n.Count >= 3)
                    f.Near = new[] { (double)n[0], (double)n[1], (double)n[2] };
                f.NearInAssembly = string.Equals(o.Value<string>("frame") ?? "", "assembly", StringComparison.OrdinalIgnoreCase);
                if (f.Near == null && f.Kind == "" && f.RadiusMin <= 0 && f.RadiusMax <= 0) return null;
                return f;
            }

            // near given in the active assembly's frame: map it into the component's
            // (row vectors: world = p R + t, so p = (world - t) R^T).
            public void ToComponentFrame(IComponent2 comp)
            {
                if (Near == null || !NearInAssembly) return;
                try
                {
                    if (comp.Transform2?.ArrayData is double[] a && a.Length >= 12)
                    {
                        double x = Near[0] - a[9], y = Near[1] - a[10], z = Near[2] - a[11];
                        Near = new[]
                        {
                            x * a[0] + y * a[1] + z * a[2],
                            x * a[3] + y * a[4] + z * a[5],
                            x * a[6] + y * a[7] + z * a[8],
                        };
                    }
                }
                catch { }
                NearInAssembly = false;
            }

            public JObject Describe()
            {
                var o = new JObject();
                if (Near != null) { o["near"] = new JArray(Near[0], Near[1], Near[2]); o["within"] = Within; }
                if (Kind != "") o["kind"] = Kind;
                if (RadiusMin > 0) o["radius_min"] = RadiusMin;
                if (RadiusMax > 0) o["radius_max"] = RadiusMax;
                return o;
            }

            // The same tests on a cached face (no COM call): the box first, then the record.
            public bool AcceptsBox(CachedFace c)
            {
                if (Near == null || Within <= 0 || c.Box == null || c.Box.Length < 6) return true;
                var b = c.Box;
                double dx = Math.Max(0, Math.Max(b[0] - Near[0], Near[0] - b[3]));
                double dy = Math.Max(0, Math.Max(b[1] - Near[1], Near[1] - b[4]));
                double dz = Math.Max(0, Math.Max(b[2] - Near[2], Near[2] - b[5]));
                return dx * dx + dy * dy + dz * dz <= Within * Within;
            }

            public bool AcceptsRecord(CachedFace c)
            {
                if (Kind != "" && c.Kind != Kind) return false;
                if (RadiusMin > 0 || RadiusMax > 0)
                {
                    if (c.Kind != "cylinder") return false;
                    if (RadiusMin > 0 && c.Radius < RadiusMin) return false;
                    if (RadiusMax > 0 && c.Radius > RadiusMax) return false;
                }
                return true;
            }

            public bool Accepts(IFace2 face)
            {
                try
                {
                    if (Near != null && Within > 0 && face.GetBox() is double[] b && b.Length >= 6)
                    {
                        double dx = Math.Max(0, Math.Max(b[0] - Near[0], Near[0] - b[3]));
                        double dy = Math.Max(0, Math.Max(b[1] - Near[1], Near[1] - b[4]));
                        double dz = Math.Max(0, Math.Max(b[2] - Near[2], Near[2] - b[5]));
                        if (dx * dx + dy * dy + dz * dz > Within * Within) return false;
                    }
                    bool needRadius = RadiusMin > 0 || RadiusMax > 0;
                    if (Kind == "" && !needRadius) return true;
                    var surf = face.IGetSurface();
                    if (surf == null) return false;
                    string kind = surf.IsPlane() ? "plane" : surf.IsCylinder() ? "cylinder"
                        : surf.IsCone() ? "cone" : surf.IsSphere() ? "sphere" : "other";
                    if (Kind != "" && kind != Kind) return false;
                    if (!needRadius) return true;
                    if (kind != "cylinder") return false;
                    var cp = surf.CylinderParams as double[];
                    if (cp == null && surf.CylinderParams is object[] cpo) cp = Array.ConvertAll(cpo, x => (double)x);
                    if (cp == null || cp.Length < 7) return false;
                    double r = cp[6];
                    if (RadiusMin > 0 && r < RadiusMin) return false;
                    if (RadiusMax > 0 && r > RadiusMax) return false;
                    return true;
                }
                catch { return true; }
            }
        }
    }
}
