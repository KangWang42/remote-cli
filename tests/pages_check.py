"""Screenshots of the app's pages at a narrow width (headless Chrome does not go below 500 pixels), against a
real relay and agent on this computer, with three projects and tasks in different states.

    python tests/pages_check.py <chrome.exe> <output folder> [folder for the demo projects]

Signs in the way the app does (password after "#") and saves overview.png, project.png and terminal.png.
Uses a temporary data folder and port 8736 (RCLI_TEST_PORT changes it; RCLI_TEST_SKIN chooses a skin).
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
new_id = lambda: secrets.token_hex(16)


def main():
    chrome, out = sys.argv[1], Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    temp = tempfile.TemporaryDirectory()
    data, profile = Path(temp.name) / "agent", Path(temp.name) / "profile"
    base = Path(sys.argv[3]) if len(sys.argv) > 3 else Path(temp.name) / "projects"       # a path that may appear in a published picture
    names = ["web-shop", "thesis", "data-tools"]
    for name in names:
        (base / name).mkdir(parents=True, exist_ok=True)
    data.mkdir()
    (data / "config.json").write_text(json.dumps({"Server": "http://127.0.0.1:%d" % PORT, "RemoteEnabled": True, "RemoteMaxMode": "full",
                                                  "RemoteDirs": ["%s=%s" % (name, base / name) for name in names]}), encoding="utf-8")
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
            if len(call("/api/terminal")["device"]["workspaces"]) == 3:
                break
            time.sleep(0.5)

        def shell(project, title, line):
            terminal = call("/api/terminal", {"action": "start", "id": new_id(), "tool": "shell", "dir": project})["terminal"]
            time.sleep(3.5)
            call("/api/terminal", {"action": "rename", "id": new_id(), "terminal": terminal, "title": title})
            call("/api/terminal", {"action": "input", "id": new_id(), "terminal": terminal, "data": "function prompt { 'PS %s> ' }; Clear-Host; %s" % (project, line) + chr(13)})
            return terminal

        # one that asks, one that works, one that has finished, one that only waits
        asking = shell("web-shop", "部署到测试环境", "'Deploying to staging'; 'Do you want to proceed?'; '> 1. Yes'; '  2. No'; $null = Read-Host")
        shell("thesis", "整理第三章的参考文献", "1..600 | ForEach-Object { 'checking reference ' + $_; Start-Sleep -Milliseconds 400 }")
        shell("web-shop", "修复结算页的金额显示", "1..4 | ForEach-Object { 'running test ' + $_; Start-Sleep -Milliseconds 500 }; 'all tests passed'")
        first = shell("data-tools", "PowerShell", "Get-Date -Format 'yyyy-MM-dd'; 'hello from the computer'")
        time.sleep(9)
        phases = sorted((t["title"], t.get("phase")) for t in call("/api/terminal")["terminals"])
        print("phases", json.dumps(phases, ensure_ascii=False))

        def shot(url, name, wait):
            subprocess.run([chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + str(profile),
                            "--window-size=500,960", "--virtual-time-budget=%d" % wait, "--screenshot=" + str(out / name), url],
                           check=True, capture_output=True, timeout=90)
        skin = os.environ.get("RCLI_TEST_SKIN", "")
        look = "?skin=" + skin if skin else ""
        shot("http://127.0.0.1:%d/#p=%s" % (PORT, password), "signin.png", 6000)           # leaves the sign-in cookie in the profile
        shot("http://127.0.0.1:%d/%s" % (PORT, look), "overview.png", 6000)
        shot("http://127.0.0.1:%d/?project=web-shop" % PORT, "project.png", 6000)
        shot("http://127.0.0.1:%d/terminal/?id=%s" % (PORT, first), "terminal.png", 12000)
        (out / "signin.png").unlink()
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
