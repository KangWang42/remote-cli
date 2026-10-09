"""Relay for persistent terminals on the owner's computer. The HTTP layer in server.py does the authentication.

The computer makes outbound HTTPS requests. Operation IDs and output sequence numbers
make retrying a dropped response safe; input is never silently executed twice.
"""
import collections
import copy
import json
import os
import re
import threading
import time
import uuid

import screen as _screen



class RemoteError(ValueError):
    """A request the relay refuses; its text is shown to the person at the phone."""


# The list of terminals is one small file. The output of each terminal is a file of its own, one piece per line,
# and saving adds only the new pieces: with megabytes of history kept, writing all of it again every few seconds
# held up every key and every line for as long as the writing took.
_logged = {}  # (path, terminal) -> [seq of the last piece in its file, characters in the file]


def _folder(path):
    return os.path.splitext(path)[0] + "-output"


def _write(path, text, mode="w"):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | (os.O_APPEND if mode == "a" else os.O_TRUNC), 0o600), mode, encoding="utf-8", newline="\n") as stream:
        stream.write(text)


def _replace(tmp, path):
    for attempt in range(6):
        try:
            return os.replace(tmp, path)
        except PermissionError:     # Windows: a virus scanner or the indexer is reading the file at this moment
            if attempt == 5:
                raise
            time.sleep(0.01)


def _lines(chunks):
    return "".join(json.dumps(c, ensure_ascii=False) + "\n" for c in chunks)


def _load(path):
    try:
        with open(path, encoding="utf-8") as stream:
            state = json.load(stream)
        if not isinstance(state, dict) or not isinstance(state.get("threads"), dict):
            raise ValueError
    except (OSError, ValueError):
        return {"threads": {}}
    for key, term in state["threads"].items():
        _logged.pop((path, key), None)
        if "output" not in term:        # a list from before this arrangement carries the output itself
            chunks, whole = [], True
            try:
                with open(os.path.join(_folder(path), key + ".jsonl"), encoding="utf-8", newline="\n") as stream:
                    for line in stream:
                        try:
                            chunk = json.loads(line)
                            if type(chunk["seq"]) is not int or not isinstance(chunk["data"], str) or (chunks and chunk["seq"] <= chunks[-1]["seq"]):
                                raise ValueError
                        except (ValueError, KeyError, TypeError):
                            whole = False       # a line cut off by a crash: what came before it is kept
                            break
                        chunks.append({"seq": chunk["seq"], "data": chunk["data"]})
            except OSError:
                whole = False
            term["output"] = chunks
            if chunks:
                term["seq"] = max(term.get("seq", 0), chunks[-1]["seq"])
            if whole:
                _logged[(path, key)] = [chunks[-1]["seq"] if chunks else 0, sum(len(c["data"]) for c in chunks)]
        term["size"] = sum(len(c["data"]) for c in term["output"])
    return state


def _save(path, state):
    folder = _folder(path)
    os.makedirs(folder, exist_ok=True)
    for key, term in state["threads"].items():
        output, kept, file = term.get("output", []), _logged.get((path, key)), os.path.join(folder, key + ".jsonl")
        last = output[-1]["seq"] if output else 0
        # Added to while the file continues where the kept output goes on; written again when it does not, and when
        # the file has grown to twice what is still kept.
        if kept and kept[0] <= last and (not output or output[0]["seq"] <= kept[0] + 1) and kept[1] <= 2 * term.get("size", 0) + 100_000:
            start = len(output)
            while start > 0 and output[start - 1]["seq"] > kept[0]:
                start -= 1
            if start < len(output):
                _write(file, _lines(output[start:]), "a")
                kept[0], kept[1] = last, kept[1] + sum(len(c["data"]) for c in output[start:])
        elif not kept or kept != [last, term.get("size", 0)]:
            _write(file + ".tmp", _lines(output))
            _replace(file + ".tmp", file)
            _logged[(path, key)] = [last, sum(len(c["data"]) for c in output)]
    _write(path + ".tmp", json.dumps({"threads": {key: {k: v for k, v in term.items() if k != "output"} for key, term in state["threads"].items()}}, ensure_ascii=False))
    _replace(path + ".tmp", path)
    for name in os.listdir(folder):
        if name.endswith(".jsonl") and name[:-6] not in state["threads"]:
            _logged.pop((path, name[:-6]), None)
            try:
                os.remove(os.path.join(folder, name))
            except OSError:
                pass

