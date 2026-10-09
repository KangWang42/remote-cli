package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** What the reminders tell and what they keep quiet about, without an Android device. */
public final class WatchCheck {
    static void require(boolean condition, String message) { if (!condition) throw new AssertionError(message); }
    static Watch.Task task(String id, String phase, boolean done) { return new Watch.Task(id, "标题" + id, "demo", phase, done); }
    static String told(List<Watch.Alert> alerts) {
        List<String> parts = new ArrayList<>();
        for (Watch.Alert alert : alerts) parts.add(alert.id + ":" + alert.kind);
        return String.join(",", parts);
    }
    public static void main(String[] args) {
        Map<String, String> phases = new HashMap<>();
        // the first look only notes what is there: the person has just left the app, where all of it was on the screen
        require(told(Watch.news(Arrays.asList(task("a", "busy", false), task("b", "confirm", false), task("c", "idle", true)), phases)).isEmpty(), "first look is quiet");
        // nothing changed, nothing told
        require(told(Watch.news(Arrays.asList(task("a", "busy", false), task("b", "confirm", false), task("c", "idle", true)), phases)).isEmpty(), "no change is quiet");
        // a begins to ask; b was answered and works again; c gets new work
        require(told(Watch.news(Arrays.asList(task("a", "confirm", false), task("b", "busy", false), task("c", "busy", true)), phases)).equals("a:confirm"), "a question is told");
        // a was answered and finished; b finished; c still works
        require(told(Watch.news(Arrays.asList(task("a", "idle", true), task("b", "idle", true), task("c", "busy", true)), phases)).equals("a:done,b:done"), "finished work is told");
        // b asks a second time, and a new task appears already asking: only what changed while watching is told
        require(told(Watch.news(Arrays.asList(task("a", "idle", true), task("b", "confirm", true), task("c", "busy", true), task("d", "confirm", false)), phases)).equals("b:confirm"), "asking again is told again");
        // a task that goes idle without having worked (the echo of a key, a redraw) is not "finished"
        phases.put("e", "busy");
        require(told(Watch.news(Arrays.asList(task("e", "idle", false)), phases)).isEmpty(), "idle without work is quiet");
        require(phases.size() == 1 && phases.containsKey("e"), "tasks that are gone are forgotten");
        // a terminal that was starting and settles at its prompt has done nothing yet; one that worked from the start has
        phases.clear(); phases.put("f", "starting"); phases.put("g", "starting");
        require(told(Watch.news(Arrays.asList(task("f", "idle", false), task("g", "idle", true)), phases)).equals("g:done"), "starting then done");
        require(new Watch.Task("x", "", "", "", false).phase.equals("idle"), "an older computer names no phase");
        System.out.println("Watch checks passed: quiet first look, questions and finished work told once per change, gone tasks forgotten.");
    }
}
