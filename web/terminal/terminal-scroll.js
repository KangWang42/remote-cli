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
  if (typeof module === 'object' && module.exports) module.exports = TerminalScroller;
  else root.TerminalScroller = TerminalScroller;
})(typeof window === 'undefined' ? globalThis : window);
