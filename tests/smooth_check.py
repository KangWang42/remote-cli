"""How evenly a terminal answers while the relay holds a long history: the delay from a key to its echo, and the
gaps in a steady stream of output. Uses an isolated relay and agent with a made-up history; build the agent first.

    python tests/smooth_check.py [--history-mb 5] [--agent RemoteCliAgent.exe]
"""
import argparse
import json
import os
import secrets
import socket
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tunnel_check import Client, await_ready, operation
from ws_client import Socket

ROOT = Path(__file__).resolve().parents[1]


def history(megabytes):
    """Ended terminals as a long-used relay keeps them: many small pieces of output."""
    threads, piece = {}, "\x1b[2K\x1b[1G" + "x" * 56 + "\r\n"
    for n in range(max(0, round(megabytes * 1_000_000 / 300_000))):
        key = secrets.token_hex(16)
        count = 300_000 // len(piece)
        threads[key] = {"id": key, "tool": "shell", "dir": "demo", "title": "PowerShell", "session": "", "status": "", "created": 1000 + n, "state": "closed",
                        "error": "", "seq": count, "output": [{"seq": i + 1, "data": piece} for i in range(count)], "size": count * len(piece), "cols": 80, "rows": 24,
                        "instance": "0" * 32, "previous": "", "history": False, "touched": True}
    return {"threads": threads}


def spread(values):
    values = sorted(values)
    return {"count": len(values), "median_ms": round(statistics.median(values), 1), "p95_ms": round(values[int(len(values) * 0.95) - 1], 1), "max_ms": round(values[-1], 1)}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--history-mb", type=float, default=5)
    parser.add_argument("--keys", type=int, default=120)
    parser.add_argument("--agent", type=Path, default=ROOT / "agent-windows/bin/RemoteCliAgent.exe")
    parser.add_argument("--relay", type=Path, default=ROOT / "relay/server.py")
    args = parser.parse_args()
    processes, hidden = [], {"creationflags": subprocess.CREATE_NO_WINDOW}
    with tempfile.TemporaryDirectory(prefix="rcli-smooth-") as folder:
        try:
            root = Path(folder)
            data, work, store = root / "agent", root / "work", root / "relay"
            for made in (data, work, store):
                made.mkdir()
            (store / "terminals.json").write_text(json.dumps(history(args.history_mb)), encoding="utf-8")
            with socket.socket() as reserved:
                reserved.bind(("127.0.0.1", 0))
                port = reserved.getsockname()[1]
            origin, password = "http://127.0.0.1:%d" % port, "test-" + secrets.token_hex(16)
            (data / "config.json").write_text(json.dumps({"Server": origin, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
            processes.append(subprocess.Popen([sys.executable, str(args.relay), "--port", str(port), "--data", str(store)],
                                              env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden))
            client = Client(origin, timeout=10)
            await_ready(lambda: client.call("/api/login", {"password": password}), "relay")
            subprocess.run([str(args.agent), "--set-password", str(data)], input=password.encode(), check=True, timeout=10, **hidden)
            processes.append(subprocess.Popen([str(args.agent), "--data", str(data)], **hidden))
            await_ready(lambda: client.call("/api/terminal")["device"]["workspaces"], "agent")
            terminal = client.call("/api/terminal", operation(terminal="", action="start", tool="shell", dir="demo"))["terminal"]
            await_ready(lambda: client.call("/api/terminal?terminal=%s&after=0" % terminal)["terminal"]["state"] == "running", "terminal")
            time.sleep(4)       # the prompt is drawn
            after = client.call("/api/terminal?terminal=%s&after=0" % terminal)["after"]
            ws = Socket(origin, "/api/terminal/ws?terminal=%s&after=%d" % (terminal, after), client.cookie["Cookie"], 10)
            ws.receive(10)

            events, arrived = [], threading.Condition()

            def read():
                try:
                    while True:
                        item = ws.receive(3600)
                        if item.get("t") == "out" and item["chunks"]:
                            with arrived:
                                events.append((time.perf_counter(), "".join(c["data"] for c in item["chunks"])))
                                arrived.notify_all()
                except (OSError, RuntimeError, ValueError):
                    pass

            threading.Thread(target=read, daemon=True).start()

            def shown(mark, since, seconds=15):
                """When output that came after `since` first contains `mark`."""
                def at():
                    text = ""
                    for moment, data in events:
                        if moment >= since:
                            text += data
                            if mark in text:
                                return moment
                with arrived:
                    if not arrived.wait_for(lambda: at() is not None, seconds):
                        raise TimeoutError("no echo of " + mark)
                    return at()

            # Typing: one letter at a time, as fast as a person, each timed until its echo is back. The letters are
            # ones that no control sequence ends with.
            echoes = []
            for n in range(args.keys):
                key, began = "qwzxvkjy"[n % 8], time.perf_counter()
                ws.send(operation(terminal, "input", data=key))
                echoes.append((shown(key, began) - began) * 1000)
                time.sleep(0.07)
            ws.send(operation(terminal, "input", data="\x03"))
            time.sleep(1)
            # A program that prints a line every few milliseconds for several seconds: the longest wait between pieces.
            began = time.perf_counter()
            ws.send(operation(terminal, "input", data="1..500 | % { 'line ' + $_ + ' ' + ('y' * 60); Start-Sleep -Milliseconds 8 }; 'STREAM_' + 'END'\r"))
            first, last = shown("line 1 ", began), shown("STREAM_END", began, 30)
            arrivals = [moment for moment, data in events if first <= moment <= last]
            gaps = [(b - a) * 1000 for a, b in zip(arrivals, arrivals[1:])]
            ws.close()
            print(json.dumps({"history_mb": args.history_mb, "key_to_echo": spread(echoes), "stream_gap": spread(gaps),
                              "stream_gaps_over_100ms": sum(g > 100 for g in gaps), "stream_seconds": round(arrivals[-1] - arrivals[0], 1)}))
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=10)


if __name__ == "__main__":
    main()
