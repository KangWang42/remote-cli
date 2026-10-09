#!/usr/bin/env python3
"""remote-cli agent for Linux: runs this computer's terminals for the phone.

    python3 agent.py                              # data in ~/.local/state/remote-cli-agent
    python3 agent.py --data /var/lib/remote-cli-agent
    python3 agent.py --set-password [folder]      # store the relay's password, read from stdin

It signs in to the relay (relay/server.py) the same way the phone does, reports terminal
state and output a few times a second, and carries out the operations the phone sends.
Only the standard library is used. Terminal input and output never reach a log.

Configuration lives in <data>/config.json:
    {"Server": "https://relay.example.com", "RemoteEnabled": true,
     "Name": "ubuntu-server", "Shell": "/bin/bash",
     "RemoteDirs": ["演示=/home/you/演示"]}
RemoteDirs holds the folders a terminal may be started in; folders added from the phone
are kept beside them in <data>/terminal-projects.json.
"""
import codecs
import fcntl
import http.client
import json
import os
import pty
import re
import secrets
import shutil
import signal
import socket
import struct
import subprocess
import sys
import termios
import threading
import time
from urllib.parse import quote, urlsplit

VERSION = "0.1.0"
ID_RE = re.compile(r"\A[a-f0-9]{16,32}\Z")
TERMINAL_RE = re.compile(r"\A[a-f0-9]{32}\Z")
SERVER_RE = re.compile(r"\Ahttps?://[^/\s]+\Z")
MAX_TERMINALS = 8
MAX_OWN_PROJECTS = 30
OP_TTL = 150            # seconds an operation may travel before it is refused
CHUNK_CHARS = 12000     # one numbered piece of output, as the Windows agent seals them
BATCH_BYTES = 512 * 1024
BATCH_CHUNKS = 150
STRIP_ENV = ("CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT")


class OpError(Exception):
    """A request the agent refuses; its text is shown to the person at the phone."""


def say(message):
    print(time.strftime("[%H:%M:%S] ") + message, file=sys.stderr, flush=True)


def write_private(path, text):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
        stream.write(text)


def read_password(data):
    """The relay's password, from RCLI_PASSWORD or <data>/password.txt. Never made up here:
    it has to match the relay, which makes its own when it starts."""
    given = os.environ.get("RCLI_PASSWORD", "").strip()
    if given:
        return given
    try:
        with open(os.path.join(data, "password.txt"), encoding="utf-8") as stream:
            return stream.read().strip()
    except OSError:
        return ""


def read_config(data):
    try:
        with open(os.path.join(data, "config.json"), encoding="utf-8") as stream:
            cfg = json.load(stream)
        return cfg if isinstance(cfg, dict) else {}
    except (OSError, ValueError):
        return {}


def tidy(text, limit=60):
    text = re.sub(r"<[^>]{1,40}>", " ", text or "")
    text = re.sub(r"[\x00-\x1f\x7f-\x9f]+", " ", text)
    text = " ".join(text.split())
    if len(text) > limit:
        text = text[:limit - 1] + "…"
    return text


def normal_folder(path):
    """A folder on this computer, as the phone may type it. POSIX paths, unlike the
    Windows agent's drive-letter rule."""
    path = (path or "").strip()
    if len(path) < 2 or len(path) > 240 or "*" in path or "?" in path or "\x00" in path \
            or any(ord(c) < 32 for c in path) or not path.startswith("/"):
        raise OpError("请填写电脑上的完整文件夹路径，例如 /home/you/demo")
    return os.path.normpath(path)


def parse_dirs(cfg):
    """Project folders from config.json, as name -> existing absolute path."""
    raw = cfg.get("RemoteDirs")
    out = {}
    for item in (raw if isinstance(raw, list) else [])[:60]:
        if isinstance(item, str):
            at = item.find("=")
            if at <= 0:
                continue
            name, path = item[:at].strip(), item[at + 1:].strip()
        elif isinstance(item, dict):
            name, path = str(item.get("name") or "").strip(), str(item.get("path") or "").strip()
        else:
            continue
        if not name or len(name) > 60 or len(path) > 260 or not path.startswith("/"):
            continue
        path = os.path.normpath(path)
        if os.path.isdir(path):
            out[name] = path
    return out


