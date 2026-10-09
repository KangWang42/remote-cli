/* Regression checks for transport selection, pipelining and immutable retries. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../web/terminal/bridge.js'), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));
const terminalId = 'a'.repeat(32);
const output = { t: 'out', terminal: { id: terminalId, state: 'running' }, chunks: [], after: 7 };

function page(host = 'test.trycloudflare.com') {
  const requests = [], sockets = [], timers = new Map(), received = [], errors = [], listeners = {};
  let counter = 0, now = 0;
  class Socket {
    static OPEN = 1;
    constructor(url) { this.url = url; this.readyState = 0; this.sent = []; sockets.push(this); }
    open() { this.readyState = 1; if (this.onopen) this.onopen(); }
    send(text) { this.sent.push(JSON.parse(text)); }
    message(item) { this.onmessage({ data: JSON.stringify(item) }); }
    close(code = 1006) { this.readyState = 3; if (this.onclose) this.onclose({ code }); }
  }
  const document = { hidden: false, addEventListener: (name, handler) => { listeners[name] = handler; } };
  const context = {
    URLSearchParams, TextDecoder, Uint8Array, AbortController, WebSocket: Socket, document,
    location: { hostname: host, host, protocol: 'https:', search: '?id=' + terminalId, replace() {} },
    localStorage: { getItem() { return null; } },
    crypto: { getRandomValues(bytes) { bytes.fill(++counter); return bytes; } },
    Date: { now: () => now },
    setTimeout(fn, delay) { const id = ++counter; timers.set(id, { fn, at: now + delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    fetch(url, options = {}) {
      return new Promise((resolve, reject) => {
        const request = { url, options, resolve, reject };
        requests.push(request);
        if (options.signal) options.signal.addEventListener('abort', () => reject(new Error('aborted')));
      });
    }
  };
  context.window = context;
  context.TerminalUI = { receive(item) { received.push(item); context.ProjectTerminal.rendered(item.after); },
    error: text => errors.push(text), inputError: text => errors.push(text) };
  vm.runInNewContext(source, context);
  return { context, requests, sockets, received, errors, listeners,
    advance(ms) {
      now += ms;
      for (const [id, timer] of [...timers]) if (timer.at <= now && timers.has(id)) { timers.delete(id); timer.fn(); }
    } };
}

async function check() {
  const live = page();
  live.context.ProjectTerminal.ready();
  const ws = live.sockets[0];
  assert.match(ws.url, /^wss:\/\/test.trycloudflare.com\/api\/terminal\/ws/);
  ws.open(); ws.message(output);
  live.context.ProjectTerminal.input('a');
  live.context.ProjectTerminal.input('b');
  assert.equal(ws.sent.length, 2, 'typing must not wait for the previous acknowledgment');
  const originals = ws.sent.map(item => JSON.stringify(item));
  ws.close(); live.advance(300);
  const resumed = live.sockets[1];
  assert.match(resumed.url, /after=7$/);
  resumed.open(); resumed.message(output);
  assert.deepEqual(resumed.sent.map(item => JSON.stringify(item)), originals, 'lost acknowledgments retry identical IDs and payloads');
  for (const op of resumed.sent) resumed.message({ t: 'ack', id: op.id, status: 200, state: 'queued' });
  live.advance(21000);
  assert.equal(resumed.readyState, 1, 'acknowledged operations must cancel their timeout');

  const reconnecting = page();
  reconnecting.context.ProjectTerminal.ready();
  const previous = reconnecting.sockets[0];
  previous.open(); previous.message(output); previous.close(); reconnecting.advance(300);
  previous.onclose({ code: 1006 });  // a late event from the old connection
  reconnecting.advance(4000); reconnecting.advance(0);
  assert.match(reconnecting.requests[0].url, /&wait=20$/, 'an old close event must not cancel the new connection timeout');

  const old = page();
  old.context.ProjectTerminal.ready(); old.sockets[0].close(); old.advance(0);
  assert.match(old.requests[0].url, /&wait=20$/, 'quick tunnels must bypass unsupported SSE when WebSocket fails');
  old.context.ProjectTerminal.input('first');
  let request = old.requests.find(item => item.options.method === 'POST');
  const first = JSON.parse(request.options.body);
  request.reject(new Error('lost response')); await settle();
  old.context.ProjectTerminal.input('second');
  request = old.requests.filter(item => item.options.method === 'POST').at(-1);
  assert.deepEqual(JSON.parse(request.options.body), first, 'new input must not change an operation already sent');
  request.resolve({ status: 200, json: async () => ({ state: 'queued' }) }); await settle();
  const next = JSON.parse(old.requests.filter(item => item.options.method === 'POST').at(-1).options.body);
  assert.notEqual(next.id, first.id);
  assert.equal(next.data, 'second');

  const buffered = page('relay.example');
  buffered.context.ProjectTerminal.ready(); buffered.sockets[0].close(); buffered.advance(0);
  assert.match(buffered.requests[0].url, /\/stream\?/);
  buffered.advance(4000); await settle();
  assert.match(buffered.requests.at(-1).url, /&wait=20$/, 'buffered first event must fall back within four seconds');
  const stale = buffered.requests.at(-1);
  buffered.context.document.hidden = true; buffered.listeners.visibilitychange(); await settle();
  assert.equal(stale.options.signal.aborted, true);
  buffered.context.document.hidden = false; buffered.listeners.visibilitychange();
  const current = buffered.requests.at(-1);
  assert.notEqual(current, stale);
  current.resolve({ status: 200, ok: true, json: async () => output }); await settle();
  assert.equal(buffered.received.length, 1, 'only the current reader may update the screen');
  const fresh = page();
  fresh.context.ProjectTerminal.ready(); fresh.sockets[0].open();
  fresh.sockets[0].message({ ...output, terminal: { id: terminalId, state: 'closed', tool: 'claude', dir: 'new-project', session: '' } });
  fresh.context.ProjectTerminal.again();
  const freshRequest = fresh.requests.find(item => item.options.method === 'POST');
  const freshPayload = JSON.parse(freshRequest.options.body);
  assert.equal(freshPayload.history, false, 'a terminal without an ID must restart fresh instead of opening the resume picker');
  assert.equal(freshPayload.session, undefined);
  freshRequest.resolve({ status: 200, json: async () => ({ terminal: terminalId }) }); await settle();
  const resumedPage = page();
  resumedPage.context.ProjectTerminal.ready(); resumedPage.sockets[0].open();
  const savedSession = '12345678-1234-1234-1234-123456789abc';
  resumedPage.sockets[0].message({ ...output, terminal: { id: terminalId, state: 'closed', tool: 'claude', dir: 'demo', session: savedSession } });
  resumedPage.context.ProjectTerminal.again();
  assert.equal(JSON.parse(resumedPage.requests.find(item => item.options.method === 'POST').options.body).session, savedSession, 'continue must preserve the exact conversation');
  console.log('bridge transport checks passed');
}

check().catch(error => { console.error(error); process.exitCode = 1; });
