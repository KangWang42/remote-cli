'use strict';
const assert = require('node:assert/strict');
const TerminalScroller = require('../web/terminal/terminal-scroll.js');
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
  for (let t = 112; t < 1200; t += 16) f.step(t);
  assert.ok(f.moves.length > before, 'a flick should continue after the finger lifts');
  assert.equal(f.frames.size, 0, 'inertia must settle');
}
{
  const f = fixture('mouse'); f.scroller.start(0, 0); f.scroller.move(1000, 16); f.scroller.end(17);
  for (let t = 32; t < 600; t += 16) f.step(t);
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
console.log('Terminal scroll behavior checks passed');
