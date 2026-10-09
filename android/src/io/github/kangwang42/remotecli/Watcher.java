package io.github.kangwang42.remotecli;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.SystemClock;
import android.webkit.CookieManager;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Keeps an eye on the computers while the app is not on the screen, and tells the person when a task begins to wait
 * on a question or finishes. It asks each computer for its overview, the same request the workbench makes, every
 * twenty seconds; nothing is sent to any other service. It runs only when the person turned reminders on, starts
 * when the app leaves the screen, and ends when the app comes back, when nothing has been running for five minutes,
 * or after eight hours.
 */
public final class Watcher extends Service {
    private static final int ONGOING = 1;
    private static final long EVERY = 20_000, LONGEST = 8 * 3600_000L;
    private static final int QUIET_LOOKS = 15;
    private static final String CHANNEL_WATCH = "watch", CHANNEL_TASKS = "tasks";

    private HandlerThread thread;
    private Handler handler;
    private final Map<String, Map<String, String>> phases = new HashMap<>();     // per computer: task -> phase at the last look
    private int quiet;
    private long began;

    static void start(Context context) {
        try { context.startForegroundService(new Intent(context, Watcher.class)); }
        catch (Exception refused) { /* the system does not allow it right now: no reminders this time */ }
    }
    static void stop(Context context) {
        try { context.stopService(new Intent(context, Watcher.class)); } catch (Exception ignored) { }
    }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int id) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        NotificationChannel quietly = new NotificationChannel(CHANNEL_WATCH, "正在留意任务", NotificationManager.IMPORTANCE_MIN);
        quietly.setShowBadge(false);
        manager.createNotificationChannel(quietly);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL_TASKS, "任务提醒", NotificationManager.IMPORTANCE_HIGH));
        Notification ongoing = new Notification.Builder(this, CHANNEL_WATCH).setSmallIcon(R.drawable.ic_shell)
            .setContentTitle("正在留意电脑上的任务").setContentText("有任务等你确认或完成时提醒你；回到 App 后停止").setOngoing(true)
            .setContentIntent(open()).build();
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(ONGOING, ongoing, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            else startForeground(ONGOING, ongoing);
        } catch (Exception refused) { stopSelf(); return START_NOT_STICKY; }
        if (thread == null) {
            thread = new HandlerThread("remote-cli-watch");
            thread.start();
            handler = new Handler(thread.getLooper());
            began = SystemClock.elapsedRealtime();
            handler.post(this::look);
        }
        return START_NOT_STICKY;
    }

    @Override public void onDestroy() {
        if (handler != null) handler.removeCallbacksAndMessages(null);
        if (thread != null) thread.quitSafely();
        super.onDestroy();
    }

    private PendingIntent open() {
        Intent intent = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        return PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    private void look() {
        boolean anything = false;
        try {
            JSONArray computers = new JSONArray(getSharedPreferences("remote-cli", MODE_PRIVATE).getString("computers", "[]"));
            for (int i = 0; i < computers.length(); i++) {
                JSONObject computer = computers.optJSONObject(i);
                if (computer == null) continue;
                String url = computer.optString("url");
                JSONObject data = overview(url);
                JSONObject device = data == null ? null : data.optJSONObject("device");
                if (device == null || !device.optBoolean("online") || !device.optBoolean("enabled")) continue;
                List<Watch.Task> tasks = new ArrayList<>();
                JSONArray terminals = data.optJSONArray("terminals");
                for (int k = 0; terminals != null && k < terminals.length(); k++) {
                    JSONObject t = terminals.optJSONObject(k);
                    if (t == null || !("running".equals(t.optString("state")) || "starting".equals(t.optString("state")))) continue;
                    tasks.add(new Watch.Task(t.optString("id"), t.optString("title"), t.optString("dir"),
                        "starting".equals(t.optString("state")) ? "starting" : t.optString("phase"), t.optBoolean("done")));
                }
                anything |= !tasks.isEmpty();
                Map<String, String> before = phases.get(url);
                if (before == null) { before = new HashMap<>(); phases.put(url, before); }
                String name = computer.optString("name").isEmpty() ? MainActivity.host(url) : computer.optString("name");
                for (Watch.Alert alert : Watch.news(tasks, before)) tell(url, name, alert);
            }
        } catch (Exception broken) { /* looked at again in a moment */ }
        quiet = anything ? 0 : quiet + 1;
        if (quiet >= QUIET_LOOKS || SystemClock.elapsedRealtime() - began > LONGEST) { stopSelf(); return; }
        handler.postDelayed(this::look, EVERY);
    }

    private void tell(String url, String computer, Watch.Alert alert) {
        boolean asks = "confirm".equals(alert.kind);
        String title = alert.title.isEmpty() ? alert.dir : alert.title;
        Notification notification = new Notification.Builder(this, CHANNEL_TASKS).setSmallIcon(R.drawable.ic_shell)
            .setContentTitle((asks ? "等你确认 · " : "已完成 · ") + computer)
            .setContentText(title + (alert.dir.isEmpty() || alert.dir.equals(title) ? "" : " · " + alert.dir))
            .setAutoCancel(true).setContentIntent(open()).build();
        try { getSystemService(NotificationManager.class).notify((url + alert.id).hashCode(), notification); }
        catch (Exception refused) { /* notifications are not allowed: the person will see it in the app */ }
    }

    /** The overview of one computer, signed in with the cookie its pages keep; null when it cannot be had. */
    private JSONObject overview(String url) {
        HttpURLConnection connection = null;
        try {
            String cookie = CookieManager.getInstance().getCookie(url);
            connection = (HttpURLConnection) new URL(url + "/api/terminal").openConnection();
            connection.setConnectTimeout(5000); connection.setReadTimeout(10000); connection.setUseCaches(false);
            connection.setInstanceFollowRedirects(false);
            if (cookie != null) connection.setRequestProperty("Cookie", cookie);
            if (connection.getResponseCode() != 200) return null;
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            try (InputStream input = connection.getInputStream()) {
                byte[] part = new byte[16384]; int n;
                while ((n = input.read(part)) > 0) {
                    if (bytes.size() + n > 4000000) return null;
                    bytes.write(part, 0, n);
                }
            }
            return new JSONObject(bytes.toString("UTF-8"));
        } catch (Exception unreachable) { return null; }
        finally { if (connection != null) connection.disconnect(); }
    }
}
