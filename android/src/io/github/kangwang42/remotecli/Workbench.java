package io.github.kangwang42.remotecli;

import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.webkit.CookieManager;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * The app's first screen. On top, the running tasks of every computer with the ones that need the user first; below,
 * each computer with its projects. Every computer is asked on its own, so one that is switched off delays nothing.
 */
final class Workbench {
    /** What one computer answered last: "on", "off" or "key" (sign in again), a line about it, and its projects. */
    private static final class Snapshot {
        final String state, line;
        final List<AggregateSessions.Project> projects;
        Snapshot(String state, String line, List<AggregateSessions.Project> projects) { this.state = state; this.line = line; this.projects = projects; }
    }
    private static final class Task {
        final String url, computer, kind;
        final AggregateSessions.Entry entry;
        Task(String url, String computer, AggregateSessions.Entry entry, String kind) { this.url = url; this.computer = computer; this.entry = entry; this.kind = kind; }
    }
    private static final int SHOWN = 4, TASKS = 12, TALKS = 8;

    private final MainActivity activity;
    private final Kit kit;
    private final Map<String, Snapshot> snapshots = new HashMap<>();
    private final Map<String, LinearLayout> holders = new HashMap<>();
    private final Map<String, String> marks = new HashMap<>();
    private final Set<String> pending = new HashSet<>(), expanded = new HashSet<>(), unfolded = new HashSet<>();      // unfolded: address, line break, project
    private final Runnable tick = this::refresh;
    private JSONObject seen;
    private LinearLayout attention;
    private TextView summary;
    private String attentionMark;

    Workbench(MainActivity activity, Kit kit) { this.activity = activity; this.kit = kit; }

    // ---- which finished work the user has already looked at: {address: {terminal: phase change}}
    private JSONObject seenAll() {
        if (seen == null) try { seen = new JSONObject(activity.prefs.getString("seen", "{}")); } catch (Exception broken) { seen = new JSONObject(); }
        return seen;
    }
    private Map<String, Long> seen(String url) {
        Map<String, Long> map = new HashMap<>();
        JSONObject one = seenAll().optJSONObject(url);
        if (one != null) for (java.util.Iterator<String> keys = one.keys(); keys.hasNext(); ) { String key = keys.next(); map.put(key, one.optLong(key)); }
        return map;
    }
    /** Called when a terminal is opened here, and by the terminal page while it is on screen. */
    void markSeen(String url, String id, long at) {
        if (at <= 0 || id == null || !id.matches("[a-f0-9]{16,32}")) return;
        try {
            JSONObject one = seenAll().optJSONObject(url);
            if (one == null) seenAll().put(url, one = new JSONObject());
            if (one.optLong(id) == at) return;
            one.put(id, at);
            activity.prefs.edit().putString("seen", seenAll().toString()).apply();
        } catch (Exception ignored) { }
    }
    /** Forgets terminals that no longer exist, and computers that were removed. */
    private void prune(String url, Set<String> alive) {
        JSONObject one = seenAll().optJSONObject(url);
        if (one == null) return;
        List<String> gone = new ArrayList<>();
        for (java.util.Iterator<String> keys = one.keys(); keys.hasNext(); ) { String key = keys.next(); if (!alive.contains(key)) gone.add(key); }
        if (gone.isEmpty()) return;
        for (String key : gone) one.remove(key);
        activity.prefs.edit().putString("seen", seenAll().toString()).apply();
    }
    void forget(String url) {
        snapshots.remove(url); expanded.remove(url);
        if (seenAll().remove(url) != null) activity.prefs.edit().putString("seen", seenAll().toString()).apply();
    }

