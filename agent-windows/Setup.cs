using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteCli {
/// The installer: unpacks the program into the current user's folder, adds shortcuts and an entry under
/// "Apps" for removing it. No administrator rights are needed; `--uninstall` undoes all of it.
public static class Setup {
    const string Version = "0.1.0", Name = "Remote CLI";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteCli";
    static string Target { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RemoteCli"); } }
    static string Data { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCli"); } }
    static string[] Shortcuts { get { return new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk") }; } }

    static bool Running() {
        bool created;
        using (var probe = new Mutex(false, "Local\\RemoteCliApp", out created)) return !created;
    }
    static void Shortcut(string file, string target) {
        Type type = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(type);
        object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { file });
        Type kind = link.GetType();
        kind.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
        kind.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
        kind.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "在手机上使用这台电脑的终端" });
        kind.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
    }
    static int Install(bool quiet) {
        if (!quiet && MessageBox.Show(Name + " " + Version + " 将安装到\n" + Target + "\n\n不需要管理员权限。继续吗？", "安装 " + Name, MessageBoxButtons.OKCancel) != DialogResult.OK) return 1;
        if (Running()) { MessageBox.Show("Remote CLI 正在运行。请先在托盘图标上点右键选“退出”，再重新运行安装程序。", "安装 " + Name); return 1; }
        if (Directory.Exists(Target)) Directory.Delete(Target, true);       // settings live in another folder and are kept
        Directory.CreateDirectory(Target);
        using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Read)) {
            string root = Path.GetFullPath(Target) + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in archive.Entries) {
                string path = Path.GetFullPath(Path.Combine(Target, entry.FullName));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包内容无效");
                if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                entry.ExtractToFile(path, true);
            }
        }
        string program = Path.Combine(Target, "RemoteCli.exe"), remover = Path.Combine(Target, "Uninstall.exe");
        File.Copy(Application.ExecutablePath, remover, true);
        foreach (string file in Shortcuts) Shortcut(file, program);
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
            key.SetValue("DisplayName", Name); key.SetValue("DisplayVersion", Version); key.SetValue("Publisher", "remote-cli");
            key.SetValue("InstallLocation", Target); key.SetValue("UninstallString", "\"" + remover + "\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (!quiet) Process.Start(new ProcessStartInfo(program) { WorkingDirectory = Target, UseShellExecute = true });
        return 0;
    }
    static int Uninstall(bool quiet) {
        if (!quiet && MessageBox.Show("卸载 " + Name + "？", "卸载", MessageBoxButtons.OKCancel) != DialogResult.OK) return 1;
        if (Running()) { MessageBox.Show("Remote CLI 正在运行。请先在托盘图标上点右键选“退出”。", "卸载"); return 1; }
        foreach (string file in Shortcuts) try { File.Delete(file); } catch { }
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (run != null) run.DeleteValue("RemoteCli", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        bool all = !quiet && Directory.Exists(Data) && MessageBox.Show("同时删除设置、密码和下载的隧道程序吗？\n" + Data, "卸载", MessageBoxButtons.YesNo) == DialogResult.Yes;
        if (all) try { Directory.Delete(Data, true); } catch { }
        // This program runs from the folder it removes: a command window finishes the job after it has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + Target + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        if (!quiet) MessageBox.Show(Name + " 已卸载。", "卸载");
        return 0;
    }
    [STAThread] public static int Main(string[] args) {
        Application.EnableVisualStyles();
        bool quiet = Array.IndexOf(args, "--quiet") >= 0;
        try { return Array.IndexOf(args, "--uninstall") >= 0 ? Uninstall(quiet) : Install(quiet); }
        catch (Exception error) { if (!quiet) MessageBox.Show("没有完成：" + error.Message, Name); return 2; }
    }
}
}
