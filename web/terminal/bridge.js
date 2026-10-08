/* Connects the terminal page to the relay from a browser: reading output, sending input, and the page's menu.
   Inside the Android app, window.RemoteCliNative adds system speech recognition and status-bar colour. */
(() => {
  'use strict';
  const id = new URLSearchParams(location.search).get('id') || '';
  const native = window.RemoteCliNative || null;
  const store = { get(key, fallback) { try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; } },
    set(key, value) { try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ } } };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const ui = () => window.TerminalUI;
  const home = () => { location.replace('../'); };      // the list is one step back, not one more page
  let after = 0, terminal = null, loaded = false, streamed = true, failures = 0, reader = null, run = 0, fetching = false, gone = false;

  async function post(payload) {
    const reply = await fetch('/api/terminal', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    let body = {};
    try { body = await reply.json(); } catch (error) { /* not JSON */ }
    if (reply.status === 401) { home(); throw new Error('请重新输入访问密码'); }
    return { status: reply.status, body };
  }

  // ---- output: one response that stays open; a network that cuts it falls back to held requests
  function receive(item) {
    terminal = item.terminal;
    if (streamed) after = item.after;
    ui().receive(item);
  }
  function problem() { ui().error('读取失败，正在重连；电脑上的终端会继续运行'); }
  async function stream() {
    if (gone || !loaded || reader) return;
    const mine = ++run;
    let got = 0, status = 0;
    const control = new AbortController();
    reader = control;
    try {
      const reply = await fetch('/api/terminal/stream?terminal=' + id + '&after=' + after, { credentials: 'same-origin', signal: control.signal, headers: { Accept: 'text/event-stream' } });
      status = reply.status;
      if (status === 200 && reply.body) {
        const body = reply.body.getReader(), text = new TextDecoder();
        let rest = '';
        for (;;) {
          const { value, done } = await body.read();
          if (done || mine !== run) break;
          rest += text.decode(value, { stream: true });
          let at;
          while ((at = rest.indexOf('\n')) >= 0) {
            const line = rest.slice(0, at); rest = rest.slice(at + 1);
            if (line.startsWith('data:')) { got++; receive(JSON.parse(line.slice(5))); }
          }
        }
      } else if (status === 400) { let body = {}; try { body = await reply.json(); } catch (error) { /* not JSON */ } ui().error(body.error || '终端已不存在'); gone = true; }
    } catch (error) { /* reconnect below */ }
    if (mine !== run) return;
    reader = null;
    if (gone) return;
    if (status === 401) return home();
    failures = got ? 0 : failures + 1;
    if (failures >= 2) { streamed = false; return held(); }
    if (!got) problem();
    if (!document.hidden) setTimeout(read, got ? 30 : 1500);
  }
  async function held() {
    if (gone || !loaded || fetching || document.hidden) return;
    fetching = true;
    try {
      const reply = await fetch('/api/terminal?terminal=' + id + '&after=' + after + '&wait=20', { credentials: 'same-origin' });
      if (reply.status === 401) return home();
      const item = await reply.json();
      if (!reply.ok) throw new Error(item.error || '');
      fetching = false;
      receive(item);
    } catch (error) { fetching = false; problem(); setTimeout(read, 1500); }
  }
  function read() { if (streamed) stream(); else held(); }
  function hangUp() { run++; if (reader) { reader.abort(); reader = null; } }
  document.addEventListener('visibilitychange', () => { if (document.hidden) hangUp(); else read(); });

  // ---- input: one request at a time, in order; keys typed meanwhile travel together
  const queue = [];
  let sending = false;
  function enqueue(op) {
    Object.assign(op, { id: newId(), terminal: id });
    op.at = Date.now();
    const last = queue[queue.length - 1];
    if (last && !(sending && queue.length === 1) && op.action === 'input' && last.action === 'input' && last.data.length + op.data.length <= 8000) last.data += op.data;
    else queue.push(op);
    flush();
  }
  async function flush() {
    if (sending || !queue.length) return;
    const op = queue[0];
    if (Date.now() - op.at > 110000) { queue.shift(); ui().inputError('发送超时，请确认终端画面后重试', op.data || ''); return flush(); }
    sending = true;
    let message = '', again = false;
    try {
      const { at, ...payload } = op;
      const { status, body } = await post(payload);
      if (status !== 200 || body.state === 'error') { message = body.error || '操作没有执行'; again = status >= 500; }
    } catch (error) { again = true; message = '发送失败，正在重连'; }
    sending = false;
    if (!again) queue.shift();
    if (message) ui().inputError(message, again ? '' : op.data || '');
    if (again) setTimeout(flush, 1000); else flush();
  }

  // ---- menu
  function sheet(title, choices) {
    let dialog = document.getElementById('rcli-menu');
    if (!dialog) { dialog = document.createElement('dialog'); dialog.id = 'rcli-menu'; document.body.append(dialog); }
    dialog.textContent = '';
    const heading = document.createElement('h2'); heading.textContent = title; dialog.append(heading);
    choices.forEach(([label, act, current]) => {
      const button = document.createElement('button');
      button.type = 'button'; button.textContent = label;
      if (current) button.setAttribute('aria-current', 'true');
      button.onclick = () => { dialog.close(); act(); };
      dialog.append(button);
    });
    const cancel = document.createElement('button');
    cancel.type = 'button'; cancel.className = 'cancel'; cancel.textContent = '取消'; cancel.onclick = () => dialog.close();
    dialog.append(cancel);
    dialog.showModal();
  }
  async function again() {
    if (!terminal || terminal.state !== 'closed' || terminal.tool === 'shell') return;
    const start = { action: 'start', id: newId(), tool: terminal.tool, dir: terminal.dir };
    if (terminal.session) start.session = terminal.session; else start.history = true;
    try {
      const { status, body } = await post(start);
      if (status !== 200) throw new Error(body.error || '没有打开');
      location.replace('?id=' + body.terminal);
    } catch (error) { ui().error(error.message); }
  }
  function menu() {
    if (!terminal) return;
    const closed = terminal.state === 'closed', dom = store.get('renderer', 'webgl') === 'dom', choices = [];
    if (closed && terminal.tool !== 'shell') choices.push(['继续这个对话', again]);
    choices.push(['更换外观', () => sheet('终端外观', ui().skins().map(s => [s.name, () => { store.set('skin', s.key); ui().setSkin(s.key); }, s.current]))]);
    choices.push(['重命名', async () => {
      const title = (prompt('终端名称', terminal.title) || '').trim();
      if (title) { const { status, body } = await post({ action: 'rename', id: newId(), terminal: id, title }); if (status !== 200) ui().error(body.error || '没有保存'); }
    }]);
    choices.push(['复制终端画面', async () => { try { await navigator.clipboard.writeText(ui().screenText()); } catch (error) { ui().error('浏览器不允许复制，请长按选择文字'); } }]);
    choices.push([dom ? '画面卡顿时：改用显卡绘制' : '画面空白或花屏时：改用兼容绘制', () => { store.set('renderer', dom ? 'webgl' : 'dom'); location.reload(); }]);
    if (!closed) choices.push(['结束终端', () => { if (confirm('结束这个终端？正在运行的任务会被中断，对话保存在电脑上。')) enqueue({ action: 'close' }); }]);
    sheet('终端选项', choices);
  }

  // ---- speech: the app's system recognizer, else the browser's where it has one
  window.RemoteCliDictated = text => ui().dictated(String(text || ''));
  function voice() {
    if (native && native.voice) return native.voice();
    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Recognition) return ui().error('这个浏览器没有语音识别，请用输入法的语音键');
    const listener = new Recognition();
    listener.lang = navigator.language || 'zh-CN';
    listener.onresult = event => ui().dictated(event.results[0][0].transcript);
    listener.onerror = () => ui().error('没有听清，请再试一次或用输入法的语音键');
    listener.start();
  }

  window.ProjectTerminal = {
    ready() { loaded = true; if (!/^[a-f0-9]{16,32}$/.test(id)) return home(); read(); },
    rendered(cursor) { if (streamed) return; after = Number(cursor) || after; setTimeout(read, 20); },
    close: home,
    voice,
    skin: () => store.get('skin', 'night'),
    renderer: () => store.get('renderer', 'webgl'),
    chrome(color) {
      if (!/^#[0-9a-fA-F]{6}$/.test(color)) return;
      let meta = document.querySelector('meta[name="theme-color"]');
      if (!meta) { meta = document.createElement('meta'); meta.name = 'theme-color'; document.head.append(meta); }
      meta.content = color;
      if (native && native.chrome) native.chrome(color);
    },
    input(data) { if (data && data.length <= 16000) enqueue({ action: 'input', data }); },
    resize(size) { try { const s = JSON.parse(size); enqueue({ action: 'resize', cols: s.cols, rows: s.rows }); } catch (error) { /* ignore */ } },
    menu,
    again,
    fontSize: () => Number(store.get('font', '13')),
    saveFontSize(size) { if (size >= 9 && size <= 20) store.set('font', String(size)); }
  };
})();
