using System;
using System.Threading;

/// Stands in for cloudflared in tests/phone_line_check.py: says the address a tunnel would be given, as cloudflared
/// does, and stays until it is ended. It opens nothing to anywhere.
static class TunnelStandin {
    static int Main() {
        Console.Error.WriteLine("INF |  https://remote-cli-standin.trycloudflare.com  |");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
