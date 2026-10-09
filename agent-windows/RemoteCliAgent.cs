using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace RemoteCli {
// A real Windows terminal, with cursor movement, interactive menus and UTF-8.
public sealed class PseudoTerminal : IDisposable {
    [StructLayout(LayoutKind.Sequential)] public struct Coord { public short X, Y; public Coord(int x, int y) { X = (short)x; Y = (short)y; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo {
        public int cb; public string reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2; public IntPtr reservedPtr, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo startup; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] static extern bool SetStdHandle(int kind, IntPtr handle);
    static readonly object creationGate = new object();
    readonly object gate = new object();
    IntPtr console;
    FileStream input, output;
    Process process;
    bool disposed;
    public int Id { get { return process.Id; } }
    public bool Closed { get; private set; }
    public bool Drained { get; private set; }   // the reader has delivered everything the program wrote
    public string ReadError = "";
    public int? ExitCode;
    public bool Alive { get { try { return process != null && !process.HasExited; } catch { return false; } } }
    public int Cols = 80, Rows = 24;

    public PseudoTerminal(string command, string cwd, int cols, int rows, Action<string> receive) {
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero, attributes = IntPtr.Zero, environment = IntPtr.Zero;
        bool initialized = false;
        try {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0) || !CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) throw new IOException("终端管道创建失败");
            int result = CreatePseudoConsole(new Coord(cols, rows), inRead, outWrite, 0, out console);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            input = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 4096, false); inWrite = IntPtr.Zero;
            output = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 4096, false); outRead = IntPtr.Zero;
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new IOException("终端属性初始化失败");
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020016), console, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new IOException("终端属性设置失败");
            StartupInfoEx startup = new StartupInfoEx(); startup.startup.cb = Marshal.SizeOf(typeof(StartupInfoEx)); startup.attributes = attributes;
            // Preserve the owner's CLI setup, but never inherit a parent coding session.
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[Convert.ToString(e.Key)] = Convert.ToString(e.Value);
            foreach (string key in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" }) env.Remove(key);
            env["TERM"] = "xterm-256color"; env["COLORTERM"] = "truecolor";
            environment = Marshal.StringToHGlobalUni(String.Join("\0", env.Select(e => e.Key + "=" + e.Value)) + "\0\0");
            ProcessInfo info;
            lock (creationGate) {
                IntPtr stdin = GetStdHandle(-10), stdout = GetStdHandle(-11), stderr = GetStdHandle(-12);
                try {
                    SetStdHandle(-10, IntPtr.Zero); SetStdHandle(-11, IntPtr.Zero); SetStdHandle(-12, IntPtr.Zero);
                    if (!CreateProcess(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400, environment, cwd, ref startup, out info))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                } finally { SetStdHandle(-10, stdin); SetStdHandle(-11, stdout); SetStdHandle(-12, stderr); }
            }
            CloseHandle(inRead); inRead = IntPtr.Zero;
            CloseHandle(outWrite); outWrite = IntPtr.Zero;
            CloseHandle(info.process); CloseHandle(info.thread);
            process = Process.GetProcessById(info.processId); Cols = cols; Rows = rows;
            Task.Run(delegate {
                byte[] bytes = new byte[8192]; char[] chars = new char[8192]; Decoder decoder = Encoding.UTF8.GetDecoder();
                try { int n; while ((n = output.Read(bytes, 0, bytes.Length)) > 0) { int count = decoder.GetChars(bytes, 0, n, chars, 0, false); if (count > 0) receive(new string(chars, 0, count)); } }
                catch (IOException ex) { ReadError = ex.Message; } catch (ObjectDisposedException) { }
                finally { Drained = true; Closed = true; }
            });
            Task.Run(delegate { try { process.WaitForExit(); ExitCode = process.ExitCode; } catch { } finally { CloseConsole(); Closed = true; } });
        } catch { Dispose(); throw; }
        finally {
            foreach (IntPtr h in new[] { inRead, inWrite, outRead, outWrite }) if (h != IntPtr.Zero) CloseHandle(h);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }
    public void Write(string text) { lock (gate) { if (disposed || Closed) throw new IOException("终端已结束"); byte[] b = Encoding.UTF8.GetBytes(text); input.Write(b, 0, b.Length); input.Flush(); } }
    public void Resize(int cols, int rows) { lock (gate) { if (console == IntPtr.Zero) return; Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, new Coord(cols, rows))); Cols = cols; Rows = rows; } }
    void CloseConsole() { IntPtr h; lock (gate) { h = console; console = IntPtr.Zero; } if (h != IntPtr.Zero) ClosePseudoConsole(h); }
    public void Dispose() {
        lock (gate) { if (disposed) return; disposed = true; }
        try { if (process != null && !process.HasExited) { using (Process kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit(10000); } } catch { }
        CloseConsole();
        if (input != null) input.Dispose();
        if (output != null) output.Dispose();
        Closed = true;
    }
}