_lock = threading.Lock()
_changed = threading.Condition(_lock)  # new output, a new state or a new operation: waiting requests look again
_unsaved = {}  # path -> time of the oldest output not yet written to disk
_tick = [0]    # counts every wake-up, so a waiter can tell that one happened between its look and its wait
SAVE_SECONDS = 2
_cache = {}


class _Operations(collections.OrderedDict):
    """Operations by their id, oldest first, kept a while so that a repeated request gets the same answer. The few
    the computer has not carried out yet are kept beside them: a phone that scrolls sends dozens a second, and
    going through all of them at every request made the relay slower the longer it was used."""

    def __init__(self):
        super().__init__()
        self.queued = {}

    def clear(self):
        super().clear()
        self.queued.clear()


_pending = _Operations()
_id = re.compile(r"^[a-f0-9]{16,32}$")
_session = re.compile(r"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$")
_device = {"seen": 0, "instance": "", "enabled": False, "workspaces": [], "tools": [], "projects": [], "candidates": []}
PROJECT_ACTIONS = ("project_add", "project_remove", "project_rename")
UPDATE_ACTION = "update"      # the program on the computer looks for a newer version of itself and installs it
_sessions = []  # conversations saved on the computer, as its last report listed them
MAX_TERMINALS, MAX_HISTORY, OUTPUT_LIMIT, CLOSED_LIMIT = 8, 12, 2_000_000, 300_000
LABELS = {"claude": "Claude Code", "codex": "Codex", "shell": "PowerShell"}
OP_SECONDS = 120


def _state(path):
    if path not in _cache:
        state = _load(path)
        changed = False
        # Starts awaiting delivery are in memory. Never replay them after a server restart.
        for term in state["threads"].values():
            if term.get("state") == "starting" and not any(
                    op["terminal"] == term["id"]
                    for op in _pending.queued.values()):
                term.update(state="closed", error="中转服务重启前的启动未完成，请重新打开终端")
                changed = True
            changed = _trim(term) or changed
        _cache[path] = state
        if changed:
            _save(path, state)
    return _cache[path]


def _name(value, limit=40):
    return isinstance(value, str) and 0 < len(value.strip()) <= limit and not any(ord(c) < 32 for c in value)


def _folders(items, limit):
    """Folders as the computer reports them; the relay only shows them and hands the chosen one back."""
    out = []
    for item in items[:limit] if isinstance(items, list) else []:
        if isinstance(item, dict) and _name(item.get("name"), 60) and _name(item.get("path"), 260):
            out.append({"name": item["name"], "path": item["path"], "fixed": item.get("fixed") is True, "exists": item.get("exists") is not False,
                        "updated": item["updated"] if type(item.get("updated")) is int else 0, "live": item.get("live") is True,
                        "tools": [t for t in str(item.get("tools", "")).split() if t in ("claude", "codex")]})
    return out


def _trim(term):
    """A running screen keeps its recent history; an ended one keeps only its final screens."""
    limit = OUTPUT_LIMIT if term.get("state") in ("starting", "running") else CLOSED_LIMIT
    output = term.get("output", [])
    if "size" not in term:
        term["size"] = sum(len(c["data"]) for c in output)
    # The size is kept as pieces come and go: counting every piece again for each new one took longer the more there were.
    size, drop = term["size"], 0
    while size > limit and drop < len(output) - 1:
        size -= len(output[drop]["data"])
        drop += 1
    del output[:drop]
    term["size"] = size
    return drop > 0


def _view(now):
    return {**_device, "online": now - _device["seen"] < 15}


