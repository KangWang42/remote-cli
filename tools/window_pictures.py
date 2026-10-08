"""Draws the Windows program's pages into docs/images/ for the README, with sample settings in a temporary
data folder (the real settings are not touched). Run after tools/package_windows.py.

    python tools/window_pictures.py
"""
import json
import os
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROGRAM = ROOT / ".build" / "windows" / "stage" / "RemoteCli.exe"
PAGES = {"windows.png": 0, "windows-projects.png": 1, "windows-settings.png": 3}


def main():
    public = Path(os.environ.get("PUBLIC", r"C:\Users\Public"))
    with tempfile.TemporaryDirectory() as data:
        settings = {"Mode": "own", "OwnServer": "https://cli.example.com", "Port": 8735, "UpdateCheckUtc": "2099-01-01T00:00:00.0000000Z",
                    "RemoteDirs": ["web-shop=%s" % (public / "Documents"), "thesis=%s" % (public / "Pictures"), "data-tools=%s" % (public / "Music")]}
        Path(data, "config.json").write_text(json.dumps(settings), encoding="utf-8")
        for name, page in PAGES.items():
            subprocess.run([str(PROGRAM), "--screenshot", str(ROOT / "docs" / "images" / name), str(page)], env=dict(os.environ, REMOTECLI_DATA=data), check=True, timeout=60)
            print(name)


if __name__ == "__main__":
    main()