def check_op(op, need_terminal=True):
    """Shape and freshness of an operation, before anything is carried out."""
    op_id = op.get("id")
    terminal = op.get("terminal")
    if not isinstance(op_id, str) or not ID_RE.match(op_id):
        raise OpError("操作编号无效")
    if need_terminal and (not isinstance(terminal, str) or not TERMINAL_RE.match(terminal)):
        raise OpError("操作编号无效")
    at = op.get("at")
    if isinstance(at, str):
        try:
            at = float(at)
        except ValueError:
            at = None
    if isinstance(at, bool) or not isinstance(at, (int, float)) or abs(time.time() - at) > OP_TTL:
        raise OpError("操作已过期")
    return op_id, terminal if isinstance(terminal, str) else ""


class Http:
    """One persistent connection, used by one thread. The report loop and the held pull
    each have their own, so a held request never blocks a report."""

    def __init__(self):
        self.lock = threading.Lock()
        self.conn = None
        self.key = None

    def close(self):
        conn, self.conn = self.conn, None
        if conn is not None:
            try:
                conn.close()
            except OSError:
                pass

    def post(self, url, payload, token="", timeout=20):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        parts = urlsplit(url)
        key = (parts.scheme, parts.netloc.lower())
        path = parts.path or "/"
        if parts.query:
            path += "?" + parts.query
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        with self.lock:
            last = None
            for attempt in range(2):        # a fresh connection once, after a dropped one
                if self.conn is not None and key != self.key:
                    self.close()
                self.key = key
                if self.conn is None:
                    if parts.scheme == "https":
                        self.conn = http.client.HTTPSConnection(parts.hostname, parts.port or 443, timeout=timeout)
                    else:
                        self.conn = http.client.HTTPConnection(parts.hostname, parts.port or 80, timeout=timeout)
                self.conn.timeout = timeout
                try:
                    self.conn.request("POST", path, body=body, headers=headers)
                    response = self.conn.getresponse()
                    return response.status, response.read()
                except (http.client.HTTPException, OSError) as error:
                    last = error
                    self.close()
            raise last


class Live:
    """One terminal: the child process, its pty, and the output waiting to be numbered."""

    def __init__(self, terminal_id, cwd, cols, rows, shell, wake):
        self.id = terminal_id
        self.dir = cwd
        self.wake = wake
        self.lock = threading.Lock()
        self.pending = []               # read from the program, not yet numbered
        self.output = []                # numbered, not yet acknowledged by the relay
        self.seq = 0
        self.closed = False             # the process has been reaped
        self.drained = False            # the reader delivered everything
        self.exit_code = None
        self.closed_at = None
        self.decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        pid, fd = pty.fork()
        if pid == 0:                    # child: pty.fork already made it a session leader
            try:
                os.chdir(cwd)
                env = dict(os.environ)
                for key in STRIP_ENV:
                    env.pop(key, None)
                env["TERM"] = "xterm-256color"
                env["COLORTERM"] = "truecolor"
                os.execvpe(shell, [shell], env)
            except BaseException:
                os._exit(127)
        self.pid = pid
        self.fd = fd
        self.resize(cols, rows)         # the kernel sends SIGWINCH to the foreground group
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._wait, daemon=True).start()

    def _read(self):
        try:
            while True:
                data = os.read(self.fd, 8192)
                if not data:
                    break
                text = self.decoder.decode(data)
                if text:
                    with self.lock:
                        self.pending.append(text)
                    self.wake()
        except OSError:
            pass                        # the terminal was closed or the program left
        finally:
            tail = self.decoder.decode(b"", final=True)
            with self.lock:
                if tail:
                    self.pending.append(tail)
                self.drained = True
            self.wake()

    def _wait(self):
        try:
            _, status = os.waitpid(self.pid, 0)
        except (ChildProcessError, OSError):
            status = 0
        if os.WIFSIGNALED(status):
            code = 128 + os.WTERMSIG(status)
        elif os.WIFEXITED(status):
            code = os.WEXITSTATUS(status)
        else:
            code = 1
        with self.lock:
            self.exit_code = code
            self.closed = True
            self.closed_at = time.time()
        self.wake()

    def write(self, text):
        data = text.encode("utf-8")
        with self.lock:
            if self.fd < 0:
                raise OpError("终端已结束")
            fd = self.fd
        try:
            os.write(fd, data)
        except OSError:
            raise OpError("终端已结束")

    def resize(self, cols, rows):
        try:
            fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
        except OSError:
            pass
        self.cols, self.rows = cols, rows

    def kill(self):
        try:
            os.killpg(self.pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError, OSError):
            pass
        deadline = time.time() + 3
        while time.time() < deadline and not self.closed:
            time.sleep(0.05)
        if not self.closed:
            try:
                os.killpg(self.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError, OSError):
                pass

    def trim(self, upto):
        with self.lock:
            self.output[:] = [chunk for chunk in self.output if chunk["seq"] > upto]

    def can_forget(self):
        with self.lock:
            return (self.closed and not self.pending and not self.output
                    and (self.drained or (self.closed_at is not None and time.time() - self.closed_at > 15)))

    def dispose(self):
        if self.fd >= 0:
            try:
                os.close(self.fd)
            except OSError:
                pass
            self.fd = -1


