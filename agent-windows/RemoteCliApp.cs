using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteCli {
/// The program on the computer: a small window and a tray icon around the relay, the optional tunnel and the
/// terminal agent. Everything it starts ends when it exits.
public sealed class App : Form {
    const string Version = "0.1.1";
    const string TunnelDownload = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    readonly string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    readonly string dataDir = TerminalAgent.DefaultData;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    Dictionary<string, object> config;
    string password = "", address = "";
    Process relay, tunnel;
    TerminalAgent agent;
    int generation;

    readonly Label status = new Label();
    readonly Option lan = new Option(), cloud = new Option(), own = new Option();
    readonly Field ownField = new Field(), addressField = new Field(), passwordField = new Field();
    TextBox ownUrl { get { return ownField.Box; } }
    TextBox addressBox { get { return addressField.Box; } }
    TextBox passwordBox { get { return passwordField.Box; } }
    readonly PictureBox qr = new PictureBox();
    readonly ListBox folders = new ListBox();
    readonly CheckBox enabled = new CheckBox(), autostart = new CheckBox();
    readonly NotifyIcon tray = new NotifyIcon();
    bool quitting, hinted;

    string ConfigFile { get { return Path.Combine(dataDir, "config.json"); } }
    string PasswordFile { get { return Path.Combine(dataDir, "password.dpapi"); } }
    string TunnelFile { get { return Path.Combine(dataDir, "cloudflared.exe"); } }
    string ChildrenFile { get { return Path.Combine(dataDir, "children.json"); } }
    string Mode { get { return Convert.ToString(config["Mode"]); } }
    int Port { get { return Convert.ToInt32(config["Port"]); } }

    // ---------- settings
    void LoadSettings() {
        Directory.CreateDirectory(dataDir);
        try { config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile, Encoding.UTF8)); } catch { config = null; }
        if (config == null) config = new Dictionary<string, object>();
        foreach (var item in new Dictionary<string, object> { { "Mode", "lan" }, { "Port", 8722 }, { "Server", "" }, { "OwnServer", "" }, { "RemoteEnabled", true },
                 { "RemoteMaxMode", "full" }, { "RemoteDirs", new object[0] } })
            if (!config.ContainsKey(item.Key) || config[item.Key] == null) config[item.Key] = item.Value;
        if (Array.IndexOf(new[] { "lan", "cloud", "own" }, Mode) < 0) config["Mode"] = "lan";
        try { password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PasswordFile), null, DataProtectionScope.CurrentUser)); } catch { password = ""; }
        if (password.Length == 0) SetPassword(NewPassword());
    }
    void Save() {
        string temporary = ConfigFile + ".tmp";
        File.WriteAllText(temporary, json.Serialize(config), new UTF8Encoding(false));
        if (File.Exists(ConfigFile)) File.Replace(temporary, ConfigFile, null); else File.Move(temporary, ConfigFile);
    }
    static string NewPassword() {
        byte[] bytes = new byte[10];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        string hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        return String.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 5, 5)));
    }
    void SetPassword(string value) {
        password = value;
        File.WriteAllBytes(PasswordFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        // Phones that signed in with the old password must sign in again.
        try { File.Delete(Path.Combine(dataDir, "relay", "sessions.json")); } catch { }
    }
    List<string> Dirs() { return ((System.Collections.IEnumerable)config["RemoteDirs"]).Cast<object>().Select(Convert.ToString).ToList(); }

    // ---------- the programs this one starts
    void Remember() {
        var ids = new[] { relay, tunnel }.Where(p => p != null).Select(p => { try { return p.HasExited ? 0 : p.Id; } catch { return 0; } }).Where(id => id != 0).ToArray();
        try { File.WriteAllText(ChildrenFile, json.Serialize(ids)); } catch { }
    }
    static void Kill(Process process) { try { if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch { } }
    // A copy that crashed may have left its relay or tunnel running; they hold the port.
    void KillLeftovers() {
        try {
            foreach (object id in json.Deserialize<object[]>(File.ReadAllText(ChildrenFile)))
                try { var p = Process.GetProcessById(Convert.ToInt32(id)); if (p.ProcessName == "python" || p.ProcessName == "cloudflared") Kill(p); } catch { }
        } catch { }
    }
    void StopChildren() { Kill(tunnel); Kill(relay); tunnel = relay = null; Remember(); }
    static bool Listening(int port) {
        try { using (var client = new TcpClient()) { var attempt = client.BeginConnect("127.0.0.1", port, null, null); bool ok = attempt.AsyncWaitHandle.WaitOne(400) && client.Connected; return ok; } } catch { return false; }
    }
    static string LanAddress() {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                 .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))))
            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address) && !ip.Address.ToString().StartsWith("169.254.")) return ip.Address.ToString();
        return "";
    }

    void Say(string text, bool problem = false) {
        if (InvokeRequired) { BeginInvoke(new Action(() => Say(text, problem))); return; }
        status.Text = text; status.ForeColor = problem ? Theme.Bad : Theme.Good;
        tray.Text = ("Remote CLI：" + text).Length > 60 ? ("Remote CLI：" + text).Substring(0, 60) : "Remote CLI：" + text;
    }
    void ShowAddress(string where) {
        if (InvokeRequired) { BeginInvoke(new Action(() => ShowAddress(where))); return; }
        address = where;
        addressBox.Text = where; passwordBox.Text = password;
        Image old = qr.Image;
        qr.Image = where.Length > 0 ? Picture("remotecli://connect?u=" + Uri.EscapeDataString(where) + "&p=" + Uri.EscapeDataString(password), 5) : null;
        if (old != null) old.Dispose();
    }
    public static Bitmap Picture(string text, int scale) {
        bool[,] dark = QrCode.Encode(text);
        if (dark == null) return null;
        int size = dark.GetLength(0), quiet = 4;
        var picture = new Bitmap((size + quiet * 2) * scale, (size + quiet * 2) * scale);
        using (var g = Graphics.FromImage(picture)) {
            g.Clear(Color.White);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (dark[y, x]) g.FillRectangle(Brushes.Black, (x + quiet) * scale, (y + quiet) * scale, scale, scale);
        }
        return picture;
    }

    // Starts what the chosen way of connecting needs. Runs off the window's thread.
    async Task Connect() {
        int mine = ++generation;
        ShowAddress("");
        StopChildren();
        string mode = Mode;
        try {
            if (mode == "own") {
                string server = Convert.ToString(config["OwnServer"]).Trim().TrimEnd('/');
                if (!Regex.IsMatch(server, @"\Ahttps?://[^/\s]+\z")) { Say("请填写中转地址，例如 https://cli.example.com", true); return; }
                config["Server"] = server; Save();
                if (agent != null) agent.Reset();
                ShowAddress(server);
                Say("使用自有中转。手机 App 扫码或输入地址连接");
                return;
            }
            string python = Path.Combine(appDir, "python", "python.exe");
            if (!File.Exists(python)) { Say("安装不完整：缺少 " + python, true); return; }
            int port = Port;
            if (Listening(port)) { Say("端口 " + port + " 已被别的程序占用，请在 config.json 里改 Port 后重新打开", true); return; }
            var start = new ProcessStartInfo(python, "\"" + Path.Combine(appDir, "relay", "server.py") + "\" --host " + (mode == "lan" ? "0.0.0.0" : "127.0.0.1") + " --port " + port
                + " --data \"" + Path.Combine(dataDir, "relay") + "\" --web \"" + Path.Combine(appDir, "web") + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = appDir };
            start.EnvironmentVariables["RCLI_PASSWORD"] = password;
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            relay = Process.Start(start);
            Remember();
            for (int n = 0; n < 40 && !Listening(port); n++) { await Task.Delay(250); if (relay.HasExited) break; }
            if (mine != generation) return;
            if (!Listening(port)) { Say("本机服务没有启动成功", true); return; }
            config["Server"] = "http://127.0.0.1:" + port; Save();
            if (agent != null) agent.Reset();
            if (mode == "lan") {
                string ip = LanAddress();
                if (ip.Length == 0) { Say("没有找到局域网地址，请先连上 Wi-Fi 或网线", true); return; }
                ShowAddress("http://" + ip + ":" + port);
                Say("局域网直连已就绪。手机连同一个 Wi-Fi，用 App 扫码");
                return;
            }
            if (!File.Exists(TunnelFile)) { Say("还没有隧道程序，点“下载隧道程序”", true); return; }
            Say("正在建立公网隧道…");
            var found = new TaskCompletionSource<string>();
            var run = new ProcessStartInfo(TunnelFile, "tunnel --no-autoupdate --url http://127.0.0.1:" + port) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            tunnel = new Process { StartInfo = run, EnableRaisingEvents = true };
            DataReceivedEventHandler look = (s, e) => { if (e.Data == null) return; var m = Regex.Match(e.Data, @"https://[a-z0-9-]+\.trycloudflare\.com"); if (m.Success) found.TrySetResult(m.Value); };
            tunnel.ErrorDataReceived += look; tunnel.OutputDataReceived += look;
            tunnel.Exited += (s, e) => { found.TrySetResult(""); if (mine == generation && !quitting) { ShowAddress(""); Say("公网隧道断开了，点“重新连接”", true); } };
            tunnel.Start(); tunnel.BeginErrorReadLine(); tunnel.BeginOutputReadLine();
            Remember();
            string url = await Task.WhenAny(found.Task, Task.Delay(40000)) == found.Task ? found.Task.Result : "";
            if (mine != generation) return;
            if (url.Length == 0) { Kill(tunnel); Say("公网隧道没有建立成功，请检查网络后重试", true); return; }
            ShowAddress(url);
            Say("公网隧道已就绪。手机在任何网络下用 App 扫码；地址每次启动都会变");
        } catch (Exception error) { Say("启动失败：" + error.Message, true); }
    }
    void Reconnect() { Task.Run(() => Connect()); }

    async void DownloadTunnel(object sender, EventArgs e) {
        if (File.Exists(TunnelFile)) { Reconnect(); return; }
        if (MessageBox.Show(this, "将从 Cloudflare 的 GitHub 发布页下载 cloudflared（约 60 MB），保存到\n" + TunnelFile + "\n\n它用来建立临时公网地址，不需要账号。继续吗？", "下载隧道程序", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var button = (Button)sender;
        button.Enabled = false;
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new WebClient()) {
                client.DownloadProgressChanged += (s, p) => Say("正在下载隧道程序 " + p.ProgressPercentage + "%");
                await client.DownloadFileTaskAsync(new Uri(TunnelDownload), TunnelFile + ".part");
            }
            if (new FileInfo(TunnelFile + ".part").Length < 10 * 1024 * 1024) throw new IOException("下载的文件不完整");
            File.Move(TunnelFile + ".part", TunnelFile);
            Reconnect();
        } catch (Exception error) {
            try { File.Delete(TunnelFile + ".part"); } catch { }
            Say("下载失败：" + error.Message + "。也可以自己下载 cloudflared-windows-amd64.exe，改名为 cloudflared.exe 放到 " + dataDir, true);
        } finally { button.Enabled = true; }
    }

    // ---------- window
    T Place<T>(Control parent, T control, int x, int y, int w, int h) where T : Control {
        control.SetBounds(x, y, w, h);
        parent.Controls.Add(control);
        return control;
    }
    Label Note(Control parent, string text, int x, int y, int w, int h, Color color, float size = 0, bool bold = false) {
        return Place(parent, Theme.Label(text, color, parent is Card ? Theme.Panel : Theme.Bg, size, bold), x, y, w, h);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitle(Handle); }
    public App() {
        LoadSettings();
        KillLeftovers();
        Text = "Remote CLI"; Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.None;      // positions below are for 100%; the whole window is scaled once at the end
        ClientSize = new Size(560, 732); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg; ForeColor = Theme.Ink;
        Icon = Theme.MarkIcon(32);

        Place(this, new PictureBox { Image = Theme.Mark(80), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Bg }, 20, 18, 40, 40);
        Note(this, "Remote CLI", 72, 14, 300, 26, Theme.Ink, 14f, true);
        Note(this, "v" + Version, 478, 22, 62, 18, Theme.Muted).TextAlign = ContentAlignment.TopRight;
        Place(this, status, 72, 41, 468, 20); status.BackColor = Theme.Bg; status.AutoEllipsis = true; status.Text = "正在启动…"; status.ForeColor = Theme.Busy;

        var way = Place(this, new Card(), 20, 76, 520, 256);
        Note(way, "连接方式", 16, 12, 200, 18, Theme.Muted, 0, true);
        lan.Title = "局域网直连"; lan.About = "手机和电脑在同一个 Wi-Fi，速度最快";
        cloud.Title = "公网隧道"; cloud.About = "不需要服务器和账号，手机在哪都能连";
        own.Title = "自有中转"; own.About = "已在自己的服务器上部署 relay，地址固定";
        Place(way, lan, 16, 36, 488, 52); Place(way, cloud, 16, 94, 372, 52); Place(way, own, 16, 152, 488, 52);
        var download = Place(way, Theme.Button(File.Exists(TunnelFile) ? "重新建立" : "下载隧道程序"), 396, 104, 108, 32);
        download.Click += DownloadTunnel;
        Place(way, ownField, 16, 213, 388, 30); ownUrl.Text = Convert.ToString(config["OwnServer"]);
        var apply = Place(way, Theme.Button("应用"), 412, 212, 92, 32);
        (Mode == "lan" ? lan : Mode == "cloud" ? cloud : own).Checked = true;
        EventHandler pick = (s, e) => {
            foreach (var option in new[] { lan, cloud, own }) option.Checked = option == s;
            config["Mode"] = s == lan ? "lan" : s == cloud ? "cloud" : "own";
            config["OwnServer"] = ownUrl.Text.Trim();
            Save(); Reconnect();
        };
        lan.Chosen += pick; cloud.Chosen += pick; own.Chosen += pick;
        apply.Click += (s, e) => { config["OwnServer"] = ownUrl.Text.Trim(); Save(); if (!own.Checked) pick(own, EventArgs.Empty); else Reconnect(); };

        var phone = Place(this, new Card(), 20, 344, 520, 196);
        Place(phone, qr, 16, 16, 164, 164); qr.SizeMode = PictureBoxSizeMode.Zoom; qr.BackColor = Color.White;
        Note(phone, "手机装好 App 后，用相机扫左边的二维码；\n扫不了时在 App 里输入下面的地址和密码。", 196, 14, 308, 38, Theme.Muted);
        Note(phone, "地址", 196, 62, 40, 20, Theme.Muted);
        Place(phone, addressField, 238, 56, 202, 30); addressBox.ReadOnly = true;
        Place(phone, Theme.Button("复制"), 446, 55, 58, 32).Click += (s, e) => { if (addressBox.Text.Length > 0) Clipboard.SetText(addressBox.Text); };
        Note(phone, "密码", 196, 100, 40, 20, Theme.Muted);
        Place(phone, passwordField, 238, 94, 202, 30); passwordBox.ReadOnly = true;
        Place(phone, Theme.Button("复制"), 446, 93, 58, 32).Click += (s, e) => Clipboard.SetText(passwordBox.Text);
        Place(phone, Theme.Button("换一个密码"), 238, 132, 110, 30).Click += (s, e) => {
            if (MessageBox.Show(this, own.Checked ? "自有中转的密码由中转服务决定。要把这里保存的密码改成中转上的密码吗？" : "换密码后，已连接的手机需要重新扫码。继续吗？", "密码", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            string wanted = own.Checked ? Ask("中转服务的密码", "") : NewPassword();
            if (String.IsNullOrWhiteSpace(wanted)) return;
            SetPassword(wanted.Trim()); Reconnect();
        };
        Note(phone, "地址和密码相当于这台电脑的钥匙，不要发给别人。", 196, 168, 312, 18, Theme.Muted, 8.25f);

        var projects = Place(this, new Card(), 20, 552, 520, 116);
        Note(projects, "项目文件夹", 16, 10, 90, 18, Theme.Muted, 0, true);
        Note(projects, "手机只能在这些文件夹里开终端", 104, 10, 300, 18, Theme.Muted, 8.25f);
        Place(projects, folders, 16, 36, 388, 68);
        folders.IntegralHeight = false; folders.BorderStyle = BorderStyle.None; folders.BackColor = Theme.Bg; folders.ForeColor = Theme.Ink; folders.DrawMode = DrawMode.OwnerDrawFixed; folders.ItemHeight = 22;
        folders.DrawItem += (s, e) => {
            if (e.Index < 0) return;
            bool chosen = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(chosen ? Theme.Raised : Theme.Bg)) e.Graphics.FillRectangle(back, e.Bounds);
            string line = Convert.ToString(folders.Items[e.Index]); int at = line.IndexOf('\t');
            string name = at > 0 ? line.Substring(0, at) : line, path = at > 0 ? line.Substring(at + 1) : "";
            using (var bold = new Font(Font, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center }) {
                float wide = Math.Min(e.Bounds.Width * 0.4f, e.Graphics.MeasureString(name, bold).Width + 10);
                e.Graphics.DrawString(name, bold, ink, new RectangleF(e.Bounds.X + 6, e.Bounds.Y, wide, e.Bounds.Height), format);
                e.Graphics.DrawString(path, Font, muted, new RectangleF(e.Bounds.X + 6 + wide, e.Bounds.Y, e.Bounds.Width - wide - 10, e.Bounds.Height), format);
            }
        };
        Place(projects, Theme.Button("添加…", true), 412, 36, 92, 30).Click += (s, e) => {
            using (var dialog = new FolderBrowserDialog { Description = "选择一个项目文件夹" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var list = Dirs();
                string name = new DirectoryInfo(dialog.SelectedPath).Name.Replace("=", " ");
                if (name.Length == 0 || name.EndsWith(":") || name.EndsWith("\\")) name = "磁盘 " + dialog.SelectedPath.Substring(0, 1);
                string wanted = name;
                for (int n = 2; list.Any(line => line.StartsWith(name + "=")); n++) name = wanted + " " + n;
                list.Add(name + "=" + dialog.SelectedPath);
                config["RemoteDirs"] = list.ToArray(); Save(); ListFolders();
            }
        };
        Place(projects, Theme.Button("移除"), 412, 72, 92, 30).Click += (s, e) => {
            if (folders.SelectedIndex < 0) return;
            var list = Dirs(); list.RemoveAt(folders.SelectedIndex);
            config["RemoteDirs"] = list.ToArray(); Save(); ListFolders();
        };
        ListFolders();

        foreach (var box in new[] { enabled, autostart }) { box.FlatStyle = FlatStyle.Flat; box.ForeColor = Theme.Ink; box.BackColor = Theme.Bg; box.Cursor = Cursors.Hand; }
        Place(this, enabled, 22, 680, 260, 22); enabled.Text = "允许手机访问";
        enabled.Checked = Convert.ToString(config["RemoteEnabled"]) == "True";
        enabled.CheckedChanged += (s, e) => { config["RemoteEnabled"] = enabled.Checked; Save(); };
        Place(this, autostart, 22, 702, 260, 22); autostart.Text = "登录 Windows 后自动启动";
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) autostart.Checked = key != null && key.GetValue("RemoteCli") != null;
        autostart.CheckedChanged += (s, e) => {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                if (autostart.Checked) key.SetValue("RemoteCli", "\"" + Application.ExecutablePath + "\" --hidden"); else key.DeleteValue("RemoteCli", false);
            }
        };
        Place(this, Theme.Button("重新连接"), 336, 684, 98, 32).Click += (s, e) => Reconnect();
        Place(this, Theme.Button("退出"), 442, 684, 98, 32).Click += (s, e) => Quit();

        tray.Icon = Theme.MarkIcon(32); tray.Visible = true; tray.Text = "Remote CLI";
        tray.DoubleClick += (s, e) => Reveal();
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("显示窗口", null, (s, e) => Reveal());
        tray.ContextMenuStrip.Items.Add("退出", null, (s, e) => Quit());
        FormClosing += (s, e) => {
            if (quitting || e.CloseReason != CloseReason.UserClosing) { Shutdown(); return; }
            e.Cancel = true; Hide();
            if (!hinted) { hinted = true; tray.ShowBalloonTip(4000, "Remote CLI 仍在运行", "手机可以继续连接。要停止，请在托盘图标上点右键选“退出”。", ToolTipIcon.Info); }
        };
        using (var g = CreateGraphics()) {
            float factor = g.DpiX / 96f;
            if (factor > 1.01f) { Scale(new SizeF(factor, factor)); folders.ItemHeight = (int)(22 * factor); }
        }
        Shown += (s, e) => {
            agent = new TerminalAgent(dataDir);
            Task.Run(async () => { await Connect(); await agent.Run(); });
        };
    }
    void ListFolders() { folders.Items.Clear(); foreach (string line in Dirs()) { int at = line.IndexOf('='); folders.Items.Add(at > 0 ? line.Substring(0, at) + "\t" + line.Substring(at + 1) : line); } }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void Shutdown() { generation++; tray.Visible = false; StopChildren(); }
    void Quit() {
        if (MessageBox.Show(this, "退出后手机不能再连接，正在运行的终端会结束。", "退出 Remote CLI", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        quitting = true; Shutdown();
        Environment.Exit(0);
    }
    string Ask(string title, string value) {
        using (var dialog = new Form { Text = title, ClientSize = new Size(360, 96), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Font = Font }) {
            var box = new TextBox { Text = value }; box.SetBounds(14, 16, 332, 26);
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK }; ok.SetBounds(256, 54, 90, 28);
            dialog.Controls.Add(box); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
            return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : "";
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] public static int Main(string[] args) {
        // --qr <text> <file.png>: writes the picture the window would show; used by the tests.
        if (args.Length == 3 && args[0] == "--qr") { using (var picture = Picture(args[1], 8)) { if (picture == null) return 2; picture.Save(args[2], ImageFormat.Png); } return 0; }
        bool created;
        using (var single = new Mutex(true, "Local\\RemoteCliApp", out created)) {
            if (!created) { MessageBox.Show("Remote CLI 已经在运行，请看屏幕右下角的托盘图标。", "Remote CLI"); return 0; }
            ServicePointManager.DefaultConnectionLimit = 8;      // the held request and the reports run side by side
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var window = new App();
            // --screenshot <file.png>: draws the window into a picture after a moment and exits; used for the documentation.
            if (args.Length == 2 && args[0] == "--screenshot") {
                window.Show();
                var until = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(30); }
                using (var picture = new Bitmap(window.ClientSize.Width, window.ClientSize.Height)) {
                    using (var g = Graphics.FromImage(picture)) g.Clear(Theme.Bg);
                    Rectangle client = window.RectangleToScreen(window.ClientRectangle);
                    using (var whole = new Bitmap(window.Width, window.Height)) {
                        window.DrawToBitmap(whole, new Rectangle(0, 0, window.Width, window.Height));
                        using (var g = Graphics.FromImage(picture)) g.DrawImage(whole, -(client.X - window.Left), -(client.Y - window.Top));
                    }
                    picture.Save(args[1], ImageFormat.Png);
                }
                window.quitting = true; window.Shutdown();
                Environment.Exit(0);
            }
            if (args.Contains("--hidden")) { window.WindowState = FormWindowState.Minimized; window.ShowInTaskbar = false; window.Shown += (s, e) => { window.Hide(); window.ShowInTaskbar = true; }; }
            Application.Run(window);
        }
        return 0;
    }
}
}
