"""Bounded RFC 6455 text messages over an already authenticated HTTP connection."""
import base64
import hashlib
import json
import struct
import threading

MAX_MESSAGE = 128_000


class ProtocolError(ValueError):
    def __init__(self, code=1002):
        self.code = code


def accept_key(headers):
    if (headers.get("Upgrade", "").lower() != "websocket"
            or "upgrade" not in [part.strip().lower() for part in headers.get("Connection", "").split(",")]
            or headers.get("Sec-WebSocket-Version") != "13"):
        raise ProtocolError()
    key = headers.get("Sec-WebSocket-Key", "")
    try:
        if len(base64.b64decode(key, validate=True)) != 16:
            raise ProtocolError()
    except (ValueError, UnicodeError):
        raise ProtocolError() from None
    return base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()).decode("ascii")


class Connection:
    def __init__(self, reader, socket):
        self.reader, self.socket = reader, socket
        self.lock = threading.Lock()
        self.closed = threading.Event()

    def send(self, payload, opcode=1):
        if opcode == 1:
            payload = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        length = len(payload)
        head = bytes((0x80 | opcode, length)) if length < 126 else (
            bytes((0x80 | opcode, 126)) + struct.pack("!H", length) if length <= 65535
            else bytes((0x80 | opcode, 127)) + struct.pack("!Q", length))
        with self.lock:
            if self.closed.is_set():
                raise ConnectionError("WebSocket closed")
            self.socket.sendall(head + payload)

    def close(self, code=1000):
        try:
            self.send(struct.pack("!H", code), opcode=8)
        except OSError:
            pass
        self.closed.set()

    def read(self, size):
        data = self.reader.read(size)
        if len(data) != size:
            raise ConnectionError("WebSocket closed")
        return data

    def receive(self):
        message, active = bytearray(), False
        while True:
            first, second = self.read(2)
            final, opcode, length = bool(first & 128), first & 15, second & 127
            if first & 112 or not second & 128:
                raise ProtocolError()
            if opcode >= 8 and (not final or length > 125):
                raise ProtocolError()
            if length == 126:
                length = struct.unpack("!H", self.read(2))[0]
                if length < 126:
                    raise ProtocolError()
            elif length == 127:
                length = struct.unpack("!Q", self.read(8))[0]
                if length < 65536 or length >> 63:
                    raise ProtocolError()
            if length > MAX_MESSAGE or (opcode < 8 and len(message) + length > MAX_MESSAGE):
                raise ProtocolError(1009)
            mask = self.read(4)
            raw = self.read(length)
            data = bytes(value ^ mask[n % 4] for n, value in enumerate(raw))
            if opcode == 8:
                if len(data) == 1:
                    raise ProtocolError()
                if len(data) >= 2:
                    code = struct.unpack("!H", data[:2])[0]
                    if code not in (1000, 1001, 1002, 1003, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014) and not 3000 <= code < 5000:
                        raise ProtocolError()
                    try:
                        data[2:].decode("utf-8")
                    except UnicodeError:
                        raise ProtocolError(1007) from None
                self.close()
                raise ConnectionError("WebSocket closed")
            if opcode == 9:
                self.send(data, opcode=10)
                continue
            if opcode == 10:
                continue
            if opcode not in (0, 1):
                raise ProtocolError(1003)
            if (opcode == 1 and active) or (opcode == 0 and not active):
                raise ProtocolError()
            active = True
            message.extend(data)
            if final:
                try:
                    return message.decode("utf-8")
                except UnicodeError:
                    raise ProtocolError(1007) from None