def seal(live):
    """Turns pending text into numbered pieces, so a busy screen is not held back by the
    per-request piece limit. Only the report thread calls this."""
    with live.lock:
        text = "".join(live.pending)
        live.pending = []
    while text:
        live.seq += 1
        live.output.append({"terminal": live.id, "seq": live.seq, "data": text[:CHUNK_CHARS]})
        text = text[CHUNK_CHARS:]


def output_batch(lives):
    """A report carries at most 150 pieces or about half a megabyte, whichever comes first."""
    batch, size = [], 0
    for live in lives:
        with live.lock:
            chunks = live.output[:30]
        for chunk in chunks:
            encoded = len(json.dumps(chunk, ensure_ascii=False).encode("utf-8"))
            if batch and size + encoded > BATCH_BYTES:
                return batch
            batch.append(chunk)
            size += encoded
            if len(batch) >= BATCH_CHUNKS:
                return batch
    return batch


class Projects:
    """Folders a terminal may start in: the config's own list, plus folders added from the phone."""

    def __init__(self, data):
        self.file = os.path.join(data, "terminal-projects.json")
        self.own = self._load()

    def _load(self):
        try:
            with open(self.file, encoding="utf-8") as stream:
                items = json.load(stream)
            return [{"name": str(i["name"]), "path": str(i["path"])} for i in items
                    if isinstance(i, dict) and i.get("name") and i.get("path")]
        except (OSError, ValueError, KeyError, TypeError):
            return []

    def _save(self):
        write_private(self.file + ".tmp", json.dumps(self.own, ensure_ascii=False))
        os.replace(self.file + ".tmp", self.file)

    def all(self, cfg):
        dirs = parse_dirs(cfg)
        for item in self.own:
            if item["name"] not in dirs:
                path = os.path.normpath(item["path"])
                if path.startswith("/") and os.path.isdir(path):
                    dirs[item["name"]] = path
        return dirs

    def entries(self, cfg, dirs):
        fixed = parse_dirs(cfg)
        out = [{"name": n, "path": p, "fixed": True, "exists": True} for n, p in fixed.items()]
        out += [{"name": i["name"], "path": i["path"], "fixed": False, "exists": os.path.isdir(i["path"])}
                for i in self.own if i["name"] not in fixed]
        return out

    def change(self, op, action, cfg, terminals):
        fixed = parse_dirs(cfg)
        name = tidy(str(op.get("name") or ""), 40)
        if action == "project_add":
            path = normal_folder(str(op.get("path") or ""))
            if any(existing == path for existing in fixed.values()) \
                    or any(existing["path"] == path for existing in self.own):
                raise OpError("这个文件夹已经在项目里")
            if len(self.own) >= MAX_OWN_PROJECTS:
                raise OpError("项目已达 %d 个，请先移除不用的" % MAX_OWN_PROJECTS)
            if not os.path.isdir(path):
                if op.get("create") is not True:
                    raise OpError("电脑上没有这个文件夹")
                parent = os.path.dirname(path.rstrip("/"))
                if not parent or not os.path.isdir(parent):
                    raise OpError("上一级文件夹不存在，不能新建")
                os.mkdir(path, 0o755)
            if not name:
                name = tidy(os.path.basename(path.rstrip("/")) or path, 40)
            wanted, n = name, 1
            while name in fixed or any(i["name"] == name for i in self.own):
                n += 1
                name = "%s %d" % (wanted, n)
            self.own.append({"name": name, "path": path})
        else:
            at = next((i for i, item in enumerate(self.own) if item["name"] == name), -1)
            if at < 0:
                raise OpError("这个项目写在电脑的配置文件里，请在电脑上修改"
                              if name in fixed else "没有这个项目")
            if action == "project_remove":
                if any(t.dir == name and not t.closed for t in terminals.values()):
                    raise OpError("这个项目还有终端在运行，请先结束")
                del self.own[at]
            else:
                to = tidy(str(op.get("to") or ""), 40)
                if not to:
                    raise OpError("名称需为 1 至 40 字")
                if to != name and (to in fixed or any(i["name"] == to for i in self.own)):
                    raise OpError("已有同名项目")
                self.own[at] = {"name": to, "path": self.own[at]["path"]}
                for t in terminals.values():
                    if t.dir == name:
                        t.dir = to
        self._save()


