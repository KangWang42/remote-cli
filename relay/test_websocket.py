import base64
import io
import json
import socket
import struct
import sys
import tempfile
import threading
import unittest
from pathlib import Path

import relay as tr
import server
import websocket as framing

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tests"))
from tunnel_check import Client, operation
from ws_client import Socket


def frame(data, opcode=1, final=True):
    mask = b"abcd"
    length = len(data)
    head = bytes(((128 if final else 0) | opcode, 128 | length)) if length < 126 else bytes(((128 if final else 0) | opcode, 254)) + struct.pack("!H", length)
    return head + mask + bytes(b ^ mask[n % 4] for n, b in enumerate(data))


class Sink:
    def __init__(self):
        self.frames = []

    def sendall(self, data):
        self.frames.append(data)


class FramingTests(unittest.TestCase):
    def test_fragmented_unicode_and_interleaved_ping(self):
        data = json.dumps({"data": "中文"}, ensure_ascii=False).encode("utf-8")
        sink = Sink()
        connection = framing.Connection(io.BytesIO(frame(data[:11], final=False) + frame(b"alive", opcode=9) + frame(data[11:], opcode=0)), sink)
        self.assertEqual(json.loads(connection.receive()), {"data": "中文"})
        self.assertEqual(sink.frames, [b"\x8a\x05alive"])

    def test_rejects_unmasked_oversize_invalid_utf8_and_control_frames(self):
        cases = [(b"\x81\x01x", 1002), (b"\x81\xff" + struct.pack("!Q", framing.MAX_MESSAGE + 1), 1009),
                 (frame(b"\xff"), 1007), (frame(b"x", opcode=9, final=False), 1002),
                 (frame(b"x", opcode=0), 1002), (frame(b"x", opcode=2), 1003),
                 (frame(struct.pack("!H", 1006), opcode=8), 1002)]
        for data, code in cases:
            with self.subTest(code=code, data=data[:2]):
                with self.assertRaises(framing.ProtocolError) as found:
                    framing.Connection(io.BytesIO(data), Sink()).receive()
                self.assertEqual(found.exception.code, code)

    def test_extended_output_length_and_standard_accept_key(self):
        headers = {"Upgrade": "websocket", "Connection": "keep-alive, Upgrade", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ=="}
        self.assertEqual(framing.accept_key(headers), "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=")
        for key in ("bad", base64.b64encode(b"short").decode()):
            with self.assertRaises(framing.ProtocolError):
                framing.accept_key(dict(headers, **{"Sec-WebSocket-Key": key}))
        sink = Sink()
        framing.Connection(io.BytesIO(), sink).send({"data": "x" * 180000})
        self.assertEqual(sink.frames[0][:2], b"\x81\x7f")
        self.assertEqual(struct.unpack("!Q", sink.frames[0][2:10])[0], len(sink.frames[0]) - 10)


class WebSocketIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        tr._cache.clear()
        tr._pending.clear()
        tr._unsaved.clear()
        tr._sessions.clear()
        self.server = server.make_server("127.0.0.1", 0, self.temp.name, str(Path(__file__).resolve().parents[1] / "web"), password="test-password-1234")
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.origin = "http://127.0.0.1:%d" % self.server.server_port
        self.client = Client(self.origin, timeout=3)
        self.client.call("/api/login", {"password": "test-password-1234"})
        self.info = {"instance": "b" * 32, "enabled": True, "tools": ["shell"], "workspaces": ["demo"]}
        tr.agent(self.server.store, {"info": self.info})
        start = operation("", "start", tool="shell", dir="demo")
        self.terminal = tr.command(self.server.store, start)["terminal"]
        self.live = {"info": self.info, "terminals": [{"id": self.terminal, "state": "running"}]}
        tr.agent(self.server.store, dict(self.live, acks=[{"id": start["id"]}]))
        self.sockets = []

    def test_a_ticket_signs_in_once_and_only_for_a_minute(self):
        ticket = self.client.call("/api/ticket", {})["ticket"]
        fresh = Client(self.origin, timeout=3)
        try:
            self.assertRaisesRegex(RuntimeError, "HTTP 401", fresh.call, "/api/terminal")
            self.assertTrue(fresh.call("/api/login", {"ticket": ticket})["ok"])
            self.assertIn("device", fresh.call("/api/terminal", None))
        finally:
            fresh.close()
        again = Client(self.origin, timeout=3)
        try:
            self.assertRaisesRegex(RuntimeError, "HTTP 401", again.call, "/api/login", {"ticket": ticket})       # used up
            # a request that names a ticket is judged by the ticket alone
            self.assertRaisesRegex(RuntimeError, "HTTP 401", again.call, "/api/login", {"ticket": "", "password": "wrong"})
            self.assertRaisesRegex(RuntimeError, "HTTP 401", again.call, "/api/ticket", {})       # only for someone signed in
        finally:
            again.close()
        tickets = self.server.tickets
        late = tickets.make()
        tickets.until = {key: 0 for key in tickets.until}
        self.assertFalse(tickets.take(late))

    def tearDown(self):
        for ws in self.sockets:
            ws.close()
        self.client.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)
        self.temp.cleanup()

    def connect(self, after=0, cookie=None):
        ws = Socket(self.origin, "/api/terminal/ws?terminal=%s&after=%d" % (self.terminal, after), self.client.cookie["Cookie"] if cookie is None else cookie, timeout=3)
        self.sockets.append(ws)
        return ws

    def next_type(self, ws, kind):
        for _ in range(10):
            item = ws.receive(3)
            if item.get("t") == kind:
                return item
        self.fail("message type did not arrive")

    def test_push_input_pipeline_and_retry_keep_order_without_duplicates(self):
        ws = self.connect()
        self.assertEqual(ws.status, 101)
        self.assertEqual(ws.receive()["t"], "out")
        ops = [operation(self.terminal, "input", data=text) for text in ("a", "中", "\r")]
        for op in ops:
            ws.send(op)
        acks = [self.next_type(ws, "ack") for _ in ops]
        self.assertEqual([a["id"] for a in acks], [o["id"] for o in ops])
        ws.send(ops[0])
        self.assertEqual(self.next_type(ws, "ack")["id"], ops[0]["id"])
        queued = tr.pull(self.server.store, {"instance": self.info["instance"], "wait": 0})["operations"]
        self.assertEqual([o["data"] for o in queued], ["a", "中", "\r"])
        tr.agent(self.server.store, dict(self.live, output=[{"terminal": self.terminal, "seq": 1, "data": "实时输出"}]))
        self.assertEqual(self.next_type(ws, "out")["chunks"], [{"seq": 1, "data": "实时输出"}])
        resumed = self.connect(after=1)
        self.assertEqual(resumed.receive()["chunks"], [])
        self.client.call("/api/terminal", ops[0])
        self.assertEqual(len(tr._pending), 4)  # one start and three inputs, across both transports

    def test_no_auth_or_cross_origin_or_missing_origin_cannot_upgrade(self):
        self.assertEqual(self.connect(cookie="").status, 401)
        headers = {"Cookie": self.client.cookie["Cookie"], "Upgrade": "websocket", "Connection": "Upgrade",
                   "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": base64.b64encode(b"a" * 16).decode()}
        for origin in (None, "https://other.example"):
            connection = self.client.connect()
            extra = dict(headers)
            if origin:
                extra["Origin"] = origin
            connection.request("GET", "/api/terminal/ws?terminal=" + self.terminal, headers=extra)
            reply = connection.getresponse()
            self.assertEqual(reply.status, 403)
            reply.read()

    def test_revoked_cookie_cannot_send_operations(self):
        ws = self.connect()
        ws.receive()
        self.client.call("/api/logout", {})
        ws.send(operation(self.terminal, "input", data="should not execute"))
        with self.assertRaises(ConnectionError):
            ws.receive(3)
        self.assertEqual(len(tr._pending), 1)

    def test_connection_cannot_target_a_different_terminal(self):
        ws = self.connect()
        ws.receive()
        ws.send(operation("c" * 32, "input", data="should not execute"))
        with self.assertRaises(ConnectionError):
            ws.receive(3)
        self.assertEqual(len(tr._pending), 1)

    def test_pages_are_kept_by_the_reader_and_text_is_sent_compressed(self):
        import gzip
        import http.client
        port = self.server.server_port

        def get(path, **headers):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
            try:
                connection.request("GET", path, headers=dict(headers, **self.client.cookie))
                reply = connection.getresponse()
                return reply.status, {k.lower(): v for k, v in reply.getheaders()}, reply.read()
            finally:
                connection.close()

        source = (Path(__file__).resolve().parents[1] / "web" / "terminal" / "vendor" / "xterm.js").read_bytes()
        status, plain, body = get("/terminal/vendor/xterm.js")
        self.assertEqual((status, body, plain["cache-control"], "content-encoding" in plain), (200, source, "no-cache", False))
        status, packed, body = get("/terminal/vendor/xterm.js", **{"Accept-Encoding": "gzip, br"})
        self.assertEqual((status, packed["content-encoding"], gzip.decompress(body)), (200, "gzip", source))
        self.assertLess(len(body), len(source) // 3)
        self.assertNotEqual(packed["etag"], plain["etag"])
        # a copy the reader holds is confirmed without being sent again; another copy is not
        for headers, tag in (({}, plain["etag"]), ({"Accept-Encoding": "gzip"}, packed["etag"])):
            status, kept, body = get("/terminal/vendor/xterm.js", **dict(headers, **{"If-None-Match": tag}))
            self.assertEqual((status, body, kept["etag"], kept["x-content-type-options"]), (304, b"", tag, "nosniff"))
        self.assertEqual(get("/terminal/vendor/xterm.js", **{"If-None-Match": '"older"'})[0], 200)
        # a font is compressed already
        self.assertNotIn("content-encoding", get("/terminal/vendor/jetbrains-mono-400.woff2", **{"Accept-Encoding": "gzip"})[1])
        # what a terminal printed is never kept by the reader, and a long answer is compressed for one that asks
        tr.agent(self.server.store, dict(self.live, output=[{"terminal": self.terminal, "seq": 1, "data": "\x1b[2K\x1b[1Gline of output\r\n" * 400}]))
        status, headers, body = get("/api/terminal?terminal=%s&after=0" % self.terminal, **{"Accept-Encoding": "gzip"})
        self.assertEqual((status, headers["cache-control"], headers["content-encoding"]), (200, "no-store", "gzip"))
        self.assertEqual(len(json.loads(gzip.decompress(body))["chunks"][0]["data"]), 400 * 24)
        status, headers, body = get("/api/terminal?terminal=%s&after=0" % self.terminal)
        self.assertEqual((headers["cache-control"], "content-encoding" in headers, len(json.loads(body)["chunks"][0]["data"])), ("no-store", False, 400 * 24))

    def test_invalid_input_returns_ack_and_keeps_connection_open(self):
        ws = self.connect()
        ws.receive()
        ws.send(operation(self.terminal, "input", data=""))
        self.assertEqual(self.next_type(ws, "ack")["status"], 400)
        ws.send(operation(self.terminal, "input", data="ok"))
        self.assertEqual(self.next_type(ws, "ack")["status"], 200)


if __name__ == "__main__":
    unittest.main()