    // ---- the screen
    View build(String problem) {
        JSONArray list = activity.computers();
        LinearLayout page = kit.page(22);
        LinearLayout head = kit.row();
        head.addView(mark(42));
        LinearLayout titles = kit.column();
        titles.setPadding(kit.dp(12), 0, kit.dp(6), 0);
        titles.addView(kit.line("Remote CLI", 21, kit.INK, true));
        summary = kit.line(list.length() == 0 ? "在手机上使用电脑里的终端" : "正在连接…", 13, kit.MUTED, false);
        titles.addView(summary);
        head.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
        if (list.length() > 0) head.addView(kit.iconButton(R.drawable.ic_plus, "添加电脑", this::addComputer));
        page.addView(head);
        if (!problem.isEmpty()) {
            LinearLayout banner = kit.column();
            banner.setPadding(kit.dp(16), kit.dp(14), kit.dp(16), kit.dp(14));
            banner.setBackground(kit.shape(Kit.mix(kit.BAD, kit.PANEL, .09f), Kit.mix(kit.BAD, kit.LINE, .45f), 16));
            banner.addView(kit.text(problem, 14, kit.INK));
            page.addView(banner, kit.below(16));
        }
        holders.clear(); marks.clear(); attentionMark = null;
        attention = kit.column();
        page.addView(attention);
        if (list.length() == 0) {
            page.addView(welcome(), kit.below(22));
            page.addView(kit.button("扫码添加电脑", 0, activity::scan), kit.below(22));
            page.addView(kit.button("手动输入地址", 1, () -> activity.add("")), kit.below(10));
        } else {
            page.addView(heading("电脑", list.length() + " 台"), kit.below(24));
            for (int i = 0; i < list.length(); i++) {
                JSONObject computer = list.optJSONObject(i);
                if (computer == null) continue;
                LinearLayout holder = kit.column();
                holders.put(computer.optString("url"), holder);
                page.addView(holder, kit.below(10));
                drawComputer(computer);
            }
            TextView more = kit.bold("添加电脑", 15, kit.MUTED);
            more.setGravity(Gravity.CENTER); more.setMinHeight(kit.dp(54));
            GradientDrawable dashed = kit.shape(0, 0, 16);
            dashed.setStroke(kit.dp(1), kit.LINE, kit.dp(5), kit.dp(4));
            more.setBackground(dashed);
            kit.press(more, this::addComputer);
            page.addView(more, kit.below(10));
            if (!problem.isEmpty()) page.addView(kit.button("重新扫码连接电脑", 0, activity::scan), kit.below(16));
            draw();
        }
        page.addView(kit.text("地址和密码相当于电脑的钥匙，不要发给别人。", 12.5f, kit.MUTED), kit.below(22));
        TextView version = kit.text("版本 " + activity.versionName() + " · 检查更新", 13, kit.ACCENT);
        version.setMinHeight(kit.dp(44)); version.setGravity(Gravity.CENTER_VERTICAL);
        kit.press(version, activity::checkUpdate);
        page.addView(version);
        activity.message = kit.text("", 13, kit.MUTED);
        page.addView(activity.message);
        return page;
    }
    /** The app's mark: a prompt on a rounded tile. */
    private View mark(int size) {
        TextView tile = kit.bold(">_", size * .36f, kit.ACCENT);
        tile.setTypeface(Typeface.create(Typeface.MONOSPACE, Typeface.BOLD)); tile.setGravity(Gravity.CENTER);
        GradientDrawable back = new GradientDrawable(GradientDrawable.Orientation.TL_BR, new int[]{kit.RAISED, kit.PANEL});
        back.setCornerRadius(kit.dp(size * .3f)); back.setStroke(kit.dp(1), kit.LINE);
        tile.setBackground(back);
        tile.setLayoutParams(new LinearLayout.LayoutParams(kit.dp(size), kit.dp(size)));
        return tile;
    }
    private View welcome() {
        LinearLayout card = kit.column();
        card.setPadding(kit.dp(18), kit.dp(18), kit.dp(18), kit.dp(20)); card.setBackground(kit.shape(kit.PANEL, kit.LINE, 20));
        card.addView(kit.bold("连接第一台电脑", 17, kit.INK));
        String[] steps = { "在电脑上安装并打开 Remote CLI", "点下面的“扫码添加电脑”，对准电脑窗口里的二维码", "连上后选一个项目，新建 Claude Code、Codex 或 PowerShell 终端" };
        for (int i = 0; i < steps.length; i++) {
            LinearLayout row = new LinearLayout(activity);
            TextView number = kit.bold(String.valueOf(i + 1), 12.5f, kit.ACCENT);
            number.setGravity(Gravity.CENTER); number.setBackground(kit.shape(Kit.tint(kit.ACCENT, 40), 0, 12));
            row.addView(number, new LinearLayout.LayoutParams(kit.dp(24), kit.dp(24)));
            TextView step = kit.text(steps[i], 14.5f, kit.INK);
            step.setPadding(kit.dp(12), kit.dp(1), 0, 0);
            row.addView(step, new LinearLayout.LayoutParams(0, -2, 1));
            card.addView(row, kit.below(14));
        }
        return card;
    }
    private View heading(String title, String note) {
        LinearLayout row = kit.row();
        row.setPadding(kit.dp(2), 0, kit.dp(2), 0);
        row.addView(kit.bold(title, 14.5f, kit.INK), new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(kit.text(note, 12.5f, kit.MUTED));
        return row;
    }
    private void addComputer() {
        kit.sheet("添加电脑", "", new String[]{"扫码添加", "手动输入地址"}, new String[]{"对准电脑端窗口里的二维码", "填写电脑端窗口里显示的地址和密码"}, -1,
            which -> { if (which == 0) activity.scan(); else activity.add(""); });
    }

    // ---- asking the computers
    /** Asks every computer what is running; repeats while this screen is shown. */
    void refresh() {
        activity.ticker.removeCallbacks(tick);
        if (!activity.showing("home")) return;
        JSONArray list = activity.computers();
        for (int i = 0; i < list.length(); i++) {
            final JSONObject computer = list.optJSONObject(i);
            if (computer == null) continue;
            final String url = computer.optString("url");
            if (!pending.add(url)) continue;
            final String cookie = CookieManager.getInstance().getCookie(url);
            activity.net.execute(() -> {
                String state = "off", line = "连不上，可能没开机或地址变了";
                JSONObject data = null;
                HttpURLConnection connection = null;
                try {
                    connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
                    connection.setConnectTimeout(4000); connection.setReadTimeout(8000); connection.setUseCaches(false);
                    connection.setInstanceFollowRedirects(false);
                    if (cookie != null) connection.setRequestProperty("Cookie", cookie);
                    int code = connection.getResponseCode();
                    if (code == 401) { state = "key"; line = "需要重新扫码登录"; }
                    else if (code != 200) line = "电脑没有响应（" + code + "）";
                    else {
                        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                        try (InputStream input = connection.getInputStream()) {
                            byte[] part = new byte[16384]; int n;
                            while ((n = input.read(part)) > 0) {
                                if (bytes.size() + n > 4000000) throw new Exception("返回内容过大");
                                bytes.write(part, 0, n);
                            }
                        }
                        data = new JSONObject(bytes.toString("UTF-8"));
                    }
                } catch (Exception unreachable) { /* keeps "off" */ }
                finally { if (connection != null) connection.disconnect(); }
                final JSONObject answer = data;
                final String s = state, l = line;
                activity.runOnUiThread(() -> {
                    pending.remove(url);
                    snapshots.put(url, answer == null ? new Snapshot(s, l, Collections.<AggregateSessions.Project>emptyList()) : read(url, answer));
                    if (!activity.showing("home")) return;
                    drawComputer(computer); draw();
                });
            });
        }
        activity.ticker.postDelayed(tick, 6000);
    }
    void stop() { activity.ticker.removeCallbacks(tick); }

    private Snapshot read(String url, JSONObject data) {
        JSONObject device = data.optJSONObject("device");
        List<AggregateSessions.Project> none = Collections.emptyList();
        if (device == null || !device.optBoolean("online")) return new Snapshot("off", "电脑端程序没有在运行", none);
        if (!device.optBoolean("enabled")) return new Snapshot("off", "电脑端暂停了手机访问", none);
        ArrayList<String> names = new ArrayList<>();
        ArrayList<AggregateSessions.Entry> entries = new ArrayList<>();
        Set<String> alive = new HashSet<>();
        JSONArray folders = device.optJSONArray("workspaces");
        for (int i = 0; folders != null && i < folders.length(); i++) names.add(folders.optString(i));
        JSONArray projects = device.optJSONArray("projects");
        for (int i = 0; projects != null && i < projects.length(); i++) {
            JSONObject project = projects.optJSONObject(i);
            if (project != null) names.add(project.optString("name"));
        }
        for (boolean terminal : new boolean[]{true, false}) {
            JSONArray source = data.optJSONArray(terminal ? "terminals" : "sessions");
            for (int i = 0; source != null && i < source.length(); i++) {
                JSONObject entry = source.optJSONObject(i);
                if (entry == null) continue;
                if (terminal) alive.add(entry.optString("id"));
                entries.add(new AggregateSessions.Entry(terminal, entry.optString("id"), entry.optString("dir"),
                    entry.optString("title"), entry.optString("tool"), entry.optString("state"),
                    entry.optString("phase"), entry.optString("status"), entry.optBoolean("live"),
                    entry.optString("host"), entry.optString("terminal"), entry.optString("session"),
                    entry.optBoolean("done"), entry.optLong("phase_at")));
            }
        }
        prune(url, alive);
        List<AggregateSessions.Project> groups = AggregateSessions.projects(names, entries);
        int[] counts = AggregateSessions.counts(groups, seen(url));
        List<String> parts = new ArrayList<>();
        if (counts[0] > 0) parts.add(counts[0] + " 个等你确认");
        if (counts[1] > 0) parts.add(counts[1] + " 个已完成");
        if (counts[2] > 0) parts.add(counts[2] + " 个在执行");
        String line = !parts.isEmpty() ? android.text.TextUtils.join(" · ", parts) : counts[3] > 0 ? "在线 · " + counts[3] + " 个终端等待输入" : "在线 · 没有任务在运行";
        return new Snapshot("on", line, groups);
    }

    // ---- drawing; a part is rebuilt only when what it shows has changed, so a press or a scroll is not interrupted
    private int color(String kind) {
        return "confirm".equals(kind) ? kit.BAD : "done".equals(kind) ? kit.GOOD : "busy".equals(kind) || "pc-busy".equals(kind) ? kit.BUSY
            : "starting".equals(kind) || "pc-idle".equals(kind) ? kit.ACCENT : kit.MUTED;
    }
    private int toolIcon(String tool) { return "claude".equals(tool) ? R.drawable.ic_claude : "codex".equals(tool) ? R.drawable.ic_codex : R.drawable.ic_shell; }
    private int toolColor(String tool) { return "claude".equals(tool) ? 0xffd98a5f : "codex".equals(tool) ? kit.ACCENT : kit.GOOD; }

    /** The summary under the title and the tasks of all computers. */
    private void draw() {
        if (attention == null) return;
        JSONArray list = activity.computers();
        List<Task> tasks = new ArrayList<>();
        int online = 0, known = 0;
        int[] total = new int[4];
        StringBuilder mark = new StringBuilder();
        for (int i = 0; i < list.length(); i++) {
            JSONObject computer = list.optJSONObject(i);
            if (computer == null) continue;
            String url = computer.optString("url"), name = computer.optString("name");
            Snapshot snapshot = snapshots.get(url);
            if (snapshot == null) continue;
            known++;
            if (!"on".equals(snapshot.state)) continue;
            online++;
            Map<String, Long> looked = seen(url);
            int[] counts = AggregateSessions.counts(snapshot.projects, looked);
            for (int k = 0; k < 4; k++) total[k] += counts[k];
            for (AggregateSessions.Project project : snapshot.projects) for (AggregateSessions.Entry entry : project.active) tasks.add(new Task(url, name, entry, entry.kind(looked)));
        }
        if (known > 0) {
            List<String> parts = new ArrayList<>();
            parts.add(list.length() == 1 ? (online == 1 ? "电脑在线" : "电脑离线") : online + " / " + list.length() + " 台在线");
            if (total[0] > 0) parts.add(total[0] + " 个等你确认");
            if (total[1] > 0) parts.add(total[1] + " 个已完成");
            if (total[2] > 0) parts.add(total[2] + " 个在执行");
            summary.setText(android.text.TextUtils.join(" · ", parts));
            summary.setTextColor(total[0] > 0 ? kit.BAD : kit.MUTED);
        }
        Collections.sort(tasks, (a, b) -> AggregateSessions.rank(a.kind) != AggregateSessions.rank(b.kind) ? AggregateSessions.rank(a.kind) - AggregateSessions.rank(b.kind) : Long.compare(b.entry.at, a.entry.at));
        for (Task task : tasks) mark.append(task.url).append('\n').append(task.entry.id).append('\n').append(task.kind).append('\n').append(task.entry.shownTitle()).append('\n').append(task.computer).append('\n');
        if (mark.toString().equals(attentionMark)) return;
        attentionMark = mark.toString();
        attention.removeAllViews();
        if (tasks.isEmpty()) return;
        List<String> parts = new ArrayList<>();
        if (total[0] > 0) parts.add(total[0] + " 个等你确认");
        if (total[1] > 0) parts.add(total[1] + " 个已完成");
        if (total[2] > 0) parts.add(total[2] + " 个在执行");
        attention.addView(heading("进行中", parts.isEmpty() ? tasks.size() + " 个终端在运行" : android.text.TextUtils.join(" · ", parts)), kit.below(24));
        boolean several = list.length() > 1;
        for (int i = 0; i < Math.min(TASKS, tasks.size()); i++) attention.addView(taskCard(tasks.get(i), several), kit.below(i == 0 ? 10 : 8));
        if (tasks.size() > TASKS) attention.addView(kit.text("另外 " + (tasks.size() - TASKS) + " 个在各自的项目里。", 12.5f, kit.MUTED), kit.below(8));
    }
    private View taskCard(Task task, boolean several) {
        final AggregateSessions.Entry entry = task.entry;
        LinearLayout card = kit.row();
        card.setMinimumHeight(kit.dp(66)); card.setPadding(kit.dp(14), kit.dp(11), kit.dp(14), kit.dp(11));
        int edge = "confirm".equals(task.kind) ? Kit.mix(kit.BAD, kit.LINE, .55f) : "done".equals(task.kind) ? Kit.mix(kit.GOOD, kit.LINE, .45f) : kit.LINE;
        card.setBackground(kit.shape(kit.PANEL, edge, 16));
        card.addView(kit.tile(toolIcon(entry.tool), toolColor(entry.tool), 40));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(12), 0, 0, 0);
        texts.addView(kit.line(entry.shownTitle(), 15.5f, kit.INK, true));
        LinearLayout meta = kit.row();
        meta.addView(kit.pill(AggregateSessions.label(task.kind), color(task.kind)));
        TextView where = kit.line(entry.project + (several ? " · " + task.computer : ""), 12.5f, kit.MUTED, false);
        where.setPadding(kit.dp(8), 0, 0, 0);
        meta.addView(where, new LinearLayout.LayoutParams(0, -2, 1));
        texts.addView(meta, kit.below(4));
        card.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        kit.press(card, () -> {
            if (!entry.terminal) { activity.openSession(task.url, entry.project, entry.id); return; }
            markSeen(task.url, entry.id, entry.at);
            activity.openTerminal(task.url, entry.id);
        });
        return card;
    }