# What a terminal is doing, for the lists on the phone:
#   starting  not running yet            busy     the program is working
#   confirm   it asks a yes/no question   idle     it waits for the next message ("done": it worked before)
#   ended     it has ended                failed   it ended with an error code
_ESCAPES = re.compile(r"\x1b\[[0-9;?<=>]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|[\x00-\x08\x0b-\x1f]")
# Questions the tools draw when they need a decision, compared without spaces and in lower case.
_ASKS = ("doyouwantto", "wouldyouliketo", "1.yes", "allowcommand", "yes,proceed", "approve?", "[y/n]", "(y/n)", "[y]yes", "[y]是", "presstoconfirm", "enter确认")
QUIET_SECONDS, ACTIVE_SECONDS = 1.5, 4
# Output that is only the screen being drawn again is not work: what a program prints before it has been given
# anything, the redraw after the phone changed the size of the screen, and the echo of keys without Enter.
REDRAW_SECONDS, ECHO_SECONDS = 3, 1.2


def _asks(term):
    text, size = [], 0
    for chunk in reversed(term.get("output", [])):
        text.append(chunk["data"])
        size += len(chunk["data"])
        if size >= 6000:
            break
    tail = "".join(_ESCAPES.sub("", "".join(reversed(text))).split()).lower()[-900:]
    return any(ask in tail for ask in _ASKS)


def _phase(term, now):
    if term["state"] == "starting":
        return "starting"
    if term["state"] != "running":
        return "failed" if term.get("exit_code") not in (None, 0) else "ended"
    quiet = now - term.get("out_at", 0)
    # Claude Code says itself whether it is working; for the others, fresh output means work.
    working = term.get("status") == "busy" if term.get("status") else quiet < ACTIVE_SECONDS
    if quiet >= QUIET_SECONDS and (working or not term.get("status")) and _asks(term):
        return "confirm"
    return "busy" if working else "idle"


def _track(term, now):
    """Keeps the phase of a terminal up to date; returns whether it changed."""
    phase = _phase(term, now)
    if phase == term.get("phase"):
        return False
    if phase == "idle":
        term["done"] = term.get("phase") in ("busy", "confirm")      # it finished something, as opposed to never having started
    term["phase"], term["phase_at"] = phase, int(now * 1000)
    return True


_screens = {}       # terminal -> (instance, Screen): rebuilt from the kept output when it is missing


def _said(term):
    """One line of what a terminal's program last said or did, for the lists. The screen is brought up to date only
    when someone looks, so a terminal nobody watches costs nothing."""
    output = term.get("output") or []
    if not output:
        return ""
    kept = _screens.get(term["id"])
    if not kept or kept[0] != term.get("instance") or kept[1].seq > term["seq"] or kept[1].seq < output[0]["seq"] - 1:
        kept = _screens[term["id"]] = (term.get("instance"), _screen.Screen(term.get("cols", 80), term.get("rows", 24)))
        size, start = 0, len(output)
        while start > 0 and size < 200_000:         # the last screens are enough to draw the present one
            start -= 1
            size += len(output[start]["data"])
        kept[1].seq = output[start]["seq"] - 1
    view = kept[1]
    if view.seq < term["seq"]:
        start, size = len(output), 0
        while start > 0 and output[start - 1]["seq"] > view.seq and size < 200_000:
            start -= 1
            size += len(output[start]["data"])
        if start > 0 and output[start - 1]["seq"] > view.seq:      # nobody looked for a long time: again from the last screens
            view = _screen.Screen(term.get("cols", 80), term.get("rows", 24))
            _screens[term["id"]] = (term.get("instance"), view)
    view.resize(term.get("cols", 80), term.get("rows", 24))
    if view.seq < term["seq"]:
        for chunk in output[start:]:
            view.feed(chunk["data"])
        view.seq = term["seq"]
        view.text = view.said()
    return view.text


def _public(term):
    shown = {k: v for k, v in term.items() if k not in ("output", "instance", "size", "out_at", "touched", "calm_until")}
    # Until the owner names a terminal, it carries the name of the conversation it has open.
    if not term.get("renamed"):
        shown["title"] = next((s["title"] for s in _sessions if s["id"] == term.get("session")), term["title"])
    return shown


