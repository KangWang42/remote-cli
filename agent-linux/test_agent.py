"""Tests for the Linux agent: pure-logic unit tests, and an end-to-end test that runs the
real relay (../relay/server.py) with a real agent subprocess and speaks the viewer protocol
to it, the way the phone does.

    python3 -m unittest discover -s agent-linux -p "test_*.py"
"""
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import agent  # noqa: E402

PASSWORD = "unit-test-password-8f2c"

# Raw pty output interleaves echo, prompts and redraw escapes; match on the plain text.
ANSI = re.compile(r"\x1b\[[0-9;?<=>]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|\r")


def plain(text):
    return ANSI.sub("", text)


def request(method, url, payload=None, token=None, timeout=10):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            return response.status, json.loads(response.read().decode("utf-8") or "{}")
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read().decode("utf-8") or "{}")


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Pure(unittest.TestCase):
    """The pieces that need no network and no processes."""

    def test_normal_folder(self):
        self.assertEqual(agent.normal_folder("/home/you/demo "), "/home/you/demo")
        self.assertEqual(agent.normal_folder("/a/./b/../c"), "/a/c")
        for bad in ("demo", "relative/path", "/tmp/*", "", "/x" * 130, "/home/\x01"):
            with self.assertRaises(agent.OpError):
                agent.normal_folder(bad)

    def test_tidy(self):
        self.assertEqual(agent.tidy("  a<b>bad</b>c  "), "a bad c")
        self.assertEqual(agent.tidy("x\x01y"), "x y")
        self.assertEqual(len(agent.tidy("长" * 50, 40)), 40)
        self.assertTrue(agent.tidy("长" * 50, 40).endswith("…"))

    def test_parse_dirs(self):
        tmp = tempfile.mkdtemp()
        self.assertEqual(agent.parse_dirs({}), {})
        dirs = agent.parse_dirs({"RemoteDirs": ["名字=" + tmp, "bad", "=x", "d={" + tmp + "}"]})
        self.assertEqual(dirs, {"名字": tmp})
        dicts = agent.parse_dirs({"RemoteDirs": [{"name": "n", "path": tmp}, {"name": "gone", "path": "/no/such"}]})
        self.assertEqual(dicts, {"n": tmp})

    def test_check_op(self):
        good = {"id": secrets.token_hex(16), "terminal": secrets.token_hex(16), "at": time.time()}
        self.assertEqual(agent.check_op(good)[0], good["id"])
        with self.assertRaises(agent.OpError):
            agent.check_op({"id": "XYZ", "at": time.time()})
        with self.assertRaises(agent.OpError):
            agent.check_op({"id": secrets.token_hex(16), "at": time.time() - 500})
        with self.assertRaises(agent.OpError):
            agent.check_op({"id": secrets.token_hex(16), "at": "not-a-number"})
        # project operations carry no terminal
        self.assertEqual(agent.check_op({"id": secrets.token_hex(8), "at": time.time()},
                                        need_terminal=False)[1], "")

    def test_seal_chunks(self):
        live = agent.Live.__new__(agent.Live)     # no process: only the buffer parts
        live.id = "t" * 32
        live.lock = __import__("threading").Lock()
        live.pending, live.output, live.seq, live.out_bytes = [], [], 0, 0
        live.pending.append("字" * 20000)          # 20000 chars -> a 12000 and a 8000 piece
        agent.seal(live)
        self.assertEqual([len(c["data"]) for c in live.output], [12000, 8000])
        self.assertEqual([c["seq"] for c in live.output], [1, 2])
        self.assertEqual("".join(c["data"] for c in live.output), "字" * 20000)
        self.assertEqual(live.pending, [])

    def test_output_batch_limits(self):
        import threading
        def fake(data, count):
            live = agent.Live.__new__(agent.Live)
            live.id = "t" * 32
            live.lock = threading.Lock()
            live.pending = []
            live.output = [{"terminal": live.id, "seq": n, "data": data} for n in range(1, count + 1)]
            live.seq = count
            return live
        # many small pieces: the piece count is what stops the batch — 30 per terminal,
        # as the Windows agent also only seals 30 of each into one report
        lives = [fake("x" * 100, 40) for _ in range(6)]
        batch = agent.output_batch(lives)
        self.assertEqual(len(batch), agent.BATCH_CHUNKS)
        # few huge pieces: the encoded bytes are what stop it
        batch = agent.output_batch([fake("x" * 400000, 3)])
        self.assertLess(len(batch), 3)
        self.assertLessEqual(sum(len(json.dumps(c, ensure_ascii=False).encode()) for c in batch),
                             agent.BATCH_BYTES)

    def test_projects_change(self):
        import threading
        root = tempfile.mkdtemp()
        data = tempfile.mkdtemp()
        projects = agent.Projects(data)
        cfg = {"RemoteDirs": ["固定=" + root]}
        terminals = {}

        def live_for(name):
            t = agent.Live.__new__(agent.Live)
            t.dir, t.closed = name, True
            return t

        terminals["a"] = live_for("手机")
        os.makedirs(root + "/手机")
        projects.change({"path": root + "/手机", "name": "手机"}, "project_add", cfg, terminals)
        self.assertEqual([i["name"] for i in projects.own], ["手机"])
        # a running terminal blocks removal
        terminals["a"].closed = False
        with self.assertRaises(agent.OpError):
            projects.change({"name": "手机"}, "project_remove", cfg, terminals)
        terminals["a"].closed = True
        projects.change({"name": "手机", "to": "改名"}, "project_rename", cfg, terminals)
        self.assertEqual(projects.own[0]["name"], "改名")
        # a fixed project cannot be touched from the phone
        with self.assertRaises(agent.OpError):
            projects.change({"name": "固定"}, "project_remove", cfg, terminals)
        # create makes the folder; a second folder with the same basename gets a numbered name
        os.makedirs(root + "/子")
        projects.change({"path": root + "/新建", "create": True}, "project_add", cfg, terminals)
        projects.change({"path": root + "/子/新建", "create": True}, "project_add", cfg, terminals)
        self.assertTrue(os.path.isdir(root + "/新建"))
        self.assertEqual([i["name"] for i in projects.own], ["改名", "新建", "新建 2"])
        projects.change({"name": "新建 2"}, "project_remove", cfg, terminals)
        # persistence across a fresh instance
        again = agent.Projects(data)
        self.assertEqual([i["name"] for i in again.own], ["改名", "新建"])

    def test_read_password_not_generated(self):
        self.assertEqual(agent.read_password(tempfile.mkdtemp()), "")
        data = tempfile.mkdtemp()
        agent.write_private(os.path.join(data, "password.txt"), PASSWORD + "\n")
        self.assertEqual(agent.read_password(data), PASSWORD)


