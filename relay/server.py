"""remote-cli relay: one small HTTP server between the phone and the computer.

    python server.py --host 127.0.0.1 --port 8722 --data ./data

It serves the web client, checks the password, and passes terminal input and output along. It never runs
anything itself: the program on the computer does. Standard library only.

Put it behind HTTPS (a reverse proxy or a tunnel) whenever it is reachable from outside your own network.
"""
import argparse
import hashlib
import hmac
import json
import os
import secrets
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # an embedded Python does not add the script's folder
import relay  # noqa: E402
import websocket  # noqa: E402

VERSION = "0.3.0"
SESSION_DAYS = 90
LOGIN_TRIES, LOGIN_LOCK, LOGIN_TRIES_ALL = 6, 900, 40
BODY_LIMIT = 4 * 1024 * 1024
TYPES = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
         ".woff2": "font/woff2", ".json": "application/json; charset=utf-8", ".svg": "image/svg+xml", ".png": "image/png",
         ".ico": "image/x-icon", ".webmanifest": "application/manifest+json"}
SECURITY = {"X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer", "X-Frame-Options": "DENY",
            "Content-Security-Policy": "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
                                       "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"}


class Sessions:
    """Sign-ins that survive a restart. Only a hash of each token is kept."""

    def __init__(self, path):
        self.path, self.lock = path, threading.Lock()
        try:
            with open(path, encoding="utf-8") as stream:
                self.items = {k: float(v) for k, v in json.load(stream).items()}
        except (OSError, ValueError, AttributeError):
            self.items = {}

    def _save(self):
        tmp = self.path + ".tmp"
        with os.fdopen(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
            json.dump(self.items, stream)
        os.replace(tmp, self.path)

    def create(self):
        token = secrets.token_hex(32)
        with self.lock:
            now = time.time()
            self.items = {k: v for k, v in self.items.items() if v > now}
            self.items[hashlib.sha256(token.encode()).hexdigest()] = now + SESSION_DAYS * 86400
            self._save()
        return token

    def valid(self, token):
        if not token or len(token) != 64:
            return False
        key = hashlib.sha256(token.encode()).hexdigest()
        with self.lock:
            expires = self.items.get(key, 0)
            now = time.time()
            if expires <= now:
                return False
            if expires - now < (SESSION_DAYS - 1) * 86400:      # used: good for another full period
                self.items[key] = now + SESSION_DAYS * 86400
                self._save()
            return True

    def remove(self, token):
        with self.lock:
            if self.items.pop(hashlib.sha256((token or "").encode()).hexdigest(), None) is not None:
                self._save()


class Attempts:
    """Wrong passwords: a limit per address and one for all addresses together."""

    def __init__(self):
        self.lock, self.failed = threading.Lock(), {}

    def _recent(self, key, now):
        self.failed[key] = [t for t in self.failed.get(key, []) if now - t < LOGIN_LOCK]
        return self.failed[key]

    def blocked(self, address):
        with self.lock:
            now = time.time()
            return len(self._recent(address, now)) >= LOGIN_TRIES or len(self._recent("*", now)) >= LOGIN_TRIES_ALL

    def fail(self, address):
        with self.lock:
            now = time.time()
            self._recent(address, now).append(now)
            self._recent("*", now).append(now)
            for key in [k for k, v in self.failed.items() if not v]:
                del self.failed[key]

    def clear(self, address):
        with self.lock:
            self.failed.pop(address, None)


def read_password(data):
    """The password from RCLI_PASSWORD, else from <data>/password.txt; one is made on first start."""
    given = os.environ.get("RCLI_PASSWORD", "").strip()
    path = os.path.join(data, "password.txt")
    if given:
        return given, False
    try:
        with open(path, encoding="utf-8") as stream:
            saved = stream.read().strip()
        if saved:
            return saved, False
    except OSError:
        pass
    made = "-".join(secrets.token_hex(3) for _ in range(4))
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as stream:
        stream.write(made + "\n")
    return made, True


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "remote-cli/" + VERSION
    sys_version = ""

    def setup(self):
        super().setup()
        # Headers and small terminal updates must not wait for a delayed TCP acknowledgment.
        self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def handle(self):
        try:
            super().handle()
        except (ConnectionError, TimeoutError):
            pass  # readers can cancel a held request when the app goes into the background

    def log_message(self, *args):       # terminal input and output must never reach a log
        pass

    # ---- helpers
    def _address(self):
        peer = self.client_address[0]
        if peer in ("127.0.0.1", "::1"):                     # a proxy or tunnel on this machine
            forwarded = self.headers.get("CF-Connecting-IP") or (self.headers.get("X-Forwarded-For") or "").split(",")[0]
            return forwarded.strip()[:64] or peer
        return peer

    def _secure(self):
        return self.headers.get("X-Forwarded-Proto", "").lower() == "https" or self.headers.get("CF-Visitor", "").find("https") >= 0

    def _token(self):
        header = self.headers.get("Authorization", "")
        if header.startswith("Bearer "):
            return header[7:].strip()
        for part in self.headers.get("Cookie", "").split(";"):
            name, _, value = part.strip().partition("=")
            if name == "rcli":
                return value
        return ""

    def _authed(self):
        return self.server.sessions.valid(self._token())

    def _send(self, code, body, kind="application/json; charset=utf-8", extra=None):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        for key, value in dict(SECURITY, **(extra or {})).items():
            self.send_header(key, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(data)

    def _json(self, code, value, extra=None):
        self._send(code, json.dumps(value, ensure_ascii=False), extra=extra)

    def _error(self, exc, fallback):
        self._json(400, {"error": str(exc) if isinstance(exc, relay.RemoteError) else fallback})

    def _static(self, path):
        root = self.server.web
        name = "index.html" if path in ("", "/") else path.lstrip("/")
        if name.endswith("/"):
            name += "index.html"
        full = os.path.normpath(os.path.join(root, *name.split("/")))
        kind = TYPES.get(os.path.splitext(full)[1].lower())
        if not full.startswith(root + os.sep) or kind is None or not os.path.isfile(full):
            return self._json(404, {"error": "not found"})
        with open(full, "rb") as stream:
            self._send(200, stream.read(), kind)

    # ---- routes
    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        url = urlsplit(self.path)
        path, query = url.path, parse_qs(url.query)
        first = lambda key, default="": (query.get(key) or [default])[0]
        if not path.startswith("/api/"):
            return self._static(path)
        if path == "/api/session":
            return self._json(200, {"signed_in": self._authed(), "version": VERSION})
        if not self._authed():
            return self._json(401, {"error": "auth"})
        if path == "/api/terminal/ws":
            return self._websocket(first("terminal"), first("after", "0"))
        store = self.server.store
        try:
            if path == "/api/terminal":
                wait = max(0.0, min(25.0, float(first("wait", "0"))))
                return self._json(200, relay.overview(store, first("terminal"), int(first("after", "0")), wait=wait))
            if path == "/api/terminal/stream":
                lines = relay.stream(store, first("terminal"), int(first("after", "0")))
                item = next(lines)
            else:
                return self._json(404, {"error": "not found"})
        except StopIteration:
            return self._json(400, {"error": "终端参数无效"})
        except (ValueError, TypeError) as exc:
            return self._error(exc, "终端参数无效")
        # One response kept open: a line is written and flushed whenever the terminal produces output.
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-store, no-transform")
        self.send_header("X-Accel-Buffering", "no")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        try:
            while True:
                self.wfile.write(b"data: " + json.dumps(item, ensure_ascii=False).encode("utf-8") + b"\n\n")
                self.wfile.flush()
                item = next(lines)
        except (StopIteration, OSError, ValueError):
            pass        # finished, the reader went away, or the terminal was removed meanwhile

    def _websocket(self, terminal, after):
        origin = urlsplit(self.headers.get("Origin", ""))
        if origin.scheme not in ("http", "https") or origin.netloc.lower() != self.headers.get("Host", "").lower():
            return self._json(403, {"error": "origin"})
        try:
            if self.command != "GET":
                raise ValueError
            key = websocket.accept_key(self.headers)
            after = int(after)
            lines = relay.stream(self.server.store, terminal, after, seconds=3600)
            first = next(lines)
        except (ValueError, StopIteration):
            return self._json(400, {"error": "WebSocket 参数无效"})
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", key)
        self.end_headers()
        self.close_connection = True
        self.connection.settimeout(35)
        channel = websocket.Connection(self.rfile, self.connection)
        channel.send(dict(first, t="out"))

        def output():
            last_ping = time.monotonic()
            try:
                for item in lines:
                    if channel.closed.is_set():
                        return
                    if not self._authed():
                        channel.close(1008)
                        return
                    channel.send(dict(item, t="out"))
                    if time.monotonic() - last_ping >= 15:
                        channel.send(b"", opcode=9)  # browsers answer pong even when the user is idle
                        last_ping = time.monotonic()
                channel.close(1001)
            except (OSError, ValueError):
                channel.close(1011)
            finally:
                channel.closed.set()
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass

        threading.Thread(target=output, daemon=True).start()
        try:
            while not channel.closed.is_set():
                raw = channel.receive()
                if not self._authed():
                    channel.close(1008)
                    break
                payload = json.loads(raw)
                if not isinstance(payload, dict) or payload.get("terminal") != terminal or payload.get("action") not in ("input", "resize", "close", "rename"):
                    raise relay.RemoteError("终端操作无效")
                try:
                    result = relay.command(self.server.store, payload)
                    channel.send(dict(result, t="ack", status=200))
                except relay.RemoteError as error:
                    channel.send({"t": "ack", "id": payload.get("id"), "status": 400, "state": "error", "error": str(error)})
                except OSError:
                    channel.send({"t": "ack", "id": payload.get("id"), "status": 500, "error": "终端记录保存失败"})
        except websocket.ProtocolError as error:
            channel.close(error.code)
        except (ValueError, TypeError):
            channel.close(1007)
        except OSError:
            pass
        finally:
            channel.closed.set()

    def do_POST(self):
        path = urlsplit(self.path).path
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = -1
        if length < 0 or length > BODY_LIMIT:
            self.close_connection = True
            return self._json(413, {"error": "请求过大"})
        raw = self.rfile.read(length)
        # A page on another site cannot send JSON with our cookie: the content type forces a preflight that is
        # never answered, and a browser's Origin must be this server.
        origin = self.headers.get("Origin")
        if not self.headers.get("Content-Type", "").lower().startswith("application/json") or (
                origin and urlsplit(origin).netloc.lower() != (self.headers.get("Host") or "").lower()):
            return self._json(403, {"error": "origin"})
        try:
            payload = json.loads(raw.decode("utf-8") or "{}")
            if not isinstance(payload, dict):
                raise ValueError
        except (ValueError, UnicodeDecodeError):
            return self._json(400, {"error": "请求格式无效"})
        if path == "/api/login":
            address, attempts = self._address(), self.server.attempts
            if attempts.blocked(address):
                return self._json(429, {"error": "密码错误次数过多，请 15 分钟后再试"})
            given = payload.get("password")
            if not isinstance(given, str) or not hmac.compare_digest(given.encode("utf-8"), self.server.password.encode("utf-8")):
                attempts.fail(address)
                time.sleep(0.4)
                return self._json(401, {"error": "密码不正确"})
            attempts.clear(address)
            token = self.server.sessions.create()
            cookie = "rcli=%s; Path=/; Max-Age=%d; HttpOnly; SameSite=Strict%s" % (token, SESSION_DAYS * 86400, "; Secure" if self._secure() else "")
            return self._json(200, {"ok": True, "token": token}, {"Set-Cookie": cookie})
        if path == "/api/logout":
            self.server.sessions.remove(self._token())
            return self._json(200, {"ok": True}, {"Set-Cookie": "rcli=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"})
        if not self._authed():
            return self._json(401, {"error": "auth"})
        call = {"/api/terminal": relay.command, "/api/terminal/agent": relay.agent, "/api/terminal/agent/pull": relay.pull}.get(path)
        if call is None:
            return self._json(404, {"error": "not found"})
        try:
            return self._json(200, call(self.server.store, payload))
        except (ValueError, TypeError) as exc:
            return self._error(exc, "请求格式无效")
        except OSError:
            return self._json(500, {"error": "终端记录保存失败"})


class Server(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 64


def make_server(host, port, data, web, password=None):
    os.makedirs(data, exist_ok=True)
    server = Server((host, port), Handler)
    made = False
    if password is None:
        password, made = read_password(data)
    server.password, server.password_made = password, made
    server.sessions = Sessions(os.path.join(data, "sessions.json"))
    server.attempts = Attempts()
    server.store = os.path.join(data, "terminals.json")
    server.web = os.path.abspath(web)
    return server


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description="remote-cli relay")
    parser.add_argument("--host", default="127.0.0.1", help="address to listen on; 0.0.0.0 for the local network")
    parser.add_argument("--port", type=int, default=8722)
    parser.add_argument("--data", default=os.path.join(here, "data"), help="folder for the password, sign-ins and terminal state")
    parser.add_argument("--web", default=os.path.join(os.path.dirname(here), "web"), help="folder of the web client")
    args = parser.parse_args()
    server = make_server(args.host, args.port, args.data, args.web)
    if len(server.password) < 12:
        print("warning: the password is shorter than 12 characters", file=sys.stderr)
    print("remote-cli relay %s on http://%s:%d" % (VERSION, args.host, args.port), flush=True)
    if server.password_made:
        print("password (also saved in %s): %s" % (os.path.join(args.data, "password.txt"), server.password), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
