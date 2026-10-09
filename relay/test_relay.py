import tempfile
import unittest

import relay as tr
from relay import RemoteError


class TerminalRelayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = self.temp.name + "/terminal.json"
        tr._cache.clear()
        tr._pending.clear()
        tr._unsaved.clear()
        tr._sessions.clear()
        self.info = {"instance": "b" * 32, "enabled": True, "tools": ["claude", "codex", "shell"], "workspaces": ["demo"]}
        tr.agent(self.path, {"info": self.info}, now=1000)

    def start(self):
        payload = {"action": "start", "id": "a" * 32, "tool": "codex", "dir": "demo", "history": False}
        result = tr.command(self.path, payload, now=1001)
        self.assertEqual(result, tr.command(self.path, payload, now=1002))
        self.terminal = result["terminal"]
        tr.agent(self.path, {"info": self.info, "acks": [{"id": payload["id"]}], "terminals": [{"id": self.terminal, "state": "running"}]}, now=1003)
        return payload

    def test_fresh_start_never_becomes_anonymous_resume(self):
        payload = {"action": "start", "id": "a" * 32, "tool": "claude", "dir": "demo", "history": True}
        with self.assertRaisesRegex(RemoteError, "resume"):
            tr.command(self.path, payload, now=1001)
        result = tr.command(self.path, dict(payload, history=False), now=1002)
        ops = tr.pull(self.path, {"instance": self.info["instance"], "wait": 0})["operations"]
        self.assertFalse(ops[0]["history"])
        self.assertEqual(tr.overview(self.path, result["terminal"], now=1003)["terminal"]["session"], "")

    def test_live_original_requires_explicit_takeover(self):
        sid = "12345678-1234-1234-1234-123456789abc"
        for tool in ("claude", "codex"):
            tr.agent(self.path, {"info": self.info, "sessions": [{"id": sid, "tool": tool, "dir": "demo", "title": "原对话", "updated": 1, "live": True, "origin": "Positron"}]}, now=1001)
            with self.assertRaisesRegex(RemoteError, "接管"):
                tr.command(self.path, {"action": "start", "id": "d" * 32, "tool": tool, "dir": "demo", "session": sid}, now=1002)
            self.assertEqual(tr.overview(self.path, now=1002)["sessions"][0]["origin"], "Positron")

    def test_codex_fork_preserves_source_and_requires_updated_agent(self):
        sid = "12345678-1234-1234-1234-123456789abc"
        session = {"id": sid, "tool": "codex", "dir": "demo", "title": "电脑会话", "updated": 5000,
                   "live": True, "can_takeover": False, "ownership_known": True, "takeover_reason": "共享后台"}
        tr.agent(self.path, {"info": self.info, "sessions": [session]}, now=1001)
        op = {"action": "start", "id": "f" * 32, "tool": "codex", "dir": "demo", "session": sid, "fork": True}
        with self.assertRaises(RemoteError):
            tr.command(self.path, op, now=1002)
        self.info["features"] = ["codex-fork", "terminal-exit"]
        tr.agent(self.path, {"info": self.info}, now=1003)
        terminal = tr.command(self.path, op, now=1004)["terminal"]
        current = tr.overview(self.path, terminal, now=1004)["terminal"]
        self.assertEqual(current["session"], "")
        self.assertIn("副本", current["title"])
        self.assertEqual(tr.overview(self.path, now=1004)["sessions"][0]["terminal"], "")
        with self.assertRaises(RemoteError):
            tr.command(self.path, dict(op, id="e" * 32, takeover=True), now=1004)
        with self.assertRaises(RemoteError):
            tr.command(self.path, dict(op, id="e" * 32, fork=1), now=1004)

    def test_session_host_is_forwarded_without_claiming_a_visible_window(self):
        for host, expected in [("cli", "cli"), ("shared", "shared"), ("remote", "remote"), ("unknown", "unknown"), (None, ""), (["cli"], "")]:
            session = {"id": "12345678-1234-1234-1234-123456789abc", "tool": "codex", "dir": "demo",
                       "title": "后台保留的旧对话", "updated": 5000, "live": True, "host": host, "can_takeover": False}
            tr.agent(self.path, {"info": self.info, "sessions": [session]}, now=1001)
            actual = tr.overview(self.path, now=1002)["sessions"][0]
            self.assertEqual(actual["host"], expected)
            self.assertTrue(actual["live"])
            self.assertFalse(actual["can_takeover"])

    def test_writer_lock_activity_does_not_claim_a_computer_window(self):
        base = {"tool": "codex", "dir": "demo", "title": "会话", "updated": 5000,
                "live": True, "can_takeover": False}
        sessions = [
            dict(base, id="12345678-1234-1234-1234-123456789abc", host="cli"),
            dict(base, id="22345678-1234-1234-1234-123456789abc", host="shared"),
            dict(base, id="32345678-1234-1234-1234-123456789abc", host="remote"),
            dict(base, id="42345678-1234-1234-1234-123456789abc", host="unknown"),
            dict(base, id="52345678-1234-1234-1234-123456789abc", live=False, host=""),
        ]
        tr.agent(self.path, {"info": self.info, "sessions": sessions}, now=1001)
        activity = {s["host"] or "history": s["activity"] for s in tr.overview(self.path, now=1002)["sessions"]}
        self.assertEqual(activity, {"cli": "active", "shared": "locked", "remote": "locked", "unknown": "locked", "history": "history"})

    def test_nonzero_terminal_exit_is_persisted_with_output(self):
        self.start()
        tr.agent(self.path, {"info": self.info,
                           "terminals": [{"id": self.terminal, "state": "closed", "exit_code": 7}],
                           "output": [{"terminal": self.terminal, "seq": 1, "data": "startup failed"}]}, now=1004)
        tr._cache.clear()
        record = tr.overview(self.path, self.terminal, now=1005)
        self.assertEqual(record["terminal"]["exit_code"], 7)
        self.assertIn("代码 7", record["terminal"]["error"])
        self.assertEqual(record["chunks"][0]["data"], "startup failed")

    def test_start_retry_and_input_delivery_are_idempotent_and_ordered(self):
        payload = self.start()
        self.assertEqual(len(tr.overview(self.path, now=1004)["terminals"]), 1)
        self.assertEqual(tr.command(self.path, payload, now=1004)["state"], "done")
        for n, data in enumerate(("/model", "\r", "\x1b[A", "中文\r"), 1):
            op = {"action": "input", "id": "%032x" % n, "terminal": self.terminal, "data": data}
            tr.command(self.path, op, now=1005)
            tr.command(self.path, op, now=1006)
        operations = tr.agent(self.path, {"info": self.info}, now=1007)["operations"]
        self.assertEqual([o["data"] for o in operations], ["/model", "\r", "\x1b[A", "中文\r"])
        self.assertEqual(tr.agent(self.path, {"info": self.info}, now=1008)["operations"], operations)
        tr.agent(self.path, {"info": self.info, "acks": [{"id": o["id"]} for o in operations]}, now=1009)
        self.assertEqual(tr.agent(self.path, {"info": self.info}, now=1010)["operations"], [])

    def test_output_retry_split_utf8_terminal_controls_and_reconnect(self):
        self.start()
        output = [{"terminal": self.terminal, "seq": i, "data": text} for i, text in enumerate(("\x1b[2J\x1b[H", "中文 /model", "\r\n完成"), 1)]
        report = {"info": self.info, "output": output}
        for at in (1004, 1007):  # the repeated report also writes the output that was held back for a moment
            self.assertEqual(tr.agent(self.path, report, now=at)["output_ack"][self.terminal], 3)
        replay = tr.overview(self.path, self.terminal, now=1005)
        self.assertEqual([c["data"] for c in replay["chunks"]], [c["data"] for c in output])
        self.assertEqual(tr.overview(self.path, self.terminal, after=2, now=1005)["chunks"], [replay["chunks"][2]])
        self.assertEqual(tr.overview(self.path, self.terminal, after=3, now=1005)["chunks"], [])
        tr._cache.clear()
        self.assertEqual(tr.overview(self.path, self.terminal, now=1005)["chunks"], replay["chunks"])

    def test_restart_closes_old_terminal_and_expired_input_is_never_replayed(self):
        self.start()
        tr.command(self.path, {"action": "input", "id": "c" * 32, "terminal": self.terminal, "data": "danger\r"}, now=1004)
        self.assertEqual(tr.agent(self.path, {"info": self.info}, now=1300)["operations"], [])
        new_info = dict(self.info, instance="d" * 32)
        tr.agent(self.path, {"info": new_info}, now=1301)
        self.assertEqual(tr.overview(self.path, self.terminal, now=1302)["terminal"]["state"], "closed")

    def test_unreceived_start_expiry_and_server_restart_release_capacity(self):
        payload = {"action": "start", "id": "a" * 32, "tool": "codex", "dir": "demo"}
        terminal = tr.command(self.path, payload, now=1001)["terminal"]
        tr.overview(self.path, now=1300)
        tr._cache.clear()
        self.assertEqual(tr.overview(self.path, terminal, now=1301)["terminal"]["state"], "closed")
        tr.agent(self.path, {"info": self.info}, now=1302)
        second = tr.command(self.path, dict(payload, id="c" * 32), now=1303)["terminal"]
        tr._cache.clear()
        tr._pending.clear()
        self.assertEqual(tr.overview(self.path, second, now=1304)["terminal"]["state"], "closed")
        self.assertEqual(tr.agent(self.path, {"info": self.info}, now=1305)["operations"], [])

    def test_invalid_tool_workspace_input_size_and_operation_id_are_refused(self):
        for payload in ([], {"action": "start", "id": "bad"}, {"action": "start", "id": "f" * 32, "tool": "bash", "dir": "demo"},
                        {"action": "start", "id": "f" * 32, "tool": "codex", "dir": "elsewhere"}):
            with self.assertRaises(RemoteError):
                tr.command(self.path, payload, now=1001)
        payload = self.start()
        with self.assertRaises(RemoteError):
            tr.command(self.path, dict(payload, tool="claude"), now=1004)
        for changes in ({"action": "input", "data": ""}, {"action": "input", "data": "x" * 16001},
                        {"action": "resize", "cols": 1, "rows": 20}, {"action": "resize", "cols": "80", "rows": 24}):
            with self.assertRaises(RemoteError):
                tr.command(self.path, {"id": "e" * 32, "terminal": self.terminal, **changes}, now=1005)
        with self.assertRaises(RemoteError):
            tr.command(self.path, {"id": "e" * 32, "terminal": self.terminal, "action": "close"}, now=2000)

    def test_close_error_and_cap_do_not_drop_existing_terminals(self):
        self.start()
        op = {"id": "c" * 32, "terminal": self.terminal, "action": "close"}
        tr.command(self.path, op, now=1004)
        tr.agent(self.path, {"info": self.info, "acks": [{"id": op["id"]}], "terminals": [{"id": self.terminal, "state": "closed"}]}, now=1005)
        self.assertEqual(tr.overview(self.path, self.terminal, now=1006)["terminal"]["state"], "closed")
        for n in range(8):
            tr.command(self.path, {"id": "%032x" % n, "action": "start", "tool": "claude", "dir": "demo"}, now=1007)
        with self.assertRaises(RemoteError):
            tr.command(self.path, {"id": "d" * 32, "action": "start", "tool": "claude", "dir": "demo"}, now=1008)

    def test_new_terminal_sorts_above_second_precision_history(self):
        old = {"action": "start", "id": "1" * 32, "tool": "shell", "dir": "demo"}
        tr.command(self.path, old, now=1000)
        tr.agent(self.path, {"info": self.info, "acks": [{"id": old["id"]}]}, now=1001)
        new = {"action": "start", "id": "2" * 32, "tool": "shell", "dir": "demo"}
        new_terminal = tr.command(self.path, new, now=1001.2)["terminal"]
        self.assertEqual(tr.overview(self.path, now=1002)["terminals"][0]["id"], new_terminal)

    def test_saved_conversations_are_listed_named_and_opened_once(self):
        sid, other = "12345678-1234-1234-1234-123456789abc", "22345678-1234-1234-1234-123456789abc"
        sessions = [{"id": sid, "tool": "claude", "dir": "demo", "title": " 修复面板 ", "updated": 5000, "live": True, "status": "busy"},
                    {"id": other, "tool": "codex", "dir": "demo", "title": "旧对话", "updated": 4000},
                    {"id": "bad", "tool": "claude", "dir": "demo", "title": "x", "updated": 1}, {"id": other, "tool": "bash", "dir": "demo", "title": "x", "updated": 1}]
        tr.agent(self.path, {"info": self.info, "sessions": sessions}, now=1001)
        listed = tr.overview(self.path, now=1002)["sessions"]
        self.assertEqual([(s["id"], s["title"], s["live"], s["status"], s["terminal"]) for s in listed], [(sid, "修复面板", True, "busy", ""), (other, "旧对话", False, "", "")])
        for wrong in ({"session": sid, "tool": "claude", "takeover": "yes"}, {"session": sid, "tool": "codex"}, {"session": "12345678-1234-1234-1234-123456789abd", "tool": "claude"}, {"session": 5, "tool": "claude"}):
            with self.assertRaises(RemoteError):
                tr.command(self.path, {"action": "start", "id": "1" * 32, "dir": "demo", **wrong}, now=1003)
        start = {"action": "start", "id": "2" * 32, "tool": "claude", "dir": "demo", "session": sid, "takeover": True}
        terminal = tr.command(self.path, start, now=1004)["terminal"]
        self.assertEqual(tr.agent(self.path, {"info": self.info, "sessions": sessions}, now=1005)["operations"][0]["takeover"], True)
        with self.assertRaises(RemoteError):
            tr.command(self.path, dict(start, id="3" * 32), now=1006)
        tr.agent(self.path, {"info": self.info, "sessions": sessions, "acks": [{"id": start["id"]}],
                             "terminals": [{"id": terminal, "state": "running", "session": other, "status": "idle"}]}, now=1007)
        view = tr.overview(self.path, now=1008)
        self.assertEqual((view["terminals"][0]["title"], view["terminals"][0]["status"]), ("旧对话", "idle"))
        self.assertEqual([s["terminal"] for s in view["sessions"]], ["", terminal])
        tr.command(self.path, {"action": "rename", "id": "4" * 32, "terminal": terminal, "title": "我的名字"}, now=1009)
        self.assertEqual(tr.overview(self.path, terminal, now=1010)["terminal"]["title"], "我的名字")

    def test_ended_terminal_keeps_only_its_final_output_and_long_output_arrives_whole(self):
        self.start()
        pieces = [{"terminal": self.terminal, "seq": i, "data": chr(64 + i % 26) * 12000} for i in range(1, 61)]
        for at in range(0, 60, 30):
            tr.agent(self.path, {"info": self.info, "output": pieces[at:at + 30]}, now=1004)
        after, received, replies = 0, [], 0
        while True:
            reply = tr.overview(self.path, self.terminal, after=after, now=1005)
            if not reply["chunks"]:
                break
            self.assertFalse(reply["reset"])
            received += [c["data"] for c in reply["chunks"]]
            after, replies = reply["after"], replies + 1
        self.assertEqual((received, after, reply["terminal"]["seq"]), ([p["data"] for p in pieces], 60, 60))
        self.assertGreater(replies, 3)
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "closed"}],
                             "output": [{"terminal": self.terminal, "seq": 61, "data": "最后一行"}]}, now=1006)
        tr._cache.clear()
        kept = tr.overview(self.path, self.terminal, after=60, now=1007)
        self.assertEqual(([c["data"] for c in kept["chunks"]], kept["terminal"]["state"]), (["最后一行"], "closed"))
        stored = tr._state(self.path)["threads"][self.terminal]
        self.assertLessEqual(stored["size"], tr.CLOSED_LIMIT)
        self.assertEqual(stored["output"][-1]["data"], "最后一行")
        self.assertTrue(tr.overview(self.path, self.terminal, after=3, now=1008)["reset"])
    def test_projects_are_listed_and_changed_only_through_the_computer(self):
        info = dict(self.info, workspaces=["demo", "组会"], projects=[
            {"name": "demo", "path": "E:\\01\\demo", "fixed": True, "exists": True}, {"name": "组会", "path": "G:\\06 数据分析", "fixed": False, "exists": True},
            {"name": "", "path": "x"}, "bad"], candidates=[{"name": "EpiAgentKit", "path": "E:\\05\\EpiAgentKit", "updated": 7, "live": True, "tools": "claude codex shell"}])
        tr.agent(self.path, {"info": info}, now=1001)
        device = tr.overview(self.path, now=1002)["device"]
        self.assertEqual([(x["name"], x["fixed"]) for x in device["projects"]], [("demo", True), ("组会", False)])
        self.assertEqual((device["candidates"][0]["tools"], device["candidates"][0]["live"]), (["claude", "codex"], True))
        for bad in ({"action": "project_add", "path": ""}, {"action": "project_add", "path": "E:\\a", "create": "yes"}, {"action": "project_remove", "name": "没有"},
                    {"action": "project_remove", "name": "demo"}, {"action": "project_rename", "name": "组会", "to": " "}, {"action": "project_rename", "name": "组会", "to": "x" * 41}):
            with self.assertRaises(RemoteError):
                tr.command(self.path, dict(bad, id="1" * 32), now=1003)
        started = tr.command(self.path, {"action": "start", "id": "2" * 32, "tool": "shell", "dir": "组会"}, now=1004)["terminal"]
        rename = {"action": "project_rename", "id": "3" * 32, "name": "组会", "to": "周会"}
        self.assertEqual(tr.command(self.path, rename, now=1005)["state"], "queued")
        sent = [o for o in tr.agent(self.path, {"info": info}, now=1006)["operations"] if o["action"] == "project_rename"]
        self.assertEqual((sent[0]["name"], sent[0]["to"], sent[0]["terminal"]), ("组会", "周会", ""))
        tr.agent(self.path, {"info": info, "acks": [{"id": rename["id"]}]}, now=1007)
        self.assertEqual(tr.command(self.path, rename, now=1008)["state"], "done")
        self.assertEqual(tr.overview(self.path, started, now=1009)["terminal"]["dir"], "周会")
        add = {"action": "project_add", "id": "4" * 32, "path": "E:\\没有"}
        tr.command(self.path, add, now=1010)
        tr.agent(self.path, {"info": info, "acks": [{"id": add["id"], "error": "电脑上没有这个文件夹"}]}, now=1011)
        self.assertEqual(tr.command(self.path, add, now=1012)["error"], "电脑上没有这个文件夹")
    def test_waiting_requests_answer_when_something_arrives(self):
        import threading
        import time
        self.start()
        instance = self.info["instance"]
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]})  # reported just now
        self.assertEqual(tr.pull(self.path, {"instance": instance, "wait": 0}), {"operations": []})
        with self.assertRaises(RemoteError):
            tr.pull(self.path, {"instance": "x"})
        answers = {}
        waiting = threading.Thread(target=lambda: answers.update(pull=tr.pull(self.path, {"instance": instance, "wait": 10})))
        began = time.monotonic()
        waiting.start()
        time.sleep(0.2)
        tr.command(self.path, {"action": "input", "id": "1" * 32, "terminal": self.terminal, "data": "x"})
        waiting.join(5)
        self.assertEqual([(o["action"], o["data"], o["terminal"]) for o in answers["pull"]["operations"]], [("input", "x", self.terminal)])
        self.assertLess(time.monotonic() - began, 3)
        self.assertEqual(len(tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]})["operations"]), 1)
        self.assertEqual(tr.pull(self.path, {"instance": instance, "wait": 0}), {"operations": []})
        self.assertEqual(tr.pull(self.path, {"instance": "c" * 32, "wait": 0}), {"operations": []})

        reading = threading.Thread(target=lambda: answers.update(read=tr.overview(self.path, self.terminal, after=0, wait=10)))
        began = time.monotonic()
        reading.start()
        time.sleep(0.2)
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}], "output": [{"terminal": self.terminal, "seq": 1, "data": "回显"}]})
        reading.join(5)
        self.assertEqual([c["data"] for c in answers["read"]["chunks"]], ["回显"])
        self.assertLess(time.monotonic() - began, 3)
        began = time.monotonic()
        self.assertEqual(tr.overview(self.path, self.terminal, after=1, wait=0.4)["chunks"], [])
        self.assertGreaterEqual(time.monotonic() - began, 0.35)
        changing = threading.Thread(target=lambda: answers.update(state=tr.overview(self.path, self.terminal, after=1, wait=10)))
        changing.start()
        time.sleep(0.2)
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "running", "status": "busy"}]})
        changing.join(5)
        self.assertEqual(answers["state"]["terminal"]["status"], "busy")

    def test_output_is_saved_in_intervals_and_a_gap_after_a_restart_does_not_block(self):
        self.start()
        report = lambda seq, now: tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}],
                                                      "output": [{"terminal": self.terminal, "seq": seq, "data": "片%d" % seq}]}, now=now)["output_ack"][self.terminal]
        self.assertEqual(report(1, 2000.0), 1)
        self.assertEqual(report(2, 2000.5), 2)
        self.assertEqual(report(3, 2002.5), 3)
        self.assertEqual(report(4, 2002.6), 4)
        tr._cache.clear()
        tr._unsaved.clear()
        self.assertEqual(tr.overview(self.path, self.terminal, now=2003)["terminal"]["seq"], 3)
        self.assertEqual(report(4, 2003.1), 4)
        self.assertEqual(report(9, 2003.2), 9)
        self.assertEqual(report(9, 2003.3), 9)
        self.assertEqual([c["data"] for c in tr.overview(self.path, self.terminal, after=3, now=2004)["chunks"]], ["片4", "片9"])
    def test_saving_adds_only_new_output_and_survives_a_restart_an_old_list_and_a_cut_line(self):
        import json
        import os
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        report = lambda seq, now: tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": seq, "data": "片%d\n" % seq}]), now=now)
        file = os.path.join(tr._folder(self.path), self.terminal + ".jsonl")
        def read(name):
            with open(name, encoding="utf-8") as stream:
                return stream.read()
        report(1, 3000.0)
        report(2, 3002.5)
        before = read(file)
        self.assertNotIn("output", json.loads(read(self.path))["threads"][self.terminal])
        saved = []
        write = tr._write
        tr._write = lambda path, text, mode="w": (saved.append((os.path.basename(path), mode, text)), write(path, text, mode))[1]
        self.addCleanup(setattr, tr, "_write", write)
        report(3, 3005.0)
        tr._save(self.path, tr._state(self.path))
        self.assertEqual([s for s in saved if s[0].startswith(self.terminal)], [(self.terminal + ".jsonl", "a", '{"seq": 3, "data": "片3\\n"}\n')])
        self.assertEqual(read(file), before + '{"seq": 3, "data": "片3\\n"}\n')
        restarted = lambda: (tr._cache.clear(), tr._unsaved.clear(), [c["data"] for c in tr.overview(self.path, self.terminal, now=3006)["chunks"]])[2]
        self.assertEqual(restarted(), ["片1\n", "片2\n", "片3\n"])
        with open(file, "a", encoding="utf-8") as stream:
            stream.write('{"seq": 4, "da')                       # the relay was ended in the middle of a line
        self.assertEqual(restarted(), ["片1\n", "片2\n", "片3\n"])
        report(4, 3010.0)
        tr._save(self.path, tr._state(self.path))
        self.assertEqual(restarted(), ["片1\n", "片2\n", "片3\n", "片4\n"])
        # A list written by an earlier version holds the output itself; it is read and then kept the new way.
        state = json.loads(read(self.path))
        state["threads"][self.terminal]["output"] = [{"seq": 1, "data": "旧"}, {"seq": 4, "data": "的"}]
        os.remove(file)
        with open(self.path, "w", encoding="utf-8") as stream:
            json.dump(state, stream, ensure_ascii=False)
        self.assertEqual(restarted(), ["旧", "的"])
        report(5, 3020.0)
        tr._save(self.path, tr._state(self.path))
        self.assertEqual(restarted(), ["旧", "的", "片5\n"])
        del tr._state(self.path)["threads"][self.terminal]
        tr._save(self.path, tr._state(self.path))
        self.assertFalse(os.path.exists(file))

    def test_a_long_history_does_not_slow_new_output(self):
        import time
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        piece = "x" * 60 + "\r\n"
        for n in range(0, 30000, 200):
            tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": n + i + 1, "data": piece} for i in range(200)]), now=4000 + n / 1000)
        began = time.perf_counter()
        for n in range(30000, 30100):
            tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": n + 1, "data": piece}]), now=4100 + (n - 30000) * 0.05)
            self.assertEqual(len(tr.overview(self.path, self.terminal, after=n, now=4100 + (n - 30000) * 0.05)["chunks"]), 1)
        self.assertLess((time.perf_counter() - began) / 100, 0.005)      # it was a tenth of a second and more for each piece

    def test_finished_operations_do_not_slow_later_requests(self):
        import time
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        for n in range(6000):       # a few minutes of scrolling a full-screen program
            key = "%032x" % (n + 1)
            tr.command(self.path, {"action": "input", "id": key, "terminal": self.terminal, "data": "x"}, now=1004)
            tr.agent(self.path, dict(live, acks=[{"id": key}]), now=1004)
        self.assertEqual((len(tr._pending), len(tr._pending.queued)), (6001, 0))
        began = time.perf_counter()
        for n in range(200):
            tr.overview(self.path, self.terminal, after=0, now=1005)
        self.assertLess((time.perf_counter() - began) / 200, 0.0005)
        self.assertEqual(tr.command(self.path, {"action": "input", "id": "%032x" % 1, "terminal": self.terminal, "data": "x"}, now=1006)["state"], "done")
        tr.overview(self.path, now=1004 + 601)
        self.assertEqual(len(tr._pending), 0)

    def test_output_is_streamed_as_it_arrives_and_a_wake_up_between_look_and_wait_is_not_lost(self):
        import threading
        import time
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        tr.agent(self.path, live)
        got, times = [], []
        began = time.monotonic()

        def read():
            for item in tr.stream(self.path, self.terminal, after=0, seconds=2.5, beat=1):
                self.assertNotIn("tick", item)
                got.append(item)
                times.append(time.monotonic() - began)
        reader = threading.Thread(target=read)
        reader.start()
        time.sleep(0.3)
        tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": 1, "data": "一"}]))
        time.sleep(0.3)
        tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": 2, "data": "二"}, {"terminal": self.terminal, "seq": 3, "data": "三"}]))
        time.sleep(0.3)
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "running", "status": "busy"}]})
        reader.join(6)
        self.assertFalse(reader.is_alive())
        self.assertEqual("".join(c["data"] for item in got for c in item["chunks"]), "一二三")
        self.assertEqual([c["seq"] for item in got for c in item["chunks"]], [1, 2, 3])
        self.assertEqual(got[0]["chunks"], [], "the first line tells the state at once")
        self.assertLess(times[0], 0.2)
        arrived = [t for item, t in zip(got, times) if item["chunks"]]
        self.assertTrue(0.25 < arrived[0] < 0.6 and 0.55 < arrived[1] < 0.9, arrived)
        self.assertTrue(any(item["terminal"].get("status") == "busy" for item in got))
        self.assertGreaterEqual(len([item for item in got if not item["chunks"]]), 3, "state line, status change and heartbeats")
        self.assertLess(times[-1], 3.6)
        self.assertNotIn("tick", tr.overview(self.path, self.terminal, after=3))
        self.assertNotIn("tick", tr.overview(self.path, self.terminal, after=3, wait=0.2))
        # A wake-up after the look but before the wait must end the wait at once.
        seen = tr._overview(self.path, self.terminal, 3, time.time())["tick"]
        tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": 4, "data": "四"}]))
        began = time.monotonic()
        tr._wait(seen, 3)
        self.assertLess(time.monotonic() - began, 0.5)
        with self.assertRaises(RemoteError):
            next(tr.stream(self.path, "0" * 32))
    def test_phase_tells_working_asking_finished_and_ended_apart(self):
        self.start()
        live = lambda status="", **more: dict({"info": self.info, "terminals": [dict({"id": self.terminal, "state": "running", "status": status}, **more)]})
        phase = lambda now: tr.overview(self.path, now=now)["terminals"][0]
        self.assertEqual(phase(1004)["phase"], "idle")
        self.assertFalse(phase(1004).get("done"))                      # it has not worked yet
        key = lambda n, data, now: tr.command(self.path, {"action": "input", "id": "%032x" % n, "terminal": self.terminal, "data": data}, now=now)
        # the banner a program prints when it opens is not work, and neither is the echo of a key
        tr.agent(self.path, dict(live(), output=[{"terminal": self.terminal, "seq": 1, "data": "Welcome\r\n"}]), now=1005)
        self.assertEqual(phase(1005)["phase"], "idle")
        key(1, "n", 1006)
        tr.agent(self.path, dict(live(), output=[{"terminal": self.terminal, "seq": 2, "data": "n"}]), now=1006.5)
        self.assertEqual(phase(1006.5)["phase"], "idle")
        self.assertFalse(phase(1006.5).get("done"))
        key(2, "pm run build\r", 1008)
        tr.agent(self.path, dict(live(), output=[{"terminal": self.terminal, "seq": 3, "data": "building\r\n"}]), now=1010)
        self.assertEqual(phase(1010)["phase"], "busy")                 # without a status of its own, fresh output means work
        tr.agent(self.path, live(), now=1016)
        done = phase(1016)
        self.assertEqual((done["phase"], done["done"]), ("idle", True))
        self.assertNotIn("out_at", done)
        self.assertNotIn("touched", done)
        # opening the terminal on the phone resizes it; the screen drawn again must not look like new work
        tr.command(self.path, {"action": "resize", "id": "%032x" % 3, "terminal": self.terminal, "cols": 50, "rows": 30}, now=1017)
        tr.agent(self.path, dict(live(), output=[{"terminal": self.terminal, "seq": 4, "data": "\x1b[2Jbuilding\r\n"}]), now=1018)
        self.assertEqual(phase(1018)["phase"], "idle")
        self.assertEqual(phase(1018)["phase_at"], done["phase_at"])
        # a question on the screen that stays there
        tr.agent(self.path, dict(live(), output=[{"terminal": self.terminal, "seq": 5, "data": "\x1b[1mDo you want to proceed?\x1b[0m\r\n\x1b[36m> 1. Yes\x1b[0m\r\n  2. No"}]), now=1020)
        self.assertEqual(phase(1020)["phase"], "busy")                 # just written: still drawing
        tr.agent(self.path, live(), now=1023)
        self.assertEqual(phase(1023)["phase"], "confirm")
        # Claude Code reports its own status; a question only counts while it says it is working
        tr.agent(self.path, live("idle"), now=1030)
        self.assertEqual(phase(1030)["phase"], "idle")
        tr.agent(self.path, live("busy"), now=1031)
        self.assertEqual(phase(1031)["phase"], "confirm")
        tr.agent(self.path, dict(live("busy"), output=[{"terminal": self.terminal, "seq": 6, "data": "x" * 2000}]), now=1040)
        tr.agent(self.path, live("busy"), now=1045)
        self.assertEqual(phase(1045)["phase"], "busy")                 # answered: the question has scrolled out of the last screen
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "closed", "exit_code": 3}]}, now=1050)
        self.assertEqual(phase(1050)["phase"], "failed")

    def test_overview_tells_what_a_running_terminal_last_said(self):
        self.start()
        live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running", "cols": 40, "rows": 8}]}
        said = lambda now: tr.overview(self.path, now=now)["terminals"][0]["said"]
        self.assertEqual(said(1004), "")
        # a message that wraps, the frame of the prompt under it, and a status line that is drawn over and over
        tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": 1, "data":
            "\x1b[2J\x1b[1;1H\u25cf \x1b[1mold answer\x1b[0m\r\n\r\n\u25cf Updated the README and\r\n  ran the tests\r\n\r\n" + "\u2500" * 40 + "\r\n> \r\n  ? for shortcuts"}]), now=1005)
        self.assertEqual(said(1006), "Updated the README and ran the tests")
        tr.agent(self.path, dict(live, output=[{"terminal": self.terminal, "seq": 2, "data": "\x1b[3;1H\x1b[2K\u25cf \u4e2d\u6587\u6d88\u606f\x1b[4;1H\x1b[2K"}]), now=1007)
        self.assertEqual(said(1008), "\u4e2d\u6587\u6d88\u606f")
        # after a restart of the relay the screen is drawn again from the output that was kept
        tr._screens.clear()
        self.assertEqual(said(1009), "\u4e2d\u6587\u6d88\u606f")
        tr.agent(self.path, {"info": self.info, "terminals": [{"id": self.terminal, "state": "closed", "exit_code": 0}]}, now=1010)
        self.assertEqual(said(1011), "")

    def test_screen_follows_cursor_erasing_scrolling_and_wide_characters(self):
        from screen import Screen
        view = Screen(10, 3)
        view.feed("one\r\ntwo\r\nthree\r\nfour")
        self.assertEqual(view.lines(), ["two", "three", "four"])
        view.feed("\x1b[1;1H\x1b[Kab\x1b[3G\u4e2d\u6587\x1b[2;3H\x1b[1K")
        self.assertEqual(view.lines(), ["ab\u4e2d\u6587", "   ee", "four"])
        view.feed("\x1b[?1049h\x1b[Hmenu\x1b[?1049l")
        self.assertEqual(view.lines()[2], "four")
        view.feed("\x1b[3;1H\x1b")            # a sequence cut in two by the end of a piece
        view.feed("[2Kdone")
        self.assertEqual(view.lines()[2], "done")
        view.resize(4, 2)
        self.assertEqual(view.lines(), ["   e", "done"])


if __name__ == "__main__":
    unittest.main()
