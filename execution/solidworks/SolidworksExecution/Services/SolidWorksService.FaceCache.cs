using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;

namespace SolidworksExecution.Services
{
    // Faces of a component's file, cached per (file, referenced configuration) and checked against
    // the model's update stamp. Every COM object SOLIDWORKS hands across processes costs ~20-30 ms
    // (measured 2026-11-25: listing the 2,890 faces of a relay model took 93 s, IGetSurface 23 ms
    // per face, a full record ~195 ms per face in assembly context: 562 s for that part). So:
    // - a miss lists the faces once and takes each face's box (one cheap call each);
    // - a face's full record (BuildFaceJson) is built only when a query reaches it, then kept;
    // - later queries on any instance of the same file make no per-face COM call.
    // Records are in the COMPONENT's coordinates, the same for every instance.
    public partial class SolidWorksService
    {
        private sealed class CachedFace
        {
            public int I;
            public IFace2 Face;
            public double[] Box;
            public JObject Rec;     // null until a query needs it
            public string Kind;     // from Rec
            public double Radius;   // from Rec

            public void EnsureRecord(SolidWorksService svc)
            {
                if (Rec != null) return;
                Rec = svc.BuildFaceJson(Face, I);
                bool planar = Rec.Value<bool?>("planar") ?? false;
                Kind = planar ? "plane" : (Rec.Value<string>("kind") ?? "other");
                Radius = Rec.Value<double?>("radius") ?? 0;
            }
        }

        private sealed class FaceCacheEntry
        {
            public int Stamp;
            public List<CachedFace> Faces;
            public double ListSeconds;
            public DateTime LastUsed;
        }

        private const int FaceCacheFiles = 24;
        private static readonly Dictionary<string, FaceCacheEntry> _faceCache =
            new Dictionary<string, FaceCacheEntry>(StringComparer.OrdinalIgnoreCase);

        // The component's faces (records built lazily), from the cache when the file's update stamp
        // is unchanged. status: "hit" or "listed"; listSeconds: the time the listing took.
        private List<CachedFace> ComponentFacesCached(IComponent2 comp, bool useCache,
            out string status, out double listSeconds)
        {
            var t = Stopwatch.StartNew();
            string key = (comp.GetPathName() ?? "") + "|" + (comp.ReferencedConfiguration ?? "");
            int stamp = -1;
            try { stamp = (comp.GetModelDoc2() as IModelDoc2)?.GetUpdateStamp() ?? -1; } catch { }
            double keyMs = t.Elapsed.TotalMilliseconds;
            FaceCacheEntry e;
            if (useCache && stamp != -1 && _faceCache.TryGetValue(key, out e) && e.Stamp == stamp)
            {
                e.LastUsed = DateTime.UtcNow;
                status = "hit";
                listSeconds = e.ListSeconds;
                ExecLog.Write($"face cache: hit {key} (key + stamp {keyMs:F0} ms)");
                return e.Faces;
            }
            var list = new List<CachedFace>();
            int idx = 0;
            object info;
            if (comp.GetBodies3((int)swBodyType_e.swSolidBody, out info) is object[] bodies)
                foreach (var b in bodies)
                {
                    if (!(b is IBody2 body) || !(body.GetFaces() is object[] fs)) continue;
                    foreach (var f in fs)
                    {
                        if (!(f is IFace2 face)) continue;
                        double[] box = null;
                        try { box = face.GetBox() as double[]; } catch { }
                        list.Add(new CachedFace { I = idx++, Face = face, Box = box });
                    }
                }
            listSeconds = Math.Round(t.Elapsed.TotalSeconds, 1);
            status = "listed";
            if (stamp != -1)
            {
                if (_faceCache.Count >= FaceCacheFiles && !_faceCache.ContainsKey(key))
                {
                    string oldest = null;
                    DateTime when = DateTime.MaxValue;
                    foreach (var kv in _faceCache)
                        if (kv.Value.LastUsed < when) { when = kv.Value.LastUsed; oldest = kv.Key; }
                    if (oldest != null) _faceCache.Remove(oldest);
                }
                _faceCache[key] = new FaceCacheEntry
                {
                    Stamp = stamp, Faces = list, ListSeconds = listSeconds, LastUsed = DateTime.UtcNow,
                };
            }
            ExecLog.Write($"face cache: listed {list.Count} faces of {key} in {listSeconds} s");
            return list;
        }

        // Faces passing the filter, with their records (built now where missing). On a COM error
        // (a stale face after the document was closed) the file's entry is dropped and read again.
        private JArray FilteredFaceRecords(IComponent2 comp, FaceFilter filter, bool useCache,
            out int total, out string status, out double listSeconds, out int built)
        {
            for (int attempt = 0; ; attempt++)
            {
                var faces = ComponentFacesCached(comp, useCache && attempt == 0, out status, out listSeconds);
                total = faces.Count;
                built = 0;
                var t = Stopwatch.StartNew();
                var arr = new JArray();
                try
                {
                    foreach (var cf in faces)
                    {
                        if (filter != null && !filter.AcceptsBox(cf)) continue;
                        if (cf.Rec == null) { cf.EnsureRecord(this); built++; }
                        if (filter != null && !filter.AcceptsRecord(cf)) continue;
                        arr.Add(cf.Rec.DeepClone());
                    }
                }
                catch (Exception ex) when (attempt == 0)
                {
                    ExecLog.Write($"face cache: stale entry dropped ({ex.Message}); reading again");
                    continue;
                }
                ExecLog.Write($"face cache: {arr.Count} of {total} faces returned, {built} records built in {t.Elapsed.TotalSeconds:F1} s");
                return arr;
            }
        }
    }
}
