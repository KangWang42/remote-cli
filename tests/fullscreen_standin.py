"""A program that keeps the whole screen to itself, the way Codex and Claude Code do: it draws on the alternate
screen, asks for mouse reports, moves its own list by the wheel or the page keys, and draws only what changed.
tests/fullscreen_check.py runs it in place of Codex. Windows only.

    f   draws the list many times over, as hours of work do
    q   leaves
"""
import ctypes
import os
import re
import shutil
import sys
import threading
import time

if "--help" in sys.argv:
    print("stand-in for a fullscreen tool")
    sys.exit(0)

kernel = ctypes.windll.kernel32
kernel.GetStdHandle.restype = ctypes.c_void_p
kernel.SetConsoleMode(ctypes.c_void_p(kernel.GetStdHandle(-10)), 0x0200)             # keys and mouse reports arrive as the terminal sent them
kernel.SetConsoleMode(ctypes.c_void_p(kernel.GetStdHandle(-11)), 0x0001 | 0x0004)

TOTAL = 300
lock = threading.Lock()
size = shutil.get_terminal_size()
state = {"top": 0, "wheel": 0, "page": 0, "flood": 0, "other": ""}


def body():
    return max(1, size.lines - 2)


def put(text):
    os.write(1, text.encode("utf-8"))


def changed():
    """The list and the line under it. The heading above is not drawn again: it has not changed."""
    state["top"] = max(0, min(TOTAL - body(), state["top"]))
    # the pseudo terminal passes on only what differs from the last picture: a long run changes every line
    fill = " " + chr(97 + state["flood"] % 26) * min(30, size.columns - 12) if state["flood"] else ""
    rows = ["\x1b[%d;1H\x1b[2Kline %03d%s" % (at + 2, state["top"] + at, fill) for at in range(body())]
    status = "wheel=%d page=%d top=%03d flood=%d other=%s" % (state["wheel"], state["page"], state["top"], state["flood"], state["other"][-12:])
    return "".join(rows) + "\x1b[%d;1H\x1b[2K%s" % (size.lines, status[:size.columns - 1])


def whole():
    return "\x1b[2J\x1b[1;1HFULLSCREEN STAND-IN %dx%d" % (size.columns, size.lines) + changed()


def watch():
    global size
    while True:
        time.sleep(0.1)
        now = shutil.get_terminal_size()
        with lock:
            if now != size:
                size = now
                put(whole())


put("\x1b[?1049h\x1b[?1003;1006h\x1b[?25l")
state["top"] = TOTAL
put(whole())
threading.Thread(target=watch, daemon=True).start()
KEY = re.compile(rb"\x1b\[<(\d+);\d+;\d+[Mm]|\x1b\[([56])~|\x1b\[[0-9;?<>=]*[ -/]*[@-~]|([^\x1b])")
rest = b""
while True:
    data = os.read(0, 4096)
    if not data:
        break
    rest += data
    with lock:
        at = 0
        for key in KEY.finditer(rest):
            if key.start() != at:
                break
            at = key.end()
            button, page, plain = key.groups()
            if button in (b"64", b"65"):
                state["wheel"] += 1
                state["top"] += -1 if button == b"64" else 1
            elif page:
                state["page"] += 1
                state["top"] += (-1 if page == b"5" else 1) * body()
            elif plain == b"q":
                put("\x1b[?1003;1006l\x1b[?25h\x1b[?1049l")
                sys.exit(0)
            elif plain == b"f":
                for _ in range(600):
                    state["flood"] += 1
                    put(changed())
                    time.sleep(0.012)           # one picture at a time, as a program at work draws them
            elif plain:
                state["other"] += plain.decode("latin-1")
        rest = rest[at:] if len(rest) - at < 64 else b""
        put(changed())