class Agent:
    def __init__(self, data, password):
        self.data = data
        self.password = password
        self.instance = secrets.token_hex(16)
        self.projects = Projects(data)
        self.terminals = {}
        self.completed = {}
        self.reported = set()
        self.work = threading.Lock()
        self.wakeup = threading.Event()
        self.stop = threading.Event()
        self.http = Http()
        self.pull_http = Http()
        self.server = ""
        self.token = ""
        self.next_login = 0.0
        self.last_activity = time.time()
        self.last_input = 0.0
        self.soon = False
        self.paired = False

    # ---- wiring
    def wake(self):
        self.wakeup.set()

    def stop_run(self, *_):
        self.stop.set()
        self.wakeup.set()

    def shell(self, cfg):
        shell = str(cfg.get("Shell") or "").strip() or os.environ.get("SHELL") or "/bin/bash"
        return shell if os.path.isfile(shell) else "/bin/bash"

    # ---- protocol
    def login(self):
        if not SERVER_RE.match(self.server):
            return False
        self.next_login = time.time() + 60      # one attempt a minute until it works
        try:
            status, body = self.http.post(self.server + "/api/login", {"password": self.password})
            token = json.loads(body.decode("utf-8")).get("token") if status == 200 else None
        except (OSError, http.client.HTTPException, ValueError):
            return False
        if not isinstance(token, str) or len(token) < 32:
            return False
        self.token = token
        say("已连接中转 " + self.server)
        return True

    def report(self):
        cfg = read_config(self.data)
        server = str(cfg.get("Server") or "").strip().rstrip("/")
        if server != self.server:
            self.server, self.token = server, ""
            self.http.close()
            self.pull_http.close()
        if not self.server:
            return
        if not self.token:
            if time.time() < self.next_login or not self.login():
                return
        if not self.paired:
            self.paired = True
            print_pairing(self.server, self.password,
                          tidy(str(cfg.get("Name") or socket.gethostname()), 60))
        with self.work:
            enabled = cfg.get("RemoteEnabled") is True
            if not enabled:
                for live in self.terminals.values():
                    if not live.closed:
                        live.kill()
            dirs = self.projects.all(cfg)
            for live in self.terminals.values():
                seal(live)
            output = output_batch(list(self.terminals.values()))
            if output:
                self.last_activity = time.time()
            acks = [item for key, item in self.completed.items() if key not in self.reported][:200]
            payload = {
                "info": {"instance": self.instance, "enabled": enabled, "tools": ["shell"],
                         "workspaces": list(dirs.keys()),
                         "projects": self.projects.entries(cfg, dirs)},
                "terminals": [{"id": live.id, "state": "closed" if live.closed else "running",
                               "cols": live.cols, "rows": live.rows, "session": "", "status": "",
                               "exit_code": live.exit_code} for live in self.terminals.values()],
                "output": output, "acks": acks}
        try:
            status, body = self.http.post(self.server + "/api/terminal/agent", payload, self.token)
        except (OSError, http.client.HTTPException):
            return
        if status == 401:
            self.token = ""
            return
        if status != 200:
            return
        try:
            result = json.loads(body.decode("utf-8") or "{}")
        except ValueError:
            return
        if not isinstance(result, dict):
            return
        with self.work:
            for ack in acks:
                self.reported.add(ack["id"])
            ops = result.get("operations")
            if isinstance(ops, list):
                for op in ops:
                    if isinstance(op, dict):
                        self.execute(op, cfg, dirs)
            upto_map = result.get("output_ack")
            if isinstance(upto_map, dict):
                for live in self.terminals.values():
                    upto = upto_map.get(live.id)
                    if isinstance(upto, int) and not isinstance(upto, bool):
                        live.trim(upto)
            if len(self.completed) > 4000:
                for key in list(self.completed)[:1000]:
                    del self.completed[key]
                    self.reported.discard(key)
            for key in [k for k, live in self.terminals.items() if live.can_forget()]:
                self.terminals.pop(key).dispose()

    def execute(self, op, cfg, dirs):
        """Carries out one operation. Called with self.work held, from the report loop or
        the held pull; the same id is never carried out twice."""
        try:
            op_id, terminal = check_op(op, need_terminal=op.get("action") not in
                                       ("project_add", "project_remove", "project_rename"))
        except OpError as error:
            say("拒绝操作：%s" % error)
            return
        if op_id in self.completed:
            self.reported.discard(op_id)        # the ack may have been lost: send it again
            return
        error = ""
        try:
            if cfg.get("RemoteEnabled") is not True:
                raise OpError("电脑远控已关闭")
            action = str(op.get("action") or "")
            if action in ("project_add", "project_remove", "project_rename"):
                self.projects.change(op, action, cfg, self.terminals)
            elif action == "start":
                self.start_terminal(op, dirs, terminal)
            else:
                live = self.terminals.get(terminal)
                if live is None or live.closed:
                    raise OpError("终端已结束")
                if live.dir not in dirs.values():
                    raise OpError("目录不再获允许")
                if action == "input":
                    data = op.get("data")
                    if not isinstance(data, str) or not data or len(data) > 16000:
                        raise OpError("输入无效")
                    self.last_input = time.time()
                    live.write(data)
                elif action == "resize":
                    cols, rows = op.get("cols"), op.get("rows")
                    if not all(isinstance(v, int) and not isinstance(v, bool) for v in (cols, rows)) \
                            or not (20 <= cols <= 240 and 6 <= rows <= 100):
                        raise OpError("尺寸无效")
                    live.resize(cols, rows)
                elif action == "close":
                    live.kill()
                else:
                    raise OpError("操作无效")
        except OpError as exc:
            error = str(exc)
        except Exception as exc:                # never leak a path or a stack to the phone
            error = "终端操作失败：" + type(exc).__name__
        self.completed[op_id] = {"id": op_id, "error": error}
        self.reported.discard(op_id)
        self.last_activity = time.time()
        self.soon = True
        if error:
            say("操作 %s 失败：%s" % (op_id[:8], error))

    def start_terminal(self, op, dirs, terminal):
        if terminal in self.terminals:
            return                              # already started; the operation is a repeat
        tool = str(op.get("tool") or "")
        if tool != "shell":
            raise OpError("电脑没有这个工具")
        name = str(op.get("dir") or "")
        if name not in dirs:
            raise OpError("目录未获允许")
        cwd = dirs[name]
        if not os.path.isdir(cwd):
            raise OpError("电脑上没有这个文件夹")
        if sum(1 for live in self.terminals.values() if not live.closed) >= MAX_TERMINALS:
            raise OpError("最多同时运行 %d 个终端，请先结束一个" % MAX_TERMINALS)
        cfg = read_config(self.data)
        self.terminals[terminal] = Live(terminal, cwd, 80, 24, self.shell(cfg), self.wake)

    # ---- loops
    def listen(self):
        """A request the relay holds open until the phone asks for something, so a key
        press does not wait for the next report."""
        while not self.stop.is_set():
            token = self.token
            if not token or not self.server:
                self.stop.wait(1)
                continue
            started = time.monotonic()
            waiting = False
            try:
                status, body = self.pull_http.post(
                    self.server + "/api/terminal/agent/pull",
                    {"instance": self.instance, "wait": 12}, token, timeout=20)
                if status == 401:
                    self.token = ""
                elif status == 200:
                    waiting = True
                    result = json.loads(body.decode("utf-8") or "{}")
                    ops = result.get("operations")
                    if isinstance(ops, list) and ops:
                        cfg = read_config(self.data)
                        with self.work:
                            dirs = self.projects.all(cfg)
                            for op in ops:
                                if isinstance(op, dict):
                                    self.execute(op, cfg, dirs)
                        self.wake()
            except (OSError, http.client.HTTPException, ValueError):
                waiting = False
            # An answer that came back at once must not become a busy loop.
            if not waiting or time.monotonic() - started < 0.04:
                self.stop.wait(0.04 if waiting else 2)

    def run(self):
        threading.Thread(target=self.listen, daemon=True).start()
        while not self.stop.is_set():
            try:
                self.report()
            except Exception as exc:            # network and login failures are retried
                say("报告失败：%s" % type(exc).__name__)
            pause = 0.02 if self.soon else 0.25 if time.time() - self.last_activity < 20 else 0.7
            self.soon = False
            if self.wakeup.wait(pause):
                self.wakeup.clear()
                if time.time() - self.last_input < 0.15:
                    time.sleep(0.012)
        for live in self.terminals.values():
            if not live.closed:
                live.kill()
            live.dispose()
        self.http.close()
        self.pull_http.close()
        return 0