def _session_activity(session):
    """Classify a saved conversation without treating a writer lock as a window.

    The agent can prove an independent CLI owner (``host=cli``), but a shared
    app-server, another remote terminal, or an unknown lock has no visible
    computer window that this app can locate.  Keep those records available for
    history/fork actions while keeping them out of the active count.
    """
    if not session.get("live"):
        return "history"
    return "active" if session.get("host") == "cli" else "locked"


def _public_session(session, terminal=""):
    shown = dict(session)
    shown["activity"] = _session_activity(session)
    shown["terminal"] = terminal
    return shown


def _expire(now):
    for op in [o for o in _pending.queued.values() if now - o["at"] > OP_SECONDS]:
        op.update(state="error", error="电脑未及时接收，操作没有执行，请重试")
        del _pending.queued[op["id"]]
        for path, state in _cache.items():
            term = state["threads"].get(op["terminal"])
            if term:
                term["error"] = op["error"]
                if op["payload"]["action"] == "start":
                    term["state"] = "closed"
                    _trim(term)
                _save(path, state)
    while _pending and now - next(iter(_pending.values()))["at"] > 600:
        _pending.queued.pop(_pending.popitem(last=False)[0], None)


def overview(path, terminal="", after=0, now=None, wait=0):
    """The list, or one terminal's output after `after`. With `wait`, an answer that has nothing new is held back
    until there is output or the terminal changed, at most that many seconds."""
    deadline = time.monotonic() + min(max(wait, 0), 25)
    first = None
    while True:
        result = _overview(path, terminal, after, time.time() if now is None else now)
        if not terminal:
            return result
        if deadline <= time.monotonic():
            result.pop("tick", None)
            return result
        mark = json.dumps([result["terminal"], result["device"]["online"], result["device"]["enabled"]], sort_keys=True)
        first = mark if first is None else first
        remaining = deadline - time.monotonic()
        tick = result.pop("tick")
        if result["chunks"] or result["reset"] or mark != first or remaining <= 0:
            return result
        _wait(tick, remaining)


def _wait(seen, seconds):
    """Sleeps until the next wake-up, unless one already happened after the caller looked."""
    with _changed:
        if _tick[0] == seen:
            _changed.wait(min(seconds, 5))


def _wake():
    _tick[0] += 1
    _changed.notify_all()


def stream(path, terminal, after=0, seconds=55, beat=15):
    """Yields a terminal's output as it arrives, for about `seconds`; the reader then asks again from where it is.
    A line without output goes out when the terminal's state changes, and every `beat` seconds so that the reader
    and anything between it and the relay can tell the response is alive."""
    deadline = time.monotonic() + seconds
    sent, last = None, time.monotonic()
    while True:
        result = _overview(path, terminal, after, time.time())
        tick = result.pop("tick")
        mark = json.dumps([result["terminal"], result["device"]["online"], result["device"]["enabled"]], sort_keys=True)
        now = time.monotonic()
        if result["chunks"] or result["reset"] or mark != sent or now - last >= beat:
            sent, last, after = mark, now, result["after"]
            yield result
            if result["chunks"] and after < result["terminal"]["seq"]:
                continue        # more is waiting: no pause between the pieces
        if now >= deadline:
            return
        _wait(tick, min(deadline - now, beat - (now - last)) + 0.01)


