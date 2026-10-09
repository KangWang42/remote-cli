package io.github.kangwang42.remotecli;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

public final class AggregateSessionsCheck {
    private static final String TERMINAL = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static AggregateSessions.Entry terminal(String project, String phase, String state, String session) {
        return new AggregateSessions.Entry(true, TERMINAL, project, "手机终端", "codex", state, phase, "", false, "", "", session);
    }
    private static AggregateSessions.Entry saved(String project, String id, String host, boolean live, String attached) {
        return new AggregateSessions.Entry(false, id, project, "对话", "codex", "", "", "busy", live, host, attached, "");
    }
    private static void require(boolean ok, String reason) { if (!ok) throw new AssertionError(reason); }
    public static void main(String[] args) {
        List<AggregateSessions.Project> groups = AggregateSessions.projects(Arrays.asList("同名项目", "空项目", "同名项目"), Arrays.asList(
            saved("同名项目", "cli", "cli", true, ""),
            terminal("同名项目", "confirm", "running", "phone-session"),
            saved("同名项目", "phone-session", "remote", true, TERMINAL),
            saved("同名项目", "shared", "shared", true, ""),
            saved("同名项目", "unknown", "", true, ""),
            saved("同名项目", "past", "", false, ""),
            terminal("终端独有项目", "busy", "starting", ""),
            terminal("同名项目", "idle", "closed", "")
        ));
        require(groups.size() == 3, "Empty and terminal-only projects must survive without duplicate project rows");
        AggregateSessions.Project group = groups.get(0);
        require(group.active.size() == 2, "Shared/unknown locks and duplicate attached histories entered active count");
        require(group.history.size() == 3, "Locked histories were lost");
        require("等你确认".equals(group.active.get(0).label()), "Confirmation must sort before computer work");
        require("后台锁定".equals(group.history.get(0).label()), "Shared busy lock must not display running");
        require("归属待确认".equals(group.history.get(1).label()), "Legacy host must not imply computer CLI");
        require("历史对话".equals(group.history.get(2).label()), "Ended history label");
        require(groups.get(1).active.isEmpty(), "Empty configured project omitted or polluted");
        require("正在启动".equals(groups.get(2).active.get(0).label()), "Starting terminal must be listed");

        List<AggregateSessions.Project> other = AggregateSessions.projects(Collections.singletonList("同名项目"),
            Collections.singletonList(saved("同名项目", "other", "cli", true, "")));
        require(other.get(0).active.size() == 1 && group.active.size() == 2, "Separate computers with the same project name must remain separate");
        require(!AggregateSessions.signature(groups).equals(AggregateSessions.signature(other)), "Inventory changes must update render signature");
        List<AggregateSessions.Project> copied = AggregateSessions.projects(Arrays.asList("同名项目", "空项目", "同名项目"), Arrays.asList(
            saved("同名项目", "cli", "cli", true, ""), terminal("同名项目", "confirm", "running", "phone-session"),
            saved("同名项目", "phone-session", "remote", true, ""), saved("同名项目", "shared", "shared", true, ""),
            saved("同名项目", "unknown", "", true, ""), saved("同名项目", "past", "", false, ""),
            terminal("终端独有项目", "busy", "starting", "")));
        require(AggregateSessions.signature(groups).equals(AggregateSessions.signature(copied)), "Unchanged display must retain its views during refresh");
        require(AggregateSessions.terminalPath(TERMINAL).equals("/terminal/?id=" + TERMINAL), "Terminal link must identify the exact terminal");
        boolean refused = false;
        try { AggregateSessions.terminalPath("../?p=secret"); } catch (IllegalArgumentException expected) { refused = true; }
        require(refused, "Terminal route must reject malformed identifiers");
        AggregateSessions.Entry finished = new AggregateSessions.Entry(true, TERMINAL, "项目", "", "claude", "running", "idle", "", false, "", "", "", true, 42L);
        java.util.Map<String, Long> looked = new java.util.HashMap<>();
        require("done".equals(finished.kind(looked)), "Finished work must stay marked until it has been looked at");
        looked.put(TERMINAL, 41L);
        require("done".equals(finished.kind(looked)), "A newer phase change must be marked again");
        looked.put(TERMINAL, 42L);
        require("idle".equals(finished.kind(looked)), "Work that was looked at must fall back to waiting");
        require(AggregateSessions.rank("confirm") < AggregateSessions.rank("done") && AggregateSessions.rank("done") < AggregateSessions.rank("busy")
            && AggregateSessions.rank("idle") < AggregateSessions.rank("pc-busy"), "Attention order must match the pages");
        int[] counts = AggregateSessions.counts(groups, null);
        require(counts[0] == 1 && counts[1] == 0 && counts[2] == 2 && counts[3] == 3, "Counts must cover confirm, busy with starting, and all open tasks");
        List<AggregateSessions.Project> one = AggregateSessions.projects(Collections.singletonList("项目"), Collections.singletonList(finished));
        looked.clear();
        String unseen = AggregateSessions.signature(one, looked);
        looked.put(TERMINAL, 42L);
        require(!unseen.equals(AggregateSessions.signature(one, looked)), "Looking at finished work must redraw its row");
        // Saved conversations: the one used last comes first, and each says how long ago that was.
        AggregateSessions.Entry older = saved("项目", "older", "", false, ""), newer = saved("项目", "newer", "", false, "");
        older.used = 1_000_000L; newer.used = 9_000_000L;
        List<AggregateSessions.Project> used = AggregateSessions.projects(Collections.singletonList("项目"), Arrays.asList(older, newer));
        require("newer".equals(used.get(0).history.get(0).id), "The conversation used last must come first");
        long now = 1_700_000_000_000L;
        require(AggregateSessions.ago(0, now).isEmpty() && "刚刚".equals(AggregateSessions.ago(now - 30_000, now)) && "5 分钟前".equals(AggregateSessions.ago(now - 300_000, now))
            && "3 小时前".equals(AggregateSessions.ago(now - 3 * 3600_000L, now)) && "2 天前".equals(AggregateSessions.ago(now - 2 * 86400_000L, now)), "Last use must read as on the pages");
        System.out.println("Aggregate checks passed: computer isolation, grouping, lock states, deduplication, sorting, stable refresh and exact terminal route.");
    }
}
