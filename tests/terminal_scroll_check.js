'use strict';
const assert = require('node:assert/strict');
const TerminalScroller = require('../web/terminal/terminal-scroll.js');
const { TerminalScrollRouter } = TerminalScroller;
function fixture(kind = 'local', reduced = false) {
  let id = 0, now = 0, visible = true;
  const frames = new Map(), moves = [];
  const scroller = new TerminalScroller({request: f => {frames.set(++id, f); return id;},
    cancel: n => frames.delete(n), mode: () => kind, lineHeight: () => 10,
    visible: () => visible, reduced: () => reduced, scroll: (lines, mode) => {moves.push({lines, mode, at: now}); return true;}});
  return {scroller, moves, frames, step(t) {now = t; const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(f => f(t));},
    hide() {visible = false;}, mode(m) {kind = m;}};
}
{
  const f = fixture('mouse'); f.scroller.start(0, 0); f.scroller.move(15, 8); f.scroller.move(35, 12);
  assert.equal(f.frames.size, 1); f.step(16);
  assert.deepEqual(f.moves, [{lines: 3, mode: 'mouse', at: 16}]);
  f.scroller.move(30, 18); f.scroller.move(10, 22); f.step(32);
  assert.equal(f.moves[1].lines, -2, 'reversing a drag must not replay queued movement in the previous direction');
}
{
  const f = fixture(); f.scroller.start(0, 0);
  for (let i = 1; i <= 5; i++) {f.scroller.move(i * 2, i * 16); f.step(i * 16);}
  assert.equal(f.moves.reduce((n, m) => n + m.lines, 0), 1, 'small movements must accumulate');
  f.scroller.move(70, 96); f.step(96); f.scroller.end(97); const before = f.moves.length;
  for (let t = 112; t < 3000; t += 16) f.step(t);
  assert.ok(f.moves.length > before, 'a flick should continue after the finger lifts');
  assert.equal(f.frames.size, 0, 'inertia must settle');
}
{
  const f = fixture('mouse'); f.scroller.start(0, 0); f.scroller.move(1000, 16); f.scroller.end(17);
  for (let t = 32; t < 3000; t += 16) f.step(t);
  assert.ok(f.moves.every(m => Math.abs(m.lines) <= 12), 'remote scrolling must send bounded batches');
  assert.equal(f.frames.size, 0, 'remote inertia must not keep queuing input');
}
{
  const f = fixture(); f.scroller.start(0, 0); f.scroller.move(100, 16); f.step(16); f.scroller.end(17);
  f.step(32); const count = f.moves.length; f.scroller.start(0, 33); f.step(48);
  assert.equal(f.moves.length, count, 'a new touch cancels an old fling');
  f.scroller.move(20, 50); f.scroller.stop(); f.step(64); assert.equal(f.moves.length, count);
}
{
  for (const action of ['hide', 'mode']) {
    const f = fixture(); f.scroller.start(0, 0); f.scroller.move(40, 16);
    if (action === 'hide') f.hide(); else f.mode('mouse');
    f.step(16); assert.equal(f.moves.length, 0, 'hidden or changed terminal must cancel pending scrolling');
  }
  const f = fixture('local', true); f.scroller.start(0, 0); f.scroller.move(40, 16); f.step(16); f.scroller.end(17);
  f.step(32); assert.equal(f.moves.length, 1, 'reduced-motion mode must avoid inertia');
}
{
  let tool = 'codex';
  const sent = [], handlers = {};
  const term = { cols: 80, rows: 24, modes: { mouseTrackingMode: 'any' },
    buffer: { active: { type: 'normal', viewportY: 100, baseY: 100 } },
    parser: { registerCsiHandler(id, handler) { handlers[id.final] = handler; } },
    scrollLines(lines) { this.buffer.active.viewportY = Math.max(0, Math.min(100, this.buffer.active.viewportY + lines)); },
    scrollToBottom() { this.buffer.active.viewportY = this.buffer.active.baseY; }
  };
  const router = new TerminalScrollRouter(term, text => { sent.push(text); return true; }, () => tool);
  assert.equal(router.mode(), 'local', 'Codex inline history must scroll locally even if pointer reporting is enabled');
  router.scroll(8);
  assert.equal(term.buffer.active.viewportY, 92, 'pulling the content down must reveal older output');
  assert.equal(sent.length, 0, 'local history must not be turned into program input');
  term.buffer.active.type = 'alternate';
  assert.equal(router.mode(), 'mouse', 'Codex fullscreen that reports the mouse must scroll by the line');
  router.scroll(2);
  assert.equal(sent.at(-1), '\x1b[<64;40;12M'.repeat(2), 'Codex takes SGR wheel reports even before the encoding is seen');
  assert.equal(router.remoteUp, 2);
  router.latest();
  assert.equal(sent.at(-1), '\x1b[<65;40;12M'.repeat(14), 'return to latest must scroll Codex back down');
  assert.equal(router.remoteUp, 0);
  term.modes.mouseTrackingMode = 'none';
  assert.equal(router.mode(), 'page', 'Codex fullscreen navigation must work without relying on native mouse mode propagation');
  router.scroll(8);
  assert.equal(sent.at(-1), '\x1b[5~', 'older fullscreen content is PageUp');
  assert.equal(router.remoteUp, 8);
  router.latest();
  assert.equal(sent.at(-1), '\x1b[6~'.repeat(3), 'return to latest must work after page scrolling');
  router.scroll(-8);
  assert.equal(sent.at(-1), '\x1b[6~', 'the opposite gesture is PageDown');
  assert.equal(router.remoteUp, 0);
  router.reset(); router.start(); router.scroll(3); router.scroll(5);
  assert.equal(sent.at(-1), '\x1b[5~', 'short movements in one gesture accumulate to a page');
  tool = 'shell'; term.modes.mouseTrackingMode = 'any';       // any other program that reads the mouse, such as an editor
  assert.equal(router.mode(), 'mouse');
  router.scroll(1);
  assert.equal(sent.at(-1), '\x1b[M' + String.fromCharCode(96, 72, 44), 'default mouse reports must use legacy encoding');
  assert.equal(handlers.h([1006]), false, 'mode tracking must let xterm parse the sequence too');
  router.scroll(1);
  assert.equal(sent.at(-1), '\x1b[<64;40;12M');
  router.scroll(-1);
  assert.equal(sent.at(-1), '\x1b[<65;40;12M');
  handlers.l([1006]); router.scroll(1);
  assert.ok(sent.at(-1).startsWith('\x1b[M'), 'disabling SGR must restore the requested mouse encoding');
  term.modes.mouseTrackingMode = 'none';
  assert.equal(router.mode(), 'page');
  term.buffer.active.type = 'normal';
  assert.equal(router.mode(), 'local');
  router.reset(); term.buffer.active.viewportY = 100;
  assert.equal(router.scroll(-1), false, 'a local scroll at the boundary must stop inertia');
}
{
  // Claude Code moves 3 lines for one wheel step, 4 for two written together and 6 for four, and further still when
  // steps follow within 80 ms: the page sends at most one group every 110 ms and counts what each group moves.
  let now = 1000;
  const sent = [];
  const term = { cols: 80, rows: 24, modes: { mouseTrackingMode: 'any' }, buffer: { active: { type: 'alternate', viewportY: 0, baseY: 0 } },
    parser: { registerCsiHandler() {} }, scrollLines() {}, scrollToBottom() {} };
  const router = new TerminalScrollRouter(term, text => { sent.push({ at: now, steps: text.split('\x1b[').length - 1, up: text.includes(String.fromCharCode(96)) || text.includes('<64;') }); return true; }, () => 'claude', () => now);
  router.sgr = true;
  const frame = lines => { now += 1000 / 60; if (lines || router.owing()) router.scroll(lines, 'mouse'); };
  const moved = () => sent.reduce((rows, s) => rows + (s.up ? 1 : -1) * { 1: 3, 2: 4, 4: 6 }[s.steps], 0);
  router.start(); frame(1);
  assert.equal(sent.length, 0, 'one row is less than half of the smallest move');
  frame(1);
  assert.deepEqual(sent.map(s => s.steps), [1], 'the smallest move is sent when the finger is halfway through it');
  // a steady drag of 30 rows a second for a second, then the finger rests
  for (let i = 0; i < 60; i++) frame(i % 2);
  for (let i = 0; i < 30; i++) frame(0);
  assert.ok(Math.abs(moved() - 32) <= 1.5, 'what Claude Code is moved by must be what the finger moved: ' + moved());
  assert.ok(sent.every((s, i) => !i || s.at - sent[i - 1].at >= 110), 'groups must be far enough apart for every step to move what it says');
  assert.ok(sent.every(s => [1, 2, 4].includes(s.steps)), 'only groups whose movement is known');
  assert.equal(router.owing(), false, 'nothing is owed once the picture has caught up');
  assert.ok(Math.abs(router.remoteUp - 32) <= 1.5);
  // a fast flick is paid off afterwards, six rows a group
  const before = moved();
  for (let i = 0; i < 6; i++) frame(5);
  for (let i = 0; i < 60; i++) frame(0);
  assert.ok(Math.abs(moved() - before - 30) <= 1.5, 'a flick must arrive in full: ' + (moved() - before));
  // turning round owes nothing in the old direction
  const turned = sent.length;
  frame(4); frame(-2);
  for (let i = 0; i < 20; i++) frame(0);
  assert.ok(sent.slice(turned).filter(s => !s.up).length === 1 && sent.at(-1).up === false, 'the reverse move follows the finger back');
}
{
  // the glide after lift-off is the same wherever the scrolling is done
  const rows = kind => { const f = fixture(kind); f.scroller.start(0, 0);
    for (let i = 1; i <= 6; i++) { f.scroller.move(i * 20, i * 16); f.step(i * 16); }
    f.scroller.end(100); for (let t = 116; t < 4000; t += 16) f.step(t);
    assert.equal(f.frames.size, 0, 'the glide must come to rest');
    return f.moves.reduce((n, m) => n + m.lines, 0); };
  assert.equal(rows('local'), rows('mouse'));
  assert.ok(rows('mouse') >= 12 + 30, 'a flick must carry on well past where the finger lifted: ' + rows('mouse'));
}
{
  // Between the moves of the terminal the picture is slid under the finger. A program like Claude Code is asked by
  // the wheel, moves its list three rows or more at a time a moment later, and keeps a heading and a message box
  // where they are; the finger moves evenly. What is seen (the rows the program shows, plus the slide) must stay
  // with the finger at every frame, come to rest where the program is, and leave the still rows alone.
  const { TerminalSlide } = TerminalScroller;
  const cell = 20, rows = 24, head = 1, foot = 4, latency = 150;
  let now = 0, top = 500, slid = 0, still = [0, 0], id = 0;
  const frames = new Map(), due = [];
  const screen = () => { const text = ['heading'];
    for (let i = 0; i < rows - head - foot; i++) text.push('line ' + (top + i));
    return text.concat(['', 'box top', '> message', 'box bottom']); };
  const term = { cols: 80, rows, modes: { mouseTrackingMode: 'any' }, buffer: { active: { type: 'alternate', viewportY: 0, baseY: 0 } },
    parser: { registerCsiHandler() {} }, scrollLines() {}, scrollToBottom() {} };
  const router = new TerminalScrollRouter(term, text => { const steps = text.split('\x1b[').length - 1, up = text.includes('<64;');
    due.push({ at: now + latency, rows: (up ? 1 : -1) * { 1: 3, 2: 4, 4: 6 }[steps] }); return true; }, () => 'claude', () => now);
  router.sgr = true;
  const slide = new TerminalSlide({ cell: () => cell, viewport: () => 0, read: screen, apply(pixels, t, b) { slid = pixels; still = [t, b]; } });
  const scroller = new TerminalScroller({ request: f => { frames.set(++id, f); return id; }, cancel: n => frames.delete(n), mode: () => 'mouse', lineHeight: () => cell,
    visible: () => true, reduced: () => false, owing: () => router.owing(), slide: pixels => slide.moved(pixels),
    scroll(lines, mode) { const moved = router.scroll(lines, mode); slide.ask(router.asked, now); return moved; } });
  const start = top;
  let finger = 0, worst = 0, jumps = 0, last = 0, moving = true;
  scroller.start(0, 0); slide.begin('remote', 0); router.start();
  for (let n = 1; n <= 240; n++) {
    now = n * 1000 / 60;
    if (n <= 90) { finger = n * 4; scroller.move(finger, now); }        // 12 rows a second for a second and a half
    if (n === 91) { scroller.velocity = 0; scroller.end(now); }           // the finger is lifted without a flick
    const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(f => f(now));
    while (due.length && due[0].at <= now) { top -= due.shift().rows; slide.drawn(); }      // the program has drawn its list moved
    moving = slide.frame(now, !scroller.dragging && !scroller.velocity && !scroller.frame);
    const seen = (start - top) * cell + slid;
    if (n <= 90) { worst = Math.max(worst, Math.abs(seen - finger)); if (Math.abs(seen - last) > cell * 1.2) jumps++; }
    last = seen;
  }
  // (it runs at most three rows ahead of the program; a program this slow is a little further behind at times)
  assert.ok(worst <= cell, 'the picture must stay with the finger, within a row: ' + worst / cell);
  assert.equal(jumps, 0, 'and must not jump by rows, as the program by itself does');
  assert.deepEqual(still, [0, 0]); assert.equal(slid, 0, 'at rest the picture is where the terminal draws it'); assert.equal(moving, false);
  assert.deepEqual([slide.top, slide.bottom], [head, foot], 'the heading and the message box are found to stand still');
  assert.ok(Math.abs((start - top) - 18) <= 1.5, 'and the program was moved by what the finger moved: ' + (start - top));
  // at the end of its list the program does not move: the picture is pulled a little, never further, and comes back
  let most = 0;
  scroller.start(0, now); slide.begin('remote', now); router.start(); due.length = 0;
  for (let n = 1; n <= 150; n++) {
    now += 1000 / 60;
    if (n <= 60) scroller.move(n * 6, now);
    if (n === 61) { scroller.velocity = 0; scroller.end(now); }
    const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(f => f(now));
    due.length = 0;                                             // asked, and not done
    moving = slide.frame(now, !scroller.dragging && !scroller.velocity && !scroller.frame);
    most = Math.max(most, Math.abs(slid));
  }
  assert.ok(most > cell && most <= cell * 3, 'a pull of at most three rows: ' + most / cell);
  assert.equal(slid, 0); assert.equal(moving, false);
  // the terminal's own history moves a row at a time, at once: the slide is what is left of a row
  let viewport = 100; slid = 0;
  const local = new TerminalSlide({ cell: () => cell, viewport: () => viewport, read: () => [], apply(pixels) { slid = pixels; } });
  local.begin('local', 0); let moved = 0, off = 0;
  for (let n = 1; n <= 50; n++) {
    local.moved(3); moved += 3;
    const lines = Math.trunc((moved - (100 - viewport) * cell) / cell);
    if (lines) { viewport -= lines; local.ask(lines, n * 16); }
    local.drawn(); local.frame(n * 16, false);
    off = Math.max(off, Math.abs((100 - viewport) * cell + slid - moved));
  }
  assert.ok(off < 0.01, 'in the history the picture is exactly under the finger: ' + off);
  for (let n = 51; n < 90; n++) local.frame(n * 16, true);
  assert.equal(slid, 0, 'and settles on a whole row');
}
console.log('Terminal scroll behavior checks passed');