def _overview(path, terminal, after, now):
    with _lock:
        _expire(now)
        state = _state(path)
        if not terminal:
            attached = {t.get("session"): t["id"] for t in state["threads"].values() if t["state"] in ("starting", "running")}
            for gone in [key for key in _screens if key not in state["threads"]]:
                del _screens[gone]
            return {"device": _view(now), "terminals": sorted(
                [dict(_public(t), said=_said(t) if t["state"] == "running" else "") for t in state["threads"].values()], key=lambda t: -t["created"]),
                "sessions": [_public_session(s, attached.get(s["id"], "")) for s in _sessions]}
        term = state["threads"].get(terminal)
        if not term:
            raise RemoteError("终端已不存在")
        chunks = term.get("output", [])
        reset = bool(chunks and (after < chunks[0]["seq"] - 1 or after > term.get("seq", 0)))
        # A reader is nearly always at the end: the new pieces are found from there, not by going through all of them.
        start = 0 if reset else len(chunks)
        while start > 0 and chunks[start - 1]["seq"] > after:
            start -= 1
        # Bound each mobile reply, without skipping the remaining output.
        out, length = [], 0
        for at in range(start, len(chunks)):
            out.append(dict(chunks[at]))
            length += len(chunks[at]["data"])
            if length >= 180_000:
                break
        # A terminal's page needs to know only whether the computer is there; the lists of folders stay with the list.
        return {"device": {"online": now - _device["seen"] < 15, "enabled": _device["enabled"]}, "terminal": _public(term), "chunks": out, "reset": reset,
                "after": out[-1]["seq"] if out else term.get("seq", 0), "tick": _tick[0]}


def command(path, payload, now=None):
    now = time.time() if now is None else now
    if not isinstance(payload, dict):
        raise RemoteError("请求格式无效")
    op_id, action = payload.get("id"), payload.get("action")
    if not isinstance(op_id, str) or not _id.fullmatch(op_id):
        raise RemoteError("操作编号无效")
    with _lock:
        _expire(now)
        old = _pending.get(op_id)
        if old:
            if old["payload"] != payload:
                raise RemoteError("操作编号已被其它操作使用")
            return {k: old[k] for k in ("id", "terminal", "state", "error")}
        state = _state(path)
        if not _view(now)["online"] or not _device["enabled"]:
            raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
        terminal = payload.get("terminal", "")
        if action == UPDATE_ACTION:
            terminal = ""
            if "update" not in _device.get("features", []):
                raise RemoteError("电脑端版本不支持从手机更新，请先在电脑上更新一次")
        elif action in PROJECT_ACTIONS:
            # The computer keeps the list of project folders and checks the folder itself; this only refuses malformed requests.
            terminal = ""
            if action == "project_add":
                if not _name(payload.get("path"), 240) or payload.get("create", False) not in (True, False) or not (payload.get("name", "") == "" or _name(payload.get("name"))):
                    raise RemoteError("文件夹路径或名称无效")
            else:
                project = next((x for x in _device["projects"] if x["name"] == payload.get("name")), None)
                if not project:
                    raise RemoteError("没有这个项目")
                if project["fixed"]:
                    raise RemoteError("这个项目写在电脑的配置文件里，请在电脑上修改")
                if action == "project_rename" and not _name(payload.get("to")):
                    raise RemoteError("名称需为 1 至 40 字")
        elif action == "start":
            tool, folder = payload.get("tool"), payload.get("dir")
            if tool not in _device["tools"] or folder not in _device["workspaces"]:
                raise RemoteError("电脑没有报告这个工具或目录")
            previous = payload.get("previous", "")
            if previous and (not isinstance(previous, str) or not _id.fullmatch(previous)):
                raise RemoteError("历史对话编号无效")
            if any(type(payload.get(k, False)) is not bool for k in ("history", "takeover", "fork")):
                raise RemoteError("历史选项无效")
            session = payload.get("session", "")
            if payload.get("history") and not session and not previous:
                raise RemoteError("请在项目中选择具体的历史对话；新建终端不会打开 resume 选择器")
            saved = None
            fork = payload.get("fork", False)
            if fork and (tool != "codex" or not session or payload.get("takeover") or "codex-fork" not in _device.get("features", [])):
                raise RemoteError("电脑端不支持这个副本选项，请更新电脑后台")
            if session:
                saved = next((s for s in _sessions if s["id"] == session and s["tool"] == tool and s["dir"] == folder), None)                     if isinstance(session, str) and _session.fullmatch(session) else None
                if not saved:
                    raise RemoteError("电脑上没有找到这个对话，请刷新后重试")
                if saved.get("live") and not fork and not payload.get("takeover"):
                    raise RemoteError("原对话仍被占用，请选择接管原对话，先结束原终端")
                if not fork and any(t.get("session") == session and t["state"] in ("starting", "running") for t in state["threads"].values()):
                    raise RemoteError("这个对话已经在手机终端里打开")
            if sum(t["state"] in ("starting", "running") for t in state["threads"].values()) >= MAX_TERMINALS:
                raise RemoteError("最多同时运行 8 个终端，请先结束一个")
            terminal = uuid.uuid4().hex
            term = {"id": terminal, "tool": tool, "dir": folder, "title": LABELS[tool] + (" · 副本" if fork else ""), "session": "" if fork else session, "status": "",
                    "created": int(now * 1000), "state": "starting", "error": "", "seq": 0, "output": [], "size": 0, "cols": 80, "rows": 24,
                    "instance": _device["instance"], "previous": previous, "history": bool(payload.get("history")), "touched": False}
            state["threads"][terminal] = term
            while len(state["threads"]) > MAX_HISTORY:
                closed = [t for t in state["threads"].values() if t["state"] not in ("starting", "running")]
                if not closed:
                    break
                del state["threads"][min(closed, key=lambda t: t["created"])["id"]]
        else:
            term = state["threads"].get(terminal) if isinstance(terminal, str) else None
            if not term:
                raise RemoteError("终端已不存在")
            if action == "rename":
                title = payload.get("title")
                if not isinstance(title, str) or not title.strip() or len(title) > 60 or any(ord(c) < 32 for c in title):
                    raise RemoteError("名称需为 1 至 60 字")
                term.update(title=title.strip(), renamed=True)
                _save(path, state)
                return {"id": op_id, "terminal": terminal, "state": "done", "error": ""}
            if term["state"] != "running":
                raise RemoteError("终端已结束或尚未启动")
            if action == "input":
                data = payload.get("data")
                if not isinstance(data, str) or not data or len(data) > 16000:
                    raise RemoteError("输入为空或超过 16000 字")
                term["touched"] = True
                if "\r" not in data and "\n" not in data:
                    term["calm_until"] = max(term.get("calm_until", 0), now + ECHO_SECONDS)
            elif action == "resize":
                if any(type(payload.get(k)) is not int for k in ("cols", "rows")) or not (20 <= payload["cols"] <= 240 and 6 <= payload["rows"] <= 100):
                    raise RemoteError("终端尺寸无效")
                term["calm_until"] = max(term.get("calm_until", 0), now + REDRAW_SECONDS)
            elif action != "close":
                raise RemoteError("不支持此操作")
        op = {"id": op_id, "terminal": terminal, "state": "queued", "error": "", "at": now, "payload": copy.deepcopy(payload)}
        _pending[op_id] = _pending.queued[op_id] = op
        _wake()
        if action == "start":
            _save(path, state)
        return {k: op[k] for k in ("id", "terminal", "state", "error")}


