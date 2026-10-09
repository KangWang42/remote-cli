package io.github.kangwang42.remotecli;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Which tasks a person should be told about while the app is not on the screen: one that has begun to wait on a
 * question, and one that has finished what it was doing. Nothing here touches Android, so it can be checked on its own.
 */
final class Watch {
    /** A terminal that runs on a computer, as its overview lists it. */
    static final class Task {
        final String id, title, dir, phase;
        final boolean done;
        Task(String id, String title, String dir, String phase, boolean done) {
            this.id = id; this.title = title; this.dir = dir; this.phase = phase == null || phase.isEmpty() ? "idle" : phase; this.done = done;
        }
    }
    /** Something to tell: kind is "confirm" or "done". */
    static final class Alert {
        final String id, kind, title, dir;
        Alert(String id, String kind, String title, String dir) { this.id = id; this.kind = kind; this.title = title; this.dir = dir; }
    }

    /**
     * What has changed since the last look at one computer. phases holds each task's phase from that look and is
     * brought up to date. A task seen for the first time is only noted: what it was doing before nobody knows, and
     * the person has just left the app, where it was on the screen. A task that asks again after it was answered,
     * or finishes a second piece of work, is told again.
     */
    static List<Alert> news(List<Task> tasks, Map<String, String> phases) {
        List<Alert> alerts = new ArrayList<>();
        Set<String> present = new HashSet<>();
        for (Task task : tasks) {
            present.add(task.id);
            String before = phases.put(task.id, task.phase);
            if (before == null || before.equals(task.phase)) continue;
            if ("confirm".equals(task.phase)) alerts.add(new Alert(task.id, "confirm", task.title, task.dir));
            else if ("idle".equals(task.phase) && task.done && ("busy".equals(before) || "confirm".equals(before) || "starting".equals(before)))
                alerts.add(new Alert(task.id, "done", task.title, task.dir));
        }
        phases.keySet().retainAll(present);
        return alerts;
    }
}
