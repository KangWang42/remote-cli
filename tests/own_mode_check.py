"""The "自有中转" tab of the Windows program, as installed (run after tools/package_windows.py): opening the tab
changes nothing, a relay that refuses the password leaves the connection in use as it was and says why, and a
relay that accepts it is switched to. The program presses its own controls (--screenshot ... own) and writes
what came of it; the relay of one's own is one started here. Uses temporary data folders and ports 8744 and 8745.

    python tests/own_mode_check.py [folder for the pictures]
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

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / ".build" / "windows" / "stage"
OWN, LOCAL = 8745, 8744
relay_password, local_password = "relay-" + secrets.token_hex(8), "local-" + secrets.token_hex(8)


def listening(port):
    try:
        socket.create_connection(("127.0.0.1", port), 1).close()
        return True
    except OSError:
        return False


def press(folder, name, *typed, mode="cloud", own_server=""):
    """Starts the program with its own data folder, opens the tab and types what is given; returns what it wrote."""
    data = Path(folder) / name
    data.mkdir()
    # "cloud" without the tunnel program: the program's own relay listens on this computer only.
    (data / "config.json").write_text(json.dumps({"Mode": mode, "Port": LOCAL, "OwnServer": own_server, "RemoteEnabled": True}), encoding="utf-8")
    subprocess.run([str(STAGE / "RemoteCliAgent.exe"), "--set-password", str(data)], input=local_password.encode(), check=True)
    picture = Path(folder) / (name + ".png")
    subprocess.run([str(STAGE / "RemoteCli.exe"), "--screenshot", str(picture), "0", "own", *typed], env=dict(os.environ, REMOTECLI_DATA=str(data)), cwd=str(STAGE), check=True, timeout=120)
    said = dict(line.split("=", 1) for line in Path(str(picture) + ".txt").read_text(encoding="utf-8").splitlines())
    said["config"] = json.loads((data / "config.json").read_text(encoding="utf-8-sig"))
    for _ in range(20):         # the program's relay goes with it
        if not listening(LOCAL):
            break
        time.sleep(0.5)
    return said


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    with tempfile.TemporaryDirectory(prefix="remote-cli-own-mode-") as folder:
        relay = subprocess.Popen([sys.executable, str(ROOT / "relay" / "server.py"), "--port", str(OWN), "--data", str(Path(folder) / "relay")],
                                 env=dict(os.environ, RCLI_PASSWORD=relay_password, PYTHONUTF8="1"), stdout=subprocess.DEVNULL)
        try:
            for _ in range(40):
                if listening(OWN):
                    break
                time.sleep(0.25)
            address = "http://127.0.0.1:%d" % OWN
            # only looking at the tab: the way in use goes on, and the two boxes can be typed into
            looked = press(folder, "looked")
            assert (looked["mode"], looked["tab"], looked["own_relay"], looked["boxes"]) == ("cloud", "2", "running", "typed"), looked
            assert looked["connect_button"] == "True" and looked["new_password_button"] == "False" and "连上之前" in looked["about"], looked
            assert looked["config"]["Mode"] == "cloud", looked["config"]
            # a password the relay refuses: said so, nothing switched, the program's own relay still there
            refused = press(folder, "refused", address, "not-the-password")
            assert (refused["mode"], refused["own_relay"]) == ("cloud", "running") and "密码" in refused["about"] and "没有变" in refused["about"], refused
            assert refused["config"]["Mode"] == "cloud" and not refused["config"].get("OwnServer"), refused["config"]
            # an address without https:// in front, and a relay that is not there
            for typed, word in ((("127.0.0.1:%d" % OWN, relay_password), "https://"), (("http://127.0.0.1:1", relay_password), "连不上中转")):
                wrong = press(folder, "wrong-" + word.strip(":/"), *typed)
                assert wrong["mode"] == "cloud" and wrong["own_relay"] == "running" and word in wrong["about"], wrong
            # the right password: switched, said, and kept for the next start
            accepted = press(folder, "accepted", address, relay_password)
            assert (accepted["mode"], accepted["tab"], accepted["own_relay"]) == ("own", "2", "stopped"), accepted
            assert accepted["status"].startswith("已连上自有中转") and accepted["address"] == address, accepted
            assert accepted["config"]["Mode"] == "own" and accepted["config"]["OwnServer"] == address, accepted["config"]
            if out:
                out.mkdir(parents=True, exist_ok=True)
                for name in ("looked", "refused", "accepted"):
                    (out / ("own-" + name + ".png")).write_bytes((Path(folder) / (name + ".png")).read_bytes())
            print("own relay tab: opening it changes nothing; a refused password, a bad address and an absent relay leave the connection as it was and say why; an accepted one is switched to")
        finally:
            relay.kill()


if __name__ == "__main__":
    main()
