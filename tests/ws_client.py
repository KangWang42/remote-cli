"""Small authenticated WebSocket client for isolated integration checks."""
import base64
import hashlib
import json
import os
import socket
import ssl
import struct
import time
from urllib.parse import urlsplit


class Socket:
    def __init__(self, origin, path, cookie, timeout=10):
        url = urlsplit(origin)
        self.socket = socket.create_connection((url.hostname, url.port or (443 if url.scheme == "https" else 80)), timeout)
        self.socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.reader = None
        try:
            if url.scheme == "https":
                self.socket = ssl.create_default_context().wrap_socket(self.socket, server_hostname=url.hostname)
            key = base64.b64encode(os.urandom(16)).decode("ascii")
            self.socket.sendall(("GET %s HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                                 "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\nOrigin: %s\r\nCookie: %s\r\n\r\n"
                                 % (path, url.netloc, key, origin, cookie)).encode("ascii"))
            self.reader = self.socket.makefile("rb")
            self.status = int(self.reader.readline(4096).split()[1])
            headers = {}
            while True:
                line = self.reader.readline(4096)
                if line in (b"\r\n", b""):
                    break
                name, value = line.split(b":", 1)
                headers[name.lower()] = value.strip()
            if self.status == 101:
                expected = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest())
                if headers.get(b"sec-websocket-accept") != expected:
                    raise RuntimeError("invalid WebSocket accept key")
        except Exception:
            self.close()
            raise

    def read(self, count):
        data = self.reader.read(count)
        if len(data) != count:
            raise ConnectionError("WebSocket closed")
        return data

    def frame(self, data, opcode=1, final=True):
        mask = os.urandom(4)
        first = (128 if final else 0) | opcode
        size = len(data)
        head = bytes((first, 128 | size)) if size < 126 else (
            bytes((first, 254)) + struct.pack("!H", size) if size <= 65535 else bytes((first, 255)) + struct.pack("!Q", size))
        self.socket.sendall(head + mask + bytes(b ^ mask[n % 4] for n, b in enumerate(data)))

    def send(self, value):
        self.frame(json.dumps(value).encode("utf-8"))

    def receive(self, timeout=10):
        deadline, pieces = time.monotonic() + timeout, bytearray()
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("WebSocket receive timeout")
            self.socket.settimeout(remaining)
            first, second = self.read(2)
            length = second & 127
            if length == 126:
                length = struct.unpack("!H", self.read(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self.read(8))[0]
            if second & 128 or length > 4_000_000:
                raise RuntimeError("invalid server frame")
            data, opcode = self.read(length), first & 15
            if opcode == 8:
                raise ConnectionError("WebSocket closed")
            if opcode == 9:
                self.frame(data, opcode=10)
            elif opcode in (0, 1):
                pieces.extend(data)
                if first & 128:
                    return json.loads(pieces.decode("utf-8"))

    def close(self):
        if getattr(self, "status", None) == 101:
            try:
                self.frame(b"", opcode=8)
            except OSError:
                pass
        if self.reader:
            self.reader.close()
        self.socket.close()
