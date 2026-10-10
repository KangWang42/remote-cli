/* Touch scrolling is paced by display frames; network and terminal rendering stay independent. */
(function (root) {
  'use strict';
  // One glide for every kind of terminal. The speed at lift-off fades with this time constant (ms): a flick of two
  // pixels a millisecond carries on for about forty rows and has come to rest within a second and a half.
  const GLIDE = 325;
  // Claude Code moves three lines for a wheel step on its own, four for two steps written together and six for four.
  // Steps that follow one another within about 80 ms move further each time (3, 7, 10, 13 ...), so they are sent
  // SLOT ms apart, where every step moves what it says here. Measured on Claude Code 2.1.
  const SLOT = 110, MOVES = [[6, 4], [4, 2], [3, 1]], OWED = 45;
  // A terminal moves by whole rows, and a program that scrolls its own view moves it a few rows at a time, a moment
  // after it was asked. Between those moves the picture is slid by what the finger is ahead of it, so that it
  // follows the finger by the pixel at every frame. AHEAD: how far it runs ahead of what the terminal shows, as
  // [the rows it follows the finger by exactly, the rows it never passes]; between the two it gives way more and
  // more, as a band does, so that it slows down and does not stop dead. `going` is for the direction the program
  // last moved in, `stuck` for one it did not move in when last asked (the end of its list), `unsure` for the rest.
  // WAIT (ms): how long a move that was asked for is waited on. SETTLE (ms): how fast the picture comes back to rest.
  // A network that holds a message back for a moment delivers the moves it held all at once. Moves that come when
  // the picture could not run far enough ahead of them are not jumped to: the picture stays where it was shown and
  // comes on as fast as the finger moves, and by itself with EASE (ms), for at most LATE rows.
  const AHEAD = { local: [1.5, 1.5], going: [4, 8], unsure: [3, 5], stuck: [.5, 1.5] }, WAIT = 700, SETTLE = 90, EASE = 70, LATE = 8;
  class TerminalSlide {
    // options: cell() the height of a row; viewport() where the terminal's own history is scrolled to; read() the
    // text of every row on the screen; apply(pixels, rowsStillAtTop, rowsStillAtBottom) moves the picture.
    constructor(options) {
      this.options = options;
      this.offset = 0; this.drain = 0; this.asked = []; this.top = 0; this.bottom = 0;
      this.active = false; this.kind = 'local'; this.rows = null; this.seen = false; this.last = 0;
      this.ease = 0; this.went = 0; this.stuck = 0;
    }
    begin(kind, at) {
      if (!this.active || kind !== this.kind) { this.offset = 0; this.drain = 0; this.asked = []; this.ease = 0; }
      this.kind = kind; this.active = true; this.seen = false; this.last = at; this.drain = 0;
      this.viewport = this.options.viewport();
      this.rows = kind === 'local' ? null : this.options.read();
    }
    cancel() {
      const moved = this.active || this.offset;
      this.offset = 0; this.drain = 0; this.asked = []; this.active = false; this.ease = 0;
      if (moved) this.options.apply(0, 0, 0);
    }
    forget() { this.top = 0; this.bottom = 0; this.went = 0; this.stuck = 0; }          // another size or another screen: what stood still is not known
    moved(pixels) {
      if (!this.active) return;
      const before = this.within();
      this.offset += pixels;
      // What the band did not let through of the finger's move brings on a picture that is behind.
      const kept = pixels - (this.within() - before);
      if (this.ease * kept < 0) this.ease += Math.sign(kept) * Math.min(Math.abs(kept), Math.abs(this.ease));
    }
    ask(rows, at) { if (this.active && rows) this.asked.push({ rows, at }); }
    // By how many rows what is on the screen has moved between two pictures of it (down is positive), and how many
    // rows at its top and bottom did not move: a program keeps its heading and its message box where they are.
    shift(before, after) {
      const n = Math.min(before.length, after.length);
      let changed = 0;
      for (let i = 0; i < n; i++) if (before[i] !== after[i]) changed++;
      if (changed < 2) return 0;
      const count = k => { let same = 0; for (let i = Math.max(0, -k); i < n - Math.max(0, k); i++) if (before[i] === after[i + k] && before[i].trim()) same++; return same; };
      // seldom over twelve rows at once; after a wait, as many as were asked for
      const far = Math.min(n - 3, Math.max(12, this.asked.reduce((sum, ask) => sum + Math.abs(ask.rows), 0)));
      let best = 0, most = 0;
      for (let k = 1; k <= far; k++) for (const way of [k, -k]) { const same = count(way); if (same > most) { best = way; most = same; } }
      if (most < 2 || most < (changed - Math.abs(best)) / 2) return 0;
      let top = 0, bottom = 0;
      while (top < n && before[top] === after[top]) top++;
      while (bottom < n - top && before[n - 1 - bottom] === after[n - 1 - bottom]) bottom++;
      // Rows that happen to read the same may be taken for still ones; over a drag the fewest seen are the true ones.
      this.top = this.seen ? Math.min(this.top, top) : top; this.bottom = this.seen ? Math.min(this.bottom, bottom) : bottom;
      this.seen = true;
      return best;
    }
    // The terminal has drawn. Rows it moved by, of those that were asked for, no longer have to be made up for.
    drawn() {
      if (!this.active) return;
      let shift = 0;
      if (this.kind === 'local') { const now = this.options.viewport(); shift = this.viewport - now; this.viewport = now; }
      else { const now = this.options.read(); if (this.asked.length) shift = this.shift(this.rows, now); this.rows = now; }
      const way = Math.sign(shift), cell = this.options.cell(), before = this.within();
      let left = Math.abs(shift), made = 0;
      while (left > 0 && this.asked.length && Math.sign(this.asked[0].rows) === way) {
        const first = this.asked[0], taken = Math.min(left, Math.abs(first.rows));
        first.rows -= way * taken; left -= taken; made += taken; this.offset -= way * taken * cell;
        if (!first.rows) this.asked.shift();
      }
      if (made) {
        this.went = way; this.stuck = 0;
        // The picture stays where it was shown: rows that came beyond what it had run ahead by are slid to.
        const most = LATE * cell;
        this.ease = Math.max(-most, Math.min(most, this.ease + before - way * made * cell - this.within()));
      }
      this.show();
    }
    // Once a frame. `resting`: no finger on the screen and no glide. Returns whether another frame is needed.
    frame(at, resting) {
      if (!this.active) return false;
      const dt = Math.max(0, Math.min(50, at - this.last)), cell = this.options.cell();
      this.last = at;
      // What was asked for long ago is not coming (the program is at the end of its list): the picture gives it up.
      while (this.asked.length && at - this.asked[0].at > WAIT) { const lost = this.asked.shift().rows; this.drain += lost * cell; this.stuck = Math.sign(lost); if (this.went === this.stuck) this.went = 0; }
      if (resting && !this.asked.length) this.drain = this.offset;
      if (this.ease) { this.ease *= Math.exp(-dt / EASE); if (Math.abs(this.ease) < .5) this.ease = 0; }
      if (this.drain) {
        const part = this.drain * (1 - Math.exp(-dt / SETTLE)), gone = Math.abs(this.drain - part) < .5 ? this.drain : part;
        this.offset -= gone; this.drain -= gone;
      }
      if (resting && !this.asked.length && Math.abs(this.offset) < .5 && !this.ease) { this.cancel(); return false; }
      this.show();
      return true;
    }
    // What the finger is ahead by, held to what the picture may run ahead of the terminal in that direction.
    within() {
      const cell = this.options.cell(), way = Math.sign(this.offset), rows = Math.abs(this.offset) / cell;
      const [free, most] = AHEAD[this.kind === 'local' ? 'local' : way === this.stuck ? 'stuck' : way === this.went ? 'going' : 'unsure'];
      const give = most - free;
      return way * cell * (rows <= free || !give ? Math.min(rows, free) : free + give * (1 - Math.exp((free - rows) / give)));
    }
    show() {
      const still = this.kind !== 'local';
      this.options.apply(this.within() + this.ease, still ? this.top : 0, still ? this.bottom : 0);
    }
  }
  class TerminalScroller {
    constructor(options) {
      this.options = options;
      this.frame = 0; this.dragging = false; this.velocity = 0; this.pending = 0;
      this.tick = this.tick.bind(this);
    }
    stop() {
      if (this.frame) this.options.cancel(this.frame);
      this.frame = 0; this.dragging = false; this.velocity = 0; this.pending = 0;
    }
    start(y, at) {
      this.stop(); this.dragging = true; this.y = y; this.lastMove = at;
      this.lastFrame = at; this.distance = 0; this.kind = this.options.mode();
    }
    move(y, at) {
      if (!this.dragging) return;
      const delta = y - this.y, dt = Math.max(8, Math.min(64, at - this.lastMove));
      if (delta && this.velocity && Math.sign(delta) !== Math.sign(this.velocity)) {
        this.pending = 0; this.velocity = 0;
      }
      this.velocity = this.velocity * .3 + delta / dt * .7;
      this.pending += delta; this.distance += Math.abs(delta);
      if (this.options.slide) this.options.slide(delta);
      this.y = y; this.lastMove = at;
      this.schedule();
    }
    end(at) {
      if (!this.dragging) return;
      this.dragging = false; this.released = at; this.lastFrame = at;
      const recent = at - this.lastMove < 80;
      if (!recent || this.distance < 12 || this.options.reduced()) this.velocity = 0;
      this.velocity = Math.max(-2.5, Math.min(2.5, this.velocity));
      this.schedule();
    }
    schedule() { if (!this.frame) this.frame = this.options.request(this.tick); }
    tick(at) {
      this.frame = 0;
      if (this.options.mode() !== this.kind || !this.options.visible()) { this.stop(); return; }
      const remote = this.kind !== 'local', dt = Math.max(0, Math.min(32, at - this.lastFrame));
      this.lastFrame = at;
      if (!this.dragging && this.velocity) {
        const decay = Math.exp(-dt / GLIDE), glide = this.velocity * GLIDE * (1 - decay);
        this.pending += glide;
        if (this.options.slide) this.options.slide(glide);
        this.velocity *= decay;
        if (at - this.released > 2500 || Math.abs(this.velocity) < .03) this.velocity = 0;
      }
      const cell = Math.max(8, this.options.lineHeight());
      let lines = Math.trunc(this.pending / cell);
      if (remote) lines = Math.max(-12, Math.min(12, lines));
      // A program that is sent fewer wheel steps than rows may still be owed one when the finger has stopped.
      const owing = () => !!this.options.owing && this.options.owing();
      if (lines || owing()) {
        this.pending -= lines * cell;
        if (this.options.scroll(lines, this.kind) === false) { this.stop(); return; }
      }
      if ((!this.dragging && this.velocity) || Math.abs(this.pending) >= cell || owing()) this.schedule();
    }
  }
  class TerminalScrollRouter {
    constructor(term, send, tool, clock) {
      this.term = term; this.send = send; this.tool = tool;
      this.clock = clock || (() => performance.now());
      this.remoteUp = 0; this.pageLines = 0; this.sgr = false;
      this.owed = 0; this.way = 0; this.wheelAt = -1e9;
      this.asked = 0;         // the rows the last call of scroll() asked the terminal or the program to move by
      // Mouse tracking and mouse encoding are independent terminal modes.
      for (const [final, enabled] of [['h', true], ['l', false]]) {
        term.parser.registerCsiHandler({ prefix: '?', final }, params => {
          if (params.includes(1006)) this.sgr = enabled;
          return false;
        });
      }
    }
    mode() {
      const alternate = this.term.buffer.active.type === 'alternate';
      // Codex's inline view writes into the terminal's own history, which is scrolled here whatever it reports.
      if (this.tool() === 'codex' && !alternate) return 'local';
      // A fullscreen program that listens to the mouse scrolls by the line for each wheel step. Page keys move a
      // screenful at a time, and are only for a program whose mouse mode never reached this terminal.
      return this.term.modes.mouseTrackingMode !== 'none' ? 'mouse' : alternate ? 'page' : 'local';
    }
    reset() { this.remoteUp = 0; this.pageLines = 0; this.owed = 0; }
    start() { this.pageLines = 0; this.owed = 0; }
    // The wheel steps that make Claude Code follow the finger by `lines` more rows: the largest move that does not
    // run more than a row and a half ahead of the finger. What the finger is ahead by is kept, up to OWED rows,
    // and paid off in the slots that follow.
    paced(lines) {
      const now = this.clock(), way = Math.sign(lines) || this.way || 0;
      if (way !== this.way) { this.owed = 0; this.way = way; }       // nothing is owed in the direction that was left
      this.owed = Math.max(-OWED, Math.min(OWED, this.owed + lines));
      const move = now - this.wheelAt >= SLOT && MOVES.find(([rows]) => this.owed * way >= rows - 1.5);
      if (!way || !move) return { steps: 0, moved: 0 };
      this.owed -= way * move[0]; this.wheelAt = now;
      return { steps: way * move[1], moved: way * move[0] };
    }
    owing() { return this.tool() === 'claude' && this.owed * (this.way || 0) >= 1.5 && this.mode() === 'mouse'; }
    wheel(lines) {
      const column = Math.max(1, Math.floor(this.term.cols / 2)), row = Math.max(1, Math.floor(this.term.rows / 2));
      const button = lines > 0 ? 64 : 65;
      // Codex reads SGR reports only: the older encoding would arrive in its message box as typed characters.
      const key = this.sgr || this.tool() === 'codex' ? `\x1b[<${button};${column};${row}M`
        : '\x1b[M' + String.fromCharCode(button + 32, Math.min(223, column) + 32, Math.min(223, row) + 32);
      return key.repeat(Math.abs(lines));
    }
    scroll(lines, mode = this.mode()) {
      this.asked = 0;
      if (mode === 'local') {
        const before = this.term.buffer.active.viewportY;
        this.term.scrollLines(-lines);
        this.asked = before - this.term.buffer.active.viewportY;
        return this.asked !== 0;
      }
      if (mode === 'page') {
        this.pageLines += lines;
        const pages = Math.trunc(this.pageLines / 8);
        if (!pages) return true;
        this.pageLines -= pages * 8;
        if (!this.send((pages > 0 ? '\x1b[5~' : '\x1b[6~').repeat(Math.abs(pages)))) return false;
        this.remoteUp = Math.max(0, this.remoteUp + pages * 8);
        this.asked = pages * 8;
        return true;
      }
      const sent = this.tool() === 'claude' ? this.paced(lines) : { steps: lines, moved: lines };
      if (!sent.steps) return true;
      if (!this.send(this.wheel(sent.steps))) return false;
      this.remoteUp = Math.max(0, this.remoteUp + sent.moved);
      this.asked = sent.moved;
      return true;
    }
    latest() {
      const mode = this.mode();
      if (this.remoteUp > 0 && mode !== 'local') {
        const keys = mode === 'page' ? '\x1b[6~'.repeat(Math.min(80, Math.ceil(this.remoteUp / 8) + 2))
          : this.wheel(-Math.min(600, this.remoteUp + 12));
        if (!this.send(keys)) return false;
      }
      this.reset(); this.term.scrollToBottom();
      return true;
    }
  }
  if (typeof module === 'object' && module.exports) {
    module.exports = TerminalScroller;
    module.exports.TerminalScrollRouter = TerminalScrollRouter;
    module.exports.TerminalSlide = TerminalSlide;
  } else { root.TerminalScroller = TerminalScroller; root.TerminalScrollRouter = TerminalScrollRouter; root.TerminalSlide = TerminalSlide; }
})(typeof window === 'undefined' ? globalThis : window);
