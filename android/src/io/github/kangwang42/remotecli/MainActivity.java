package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.speech.RecognizerIntent;
import android.text.InputType;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * The computers this phone knows (each with what is running on it right now), a screen to add one by scanning
 * its code or typing its address, and the pages served by the program on the chosen computer, shown full screen.
 */
public final class MainActivity extends Activity {
    private static final int SPEECH = 4103, CAMERA = 4104;
    /** Colours of the app's own screens; they follow the skin last chosen in the pages. */
    private int BG, PANEL, RAISED, LINE, INK, MUTED, ACCENT, ON_ACCENT, GOOD, BUSY, BAD;
    private void palette(boolean light) {
        BG = light ? Color.rgb(251, 251, 252) : Color.rgb(26, 27, 38); PANEL = light ? Color.rgb(240, 241, 244) : Color.rgb(34, 36, 54);
        RAISED = light ? Color.rgb(228, 230, 235) : Color.rgb(44, 47, 71); LINE = light ? Color.rgb(213, 216, 223) : Color.rgb(47, 51, 77);
        INK = light ? Color.rgb(43, 47, 58) : Color.rgb(192, 202, 245); MUTED = light ? Color.rgb(102, 107, 120) : Color.rgb(129, 137, 173);
        ACCENT = light ? Color.rgb(47, 111, 228) : Color.rgb(122, 162, 247); ON_ACCENT = light ? Color.WHITE : Color.rgb(16, 18, 28);
        GOOD = light ? Color.rgb(61, 138, 58) : Color.rgb(158, 206, 106); BUSY = light ? Color.rgb(163, 106, 0) : Color.rgb(224, 175, 104);
        BAD = light ? Color.rgb(201, 60, 55) : Color.rgb(247, 118, 142);
    }
    /** Colours the system bars and picks dark or light icons on them, so they stay readable on any skin. */
    private void bars(int shade) {
        boolean light = (Color.red(shade) * 299 + Color.green(shade) * 587 + Color.blue(shade) * 114) / 1000 > 150;
        getWindow().setStatusBarColor(shade); getWindow().setNavigationBarColor(shade);
        int flags = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        View decor = getWindow().getDecorView();
        decor.setSystemUiVisibility(light ? decor.getSystemUiVisibility() | flags : decor.getSystemUiVisibility() & ~flags);
        root.setBackgroundColor(shade);
        if (light != prefs.getBoolean("light", false)) prefs.edit().putBoolean("light", light).apply();
    }

    private SharedPreferences prefs;
    private FrameLayout root;
    private WebView web;
    private EditText nameBox, addressBox, passwordBox;
    private TextView message;
    private GithubUpdater updater;
    private Scanner scanner;
    private String screen = "";                 // "home", "add", "scan" or "web"
    private final Handler ticker = new Handler(Looper.getMainLooper());
    private final ExecutorService net = Executors.newFixedThreadPool(3);
    private final Map<String, String[]> glance = new HashMap<>();       // address -> {state, text} from the last look
    private final Map<String, TextView[]> glanceViews = new HashMap<>();
    private final Runnable look = this::lookAtAll;
    private final Runnable watch = this::checkConnection;
    private boolean foreground, checkingConnection;
    private String currentOrigin = "";
    private ConnectionHealth connectionHealth = new ConnectionHealth();
    private static final class ComputerSnapshot {
        final JSONObject computer, data;
        final String error;
        ComputerSnapshot(JSONObject computer, JSONObject data, String error) { this.computer = computer; this.data = data; this.error = error; }
    }