class EndToEnd(unittest.TestCase):
    """The real relay on 127.0.0.1, the real agent as a subprocess, this test as the phone."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="rcli-e2e-")
        cls.project = os.path.join(cls.tmp, "项目")
        os.makedirs(cls.project)
        port = free_port()
        cls.relay_data = os.path.join(cls.tmp, "relay")
        cls.base = "http://127.0.0.1:%d" % port
        cls.relay = subprocess.Popen(
            [sys.executable, os.path.join(REPO, "relay", "server.py"),
             "--host", "127.0.0.1", "--port", str(port), "--data", cls.relay_data,
             "--web", os.path.join(REPO, "web")],
            env={**os.environ, "RCLI_PASSWORD": PASSWORD},
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.wait_tcp(port)
        agent_data = os.path.join(cls.tmp, "agent")
        os.makedirs(agent_data)
        with open(os.path.join(agent_data, "config.json"), "w", encoding="utf-8") as stream:
            json.dump({"Server": cls.base, "RemoteEnabled": True, "Name": "e2e-ubuntu",
                       "RemoteDirs": ["项目=" + cls.project]}, stream)
        cls.agent = subprocess.Popen(cls.agent_command(agent_data),
                                     env={**os.environ, "RCLI_PASSWORD": PASSWORD},
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.token = request("POST", cls.base + "/api/login", {"password": PASSWORD})[1]["token"]
        view = None
        deadline = time.time() + 30
        while time.time() < deadline:
            status, view = request("GET", cls.base + "/api/terminal", token=cls.token)
            if status == 200 and view.get("device", {}).get("online") \
                    and "项目" in view.get("device", {}).get("workspaces", []):
                return
            time.sleep(0.3)
        raise AssertionError("agent did not come online: %r" % view)

    @classmethod
    def agent_command(cls, agent_data):
        """What spawns the agent under test; overridden by the Rust contract test."""
        return [sys.executable, os.path.join(HERE, "agent.py"), "--data", agent_data]

    @classmethod
    def wait_tcp(cls, port):
        deadline = time.time() + 10
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    return
            except OSError:
                time.sleep(0.1)
        raise AssertionError("relay did not start")

    @classmethod
    def tearDownClass(cls):
        for process in (cls.agent, cls.relay):
            if process.poll() is None:
                process.terminate()
        for process in (cls.agent, cls.relay):
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
        import shutil
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def overview(self):
        status, view = request("GET", self.base + "/api/terminal", token=self.token)
        self.assertEqual(status, 200)
        return view

    def poll(self, what, until, seconds=20, step=0.3):
        deadline = time.time() + seconds
        while time.time() < deadline:
            value = what()
            if until(value):
                return value
            time.sleep(step)
        raise AssertionError("timed out waiting for %r, last: %r" % (until, value))

    def op(self, payload):
        payload = {"id": secrets.token_hex(16), **payload}
        status, result = request("POST", self.base + "/api/terminal", payload, token=self.token)
        self.assertEqual(status, 200, result)
        return result

    def test_end_to_end(self):
        # 1. start a shell in the project and wait for its prompt to arrive
        started = self.op({"action": "start", "tool": "shell", "dir": "项目"})
        self.assertEqual(started["state"], "queued", started)
        terminal = started["terminal"]
        after, text = 0, ""
        deadline = time.time() + 40
        while time.time() < deadline:
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            self.assertEqual(status, 200, view)
            for chunk in view["chunks"]:
                text += chunk["data"]
                after = chunk["seq"]
            if "$" in plain(text) or "#" in plain(text):
                break
        self.assertIn("$", plain(text), "no shell prompt arrived")
        self.assertEqual(self.overview()["terminals"][0]["state"], "running")

        # 2. text round trip: the phone types, the shell answers
        self.op({"action": "input", "terminal": terminal, "data": "echo E2EOK_$((21*2))\n"})
        deadline = time.time() + 30
        while time.time() < deadline and "E2EOK_42" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.2)
        self.assertIn("E2EOK_42", plain(text), "the shell never answered")

        # 3. resize reaches the program (the pty's winsize is what the shell reports);
        # the probe is typed once, the shell runs it when it gets to it
        self.op({"action": "resize", "terminal": terminal, "cols": 100, "rows": 30})
        self.op({"action": "input", "terminal": terminal,
                 "data": "python3 -c \"import shutil;print('COLS_'+str(shutil.get_terminal_size().columns))\"\n"})
        deadline = time.time() + 40
        while time.time() < deadline and "COLS_100" not in plain(text):
            status, view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                                   % (self.base, terminal, after), token=self.token)
            for chunk in view.get("chunks", []):
                text += chunk["data"]
                after = chunk["seq"]
            time.sleep(0.3)
        self.assertIn("COLS_100", plain(text), "resize did not reach the terminal")

        # 4. a graceful exit is reported with its code
        self.op({"action": "input", "terminal": terminal, "data": "exit\n"})
        view = self.poll(lambda: self.overview(),
                         lambda v: v["terminals"] and v["terminals"][0]["state"] == "closed")
        self.assertEqual(view["terminals"][0].get("exit_code"), 0)

        # 5. projects added from the phone appear as workspaces, rename and remove follow
        fresh = os.path.join(self.tmp, "新建项目")
        self.op({"action": "project_add", "path": fresh, "name": "新增", "create": True})
        self.poll(lambda: self.overview(), lambda v: "新增" in v["device"]["workspaces"])
        self.assertTrue(os.path.isdir(fresh), "the agent did not create the folder")
        self.op({"action": "project_rename", "name": "新增", "to": "改名"})
        self.poll(lambda: self.overview(),
                  lambda v: "改名" in v["device"]["workspaces"] and "新增" not in v["device"]["workspaces"])
        self.op({"action": "project_remove", "name": "改名"})
        self.poll(lambda: self.overview(), lambda v: "改名" not in v["device"]["workspaces"])

        # 6. a folder that is not a project never becomes a terminal: the relay refuses it
        # at the door (the agent checks again itself, but this request cannot even reach it)
        status, refused = request("POST", self.base + "/api/terminal",
                                  {"id": secrets.token_hex(16), "action": "start",
                                   "tool": "shell", "dir": "其他"}, token=self.token)
        self.assertEqual(status, 400)
        self.assertIn("工具或目录", refused["error"])


class SingletonLock(unittest.TestCase):
    """A second agent on the same data directory must refuse to start (exit 3)."""

    def run_second(self, command_for):
        import fcntl
        data = tempfile.mkdtemp()
        lock = open(os.path.join(data, "agent.lock"), "w")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            proc = subprocess.run(command_for(data), capture_output=True, timeout=30)
            self.assertEqual(proc.returncode, 3)
            self.assertIn("已有 agent", proc.stderr.decode("utf-8"))
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)
            lock.close()

    def test_python_second_instance_exits_3(self):
        self.run_second(lambda data: [sys.executable, os.path.join(HERE, "agent.py"), "--data", data])


class EnvILocaleFallback(unittest.TestCase):
    """A bare service environment (no LANG) must still yield a UTF-8 locale in the pty."""

    def test_envi_locale_fallback(self):
        tmp = tempfile.mkdtemp(prefix="rcli-envi-")
        project = os.path.join(tmp, "p")
        os.makedirs(project)
        port = free_port()
        base = "http://127.0.0.1:%d" % port
        relay_log = open(os.path.join(tmp, "relay.log"), "w")
        relay = subprocess.Popen(
            [sys.executable, os.path.join(REPO, "relay", "server.py"), "--host", "127.0.0.1",
             "--port", str(port), "--data", os.path.join(tmp, "relay"), "--web", os.path.join(REPO, "web")],
            env={**os.environ, "RCLI_PASSWORD": PASSWORD},
            stdout=subprocess.DEVNULL, stderr=relay_log)
        deadline = time.time() + 10
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    break
            except OSError:
                time.sleep(0.1)
        agent = None
        try:
            agent_data = os.path.join(tmp, "agent")
            os.makedirs(agent_data)
            with open(os.path.join(agent_data, "config.json"), "w", encoding="utf-8") as stream:
                json.dump({"Server": base, "RemoteEnabled": True, "RemoteDirs": ["p=" + project]}, stream)
            # a bare service environment: no LANG, no LC_*
            agent = subprocess.Popen(
                [sys.executable, os.path.join(HERE, "agent.py"), "--data", agent_data],
                env={"RCLI_PASSWORD": PASSWORD, "HOME": os.path.expanduser("~"), "PATH": "/usr/bin:/bin"},
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            token = request("POST", base + "/api/login", {"password": PASSWORD})[1]["token"]
            deadline = time.time() + 30
            while time.time() < deadline:
                view = request("GET", base + "/api/terminal", token=token)[1]
                if view.get("device", {}).get("online"):
                    break
                time.sleep(0.3)
            started = request("POST", base + "/api/terminal",
                              {"id": secrets.token_hex(16), "action": "start", "tool": "shell", "dir": "p"},
                              token=token)[1]
            self.assertIn("terminal", started, started)
            terminal, after, text = started["terminal"], 0, ""
            deadline = time.time() + 30
            while time.time() < deadline and "$" not in plain(text):
                view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                               % (base, terminal, after), token=token)[1]
                for chunk in view.get("chunks", []):
                    text += chunk["data"]
                    after = chunk["seq"]
                time.sleep(0.2)
            request("POST", base + "/api/terminal", {"id": secrets.token_hex(16), "action": "input",
                      "terminal": terminal, "data": "locale | head -1; echo 中文兜底\n"}, token=token)
            deadline = time.time() + 20
            while time.time() < deadline and "兜底" not in plain(text):
                view = request("GET", "%s/api/terminal?terminal=%s&after=%d&wait=2"
                               % (base, terminal, after), token=token)[1]
                for chunk in view.get("chunks", []):
                    text += chunk["data"]
                    after = chunk["seq"]
                time.sleep(0.2)
            p = plain(text)
            self.assertIn("中文兜底", p, "Chinese echo broken")
            self.assertIn("LANG=C.UTF-8", p, "locale fallback did not reach the pty")
        finally:
            if agent is not None:
                agent.terminate()
            relay.terminate()
            import shutil
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
