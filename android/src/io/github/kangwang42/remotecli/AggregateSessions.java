package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Set;

/** One computer's project inventory. Kept independent of Android views for regression checks. */
final class AggregateSessions {
    static final class Entry {
        final boolean terminal, live;
        final String id, project, title, tool, state, phase, status, host, attached, session;
        Entry(boolean terminal, String id, String project, String title, String tool, String state,
              String phase, String status, boolean live, String host, String attached, String session) {
            this.terminal = terminal; this.id = id; this.project = project; this.title = title;
            this.tool = tool; this.state = state; this.phase = phase; this.status = status;
            this.live = live; this.host = host; this.attached = attached; this.session = session;
        }
        boolean running() {
            return terminal ? "running".equals(state) || "starting".equals(state) : live && "cli".equals(host);
        }
        String label() {
            if (!terminal) {
                if (!live) return "历史对话";
                if ("shared".equals(host)) return "后台锁定";
                if ("remote".equals(host)) return "其它远程终端";
                if (!"cli".equals(host)) return "归属待确认";
                return "busy".equals(status) ? "电脑正在执行" : "电脑等待输入";
            }
            if ("starting".equals(state)) return "正在启动";
            if (!running()) return "已结束";
            String value = phase.isEmpty() ? ("busy".equals(status) ? "busy" : "idle") : phase;
            if ("confirm".equals(value)) return "等你确认";
            if ("busy".equals(value)) return "正在执行";
            if ("done".equals(value)) return "已完成";
            return "等待输入";
        }
        int rank() {
            String value = label();
            return "等你确认".equals(value) ? 0 : "正在执行".equals(value) || "电脑正在执行".equals(value) ? 1 : 2;
        }
        String toolName() { return "claude".equals(tool) ? "Claude Code" : "codex".equals(tool) ? "Codex" : "PowerShell"; }
        String shownTitle() { return title.isEmpty() ? toolName() : title; }
    }
    static final class Project {
        final String name;
        final List<Entry> active = new ArrayList<>(), history = new ArrayList<>();
        Project(String name) { this.name = name; }
    }
    static List<Project> projects(List<String> folders, List<Entry> entries) {
        LinkedHashMap<String, Project> groups = new LinkedHashMap<>();
        for (String name : folders) if (!name.isEmpty() && !groups.containsKey(name)) groups.put(name, new Project(name));
        Set<String> phoneSessions = new HashSet<>();
        for (Entry entry : entries) if (entry.terminal && entry.running() && !entry.session.isEmpty()) phoneSessions.add(entry.tool + ":" + entry.session);
        for (Entry entry : entries) {
            if (entry.project.isEmpty()) continue;
            if (!groups.containsKey(entry.project)) groups.put(entry.project, new Project(entry.project));
            if (entry.terminal && !entry.running()) continue;
            if (!entry.terminal && (!entry.attached.isEmpty() || phoneSessions.contains(entry.tool + ":" + entry.id))) continue;
            Project group = groups.get(entry.project);
            if (entry.running()) group.active.add(entry); else group.history.add(entry);
        }
        for (Project group : groups.values()) Collections.sort(group.active, Comparator.comparingInt(Entry::rank));
        return new ArrayList<>(groups.values());
    }
    static String terminalPath(String id) {
        if (id == null || !id.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("终端编号无效");
        return "/terminal/?id=" + id;
    }
    static String signature(List<Project> projects) {
        StringBuilder key = new StringBuilder();
        for (Project project : projects) {
            add(key, project.name);
            for (List<Entry> entries : Arrays.asList(project.active, project.history)) {
                key.append(entries.size()).append(':');
                for (Entry entry : entries) {
                    add(key, entry.id); add(key, entry.shownTitle()); add(key, entry.tool);
                    add(key, entry.label()); add(key, entry.host);
                }
            }
        }
        return key.toString();
    }
    private static void add(StringBuilder key, String value) { key.append(value.length()).append(':').append(value); }
}