def print_pairing(server, password, name):
    """The same code the Windows program shows as a picture, printed for the console."""
    payload = "remotecli://connect?u=%s&p=%s&n=%s" % (
        quote(server, safe=""), quote(password, safe=""), quote(name, safe=""))
    print("\n配对：手机 App 点“扫码添加电脑”，扫下面的二维码，或手动输入地址与密码。", file=sys.stderr)
    print("  地址：%s\n  名称：%s\n  密码：%s\n" % (server, name, password), file=sys.stderr)
    qr = shutil.which("qrencode")
    if qr:
        for kind in ("ANSIUTF8", "UTF8", "ANSI"):
            result = subprocess.run([qr, "-t", kind, payload], capture_output=True)
            if result.returncode == 0 and result.stdout:
                sys.stderr.write(result.stdout.decode("utf-8", "replace"))
                break


def set_password(folder):
    """Stores the relay's password for this agent, the way --set-password does on Windows."""
    secret = sys.stdin.readline().strip()
    if not secret:
        return 2
    os.makedirs(folder, mode=0o700, exist_ok=True)
    write_private(os.path.join(folder, "password.txt"), secret + "\n")
    print("密码已写入 %s（权限 600）" % os.path.join(folder, "password.txt"))
    return 0


def main():
    argv = sys.argv[1:]
    data = os.environ.get("REMOTECLI_DATA") or \
        os.path.join(os.path.expanduser("~"), ".local", "state", "remote-cli-agent")
    if "--data" in argv:
        at = argv.index("--data")
        if at + 1 < len(argv):
            data = argv[at + 1]
    if argv and argv[0] == "--set-password":
        return set_password(argv[1] if len(argv) > 1 else data)
    os.makedirs(data, mode=0o700, exist_ok=True)
    # One agent per data directory (the Windows agent's mutex): a second instance would
    # report with its own id and the relay would keep closing the first one's terminals.
    # The Rust agent takes the same lock, so the two implementations guard each other.
    lock = open(os.path.join(data, "agent.lock"), "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        say("此数据目录已有 agent 在运行（agent.lock 被占用），退出")
        return 3
    password = read_password(data)
    if not password:
        say("缺少中转密码：运行 python3 agent.py --set-password %s 并输入中转的密码，"
            "或设置环境变量 RCLI_PASSWORD" % data)
        return 2
    say("remote-cli agent (Linux) %s，数据目录 %s" % (VERSION, data))
    agent = Agent(data, password)
    signal.signal(signal.SIGTERM, agent.stop_run)
    signal.signal(signal.SIGINT, agent.stop_run)
    return agent.run()


if __name__ == "__main__":
    sys.exit(main())
