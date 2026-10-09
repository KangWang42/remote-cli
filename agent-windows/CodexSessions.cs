using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteCli {
// Read-only ownership checks run outside the terminal input/report lock.
public sealed class CodexSessions {
    public sealed class Owner {
        public bool Known, Live, CanTakeover;
        public int Pid;
        public long Started;
        public string Host = "unknown";
        public string Reason = "正在确认电脑会话的归属，请稍后刷新";
    }
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public uint pid; public System.Runtime.InteropServices.ComTypes.FILETIME started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessInfo {
        public UniqueProcess process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string service;
        public uint type, status, session;
        [MarshalAs(UnmanagedType.Bool)] public bool restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint processes, IntPtr applications, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    readonly object gate = new object();
    Dictionary<string, Owner> cached = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
    bool refreshing;
    DateTime next = DateTime.MinValue;
    public static string Home { get { return Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"); } }
    public static string LockPath(string id) {
        if (!Regex.IsMatch(id ?? "", @"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z")) throw new ArgumentException("会话编号无效");
        return Path.Combine(Home, "thread-writer-locks", id + ".lock");
    }
    // Existence alone is insufficient: the Rust writer locks the file's bytes.
    static bool? Held(string path) {
        try {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                try { stream.Lock(0, Int64.MaxValue); stream.Unlock(0, Int64.MaxValue); return false; }
                catch (IOException ex) { return (ex.HResult & 0xffff) == 33 ? (bool?)true : null; }
            }
        } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }
    public static Owner Inspect(string id) {
        string path = LockPath(id); bool? held = Held(path);
        if (held == false) return new Owner { Known = true, Host = "", Reason = "" };
        if (held == null) return new Owner { Reason = "无法核实写入锁，请在电脑结束会话后重试，或在手机新开副本" };
        var result = new Owner { Known = true, Live = true, Reason = "无法确认独立 CLI 的归属，可在手机新开副本" };
        uint session;
        if (RmStartSession(out session, 0, new StringBuilder(33)) != 0) return result;
        try {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return result;
            uint needed, count = 8, reasons = 0; var records = new ProcessInfo[count];
            int code = RmGetList(session, out needed, ref count, records, ref reasons);
            if (code == 234 && needed <= 256) { count = needed; records = new ProcessInfo[count]; code = RmGetList(session, out needed, ref count, records, ref reasons); }
            if (code != 0 || count != 1) return result;
            var record = records[0];
            long started = ((long)record.process.started.dwHighDateTime << 32) | (uint)record.process.started.dwLowDateTime;
            using (var process = Process.GetProcessById((int)record.process.pid)) {
                if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != started) return result;
                result.Pid = process.Id; result.Started = started;
                if (!String.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase)) return result;
                string command = "";
                using (var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + process.Id))
                using (var rows = search.Get()) foreach (ManagementObject row in rows) using (row) command = Convert.ToString(row["CommandLine"]);
                if (String.IsNullOrWhiteSpace(command)) return result;
                if (Regex.IsMatch(command, @"(?:^|\s)(?:app-server|mcp-server)(?:\s|$)", RegexOptions.IgnoreCase)) {
                    result.Host = "shared";
                    result.Reason = "此对话的写入锁由桌面应用或编辑器后台保留，不代表窗口正在打开它；可在手机新开副本";
                    return result;
                }
                if (RemoteAncestor(process.Id)) {
                    result.Host = "remote";
                    result.Reason = "此对话在另一个远程终端后台运行，电脑上可能没有窗口；请在原远程终端继续或结束，也可在这里新开副本";
                    return result;
                }
                result.Host = "cli";
                result.CanTakeover = true; result.Reason = "接手会结束电脑上的这个 CLI 会话，正在执行的任务可能中断";
            }
        } catch { /* Unknown ownership cannot authorize process termination. */ }
        finally { RmEndSession(session); }
        return result;
    }
    // Other installed terminal hosts must not look like a standalone computer CLI, or be terminated here.
    static bool RemoteAncestor(int pid) {
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 16 && pid > 0 && seen.Add(pid); depth++) {
            int parent = 0;
            using (var search = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
            using (var rows = search.Get()) foreach (ManagementObject row in rows) using (row) parent = Convert.ToInt32(row["ParentProcessId"]);
            if (parent <= 0 || parent == pid) break;
            try {
                using (var child = Process.GetProcessById(pid)) using (var ancestor = Process.GetProcessById(parent)) {
                    // A reused parent PID is not evidence of ancestry.
                    if (ancestor.StartTime.ToUniversalTime() > child.StartTime.ToUniversalTime()) break;
                    string name = ancestor.ProcessName;
                    if (String.Equals(name, "RemoteCli", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "RemoteCliAgent", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "TerminalAgent", StringComparison.OrdinalIgnoreCase)) return true;
                }
            } catch (ArgumentException) { break; }
            catch (System.ComponentModel.Win32Exception) { break; }
            pid = parent;
        }
        return false;
    }
    public void Refresh() {
        lock (gate) { if (refreshing || DateTime.UtcNow < next) return; refreshing = true; next = DateTime.UtcNow.AddSeconds(5); }
        Task.Run(delegate {
            var fresh = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
            try {
                string folder = Path.Combine(Home, "thread-writer-locks");
                if (Directory.Exists(folder)) foreach (string path in Directory.GetFiles(folder, "*.lock")) {
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (Regex.IsMatch(id, @"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z")) fresh[id] = Inspect(id);
                }
                lock (gate) cached = fresh;
            } catch { /* Keep the last verified snapshot; actions always recheck. */ }
            finally { lock (gate) refreshing = false; }
        });
    }
    public Owner Get(string id) {
        lock (gate) { Owner owner; return cached.TryGetValue(id, out owner) ? owner : new Owner { Known = !File.Exists(LockPath(id)), Reason = "正在确认电脑会话的归属，请稍后刷新" }; }
    }
    public static bool Descends(int pid, IEnumerable<int> roots, Dictionary<int, int> parents) {
        var rootSet = new HashSet<int>(roots); var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid)) { if (rootSet.Contains(pid)) return true; int parent; if (!parents.TryGetValue(pid, out parent)) break; pid = parent; }
        return false;
    }
    public static void Takeover(string id, IEnumerable<int> protectedRoots, Dictionary<int, int> parents) {
        Owner owner = Inspect(id);
        if (!owner.Known) throw new InvalidOperationException(owner.Reason);
        if (!owner.Live) return;
        if (!owner.CanTakeover || Descends(owner.Pid, protectedRoots, parents)) throw new InvalidOperationException("无法安全结束这个电脑会话，请先在电脑结束，或在手机新开副本");
        // Process.Kill uses the opened process handle; never invoke a global name-based kill.
        using (var process = Process.GetProcessById(owner.Pid)) {
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.Started) throw new InvalidOperationException("会话进程已变化，请刷新后重试");
            Owner current = Inspect(id);
            if (!current.CanTakeover || current.Pid != owner.Pid || current.Started != owner.Started) throw new InvalidOperationException("会话归属已变化，请刷新后重试");
            process.Kill();
            if (!process.WaitForExit(5000)) throw new InvalidOperationException("电脑会话没有结束，已取消接手");
        }
        for (int n = 0; n < 50; n++) { if (Held(LockPath(id)) == false) return; Thread.Sleep(50); }
        throw new InvalidOperationException("写入锁尚未释放，已取消接手，请稍后重试");
    }
}
}