FILE_ACTIONS = ("file_list", "file_read")
FILE_SECONDS = 25


def files(path, payload, now=None, wait=FILE_SECONDS):
    """What a project folder holds, or a piece of one of its files. The computer reads it; the relay passes the
    question on and holds the request until the answer is there. Nothing of it is written anywhere, and the answer
    is handed over once."""
    now = time.time() if now is None else now
    if not isinstance(payload, dict):
        raise RemoteError("请求格式无效")
    op_id, action, folder, where = payload.get("id"), payload.get("action"), payload.get("dir"), payload.get("path", "")
    if not isinstance(op_id, str) or not _id.fullmatch(op_id):
        raise RemoteError("操作编号无效")
    if action not in FILE_ACTIONS:
        raise RemoteError("不支持此操作")
    if not isinstance(where, str) or len(where) > 1000 or any(ord(c) < 32 for c in where):
        raise RemoteError("路径无效")
    if type(payload.get("offset", 0)) is not int or payload.get("offset", 0) < 0:
        raise RemoteError("位置无效")
    with _changed:
        _expire(now)
        if op_id in _pending:
            raise RemoteError("操作编号已被其它操作使用")
        if not _view(now)["online"] or not _device["enabled"]:
            raise RemoteError("电脑未连接或远控已关闭，请等待电脑上线后重试")
        if "files" not in _device.get("features", []):
            raise RemoteError("电脑端版本不支持查看文件，请更新电脑端")
        if folder not in _device["workspaces"]:
            raise RemoteError("没有这个项目")
        op = {"id": op_id, "terminal": "", "state": "queued", "error": "", "at": now,
              "payload": {"id": op_id, "action": action, "dir": folder, "path": where, "offset": payload.get("offset", 0)}}
        _pending[op_id] = _pending.queued[op_id] = op
        _wake()
        deadline = time.monotonic() + wait
        while op["state"] == "queued":
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            _changed.wait(min(remaining, 5))
        _pending.queued.pop(op_id, None)
        _pending.pop(op_id, None)       # asked once, answered once: nothing of a file stays here
        if op["state"] == "queued":
            op["state"] = "error"
            raise RemoteError("电脑没有及时回应，请重试")
        if op["error"]:
            raise RemoteError(op["error"])
        return op.get("result") or {}


