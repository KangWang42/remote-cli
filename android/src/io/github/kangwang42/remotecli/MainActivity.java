package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.speech.RecognizerIntent;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.ArrayList;
import org.json.JSONObject;

/**
 * One screen to choose the computer (scan its code with the phone's camera, or type the address and password),
 * then the terminal pages served by the program on that computer, shown full screen.
 */
public final class MainActivity extends Activity {
    private static final int SPEECH = 4103;
    private static final int BG = Color.rgb(26, 27, 38), PANEL = Color.rgb(34, 36, 54), LINE = Color.rgb(47, 51, 77),
            INK = Color.rgb(192, 202, 245), MUTED = Color.rgb(129, 137, 173), ACCENT = Color.rgb(122, 162, 247), BAD = Color.rgb(247, 118, 142);
    private SharedPreferences prefs;
    private FrameLayout root;
    private WebView web;
    private EditText addressBox, passwordBox;
    private TextView message;
    private String server = "";

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("remote-cli", MODE_PRIVATE);
        root = new FrameLayout(this);
        root.setBackgroundColor(BG);
        setContentView(root);
        if (!linked(getIntent())) {
            server = prefs.getString("server", "");
            if (server.isEmpty()) choose(""); else open(server, "");
        }
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); linked(intent); }

    /** remotecli://connect?u=address&p=password, from the code on the computer's screen. */
    private boolean linked(Intent intent) {
        Uri link = intent == null ? null : intent.getData();
        if (link == null || !"remotecli".equals(link.getScheme())) return false;
        String address = clean(link.getQueryParameter("u")), password = link.getQueryParameter("p");
        if (address.isEmpty()) { choose("二维码里的地址无效，请在电脑上重新显示后再扫。"); return true; }
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

    private int dp(float value) { return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, getResources().getDisplayMetrics())); }
    private TextView text(String value, float size, int color) {
        TextView view = new TextView(this);
        view.setText(value); view.setTextSize(size); view.setTextColor(color); view.setLineSpacing(dp(3), 1f);
        return view;
    }
    private GradientDrawable shape(int fill, int stroke) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill); drawable.setCornerRadius(dp(12)); drawable.setStroke(dp(1), stroke);
        return drawable;
    }
    private EditText field(String hint, int type) {
        EditText box = new EditText(this);
        box.setHint(hint); box.setHintTextColor(MUTED); box.setTextColor(INK); box.setTextSize(16); box.setSingleLine(true);
        box.setInputType(type); box.setBackground(shape(PANEL, LINE)); box.setPadding(dp(14), 0, dp(14), 0); box.setMinHeight(dp(50));
        return box;
    }
    private LinearLayout.LayoutParams below(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(top);
        return params;
    }

    /** The screen for choosing a computer. */
    private void choose(String problem) {
        if (web != null) { root.removeView(web); web.destroy(); web = null; }
        root.removeAllViews();
        getWindow().setStatusBarColor(BG); getWindow().setNavigationBarColor(BG);
        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setPadding(dp(24), dp(56), dp(24), dp(32));
        TextView title = text("Remote CLI", 26, INK);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        column.addView(title);
        column.addView(text("在手机上使用电脑里的终端、Claude Code 和 Codex。", 14.5f, MUTED), below(6));
        column.addView(text("1. 在电脑上打开 Remote CLI。\n2. 用手机相机扫它显示的二维码，会直接回到这里并连上。\n3. 扫不了时，把电脑上显示的地址和密码填在下面。", 14.5f, INK), below(22));
        addressBox = field("地址，例如 192.168.1.5:8722", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        addressBox.setText(prefs.getString("server", ""));
        column.addView(addressBox, below(22));
        passwordBox = field("访问密码", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        passwordBox.setImeOptions(EditorInfo.IME_ACTION_GO);
        passwordBox.setOnEditorActionListener((view, action, event) -> { connect(); return true; });
        column.addView(passwordBox, below(10));
        TextView go = text("连接", 16, Color.rgb(16, 18, 28));
        go.setTypeface(Typeface.DEFAULT_BOLD); go.setGravity(Gravity.CENTER); go.setBackground(shape(ACCENT, ACCENT)); go.setMinHeight(dp(50));
        go.setClickable(true); go.setFocusable(true); go.setOnClickListener(view -> connect());
        column.addView(go, below(14));
        message = text(problem, 14, BAD);
        column.addView(message, below(12));
        column.addView(text("地址和密码相当于这台电脑的钥匙，不要发给别人。在公共网络下请用电脑端的“公网隧道”或自己的 https 中转。", 12.5f, MUTED), below(18));
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(column, new ViewGroup.LayoutParams(-1, -2));
        root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
    }
    private void connect() {
        String address = clean(addressBox.getText().toString());
        if (address.isEmpty()) { message.setText("地址无效。例如 192.168.1.5:8722，或 https:// 开头的地址。"); return; }
        open(address, passwordBox.getText().toString().trim());
    }

    /** Shows the pages of the computer at this address; a password signs in first. */
    private void open(String address, String password) {
        server = address;
        prefs.edit().putString("server", address).apply();
        if (web != null) { root.removeView(web); web.destroy(); }
        root.removeAllViews();
        web = new WebView(this);
        web.setBackgroundColor(BG);
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
                if (request.isForMainFrame() && view == web) root.post(() -> choose("连不上 " + origin + "。请确认电脑上的 Remote CLI 在运行；局域网直连时手机要连同一个 Wi-Fi。公网隧道的地址每次启动都会变，需要重新扫码。"));
            }
        });
        root.addView(web, new FrameLayout.LayoutParams(-1, -1));
        web.loadUrl(address + "/" + (password.isEmpty() ? "" : "#p=" + Uri.encode(password)));
    }

    private final class Bridge {
        /** The system's speech recognizer; what was said goes into the page's message box. */
        @JavascriptInterface public void voice() { runOnUiThread(MainActivity.this::dictate); }
        /** The page tells the colour of its skin so the bars around it match. */
        @JavascriptInterface public void chrome(String color) {
            if (color == null || !color.matches("#[0-9a-fA-F]{6}")) return;
            final int shade = Color.parseColor(color);
            runOnUiThread(() -> { getWindow().setStatusBarColor(shade); getWindow().setNavigationBarColor(shade); root.setBackgroundColor(shade); });
        }
        /** Back to the screen for choosing a computer. */
        @JavascriptInterface public void disconnect() { runOnUiThread(() -> choose("")); }
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
        if (web != null && web.canGoBack()) web.goBack(); else super.onBackPressed();
    }
    @Override protected void onPause() { super.onPause(); CookieManager.getInstance().flush(); if (web != null) web.onPause(); }
    @Override protected void onResume() { super.onResume(); if (web != null) web.onResume(); }
    @Override protected void onDestroy() { if (web != null) { web.destroy(); web = null; } super.onDestroy(); }
}