    /** One computer: its name and state, then its projects, the ones with running tasks first. */
    private void drawComputer(JSONObject computer) {
        final String url = computer.optString("url");
        LinearLayout holder = holders.get(url);
        if (holder == null) return;
        Snapshot snapshot = snapshots.get(url);
        Map<String, Long> looked = seen(url);
        String name = computer.optString("name").isEmpty() ? MainActivity.host(url) : computer.optString("name");
        String mark = name + '\n' + expanded.contains(url) + '\n' + unfolded + '\n' + (snapshot == null ? "" : snapshot.state + '\n' + snapshot.line + '\n' + AggregateSessions.signature(snapshot.projects, looked));
        if (mark.equals(marks.get(url))) return;
        marks.put(url, mark);
        holder.removeAllViews();
        LinearLayout card = kit.column();
        card.setBackground(kit.shape(kit.PANEL, kit.LINE, 18));
        boolean on = snapshot != null && "on".equals(snapshot.state);
        int[] counts = on ? AggregateSessions.counts(snapshot.projects, looked) : new int[4];
        int shade = snapshot == null ? kit.MUTED : !on ? ("key".equals(snapshot.state) ? kit.BUSY : kit.BAD) : counts[0] > 0 ? kit.BAD : counts[2] > 0 ? kit.BUSY : kit.GOOD;
        LinearLayout head = kit.row();
        head.setMinimumHeight(kit.dp(64)); head.setPadding(kit.dp(16), kit.dp(10), kit.dp(4), kit.dp(10));
        head.addView(kit.dot(shade, 10));
        LinearLayout texts = kit.column();
        texts.setPadding(kit.dp(14), 0, kit.dp(4), 0);
        texts.addView(kit.line(name, 16.5f, kit.INK, true));
        texts.addView(kit.line(snapshot == null ? "正在连接…" : snapshot.line, 12.5f, on && counts[0] > 0 ? kit.BAD : kit.MUTED, false));
        head.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        head.addView(kit.iconButton(R.drawable.ic_more, name + " 的选项", () -> activity.manage(url)));
        kit.press(head, () -> activity.open(url, ""));
        head.setOnLongClickListener(v -> { activity.manage(url); return true; });
        card.addView(head);
        if (on) {
            List<AggregateSessions.Project> order = new ArrayList<>();
            for (AggregateSessions.Project project : snapshot.projects) if (!project.active.isEmpty()) order.add(project);
            int busy = order.size();
            for (AggregateSessions.Project project : snapshot.projects) if (project.active.isEmpty()) order.add(project);
            boolean all = expanded.contains(url);
            int shown = all ? order.size() : Math.max(busy, Math.min(SHOWN, order.size()));
            for (int i = 0; i < shown; i++) { card.addView(rule()); card.addView(projectRow(computer, order.get(i), looked)); }
            if (order.isEmpty()) { card.addView(rule()); card.addView(note("还没有项目。在电脑端的“项目”页添加文件夹。")); }
            if (order.size() > Math.max(busy, SHOWN) || all && order.size() > SHOWN) {
                card.addView(rule());
                TextView toggle = kit.text(all ? "收起" : "其余 " + (order.size() - shown) + " 个项目", 13, kit.MUTED);
                toggle.setGravity(Gravity.CENTER); toggle.setMinHeight(kit.dp(44));
                kit.press(toggle, () -> { if (!expanded.remove(url)) expanded.add(url); drawComputer(computer); });
                card.addView(toggle);
            }
        } else if (snapshot != null) {
            card.addView(rule());
            LinearLayout help = kit.row();
            help.setPadding(kit.dp(16), kit.dp(10), kit.dp(12), kit.dp(12));
            help.addView(kit.text("key".equals(snapshot.state) ? "登录已过期。" : "确认电脑开着、Remote CLI 在运行；公网隧道的地址每次启动都会变。", 12.5f, kit.MUTED), new LinearLayout.LayoutParams(0, -2, 1));
            TextView again = kit.bold("重新扫码", 13.5f, kit.ACCENT);
            again.setGravity(Gravity.CENTER); again.setMinHeight(kit.dp(40)); again.setPadding(kit.dp(14), 0, kit.dp(14), 0);
            again.setBackground(kit.shape(Kit.tint(kit.ACCENT, 34), 0, 12));
            kit.press(again, activity::scan);
            LinearLayout.LayoutParams beside = new LinearLayout.LayoutParams(-2, -2);
            beside.leftMargin = kit.dp(10);
            help.addView(again, beside);
            card.addView(help);
        }
        holder.addView(card);
    }
    private View rule() {
        View rule = new View(activity);
        rule.setBackgroundColor(kit.LINE);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, kit.dp(1));
        params.leftMargin = kit.dp(16);
        rule.setLayoutParams(params);
        return rule;
    }
    private View note(String text) {
        TextView note = kit.text(text, 13, kit.MUTED);
        note.setPadding(kit.dp(16), kit.dp(12), kit.dp(16), kit.dp(14));
        return note;
    }
    /** One project. A tap unfolds its conversations here, so that one of them is opened without a trip through that computer's pages. */
    private View projectRow(JSONObject computer, AggregateSessions.Project project, Map<String, Long> looked) {
        final String url = computer.optString("url"), key = url + '\n' + project.name;
        int need = 0, done = 0, work = 0;
        for (AggregateSessions.Entry entry : project.active) {
            String kind = entry.kind(looked);
            if ("confirm".equals(kind)) need++;
            else if ("done".equals(kind)) done++;
            else if ("busy".equals(kind) || "starting".equals(kind) || "pc-busy".equals(kind)) work++;
        }
        boolean open = unfolded.contains(key);
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(52)); row.setPadding(kit.dp(16), kit.dp(8), kit.dp(12), kit.dp(8));
        row.addView(kit.icon(R.drawable.ic_folder, open ? kit.ACCENT : kit.MUTED, 20));
        TextView name = kit.line(project.name, 15, kit.INK, open);
        name.setPadding(kit.dp(12), 0, kit.dp(8), 0);
        row.addView(name, new LinearLayout.LayoutParams(0, -2, 1));
        // each state keeps its own colour here too: red asks for a decision, green is finished work not yet looked at
        int[] counts = { need, done, work }, shades = { kit.BAD, kit.GOOD, kit.BUSY };
        String[] says = { " 个等你确认", " 个已完成", " 个在执行" };
        boolean first = true;
        for (int i = 0; i < 3; i++) {
            if (counts[i] == 0) continue;
            LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, -2);
            gap.leftMargin = first ? 0 : kit.dp(6);
            row.addView(kit.pill(counts[i] + says[i], shades[i]), gap);
            first = false;
        }
        if (first) row.addView(kit.text(!project.active.isEmpty() ? project.active.size() + " 个终端开着" : project.history.isEmpty() ? "还没有对话" : project.history.size() + " 段对话", 12.5f, kit.MUTED));
        View chevron = kit.icon(R.drawable.ic_chevron, Kit.tint(kit.MUTED, 150), 16);
        ((LinearLayout.LayoutParams) chevron.getLayoutParams()).leftMargin = kit.dp(6);
        chevron.setRotation(open ? 90 : 0);
        row.addView(chevron);
        kit.press(row, () -> { if (!unfolded.remove(key)) unfolded.add(key); drawComputer(computer); });
        if (!open) return row;
        LinearLayout box = kit.column();
        box.addView(row);
        LinearLayout inside = kit.column();
        inside.setPadding(kit.dp(8), kit.dp(2), kit.dp(8), kit.dp(8));
        inside.setBackground(kit.shape(kit.BG, 0, 14));
        for (AggregateSessions.Entry entry : project.active) inside.addView(talkRow(url, entry, entry.kind(looked)));
        for (int i = 0; i < Math.min(TALKS, project.history.size()); i++) inside.addView(talkRow(url, project.history.get(i), project.history.get(i).kind(looked)));
        int rest = project.history.size() - Math.min(TALKS, project.history.size());
        TextView enter = kit.bold(rest > 0 ? "新建终端，或查看其余 " + rest + " 段对话" : "新建终端 · 进入项目", 13.5f, kit.ACCENT);
        enter.setGravity(Gravity.CENTER_VERTICAL); enter.setMinHeight(kit.dp(46)); enter.setPadding(kit.dp(8), 0, kit.dp(8), 0);
        kit.press(enter, () -> activity.open(url, "", project.name));
        inside.addView(enter);
        LinearLayout.LayoutParams inset = new LinearLayout.LayoutParams(-1, -2);
        inset.setMargins(kit.dp(10), 0, kit.dp(10), kit.dp(10));
        box.addView(inside, inset);
        return box;
    }
    /** One conversation of an unfolded project: a phone terminal is entered, a saved one is continued or taken over. */
    private View talkRow(String url, AggregateSessions.Entry entry, String kind) {
        LinearLayout row = kit.row();
        row.setMinimumHeight(kit.dp(50)); row.setPadding(kit.dp(8), kit.dp(6), kit.dp(8), kit.dp(6));
        row.addView(kit.tile(toolIcon(entry.tool), toolColor(entry.tool), 30));
        TextView title = kit.line(entry.shownTitle(), 14.5f, "history".equals(kind) ? kit.MUTED : kit.INK, false);
        title.setPadding(kit.dp(10), 0, kit.dp(8), 0);
        row.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        if ("history".equals(kind)) row.addView(kit.text("继续", 12.5f, kit.MUTED));
        else row.addView(kit.pill(AggregateSessions.label(kind), color(kind)));
        kit.press(row, () -> {
            if (!entry.terminal) { activity.openSession(url, entry.project, entry.id); return; }
            markSeen(url, entry.id, entry.at);
            activity.openTerminal(url, entry.id);
        });
        return row;
    }
}