def pull(path, payload, now=None):
    """Held open by the computer: answers with the operations it has not been given yet, as soon as there is one.
    They stay queued until the computer reports them done, so a lost answer is made up by its next report."""
    if not isinstance(payload, dict) or not isinstance(payload.get("instance"), str) or not _id.fullmatch(payload["instance"]):
        raise RemoteError("电脑实例编号无效")
    seconds = payload.get("wait", 12)
    deadline = time.monotonic() + (min(max(seconds, 0), 12) if type(seconds) in (int, float) else 12)
    with _changed:
        while True:
            fresh = [op for op in _pending.queued.values() if not op.get("sent")] if payload["instance"] == _device["instance"] else []
            if fresh:
                for op in fresh:
                    op["sent"] = True
                return {"operations": [{**op["payload"], "terminal": op["terminal"], "at": op["at"]} for op in fresh]}
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return {"operations": []}
            _changed.wait(min(remaining, 5))


def agent(path, payload, now=None):
    now = time.time() if now is None else now
    if not isinstance(payload, dict) or not isinstance(payload.get("info"), dict):
        raise RemoteError("请求格式无效")
    info = payload["info"]
    instance = info.get("instance")
    if not isinstance(instance, str) or not _id.fullmatch(instance):
        raise RemoteError("电脑实例编号无效")
    with _lock:
        _expire(now)
        state = _state(path)
        dirty = wrote = False
        if isinstance(payload.get("sessions"), list):
            _sessions[:] = [
                {"id": s["id"], "tool": s["tool"], "dir": s["dir"], "title": s["title"].strip()[:80], "updated": s["updated"],
                 "live": s.get("live") is True, "status": s.get("status") if s.get("status") in ("idle", "busy") else "",
                 "host": s.get("host") if s.get("host") in ("cli", "shared", "remote", "unknown") else "",
                 "origin": str(s.get("origin") or "")[:60],
                 "can_takeover": s.get("can_takeover") is True, "ownership_known": s.get("ownership_known") is True,
                 "takeover_reason": str(s.get("takeover_reason") or "")[:200]}
                for s in payload["sessions"][:80]
                if isinstance(s, dict) and isinstance(s.get("id"), str) and _session.fullmatch(s["id"]) and s.get("tool") in ("claude", "codex")
                and isinstance(s.get("dir"), str) and isinstance(s.get("title"), str) and s["title"].strip() and type(s.get("updated")) is int]
        _device.update(seen=now, instance=instance, enabled=info.get("enabled") is True,
                       version=str(info.get("version") or "")[:20], newer=str(info.get("newer") or "")[:20],
                       features=[x for x in info.get("features", []) if x in ("codex-fork", "codex-takeover", "terminal-exit", "files", "update")],
                       tools=[x for x in info.get("tools", []) if x in ("claude", "codex", "shell")],
                       workspaces=[x for x in info.get("workspaces", []) if isinstance(x, str) and 0 < len(x) <= 60],
                       projects=_folders(info.get("projects"), 60), candidates=_folders(info.get("candidates"), 12))
        for term in state["threads"].values():
            if term.get("instance") != instance and term["state"] in ("starting", "running"):
                term.update(state="closed", error="电脑后台已重启，请从历史对话恢复")
                _track(term, now)       # or it would keep the phase it had, and be listed as still working
                _trim(term)
                dirty = True
        for ack in payload.get("acks", [])[:200]:
            if not isinstance(ack, dict):
                continue
            op = _pending.get(ack.get("id"))
            if op and op["state"] == "queued":
                if op["payload"]["action"] in FILE_ACTIONS:
                    op["result"] = ack.get("result") if isinstance(ack.get("result"), dict) else {}
                    wrote = True        # the request that waits for it looks again
                op.update(state="error" if ack.get("error") else "done", error=str(ack.get("error", ""))[:300])
                _pending.queued.pop(op["id"], None)
                term = state["threads"].get(op["terminal"])
                if term and term.get("error") != op["error"]:       # nearly every key ends without one: nothing to write
                    term["error"] = op["error"]
                    dirty = True
                if op["payload"]["action"] == "project_rename" and not op["error"]:
                    # Records made under the old name follow the project.
                    for record in state["threads"].values():
                        if record["dir"] == op["payload"]["name"]:
                            record["dir"] = op["payload"]["to"].strip()
                            dirty = True
                if term and op["payload"]["action"] == "start" and op["error"]:
                    term.update(state="closed", error=op["error"])
                    _trim(term)
                    dirty = True
        for item in payload.get("terminals", [])[:MAX_TERMINALS + MAX_HISTORY]:
            if not isinstance(item, dict):
                continue
            term = state["threads"].get(item.get("id"))
            if term and term.get("instance") == instance:
                status = item.get("state")
                if status in ("running", "closed") and status != term["state"]:
                    term["state"] = status
                    _trim(term)
                    dirty = True
                session = item.get("session")
                if isinstance(session, str) and _session.fullmatch(session) and session != term.get("session"):
                    term["session"] = session
                    dirty = True
                status = item.get("status") if item.get("status") in ("idle", "busy") and term["state"] == "running" else ""
                wrote = wrote or status != term.get("status")
                term["status"] = status
                exit_code = item.get("exit_code")
                if term["state"] == "closed" and type(exit_code) is int and term.get("exit_code") != exit_code:
                    term["exit_code"] = exit_code
                    if exit_code != 0:
                        term["error"] = "程序退出（代码 %d），请检查终端画面中的原因" % exit_code
                    dirty = True
                for key in ("cols", "rows"):
                    if type(item.get(key)) is int:
                        term[key] = item[key]
        for chunk in payload.get("output", [])[:200]:
            if not isinstance(chunk, dict):
                continue
            term = state["threads"].get(chunk.get("terminal"))
            seq, data = chunk.get("seq"), chunk.get("data")
            # A piece is taken once. A gap can only follow a relay restart that lost the last unsaved pieces; the
            # computer has discarded them by then, so later output is accepted rather than refused forever.
            if not term or term.get("instance") != instance or type(seq) is not int or seq <= term["seq"] or not isinstance(data, str) or len(data) > 30000:
                continue
            term["output"].append({"seq": seq, "data": data})
            term["seq"] = seq
            # A terminal from before this rule has no "touched" and keeps the old behaviour.
            if term.get("touched", True) and now >= term.get("calm_until", 0):
                term["out_at"] = now
            term["size"] = term.get("size", 0) + len(data)
            _trim(term)
            wrote = True
            _unsaved.setdefault(path, now)
        for term in state["threads"].values():
            if term.get("instance") == instance and _track(term, now):
                dirty = True
        # States are written at once. Output alone is written every few seconds: a busy screen reports many times a second.
        if dirty or (path in _unsaved and now - _unsaved[path] >= SAVE_SECONDS):
            _save(path, state)
            _unsaved.pop(path, None)
        if dirty or wrote:
            _wake()
        for op in _pending.queued.values():
            op["sent"] = True
        ops = [{**op["payload"], "terminal": op["terminal"], "at": op["at"]} for op in _pending.queued.values()]
        return {"operations": ops, "output_ack": {t["id"]: t["seq"] for t in state["threads"].values() if t.get("instance") == instance}}
