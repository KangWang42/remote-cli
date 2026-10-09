using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RemoteCli;

public static class CodexOwnerCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Process Fixture(string mode, string id) {
        var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
            mode + " " + id) { UseShellExecute = false, CreateNoWindow = true });
        for (int n = 0; n < 100; n++) {
            if (File.Exists(Path.Combine(CodexSessions.Home, id + ".ready"))) return process;
            if (process.HasExited) throw new Exception("Fixture exited");
            Thread.Sleep(50);
        }
        process.Kill(); throw new Exception("Fixture not ready");
    }
    public static int Main(string[] args) {
        if (args.Length == 2) {
            if (args[0] == "remote-host") {
                using (var writer = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "codex.exe"), "hold " + args[1]) { UseShellExecute = false, CreateNoWindow = true })) writer.WaitForExit();
                return 0;
            }
            string path = CodexSessions.LockPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                stream.Lock(0, Int64.MaxValue);
                File.WriteAllText(Path.Combine(CodexSessions.Home, args[1] + ".ready"), "ready");
                for (;;) Thread.Sleep(500);
            }
        }
        var children = new List<Process>();
        string root = Path.Combine(Path.GetTempPath(), "remote-cli-owner-" + Guid.NewGuid().ToString("N"));
        string previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", root);
        try {
            Directory.CreateDirectory(Path.Combine(root, "thread-writer-locks"));
            string stale = Guid.NewGuid().ToString(); File.WriteAllText(CodexSessions.LockPath(stale), "");
            Require(TerminalAgent.Command("codex", @"C:\tools\codex.exe", stale, false, "read", true).Contains(" fork " + stale + " -s read-only"), "Fork command lost source or permission mode");
            Require(TerminalAgent.Command("codex", @"C:\tools\codex.cmd", stale, false, "edit").Contains(" resume " + stale + " -s workspace-write"), "Resume wrapper lost permission mode");
            var idle = CodexSessions.Inspect(stale);
            Require(idle.Known && !idle.Live && !idle.CanTakeover, "Stale lock was considered live");
            string id = Guid.NewGuid().ToString(); var cli = Fixture("hold", id); children.Add(cli);
            var live = CodexSessions.Inspect(id);
            Require(live.Known && live.Live && live.Pid == cli.Id && live.CanTakeover && live.Host == "cli", "Independent writer ownership failed: pid=" + live.Pid + " host=" + live.Host + " reason=" + live.Reason);
            var parents = new Dictionary<int, int> { { cli.Id, Process.GetCurrentProcess().Id } };
            bool refused = false;
            try { CodexSessions.Takeover(id, new[] { Process.GetCurrentProcess().Id }, parents); } catch (InvalidOperationException) { refused = true; }
            Require(refused && !cli.HasExited, "Protected phone descendant was terminated");
            CodexSessions.Takeover(id, new int[0], parents);
            Require(cli.WaitForExit(3000) && !CodexSessions.Inspect(id).Live, "Takeover did not release the exact writer");
            string sharedId = Guid.NewGuid().ToString(); var shared = Fixture("app-server", sharedId); children.Add(shared);
            var backend = CodexSessions.Inspect(sharedId);
            Require(backend.Live && backend.Pid == shared.Id && !backend.CanTakeover && backend.Host == "shared", "Shared backend was offered for termination or classified as a window");
            refused = false;
            try { CodexSessions.Takeover(sharedId, new int[0], new Dictionary<int, int>()); } catch (InvalidOperationException) { refused = true; }
            Require(refused && !shared.HasExited, "Shared backend was terminated");
            string remoteId = Guid.NewGuid().ToString();
            var host = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "RemoteCliAgent.exe"), "remote-host " + remoteId) { UseShellExecute = false, CreateNoWindow = true }); children.Add(host);
            for (int n = 0; n < 100 && !File.Exists(Path.Combine(CodexSessions.Home, remoteId + ".ready")); n++) Thread.Sleep(50);
            var remote = CodexSessions.Inspect(remoteId); children.Add(Process.GetProcessById(remote.Pid));
            Require(remote.Live && remote.Host == "remote" && !remote.CanTakeover, "Other remote terminal was classified as a computer CLI");
            refused = false;
            try { CodexSessions.Takeover(remoteId, new int[0], new Dictionary<int, int>()); } catch (InvalidOperationException) { refused = true; }
            Require(refused && !host.HasExited, "Other remote terminal was terminated");
            Console.WriteLine("Native checks passed: stale lock, standalone CLI, protected descendant, exact takeover, shared backend, other remote host.");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        } finally {
            foreach (var child in children) { try { if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); } } finally { child.Dispose(); } }
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