    // ---- the computers this phone knows: [{"name": ..., "url": ...}]
    private JSONArray computers() {
        try { return new JSONArray(prefs.getString("computers", "[]")); } catch (Exception broken) { return new JSONArray(); }
    }
    private void store(JSONArray list) { prefs.edit().putString("computers", list.toString()).apply(); }
    /** Adds a computer or updates it: the same address, or the same name with a new address (a tunnel that restarted). */
    private void remember(String name, String url) {
        try {
            JSONArray list = computers();
            int at = -1;
            for (int i = 0; i < list.length(); i++) if (url.equals(list.getJSONObject(i).optString("url"))) at = i;
            if (at < 0 && !name.isEmpty()) for (int i = 0; i < list.length(); i++) if (name.equals(list.getJSONObject(i).optString("name"))) at = i;
            String shown = !name.isEmpty() ? name : at >= 0 ? list.getJSONObject(at).optString("name") : host(url);
            JSONObject item = new JSONObject().put("name", shown).put("url", url);
            if (at >= 0) list.put(at, item); else list.put(item);
            store(list);
        } catch (Exception ignored) { }
    }
    private static String host(String url) { String h = Uri.parse(url).getHost(); return h == null ? url : h; }

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("remote-cli", MODE_PRIVATE);
        // An earlier version knew one computer only.
        String single = prefs.getString("server", "");
        if (!single.isEmpty() && computers().length() == 0) remember("", single);
        updater = new GithubUpdater(this, new GithubUpdater.Listener() {
            @Override public void found(GithubUpdater.Release release) { new AlertDialog.Builder(MainActivity.this).setTitle("发现 Remote CLI 新版本").setMessage("GitHub 上有 v" + release.version + "，现在下载并安装吗？").setPositiveButton("更新", (d, w) -> updater.install(release)).setNegativeButton("稍后", null).show(); }
            @Override public void message(String text) {
                if (text == null || text.isEmpty()) return;
                // Inside a computer's pages there is no line of the app's own to write on.
                if ("web".equals(screen) || message == null) android.widget.Toast.makeText(MainActivity.this, text, android.widget.Toast.LENGTH_LONG).show(); else { message.setTextColor(MUTED); message.setText(text); }
            }
        });
        palette(prefs.getBoolean("light", false));
        root = new FrameLayout(this);
        setContentView(root);
        bars(BG);
        if (!linked(getIntent())) {
            JSONArray list = computers();
            if (list.length() == 1) open(list.optJSONObject(0).optString("url"), ""); else home("");
        }
        updater.checkIfDue();
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); linked(intent); }

    /** remotecli://connect?u=address&p=password&n=name, from the code on the computer's screen. */
    private boolean linked(Intent intent) { return link(intent == null ? null : intent.getData()); }
    private boolean link(Uri link) {
        if (link == null || !"remotecli".equals(link.getScheme()) || !link.isHierarchical()) return false;
        String address = clean(link.getQueryParameter("u")), password = link.getQueryParameter("p"), name = link.getQueryParameter("n");
        if (address.isEmpty()) { home("二维码里的地址无效，请在电脑上重新显示后再扫。"); return true; }
        remember(name == null ? "" : name.trim(), address);
        open(address, password == null ? "" : password);
        return true;
    }

    /** "192.168.1.5:8722" and "https://x.example.com/" both become an origin; anything else is refused. */
    static String clean(String typed) {
        String text = typed == null ? "" : typed.trim();
        if (text.isEmpty()) return "";
        if (!text.contains("://")) text = "http://" + text;
        Uri uri = Uri.parse(text);
        String scheme = uri.getScheme(), host = uri.getHost();
        if (host == null || host.isEmpty() || !("http".equals(scheme) || "https".equals(scheme))) return "";
        return scheme + "://" + host + (uri.getPort() > 0 ? ":" + uri.getPort() : "");
    }

    // ---- small pieces the screens are built from
    private int dp(float value) { return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, getResources().getDisplayMetrics())); }
    private TextView text(String value, float size, int color) {
        TextView view = new TextView(this);
        view.setText(value); view.setTextSize(size); view.setTextColor(color); view.setLineSpacing(dp(4), 1f);
        return view;
    }
    private TextView bold(String value, float size, int color) { TextView view = text(value, size, color); view.setTypeface(Typeface.DEFAULT_BOLD); return view; }
    private GradientDrawable shape(int fill, int stroke, float radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill); drawable.setCornerRadius(dp(radius)); if (stroke != 0) drawable.setStroke(dp(1), stroke);
        return drawable;
    }
    private GradientDrawable shape(int fill, int stroke) { return shape(fill, stroke, 14); }
    private static int tint(int color, int alpha) { return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color)); }
    private EditText field(String hint, int type) {
        EditText box = new EditText(this);
        box.setHint(hint); box.setHintTextColor(MUTED); box.setTextColor(INK); box.setTextSize(16); box.setSingleLine(true);
        box.setInputType(type); box.setBackground(shape(PANEL, LINE)); box.setPadding(dp(16), 0, dp(16), 0); box.setMinHeight(dp(52));
        return box;
    }
    /** A button: 0 the main action, 1 an ordinary one, 2 only text. A press dims it for a moment. */
    private TextView button(String label, int kind, Runnable action) {
        TextView view = bold(label, 16, kind == 0 ? ON_ACCENT : kind == 1 ? INK : MUTED);
        view.setGravity(Gravity.CENTER); view.setMinHeight(dp(52)); view.setPadding(dp(18), 0, dp(18), 0);
        if (kind < 2) view.setBackground(shape(kind == 0 ? ACCENT : PANEL, kind == 0 ? 0 : LINE, 16));
        press(view, action);
        return view;
    }
    private void press(View view, Runnable action) {
        view.setClickable(true); view.setFocusable(true);
        view.setOnTouchListener((v, event) -> {
            int what = event.getActionMasked();
            if (what == android.view.MotionEvent.ACTION_DOWN) v.setAlpha(.6f);
            else if (what == android.view.MotionEvent.ACTION_UP || what == android.view.MotionEvent.ACTION_CANCEL) v.setAlpha(1f);
            return false;
        });
        view.setOnClickListener(v -> action.run());
    }
    private LinearLayout.LayoutParams below(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(top);
        return params;
    }
    private LinearLayout column(int top) {
        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setPadding(dp(20), dp(top), dp(20), dp(28));
        return column;
    }
    private void show(View content, String name) {
        ticker.removeCallbacks(look);
        ticker.removeCallbacks(watch);
        if (web != null) { root.removeView(web); web.destroy(); web = null; }
        root.removeAllViews();
        palette(prefs.getBoolean("light", false));
        bars(BG);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(content, new ViewGroup.LayoutParams(-1, -2));
        root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        screen = name;
    }
    /** The app's mark: a prompt on a rounded tile. */
    private View mark(int size) {
        TextView tile = bold(">_", size * 0.36f, ACCENT);
        tile.setTypeface(Typeface.create(Typeface.MONOSPACE, Typeface.BOLD)); tile.setGravity(Gravity.CENTER);
        GradientDrawable back = new GradientDrawable(GradientDrawable.Orientation.TL_BR, new int[]{RAISED, PANEL});
        back.setCornerRadius(dp(size * 0.28f)); back.setStroke(dp(1), LINE);
        tile.setBackground(back);
        tile.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return tile;
    }

    // ---- home: the computers, and what is going on on each
    private void home(String problem) {
        palette(prefs.getBoolean("light", false));
        LinearLayout column = column(40);
        LinearLayout head = new LinearLayout(this);
        head.setGravity(Gravity.CENTER_VERTICAL);
        head.addView(mark(46));
        LinearLayout titles = new LinearLayout(this);
        titles.setOrientation(LinearLayout.VERTICAL); titles.setPadding(dp(14), 0, 0, 0);
        JSONArray list = computers();
        titles.addView(bold("Remote CLI", 23, INK));
        titles.addView(text(list.length() == 0 ? "在手机上使用电脑里的终端" : list.length() + " 台电脑", 13.5f, MUTED));
        head.addView(titles);
        column.addView(head);
        message = text(problem, 14, BAD);
        if (!problem.isEmpty()) column.addView(message, below(16));
        glanceViews.clear();
        if (list.length() == 0) {
            LinearLayout card = column(18);
            card.setPadding(dp(18), dp(18), dp(18), dp(18)); card.setBackground(shape(PANEL, LINE, 20));
            card.addView(bold("连接第一台电脑", 17, INK));
            String[] steps = { "在电脑上安装并打开 Remote CLI", "点下面的“扫码添加电脑”，对准电脑窗口里的二维码", "连上后选一个项目，新建 Claude Code、Codex 或 PowerShell 终端" };
            for (int i = 0; i < steps.length; i++) {
                LinearLayout row = new LinearLayout(this);
                TextView number = bold(String.valueOf(i + 1), 12.5f, ACCENT);
                number.setGravity(Gravity.CENTER); number.setBackground(shape(tint(ACCENT, 40), 0, 12));
                row.addView(number, new LinearLayout.LayoutParams(dp(24), dp(24)));
                TextView step = text(steps[i], 14.5f, INK); step.setPadding(dp(12), dp(1), 0, 0);
                row.addView(step, new LinearLayout.LayoutParams(0, -2, 1));
                card.addView(row, below(14));
            }
            column.addView(card, below(24));
        } else {
            column.addView(bold("我的电脑", 13.5f, MUTED), below(26));
            for (int i = 0; i < list.length(); i++) column.addView(computerCard(list.optJSONObject(i), i), below(i == 0 ? 10 : 10));
            if (list.length() > 1) column.addView(button("聚合查看所有项目和对话", 1, this::aggregate), below(18));
        }
        column.addView(button(problem.contains("重新扫码") ? "重新扫码连接电脑" : "扫码添加电脑", 0, this::scan), below(24));
        column.addView(button("手动输入地址", 1, () -> add("")), below(10));
        if (problem.isEmpty()) column.addView(message, below(12));
        column.addView(text("地址和密码相当于电脑的钥匙，不要发给别人。长按一台电脑可以改名或移除。", 12.5f, MUTED), below(18));
        TextView version = text("版本 " + versionName() + " · 检查更新", 13, ACCENT);
        version.setGravity(Gravity.CENTER); version.setMinHeight(dp(44));
        press(version, () -> { message.setTextColor(MUTED); message.setText("正在检查新版本…"); updater.check(true); });
        column.addView(version, below(10));
        show(column, "home");
        lookAtAll();
    }
    private View computerCard(JSONObject computer, int index) {
        final String url = computer.optString("url"), name = computer.optString("name");
        LinearLayout card = new LinearLayout(this);
        card.setGravity(Gravity.CENTER_VERTICAL); card.setPadding(dp(16), dp(14), dp(12), dp(14)); card.setBackground(shape(PANEL, LINE, 20));
        View dot = new View(this);
        dot.setBackground(shape(MUTED, 0, 6));
        card.addView(dot, new LinearLayout.LayoutParams(dp(10), dp(10)));
        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL); texts.setPadding(dp(14), 0, dp(8), 0);
        TextView title = bold(name, 17, INK); title.setSingleLine(true); title.setEllipsize(TextUtils.TruncateAt.END);
        texts.addView(title);
        TextView where = text(host(url), 12.5f, MUTED); where.setSingleLine(true); where.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        texts.addView(where);
        TextView state = bold("正在查看…", 13, MUTED); state.setPadding(0, dp(6), 0, 0);
        texts.addView(state);
        card.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        card.addView(text("›", 26, MUTED));
        glanceViews.put(url, new TextView[]{state, title});
        dot.setTag(url); state.setTag(dot);
        String[] known = glance.get(url);
        if (known != null) paint(url, known[0], known[1]);
        press(card, () -> open(url, ""));
        card.setOnLongClickListener(v -> { manage(index, name); return true; });
        return card;
    }
    private void manage(int index, String name) {
        new AlertDialog.Builder(this).setTitle(name).setItems(new String[]{"改名", "从列表移除"}, (dialog, which) -> {
            if (which == 0) {
                final EditText box = new EditText(this);
                box.setText(name); box.setSingleLine(true); box.setSelection(name.length());
                new AlertDialog.Builder(this).setTitle("电脑名称").setView(box).setNegativeButton("取消", null).setPositiveButton("保存", (d, w) -> {
                    String wanted = box.getText().toString().trim();
                    if (wanted.isEmpty() || wanted.length() > 40) return;
                    try { JSONArray list = computers(); list.getJSONObject(index).put("name", wanted); store(list); } catch (Exception ignored) { }
                    home("");
                }).show();
            } else {
                new AlertDialog.Builder(this).setTitle("移除“" + name + "”？").setMessage("只是从这部手机的列表里移除，电脑上的终端和对话不受影响。")
                        .setNegativeButton("取消", null).setPositiveButton("移除", (d, w) -> { JSONArray list = computers(); list.remove(index); store(list); home(""); }).show();
            }
        }).show();
    }
    /** state: "ok", "busy", "need", "off" or "key". */
    private void paint(String url, String state, String line) {
        glance.put(url, new String[]{state, line});
        TextView[] views = glanceViews.get(url);
        if (views == null) return;
        int color = "need".equals(state) ? BAD : "busy".equals(state) ? BUSY : "ok".equals(state) ? GOOD : MUTED;
        views[0].setText(line); views[0].setTextColor("ok".equals(state) ? MUTED : color);
        Object dot = views[0].getTag();
        if (dot instanceof View) ((View) dot).setBackground(shape("off".equals(state) || "key".equals(state) ? MUTED : color, 0, 6));
    }
    /** Asks every computer what is running; each answers on its own time. Repeats while the home screen is shown. */
    private void lookAtAll() {
        ticker.removeCallbacks(look);
        if (!"home".equals(screen)) return;
        JSONArray list = computers();
        for (int i = 0; i < list.length(); i++) {
            final String url = list.optJSONObject(i).optString("url");
            final String cookie = CookieManager.getInstance().getCookie(url);
            net.execute(() -> {
                String state = "off", line = "连不上，可能没开机或地址变了";
                HttpURLConnection connection = null;
                try {
                    connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
                    connection.setConnectTimeout(4000); connection.setReadTimeout(6000);
                    if (cookie != null) connection.setRequestProperty("Cookie", cookie);
                    int code = connection.getResponseCode();
                    if (code == 401) { state = "key"; line = "需要重新扫码登录"; }
                    else if (code == 200) {
                        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                        try (InputStream input = connection.getInputStream()) { byte[] part = new byte[16384]; int n; while ((n = input.read(part)) > 0 && bytes.size() < 4000000) bytes.write(part, 0, n); }
                        JSONObject data = new JSONObject(bytes.toString("UTF-8")), device = data.optJSONObject("device");
                        JSONArray terminals = data.optJSONArray("terminals");
                        int confirm = 0, busy = 0, open = 0;
                        for (int k = 0; terminals != null && k < terminals.length(); k++) {
                            JSONObject t = terminals.getJSONObject(k);
                            if (!"running".equals(t.optString("state")) && !"starting".equals(t.optString("state"))) continue;
                            open++;
                            String phase = t.optString("phase", "busy".equals(t.optString("status")) ? "busy" : "idle");
                            if ("confirm".equals(phase)) confirm++; else if ("busy".equals(phase) || "starting".equals(phase)) busy++;
                        }
                        if (device == null || !device.optBoolean("online")) { state = "off"; line = "电脑端程序没有在运行"; }
                        else if (!device.optBoolean("enabled")) { state = "off"; line = "电脑端暂停了手机访问"; }
                        else if (confirm > 0) { state = "need"; line = confirm + " 个任务等你确认" + (busy > 0 ? " · " + busy + " 个在执行" : ""); }
                        else if (busy > 0) { state = "busy"; line = busy + " 个任务在执行" + (open > busy ? " · " + (open - busy) + " 个等待输入" : ""); }
                        else { state = "ok"; line = open > 0 ? "在线 · " + open + " 个终端等待输入" : "在线 · 没有任务在跑"; }
                    }
                } catch (Exception unreachable) { /* keeps "off" */ }
                finally { if (connection != null) connection.disconnect(); }
                final String s = state, l = line;
                runOnUiThread(() -> paint(url, s, l));
            });
        }
        ticker.postDelayed(look, 6000);
    }

    /** Fetches one computer's list for the all-computers view. It never changes the selected WebView. */
    private JSONObject terminalData(String url) throws Exception {
        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
            connection.setConnectTimeout(4000); connection.setReadTimeout(8000); connection.setUseCaches(false);
            String cookie = CookieManager.getInstance().getCookie(url);
            if (cookie != null) connection.setRequestProperty("Cookie", cookie);
            int code = connection.getResponseCode();
            if (code == 401) return new JSONObject().put("_error", "需要重新扫码登录");
            if (code != 200) return new JSONObject().put("_error", "电脑没有响应（" + code + "）");
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            try (InputStream input = connection.getInputStream()) {
                byte[] part = new byte[16384]; int n;
                while ((n = input.read(part)) > 0) {
                    if (bytes.size() + n > 4000000) throw new Exception("返回内容过大");
                    bytes.write(part, 0, n);
                }
            }
            return new JSONObject(bytes.toString("UTF-8"));
        } finally { if (connection != null) connection.disconnect(); }
    }

    /** Shows one comfortable, read-only list before the user enters a specific computer. */
    private void aggregate() {
        final JSONArray list = computers();
        if (list.length() < 2) return;
        LinearLayout loading = column(34);
        loading.addView(button("返回我的电脑", 2, () -> home("")));
        loading.addView(bold("全部项目和对话", 23, INK), below(18));
        loading.addView(text("正在读取每台电脑的项目和终端…", 14, MUTED), below(6));
        show(loading, "aggregate");
        net.execute(() -> {
            ArrayList<ComputerSnapshot> snapshots = new ArrayList<>();
            for (int i = 0; i < list.length(); i++) {
                JSONObject computer = list.optJSONObject(i); if (computer == null) continue;
                String url = computer.optString("url"), error = ""; JSONObject data = null;
                try { data = terminalData(url); if (data.has("_error")) { error = data.optString("_error"); data = null; } }
                catch (Exception unreachable) { error = "连不上，可能没开机或地址已变化"; }
                snapshots.add(new ComputerSnapshot(computer, data, error));
            }
            runOnUiThread(() -> { if ("aggregate".equals(screen)) aggregateView(snapshots); });
        });
    }

    private void aggregateView(ArrayList<ComputerSnapshot> snapshots) {
        LinearLayout column = column(34);
        column.addView(button("返回我的电脑", 2, () -> home("")));
        column.addView(bold("全部项目和对话", 23, INK), below(18));
        column.addView(text("按电脑分组显示；点项目即可进入对应电脑继续使用。", 14, MUTED), below(6));
        for (ComputerSnapshot snapshot : snapshots) column.addView(aggregateComputer(snapshot), below(16));
        column.addView(button("刷新", 1, this::aggregate), below(2));
        show(column, "aggregate");
    }

    private View aggregateComputer(ComputerSnapshot snapshot) {
        JSONObject computer = snapshot.computer;
        String url = computer.optString("url"), name = computer.optString("name");
        LinearLayout card = column(14); card.setPadding(dp(16), dp(16), dp(16), dp(16)); card.setBackground(shape(PANEL, LINE, 20));
        card.addView(bold(name.isEmpty() ? host(url) : name, 17, INK));
        card.addView(text(host(url), 12.5f, MUTED), below(2));
        if (snapshot.data == null) { card.addView(text(snapshot.error, 14, BAD), below(12)); return card; }
        JSONObject device = snapshot.data.optJSONObject("device");
        if (device == null || !device.optBoolean("online")) { card.addView(text("电脑端程序没有在运行", 14, MUTED), below(12)); return card; }
        if (!device.optBoolean("enabled")) { card.addView(text("电脑端暂停了手机访问", 14, MUTED), below(12)); return card; }
        JSONArray projects = device.optJSONArray("projects"), terminals = snapshot.data.optJSONArray("terminals"), sessions = snapshot.data.optJSONArray("sessions");
        ArrayList<String> names = new ArrayList<>();
        for (int i = 0; projects != null && i < projects.length(); i++) { String project = projects.optJSONObject(i) == null ? "" : projects.optJSONObject(i).optString("name"); if (!project.isEmpty()) names.add(project); }
        for (int i = 0; sessions != null && i < sessions.length(); i++) { JSONObject session = sessions.optJSONObject(i); String project = session == null ? "" : session.optString("dir"); if (!project.isEmpty() && !names.contains(project)) names.add(project); }
        for (String project : names) {
            int open = 0, saved = 0;
            for (int i = 0; terminals != null && i < terminals.length(); i++) { JSONObject terminal = terminals.optJSONObject(i); if (terminal != null && project.equals(terminal.optString("dir")) && ("running".equals(terminal.optString("state")) || "starting".equals(terminal.optString("state")))) open++; }
            for (int i = 0; sessions != null && i < sessions.length(); i++) { JSONObject session = sessions.optJSONObject(i); if (session != null && project.equals(session.optString("dir"))) saved++; }
            LinearLayout projectCard = column(8); projectCard.setPadding(dp(12), dp(10), dp(12), dp(10)); projectCard.setBackground(shape(RAISED, 0, 14));
            projectCard.addView(bold(project, 15, INK));
            projectCard.addView(text((open > 0 ? open + " 个终端" : "没有运行中的终端") + (saved > 0 ? " · " + saved + " 段对话" : ""), 12.5f, MUTED), below(1));
            for (int i = 0; terminals != null && i < terminals.length(); i++) {
                JSONObject terminal = terminals.optJSONObject(i);
                if (terminal == null || !project.equals(terminal.optString("dir")) || !("running".equals(terminal.optString("state")) || "starting".equals(terminal.optString("state")))) continue;
                String title = terminal.optString("title"); if (title.isEmpty()) title = terminal.optString("tool");
                String phase = terminal.optString("phase", "busy".equals(terminal.optString("status")) ? "正在执行" : "等待输入");
                projectCard.addView(text("终端 · " + title + " · " + phase, 13, INK), below(6));
            }
            int shown = 0;
            for (int i = 0; sessions != null && i < sessions.length() && shown < 30; i++) {
                JSONObject session = sessions.optJSONObject(i); if (session == null || !project.equals(session.optString("dir"))) continue;
                String title = session.optString("title"); if (title.isEmpty()) title = session.optString("tool");
                String state = session.optBoolean("live") ? ("busy".equals(session.optString("status")) ? "运行中" : "已占用") : "历史";
                projectCard.addView(text("对话 · " + title + " · " + state, 13, MUTED), below(5)); shown++;
            }
            if (saved > shown) projectCard.addView(text("还有 " + (saved - shown) + " 段对话，进入项目查看", 12.5f, MUTED), below(5));
            final String target = project;
            press(projectCard, () -> open(url, "", target));
            card.addView(projectCard, below(10));
        }
        if (names.isEmpty()) card.addView(text("还没有项目或保存的对话", 14, MUTED), below(12));
        return card;
    }

    private String versionName() { try { return getPackageManager().getPackageInfo(getPackageName(), 0).versionName; } catch (Exception unknown) { return ""; } }

    // ---- adding a computer by typing
    private void add(String problem) {
        LinearLayout column = column(40);
        column.addView(bold("手动添加电脑", 23, INK));
        column.addView(text("填电脑上 Remote CLI 窗口里显示的地址和密码。", 14.5f, MUTED), below(6));
        nameBox = field("名称（可不填），例如 办公室电脑", InputType.TYPE_CLASS_TEXT);
        column.addView(nameBox, below(22));
        addressBox = field("地址，例如 192.168.1.5:8722", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        column.addView(addressBox, below(10));
        passwordBox = field("访问密码", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        passwordBox.setImeOptions(EditorInfo.IME_ACTION_GO);
        passwordBox.setOnEditorActionListener((view, action, event) -> { connect(); return true; });
        column.addView(passwordBox, below(10));
        message = text(problem, 14, BAD);
        column.addView(message, below(12));
        column.addView(button("连接", 0, this::connect), below(8));
        column.addView(button("返回", 2, () -> home("")), below(6));
        column.addView(text("在公共网络下请用电脑端的“公网隧道”或自己的 https 中转；以 http:// 开头的地址只适合家里或办公室的 Wi-Fi。", 12.5f, MUTED), below(14));
        show(column, "add");
    }
    private void connect() {
        String address = clean(addressBox.getText().toString());
        if (address.isEmpty()) { message.setText("地址无效。例如 192.168.1.5:8722，或 https:// 开头的地址。"); return; }
        remember(nameBox.getText().toString().trim(), address);
        open(address, passwordBox.getText().toString().trim());
    }

    // ---- scanning the code inside the app
    private void scan() {
        if (checkSelfPermission(android.Manifest.permission.CAMERA) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{android.Manifest.permission.CAMERA}, CAMERA);
            return;
        }
        LinearLayout column = column(40);
        column.setGravity(Gravity.CENTER_HORIZONTAL);
        column.addView(bold("扫码添加电脑", 23, INK), new LinearLayout.LayoutParams(-1, -2));
        column.addView(text("对准电脑上 Remote CLI 窗口里的二维码。", 14.5f, MUTED), below(6));
        final int side = Math.min(getResources().getDisplayMetrics().widthPixels - dp(40), dp(380));
        FrameLayout frame = new FrameLayout(this);
        frame.setBackground(shape(Color.BLACK, LINE, 24));
        frame.setClipToOutline(true);
        LinearLayout.LayoutParams square = new LinearLayout.LayoutParams(side, side);
        square.topMargin = dp(24);
        column.addView(frame, square);
        // four corners that mark where the code should be
        FrameLayout aim = new FrameLayout(this);
        int[] gravities = { Gravity.TOP | Gravity.START, Gravity.TOP | Gravity.END, Gravity.BOTTOM | Gravity.START, Gravity.BOTTOM | Gravity.END };
        for (int gravity : gravities) for (int part = 0; part < 2; part++) {
            View bar = new View(this);
            bar.setBackground(shape(ACCENT, 0, 2));
            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(part == 0 ? dp(34) : dp(4), part == 0 ? dp(4) : dp(34), gravity);
            params.setMargins(dp(40), dp(40), dp(40), dp(40));
            aim.addView(bar, params);
        }
        column.addView(button("取消", 1, () -> { endScan(); home(""); }), below(24));
        column.addView(text("画面只在手机上用来找二维码，不保存也不上传。", 12.5f, MUTED), below(14));
        show(column, "scan");
        scanner = new Scanner(this, frame, value -> {
            scanner = null;
            if (!link(Uri.parse(value))) home("这不是 Remote CLI 的二维码。请扫电脑上 Remote CLI 窗口里的那一个。");
        }, problem -> { scanner = null; add(problem + "。可以手动输入地址和密码。"); });
        frame.post(() -> { if (scanner != null) { scanner.start(); frame.addView(aim, new FrameLayout.LayoutParams(-1, -1)); } });
    }
    private void endScan() { if (scanner != null) { scanner.stop(); scanner = null; } }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request != CAMERA) return;
        if (results.length > 0 && results[0] == android.content.pm.PackageManager.PERMISSION_GRANTED) scan();
        else add("没有相机权限，不能扫码。可以在系统设置里允许，或在这里手动输入");
    }

    /** Shows the pages of the computer at this address; a password signs in first. */
    private void open(String address, String password) {
        open(address, password, "");
    }
    private void open(String address, String password, String project) {
        ticker.removeCallbacks(look);
        ticker.removeCallbacks(watch);
        currentOrigin = address;
        connectionHealth = new ConnectionHealth();
        prefs.edit().putString("server", address).apply();
        if (web != null) { root.removeView(web); web.destroy(); }
        root.removeAllViews();
        screen = "web";
        web = new WebView(this);
        web.setBackgroundColor(BG);
        bars(BG);
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setTextZoom(100); settings.setSupportZoom(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        CookieManager.getInstance().setAcceptCookie(true);
        final String origin = address;
        web.addJavascriptInterface(new Bridge(), "RemoteCliNative");
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return !request.getUrl().toString().startsWith(origin + "/");       // the pages of this computer only
            }
            @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) reconnect(view, origin);
            }
            @Override public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
                String url = request.getUrl().toString();
                boolean relayRequest = url.startsWith(origin + "/api/");
                if (ConnectionHealth.httpError(request.isForMainFrame(), relayRequest, response.getStatusCode())) reconnect(view, origin);
            }
        });
        root.addView(web, new FrameLayout.LayoutParams(-1, -1));
        String target = address + "/" + (project.isEmpty() ? "" : "?project=" + Uri.encode(project));
        if (!password.isEmpty()) target += "#p=" + Uri.encode(password);
        web.loadUrl(target);
        if (foreground) ticker.post(watch);
    }

    /** Runs independently of the page, including while a terminal is open or its WebSocket has stopped. */
    private void checkConnection() {
        ticker.removeCallbacks(watch);
        if (!foreground || web == null || !"web".equals(screen)) return;
        if (checkingConnection) { ticker.postDelayed(watch, 1000); return; }
        final WebView checked = web;
        final String origin = currentOrigin;
        final ConnectionHealth health = connectionHealth;
        checkingConnection = true;
        net.execute(() -> {
            boolean reachable = false;
            int status = 0;
            HttpURLConnection connection = null;
            try {
                connection = (HttpURLConnection) new URL(origin + "/api/session").openConnection();
                connection.setConnectTimeout(3000); connection.setReadTimeout(3000);
                connection.setInstanceFollowRedirects(false); connection.setUseCaches(false);
                status = connection.getResponseCode();
                if (status == 200) {
                    ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                    try (InputStream input = connection.getInputStream()) {
                        byte[] part = new byte[1024]; int n;
                        while ((n = input.read(part)) > 0) {
                            if (bytes.size() + n > 16384) throw new java.io.IOException("Unexpected session response");
                            bytes.write(part, 0, n);
                        }
                    }
                    JSONObject session = new JSONObject(bytes.toString("UTF-8"));
                    reachable = session.opt("signed_in") instanceof Boolean;
                }
            } catch (Exception unreachable) { /* A second failed check returns to pairing. */ }
            finally { if (connection != null) connection.disconnect(); }
            final boolean ok = reachable;
            final int code = status;
            runOnUiThread(() -> {
                checkingConnection = false;
                if (!foreground || web != checked || !"web".equals(screen)) return;
                if (health.sample(ok, code)) reconnect(checked, origin);
                else ticker.postDelayed(watch, 3000);
            });
        });
    }

    private void reconnect(WebView failed, String origin) {
        root.post(() -> {
            // A late error from a destroyed page must not close a newly scanned connection.
            if (web != failed || !"web".equals(screen)) return;
            failed.stopLoading();
            home("电脑连接已断开。请确认电脑上的 Remote CLI 已打开，然后重新扫码连接；局域网直连时请检查是否在同一个 Wi-Fi。");
            paint(origin, "off", "连接已断开，请重新扫码");
        });
    }

    private final class Bridge {
        /** The system's speech recognizer; what was said goes into the page's message box. */
        @JavascriptInterface public void voice() { runOnUiThread(MainActivity.this::dictate); }
        /** The page tells the colour of its skin so the bars around it match. */
        @JavascriptInterface public void chrome(String color) {
            if (color == null || !color.matches("#[0-9a-fA-F]{6}")) return;
            final int shade = Color.parseColor(color);
            runOnUiThread(() -> { if (web == null) return; bars(shade); web.setBackgroundColor(shade); });
        }
        /** Looks for a newer version of the app now; the answer comes as a short notice or a question. */
        @JavascriptInterface public void update() { runOnUiThread(() -> { android.widget.Toast.makeText(MainActivity.this, "正在检查新版本…", android.widget.Toast.LENGTH_SHORT).show(); updater.check(true); }); }
        @JavascriptInterface public String version() { return versionName(); }
        /** Back to the list of computers. */
        @JavascriptInterface public void disconnect() { runOnUiThread(() -> home("")); }
    }

    private void dictate() {
        Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                .putExtra(RecognizerIntent.EXTRA_PROMPT, "说出要输入的内容");
        try { startActivityForResult(intent, SPEECH); }
        catch (Exception missing) { say("这台手机没有系统语音识别，请用输入法的语音键"); }
    }
    private void say(String text) { if (web != null) web.evaluateJavascript("window.TerminalUI&&TerminalUI.error(" + JSONObject.quote(text) + ")", null); }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != SPEECH || result != RESULT_OK || data == null || web == null) return;
        ArrayList<String> heard = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
        if (heard != null && !heard.isEmpty()) web.evaluateJavascript("window.RemoteCliDictated&&RemoteCliDictated(" + JSONObject.quote(heard.get(0)) + ")", null);
    }

    @Override public void onBackPressed() {
        if (scanner != null) { endScan(); home(""); return; }
        if ("add".equals(screen)) { home(""); return; }
        if ("aggregate".equals(screen)) { home(""); return; }
        if (web != null && web.canGoBack()) { web.goBack(); return; }
        // From a computer's first page, back leads to the list when there is more than one to choose from.
        if (web != null && computers().length() > 1) { home(""); return; }
        super.onBackPressed();
    }
    @Override protected void onPause() { super.onPause(); foreground = false; ticker.removeCallbacks(look); ticker.removeCallbacks(watch); CookieManager.getInstance().flush(); if (web != null) web.onPause(); if (scanner != null) { endScan(); home(""); } }
    @Override protected void onResume() { super.onResume(); foreground = true; if (web != null) { web.onResume(); ticker.post(watch); } if ("home".equals(screen)) lookAtAll(); if (updater != null) updater.checkIfDue(); }
    @Override protected void onDestroy() { foreground = false; ticker.removeCallbacks(look); ticker.removeCallbacks(watch); net.shutdownNow(); if (updater != null) updater.stop(); if (web != null) { web.destroy(); web = null; } super.onDestroy(); }
}
