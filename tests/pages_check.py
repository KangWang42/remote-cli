"""Screenshots of the app's pages at a narrow width (headless Chrome does not go below 500 pixels), against a real relay and agent on this computer.

    python tests/pages_check.py <chrome.exe> <output folder> [project folder]

Signs in the way the app does (password after "#"), opens a PowerShell terminal, and saves list.png and
terminal.png. Uses a temporary data folder and port 8736.
"""
import http.client
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PORT = int(os.environ.get("RCLI_TEST_PORT", "8736"))
password = "test-" + secrets.token_hex(8)


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    temp = tempfile.TemporaryDirectory()
    data, work, profile = Path(temp.name) / "agent", Path(temp.name) / "work", Path(temp.name) / "profile"
    data.mkdir()
    if len(sys.argv) > 3:
        work = Path(sys.argv[3])       # a folder whose path may appear in a published screenshot
    work.mkdir(exist_ok=True)
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
    relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(PORT), "--data", str(Path(temp.name) / "relay")],
                             env=dict(os.environ, RCLI_PASSWORD=password, PYTHONUTF8="1"))
    agent_exe = ROOT / "agent-windows" / "bin" / "RemoteCliAgent.exe"
    subprocess.run([str(agent_exe), "--set-password", str(data)], input=password.encode(), check=True)
    agent = subprocess.Popen([str(agent_exe), "--data", str(data)])
    try:
        cookie = {}

        def call(path, payload=None):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=30)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == 200, (path, reply.status, text[:200])
            return json.loads(text)

        for _ in range(40):
            try:
                call("/api/login", {"password": password})
                break
            except OSError:
                time.sleep(0.5)
        for _ in range(60):
            if call("/api/terminal")["device"]["workspaces"]:
                break
            time.sleep(0.5)
        terminal = call("/api/terminal", {"action": "start", "id": secrets.token_hex(16), "tool": "shell", "dir": "demo"})["terminal"]
        time.sleep(4)
        call("/api/terminal", {"action": "input", "id": secrets.token_hex(16), "terminal": terminal, "data": "function prompt { 'PS demo> ' }; Clear-Host; Get-Date -Format 'yyyy-MM-dd'; 'hello from the computer'" + chr(13)})
        time.sleep(2)

        def shot(url, name, wait):
            subprocess.run([chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + str(profile),
                            "--window-size=500,900", "--virtual-time-budget=%d" % wait, "--screenshot=" + str(out / name), url],
                           check=True, capture_output=True, timeout=90)
        shot("http://127.0.0.1:%d/#p=%s" % (PORT, password), "signin.png", 6000)           # leaves the sign-in cookie in the profile
        shot("http://127.0.0.1:%d/" % PORT, "list.png", 6000)
        shot("http://127.0.0.1:%d/terminal/?id=%s" % (PORT, terminal), "terminal.png", 9000)
        print("saved", sorted(p.name for p in out.glob("*.png")))
    finally:
        agent.kill(); relay.kill()
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
