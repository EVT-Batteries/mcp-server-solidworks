using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // SolidWorksService partial: occupancy_grid (read-only). Voxelizes the real tessellated geometry
    // of the components in a box region of the active assembly, so a route planner sees the free
    // space (a sheet-metal bracket's bounding box fills a whole corridor; its faces do not).
    //
    // COM cost is per object crossing (~20-30 ms), not per call, so the triangles come in as few
    // array calls as possible: IComponent2.GetTessTriangles for a lightweight component (SOLIDWORKS:
    // "available only when the component is loaded as lightweight"), IPartDoc.GetTessTriangles for
    // a resolved part (one call per file, part coordinates), and IFace2.GetTessTriangles per face of
    // GetBodies3 (solid and sheet bodies) as the fallback. Triangles are cached per (file,
    // configuration) in the part's coordinates, checked against the model's update stamp.
    //
    // Also the region helpers and the per-assembly component box cache shared with component_status.
    public partial class SolidWorksService
    {
        private const long OccupancyMaxVoxels = 8000000;

        // ---------------------------------------------------------------- region and transforms

        // Any-length list of numbers: a JSON array or a JSON-array string; null when absent.
        private static double[] ParseNumberList(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            JArray a;
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.Length == 0 || s == "[]") return null;
                a = JArray.Parse(s);
            }
            else a = t as JArray;
            if (a == null || a.Count == 0) return null;
            return a.Select(x => (double)x).ToArray();
        }

        // A component's transform to the top assembly (Transform2.ArrayData: [0..8] rotation row
        // major, [9..11] translation; row vectors: world = p R + t), or null.
        private static double[] XformOf(IComponent2 c)
        {
            try
            {
                var xf = c.Transform2;
                if (xf == null) return null;
                object ad = xf.ArrayData;
                var d = ad as double[];
                if (d == null && ad is object[] oa) d = Array.ConvertAll(oa, o => Convert.ToDouble(o));
                return d != null && d.Length >= 12 ? d : null;
            }
            catch { return null; }
        }

        private static double[] ApplyXf(double[] a, double x, double y, double z)
        {
            return new[]
            {
                x * a[0] + y * a[3] + z * a[6] + a[9],
                x * a[1] + y * a[4] + z * a[7] + a[10],
                x * a[2] + y * a[5] + z * a[8] + a[11],
            };
        }

        // The affine (12 numbers, same layout) taking component coordinates to frame coordinates:
        // f = (p Rc + tc - tf) Rf^T. frame null = the top assembly.
        private static double[] ComposeToFrame(double[] c, double[] f)
        {
            if (f == null) return c;
            var m = new double[12];
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                {
                    double s = 0;
                    for (int j = 0; j < 3; j++) s += c[3 * i + j] * f[3 * k + j];
                    m[3 * i + k] = s;
                }
            for (int k = 0; k < 3; k++)
            {
                double s = 0;
                for (int j = 0; j < 3; j++) s += (c[9 + j] - f[9 + j]) * f[3 * k + j];
                m[9 + k] = s;
            }
            return m;
        }

        // region (6 numbers, frame coordinates) -> sorted region and its world AABB (the 8 corners
        // mapped by the frame component's transform). frameXf null = world.
        private bool RegionToWorld(IAssemblyDoc asm, JToken regionTok, string frameName,
            out double[] region, out double[] worldBox, out double[] frameXf, out string errCode, out string errMsg)
        {
            region = null; worldBox = null; frameXf = null; errCode = null; errMsg = null;
            double[] r;
            try { r = ParseNumberList(regionTok); }
            catch (Exception ex) { errCode = "INVALID_PARAMETER"; errMsg = "region is not a JSON list of numbers: " + ex.Message; return false; }
            if (r == null || r.Length != 6 || r.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
            { errCode = "INVALID_PARAMETER"; errMsg = "region needs 6 numbers [x0,y0,z0,x1,y1,z1] (m)."; return false; }
            region = new[]
            {
                Math.Min(r[0], r[3]), Math.Min(r[1], r[4]), Math.Min(r[2], r[5]),
                Math.Max(r[0], r[3]), Math.Max(r[1], r[4]), Math.Max(r[2], r[5]),
            };
            if (!string.IsNullOrWhiteSpace(frameName))
            {
                var fc = FindComponentByName(asm, frameName.Trim());
                if (fc == null)
                { errCode = "COMPONENT_NOT_FOUND"; errMsg = "frame component: " + ComponentNotFoundMessage(asm, frameName.Trim()); return false; }
                frameXf = XformOf(fc);
                if (frameXf == null)
                { errCode = "NO_TRANSFORM"; errMsg = "frame component '" + frameName + "' has no transform (suppressed?)."; return false; }
            }
            if (frameXf == null) { worldBox = (double[])region.Clone(); return true; }
            var wb = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            for (int k = 0; k < 8; k++)
            {
                var w = ApplyXf(frameXf, region[(k & 1) != 0 ? 3 : 0], region[(k & 2) != 0 ? 4 : 1], region[(k & 4) != 0 ? 5 : 2]);
                for (int i = 0; i < 3; i++) { wb[i] = Math.Min(wb[i], w[i]); wb[i + 3] = Math.Max(wb[i + 3], w[i]); }
            }
            worldBox = wb;
            return true;
        }

        // ---------------------------------------------------------------- component box cache

        // Component world boxes of an assembly by instance path, kept while the assembly's update
        // stamp is unchanged (GetBox is one cheap call, but 1,500 of them plus the walk add up).
        private sealed class BoxCacheEntry
        {
            public int Stamp;
            public Dictionary<string, double[]> Boxes;
            public DateTime LastUsed;
        }

        private const int BoxCacheAssemblies = 8;
        private static readonly Dictionary<string, BoxCacheEntry> _boxCache =
            new Dictionary<string, BoxCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static void ClearOccupancyCaches(out int boxes, out int tess)
        {
            boxes = _boxCache.Count;
            tess = _tessCache.Count;
            _boxCache.Clear();
            _tessCache.Clear();
        }

        // The box table of the active assembly (status "hit" when its stamp is unchanged, else
        // "built": a fresh, empty table filled as boxes are read).
        private static Dictionary<string, double[]> AssemblyBoxCache(IModelDoc2 doc, out string status)
        {
            string cfg = "";
            try { cfg = (doc.GetActiveConfiguration() as IConfiguration)?.Name ?? ""; } catch { }
            string key = SafePath(doc) + "|" + cfg;
            int stamp = -1;
            try { stamp = doc.GetUpdateStamp(); } catch { }
            BoxCacheEntry e;
            if (stamp != -1 && _boxCache.TryGetValue(key, out e) && e.Stamp == stamp)
            {
                e.LastUsed = DateTime.UtcNow;
                status = "hit";
                return e.Boxes;
            }
            status = "built";
            var boxes = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            if (stamp == -1) return boxes;
            if (_boxCache.Count >= BoxCacheAssemblies && !_boxCache.ContainsKey(key))
            {
                string oldest = _boxCache.OrderBy(kv => kv.Value.LastUsed).First().Key;
                _boxCache.Remove(oldest);
            }
            _boxCache[key] = new BoxCacheEntry { Stamp = stamp, Boxes = boxes, LastUsed = DateTime.UtcNow };
            return boxes;
        }

        private static double[] CachedComponentBox(Dictionary<string, double[]> cache, IComponent2 c, string path)
        {
            double[] b;
            if (cache != null && path != null && cache.TryGetValue(path, out b)) return b;
            b = ComponentBox(c);
            if (cache != null && path != null) cache[path] = b;
            return b;
        }

        // ---------------------------------------------------------------- triangle cache

        private sealed class TessEntry
        {
            public long Stamp;
            public float[] Local;
            public string Source;
            public DateTime LastUsed;
        }

        private const long TessCacheMaxFloats = 30000000;   // ~120 MB
        private static readonly Dictionary<string, TessEntry> _tessCache =
            new Dictionary<string, TessEntry>(StringComparer.OrdinalIgnoreCase);

        private static void TessCacheStore(string key, long stamp, float[] local, string source)
        {
            if (local == null || local.Length > TessCacheMaxFloats) return;
            long total = _tessCache.Values.Sum(v => (long)v.Local.Length) + local.Length;
            while (total > TessCacheMaxFloats && _tessCache.Count > 0)
            {
                var oldest = _tessCache.OrderBy(kv => kv.Value.LastUsed).First();
                total -= oldest.Value.Local.Length;
                _tessCache.Remove(oldest.Key);
            }
            _tessCache[key] = new TessEntry { Stamp = stamp, Local = local, Source = source, LastUsed = DateTime.UtcNow };
        }

        private static float[] ToFloats(object o)
        {
            if (o is float[] f) return f;
            if (o is double[] d) return Array.ConvertAll(d, v => (float)v);
            if (o is object[] a) return Array.ConvertAll(a, v => Convert.ToSingle(v));
            return null;
        }

        private static long FileStamp(string file)
        {
            try { return string.IsNullOrEmpty(file) || !File.Exists(file) ? -1 : File.GetLastWriteTimeUtc(file).Ticks; }
            catch { return -1; }
        }

        private static double[] TriBox(float[] t, double[] xf)
        {
            var b = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                double x = t[i], y = t[i + 1], z = t[i + 2];
                if (xf != null)
                {
                    double wx = x * xf[0] + y * xf[3] + z * xf[6] + xf[9];
                    double wy = x * xf[1] + y * xf[4] + z * xf[7] + xf[10];
                    double wz = x * xf[2] + y * xf[5] + z * xf[8] + xf[11];
                    x = wx; y = wy; z = wz;
                }
                if (x < b[0]) b[0] = x; if (y < b[1]) b[1] = y; if (z < b[2]) b[2] = z;
                if (x > b[3]) b[3] = x; if (y > b[4]) b[4] = y; if (z > b[5]) b[5] = z;
            }
            return b;
        }

        private static double BoxGap(double[] a, double[] b)
        {
            double g = 0;
            for (int i = 0; i < 6; i++) g = Math.Max(g, Math.Abs(a[i] - b[i]));
            return g;
        }

        // Triangles whose coordinate frame the API does not document (component and face
        // tessellation): compare their box, as read and as mapped by the component transform,
        // with the component's world box, and return them in component coordinates.
        private static float[] ToComponentFrame(float[] raw, double[] worldBox, double[] xf, out string frame, out double gap)
        {
            gap = -1;
            if (worldBox == null || xf == null) { frame = "assumed_local"; return raw; }
            double gRaw = BoxGap(TriBox(raw, null), worldBox);
            double gXf = BoxGap(TriBox(raw, xf), worldBox);
            if (gRaw < gXf)
            {
                frame = "world";
                gap = gRaw;
                var local = new float[raw.Length];
                for (int i = 0; i + 2 < raw.Length; i += 3)
                {
                    double x = raw[i] - xf[9], y = raw[i + 1] - xf[10], z = raw[i + 2] - xf[11];
                    local[i] = (float)(x * xf[0] + y * xf[1] + z * xf[2]);
                    local[i + 1] = (float)(x * xf[3] + y * xf[4] + z * xf[5]);
                    local[i + 2] = (float)(x * xf[6] + y * xf[7] + z * xf[8]);
                }
                return local;
            }
            frame = "local";
            gap = gXf;
            return raw;
        }

        // Every face triangle of the component's solid and sheet bodies, one array call per face.
        private static float[] FaceTriangles(IComponent2 comp)
        {
            var all = new List<float>();
            for (int type = 0; type <= 1; type++)   // swSolidBody 0, swSheetBody 1
            {
                object info;
                object[] bodies = null;
                try { bodies = comp.GetBodies3(type, out info) as object[]; } catch { }
                if (bodies == null) continue;
                foreach (var b in bodies)
                {
                    if (!(b is IBody2 body)) continue;
                    object[] faces = null;
                    try { faces = body.GetFaces() as object[]; } catch { }
                    if (faces == null) continue;
                    foreach (var f in faces)
                    {
                        if (!(f is IFace2 face)) continue;
                        float[] t = null;
                        try { t = ToFloats(face.GetTessTriangles(true)); } catch { }
                        if (t != null) all.AddRange(t);
                    }
                }
            }
            return all.Count >= 9 ? all.ToArray() : null;
        }

        private sealed class OccupancyStats
        {
            public readonly Dictionary<string, int> Sources = new Dictionary<string, int>();
            public int CacheHits;
            public readonly JArray BoxMismatch = new JArray();
            public void Count(string s) { int n; Sources.TryGetValue(s, out n); Sources[s] = n + 1; }
        }

        // The component's triangles in its own coordinates (null: none), resolving a lightweight
        // component when its lightweight tessellation is empty. comp is replaced when resolved.
        private float[] ComponentTriangles(IAssemblyDoc asm, ref IComponent2 comp, string path, double[] worldBox,
            ref double[] xf, OccupancyStats st, out string skip)
        {
            skip = null;
            string file = SafeComponentPath(comp);
            string cfg = "";
            try { cfg = comp.ReferencedConfiguration ?? ""; } catch { }
            TessEntry e;
            string frame;
            double gap;

            if (IsLightweightState(SafeSuppression(comp)))
            {
                string lwKey = file + "|" + cfg + "|lightweight";
                long fstamp = FileStamp(file);
                if (fstamp != -1 && _tessCache.TryGetValue(lwKey, out e) && e.Stamp == fstamp)
                { e.LastUsed = DateTime.UtcNow; st.CacheHits++; st.Count(e.Source); return e.Local; }
                float[] raw = null;
                try { raw = ToFloats(comp.GetTessTriangles(true)); } catch { }
                if (raw != null && raw.Length >= 9)
                {
                    var local = ToComponentFrame(raw, worldBox, xf, out frame, out gap);
                    string src = "lightweight_" + frame;
                    st.Count(src);
                    if (fstamp != -1) TessCacheStore(lwKey, fstamp, local, src);
                    return local;
                }
                string ec, em;
                var r = ResolveForGeometry(asm, comp, out ec, out em);
                if (r == null) { skip = em ?? "lightweight, not resolved"; return null; }
                comp = r;
                xf = XformOf(comp) ?? xf;
            }

            IModelDoc2 md = null;
            try { md = comp.GetModelDoc2() as IModelDoc2; } catch { }
            string key = file + "|" + cfg;
            int stamp = -1;
            try { if (md != null) stamp = md.GetUpdateStamp(); } catch { }
            if (stamp != -1 && _tessCache.TryGetValue(key, out e) && e.Stamp == stamp)
            { e.LastUsed = DateTime.UtcNow; st.CacheHits++; st.Count(e.Source); return e.Local; }

            float[] loc = null;
            string source = null;
            if (md is IPartDoc pd)
            {
                // The part document's tessellation is that of its ACTIVE configuration: use it only
                // when that is the configuration the component references.
                string active = null;
                try { active = (md.GetActiveConfiguration() as IConfiguration)?.Name; } catch { }
                if (string.IsNullOrEmpty(cfg) || string.Equals(active, cfg, StringComparison.OrdinalIgnoreCase))
                {
                    try { loc = ToFloats(pd.GetTessTriangles(true)); } catch { }
                    if (loc != null && loc.Length >= 9)
                    {
                        source = "part";
                        // Part coordinates by definition; still compare with the world box so a
                        // live check shows a wrong assumption.
                        if (worldBox != null && xf != null)
                        {
                            double g = BoxGap(TriBox(loc, xf), worldBox);
                            double diag = Math.Sqrt(Math.Pow(worldBox[3] - worldBox[0], 2) + Math.Pow(worldBox[4] - worldBox[1], 2) + Math.Pow(worldBox[5] - worldBox[2], 2));
                            if (g > Math.Max(0.005, 0.05 * diag) && st.BoxMismatch.Count < 20)
                                st.BoxMismatch.Add(new JObject { ["component"] = path, ["source"] = source, ["gap_mm"] = Math.Round(g * 1000, 1) });
                        }
                    }
                    else loc = null;
                }
            }
            if (loc == null)
            {
                var raw = FaceTriangles(comp);
                if (raw != null)
                {
                    loc = ToComponentFrame(raw, worldBox, xf, out frame, out gap);
                    source = "faces_" + frame;
                }
            }
            if (loc == null) { skip = "no tessellation (not displayed yet, or no bodies)"; return null; }
            st.Count(source);
            if (stamp != -1) TessCacheStore(key, stamp, loc, source);
            return loc;
        }

        // ---------------------------------------------------------------- voxel grid

        private sealed class VoxelGrid
        {
            public double Ox, Oy, Oz, R;
            public int Nx, Ny, Nz;
            public int[] Owner;     // last component id that marked the voxel, 0 = free
            public long Triangles;  // triangles that reached the grid

            // Marks the voxels the component's triangles overlap; returns how many distinct voxels
            // this component marked. m: component -> frame affine.
            public int Mark(float[] t, double[] m, int id)
            {
                int count = 0;
                var u = new double[3][];
                for (int q = 0; q < 3; q++) u[q] = new double[3];
                double inv = 1.0 / R;
                for (int i = 0; i + 8 < t.Length; i += 9)
                {
                    for (int q = 0; q < 3; q++)
                    {
                        double x = t[i + 3 * q], y = t[i + 3 * q + 1], z = t[i + 3 * q + 2];
                        u[q][0] = (x * m[0] + y * m[3] + z * m[6] + m[9] - Ox) * inv;
                        u[q][1] = (x * m[1] + y * m[4] + z * m[7] + m[10] - Oy) * inv;
                        u[q][2] = (x * m[2] + y * m[5] + z * m[8] + m[11] - Oz) * inv;
                    }
                    count += Triangle(u[0], u[1], u[2], id);
                }
                return count;
            }

            private int N(int axis) { return axis == 0 ? Nx : axis == 1 ? Ny : Nz; }

            private int Set(int ix, int iy, int iz, int id)
            {
                int idx = ix + Nx * (iy + Ny * iz);
                if (Owner[idx] == id) return 0;
                Owner[idx] = id;
                return 1;
            }

            // One triangle in grid units (voxel (i,j,k) = the unit cube [i,i+1]x[j,j+1]x[k,k+1]).
            private int Triangle(double[] a, double[] b, double[] c, int id)
            {
                var lo = new int[3];
                var hi = new int[3];
                for (int ax = 0; ax < 3; ax++)
                {
                    double mn = Math.Min(a[ax], Math.Min(b[ax], c[ax]));
                    double mx = Math.Max(a[ax], Math.Max(b[ax], c[ax]));
                    if (mx < 0 || mn > N(ax)) return 0;
                    lo[ax] = Math.Max(0, (int)Math.Floor(mn));
                    hi[ax] = Math.Min(N(ax) - 1, (int)Math.Floor(mx));
                    if (lo[ax] > hi[ax]) return 0;
                }
                Triangles++;
                double e1x = b[0] - a[0], e1y = b[1] - a[1], e1z = b[2] - a[2];
                double e2x = c[0] - a[0], e2y = c[1] - a[1], e2z = c[2] - a[2];
                var n = new[] { e1y * e2z - e1z * e2y, e1z * e2x - e1x * e2z, e1x * e2y - e1y * e2x };
                double nlen = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                int count = 0;
                if (nlen < 1e-12)
                {
                    // Degenerate (a sliver or a point): sample its edges.
                    count += Segment(a, b, id);
                    count += Segment(b, c, id);
                    count += Segment(c, a, id);
                    return count;
                }
                int d = 0;
                if (Math.Abs(n[1]) > Math.Abs(n[d])) d = 1;
                if (Math.Abs(n[2]) > Math.Abs(n[d])) d = 2;
                int p = (d + 1) % 3, q = (d + 2) % 3;
                double nd = n[d], np = n[p], nq = n[q];
                double w = n[0] * a[0] + n[1] * a[1] + n[2] * a[2];
                var cell = new int[3];
                for (int ip = lo[p]; ip <= hi[p]; ip++)
                    for (int iq = lo[q]; iq <= hi[q]; iq++)
                    {
                        // The plane's extent along d over this column.
                        double t00 = (w - np * ip - nq * iq) / nd;
                        double t10 = (w - np * (ip + 1) - nq * iq) / nd;
                        double t01 = (w - np * ip - nq * (iq + 1)) / nd;
                        double t11 = (w - np * (ip + 1) - nq * (iq + 1)) / nd;
                        double tmin = Math.Min(Math.Min(t00, t10), Math.Min(t01, t11));
                        double tmax = Math.Max(Math.Max(t00, t10), Math.Max(t01, t11));
                        int k0 = Math.Max(lo[d], (int)Math.Floor(tmin));
                        int k1 = Math.Min(hi[d], (int)Math.Floor(tmax));
                        for (int k = k0; k <= k1; k++)
                        {
                            cell[d] = k; cell[p] = ip; cell[q] = iq;
                            if (TriBoxOverlap(cell[0] + 0.5, cell[1] + 0.5, cell[2] + 0.5, 0.5 + 1e-6, a, b, c))
                                count += Set(cell[0], cell[1], cell[2], id);
                        }
                    }
                return count;
            }

            private int Segment(double[] a, double[] b, int id)
            {
                double len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
                int steps = Math.Max(1, (int)Math.Ceiling(len / 0.25));
                int count = 0;
                for (int s = 0; s <= steps; s++)
                {
                    double f = (double)s / steps;
                    int ix = (int)Math.Floor(a[0] + f * (b[0] - a[0]));
                    int iy = (int)Math.Floor(a[1] + f * (b[1] - a[1]));
                    int iz = (int)Math.Floor(a[2] + f * (b[2] - a[2]));
                    if (ix < 0 || iy < 0 || iz < 0 || ix >= Nx || iy >= Ny || iz >= Nz) continue;
                    count += Set(ix, iy, iz, id);
                }
                return count;
            }

            // Separating-axis triangle / axis-aligned cube test (Akenine-Moller): the 3 box axes,
            // the triangle normal and the 9 edge x axis cross products.
            private static bool TriBoxOverlap(double cx, double cy, double cz, double h, double[] A, double[] B, double[] C)
            {
                double ax = A[0] - cx, ay = A[1] - cy, az = A[2] - cz;
                double bx = B[0] - cx, by = B[1] - cy, bz = B[2] - cz;
                double qx = C[0] - cx, qy = C[1] - cy, qz = C[2] - cz;
                if (Math.Min(ax, Math.Min(bx, qx)) > h || Math.Max(ax, Math.Max(bx, qx)) < -h) return false;
                if (Math.Min(ay, Math.Min(by, qy)) > h || Math.Max(ay, Math.Max(by, qy)) < -h) return false;
                if (Math.Min(az, Math.Min(bz, qz)) > h || Math.Max(az, Math.Max(bz, qz)) < -h) return false;
                double[] ex = { bx - ax, qx - bx, ax - qx };
                double[] ey = { by - ay, qy - by, ay - qy };
                double[] ez = { bz - az, qz - bz, az - qz };
                for (int e = 0; e < 3; e++)
                {
                    // axis = X x edge = (0, -ez, ey)
                    if (!AxisOverlap(0, -ez[e], ey[e], h, ax, ay, az, bx, by, bz, qx, qy, qz)) return false;
                    // axis = Y x edge = (ez, 0, -ex)
                    if (!AxisOverlap(ez[e], 0, -ex[e], h, ax, ay, az, bx, by, bz, qx, qy, qz)) return false;
                    // axis = Z x edge = (-ey, ex, 0)
                    if (!AxisOverlap(-ey[e], ex[e], 0, h, ax, ay, az, bx, by, bz, qx, qy, qz)) return false;
                }
                double nx = ey[0] * ez[1] - ez[0] * ey[1];
                double ny = ez[0] * ex[1] - ex[0] * ez[1];
                double nz = ex[0] * ey[1] - ey[0] * ex[1];
                return AxisOverlap(nx, ny, nz, h, ax, ay, az, bx, by, bz, qx, qy, qz);
            }

            private static bool AxisOverlap(double x, double y, double z, double h,
                double ax, double ay, double az, double bx, double by, double bz, double qx, double qy, double qz)
            {
                if (x == 0 && y == 0 && z == 0) return true;
                double p0 = x * ax + y * ay + z * az;
                double p1 = x * bx + y * by + z * bz;
                double p2 = x * qx + y * qy + z * qz;
                double r = h * (Math.Abs(x) + Math.Abs(y) + Math.Abs(z));
                return !(Math.Min(p0, Math.Min(p1, p2)) > r || Math.Max(p0, Math.Max(p1, p2)) < -r);
            }
        }

        // Voxels whose centre lies within `radius` voxels of an occupied voxel's centre (exact
        // squared Euclidean distance transform, Felzenszwalb-Huttenlocher, one pass per axis).
        private static bool[] Dilate(int[] owner, int nx, int ny, int nz, double radius)
        {
            int n = owner.Length;
            const float Inf = 1e20f;
            var g = new float[n];
            for (int i = 0; i < n; i++) g[i] = owner[i] != 0 ? 0f : Inf;
            int maxLen = Math.Max(nx, Math.Max(ny, nz));
            var f = new double[maxLen];
            var dd = new double[maxLen];
            var v = new int[maxLen];
            var z = new double[maxLen + 1];
            // x lines
            for (int iz = 0; iz < nz; iz++)
                for (int iy = 0; iy < ny; iy++)
                {
                    int b = nx * (iy + ny * iz);
                    for (int i = 0; i < nx; i++) f[i] = g[b + i];
                    Edt1D(f, nx, dd, v, z);
                    for (int i = 0; i < nx; i++) g[b + i] = (float)dd[i];
                }
            // y lines
            for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    for (int i = 0; i < ny; i++) f[i] = g[ix + nx * (i + ny * iz)];
                    Edt1D(f, ny, dd, v, z);
                    for (int i = 0; i < ny; i++) g[ix + nx * (i + ny * iz)] = (float)dd[i];
                }
            // z lines
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    for (int i = 0; i < nz; i++) f[i] = g[ix + nx * (iy + ny * i)];
                    Edt1D(f, nz, dd, v, z);
                    for (int i = 0; i < nz; i++) g[ix + nx * (iy + ny * i)] = (float)dd[i];
                }
            double r2 = radius * radius + 1e-9;
            var occ = new bool[n];
            for (int i = 0; i < n; i++) occ[i] = g[i] <= r2;
            return occ;
        }

        // d[q] = min_p ((q - p)^2 + f[p]) over p in [0, n).
        private static void Edt1D(double[] f, int n, double[] d, int[] v, double[] z)
        {
            bool any = false;
            for (int i = 0; i < n; i++) if (f[i] < 1e19) { any = true; break; }
            if (!any) { for (int i = 0; i < n; i++) d[i] = f[i]; return; }
            int k = 0;
            v[0] = 0;
            z[0] = double.NegativeInfinity;
            z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        // ---------------------------------------------------------------- component walk

        private sealed class OccLeaf
        {
            public IComponent2 C;
            public string Path;
            public double[] Box;
        }

        private static bool IsIgnored(string path, List<string> ignore)
        {
            foreach (var f in ignore)
                if (path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                    || (path + "/").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Leaf parts under `c` whose world box touches `worldBox` (grown by margin). Instance
        // paths are built on the way down (ComponentPath walks the parents: one COM object each).
        private void CollectOccupancyLeaves(IAssemblyDoc asm, IComponent2 c, string parentPath, double[] worldBox,
            double margin, List<string> ignore, bool includeHidden, Dictionary<string, double[]> boxCache,
            List<OccLeaf> leaves, List<string> skippedLight, ref int scanned, int depth)
        {
            if (c == null || depth > 30) return;
            int state = SafeSuppression(c);
            if (state == 0) return;
            string name = LastSegment(c.Name2 ?? "?");
            string path = parentPath.Length > 0 ? parentPath + "/" + name : name;
            if (IsIgnored(path, ignore)) return;
            if (!includeHidden && SafeVisible(c) == 0) return;
            scanned++;
            var box = CachedComponentBox(boxCache, c, path);
            bool light = IsLightweightState(state);
            if (box == null && light) { skippedLight.Add(path); return; }
            if (box != null && !BoxesWithin(box, worldBox, margin)) return;

            object[] kids = null;
            try { kids = c.GetChildren() as object[]; } catch { }
            bool isAssembly = SafeComponentPath(c).EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            if ((kids == null || kids.Length == 0) && isAssembly && light)
            {
                // A lightweight sub-assembly in the region: load it to see its parts.
                string ec, em;
                var r = ResolveForGeometry(asm, c, out ec, out em);
                if (r == null) { skippedLight.Add(path); return; }
                c = r;
                try { kids = c.GetChildren() as object[]; } catch { }
            }
            if (kids != null && kids.Length > 0)
            {
                foreach (var k in kids)
                    CollectOccupancyLeaves(asm, k as IComponent2, path, worldBox, margin, ignore, includeHidden,
                        boxCache, leaves, skippedLight, ref scanned, depth + 1);
                return;
            }
            if (isAssembly || box == null) return;
            try { if (c.IsEnvelope()) return; } catch { }
            leaves.Add(new OccLeaf { C = c, Path = path, Box = box });
        }

        // ---------------------------------------------------------------- the tool

        public ExecutionResponse OccupancyGrid(ToolRequest request)
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
                var clock = Stopwatch.StartNew();
                _autoResolved.Clear();
                var p = request.Params as JObject;
                double res = p?.Value<double?>("resolution") ?? 0.002;
                string frameName = (p?.Value<string>("frame_component") ?? "").Trim();
                var ignore = ParseNameList(p?["ignore"]);
                bool includeHidden = p?.Value<bool?>("include_hidden") ?? false;
                double dilate = p?.Value<double?>("dilate") ?? 0;
                double budget = p?.Value<double?>("time_budget_s") ?? 300;
                var deadline = budget > 0 ? DateTime.UtcNow.AddSeconds(budget) : DateTime.MaxValue;
                if (!(res > 0) || double.IsInfinity(res))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "resolution must be > 0 (m).");
                if (dilate < 0 || double.IsNaN(dilate))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "dilate must be >= 0 (m).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "occupancy_grid requires an active assembly.");

                double[] region, worldBox, frameXf;
                string ec, em;
                if (!RegionToWorld(asm, p?["region"], frameName, out region, out worldBox, out frameXf, out ec, out em))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), ec, em);

                int nx = Math.Max(1, (int)Math.Ceiling((region[3] - region[0]) / res - 1e-9));
                int ny = Math.Max(1, (int)Math.Ceiling((region[4] - region[1]) / res - 1e-9));
                int nz = Math.Max(1, (int)Math.Ceiling((region[5] - region[2]) / res - 1e-9));
                long total = (long)nx * ny * nz;
                if (total > OccupancyMaxVoxels)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "GRID_TOO_LARGE",
                        $"{nx} x {ny} x {nz} = {total} voxels (max {OccupancyMaxVoxels}): use a coarser resolution "
                        + $"(>= {Math.Ceiling(Math.Pow((region[3] - region[0]) * (region[4] - region[1]) * (region[5] - region[2]) / OccupancyMaxVoxels, 1.0 / 3) * 10000) / 10000} m) or a smaller region.");

                // 1. Leaf parts whose world box touches the region.
                string boxStatus;
                var boxCache = AssemblyBoxCache(modelDoc, out boxStatus);
                var leaves = new List<OccLeaf>();
                var skippedLight = new List<string>();
                int scanned = 0;
                double margin = dilate + res;
                if (asm.GetComponents(true) is object[] tops)
                    foreach (var o in tops)
                        CollectOccupancyLeaves(asm, o as IComponent2, "", worldBox, margin, ignore, includeHidden,
                            boxCache, leaves, skippedLight, ref scanned, 0);
                double walkS = clock.Elapsed.TotalSeconds;

                // 2. Their triangles, voxelized in the frame.
                var grid = new VoxelGrid
                {
                    Ox = region[0], Oy = region[1], Oz = region[2], R = res,
                    Nx = nx, Ny = ny, Nz = nz, Owner = new int[total],
                };
                var st = new OccupancyStats();
                var perComp = new List<KeyValuePair<string, int>>();
                var noTess = new JArray();
                var notProcessed = new List<string>();
                long trianglesRead = 0;
                int id = 0;
                foreach (var leaf in leaves)
                {
                    if (DateTime.UtcNow > deadline) { notProcessed.Add(leaf.Path); continue; }
                    var comp = leaf.C;
                    var xf = XformOf(comp);
                    string skip;
                    float[] tris;
                    try { tris = ComponentTriangles(asm, ref comp, leaf.Path, leaf.Box, ref xf, st, out skip); }
                    catch (Exception ex) { tris = null; skip = ex.Message; }
                    if (tris == null)
                    {
                        if (skip != null && skip.IndexOf("lightweight", StringComparison.OrdinalIgnoreCase) >= 0
                            && !skip.StartsWith("no tessellation")) skippedLight.Add(leaf.Path);
                        else if (noTess.Count < 50) noTess.Add(new JObject { ["component"] = leaf.Path, ["reason"] = skip });
                        continue;
                    }
                    if (xf == null) { if (noTess.Count < 50) noTess.Add(new JObject { ["component"] = leaf.Path, ["reason"] = "no transform" }); continue; }
                    trianglesRead += tris.Length / 9;
                    int n = grid.Mark(tris, ComposeToFrame(xf, frameXf), ++id);
                    if (n > 0) perComp.Add(new KeyValuePair<string, int>(leaf.Path, n));
                }

                // 3. Dilation, run-length encoding.
                int before = 0;
                for (int i = 0; i < total; i++) if (grid.Owner[i] != 0) before++;
                bool[] occ = null;
                if (dilate > 0 && before > 0) occ = Dilate(grid.Owner, nx, ny, nz, dilate / res);
                var rle = new JArray();
                int occupied = 0;
                int runStart = -1;
                for (int i = 0; i <= total; i++)
                {
                    bool on = i < total && (occ != null ? occ[i] : grid.Owner[i] != 0);
                    if (on) { occupied++; if (runStart < 0) runStart = i; }
                    else if (runStart >= 0) { rle.Add(new JArray(runStart, i - runStart)); runStart = -1; }
                }

                var comps = new JArray();
                foreach (var kv in perComp.OrderByDescending(kv => kv.Value).Take(200))
                    comps.Add(new JObject { ["component"] = kv.Key, ["voxels"] = kv.Value });

                var result = new JObject
                {
                    ["frame"] = frameXf == null ? "world" : frameName,
                    ["origin"] = new JArray(region[0], region[1], region[2]),
                    ["resolution"] = res,
                    ["dims"] = new JArray(nx, ny, nz),
                    ["occupied_rle"] = rle,
                    ["occupied_count"] = occupied,
                    ["components"] = comps,
                    ["skipped_lightweight"] = new JArray(skippedLight.Distinct().ToArray()),
                    ["triangles"] = grid.Triangles,
                    ["elapsed_s"] = Math.Round(clock.Elapsed.TotalSeconds, 2),
                    ["assembly"] = modelDoc.GetTitle(),
                    ["region"] = new JArray(region),
                    ["region_world_box"] = new JArray(worldBox.Select(v => Math.Round(v, 6))),
                    ["dilate"] = dilate,
                    ["occupied_before_dilate"] = before,
                    ["components_with_voxels"] = perComp.Count,
                    ["leaf_components"] = leaves.Count,
                    ["components_scanned"] = scanned,
                    ["triangles_read"] = trianglesRead,
                    ["tessellation_sources"] = JObject.FromObject(st.Sources),
                    ["tessellation_cache_hits"] = st.CacheHits,
                    ["box_cache"] = boxStatus,
                    ["walk_s"] = Math.Round(walkS, 2),
                };
                if (frameXf != null) result["frame_transform"] = new JArray(frameXf.Take(12).Select(v => Math.Round(v, 9)));
                if (ignore.Count > 0) result["ignore"] = new JArray(ignore.ToArray());
                if (noTess.Count > 0) result["no_tessellation"] = noTess;
                if (st.BoxMismatch.Count > 0) result["box_mismatch"] = st.BoxMismatch;
                if (notProcessed.Count > 0)
                {
                    result["truncated"] = true;
                    result["truncated_reason"] = $"time_budget_s {budget} s reached: {notProcessed.Count} components not voxelized (their space shows free). Raise time_budget_s or shrink the region.";
                    result["not_processed"] = new JArray(notProcessed.Take(100).ToArray());
                }
                AddAutoResolved(result);
                ExecLog.Write($"occupancy_grid: {nx}x{ny}x{nz} res {res}, {leaves.Count} leaves, {grid.Triangles} triangles, {occupied} occupied, {clock.Elapsed.TotalSeconds:F1} s");
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
    }
}
