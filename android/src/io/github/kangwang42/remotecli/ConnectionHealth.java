package io.github.kangwang42.remotecli;

/** A brief network interruption is retried; a dead Cloudflare tunnel goes straight to pairing. */
final class ConnectionHealth {
    private int failures;
    boolean sample(boolean reachable, int status) {
        if (reachable) { failures = 0; return false; }
        return status == 530 || ++failures >= 2;
    }
    static boolean httpError(boolean mainFrame, boolean relayRequest, int status) {
        return status >= 500 && (mainFrame || relayRequest);
    }
}
