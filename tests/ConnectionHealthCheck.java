package io.github.kangwang42.remotecli;

/** Regression cases for dead tunnels, transient failures and unrelated WebView resources. */
public final class ConnectionHealthCheck {
    private static void expect(boolean value, String name) {
        if (!value) throw new AssertionError(name);
    }
    public static void main(String[] args) {
        expect(ConnectionHealth.httpError(true, false, 530), "1033 tunnel page returns to pairing");
        expect(ConnectionHealth.httpError(false, true, 530), "API tunnel error returns to pairing");
        expect(!ConnectionHealth.httpError(false, false, 530), "unrelated resource must not disconnect");
        expect(!ConnectionHealth.httpError(false, true, 401), "expired login remains on login page");
        expect(!ConnectionHealth.httpError(true, false, 404), "missing page is not a tunnel failure");
        ConnectionHealth health = new ConnectionHealth();
        expect(!health.sample(false, 0), "one timeout is retried");
        expect(!health.sample(true, 200), "healthy response resets failures");
        expect(!health.sample(false, 0), "timeout after recovery is retried");
        expect(health.sample(false, 0), "two timeouts return to pairing");
        expect(new ConnectionHealth().sample(false, 530), "dead tunnel returns without waiting again");
        ConnectionHealth fresh = new ConnectionHealth();
        expect(!fresh.sample(true, 200), "new scan starts healthy");
        expect(!fresh.sample(false, 200), "invalid service response is retried once");
        expect(fresh.sample(false, 200), "repeated HTML response returns to pairing");
        System.out.println("Connection health: tunnel HTTP errors, failure recovery, login and resource isolation passed");
    }
}
