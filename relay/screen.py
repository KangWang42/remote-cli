"""What a terminal shows right now, kept as a grid of characters, to tell in one line what its program last said.

Only what decides where text lands is followed: cursor movement, erasing, scrolling and the alternate screen.
Colours and other attributes are dropped. The phone draws the real screen itself; this is for the lists.
"""
import re

_TOKEN = re.compile(r"\x1b\[([0-9;?<=>]*)([ -/]*)([@-~])|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|\x1b[()][0-9A-Za-z]|\x1b([@-Z\\-_78=>c])|([\x00-\x1a\x1c-\x1f\x7f])")
_PARTIAL = re.compile(r"\x1b(?:\[[0-9;?<=>]*[ -/]*|\][^\x07\x1b]*\x1b?|[PX^_][^\x1b]*\x1b?|[()])?\Z")
# The first signs with which Claude Code and Codex begin something they say or do.
_MARKS = "●⏺•"
# Lines that are only a frame, a rule or an empty prompt.
_FRAME = re.compile("^[\\s─-▟%s-%s›»_=>$#|+*.·-]*$" % (chr(0x2700), chr(0x27bf)))      # box drawing, blocks, dingbats
_HINTS = ("for shortcuts", "to interrupt", "shift+tab", "auto mode", "bypass permissions", "ctrl+", "context left", "esc to", "tip:", "ask codex to",
          "back to bottom", "openai codex", "permissions:", " default \u00b7 ")
# A line that begins with one of these belongs to the frame around a message, not to the message.
_ASIDE = "\u23bf\u2514\u251c\u2193\u2191*\u00b7\u203a>\u2502"
# A waiting prompt, or a path alone on its line.
_PROMPT = re.compile(r"^PS [^>]*>\s*$|^[A-Za-z]:\\[^>]*>\s*$|^[A-Za-z]:\\\S*$")


def _wide(char):
    code = ord(char)
    return code >= 0x1100 and (code <= 0x115f or 0x2e80 <= code <= 0xa4cf or 0xac00 <= code <= 0xd7a3 or 0xf900 <= code <= 0xfaff
                                or 0xfe30 <= code <= 0xfe6f or 0xff00 <= code <= 0xff60 or 0xffe0 <= code <= 0xffe6 or 0x1f300 <= code <= 0x1faff
                                or 0x20000 <= code <= 0x3fffd)


