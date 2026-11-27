using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Infrastructure;

namespace SolidworksExecution.Services
{
    // Which SolidWorks instance a call drives (KNOWN-LIMITATIONS #14, second case).
    //
    // Marshal.GetActiveObject("SldWorks.Application") returns the instance holding the class's
    // active-object registration: the FIRST one that registered. Observed: SolidWorks crashed, the
    // server launched a replacement through COM, the user later closed that window and started
    // SolidWorks again. The closed instance stayed alive (this server still held a reference to it),
    // kept the class registration and lost its "SolidWorks_PID_<pid>" ROT entry; the user's new
    // instance registered only "SolidWorks_PID_<pid>". Every tool call then drove the stale instance
    // the user could not see (no Routing add-in, documents opened where nobody saw them).
    //
    // Rule: every live SolidWorks registers "SolidWorks_PID_<pid>" in the ROT. When the
    // GetActiveObject instance has no such entry while another instance does, bind that one (the
    // most recently started) through its ROT entry instead, and release the stale reference so the
    // stale process can exit. LastAttachNote says what happened (reported by ensure_ready / health).
    public partial class SolidWorksService
    {
        internal static string LastAttachNote;
        private static int _cachedSwPid = -1;

        // Invalidates static CAD caches whenever the attached SolidWorks process changes
        // (so restarting SLDWORKS.EXE never leaves stale COM face handles or update stamps).
        private static void SyncCachesToSwPid(ISldWorks sw)
        {
            if (sw == null) return;
            int pid = -1;
            try { pid = sw.GetProcessID(); } catch { }
            if (pid <= 0 || pid == _cachedSwPid) return;
            if (_cachedSwPid != -1)
            {
                int faces = ClearFaceCache();
                ClearOccupancyCaches(out int boxes, out int tess);
                ExecLog.Write($"SolidWorks PID changed ({_cachedSwPid} -> {pid}): cleared caches (faces={faces}, boxes={boxes}, tess={tess})");
            }
            _cachedSwPid = pid;
        }

        [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
        [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

        private static readonly Regex RotPidName = new Regex(@"^SolidWorks_PID_(\d+)$", RegexOptions.IgnoreCase);

        // PID -> ROT display name of every "SolidWorks_PID_<pid>" entry.
        private static Dictionary<int, string> RotSolidWorksPids()
        {
            var res = new Dictionary<int, string>();
            try
            {
                if (GetRunningObjectTable(0, out var rot) != 0 || rot == null) return res;
                rot.EnumRunning(out var en);
                CreateBindCtx(0, out var ctx);
                var mk = new IMoniker[1];
                while (en.Next(1, mk, IntPtr.Zero) == 0)
                {
                    try
                    {
                        mk[0].GetDisplayName(ctx, null, out string name);
                        var m = RotPidName.Match(name ?? "");
                        if (m.Success) res[int.Parse(m.Groups[1].Value)] = name;
                    }
                    catch { }
                }
            }
            catch { }
            return res;
        }

        private static ISldWorks BindRotEntry(string displayName)
        {
            try
            {
                if (GetRunningObjectTable(0, out var rot) != 0 || rot == null) return null;
                rot.EnumRunning(out var en);
                CreateBindCtx(0, out var ctx);
                var mk = new IMoniker[1];
                while (en.Next(1, mk, IntPtr.Zero) == 0)
                {
                    mk[0].GetDisplayName(ctx, null, out string name);
                    if (!string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                    rot.GetObject(mk[0], out object o);
                    return o as ISldWorks;
                }
            }
            catch { }
            return null;
        }

        private static DateTime StartTimeOf(int pid)
        {
            try { return Process.GetProcessById(pid).StartTime; } catch { return DateTime.MinValue; }
        }

        // The instance a call should drive (see above). Throws COMException like GetActiveObject
        // when no SolidWorks can be bound at all.
        private static ISldWorks AttachPreferred()
        {
            ISldWorks active = null;
            COMException activeErr = null;
            try { active = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
            catch (COMException ex) { activeErr = ex; }

            int activePid = -1;
            if (active != null) { try { activePid = active.GetProcessID(); } catch { } }
            var registered = RotSolidWorksPids();
            if (registered.Count == 0 || (activePid > 0 && registered.ContainsKey(activePid)))
            {
                LastAttachNote = null;
                if (active == null) throw activeErr ?? new COMException("SolidWorks is not running.");
                return active;
            }
            // The class registration points to an instance without its own ROT entry (or nowhere):
            // drive the most recently started registered instance instead.
            foreach (var pid in registered.Keys.OrderByDescending(StartTimeOf))
            {
                var b = BindRotEntry(registered[pid]);
                if (b == null) continue;
                try { b.RevisionNumber(); } catch { continue; }
                if (active != null)
                {
                    LastAttachNote = $"SldWorks.Application pointed to PID {activePid}, an instance with no " +
                        "SolidWorks_PID ROT entry (stale: closed by the user, kept alive by a COM reference); " +
                        $"attached to PID {pid} instead. Close PID {activePid} if it is still running.";
                    try { Marshal.ReleaseComObject(active); } catch { }
                }
                else LastAttachNote = $"GetActiveObject failed; attached to PID {pid} through its ROT entry.";
                return b;
            }
            if (active == null) throw activeErr ?? new COMException("SolidWorks is not running.");
            return active;
        }

        // Diagnostics for ensure_ready / health: the PID this server drives and every SolidWorks process.
        private void AddInstanceInfo(Dictionary<string, object> result)
        {
            try
            {
                int pid = _solidWorks != null ? _solidWorks.GetProcessID() : -1;
                if (pid > 0) result["attachedPid"] = pid;
                var procs = Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToArray();
                if (procs.Length > 1)
                    result["instanceWarning"] = $"{procs.Length} SolidWorks processes ({string.Join(", ", procs)}); " +
                        $"tools drive PID {pid}. Close the others: two instances with the same files open can overwrite each other's saves.";
                if (LastAttachNote != null) result["attachNote"] = LastAttachNote;
            }
            catch { }
        }
    }
}
