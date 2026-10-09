"""A quick tunnel outlives the program: after RemoteCli.exe is ended without its tunnel (what an update does, and
a crash), the next start takes the running tunnel over and the public address stays the same. Run after
tools/package_windows.py. Needs the network; uses a temporary data folder and port 8736.

    python tests/tunnel_keep_check.py <cloudflared.exe>
"""
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
PORT = 8736


def alive(pid):
    return b'"%d"' % pid in subprocess.run(["tasklist", "/FI", "PID eq %d" % pid, "/FO", "CSV", "/NH"], capture_output=True).stdout


def wait(what, check, seconds):
    deadline = time.time() + seconds
    while time.time() < deadline:
        found = check()
        if found:
            return found
        time.sleep(0.5)
    raise AssertionError(what)


def answers(url):
    try:
        with urllib.request.urlopen(url + "/api/session", timeout=10) as reply:
            return reply.status == 200
    except (urllib.error.URLError, OSError):
        return False


def main():
    temp = tempfile.TemporaryDirectory()
    data = Path(temp.name) / "data"
    data.mkdir()
    shutil.copy(sys.argv[1], data / "cloudflared.exe")
    (data / "config.json").write_text(json.dumps({"Mode": "cloud", "Port": PORT, "RemoteEnabled": True, "RemoteMaxMode": "full", "RemoteDirs": []}), encoding="utf-8")
    env = dict(os.environ, REMOTECLI_DATA=str(data))
    subprocess.run([str(STAGE / "RemoteCliAgent.exe"), "--set-password", str(data)], input=("test-" + secrets.token_hex(8)).encode(), check=True)
    kept = lambda: json.loads((data / "tunnel.json").read_text(encoding="utf-8")) if (data / "tunnel.json").is_file() else None
    start = lambda: subprocess.Popen([str(STAGE / "RemoteCli.exe"), "--hidden"], env=env, cwd=str(STAGE))
    app, tunnel = start(), 0
    try:
        first = wait("no quick tunnel address", kept, 90)
        tunnel = first["Pid"]
        wait("the new address does not answer", lambda: answers(first["Url"]), 90)
        subprocess.run(["taskkill", "/PID", str(app.pid), "/F"], capture_output=True)      # the program alone, not what it started
        app.wait(10)
        assert alive(tunnel), "the tunnel ended with the program"
        relay = json.loads((data / "children.json").read_text())[0]
        app = start()
        wait("the leftover relay was not replaced", lambda: not alive(relay), 30)
        wait("the tunnel was not taken over", lambda: (data / "children.json").is_file() and tunnel in json.loads((data / "children.json").read_text()) and len(json.loads((data / "children.json").read_text())) == 2, 40)
        assert alive(tunnel) and kept() == first, (kept(), first)
        wait("the kept address does not answer", lambda: answers(first["Url"]), 60)
        time.sleep(5)
        assert alive(tunnel) and answers(first["Url"]), "the kept tunnel did not last"
        print(json.dumps({"address_kept": True, "tunnel_pid": tunnel}))
    finally:
        subprocess.run(["taskkill", "/PID", str(app.pid), "/T", "/F"], capture_output=True)
        if tunnel:
            subprocess.run(["taskkill", "/PID", str(tunnel), "/F"], capture_output=True)
        for pid in (json.loads((data / "children.json").read_text()) if (data / "children.json").is_file() else []):
            subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
        time.sleep(1)
        try:
            temp.cleanup()
        except OSError:
            pass


if __name__ == "__main__":
    main()