class Screen:
    def __init__(self, cols=80, rows=24):
        self.cols, self.rows = max(1, cols), max(1, rows)
        self.grid = self._blank()
        self.other = None           # the main screen, while the alternate one is shown
        self.row = self.col = 0
        self.top, self.bottom = 0, self.rows - 1
        self.rest = ""              # the beginning of a sequence whose end has not arrived yet
        self.seq = 0                # the last piece of output that was taken
        self.text = ""              # what said() gave for it

    def _blank(self):
        return [[" "] * self.cols for _ in range(self.rows)]

    def resize(self, cols, rows):
        cols, rows = max(1, cols), max(1, rows)
        if (cols, rows) == (self.cols, self.rows):
            return
        for grid in (self.grid, self.other):
            if grid is None:
                continue
            del grid[:max(0, len(grid) - rows)]          # the newest lines stay
            grid.extend([" "] * self.cols for _ in range(rows - len(grid)))
            for line in grid:
                del line[cols:]
                line.extend(" " * (cols - len(line)))
        self.cols, self.rows = cols, rows
        self.top, self.bottom = 0, rows - 1
        self.row, self.col = min(self.row, rows - 1), min(self.col, cols - 1)

    def _scroll(self, count):
        """Moves the lines between top and bottom up (count > 0) or down."""
        for _ in range(min(abs(count), self.bottom - self.top + 1)):
            if count > 0:
                del self.grid[self.top]
                self.grid.insert(self.bottom, [" "] * self.cols)
            else:
                del self.grid[self.bottom]
                self.grid.insert(self.top, [" "] * self.cols)

    def _feed_line(self):
        if self.row == self.bottom:
            self._scroll(1)
        elif self.row < self.rows - 1:
            self.row += 1

    def _write(self, text):
        for char in text:
            width = 2 if _wide(char) else 1
            if self.col + width > self.cols:
                self.col = 0
                self._feed_line()
            line = self.grid[self.row]
            line[self.col] = char
            if width == 2 and self.col + 1 < self.cols:
                line[self.col + 1] = ""
            self.col += width

    def _control(self, params, final):
        private = params[:1] in ("?", "<", "=", ">")
        numbers = [int(n) if n.isdigit() else 0 for n in params.lstrip("?<=>").split(";")] if params.lstrip("?<=>") else []
        first = numbers[0] if numbers else 0
        count = max(1, first)
        line = self.grid[self.row]
        if private:
            if final in "hl" and any(n in (47, 1047, 1049) for n in numbers):
                if final == "h" and self.other is None:
                    self.other, self.grid = self.grid, self._blank()
                    self.row = self.col = 0
                elif final == "l" and self.other is not None:
                    self.grid, self.other = self.other, None
            return
        if final in "Hf":
            self.row = min(self.rows, max(1, first)) - 1
            self.col = min(self.cols, max(1, numbers[1] if len(numbers) > 1 else 1)) - 1
        elif final == "A":
            self.row = max(0, self.row - count)
        elif final in "Be":
            self.row = min(self.rows - 1, self.row + count)
        elif final in "Ca":
            self.col = min(self.cols - 1, self.col + count)
        elif final == "D":
            self.col = max(0, self.col - count)
        elif final == "E":
            self.row, self.col = min(self.rows - 1, self.row + count), 0
        elif final == "F":
            self.row, self.col = max(0, self.row - count), 0
        elif final in "G`":
            self.col = min(self.cols, count) - 1
        elif final == "d":
            self.row = min(self.rows, count) - 1
        elif final == "K":
            start, end = (self.col, self.cols) if first == 0 else (0, self.col + 1) if first == 1 else (0, self.cols)
            line[start:end] = [" "] * (min(end, self.cols) - start)
        elif final == "J":
            rows = range(self.row + 1, self.rows) if first == 0 else range(0, self.row) if first == 1 else range(self.rows)
            for at in rows:
                self.grid[at] = [" "] * self.cols
            if first == 0:
                line[self.col:] = [" "] * (self.cols - self.col)
            elif first == 1:
                line[:self.col + 1] = [" "] * (self.col + 1)
        elif final == "X":
            line[self.col:self.col + count] = [" "] * (min(self.col + count, self.cols) - self.col)
        elif final == "P":
            del line[self.col:self.col + count]
            line.extend(" " * (self.cols - len(line)))
        elif final == "@":
            line[self.col:self.col] = [" "] * count
            del line[self.cols:]
        elif final in "LM" and self.top <= self.row <= self.bottom:
            top, self.top = self.top, self.row
            self._scroll(-count if final == "L" else count)
            self.top = top
        elif final == "S":
            self._scroll(count)
        elif final == "T":
            self._scroll(-count)
        elif final == "r":
            top, bottom = max(1, first), numbers[1] if len(numbers) > 1 and numbers[1] else self.rows
            if top < bottom <= self.rows:
                self.top, self.bottom = top - 1, bottom - 1
                self.row = self.col = 0

    def feed(self, data):
        data = self.rest + data
        cut = _PARTIAL.search(data)
        self.rest = data[cut.start():] if cut and len(data) - cut.start() < 4000 else ""       # a sequence that never ends is given up
        if cut:
            data = data[:cut.start()]
        at = 0
        for token in _TOKEN.finditer(data):
            if token.start() > at:
                self._write(data[at:token.start()])
            at = token.end()
            params, between, final, escape, control = token.groups()
            if final:
                if not between:
                    self._control(params, final)
            elif escape:
                if escape == "M":                      # up one line, scrolling down at the top
                    if self.row == self.top:
                        self._scroll(-1)
                    else:
                        self.row = max(0, self.row - 1)
                elif escape in "DE":
                    self._feed_line()
                    if escape == "E":
                        self.col = 0
                elif escape == "c":
                    self.__init__(self.cols, self.rows)
            elif control == "\r":
                self.col = 0
            elif control in ("\n", "\x0b", "\x0c"):
                self._feed_line()
            elif control == "\b":
                self.col = max(0, self.col - 1)
            elif control == "\t":
                self.col = min(self.cols - 1, (self.col // 8 + 1) * 8)
        if at < len(data):
            self._write(data[at:])

    def lines(self):
        return ["".join(line).rstrip() for line in self.grid]

    def said(self, limit=140):
        """The last thing the program said or did, on one line; empty when the screen holds nothing of the kind."""
        lines = [" ".join(line.split()) for line in self.lines()]
        for at in range(len(lines) - 1, -1, -1):
            line = lines[at]
            if len(line) < 5 or line[0] not in _MARKS or any(hint in line.lower() for hint in _HINTS):
                continue
            text = line[1:].strip()
            for more in lines[at + 1:at + 5]:
                if not more or more[0] in _MARKS or more[0] in _ASIDE or 0x2700 <= ord(more[0]) <= 0x27bf or _FRAME.match(more) or len(text) >= limit or any(hint in more.lower() for hint in _HINTS):
                    break
                glue = " " if text[-1:].isascii() and more[:1].isascii() else ""      # Chinese runs on without a space
                text += glue + more
            return text[:limit]
        for line in reversed(lines):
            if len(line) >= 3 and not _FRAME.match(line) and not _PROMPT.match(line) and not any(hint in line.lower() for hint in _HINTS):
                return line[:limit]
        return ""
