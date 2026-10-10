/* Touch scrolling is paced by display frames; network and terminal rendering stay independent. */
(function (root) {
  'use strict';
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
        const decay = Math.exp(-dt / (remote ? 70 : 180));
        this.pending += this.velocity * (remote ? 70 : 180) * (1 - decay);
        this.velocity *= decay;
        if (at - this.released > (remote ? 180 : 900) || Math.abs(this.velocity) < .025) this.velocity = 0;
      }
      const cell = Math.max(8, this.options.lineHeight());
      let lines = Math.trunc(this.pending / cell);
      if (remote) lines = Math.max(-12, Math.min(12, lines));
      if (lines) {
        this.pending -= lines * cell;
        if (this.options.scroll(lines, this.kind) === false) { this.stop(); return; }
      }
      if ((!this.dragging && this.velocity) || Math.abs(this.pending) >= cell) this.schedule();
    }
  }
  class TerminalScrollRouter {
    constructor(term, send, tool) {
      this.term = term; this.send = send; this.tool = tool;
      this.remoteUp = 0; this.pageLines = 0; this.sgr = false;
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
    reset() { this.remoteUp = 0; this.pageLines = 0; }
    start() { this.pageLines = 0; }
    wheel(lines) {
      const column = Math.max(1, Math.floor(this.term.cols / 2)), row = Math.max(1, Math.floor(this.term.rows / 2));
      const button = lines > 0 ? 64 : 65;
      // Codex reads SGR reports only: the older encoding would arrive in its message box as typed characters.
      const key = this.sgr || this.tool() === 'codex' ? `\x1b[<${button};${column};${row}M`
        : '\x1b[M' + String.fromCharCode(button + 32, Math.min(223, column) + 32, Math.min(223, row) + 32);
      return key.repeat(Math.abs(lines));
    }
    scroll(lines, mode = this.mode()) {
      if (mode === 'local') {
        const before = this.term.buffer.active.viewportY;
        this.term.scrollLines(-lines);
        return this.term.buffer.active.viewportY !== before;
      }
      if (mode === 'page') {
        this.pageLines += lines;
        const pages = Math.trunc(this.pageLines / 8);
        if (!pages) return true;
        this.pageLines -= pages * 8;
        if (!this.send((pages > 0 ? '\x1b[5~' : '\x1b[6~').repeat(Math.abs(pages)))) return false;
        this.remoteUp = Math.max(0, this.remoteUp + pages * 8);
        return true;
      }
      if (!this.send(this.wheel(lines))) return false;
      this.remoteUp = Math.max(0, this.remoteUp + lines);
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
  } else { root.TerminalScroller = TerminalScroller; root.TerminalScrollRouter = TerminalScrollRouter; }
})(typeof window === 'undefined' ? globalThis : window);
