/* The program on the computer, asked from the phone when the computer is out of reach: which line it is reached by,
   and a few of its settings. Changing the line is done by the computer: it makes the new line ready beside the one
   in use and hands this page its address with a sign-in that works once; the page goes there and says that it
   arrived. A page that does not arrive is waited for, and then the computer goes back to the line it had. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id), native = window.RemoteCliNative || null;
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  const wait = ms => new Promise(done => setTimeout(done, ms));
  const saved = (key, fallback) => {
    if (native && native.pref) { const chosen = native.pref(key); if (chosen) return chosen; }
    try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; }
  };
  const svg = path => `<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
  const relay = svg('<rect x="4" y="4.5" width="16" height="6" rx="2"/><rect x="4" y="13.5" width="16" height="6" rx="2"/><path d="M7.5 7.5h.01M7.5 16.5h.01"/>');
  const ICON = { back: svg('<path d="M15 5l-7 7 7 7"/>'), own: relay, public: relay,
    lan: svg('<path d="M4.5 10a11 11 0 0 1 15 0M7.5 13.2a6.6 6.6 0 0 1 9 0M10.4 16.3a2.4 2.4 0 0 1 3.2 0"/><path d="M12 19.5h.01"/>'),
    cloud: svg('<path d="M7.5 18.5a4 4 0 0 1-.6-7.950 5.500 5.500 0 0 1 10.600 1.200 3.400 3.400 0 0 1-.5 6.750z"/>') };
  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    Object.keys(props || {}).forEach(key => { if (key === 'html') node.innerHTML = props[key]; else if (key in node) node[key] = props[key]; else node.setAttribute(key, props[key]); });
    node.append(...children.filter(Boolean));
    return node;
  }
  let toastTimer = 0;
  function toast(text) { const box = $('toast'); box.textContent = text; box.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => { box.hidden = true; }, 3000); }

  // ---- look: the skin and the text size of the other pages
  const skin = window.RemoteCliPaint(saved('skin', 'paper'));
  document.documentElement.style.fontSize = { small: '14px', normal: '15.5px', large: '17px', larger: '19px' }[saved('text', 'normal')] || '15.5px';
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.content = skin.t.background;
  if (native && native.chrome) native.chrome(skin.t.background);
  $('back').innerHTML = ICON.back;
  // A page the phone was sent to from another line has nothing behind it.
  $('back').addEventListener('click', () => { if (history.length > 1 && document.referrer.startsWith(location.origin)) history.back(); else location.replace('../'); });

  // ---- asking the program
  async function ask(action, fields) {
    const reply = await fetch('../api/computer', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(Object.assign({ id: newId(), action }, fields)) });
    let body = {};
    try { body = await reply.json(); } catch (error) { /* not JSON */ }
    if (reply.status === 401) { location.replace('../'); throw new Error('请重新登录'); }
    if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
    return body;
  }
  // Asked again for a while: the program's relay may be started again in the middle of a change, and a computer that
  // has just turned to this line is not there in the first moment.
  async function read(tries) {
    for (let n = 1; ; n++) {
      try { return await ask('settings_read'); }
      catch (error) { if (n >= tries) throw error; await wait(1000); }
    }
  }
  function confirm(title, text, yes) {
    return new Promise(done => {
      const form = $('sheet-form'), sheet = $('sheet');
      let agreed = false;
      form.replaceChildren(el('h3', { textContent: title }), el('p', { textContent: text }),
        el('button', { type: 'button', className: 'choice solid', onclick: () => { agreed = true; sheet.close(); } }, el('span', { textContent: yes })),
        el('button', { type: 'button', className: 'cancel', textContent: '取消', onclick: () => sheet.close() }));
      sheet.onclose = () => done(agreed);
      sheet.onclick = event => { if (event.target === sheet) sheet.close(); };
      sheet.showModal();
    });
  }

  // ---- what is shown
  const KIND = { lan: ['局域网', '手机和电脑连同一个 Wi-Fi 时使用，速度最快'], cloud: ['公网隧道', '任何网络都能连；每次建立，地址都会变'],
    own: ['中转', '自己的中转 · 地址固定'], public: ['公共中转', '公共中转 · 无需配置，内容经由其服务器'] };
  const SETTINGS = [
    { name: 'rights', title: '新终端的权限', about: 'Claude Code 和 Codex 从手机启动时的权限模式，对之后新开的终端生效', choices: [['full', '默认'], ['edit', '自动改文件'], ['read', '只读']] },
    { name: 'tunnel', title: '公网隧道的协议', about: '隧道经常断开时改用 HTTP/2，下次建立隧道时生效', choices: [['auto', '自动'], ['http2', 'HTTP/2']] },
    { name: 'autostart', title: '登录 Windows 后自动启动', about: '开机后在托盘运行，不弹出窗口' },
    { name: 'update', title: '自动更新', about: '有新版本时，在终端空闲时自动安装' }];
  let told = null, busy = false;
  const name = line => line.name || KIND[line.kind][0];
  function state(kind, title, text) {
    const box = $('state');
    box.hidden = !title; box.className = 'notice ' + kind;
    $('state-title').textContent = title || ''; $('state-text').textContent = text || '';
  }
  function draw() {
    $('loading').hidden = true;
    if (!told) return;
    $('body').hidden = false;
    $('device-text').textContent = told.name + ' · v' + told.version;
    $('lines').classList.toggle('held', busy);
    $('lines').replaceChildren(...told.lines.map(line => {
      const about = KIND[line.kind][1] + (line.kind === 'cloud' && !line.ready ? ' · 需先下载隧道程序' : '');
      const tag = line.current ? el('span', { className: 'pill done', textContent: '使用中' }) : line.ms >= 0 ? el('span', { className: 'chip', textContent: line.ms + ' ms' })
        : line.ms === -1 ? el('span', { className: 'pill failed', textContent: '连不上' }) : '';
      return el('li', { className: 'card' + (line.current ? ' current' : '') }, el('button', { type: 'button', className: 'open', disabled: line.current || busy, onclick: () => turn(line) },
        el('span', { className: 'tool ' + line.kind, html: ICON[line.kind] }), el('span', { className: 'text' }, el('b', { textContent: name(line) }), el('span', { className: 'meta' }, el('span', { textContent: about }))), tag));
    }));
    $('settings').replaceChildren(...SETTINGS.map(setting => {
      const value = told.settings[setting.name];
      const control = setting.choices
        ? el('span', { className: 'pick', role: 'group', 'aria-label': setting.title }, ...setting.choices.map(([key, label]) =>
            el('button', { type: 'button', textContent: label, 'aria-pressed': String(key === value), onclick: () => { if (key !== value) change(setting.name, key); } })))
        : el('button', { type: 'button', className: 'flip', role: 'switch', 'aria-checked': String(value === true), 'aria-label': setting.title, onclick: () => change(setting.name, value !== true) });
      return el('li', { className: 'card' }, el('span', { className: 'text' }, el('b', { textContent: setting.title }), el('span', { textContent: setting.about })), control);
    }));
  }
  async function change(setting, value) {
    try { told = await ask('settings_change', { name: setting, value }); toast('已保存到电脑'); }
    catch (error) { toast(error.message); }
    draw();
  }

  // ---- another line
  async function turn(line) {
    if (busy) return;
    // An app that cannot follow to another address would be left behind at this one.
    if (native && !native.moved) return toast('请先把 App 更新到 1.2.0 或更高版本，再从手机切换线路');
    const more = line.kind === 'lan' ? '局域网只在手机和电脑连同一个 Wi-Fi 时能连上。' : line.kind === 'public' ? '终端内容会经过这个公共中转的服务器。'
      : line.kind === 'cloud' && !line.ready ? '电脑上还没有隧道程序，会先下载（约 60 MB），需要等几分钟。' : '';
    if (!await confirm('切换到“' + name(line) + '”？', '电脑先把新线路准备好，再把这个页面转过去。新线路连不上时，电脑会在 2 分钟内自动回到现在的线路。' + more, '切换')) return;
    busy = true; draw();
    try {
      told = await ask('line_switch', { to: line.id });
      // The computer makes the line ready; the page asks how far it is, takes it when it is ready, and goes there.
      for (;;) {
        const now = told.change;
        if (now.state === 'ready') {
          const handed = await ask('line_take');
          state('busy', '正在转到“' + name(line) + '”', '');
          if (native) native.moved(handed.address, handed.ticket, handed.key);
          else location.href = handed.address + '/computer/#t=' + encodeURIComponent(handed.ticket) + '&keep=' + handed.key;
          return;
        }
        if (now.state !== 'preparing') { state('', now.state === 'failed' ? '没有切换到“' + name(line) + '”' : '', now.note); break; }
        state('busy', '正在准备“' + name(line) + '”', now.note);
        await wait(1200);
        told = await read(15);
      }
    } catch (error) { state('', '没有切换到“' + name(line) + '”', error.message); }
    busy = false; draw();
  }
  // This page was opened at the new line with what the old one was handed: it signs in, and tells the computer that
  // the phone has arrived, with the word the computer gave for it.
  async function arrive(ticket, key) {
    history.replaceState(null, '', location.pathname);
    state('busy', '正在新线路上确认', '');
    const signed = await fetch('../api/login', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ticket }) });
    if (!signed.ok) return state('', '没有登录到新线路', '这次切换的登录已失效。电脑会自动回到原来的线路，之后请从工作台重新打开这台电脑。');
    told = await read(60);
    if (told.change.state !== 'trial' && told.change.state !== 'moving') return state('', '这次切换已经结束', told.change.note || '');
    told = await ask('line_keep', { key });
    const current = told.lines.find(line => line.current);
    state('good', '已切换到“' + (current ? name(current) : '新线路') + '”', '之后都从这条线路连接这台电脑。');
    if (native && native.kept) native.kept();
  }

  (async () => {
    const handed = new URLSearchParams(location.hash.slice(1));
    try {
      if (handed.get('t') && handed.get('keep')) await arrive(handed.get('t'), handed.get('keep'));
      else {
        told = await read(1);
        if (told.change.state === 'failed') state('', '上一次切换线路没有完成', told.change.note);
        else if (told.change.state) state('plain', '电脑正在切换线路', told.change.note);
      }
    } catch (error) { state('', '没有读到电脑的设置', error.message); }
    draw();
  })();
})();
