/* The terminal screen. bridge.js connects it to the relay. */
(async () => {
  'use strict';
  const bridge = window.ProjectTerminal;
  const $ = id => document.getElementById(id);
  const input = $('input'), status = $('status-text'), notice = $('notice'), palette = $('palette'), sendButton = $('send');
  const screen = $('screen'), holder = $('terminal'), dot = $('dot'), slash = $('slash'), latest = $('latest');

  const SKINS = window.RemoteCliSkins;      // skins.js
  let skin = bridge && SKINS[bridge.skin()] ? bridge.skin() : 'paper';
  const FAMILY = '"Terminal Mono", "Noto Sans Mono CJK SC", "Droid Sans Mono", monospace';
  // The typeface must be ready before the terminal measures a character, or every cell gets the wrong width.
  try { await Promise.race([Promise.all([document.fonts.load('13px "Terminal Mono"'), document.fonts.load('bold 13px "Terminal Mono"')]), new Promise(done => setTimeout(done, 900))]); } catch (error) { /* system monospace */ }

  const sizes = [11, 13, 15, 17];
  // Room between the lines: closer shows more of the screen, wider is easier on the eyes.
  const SPACING = { tight: ['紧凑', 1.0], cozy: ['适中', 1.12], roomy: ['宽松', 1.3] };
  let spacing = bridge && bridge.spacing && SPACING[bridge.spacing()] ? bridge.spacing() : 'cozy';
  let fontSize = Number(bridge && bridge.fontSize()) || 13;
  if (!sizes.includes(fontSize)) fontSize = 13;
  const term = new Terminal({ fontSize, fontFamily: FAMILY, cursorBlink: false, lineHeight: SPACING[spacing][1],
    // No per-row accessibility tree: on a phone it is rebuilt on every refresh and makes typing stutter.
    scrollback: 5000, screenReaderMode: false, allowProposedApi: false, theme: SKINS[skin].t, ...window.RemoteCliLook });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(holder);
  // Drawing on the graphics card keeps a busy screen smooth; a phone that cannot do it falls back to plain elements.
  let drawing = 'dom';
  if (window.WebglAddon && !(bridge && bridge.renderer() === 'dom')) {
    try {
      const gl = new WebglAddon.WebglAddon();
      gl.onContextLoss(() => { gl.dispose(); drawing = 'dom'; });
      term.loadAddon(gl);
      drawing = 'webgl';
    } catch (error) { drawing = 'dom'; }
  }
  function applySkin(name) {
    if (!SKINS[name]) return;
    skin = name;
    const s = window.RemoteCliPaint(name);
    term.options.theme = s.t;
    if (bridge) bridge.chrome(s.t.background);
  }
  applySkin(skin);

  // What the tools themselves offer after "/". Anything else can still be typed.
  const COMMANDS = {
    claude: [
      ['/resume', '打开历史对话列表，用 ↑ ↓ 选择后回车'], ['/model', '切换模型'], ['/compact', '压缩上下文，保留摘要继续'],
      ['/clear', '清空当前对话，重新开始'], ['/context', '查看上下文占用'], ['/usage', '查看套餐用量与限额'],
      ['/cost', '查看本次对话的用量'], ['/status', '查看版本、账号与连接状态'], ['/rewind', '回退到之前的某一步'],
      ['/permissions', '查看和调整工具权限'], ['/memory', '编辑记忆文件'], ['/review', '审查当前改动'],
      ['/agents', '管理子代理'], ['/mcp', '查看 MCP 服务'], ['/init', '为项目生成 CLAUDE.md'], ['/help', '全部命令与说明'],
      ['/exit', '退出 Claude Code（终端随之结束）']],
    codex: [
      ['/resume', '打开历史对话列表，用 ↑ ↓ 选择后回车'], ['/model', '切换模型与推理强度'], ['/new', '开始新对话'],
      ['/compact', '压缩上下文，保留摘要继续'], ['/status', '查看当前配置与用量'], ['/diff', '查看当前改动'],
      ['/review', '审查当前改动'], ['/mcp', '查看 MCP 服务'], ['/init', '为项目生成 AGENTS.md'],
      ['/quit', '退出 Codex（终端随之结束）']],
    shell: []
  };
  const KEYS = { escape: '\x1b', tab: '\t', backtab: '\x1b[Z', up: '\x1b[A', down: '\x1b[B', left: '\x1b[D', right: '\x1b[C', enter: '\r', interrupt: '\x03',
    pageup: '\x1b[5~', pagedown: '\x1b[6~' };

  let running = false, restoring = true, initialized = false, lastSize = '', tool = 'claude', ended = false;
  let wide = 0, tall = 0, start = 0, paletteOpen = false, sizeTimer = 0;
  // A terminal can be open on the phone and in a window on the computer at once, and the two are seldom the same
  // size. The program is drawn for one size only: that of whoever typed last. `theirs` is the size the computer
  // reports; `sized` is when this page last asked for its own, so that a report still on its way is not taken for
  // someone else's wish.
  let theirs = '', sized = 0;

  function send(data) {
    if (!bridge) return false;
    if (!running) { notice.textContent = restoring && !ended ? '正在载入终端输出，稍后再发送' : '终端尚未连接或已经结束'; return false; }
    const mine = `${term.cols}x${term.rows}`;
    if (theirs && theirs !== mine && Date.now() - sized > 2000) {
      lastSize = mine; sized = Date.now();
      bridge.resize(JSON.stringify({ cols: term.cols, rows: term.rows }));
    }
    bridge.input(data);
    return true;
  }
  function resize() {
    const proposed = fit.proposeDimensions();
    if (!proposed || !proposed.cols || !proposed.rows) return;
    const cols = Math.max(20, Math.min(240, proposed.cols)), rows = Math.max(6, Math.min(100, proposed.rows));
    if (cols !== term.cols || rows !== term.rows) term.resize(cols, rows);
    const size = `${cols}x${rows}`;
    // The program on the computer is told once the phone's size has settled, and never while old output is replayed.
    clearTimeout(sizeTimer);
    if (running && size !== lastSize && bridge) sizeTimer = setTimeout(() => {
      if (!running || size === lastSize) return;
      lastSize = size; sized = Date.now();
      bridge.resize(JSON.stringify({ cols, rows }));
    }, 180);
  }
  function layout() {
    const w = screen.clientWidth, h = screen.clientHeight;
    if (!w || !h) return;
    if (w !== wide) { wide = w; tall = h; } else if (h > tall) tall = h;
    holder.style.height = tall + 'px';
    if (!restoring || !initialized) resize();
  }
  new ResizeObserver(layout).observe(screen);

  function follow() {
    const buffer = term.buffer.active;
    const atBottom = buffer.viewportY >= buffer.baseY && remoteUp <= 0;
    if (latest.hidden !== atBottom) latest.hidden = atBottom;
  }
  term.onScroll(follow);
  // Claude Code and Codex scroll their own screen. How far up the wheel has taken them is counted here, so that
  // "back to the newest" can take them down again; a few steps too many do no harm.
  let remoteUp = 0;
  latest.addEventListener('click', () => {
    scroller.stop();
    if (remoteUp > 0 && term.modes.mouseTrackingMode !== 'none') {
      const column = Math.max(1, Math.floor(term.cols / 2)), row = Math.max(1, Math.floor(term.rows / 2));
      send(`\x1b[<65;${column};${row}M`.repeat(Math.min(600, remoteUp + 12)));
    }
    remoteUp = 0;
    term.scrollToBottom(); follow();
  });
  term.onData(data => { if (!restoring) send(data); });

  // Dragging a finger over the screen scrolls it. Claude Code and Codex draw a full screen of their own and keep the
  // earlier lines themselves, so for them the drag is passed on as mouse-wheel steps; a plain shell keeps its lines
  // here and is scrolled directly.
  let pageLines = 0;
  const scroller = new TerminalScroller({
    request: callback => requestAnimationFrame(callback), cancel: id => cancelAnimationFrame(id),
    lineHeight: () => tall / Math.max(1, term.rows),
    mode: () => term.modes.mouseTrackingMode !== 'none' ? 'mouse' : term.buffer.active.type === 'alternate' ? 'page' : 'local',
    visible: () => !document.hidden,
    reduced: () => window.matchMedia('(prefers-reduced-motion: reduce)').matches,
    scroll(lines, mode) {
      if (mode === 'mouse') {
        const column = Math.max(1, Math.floor(term.cols / 2)), row = Math.max(1, Math.floor(term.rows / 2));
        const sent = send(`\x1b[<${lines > 0 ? 64 : 65};${column};${row}M`.repeat(Math.abs(lines)));
        if (sent) { remoteUp = Math.max(0, remoteUp + lines); follow(); }
        return sent;
      }
      if (mode === 'page') {
        pageLines += lines;
        const pages = Math.trunc(pageLines / 8);
        if (pages) { pageLines -= pages * 8; return send((pages > 0 ? KEYS.pageup : KEYS.pagedown).repeat(Math.abs(pages))); }
        return true;
      }
      const before = term.buffer.active.viewportY;
      term.scrollLines(-lines);
      return term.buffer.active.viewportY !== before;
    }
  });
  screen.addEventListener('touchstart', event => {
    pageLines = 0;
    if (event.touches.length === 1 && !latest.contains(event.target)) scroller.start(event.touches[0].clientY, performance.now());
    else scroller.stop();
  }, { capture: true, passive: true });
  screen.addEventListener('touchmove', event => {
    if (event.touches.length !== 1) { scroller.stop(); return; }
    if (!scroller.dragging) return;
    scroller.move(event.touches[0].clientY, performance.now());
    event.preventDefault(); event.stopPropagation();
  }, { capture: true, passive: false });
  screen.addEventListener('touchend', () => scroller.end(performance.now()), { capture: true, passive: true });
  screen.addEventListener('touchcancel', () => scroller.stop(), { capture: true, passive: true });
  document.addEventListener('visibilitychange', () => { if (document.hidden) scroller.stop(); });

  // Buttons act on the terminal without taking the keyboard away from the message box.
  function keepFocus(element) { element.addEventListener('mousedown', event => event.preventDefault()); }
  document.querySelectorAll('.keys button, #slash, #send, #latest, #voice').forEach(keepFocus);
  document.querySelectorAll('[data-key]').forEach(button => button.addEventListener('click', () => send(KEYS[button.dataset.key])));

  // The box is measured only when its text may have wrapped differently, once per frame: measuring on every key
  // press made the whole page lay itself out again while typing.
  let boxHeight = 44, boxLength = 0, boxFrame = 0, boxFilled = null;
  function measure() {
    boxFrame = 0;
    const length = input.value.length;
    if (length < boxLength || !length) { input.style.height = '44px'; boxHeight = 44; }
    boxLength = length;
    const wanted = Math.min(120, Math.max(44, input.scrollHeight + 2));
    if (wanted !== boxHeight) { boxHeight = wanted; input.style.height = wanted + 'px'; }
  }
  function grow() {
    const filled = input.value.length > 0;
    if (filled !== boxFilled) {
      boxFilled = filled;
      sendButton.textContent = filled ? '发送' : '回车';
      sendButton.classList.toggle('text', filled);
    }
    if (!boxFrame) boxFrame = requestAnimationFrame(measure);
  }
  function drawPalette() {
    const typed = input.value;
    const typing = /^\/\S*$/.test(typed);
    const list = COMMANDS[tool] || [];
    if (!list.length || ended || !(typing || paletteOpen)) { palette.hidden = true; slash.setAttribute('aria-expanded', 'false'); return; }
    const wanted = typing ? typed.toLowerCase() : '/';
    const matches = list.filter(c => c[0].startsWith(wanted)).concat(list.filter(c => !c[0].startsWith(wanted) && c[0].includes(wanted.slice(1)) && wanted.length > 1));
    palette.textContent = '';
    matches.forEach(([command, about]) => {
      const row = document.createElement('button'), name = document.createElement('b'), text = document.createElement('span');
      row.type = 'button'; row.setAttribute('role', 'option'); name.textContent = command; text.textContent = about;
      row.append(name, text); keepFocus(row);
      row.addEventListener('click', () => { if (send(paste(command) + '\r')) { input.value = ''; paletteOpen = false; grow(); drawPalette(); } });
      palette.append(row);
    });
    if (!matches.length) { const none = document.createElement('p'); none.textContent = '常用命令里没有匹配项；仍可直接发送，由工具自己处理。'; palette.append(none); }
    palette.hidden = false; slash.setAttribute('aria-expanded', 'true');
  }
  // Bracketed paste keeps Chinese and multi-line text intact in the tool's own editor.
  function paste(text) { return term.modes.bracketedPasteMode ? `\x1b[200~${text}\x1b[201~` : text; }
  input.addEventListener('input', () => { grow(); if (!palette.hidden || input.value.charCodeAt(0) === 47) drawPalette(); });
  slash.addEventListener('click', () => {
    paletteOpen = palette.hidden;
    if (paletteOpen && !input.value) { input.value = '/'; grow(); } else if (!paletteOpen && /^\/\S*$/.test(input.value)) { input.value = ''; grow(); }
    drawPalette();
  });
  $('composer').addEventListener('submit', event => {
    event.preventDefault();
    const text = input.value;
    if (!text) { send('\r'); return; }
    if (!text.trim()) return;
    if (send(paste(text) + '\r')) { input.value = ''; paletteOpen = false; grow(); drawPalette(); remoteUp = 0; term.scrollToBottom(); follow(); }
  });
  $('font').addEventListener('click', () => {
    fontSize = sizes[(sizes.indexOf(fontSize) + 1) % sizes.length];
    term.options.fontSize = fontSize;
    if (bridge) bridge.saveFontSize(fontSize);
    resize();
  });
  $('menu').addEventListener('click', () => { if (bridge) bridge.menu(); });
  $('files').addEventListener('click', () => { if (bridge) bridge.files(); });
  $('again').addEventListener('click', () => { if (bridge) bridge.again(); });
  $('back').addEventListener('click', () => { if (bridge) bridge.close(); });
  $('voice').addEventListener('click', () => { if (bridge) bridge.voice(); });

  let wasEnded = null;
  function show(element, text) { if (element.textContent !== text) element.textContent = text; }
  window.TerminalUI = {
    receive(payload) {
      const t = payload.terminal, device = payload.device;
      const live = t.state === 'running' && device.online && device.enabled;
      tool = t.tool; ended = t.state === 'closed';
      theirs = t.cols && t.rows ? `${t.cols}x${t.rows}` : '';
      slash.hidden = !(COMMANDS[tool] || []).length;
      if ($('files').hidden) $('files').hidden = false;        // the folder this terminal works in is known now
      if (!initialized || payload.reset) {
        restoring = true; running = false;
        scroller.stop();
        term.reset(); term.resize(t.cols || 80, t.rows || 24);
        start = payload.chunks.length ? payload.chunks[0].seq - 1 : payload.after;
      }
      const more = payload.after < t.seq;
      // Output arrives many times a second: the page around the terminal is touched only where something changed.
      const plain = device.shell || 'PowerShell';      // what the computer calls its plain terminal
      const where = (tool === 'codex' ? 'Codex' : tool === 'shell' ? plain : 'Claude Code') + ' · ' + t.dir;
      // Someone else's size on the screen means the terminal is open in another place too.
      $('shared').hidden = !(running && theirs && theirs !== `${term.cols}x${term.rows}` && Date.now() - sized > 2500);
      const loading = restoring && more ? `载入输出 ${Math.round(100 * (payload.after - start) / Math.max(1, t.seq - start))}%` : '';
      show($('title'), t.title);
      show(status, where + ' · ' + (loading || (!device.online ? '电脑离线，等待重连' : !device.enabled ? '电脑远控已关闭' : t.state === 'starting' ? '正在启动'
        : ended ? '已结束' : t.status === 'busy' ? '正在执行' : t.status === 'idle' ? '等待输入' : '已连接')));
      const mark = live ? (t.status === 'busy' ? 'busy' : 'ready') : '';
      if (dot.className !== mark) dot.className = mark;
      show(notice, t.error || '');
      if ($('ended').hidden !== (!ended || more)) $('ended').hidden = !ended || more;
      if (ended !== wasEnded) {
        wasEnded = ended;
        show($('ended-text'), tool === 'shell' ? plain + ' 已结束，画面保留到这里' : '终端已结束，对话保存在电脑上');
        $('again').hidden = tool === 'shell';
        input.disabled = ended; sendButton.disabled = ended; $('voice').disabled = ended;
        drawPalette();
      }
      const text = payload.chunks.map(c => c.data).join('');
      const written = () => {
        initialized = true;
        if (!more) {
          const caughtUp = restoring, connected = live && !running;
          restoring = false; running = live;
          if (caughtUp) term.scrollToBottom();
          if (caughtUp || connected) layout();      // the size is settled once, not after every piece of output
        } else if (!restoring) running = live;
        follow();
        if (bridge) bridge.rendered(String(payload.after), more ? 'more' : text || t.status === 'busy' ? 'active' : 'quiet');
      };
      if (text) term.write(text, written); else written();
    },
    error(message) { notice.textContent = message; status.textContent = '连接中断，正在重连'; dot.className = ''; running = false; },
    inputError(message, draft) {
      notice.textContent = message;
      // A typed message comes back for another try; a single key press does not.
      const typed = (draft || '').replace(/\x1b\[20[01]~/g, '').replace(/\r$/, '');
      if (typed && !input.value && !/[\x00-\x08\x0b-\x1f]/.test(typed)) { input.value = typed; grow(); }
    },
    // Spoken words are put into the message box to be checked before they are sent.
    dictated(text) {
      if (!text || ended) return;
      const gap = /[A-Za-z0-9]$/.test(input.value) && /^[A-Za-z0-9]/.test(text) ? ' ' : '';   // Chinese runs on without a space
      input.value = (input.value + gap + text).slice(0, 8000);
      grow(); drawPalette();
    },
    skins() { return Object.keys(SKINS).map(key => ({ key, name: SKINS[key].name + (SKINS[key].light ? '（明亮）' : ''), current: key === skin })); },
    spacings() { return Object.keys(SPACING).map(key => ({ key, name: SPACING[key][0], current: key === spacing })); },
    setSpacing(name) { if (!SPACING[name]) return; spacing = name; term.options.lineHeight = SPACING[name][1]; resize(); },
    setSkin(name) { applySkin(name); },
    drawing() { return drawing; },
    screenText() {
      let text = term.getSelection();
      if (!text) {
        const buffer = term.buffer.active, lines = [];
        for (let i = 0; i < buffer.length; i++) lines.push(buffer.getLine(i).translateToString(true));
        text = lines.join('\n').trim();
      }
      return text;
    },
    terminal: term
  };
  grow();
  layout();
  if (bridge) bridge.ready();
})();
