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
    const string Version = "0.6.9";
    const string TunnelDownload = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    readonly string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    readonly string dataDir = TerminalAgent.DefaultData;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    Dictionary<string, object> config;
    string password = "", address = "";
    Process relay, tunnel, kept;
    string keptUrl = "";
    TerminalAgent agent;
    int generation;

    readonly StatusLine status = new StatusLine(), sideStatus = new StatusLine();
    readonly Segmented modePick = new Segmented();
    readonly Label modeAbout = new Label(), qrHint = new Label(), toast = new Label(), activityNote = new Label();
    readonly Field ownField = new Field(), addressField = new Field(), passwordField = new Field();
    TextBox ownUrl { get { return ownField.Box; } }
    TextBox addressBox { get { return addressField.Box; } }
    TextBox passwordBox { get { return passwordField.Box; } }
    readonly PictureBox qr = new PictureBox();
    readonly RowList folders = new RowList(), activity = new RowList();
    readonly Switch enabledSwitch = new Switch(), autostartSwitch = new Switch();
    readonly List<Panel> pages = new List<Panel>();
    readonly List<NavItem> nav = new List<NavItem>();
    readonly System.Windows.Forms.Timer toastTimer = new System.Windows.Forms.Timer(), activityTimer = new System.Windows.Forms.Timer();
    RoundButton updateButton, tunnelButton, ownApply;
    string activityMark = "";
    bool trouble, preparing = true;
    static string ComputerName = Environment.MachineName;
    readonly NotifyIcon tray = new NotifyIcon();
    bool quitting, hinted, updating;
    ReleaseUpdate availableUpdate;

    string ConfigFile { get { return Path.Combine(dataDir, "config.json"); } }
    string PasswordFile { get { return Path.Combine(dataDir, "password.dpapi"); } }
    string TunnelFile { get { return Path.Combine(dataDir, "cloudflared.exe"); } }
    string ChildrenFile { get { return Path.Combine(dataDir, "children.json"); } }
    string KeptFile { get { return Path.Combine(dataDir, "tunnel.json"); } }
    string Mode { get { return Convert.ToString(config["Mode"]); } }
    int Port { get { return Convert.ToInt32(config["Port"]); } }

    // ---------- settings
    void LoadSettings() {
        Directory.CreateDirectory(dataDir);
        try { config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile, Encoding.UTF8)); } catch { config = null; }
        if (config == null) config = new Dictionary<string, object>();
        foreach (var item in new Dictionary<string, object> { { "Mode", "lan" }, { "Port", 8722 }, { "Server", "" }, { "OwnServer", "" }, { "RemoteEnabled", true },
                 { "RemoteMaxMode", "full" }, { "TunnelProtocol", "auto" }, { "RemoteDirs", new object[0] } })
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
                try { var p = Process.GetProcessById(Convert.ToInt32(id)); if (kept != null && p.Id == kept.Id) continue; if (p.ProcessName == "python" || p.ProcessName == "cloudflared") Kill(p); } catch { }
        } catch { }
    }
    // An update leaves the tunnel running: the new version takes it over, so the phone keeps the address it knows.
    void StopChildren() { if (!updating) { Kill(tunnel); tunnel = null; } Kill(relay); relay = null; Remember(); }
    void Keep(string url, int port) {
        try {
            File.WriteAllText(KeptFile, json.Serialize(new Dictionary<string, object> { { "Pid", tunnel.Id }, { "Started", tunnel.StartTime.ToFileTimeUtc().ToString() }, { "Url", url },
                { "Port", port }, { "Protocol", Convert.ToString(config["TunnelProtocol"]) } }));
        } catch { }
    }
    // The tunnel an update or a crash of this program left running, when it still fits the settings.
    Process Kept() {
        try {
            var was = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(KeptFile));
            var p = Process.GetProcessById(Convert.ToInt32(was["Pid"]));
            if (p.ProcessName != "cloudflared" || p.StartTime.ToFileTimeUtc().ToString() != Convert.ToString(was["Started"])) return null;
            if (Mode != "cloud" || Convert.ToInt32(was["Port"]) != Port || Convert.ToString(was["Protocol"]) != Convert.ToString(config["TunnelProtocol"])) return null;
            keptUrl = Convert.ToString(was["Url"]);
            return p;
        } catch { return null; }
    }
    // Cloudflare answers 530 for an address whose tunnel is gone; anything else, even no answer, leaves it in use.
    static bool Gone(string url) {
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url + "/api/session"); request.Timeout = 8000;
            using (request.GetResponse()) return false;
        } catch (WebException error) { var reply = error.Response as HttpWebResponse; return reply != null && (int)reply.StatusCode == 530; }
        catch { return false; }
    }
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
        status.Set(text, problem ? Theme.Bad : text.StartsWith("正在") ? Theme.Busy : Theme.Good);
        trouble = problem; preparing = !problem && text.StartsWith("正在");
        if (nav.Count > 0) RefreshActivity();
        tray.Text = ("Remote CLI：" + text).Length > 60 ? ("Remote CLI：" + text).Substring(0, 60) : "Remote CLI：" + text;
    }
    void ShowAddress(string where) {
        if (InvokeRequired) { BeginInvoke(new Action(() => ShowAddress(where))); return; }
        address = where;
        addressBox.Text = where; passwordBox.Text = password;
        Image old = qr.Image;
        string link = "remotecli://connect?u=" + Uri.EscapeDataString(where) + "&p=" + Uri.EscapeDataString(password);
        // The computer's name lets a phone that knows several computers tell them apart; it is left out when the code would not fit.
        qr.Image = where.Length > 0 ? (Picture(link + "&n=" + Uri.EscapeDataString(ComputerName), 6) ?? Picture(link, 6)) : null;
        qr.Visible = qr.Image != null; qrHint.Visible = qr.Image == null;
        if (old != null) old.Dispose();
        ShowMode();
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
        Process held = kept; kept = null;      // only the first start takes a tunnel over; "reconnect" always makes a new one
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
            if (Listening(port)) { Say("端口 " + port + " 已被别的程序占用，请到“设置”里换一个端口", true); return; }
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
            if (!File.Exists(TunnelFile)) { Say("还没有隧道程序，点下面的“下载隧道程序”", true); return; }
            EventHandler lost = (s, e) => { if (mine == generation && !quitting) { ShowAddress(""); Say("公网隧道断开了，点“重新连接”。经常断开时到“设置”把隧道协议改为 HTTP/2", true); } };
            if (held != null && !held.HasExited && !Gone(keptUrl)) {
                if (mine != generation) return;
                tunnel = held; tunnel.Exited += lost; tunnel.EnableRaisingEvents = true;
                Remember();
                ShowAddress(keptUrl);
                Say("公网隧道已就绪，地址和上次相同，手机不用重新扫码");
                return;
            }
            Say("正在建立公网隧道…");
            var found = new TaskCompletionSource<string>();
            var run = new ProcessStartInfo(TunnelFile, "tunnel --no-autoupdate" + (Convert.ToString(config["TunnelProtocol"]) == "http2" ? " --protocol http2" : "") + " --url http://127.0.0.1:" + port) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = dataDir };
            tunnel = new Process { StartInfo = run, EnableRaisingEvents = true };
            DataReceivedEventHandler look = (s, e) => { if (e.Data == null) return; var m = Regex.Match(e.Data, @"https://[a-z0-9-]+\.trycloudflare\.com"); if (m.Success) found.TrySetResult(m.Value); };
            tunnel.ErrorDataReceived += look; tunnel.OutputDataReceived += look;
            tunnel.Exited += (s, e) => { found.TrySetResult(""); lost(s, e); };
            tunnel.Start(); tunnel.BeginErrorReadLine(); tunnel.BeginOutputReadLine();
            Remember();
            string url = await Task.WhenAny(found.Task, Task.Delay(40000)) == found.Task ? found.Task.Result : "";
            if (mine != generation) return;
            if (url.Length == 0) { Kill(tunnel); Say("公网隧道没有建立成功，请检查网络后重试", true); return; }
            Keep(url, port);
            ShowAddress(url);
            Say("公网隧道已就绪。手机在任何网络下用 App 扫码；更新程序后地址不变");
        } catch (Exception error) { Say("启动失败：" + error.Message, true); }
        finally { if (held != null && held != tunnel) Kill(held); }
    }
    void Reconnect() { Task.Run(() => Connect()); }

    async void DownloadTunnel(object sender, EventArgs e) {
        if (File.Exists(TunnelFile)) { Reconnect(); return; }
        if (!Confirm("下载隧道程序？", "从 Cloudflare 的 GitHub 发布页下载 cloudflared（约 60 MB），用来建立临时公网地址，不需要账号。", "下载", false)) return;
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
    const int SideWidth = 212, PageWidth = 708, WindowHeight = 600;
    T Place<T>(Control parent, T control, int x, int y, int w, int h) where T : Control {
        control.SetBounds(x, y, w, h);
        parent.Controls.Add(control);
        return control;
    }
    Label Note(Control parent, string text, int x, int y, int w, int h, Color color, float size = 0, bool bold = false) {
        return Place(parent, Theme.Label(text, color, parent.BackColor, size, bold), x, y, w, h);
    }
    RoundButton Action(Control parent, string text, string glyph, ButtonKind kind, int x, int y, int w, int h, EventHandler click) {
        var button = Place(parent, new RoundButton { Text = text, Glyph = glyph, Kind = kind }, x, y, w, h);
        if (click != null) button.Click += click;
        return button;
    }
    Panel Page(string title, string about) {
        var page = Place(this, new Panel { BackColor = Theme.Bg, Visible = false }, SideWidth, 0, PageWidth, WindowHeight);
        Note(page, title, 28, 24, 420, 30, Theme.Ink, 15f, true);
        Note(page, about, 28, 56, 652, 20, Theme.Muted);
        pages.Add(page);
        return page;
    }
    void Go(int index) {
        for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; nav[i].Chosen = i == index; }
        if (index == 2) RefreshActivity();
    }
    // A short confirmation that fades by itself, e.g. after copying.
    void Toast(string text) {
        toast.Text = text;
        using (var g = CreateGraphics()) toast.Width = (int)g.MeasureString(text, toast.Font).Width + (int)(36 * g.DpiX / 96f);
        toast.Left = pages[0].Left + (pages[0].Width - toast.Width) / 2;
        toast.Visible = true; toast.BringToFront();
        toastTimer.Stop(); toastTimer.Start();
    }
    void Copy(string text, string what) {
        if (String.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Toast("已复制" + what); } catch (ExternalException) { Toast("复制失败，请再试一次"); }
    }
    void ShowMode() {
        int mode = Mode == "lan" ? 0 : Mode == "cloud" ? 1 : 2;
        modePick.Chosen = mode;
        modeAbout.Text = mode == 0 ? "手机和电脑连同一个 Wi-Fi 时用，速度最快。第一次使用时 Windows 防火墙会询问，请选“允许”。"
            : mode == 1 ? "不需要服务器和账号，手机在任何网络都能连。更新程序后地址不变；退出程序或重启电脑后地址会变，手机需要重新扫码。"
            : "已经在自己的服务器上部署了 relay 时用，地址固定。密码用中转服务的密码。";
        tunnelButton.Visible = mode == 1; ownField.Visible = ownApply.Visible = mode == 2;
        tunnelButton.Text = File.Exists(TunnelFile) ? "重新建立隧道" : "下载隧道程序（约 60 MB）";
        tunnelButton.Glyph = File.Exists(TunnelFile) ? Theme.IconRefresh : Theme.IconDownload;
    }
    void ShowProjects() {
        folders.Show(Dirs().Select(line => {
            int at = line.IndexOf('=');
            string name = at > 0 ? line.Substring(0, at) : line, path = at > 0 ? line.Substring(at + 1) : "";
            bool there = false; try { there = Directory.Exists(path); } catch { }
            return new Row { Glyph = Theme.IconFolder, Title = name, About = path, Tag = there ? "" : "文件夹不存在", TagColour = Theme.Bad, Value = path };
        }));
        nav[1].SetBadge(folders.Rows.Count > 0 ? folders.Rows.Count.ToString() : "");
    }
    void AddProject(string path) {
        var list = Dirs();
        string full; try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return; }
        if (!Directory.Exists(full)) return;
        if (list.Any(line => String.Equals(line.Substring(line.IndexOf('=') + 1).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))) { Toast("这个文件夹已经在列表里"); return; }
        string name = new DirectoryInfo(full).Name.Replace("=", " ");
        if (name.Length == 0 || name.EndsWith(":") || name.EndsWith("\\")) name = "磁盘 " + full.Substring(0, 1);
        string wanted = name;
        for (int n = 2; list.Any(line => line.StartsWith(name + "=")); n++) name = wanted + " " + n;
        list.Add(name + "=" + full);
        config["RemoteDirs"] = list.ToArray(); Save(); ShowProjects();
        folders.SelectedIndex = folders.Items.Count - 1;
        Toast("已添加 " + name);
    }
    static string Since(DateTime started) {
        TimeSpan gone = DateTime.UtcNow - started;
        return gone.TotalMinutes < 1 ? "刚刚开始" : gone.TotalHours < 1 ? (int)gone.TotalMinutes + " 分钟前开始" : gone.TotalDays < 1 ? (int)gone.TotalHours + " 小时前开始" : (int)gone.TotalDays + " 天前开始";
    }
    void RefreshActivity() {
        List<string[]> running = agent == null ? new List<string[]>() : agent.Running();
        if (running == null) return;
        string mark = String.Join("|", running.Select(r => String.Join(",", r)));
        int phones = running.Count(r => r[6] == "phone"), cli = running.Count(r => r[6] == "cli");
        nav[2].SetBadge(running.Count > 0 ? running.Count.ToString() : "");
        bool allowed = Convert.ToString(config["RemoteEnabled"]) == "True";
        sideStatus.Set(trouble ? "还不能连接" : preparing ? "正在准备…" : !allowed ? "已暂停手机访问" : phones == 0 ? "手机可以连接" : phones + " 个手机终端运行中",
            trouble ? Theme.Bad : preparing || !allowed ? Theme.Busy : Theme.Good);
        if (mark == activityMark && running.Count > 0 && !pages[2].Visible) return;
        activityMark = mark;
        activity.Show(running.Select(r => {
            DateTime started; DateTime.TryParse(r[3], null, System.Globalization.DateTimeStyles.RoundtripKind, out started);
            string tool = r[0] == "claude" ? "Claude Code" : r[0] == "codex" ? "Codex" : "PowerShell";
            string tag = r[6] == "shared" ? "共享后台保留" : r[6] == "remote" ? "其它远程终端" : r[6] == "unknown" ? "归属待确认" : r[6] == "cli" ? "电脑 CLI" : r[2] == "busy" ? "正在执行" : "手机终端";
            return new Row { Glyph = Theme.IconTerminal, Title = (r[4].Length > 0 ? r[4] : tool) + " · " + r[1], About = (r[5].Length > 0 ? r[5] + " · " : "") + tool + (r[6] == "phone" ? " · 双击可在电脑接管 · " + Since(started.ToUniversalTime()) : ""), Value = r,
                             Tag = tag, TagColour = r[6] == "phone" || r[6] == "cli" ? Theme.Good : Theme.Busy };
        }));
        activityNote.Text = running.Count == 0 ? "" : phones + " 个手机终端 · " + cli + " 个电脑 CLI。这里仅显示可操作的活动；后台写入锁不代表电脑有窗口。";
    }
    void TakeoverActivity() {
        var row = activity.Selected; var raw = row == null ? null : row.Value as string[];
        if (raw == null || raw.Length < 8 || raw[6] != "phone") { Toast("只有手机创建的终端可以从电脑接管"); return; }
        if (agent == null) { Toast("电脑后台还没有准备好，请稍后再试"); return; }
        string tool = raw[0] == "claude" ? "Claude Code" : raw[0] == "codex" ? "Codex" : "PowerShell";
        if (raw[0] == "shell") { Toast("PowerShell 没有可恢复的会话，请在电脑端项目页重新新建"); return; }
        if (!Confirm("在电脑上接管这个终端？", "手机终端会先结束，然后在电脑打开 " + tool + "。对话会继续使用同一个会话；接管期间手机不能继续输入。", "接管并在电脑打开", true)) return;
        activity.Enabled = false;
        Task.Run(() => {
            try {
                var takeover = agent.TakeoverForComputer(raw[7]);
                BeginInvoke(new Action(() => {
                    activity.Enabled = true;
                    try { Process.Start(TerminalAgent.ComputerProcess(takeover, Convert.ToString(config["RemoteMaxMode"]))); Toast("已在电脑上接管 " + tool); RefreshActivity(); }
                    catch (Exception error) { Toast("电脑终端没有打开：" + error.Message); }
                }));
            } catch (Exception error) {
                BeginInvoke(new Action(() => { activity.Enabled = true; Toast(error.Message); RefreshActivity(); }));
            }
        });
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitle(Handle); }
    public App() {
        LoadSettings();
        kept = Kept();
        KillLeftovers();
        Text = "Remote CLI"; Font = Theme.Text();
        AutoScaleMode = AutoScaleMode.None;      // positions below are for 100%; the whole window is scaled once at the end
        ClientSize = new Size(SideWidth + PageWidth, WindowHeight); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg; ForeColor = Theme.Ink;
        Icon = Theme.MarkIcon(32);

        // ---- side bar
        var side = Place(this, new Panel { BackColor = Theme.Side }, 0, 0, SideWidth, WindowHeight);
        Place(side, new PictureBox { Image = Theme.Mark(88), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Side }, 20, 24, 40, 40);
        Note(side, "Remote CLI", 70, 24, 130, 22, Theme.Ink, 11.5f, true);
        Note(side, "v" + Version, 71, 46, 130, 18, Theme.Faint, 8.25f);
        string[] names = { "连接", "项目", "活动", "设置" }, glyphs = { Theme.IconPhone, Theme.IconFolder, Theme.IconPulse, Theme.IconGear };
        for (int i = 0; i < names.Length; i++) {
            int index = i;
            var item = Place(side, new NavItem { Title = names[i], Glyph = glyphs[i] }, 12, 92 + i * 46, SideWidth - 24, 40);
            item.Click += (s, e) => Go(index);
            nav.Add(item);
        }
        Place(side, sideStatus, 18, WindowHeight - 84, SideWidth - 30, 22); sideStatus.BackColor = Theme.Side; sideStatus.Set("正在启动…", Theme.Busy);
        Note(side, "关闭窗口后仍在托盘运行", 20, WindowHeight - 58, SideWidth - 30, 18, Theme.Faint, 8.25f);
        Action(side, "退出", Theme.IconPower, ButtonKind.Ghost, 12, WindowHeight - 38, 76, 28, (s, e) => Quit()).BackColor = Theme.Side;

        // ---- page: connect
        var connect = Page("连接手机", "在手机 App 里点“扫码连接”，扫下面的二维码。");
        Place(connect, status, 28, 82, 652, 26); status.Font = Theme.Text(9.5f, true); status.Set("正在启动…", Theme.Busy);
        var code = Place(connect, new Card(), 28, 122, 252, 318);
        var tile = Place(code, new Card { Fill = Color.White }, 22, 22, 208, 208);
        Place(tile, qr, 10, 10, 188, 188); qr.SizeMode = PictureBoxSizeMode.Zoom; qr.BackColor = Color.White;
        Place(tile, qrHint, 10, 84, 188, 40); qrHint.BackColor = Color.White; qrHint.ForeColor = Color.FromArgb(120, 124, 140); qrHint.TextAlign = ContentAlignment.MiddleCenter; qrHint.Text = "正在准备…";
        var caption = Note(code, "用手机 App 扫码", 12, 242, 228, 22, Theme.Ink, 10f, true); caption.TextAlign = ContentAlignment.MiddleCenter;
        var sub = Note(code, "或在 App 里输入右边的地址和密码", 12, 266, 228, 18, Theme.Muted, 8.5f); sub.TextAlign = ContentAlignment.MiddleCenter;
        var keys = Note(code, Theme.IconLock + "  地址和密码不要发给别人", 12, 288, 228, 18, Theme.Faint, 8.25f); keys.TextAlign = ContentAlignment.MiddleCenter; keys.Font = new Font(Theme.Icons, 8.25f);
        keys.Paint += (s, e) => { };

        var way = Place(connect, new Card(), 296, 122, 384, 318);
        Note(way, "连接方式", 20, 16, 200, 18, Theme.Muted, 8.5f, true);
        Place(way, modePick, 20, 40, 344, 36); modePick.Items = new[] { "局域网", "公网隧道", "自有中转" };
        Place(way, modeAbout, 20, 84, 344, 38); modeAbout.BackColor = Theme.Panel; modeAbout.ForeColor = Theme.Muted; modeAbout.Font = Theme.Text(8.5f);
        tunnelButton = Action(way, "", "", ButtonKind.Normal, 20, 126, 230, 32, DownloadTunnel);
        Place(way, ownField, 20, 126, 256, 32); ownUrl.Text = Convert.ToString(config["OwnServer"]);
        ownApply = Action(way, "应用", "", ButtonKind.Normal, 284, 126, 80, 32, (s, e) => { config["OwnServer"] = ownUrl.Text.Trim(); config["Mode"] = "own"; Save(); ShowMode(); Reconnect(); });
        modePick.Changed += (s, e) => { config["Mode"] = modePick.Chosen == 0 ? "lan" : modePick.Chosen == 1 ? "cloud" : "own"; config["OwnServer"] = ownUrl.Text.Trim(); Save(); ShowMode(); Reconnect(); };
        Note(way, "地址", 20, 172, 200, 18, Theme.Muted, 8.5f, true);
        Place(way, addressField, 20, 194, 256, 34); addressBox.ReadOnly = true;
        Action(way, "复制", Theme.IconCopy, ButtonKind.Normal, 284, 194, 80, 34, (s, e) => Copy(addressBox.Text, "地址"));
        Note(way, "密码", 20, 238, 200, 18, Theme.Muted, 8.5f, true);
        Place(way, passwordField, 20, 260, 256, 34); passwordBox.ReadOnly = true; passwordBox.Font = new Font("Consolas", 10f);
        Action(way, "复制", Theme.IconCopy, ButtonKind.Normal, 284, 260, 80, 34, (s, e) => Copy(passwordBox.Text, "密码"));
        Action(connect, "重新连接", Theme.IconRefresh, ButtonKind.Normal, 28, 456, 116, 34, (s, e) => Reconnect());
        Action(connect, "换一个密码", Theme.IconLock, ButtonKind.Ghost, 152, 456, 124, 34, (s, e) => {
            bool mine = Mode != "own";
            string wanted = mine ? (Confirm("换一个密码？", "换密码后，已连接的手机需要重新扫码。", "换密码", false) ? NewPassword() : "") : Ask("中转服务的密码", "自有中转的密码由中转服务决定，这里填它的密码。", "");
            if (String.IsNullOrWhiteSpace(wanted)) return;
            SetPassword(wanted.Trim()); Reconnect(); Toast("密码已更新");
        });
        Note(connect, "手机只能在“项目”里添加的文件夹中开终端；终端开起来后和你坐在电脑前一样，可以访问整台电脑。", 28, 506, 652, 36, Theme.Faint, 8.5f);

        // ---- page: projects
        var projects = Page("项目文件夹", "手机只能在这些文件夹里新建终端和继续对话。可以把文件夹直接拖进来。");
        var listCard = Place(projects, new Card(), 28, 92, 652, 410);
        Place(listCard, folders, 8, 8, 636, 394);
        folders.EmptyTitle = "还没有项目文件夹"; folders.EmptyAbout = "把文件夹拖到这里，或点下面的“添加文件夹”。\n添加后在手机上就能看到这个项目。";
        Action(projects, "添加文件夹", Theme.IconAdd, ButtonKind.Primary, 28, 518, 128, 36, (s, e) => {
            using (var dialog = new FolderBrowserDialog { Description = "选择一个项目文件夹" }) if (dialog.ShowDialog(this) == DialogResult.OK) AddProject(dialog.SelectedPath);
        });
        var rename = Action(projects, "重命名", Theme.IconEdit, ButtonKind.Normal, 164, 518, 100, 36, (s, e) => {
            int at = folders.SelectedIndex; if (at < 0) return;
            var list = Dirs(); string path = list[at].Substring(list[at].IndexOf('=') + 1);
            string name = Ask("项目名称", "手机上显示的名字，不会改动文件夹本身。", folders.Rows[at].Title).Trim().Replace("=", " ");
            if (name.Length == 0 || name.Length > 40) return;
            if (list.Where((line, i) => i != at).Any(line => line.StartsWith(name + "="))) { Toast("已有同名项目"); return; }
            list[at] = name + "=" + path; config["RemoteDirs"] = list.ToArray(); Save(); ShowProjects();
        });
        var open = Action(projects, "打开文件夹", Theme.IconOpen, ButtonKind.Normal, 272, 518, 124, 36, (s, e) => {
            var row = folders.Selected; if (row == null) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + row.Value + "\"")); } catch (Exception) { Toast("打不开这个文件夹"); }
        });
        var remove = Action(projects, "移除", Theme.IconDelete, ButtonKind.Danger, 588, 518, 92, 36, (s, e) => {
            int at = folders.SelectedIndex; if (at < 0) return;
            if (!Confirm("移除“" + folders.Rows[at].Title + "”？", "只是不再让手机访问这个文件夹，文件夹和里面的对话都不会被删除。", "移除", true)) return;
            var list = Dirs(); list.RemoveAt(at); config["RemoteDirs"] = list.ToArray(); Save(); ShowProjects();
        });
        EventHandler chosen = (s, e) => { rename.Enabled = open.Enabled = remove.Enabled = folders.SelectedIndex >= 0; };
        folders.SelectedIndexChanged += chosen; chosen(null, EventArgs.Empty);
        folders.DoubleClick += (s, e) => open.PerformClick();
        foreach (Control target in new Control[] { projects, listCard, folders }) {
            target.AllowDrop = true;
            target.DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; };
            target.DragDrop += (s, e) => { var dropped = e.Data.GetData(DataFormats.FileDrop) as string[]; if (dropped != null) foreach (string path in dropped) AddProject(path); };
        }
        ShowProjects();

        // ---- page: activity
        var activityPage = Page("活动", "电脑和手机看到同一份会话。这里显示手机终端和可定位的电脑 CLI；双击手机终端，可结束手机输入并在电脑接管。");
        var liveCard = Place(activityPage, new Card(), 28, 92, 652, 410);
        Place(liveCard, activity, 8, 8, 636, 394);
        activity.EmptyTitle = "没有正在运行的终端"; activity.EmptyAbout = "在手机上选一个项目，新建 Claude Code、Codex 或 PowerShell 终端，\n它就会出现在这里。";
        activity.DoubleClick += (s, e) => TakeoverActivity();
        activity.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TakeoverActivity(); } };
        Place(activityPage, activityNote, 28, 518, 652, 20); activityNote.BackColor = Theme.Bg; activityNote.ForeColor = Theme.Muted;

        // ---- page: settings
        var settings = Page("设置", "这些设置只影响这台电脑。");
        var rows = Place(settings, new Card(), 28, 92, 652, 6 * 60 + 12);
        enabledSwitch.On = Convert.ToString(config["RemoteEnabled"]) == "True";
        enabledSwitch.Changed += (s, e) => { config["RemoteEnabled"] = enabledSwitch.On; Save(); RefreshActivity(); Toast(enabledSwitch.On ? "已允许手机访问" : "已暂停，手机立即不能操作"); };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) autostartSwitch.On = key != null && key.GetValue("RemoteCli") != null;
        autostartSwitch.Changed += (s, e) => {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                if (autostartSwitch.On) key.SetValue("RemoteCli", "\"" + Application.ExecutablePath + "\" --hidden"); else key.DeleteValue("RemoteCli", false);
            }
        };
        var rights = new Segmented { Items = new[] { "默认", "自动改文件", "只读" } };
        string max = Convert.ToString(config["RemoteMaxMode"]);
        rights.Chosen = max == "edit" ? 1 : max == "read" ? 2 : 0;
        rights.Changed += (s, e) => { config["RemoteMaxMode"] = rights.Chosen == 1 ? "edit" : rights.Chosen == 2 ? "read" : "full"; Save(); Toast("对之后新开的终端生效"); };
        var protocol = new Segmented { Items = new[] { "自动", "HTTP/2" } };
        protocol.Chosen = Convert.ToString(config["TunnelProtocol"]) == "http2" ? 1 : 0;
        protocol.Changed += (s, e) => { config["TunnelProtocol"] = protocol.Chosen == 1 ? "http2" : "auto"; Save(); if (Mode == "cloud") Reconnect(); };
        var portBox = new Field(); portBox.Box.Text = Port.ToString(); portBox.Box.MaxLength = 5;
        EventHandler port = (s, e) => {
            int wanted;
            if (!Int32.TryParse(portBox.Box.Text.Trim(), out wanted) || wanted < 1024 || wanted > 65535) { portBox.Box.Text = Port.ToString(); Toast("端口要在 1024 到 65535 之间"); return; }
            if (wanted == Port) return;
            config["Port"] = wanted; Save(); Toast("端口已改为 " + wanted); if (Mode != "own") Reconnect();
        };
        portBox.Box.Leave += port; portBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; port(s, e); } };
        updateButton = new RoundButton { Text = "检查更新", Glyph = Theme.IconRefresh };
        updateButton.Click += (s, e) => CheckForUpdate(true);
        var all = new[] {
            new SettingRow("允许手机访问", "关闭后手机立即不能操作，正在运行的终端不受影响", enabledSwitch, 46, 26),
            new SettingRow("登录 Windows 后自动启动", "开机后在托盘里运行，不弹出窗口", autostartSwitch, 46, 26),
            new SettingRow("新终端的权限", "Claude Code 和 Codex 从手机启动时用什么权限模式", rights, 250, 34),
            new SettingRow("公网隧道的协议", "隧道经常断开或出现 1033 时改用 HTTP/2", protocol, 170, 34),
            new SettingRow("本机端口", "局域网直连和公网隧道使用，被占用时换一个", portBox, 96, 32),
            new SettingRow("版本 " + Version, "从 GitHub 发布页检查并安装新版本", updateButton, 150, 34) };
        for (int i = 0; i < all.Length; i++) Place(rows, all[i], 2, 6 + i * 60, 648, 60);
        Action(settings, "打开数据文件夹", Theme.IconOpen, ButtonKind.Normal, 28, 484, 150, 36, (s, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dataDir + "\"")); } catch (Exception) { } });
        Action(settings, "项目主页", Theme.IconGlobe, ButtonKind.Ghost, 186, 484, 110, 36, (s, e) => { try { Process.Start(new ProcessStartInfo("https://github.com/KangWang42/remote-cli") { UseShellExecute = true }); } catch (Exception) { } });
        Action(settings, "退出 Remote CLI", Theme.IconPower, ButtonKind.Danger, 530, 484, 150, 36, (s, e) => Quit());

        // ---- toast, tray, timers
        Place(this, toast, 0, WindowHeight - 58, 200, 34);
        toast.Visible = false; toast.BackColor = Theme.Ink; toast.ForeColor = Theme.OnAccent; toast.TextAlign = ContentAlignment.MiddleCenter; toast.Font = Theme.Text(9f, true);
        toastTimer.Interval = 1600; toastTimer.Tick += (s, e) => { toastTimer.Stop(); toast.Visible = false; };
        activityTimer.Interval = 2000; activityTimer.Tick += (s, e) => RefreshActivity();
        tray.Icon = Theme.MarkIcon(32); tray.Visible = true; tray.Text = "Remote CLI";
        tray.DoubleClick += (s, e) => Reveal();
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("显示窗口", null, (s, e) => Reveal());
        tray.ContextMenuStrip.Items.Add("复制地址", null, (s, e) => { if (address.Length > 0) try { Clipboard.SetText(address); } catch (ExternalException) { } });
        tray.ContextMenuStrip.Items.Add("重新连接", null, (s, e) => Reconnect());
        tray.ContextMenuStrip.Items.Add("-");
        tray.ContextMenuStrip.Items.Add("退出", null, (s, e) => Quit());
        FormClosing += (s, e) => {
            if (quitting || e.CloseReason != CloseReason.UserClosing) { Shutdown(); return; }
            e.Cancel = true; Hide();
            if (!hinted) { hinted = true; tray.ShowBalloonTip(4000, "Remote CLI 仍在运行", "手机可以继续连接。要停止，请在托盘图标上点右键选“退出”。", ToolTipIcon.Info); }
        };
        ShowMode();
        Go(Dirs().Count == 0 ? 1 : 0);        // a first start begins where something has to be done
        using (var g = CreateGraphics()) {
            float factor = g.DpiX / 96f;
            if (factor > 1.01f) { Scale(new SizeF(factor, factor)); folders.ItemHeight = (int)(54 * factor); activity.ItemHeight = (int)(54 * factor); }
        }
        Shown += (s, e) => {
            agent = new TerminalAgent(dataDir);
            Task.Run(async () => { await Connect(); await agent.Run(); });
            activityTimer.Start();
            CheckForUpdate(false);
        };
    }
    async void CheckForUpdate(bool manual) {
        if (updateButton == null || !updateButton.Enabled) return;
        if (!manual) {
            DateTime previous;
            if (config.ContainsKey("UpdateCheckUtc") && DateTime.TryParse(Convert.ToString(config["UpdateCheckUtc"]), null, System.Globalization.DateTimeStyles.RoundtripKind, out previous) && DateTime.UtcNow - previous < TimeSpan.FromHours(6)) return;
            config["UpdateCheckUtc"] = DateTime.UtcNow.ToString("o"); Save();
        }
        updateButton.Enabled = false; if (manual) Toast("正在检查 GitHub 最新版本…");
        try {
            ReleaseUpdate found = await AutoUpdater.CheckAsync(Version); availableUpdate = found;
            if (found == null) { updateButton.Text = "已是最新版"; updateButton.Glyph = Theme.IconCheck; if (manual) Toast("已是最新版"); return; }
            updateButton.Text = "更新到 v" + found.Version; updateButton.Kind = ButtonKind.Primary; updateButton.Glyph = Theme.IconDownload; nav[3].SetBadge("新");
            if (Confirm("发现新版本 v" + found.Version, "现在下载并重启更新吗？更新时手机终端会短暂断开，公网隧道的地址不变。", "下载并更新", false)) await InstallUpdate(found);
        } catch (Exception error) { updateButton.Text = "检查更新"; if (manual) Toast("检查更新失败：" + error.Message); }
        finally { updateButton.Enabled = true; }
    }
    async Task InstallUpdate(ReleaseUpdate found) {
        updateButton.Enabled = false; updateButton.Text = "下载中…"; Say("正在从 GitHub 下载 v" + found.Version + "…");
        try {
            string file = await AutoUpdater.DownloadAsync(found, Path.Combine(dataDir, "updates"));
            string folder = appDir;
            Process.Start(new ProcessStartInfo(file, "--quiet --dir \"" + folder + "\" --wait-pid " + Process.GetCurrentProcess().Id + " --launch") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = folder });
            updating = Mode == "cloud" && address.Length > 0; quitting = true; Shutdown(); Application.Exit();
        } catch (Exception error) { updateButton.Enabled = true; updateButton.Text = "更新失败"; Say("更新失败：" + error.Message, true); }
    }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void Shutdown() { generation++; tray.Visible = false; StopChildren(); }
    void Quit() {
        if (!Confirm("退出 Remote CLI？", "退出后手机不能再连接，正在运行的终端会结束。", "退出", true)) return;
        quitting = true; Shutdown();
        Environment.Exit(0);
    }

    // A small window in the program's own look: a question with two answers, optionally with one line to fill in.
    string Dialog(string title, string text, string yes, bool danger, bool field, string value) {
        using (var dialog = new Form { Text = "Remote CLI", ClientSize = new Size(420, field ? 196 : 158), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                                       MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Theme.Text(), BackColor = Theme.Bg, ForeColor = Theme.Ink, AutoScaleMode = AutoScaleMode.None, Icon = Icon }) {
            dialog.HandleCreated += (s, e) => Theme.DarkTitle(dialog.Handle);
            Place(dialog, Theme.Label(title, Theme.Ink, Theme.Bg, 11.5f, true), 24, 22, 372, 26);
            Place(dialog, Theme.Label(text, Theme.Muted, Theme.Bg), 24, 52, 372, 40);
            var box = new Field(); box.BackColor = Theme.Bg; box.Box.Text = value ?? "";
            if (field) Place(dialog, box, 24, 96, 372, 34);
            var ok = new RoundButton { Text = yes, Kind = danger ? ButtonKind.Danger : ButtonKind.Primary, DialogResult = DialogResult.OK };
            var cancel = new RoundButton { Text = "取消", DialogResult = DialogResult.Cancel };
            int y = field ? 146 : 108;
            Place(dialog, cancel, 196, y, 96, 34); Place(dialog, ok, 300, y, 96, 34);
            dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            using (var g = dialog.CreateGraphics()) { float factor = g.DpiX / 96f; if (factor > 1.01f) dialog.Scale(new SizeF(factor, factor)); }
            if (field) dialog.Shown += (s, e) => { box.Box.Focus(); box.Box.SelectAll(); };
            return dialog.ShowDialog(Visible ? this : null) == DialogResult.OK ? (field ? box.Box.Text : "yes") : null;
        }
    }
    bool Confirm(string title, string text, string yes, bool danger) { return Dialog(title, text, yes, danger, false, null) != null; }
    string Ask(string title, string text, string value) { return Dialog(title, text, "确定", false, true, value) ?? ""; }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] public static int Main(string[] args) {
        // --qr <text> <file.png>: writes the picture the window would show; used by the tests.
        if (args.Length == 3 && args[0] == "--qr") { using (var picture = Picture(args[1], 8)) { if (picture == null) return 2; picture.Save(args[2], ImageFormat.Png); } return 0; }
        bool created;
        // One copy per data folder: a copy pointed at another folder with REMOTECLI_DATA (a test, a second setup) may run beside it.
        string elsewhere = Environment.GetEnvironmentVariable("REMOTECLI_DATA");
        using (var single = new Mutex(true, "Local\\RemoteCliApp" + (String.IsNullOrWhiteSpace(elsewhere) ? "" : "-" + Math.Abs(elsewhere.ToLowerInvariant().GetHashCode())), out created)) {
            if (!created) { MessageBox.Show("Remote CLI 已经在运行，请看屏幕右下角的托盘图标。", "Remote CLI"); return 0; }
            ServicePointManager.DefaultConnectionLimit = 8;      // the held request and the reports run side by side
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--screenshot") ComputerName = "my-computer";       // a published picture must not carry this computer's name
            var window = new App();
            // --screenshot <file.png>: draws the window into a picture after a moment and exits; used for the documentation.
            if (args.Length >= 2 && args[0] == "--screenshot") {      // an optional third argument chooses the page (0 to 3)
                window.Show();
                if (args.Length == 3) { int page; if (Int32.TryParse(args[2], out page)) window.Go(page); }
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
