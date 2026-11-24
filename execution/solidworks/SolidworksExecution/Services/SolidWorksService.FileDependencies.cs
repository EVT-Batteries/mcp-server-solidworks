using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: file_dependencies (read-only). Lists the files a part, assembly or
    // drawing references, down the whole tree, WITHOUT opening it: ISldWorks.GetDocumentDependencies2
    // reads the references stored in each file and resolves them with SOLIDWORKS' own search rules.
    // Each file is reported with its sync state in a cloud-synced folder (OneDrive / SharePoint
    // placeholders that are downloaded on first read), its read-only state, and the drawings with
    // the same name next to each model (the drawings Pack and Go picks up). Use it before opening a
    // large assembly, before Pack and Go, or to check that a copied tree is complete.
    public partial class SolidWorksService
    {
        // Windows file attribute bits not in System.IO.FileAttributes (Cloud Files API).
        private const int AttrOffline = 0x1000;
        private const int AttrRecallOnOpen = 0x40000;
        private const int AttrPinned = 0x80000;
        private const int AttrUnpinned = 0x100000;
        private const int AttrRecallOnDataAccess = 0x400000;

        private class DepFile
        {
            public string Path;
            public int Depth;
            public readonly SortedSet<string> ReferencedBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool Exists;
            public long Size;
            public string Sync;      // local | always_keep | cloud_only | missing
            public bool ReadOnlyAttr;
            public string ReadOnlyLocation;
            public bool Virtual;
            public bool Scanned;
            public string ScanNote;
            public bool IsDrawing;
        }

        public ExecutionResponse FileDependencies(ToolRequest request)
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
                string root = p?.Value<string>("path") ?? "";
                bool recursive = p?.Value<bool?>("recursive") ?? true;
                bool includeDrawings = p?.Value<bool?>("include_drawings") ?? true;
                bool readCloudOnly = p?.Value<bool?>("read_cloud_only") ?? false;
                string listMode = (p?.Value<string>("show") ?? "all").Trim().ToLowerInvariant();
                int limit = p?.Value<int?>("limit") ?? 1000;
                double budget = p?.Value<double?>("time_budget_s") ?? 120;
                var deadline = budget > 0 ? DateTime.UtcNow.AddSeconds(budget) : DateTime.MaxValue;

                if (listMode != "all" && listMode != "problems" && listMode != "none")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "show must be 'all', 'problems' or 'none'.");
                if (string.IsNullOrWhiteSpace(root))
                {
                    // Default: the active document's file.
                    var active = _solidWorks.IActiveDoc2 as IModelDoc2;
                    root = active?.GetPathName() ?? "";
                    if (string.IsNullOrEmpty(root))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "path (a .sldprt / .sldasm / .slddrw file) is required when no saved document is active.");
                }
                root = NormalizePath(root);
                if (!File.Exists(root))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", "File not found: " + root);

                var files = new Dictionary<string, DepFile>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<DepFile>();
                var rootFile = NewDepFile(root, 0);
                files[root] = rootFile;
                queue.Enqueue(rootFile);
                bool truncated = false;
                int apiCalls = 0;

                // Breadth-first, one level per call (traverse = false) so that every file knows
                // which documents reference it.
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    if (!f.Exists || f.Virtual) continue;
                    if (f.Depth > 0 && !recursive) continue;
                    if (f.Sync == "cloud_only" && !readCloudOnly && f.Depth > 0)
                    {
                        f.ScanNote = "not read: cloud-only (read_cloud_only=true downloads and reads it)";
                        continue;
                    }
                    if (DateTime.UtcNow > deadline) { truncated = true; break; }

                    string[] deps;
                    try
                    {
                        deps = DependencyPaths(_solidWorks.GetDocumentDependencies2(f.Path, false, true, false));
                        apiCalls++;
                        f.Scanned = true;
                    }
                    catch (COMException ex)
                    {
                        f.ScanNote = "GetDocumentDependencies2 failed: " + ex.Message;
                        continue;
                    }

                    foreach (var dp in deps)
                    {
                        string full = IsVirtualPath(dp) ? dp : NormalizePath(dp);
                        if (string.Equals(full, f.Path, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!files.TryGetValue(full, out var child))
                        {
                            child = NewDepFile(full, f.Depth + 1);
                            files[full] = child;
                            queue.Enqueue(child);
                        }
                        child.ReferencedBy.Add(System.IO.Path.GetFileName(f.Path));
                    }
                }

                // Drawings with the same name as a model, in the model's folder (Pack and Go's rule).
                int drawingCount = 0;
                if (includeDrawings)
                {
                    var models = files.Values.Where(x => x.Exists && !x.Virtual && !IsDrawingPath(x.Path)).ToList();
                    foreach (var m in models)
                    {
                        string dir = System.IO.Path.GetDirectoryName(m.Path);
                        string stem = System.IO.Path.GetFileNameWithoutExtension(m.Path);
                        string drw = System.IO.Path.Combine(dir ?? "", stem + ".slddrw");
                        if (!File.Exists(drw) || files.ContainsKey(NormalizePath(drw))) continue;
                        var d = NewDepFile(NormalizePath(drw), m.Depth);
                        d.IsDrawing = true;
                        d.ReferencedBy.Add("(same name as " + System.IO.Path.GetFileName(m.Path) + ")");
                        files[d.Path] = d;
                        drawingCount++;
                    }
                }

                // Summary by folder.
                var folders = new JArray();
                foreach (var g in files.Values.Where(x => !x.Virtual)
                             .GroupBy(x => System.IO.Path.GetDirectoryName(x.Path) ?? "", StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var fo = new JObject
                    {
                        ["folder"] = g.Key,
                        ["files"] = g.Count(),
                    };
                    foreach (var s in new[] { "local", "always_keep", "cloud_only", "missing" })
                    {
                        int n = g.Count(x => x.Sync == s);
                        if (n > 0) fo[s] = n;
                    }
                    int ro = g.Count(x => x.ReadOnlyAttr);
                    if (ro > 0) fo["read_only_attribute"] = ro;
                    string loc = g.Select(x => x.ReadOnlyLocation).FirstOrDefault(x => x != null);
                    if (loc != null) fo["read_only_location"] = loc;
                    folders.Add(fo);
                }

                var counts = new JObject
                {
                    ["files"] = files.Count,
                    ["models"] = files.Values.Count(x => !x.IsDrawing && !IsDrawingPath(x.Path)),
                    ["drawings_same_name"] = drawingCount,
                    ["missing"] = files.Values.Count(x => x.Sync == "missing"),
                    ["cloud_only"] = files.Values.Count(x => x.Sync == "cloud_only"),
                    ["always_keep"] = files.Values.Count(x => x.Sync == "always_keep"),
                    ["local"] = files.Values.Count(x => x.Sync == "local"),
                    ["virtual"] = files.Values.Count(x => x.Virtual),
                    ["read_only_attribute"] = files.Values.Count(x => x.ReadOnlyAttr),
                    ["in_read_only_location"] = files.Values.Count(x => x.ReadOnlyLocation != null),
                    ["total_mb"] = Math.Round(files.Values.Sum(x => x.Size) / 1048576.0, 1),
                };

                var list = new JArray();
                int listed = 0;
                foreach (var f in files.Values.OrderBy(x => x.Depth).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                {
                    bool problem = f.Sync == "missing" || f.Sync == "cloud_only" || f.ScanNote != null;
                    if (listMode == "none" || (listMode == "problems" && !problem)) continue;
                    if (listed >= limit) continue;
                    listed++;
                    var o = new JObject
                    {
                        ["path"] = f.Path,
                        ["depth"] = f.Depth,
                        ["sync"] = f.Sync,
                    };
                    if (f.Exists) o["size_kb"] = Math.Round(f.Size / 1024.0);
                    if (f.ReferencedBy.Count > 0) o["referenced_by"] = new JArray(f.ReferencedBy.ToArray());
                    if (f.IsDrawing) o["drawing"] = true;
                    if (f.Virtual) o["virtual"] = true;
                    if (f.ReadOnlyAttr) o["read_only_attribute"] = true;
                    if (f.ReadOnlyLocation != null) o["read_only_location"] = f.ReadOnlyLocation;
                    if (f.ScanNote != null) o["note"] = f.ScanNote;
                    list.Add(o);
                }

                var result = new JObject
                {
                    ["root"] = root,
                    ["recursive"] = recursive,
                    ["documents_read"] = apiCalls,
                    ["counts"] = counts,
                    ["folders"] = folders,
                };
                if (listMode != "none") result["files"] = list;
                if (listMode != "none" && listed >= limit) result["truncated_at"] = limit;
                var missing = files.Values.Where(x => x.Sync == "missing").Select(x => x.Path).ToArray();
                if (missing.Length > 0) result["missing"] = new JArray(missing);
                int unread = files.Values.Count(x => x.ScanNote != null && x.ScanNote.StartsWith("not read"));
                if (unread > 0)
                    result["unread_cloud_only"] = unread;
                if (truncated)
                {
                    result["truncated"] = true;
                    result["truncated_reason"] = $"time_budget_s {budget} s reached: files queued after it were not read.";
                }

                var activeDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = activeDoc?.GetTitle(),
                        DocumentType = activeDoc == null ? null
                            : activeDoc is IAssemblyDoc ? "ASSEMBLY" : activeDoc is IDrawingDoc ? "DRAWING" : "PART",
                        ActiveSketch = null,
                        Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
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

        private static DepFile NewDepFile(string path, int depth)
        {
            var f = new DepFile { Path = path, Depth = depth, IsDrawing = IsDrawingPath(path) };
            if (IsVirtualPath(path))
            {
                f.Virtual = true;
                f.Sync = "virtual";
                return f;
            }
            f.ReadOnlyLocation = ReadOnlyLocationOf(path);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { f.Sync = "missing"; return f; }
                f.Exists = true;
                f.Size = fi.Length;
                // Attributes are metadata: reading them does not download a cloud placeholder.
                int a = (int)fi.Attributes;
                f.ReadOnlyAttr = (a & (int)FileAttributes.ReadOnly) != 0;
                if ((a & (AttrRecallOnDataAccess | AttrRecallOnOpen | AttrOffline)) != 0) f.Sync = "cloud_only";
                else if ((a & AttrPinned) != 0) f.Sync = "always_keep";
                else f.Sync = "local";
            }
            catch (Exception)
            {
                f.Sync = "missing";
            }
            return f;
        }

        // GetDocumentDependencies2 returns name / path pairs (or name / path / read-only triples):
        // keep the entries that are full paths.
        private static string[] DependencyPaths(object raw)
        {
            if (!(raw is object[] arr)) return new string[0];
            var outList = new List<string>();
            foreach (var o in arr)
            {
                string s = o as string;
                if (string.IsNullOrEmpty(s)) continue;
                bool isPath = (s.Length > 2 && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) || s.StartsWith("\\\\");
                if (isPath) outList.Add(s);
            }
            return outList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool IsDrawingPath(string p) =>
            p.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);

        // Virtual components live inside their assembly (\VC~~\ temp paths, Part^Assembly names).
        private static bool IsVirtualPath(string p) =>
            p.IndexOf("\\VC~~\\", StringComparison.OrdinalIgnoreCase) >= 0
            || System.IO.Path.GetFileName(p).IndexOf('^') >= 0;

        private static string ReadOnlyLocationOf(string path)
        {
            string ro = System.Environment.GetEnvironmentVariable("SOLIDPILOT_READONLY_PATHS") ?? "";
            foreach (var frag in ro.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string f = frag.Trim();
                if (f.Length > 0 && path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return f;
            }
            return null;
        }
    }
}