public sealed class TerminalAgent {
    string Origin = "";
    readonly string dataDir;
    public TerminalAgent(string dataFolder = null) { dataDir = dataFolder ?? DefaultData; }
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    readonly string instance = Guid.NewGuid().ToString("N");
    readonly Dictionary<string, LiveTerminal> terminals = new Dictionary<string, LiveTerminal>();
    readonly Dictionary<string, Dictionary<string, object>> completed = new Dictionary<string, Dictionary<string, object>>();
    readonly HashSet<string> reported = new HashSet<string>();
    readonly object gate = new object();
    HttpClient client;
    DateTime nextLogin = DateTime.MinValue;
    public sealed class LiveTerminal {
        public string Id, Tool, Dir, Session = "", Status = ""; public PseudoTerminal Pty; public long Seq;
        public DateTime Started = DateTime.UtcNow, ClosedSeen = DateTime.MinValue;
        public StringBuilder Pending = new StringBuilder();   // read from the program, not yet numbered
        public List<Dictionary<string, object>> Output = new List<Dictionary<string, object>>();
    }
    public sealed class SessionInfo { public string Id, Tool, Dir, Title, Status = "", Host = ""; public long Updated; public DateTime Created; public bool Live, CanTakeover, OwnershipKnown; public string TakeoverReason = ""; }
    sealed class TitleCache { public long Stamp, Length; public string Title; }
    readonly Dictionary<string, TitleCache> titles = new Dictionary<string, TitleCache>();
    readonly CodexSessions codexSessions = new CodexSessions();
    readonly Dictionary<string, string> codexFolders = new Dictionary<string, string>();
    Dictionary<string, string> codexNames = new Dictionary<string, string>();
    long codexNamesStamp;
    List<SessionInfo> sessions = new List<SessionInfo>();
    List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
    readonly Dictionary<string, string> claudeFolders = new Dictionary<string, string>();
    string sentSessions = ""; DateTime sessionsSent = DateTime.MinValue;
    // Released when a program wrote to its terminal or the relay holds a new operation: the next report goes out at once.
    readonly SemaphoreSlim wake = new SemaphoreSlim(0);
    bool soon;
    // Terminals and finished operations are worked on by the report loop and by the held request; one at a time.
    readonly object work = new object();
    DateTime lastInput = DateTime.MinValue, lastScan = DateTime.MinValue, toolsAt = DateTime.MinValue;
    string[] tools = new string[0];
    void Wake() { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    DateTime nextScan = DateTime.MinValue, lastActivity = DateTime.MinValue;
    const string Uuid = @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry {
        public uint size, usage, processId; public IntPtr heap; public uint module, threads, parentId; public int priority; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string file;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseSnapshot(IntPtr handle);
    public static Dictionary<int, int> Parents() {
        var map = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return map;
        try {
            var entry = new ProcessEntry { size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            if (Process32FirstW(snapshot, ref entry)) do { map[(int)entry.processId] = (int)entry.parentId; } while (Process32NextW(snapshot, ref entry));
        } finally { CloseSnapshot(snapshot); }
        return map;
    }
    static string ReadPart(string path, bool tail, int limit) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (tail && stream.Length > limit) stream.Seek(-limit, SeekOrigin.End);
            byte[] bytes = new byte[(int)Math.Min(limit, stream.Length - stream.Position)]; int read = 0, n;
            while (read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0) read += n;
            return Encoding.UTF8.GetString(bytes, 0, read);
        }
    }
    // The value of a JSON string field, found without parsing lines that may be megabytes long or cut off.
    static string JsonText(string text, string field, bool last) {
        var matches = Regex.Matches(text, field + @"\s*""((?:[^""\\]|\\.)*)""");
        if (matches.Count == 0) return "";
        try { return new JavaScriptSerializer().Deserialize<string>("\"" + matches[last ? matches.Count - 1 : 0].Groups[1].Value + "\"") ?? ""; } catch { return ""; }
    }
    public static string Tidy(string title) {
        title = Regex.Replace(Regex.Replace(title ?? "", @"<[^>]{1,40}>", " "), @"[\s\p{C}]+", " ").Trim();
        if (title.Length > 60) title = title.Substring(0, Char.IsHighSurrogate(title[58]) ? 58 : 59) + "…";
        return title;
    }
    // The name the tool itself shows for a conversation; without one, what the owner last asked.
    public static string ClaudeTitleOf(string path) {
        string tail = ReadPart(path, true, 256 * 1024);
        string title = JsonText(tail, @"""customTitle"":", true);
        if (title.Length == 0) title = JsonText(tail, @"""aiTitle"":", true);
        if (title.Length == 0) title = JsonText(tail, @"""lastPrompt"":", true);
        if (title.Length == 0) {
            string head = ReadPart(path, false, 768 * 1024);
            title = JsonText(head, @"""role"":""user"",""content"":", false);
            if (title.Length == 0) title = JsonText(head, @"""operation"":""enqueue"",[^\n]{0,200}?""content"":", false);
        }
        return Tidy(title);
    }
    string ClaudeTitle(FileInfo file) {
        TitleCache cached;
        if (titles.TryGetValue(file.FullName, out cached) && cached.Stamp == file.LastWriteTimeUtc.Ticks && cached.Length == file.Length) return cached.Title;
        string title;
        try { title = ClaudeTitleOf(file.FullName); } catch (IOException) { return cached != null ? cached.Title : ""; } catch (UnauthorizedAccessException) { return ""; }
        titles[file.FullName] = new TitleCache { Stamp = file.LastWriteTimeUtc.Ticks, Length = file.Length, Title = title };
        return title;
    }
    public static string ProjectFolder(string path) { return Regex.Replace(path, "[^a-zA-Z0-9]", "-"); }
    static bool SamePath(string a, string b) { return String.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
    static long Milliseconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    // Conversations of the allowed folders: what each tool saved, and which of them a program on this computer has open.
    public List<SessionInfo> Scan(Dictionary<string, string> dirs) {
        var found = new List<SessionInfo>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var open = new Dictionary<string, string>();
        var seen = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);   // folders that are not projects
        Action<string, string, DateTime, bool> note = delegate(string folder, string tool, DateTime at, bool live) {
            folder = (folder ?? "").TrimEnd('\\', '/');
            if (folder.Length < 3 || dirs.Values.Any(d => SamePath(d, folder)) || folder.IndexOf(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) >= 0) return;
            Dictionary<string, object> item;
            if (!seen.TryGetValue(folder, out item)) seen[folder] = item = new Dictionary<string, object> { { "path", folder }, { "name", Tidy(Path.GetFileName(folder)) }, { "updated", 0L }, { "live", false }, { "tools", "" } };
            if (Milliseconds(at) > (long)item["updated"]) item["updated"] = Milliseconds(at);
            if (live) item["live"] = true;
            if (((string)item["tools"]).IndexOf(tool, StringComparison.Ordinal) < 0) item["tools"] = ((string)item["tools"] + " " + tool).Trim();
        };
        try {
            var parents = Parents();
            foreach (string file in Directory.GetFiles(Path.Combine(home, ".claude", "sessions"), "*.json")) {
                try {
                    var record = json.Deserialize<Dictionary<string, object>>(ReadPart(file, false, 64 * 1024));
                    int pid; string session = Get(record, "sessionId");
                    if (!Int32.TryParse(Get(record, "pid"), out pid) || !parents.ContainsKey(pid) || !Regex.IsMatch(session, Uuid)) continue;
                    using (Process process = Process.GetProcessById(pid)) if (process.ProcessName.IndexOf("node", StringComparison.OrdinalIgnoreCase) < 0 && process.ProcessName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    LiveTerminal owner = null; int at = pid;
                    for (int depth = 0; depth < 12 && owner == null; depth++) {
                        owner = terminals.Values.FirstOrDefault(t => !t.Pty.Closed && t.Pty.Id == at);
                        int parent; if (!parents.TryGetValue(at, out parent) || parent == at || parent == 0) break; at = parent;
                    }
                    if (owner != null) { owner.Session = session; owner.Status = Get(record, "status"); }
                    else { open[session] = Get(record, "status"); note(Get(record, "cwd"), "claude", File.GetLastWriteTimeUtc(file), true); }
                } catch { }
            }
        } catch { }
        foreach (var dir in dirs) {
            try {
                var folder = new DirectoryInfo(Path.Combine(home, ".claude", "projects", ProjectFolder(dir.Value)));
                if (!folder.Exists) continue;
                foreach (FileInfo file in folder.GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(15)) {
                    string id = Path.GetFileNameWithoutExtension(file.Name), title;
                    if (!Regex.IsMatch(id, Uuid) || (title = ClaudeTitle(file)).Length == 0) continue;
                    string status; bool live = open.TryGetValue(id, out status);
                    found.Add(new SessionInfo { Id = id, Tool = "claude", Dir = dir.Key, Title = title, Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live, Host = live ? "cli" : "", CanTakeover = live, OwnershipKnown = true, Status = live ? status : "" });
                }
            } catch { }
        }
        try {
            // Other folders Claude Code has worked in: its folder names lose characters, the real path is inside a conversation.
            foreach (var folder in new DirectoryInfo(Path.Combine(home, ".claude", "projects")).GetDirectories()
                    .Select(d => new { d.Name, Newest = d.GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() })
                    .Where(d => d.Newest != null).OrderByDescending(d => d.Newest.LastWriteTimeUtc).Take(25)) {
                string path;
                if (!claudeFolders.TryGetValue(folder.Name, out path)) {
                    try { path = JsonText(ReadPart(folder.Newest.FullName, false, 1024 * 1024), @"""cwd"":", false); } catch (IOException) { continue; }
                    if (path.Length > 0) claudeFolders[folder.Name] = path;
                }
                if (path.Length > 0 && ProjectFolder(path) == folder.Name) note(path, "claude", folder.Newest.LastWriteTimeUtc, false);
            }
        } catch { }
        try {
            var index = new FileInfo(Path.Combine(CodexSessions.Home, "session_index.jsonl"));
            if (index.Exists && index.LastWriteTimeUtc.Ticks != codexNamesStamp) {
                var names = new Dictionary<string, string>();
                foreach (string line in ReadPart(index.FullName, true, 2 * 1024 * 1024).Split('\n')) {
                    try { var row = json.Deserialize<Dictionary<string, object>>(line); if (Get(row, "id").Length > 0) names[Get(row, "id")] = Get(row, "thread_name"); } catch { }
                }
                codexNames = names; codexNamesStamp = index.LastWriteTimeUtc.Ticks;
            }
            var root = new DirectoryInfo(Path.Combine(CodexSessions.Home, "sessions"));
            var counts = new Dictionary<string, int>();
            codexSessions.Refresh();
            var ownerParents = Parents();
            if (root.Exists) foreach (FileInfo file in root.GetFiles("rollout-*.jsonl", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(150)) {
                string name = Path.GetFileNameWithoutExtension(file.Name), id = name.Length > 36 ? name.Substring(name.Length - 36) : "", folder;
                if (!Regex.IsMatch(id, Uuid)) continue;
                if (!codexFolders.TryGetValue(file.FullName, out folder)) { try { folder = JsonText(ReadPart(file.FullName, false, 16 * 1024), @"""cwd"":", false); } catch (IOException) { continue; } codexFolders[file.FullName] = folder; }
                string dir = dirs.Where(d => SamePath(d.Value, folder)).Select(d => d.Key).FirstOrDefault();
                var owner = codexSessions.Get(id);
                bool live = owner.Live;
                bool phone = owner.Pid > 0 && CodexSessions.Descends(owner.Pid, terminals.Values.Select(t => t.Pty.Id), ownerParents);
                if (dir == null) { note(folder, "codex", file.LastWriteTimeUtc, live); continue; }
                int count; counts.TryGetValue(dir, out count);
                if (count >= 15) continue;
                counts[dir] = count + 1;
                string title; codexNames.TryGetValue(id, out title); title = Tidy(title);
                found.Add(new SessionInfo { Id = id, Tool = "codex", Dir = dir, Title = title.Length > 0 ? title : "Codex 对话 " + file.CreationTime.ToString("MM-dd HH:mm"), Updated = Milliseconds(file.LastWriteTimeUtc), Created = file.CreationTimeUtc, Live = live, Host = live ? owner.Host : "", OwnershipKnown = owner.Known, CanTakeover = live && owner.CanTakeover && !phone, TakeoverReason = owner.Reason });
            }
        } catch { }
        // Associate a phone terminal only with the verified writer process in its own process tree.
        var codexParents = Parents();
        foreach (LiveTerminal t in terminals.Values.Where(t => t.Tool == "codex" && t.Session.Length == 0 && !t.Pty.Closed)) {
            var mine = found.Where(s => s.Tool == "codex" && s.Dir == t.Dir && !terminals.Values.Any(o => o.Session == s.Id)
                && codexSessions.Get(s.Id).Pid > 0 && CodexSessions.Descends(codexSessions.Get(s.Id).Pid, new[] { t.Pty.Id }, codexParents)).ToArray();
            if (mine.Length == 1) t.Session = mine[0].Id;
        }
        sessions = found.OrderByDescending(s => s.Updated).Take(200).ToList();
        candidates = seen.Values.Where(c => { try { return Directory.Exists((string)c["path"]); } catch { return false; } })
            .OrderByDescending(c => (bool)c["live"]).ThenByDescending(c => (long)c["updated"]).Take(12).ToList();
        if (claudeFolders.Count > 500) claudeFolders.Clear();
        if (titles.Count > 600) titles.Clear();
        if (codexFolders.Count > 3000) codexFolders.Clear();
        return sessions;
    }
    // Many small reads become few numbered pieces, so a busy screen is not held back by the per-request piece limit.
    public static void Seal(LiveTerminal t) {
        while (t.Pending.Length > 0) {
            int n = Math.Min(12000, t.Pending.Length);
            if (n < t.Pending.Length && Char.IsHighSurrogate(t.Pending[n - 1])) n--;
            t.Output.Add(new Dictionary<string, object> { { "terminal", t.Id }, { "seq", ++t.Seq }, { "data", t.Pending.ToString(0, n) } });
            t.Pending.Remove(0, n);
        }
    }
    static string Get(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
    static IEnumerable<object> List(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : new object[0]; }
    // Settings and state live in %LOCALAPPDATA%\RemoteCli; REMOTECLI_DATA points a second copy (or a test) elsewhere.
    public static string DefaultData { get { string wanted = Environment.GetEnvironmentVariable("REMOTECLI_DATA"); return !String.IsNullOrWhiteSpace(wanted) ? wanted : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCli"); } }
    // config.json: Server (address of the relay), RemoteEnabled, RemoteMaxMode (full, edit or read) and RemoteDirs ("name=folder").
    Dictionary<string, object> Preferences() {
        var prefs = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dataDir, "config.json"), Encoding.UTF8));
        Origin = Get(prefs, "Server").TrimEnd('/');
        return prefs;
    }
    static string FindTool(string name) {
        if (name == "shell") return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") }))
            foreach (string ext in new[] { ".exe", ".cmd" }) { try { string path = Path.Combine(folder.Trim(), name + ext); if (File.Exists(path)) return path; } catch { } }
        return null;
    }
    static Dictionary<string, string> Dirs(Dictionary<string, object> prefs) {
        var dirs = new Dictionary<string, string>();
        foreach (object raw in List(prefs, "RemoteDirs")) { string line = Convert.ToString(raw); int at = line.IndexOf('='); if (at <= 0) continue; string path = line.Substring(at + 1).Trim(), name = line.Substring(0, at).Trim(); if (Directory.Exists(path)) dirs[name] = Path.GetFullPath(path); }
        return dirs;
    }
    public List<Dictionary<string, object>> Candidates { get { return candidates; } }
    string ProjectsFile { get { return Path.Combine(dataDir, "terminal-projects.json"); } }
    // Folders added from the phone, beside the ones set in the desktop widget's settings.
    List<KeyValuePair<string, string>> OwnProjects() {
        var own = new List<KeyValuePair<string, string>>();
        try {
            if (File.Exists(ProjectsFile)) foreach (var item in json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(ProjectsFile, Encoding.UTF8)))
                if (Get(item, "name").Length > 0 && Get(item, "path").Length > 0 && !own.Any(p => p.Key == Get(item, "name"))) own.Add(new KeyValuePair<string, string>(Get(item, "name"), Get(item, "path")));
        } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        return own;
    }
    void SaveProjects(List<KeyValuePair<string, string>> own) {
        string temporary = ProjectsFile + ".tmp";
        File.WriteAllText(temporary, json.Serialize(own.Select(p => new Dictionary<string, object> { { "name", p.Key }, { "path", p.Value } }).ToArray()), new UTF8Encoding(false));
        if (File.Exists(ProjectsFile)) File.Replace(temporary, ProjectsFile, null); else File.Move(temporary, ProjectsFile);
    }
    public Dictionary<string, string> AllDirs(Dictionary<string, object> prefs) {
        var dirs = Dirs(prefs);
        foreach (var p in OwnProjects()) { try { if (!dirs.ContainsKey(p.Key) && Directory.Exists(p.Value)) dirs[p.Key] = p.Value; } catch { } }
        return dirs;
    }
    public static string NormalFolder(string path) {
        path = (path ?? "").Trim();
        if (path.Length < 3 || path.Length > 240 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(new[] { '*', '?' }) >= 0 || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]"))
            throw new ArgumentException("请填写电脑上的完整文件夹路径，例如 E:\\项目\\组会");
        path = Path.GetFullPath(path).TrimEnd('\\');
        return path.Length == 2 ? path + "\\" : path;
    }
    public void ChangeProjects(Dictionary<string, object> op, Dictionary<string, object> prefs, string action) {
        var settings = Dirs(prefs); var own = OwnProjects();
        string name = Tidy(Get(op, "name"));
        if (action == "project_add") {
            string path = NormalFolder(Get(op, "path"));
            if (settings.Values.Concat(own.Select(p => p.Value)).Any(p => SamePath(p, path))) throw new ArgumentException("这个文件夹已经在项目里");
            if (own.Count >= 30) throw new InvalidOperationException("项目已达 30 个，请先移除不用的");
            if (!Directory.Exists(path)) {
                if (Get(op, "create") != "True") throw new ArgumentException("电脑上没有这个文件夹");
                string parent = Path.GetDirectoryName(path);
                if (String.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new ArgumentException("上一级文件夹不存在，不能新建");
                Directory.CreateDirectory(path);
            }
            if (name.Length == 0) name = Tidy(Path.GetFileName(path));
            if (name.Length == 0) name = path;
            if (name.Length > 40) name = name.Substring(0, 40);
            string wanted = name;
            for (int n = 2; settings.ContainsKey(name) || own.Any(p => p.Key == name); n++) name = wanted + " " + n;
            own.Add(new KeyValuePair<string, string>(name, path));
        } else {
            int at = own.FindIndex(p => p.Key == name);
            if (at < 0) throw new ArgumentException(settings.ContainsKey(name) ? "这个项目写在电脑的配置文件里，请在电脑上修改" : "没有这个项目");
            if (action == "project_remove") {
                if (terminals.Values.Any(t => t.Dir == name && !t.Pty.Closed)) throw new InvalidOperationException("这个项目还有终端在运行，请先结束");
                own.RemoveAt(at);
            } else {
                string to = Tidy(Get(op, "to"));
                if (to.Length == 0 || to.Length > 40) throw new ArgumentException("名称需为 1 至 40 字");
                if (to != name && (settings.ContainsKey(to) || own.Any(p => p.Key == to))) throw new ArgumentException("已有同名项目");
                own[at] = new KeyValuePair<string, string>(to, own[at].Value);
                foreach (var t in terminals.Values.Where(t => t.Dir == name)) t.Dir = to;
            }
        }
        SaveProjects(own);
        nextScan = DateTime.MinValue;
    }
    // Ends the Claude Code program that has this conversation open in a window on this computer, so that only the
    // phone's terminal writes to it. Programs started by a phone terminal are never touched.
    void CloseOnComputer(string session) {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parents = Parents();
        foreach (string file in Directory.GetFiles(Path.Combine(home, ".claude", "sessions"), "*.json")) {
            int pid;
            try {
                var record = json.Deserialize<Dictionary<string, object>>(ReadPart(file, false, 64 * 1024));
                if (Get(record, "sessionId") != session || !Int32.TryParse(Get(record, "pid"), out pid) || !parents.ContainsKey(pid)) continue;
            } catch (IOException) { continue; } catch (ArgumentException) { continue; } catch (InvalidOperationException) { continue; }
            int at = pid; bool ours = false;
            for (int depth = 0; depth < 12 && !ours; depth++) {
                ours = at == Process.GetCurrentProcess().Id || terminals.Values.Any(t => t.Pty.Id == at);
                int parent; if (!parents.TryGetValue(at, out parent) || parent == at || parent == 0) break; at = parent;
            }
            if (ours) continue;
            Process process;
            try { process = Process.GetProcessById(pid); } catch (ArgumentException) { continue; }
            using (process) {
                if (process.ProcessName.IndexOf("node", StringComparison.OrdinalIgnoreCase) < 0 && process.ProcessName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0) continue;
                using (Process kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit(8000);
                if (!process.WaitForExit(5000)) throw new InvalidOperationException("电脑上的窗口没有关掉，已取消接手");
            }
            Thread.Sleep(300);   // the conversation file is released before it is opened again
        }
    }
    public static string Command(string tool, string launcher, string session, bool history, string mode, bool fork = false) {
        if (tool != "claude" && tool != "codex" && tool != "shell") throw new ArgumentException("工具无效");
        if (launcher == null || launcher.IndexOfAny(new[] { '"', '%', '\r', '\n' }) >= 0) throw new ArgumentException("工具路径无效");
        if (!String.IsNullOrEmpty(session) && !Regex.IsMatch(session, @"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")) throw new ArgumentException("历史会话编号无效");
        string args = "";
        if (tool == "claude") args = (session.Length > 0 ? " --resume " + session : history ? " --resume" : "") + (mode == "read" ? " --permission-mode plan" : mode == "edit" ? " --permission-mode acceptEdits" : "");
        if (tool == "codex") args = (session.Length > 0 ? (fork ? " fork " : " resume ") + session : history ? " resume --include-non-interactive" : "") + (mode == "read" ? " -s read-only" : mode == "edit" ? " -s workspace-write" : "");
        if (tool == "shell") args = " -NoLogo -NoExit";
        if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") + "\" /d /s /c \"\"" + launcher + "\"" + args + "\"";
        return "\"" + launcher + "\"" + args;
    }
    public static List<Dictionary<string, object>> OutputBatch(IEnumerable<Dictionary<string, object>> chunks) {
        // ANSI escapes expand in JSON. Bound encoded bytes, not the number of characters.
        var serializer = new JavaScriptSerializer();
        var batch = new List<Dictionary<string, object>>();
        int bytes = 0;
        foreach (var chunk in chunks) {
            int size = Encoding.UTF8.GetByteCount(serializer.Serialize(chunk)) + 1;
            if (bytes + size > 512 * 1024) break;
            batch.Add(chunk); bytes += size;
            if (batch.Count >= 150) break;
        }
        return batch;
    }
    // The address or the password changed: sign in again at once instead of after the usual pause.
    public void Reset() { nextLogin = DateTime.MinValue; client = null; }
    /// The terminals that are running now, for the window on the computer: tool, project, status ("busy", "idle" or ""), start time.
    public List<string[]> Running() {
        try {
            lock (gate) return terminals.Values.Where(t => t.Pty != null && !t.Pty.Closed)
                .Select(t => new[] { t.Tool, t.Dir, t.Status ?? "", t.Started.ToString("o") }).ToList();
        } catch (InvalidOperationException) { return null; }      // the list changed while it was read: the caller keeps what it showed
    }
    async Task<bool> Login(Dictionary<string, object> prefs) {
        if (DateTime.UtcNow < nextLogin) return false;
        nextLogin = DateTime.UtcNow.AddSeconds(60);
        if (client != null) client.Dispose();
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(20) };
        if (!Regex.IsMatch(Origin, @"\Ahttps?://[^/\s]+\z")) return false;
        var endpoint = ServicePointManager.FindServicePoint(new Uri(Origin));
        endpoint.UseNagleAlgorithm = false;
        endpoint.Expect100Continue = false;  // small JSON reports can send their body with the headers
        // The password is kept encrypted for this Windows account (see --set-password).
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(dataDir, "password.dpapi")), null, DataProtectionScope.CurrentUser);
        try {
            var content = new StringContent(json.Serialize(new Dictionary<string, object> { { "password", Encoding.UTF8.GetString(bytes) } }), Encoding.UTF8, "application/json");
            using (var r = await client.PostAsync(Origin + "/api/login", content))
                return r.IsSuccessStatusCode;
        } finally { Array.Clear(bytes, 0, bytes.Length); }
    }
    void Execute(Dictionary<string, object> op, Dictionary<string, object> prefs) {
        string id = Get(op, "id"), terminal = Get(op, "terminal"), action = Get(op, "action");
        if (completed.ContainsKey(id)) { reported.Remove(id); return; }
        string error = "";
        try {
            bool project = action == "project_add" || action == "project_remove" || action == "project_rename";
            if (!Regex.IsMatch(id, @"\A[a-f0-9]{16,32}\z") || (!project && !Regex.IsMatch(terminal, @"\A[a-f0-9]{32}\z"))) throw new ArgumentException("操作编号无效");
            double at; if (!Double.TryParse(Get(op, "at"), out at) || Math.Abs((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds - at) > 150) throw new ArgumentException("操作已过期");
            if (Get(prefs, "RemoteEnabled") != "True") throw new InvalidOperationException("电脑远控已关闭");
            if (project) ChangeProjects(op, prefs, action);
            else if (action == "start") {
                if (terminals.ContainsKey(terminal)) return;
                if (terminals.Values.Count(t => !t.Pty.Closed) >= 8) throw new InvalidOperationException("最多同时运行 8 个终端");
                string tool = Get(op, "tool"), dir = Get(op, "dir"), previous = Get(op, "previous"), session = Get(op, "session");
                bool fork = Get(op, "fork") == "True";
                if (fork && (tool != "codex" || session.Length == 0 || Get(op, "takeover") == "True")) throw new ArgumentException("副本选项无效");
                var dirs = AllDirs(prefs);
                if (!dirs.ContainsKey(dir)) throw new ArgumentException("目录未获允许");
                string launcher = FindTool(tool);
                if (launcher == null) throw new ArgumentException("电脑未安装这个工具");
                if (session.Length > 0) {
                    if (!Scan(dirs).Any(s => s.Id == session && s.Tool == tool && s.Dir == dir)) throw new ArgumentException("电脑上没有找到这个对话");
                    if (Get(op, "takeover") == "True" && tool == "claude") CloseOnComputer(session);
                    if (tool == "codex" && !fork) {
                        var roots = terminals.Values.Select(t => t.Pty.Id).Concat(new[] { Process.GetCurrentProcess().Id }).ToArray();
                        if (Get(op, "takeover") == "True") CodexSessions.Takeover(session, roots, Parents());
                        var owner = CodexSessions.Inspect(session);
                        if (!owner.Known || owner.Live) throw new InvalidOperationException("这个 Codex 对话仍在电脑上使用，请先结束电脑会话，或在手机新开副本");
                    }
                } else if (previous.Length > 0) {
                    if (!Regex.IsMatch(previous, @"\A[a-f0-9]{16,32}\z")) throw new ArgumentException("历史编号无效");
                    session = List(prefs, "RemoteSessions").Select(Convert.ToString).Where(s => s.StartsWith(previous + "|", StringComparison.Ordinal)).Select(s => s.Substring(previous.Length + 1)).FirstOrDefault() ?? "";
                    if (session.Length == 0) throw new ArgumentException("旧对话没有会话编号，请使用历史对话入口");
                }
                var live = new LiveTerminal { Id = terminal, Tool = tool, Dir = dir, Session = fork ? "" : session };
                live.Pty = new PseudoTerminal(Command(tool, launcher, session, Get(op, "history") == "True", Get(prefs, "RemoteMaxMode"), fork), dirs[dir], 80, 24, text => {
                    lock (gate) live.Pending.Append(text);
                    Wake();
                });
                terminals[terminal] = live;
            } else {
                LiveTerminal live;
                if (!terminals.TryGetValue(terminal, out live) || live.Pty.Closed) throw new ArgumentException("终端已结束");
                if (!AllDirs(prefs).ContainsKey(live.Dir)) throw new ArgumentException("目录不再获允许");
                if (action == "input") { lastInput = DateTime.UtcNow; string text = Get(op, "data"); if (text.Length == 0 || text.Length > 16000) throw new ArgumentException("输入无效"); live.Pty.Write(text); }
                else if (action == "resize") { int cols = Convert.ToInt32(op["cols"]), rows = Convert.ToInt32(op["rows"]); if (cols < 20 || cols > 240 || rows < 6 || rows > 100) throw new ArgumentException("尺寸无效"); live.Pty.Resize(cols, rows); }
                else if (action == "close") live.Pty.Dispose();
                else throw new ArgumentException("操作无效");
            }
        } catch (Exception ex) { error = ex is ArgumentException || ex is InvalidOperationException || ex is IOException ? ex.Message : "终端操作失败：" + ex.GetType().Name; }
        completed[id] = new Dictionary<string, object> { { "id", id }, { "error", error } };
        lastActivity = DateTime.UtcNow; soon = true; if (action == "start" || action == "close") nextScan = DateTime.UtcNow.AddSeconds(1.5);
        if (action.StartsWith("project_", StringComparison.Ordinal)) nextScan = DateTime.MinValue;
    }
    async Task Tick() {
        Dictionary<string, object> prefs = Preferences();
        if (client == null && !await Login(prefs)) return;
        string body, listedText; bool listing; Dictionary<string, object>[] acknowledgments;
        lock (work) {
            bool enabled = Get(prefs, "RemoteEnabled") == "True";
            if (!enabled) foreach (var t in terminals.Values.Where(t => !t.Pty.Closed)) t.Pty.Dispose();
            List<Dictionary<string, object>> output;
            var allowed = AllDirs(prefs);
            var settings = Dirs(prefs);
            // Reading the conversation lists takes a moment; it waits while someone is typing.
            if (DateTime.UtcNow >= nextScan && (DateTime.UtcNow - lastInput > TimeSpan.FromSeconds(2) || DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(20))) {
                nextScan = DateTime.UtcNow.AddSeconds(5); lastScan = DateTime.UtcNow;
                if (enabled) Scan(allowed); else { sessions.Clear(); candidates.Clear(); }
            }
            if (DateTime.UtcNow - toolsAt > TimeSpan.FromSeconds(30)) { tools = new[] { "claude", "codex", "shell" }.Where(t => FindTool(t) != null).ToArray(); toolsAt = DateTime.UtcNow; }
            lock (gate) { foreach (var t in terminals.Values) Seal(t); output = OutputBatch(terminals.Values.SelectMany(t => t.Output.Take(30))); }
            if (output.Count > 0) lastActivity = DateTime.UtcNow;
            acknowledgments = completed.Where(p => !reported.Contains(p.Key)).Take(200).Select(p => p.Value).ToArray();
            var payload = new Dictionary<string, object> {
                { "info", new { instance = instance, enabled = enabled, workspaces = allowed.Keys.ToArray(), tools = tools, features = new[] { "codex-fork", "codex-takeover", "terminal-exit" },
                    projects = settings.Select(d => new { name = d.Key, path = d.Value, @fixed = true, exists = true })
                        .Concat(OwnProjects().Where(p => !settings.ContainsKey(p.Key)).Select(p => new { name = p.Key, path = p.Value, @fixed = false, exists = allowed.ContainsKey(p.Key) })).ToArray(),
                    candidates = candidates.ToArray() } },
                { "terminals", terminals.Values.Select(t => new { id = t.Id, state = t.Pty.Closed ? "closed" : "running", cols = t.Pty.Cols, rows = t.Pty.Rows, session = t.Session, status = t.Status, exit_code = t.Pty.ExitCode, error = t.Pty.ExitCode.HasValue && t.Pty.ExitCode.Value != 0 ? "程序退出（代码 " + t.Pty.ExitCode.Value + "），请检查终端画面中的原因" : "" }).ToArray() },
                { "output", output }, { "acks", acknowledgments }
            };
            // The list of conversations is long: it goes out when it changed, and now and then in case the relay restarted.
            var listed = sessions.Select(s => new { id = s.Id, tool = s.Tool, dir = s.Dir, title = s.Title, updated = s.Updated, live = s.Live, host = s.Host, status = s.Status, can_takeover = s.CanTakeover, ownership_known = s.OwnershipKnown, takeover_reason = s.TakeoverReason }).ToArray();
            listedText = json.Serialize(listed);
            listing = listedText != sentSessions || DateTime.UtcNow - sessionsSent > TimeSpan.FromSeconds(10);
            if (listing) payload["sessions"] = listed;
            body = json.Serialize(payload);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/api/terminal/agent");
        request.Headers.TryAddWithoutValidation("Origin", Origin); request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using (request) using (var response = await client.SendAsync(request)) {
            if ((int)response.StatusCode == 401) { await Login(prefs); return; }
            if (!response.IsSuccessStatusCode) return;
            string answer = await response.Content.ReadAsStringAsync();
            lock (work) {
                var result = json.Deserialize<Dictionary<string, object>>(answer);
                foreach (var a in acknowledgments) reported.Add(Convert.ToString(a["id"]));
                if (listing) { sentSessions = listedText; sessionsSent = DateTime.UtcNow; }
                var ack = result.ContainsKey("output_ack") ? result["output_ack"] as Dictionary<string, object> : null;
                if (ack != null) lock (gate) foreach (var t in terminals.Values) { object seq; if (ack.TryGetValue(t.Id, out seq)) t.Output.RemoveAll(c => Convert.ToInt64(c["seq"]) <= Convert.ToInt64(seq)); }
                foreach (object item in List(result, "operations")) { var op = item as Dictionary<string, object>; if (op != null) Execute(op, prefs); }
                if (completed.Count > 4000) foreach (string key in completed.Keys.Take(1000).ToArray()) { completed.Remove(key); reported.Remove(key); }
                // A terminal is forgotten only after its last output has been read from the program and stored by the relay.
                foreach (var t in terminals.Values.Where(t => t.Pty.Closed && t.ClosedSeen == DateTime.MinValue)) t.ClosedSeen = DateTime.UtcNow;
                string[] finished; lock (gate) finished = terminals.Where(t => t.Value.Pty.Closed && (t.Value.Pty.Drained || DateTime.UtcNow - t.Value.ClosedSeen > TimeSpan.FromSeconds(15)) && t.Value.Output.Count == 0 && t.Value.Pending.Length == 0).Select(t => t.Key).ToArray();
                foreach (string key in finished) { terminals[key].Pty.Dispose(); terminals.Remove(key); }
            }
        }
    }
    // A request the relay holds open until the phone asks for something. What it brings is carried out at once, so
    // a key press does not wait for the next report; the report then only takes the result back.
    async Task Listen() {
        var reader = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        for (;;) {
            var started = DateTime.UtcNow; bool waiting = false;
            try {
                HttpClient current = client;
                if (current != null) {
                    var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/api/terminal/agent/pull");
                    request.Headers.TryAddWithoutValidation("Origin", Origin);
                    request.Content = new StringContent(reader.Serialize(new Dictionary<string, object> { { "instance", instance }, { "wait", 12 } }), Encoding.UTF8, "application/json");
                    using (request) using (var response = await current.SendAsync(request)) {
                        if (response.IsSuccessStatusCode) {
                            var result = reader.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                            waiting = true;
                            var operations = List(result, "operations").OfType<Dictionary<string, object>>().ToArray();
                            if (operations.Length > 0) {
                                Dictionary<string, object> prefs = Preferences();
                                lock (work) foreach (var op in operations) Execute(op, prefs);
                            }
                            if (operations.Length > 0 || Get(result, "operations") == "True") Wake();
                        }
                    }
                }
            } catch { }
            // An answer that came back at once without being a held request (not signed in yet, an older relay) must not become a busy loop.
            if (!waiting || DateTime.UtcNow - started < TimeSpan.FromMilliseconds(40)) await Task.Delay(waiting ? 40 : 2000);
        }
    }
    public async Task Run() {
        Task listening = Listen();
        for (;;) {
            try { await Tick(); } catch { /* Retry network and login failures without logging credentials or terminal input. */ }
            int pause = soon ? 20 : DateTime.UtcNow - lastActivity < TimeSpan.FromSeconds(20) ? 250 : 700;
            soon = false;
            bool woken = false, failed = false;
            try { woken = await wake.WaitAsync(pause); } catch { failed = true; }
            if (failed) await Task.Delay(pause);
            if (woken && DateTime.UtcNow - lastInput > TimeSpan.FromMilliseconds(150)) await Task.Delay(12);
        }
    }
    [STAThread] public static int Main(string[] args) {
        // --set-password reads the password from standard input and stores it encrypted for this Windows account.
        if (args.Length > 0 && args[0] == "--set-password") {
            string folder = args.Length > 1 ? args[1] : DefaultData;
            string secret = (Console.In.ReadLine() ?? "").Trim();
            if (secret.Length == 0) return 2;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "password.dpapi"), ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
            return 0;
        }
        string data = args.Length > 1 && args[0] == "--data" ? args[1] : null;
        bool created;
        using (var singleton = new Mutex(true, "Local\\RemoteCliAgent" + (data == null ? "" : "-" + Math.Abs(data.ToLowerInvariant().GetHashCode())), out created)) {
            if (!created) return 0;
            ServicePointManager.DefaultConnectionLimit = 8;   // the held request and the reports run side by side
            try { new TerminalAgent(data).Run().GetAwaiter().GetResult(); } finally { singleton.ReleaseMutex(); }
        }
        return 0;
    }
}
}
