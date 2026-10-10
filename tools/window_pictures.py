"""Draws the Windows program's pages into docs/images/ for the README, with sample settings in a temporary
data folder (the real settings are not touched) and a relay for everyone that runs on this computer for the
moment. The windows stay off the screen. Run after tools/package_windows.py.

    python tools/window_pictures.py
"""
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROGRAM = ROOT / ".build" / "windows" / "stage" / "RemoteCli.exe"
# the page, and what is pressed on it: "public" chooses the relay in the relay tab
PAGES = {"windows.png": ["0", "public"], "windows-projects.png": ["1"], "windows-settings.png": ["3"]}


def main():
    public = Path(os.environ.get("PUBLIC", r"C:\Users\Public"))
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as data:
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            port = reserved.getsockname()[1]
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(port), "--data", str(Path(data, "relay")), "--public"],
                                 env=dict(os.environ, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
        try:
            time.sleep(2)
            listed = Path(data, "relays.json")
            listed.write_text(json.dumps({"relays": [{"name": "示例", "url": "http://127.0.0.1:%d" % port}]}), encoding="utf-8")
            settings = {"Mode": "lan", "Port": 8735, "UpdateCheckUtc": "2099-01-01T00:00:00.0000000Z",
                        "RemoteDirs": ["web-shop=%s" % (public / "Documents"), "thesis=%s" % (public / "Pictures"), "data-tools=%s" % (public / "Music")]}
            Path(data, "config.json").write_text(json.dumps(settings), encoding="utf-8")
            for name, pressed in PAGES.items():
                picture = ROOT / "docs" / "images" / name
                subprocess.run([str(PROGRAM), "--screenshot", str(picture), *pressed], env=dict(os.environ, REMOTECLI_DATA=data, REMOTECLI_RELAYS=str(listed)), check=True, timeout=120)
                Path(str(picture) + ".txt").unlink(missing_ok=True)      # what the tests read; no part of the pictures
                print(name)
        finally:
            relay.kill()


if __name__ == "__main__":
    main()
