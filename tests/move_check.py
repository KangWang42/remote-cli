"""The computer turns from one relay to another and back (as when the way of connecting is changed between the
tunnel and a relay of one's own): its running terminals are listed at the relay it turns to, with their names
and what they printed, and are used there; the terminals that ended are listed as recently used; a terminal that
ended while the computer was elsewhere is not left running at the relay it comes back to; nothing is listed twice.
Two isolated relays and the real agent; build the agent first.

    python tests/move_check.py
"""
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tunnel_check import Client, await_ready, operation

ROOT = Path(__file__).resolve().parents[1]


def main():
    hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    with tempfile.TemporaryDirectory(prefix="rcli-move-", ignore_cleanup_errors=True) as folder:
        root = Path(folder)
        data, work = root / "agent", root / "work"
        for made in (data, work, root / "first", root / "second"):
            made.mkdir()
        ports = []
        for _ in range(2):
            with socket.socket() as reserved:
                reserved.bind(("127.0.0.1", 0))
                ports.append(reserved.getsockname()[1])
        password = "test-" + secrets.token_hex(16)
        origins = ["http://127.0.0.1:%d" % port for port in ports]

        def turn(origin):       # what the window does when the way of connecting is changed
            (data / "config.tmp").write_text(json.dumps({"Server": origin, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
            os.replace(data / "config.tmp", data / "config.json")

        turn(origins[0])
        subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True, timeout=10, **hidden)
        relays = [subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(root / name)],
                                   env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL, **hidden)
                  for port, name in zip(ports, ("first", "second"))]
        agent = None
        try:
            first, second = Client(origins[0], timeout=10), Client(origins[1], timeout=10)
            for client in (first, second):
                await_ready(lambda: client.call("/api/login", {"password": password}), "relay")
            agent = subprocess.Popen([str(agent_exe), "--data", str(data)], **hidden)
            await_ready(lambda: first.call("/api/terminal")["device"]["workspaces"], "agent")
            listed = lambda client: {t["id"]: t for t in client.call("/api/terminal")["terminals"]}
            text = lambda client, terminal, after=0: "".join(c["data"] for c in client.call("/api/terminal?terminal=%s&after=%d" % (terminal, after))["chunks"])

            def started(client):
                terminal = client.call("/api/terminal", operation("", "start", tool="shell", dir="demo"))["terminal"]
                await_ready(lambda: listed(client)[terminal]["state"] == "running", "terminal")
                time.sleep(3)
                return terminal

            def say(client, terminal, *words):
                client.call("/api/terminal", operation(terminal, "input", data="'%s'\r" % "' + '".join(words)))
                await_ready(lambda: "".join(words) in text(client, terminal), "".join(words))

            # at the first relay: a terminal that is named and stays, and one that ends
            stays = started(first)
            say(first, stays, "AT_", "FIRST")
            first.call("/api/terminal", operation(stays, "rename", title="我的终端"))
            ends = started(first)
            say(first, ends, "LAST_", "WORDS")
            first.call("/api/terminal", operation(ends, "close"))
            await_ready(lambda: listed(first)[ends]["state"] == "closed", "the second terminal's end")
            time.sleep(2)           # the agent has been told the name and has put the ended terminal aside

            # the computer turns to the second relay, which has never heard of either
            turned = time.monotonic()
            turn(origins[1])
            await_ready(lambda: listed(second).get(stays, {}).get("state") == "running", "the running terminal at the second relay", 30)
            took = time.monotonic() - turned
            await_ready(lambda: "AT_FIRST" in text(second, stays), "what the running terminal printed before")
            await_ready(lambda: listed(second).get(ends, {}).get("state") == "closed" and "LAST_WORDS" in text(second, ends), "the ended terminal and its last screen")
            there = listed(second)
            assert there[stays]["title"] == "我的终端" and there[stays]["tool"] == "shell" and there[stays]["dir"] == "demo", there[stays]
            assert sorted(there) == sorted([stays, ends]), there
            assert second.call("/api/terminal")["device"]["online"]
            say(second, stays, "AT_", "SECOND")                     # it is used there like any other
            later = started(second)
            say(second, later, "BORN_", "SECOND")
            second.call("/api/terminal", operation(stays, "close"))
            await_ready(lambda: listed(second)[stays]["state"] == "closed", "the first terminal's end at the second relay")
            time.sleep(2)

            # and back: the first relay still lists `stays` as running, and has never heard of `later`
            assert listed(first)[stays]["state"] == "running", listed(first)[stays]
            turn(origins[0])
            await_ready(lambda: listed(first).get(later, {}).get("state") == "running", "the newer terminal at the first relay", 30)
            await_ready(lambda: listed(first)[stays]["state"] == "closed", "the end of the terminal that ended elsewhere")
            await_ready(lambda: "BORN_SECOND" in text(first, later), "what the newer terminal printed")
            await_ready(lambda: "AT_SECOND" in text(first, stays), "what the first terminal printed elsewhere")
            say(first, later, "BACK_", "FIRST")
            back = listed(first)
            assert sorted(back) == sorted([stays, ends, later]), back
            assert [t["state"] for t in (back[stays], back[ends], back[later])] == ["closed", "closed", "running"], back
            first.call("/api/terminal", operation(later, "close"))
            print(json.dumps({"listed_at_other_relay_seconds": round(took, 1), "history_follows": True, "name_follows": True, "ended_follow": True, "no_terminal_left_running": True}))
        finally:
            for process in [agent] + relays:
                if process and process.poll() is None:
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
                    process.wait(10)


if __name__ == "__main__":
    main()
