"""The packaged Windows program, as installed: RemoteCli.exe starts its own relay with the packed Python and
serves a PowerShell terminal. Run after tools/package_windows.py. Uses a temporary data folder and port 8734.

    python tests/e2e_packaged.py [screenshot.png]
"""
import ctypes
import http.client
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
PORT = 8734
password = "test-" + secrets.token_hex(8)
new_id = lambda: secrets.token_hex(16)


def window_rect(pid):
    """Screen rectangle of the visible top-level window of a process, or None."""
    user = ctypes.windll.user32
    user.SetProcessDPIAware()       # real pixels, so the rectangle matches what a screenshot sees
    found = []

    def each(handle, _):
        owner = wintypes.DWORD()
        user.GetWindowThreadProcessId(handle, ctypes.byref(owner))
        if owner.value == pid and user.IsWindowVisible(handle):
            rect = wintypes.RECT()
            user.GetWindowRect(handle, ctypes.byref(rect))
            if rect.right - rect.left > 200:
                user.SetForegroundWindow(handle)
                found.append((rect.left, rect.top, rect.right, rect.bottom))
        return True
    user.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(each), 0)
    return found[0] if found else None


def main():
    temp = tempfile.TemporaryDirectory()
    data, work = Path(temp.name) / "data", Path(temp.name) / "work"
    data.mkdir(); work.mkdir()
    # "cloud" without the tunnel program: the relay listens on this computer only, so no firewall question appears.
    (data / "config.json").write_text(json.dumps({"Mode": "cloud", "Port": PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": ["demo=" + str(work)]}), encoding="utf-8")
    env = dict(os.environ, REMOTECLI_DATA=str(data))
    subprocess.run([str(STAGE / "RemoteCliAgent.exe"), "--set-password", str(data)], input=password.encode(), check=True)
    app = subprocess.Popen([str(STAGE / "RemoteCli.exe")], env=env, cwd=str(STAGE))
    report = {}
    try:
        cookie = {}

        def call(path, payload=None, expect=200):
            connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=30)
            body = None if payload is None else json.dumps(payload).encode()
            connection.request("POST" if body is not None else "GET", path, body=body, headers=dict(cookie, **({"Content-Type": "application/json"} if body is not None else {})))
            reply = connection.getresponse()
            text = reply.read()
            if reply.getheader("Set-Cookie"):
                cookie["Cookie"] = reply.getheader("Set-Cookie").split(";")[0]
            assert reply.status == expect, (path, reply.status, text[:200])
            connection.close()
            return json.loads(text)

        deadline = time.time() + 30
        while True:
            try:
                call("/api/session")
                break
            except OSError:
                assert time.time() < deadline, "the packed relay did not start"
                time.sleep(0.5)
        call("/api/login", {"password": password})
        deadline = time.time() + 40
        while time.time() < deadline:
            device = call("/api/terminal")["device"]
            if device["online"] and device["workspaces"]:
                break
            time.sleep(0.5)
        assert device["online"] and device["workspaces"] == ["demo"], device
        report["device"] = {k: device[k] for k in ("online", "enabled", "workspaces", "tools")}
        terminal = call("/api/terminal", {"action": "start", "id": new_id(), "tool": "shell", "dir": "demo"})["terminal"]
        after, text, sent = 0, "", False
        deadline = time.time() + 40
        while time.time() < deadline and "PACKED_OK" not in text:
            item = call("/api/terminal?terminal=%s&after=%d&wait=5" % (terminal, after))
            after = item["after"]
            text += "".join(c["data"] for c in item["chunks"])
            if item["terminal"]["state"] == "running" and not sent and text:
                time.sleep(1.5)
                call("/api/terminal", {"action": "input", "id": new_id(), "terminal": terminal, "data": "'PACKED_'+'OK'\r"})
                sent = True
        assert "PACKED_OK" in text, text[-300:]
        report["echo"] = True
        call("/api/terminal", {"action": "close", "id": new_id(), "terminal": terminal})
        children = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-CimInstance Win32_Process -Filter 'ParentProcessId=%d').Name" % app.pid], capture_output=True, text=True).stdout.split()
        report["children"] = sorted(set(n for n in children if n.endswith(".exe")))
        rect = window_rect(app.pid)
        report["window"] = rect
        if rect and len(sys.argv) > 1:
            time.sleep(0.8)
            from PIL import ImageGrab
            ImageGrab.grab(bbox=rect).save(sys.argv[1])
        print(json.dumps(report, ensure_ascii=False))
    finally:
        subprocess.run(["taskkill", "/PID", str(app.pid), "/T", "/F"], capture_output=True)
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
