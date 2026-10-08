/* The list: projects on the computer, terminals opened from the phone, and the conversations saved there. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const TOOLS = { claude: 'Claude Code', codex: 'Codex', shell: 'PowerShell' };
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  let data = null, project = '', timer = 0, busy = false;
  const saved = (key, fallback) => { try { return localStorage.getItem('rcli-' + key) || fallback; } catch (error) { return fallback; } };
  const keep = (key, value) => { try { localStorage.setItem('rcli-' + key, value); } catch (error) { /* private window */ } };
  project = saved('project', '');
  // The look chosen here is the one the terminal page opens with.
  const TEXT = { small: ['紧凑', '14px'], normal: ['标准', '15.5px'], large: ['大', '17px'], larger: ['特大', '19px'] };
  // ?skin=<name> chooses a skin from a link, for example when showing the app to someone.
  const asked = new URLSearchParams(location.search).get('skin');
  if (asked && window.RemoteCliSkins[asked]) keep('skin', asked);
  function paint() {
    const skin = window.RemoteCliPaint(saved('skin', 'night'));
    document.documentElement.style.fontSize = (TEXT[saved('text', 'normal')] || TEXT.normal)[1];
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = skin.t.background;
    if (window.RemoteCliNative) window.RemoteCliNative.chrome(skin.t.background);
  }
  paint();

  async function api(path, payload) {
    const reply = await fetch(path, payload === undefined ? { credentials: 'same-origin' }
      : { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    let body = {};
    try { body = await reply.json(); } catch (error) { /* not JSON */ }
    if (reply.status === 401 && path !== '/api/login') { showLogin(); throw new Error('请重新输入访问密码'); }
    if (!reply.ok) throw new Error(body.error || '请求失败（' + reply.status + '）');
    return body;
  }
  function say(text) { $('message').textContent = text || ''; }
  function el(tag, props, ...children) {
    const node = Object.assign(document.createElement(tag), props || {});
    node.append(...children.filter(Boolean));
    return node;
  }
  function ago(ms) {
    const s = Math.max(0, (Date.now() - ms) / 1000);
    return s < 90 ? '刚刚' : s < 3600 ? Math.round(s / 60) + ' 分钟前' : s < 86400 ? Math.round(s / 3600) + ' 小时前' : Math.round(s / 86400) + ' 天前';
  }

  function showLogin() {
    clearTimeout(timer);
    $('home').hidden = true; $('login').hidden = false;
    $('password').focus();
  }
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault();
    $('login-error').textContent = '';
    try {
      await api('/api/login', { password: $('password').value });
      $('password').value = '';
      $('login').hidden = true; $('home').hidden = false;
      refresh();
    } catch (error) { $('login-error').textContent = error.message; }
  });
  $('look').addEventListener('click', async () => {
    const skins = window.RemoteCliSkins, now = saved('skin', 'night'), size = saved('text', 'normal');
    const choice = await ask('外观', '配色同时用于列表和终端。',
      Object.keys(skins).map(key => ({ label: (key === now ? '● ' : '') + skins[key].name + (skins[key].light ? '（明亮）' : ''), value: 'skin:' + key }))
        .concat(Object.keys(TEXT).map(key => ({ label: (key === size ? '● ' : '') + '文字 ' + TEXT[key][0], value: 'text:' + key }))));
    if (!choice) return;
    const [kind, value] = choice.split(':');
    keep(kind, value);
    paint();
  });
  $('logout').addEventListener('click', async () => { try { await api('/api/logout', {}); } catch (error) { /* signed out anyway */ } showLogin(); });

  // A sheet with a few choices; resolves with the chosen value, or null when dismissed.
  function ask(title, text, choices, fields) {
    return new Promise(done => {
      const form = $('sheet-form'), sheet = $('sheet');
      form.textContent = '';
      form.append(el('h3', { textContent: title }), text ? el('p', { textContent: text }) : '');
      const inputs = {};
      (fields || []).forEach(f => {
        inputs[f.key] = el('input', { id: 'field-' + f.key, value: f.value || '', placeholder: f.placeholder || '', maxLength: f.max || 240 });
        form.append(el('label', { htmlFor: 'field-' + f.key, textContent: f.label }), inputs[f.key]);
      });
      let answer = null;
      choices.forEach(c => form.append(el('button', { type: 'button', className: c.kind || '', textContent: c.label, onclick: () => {
        answer = fields ? Object.assign({ choice: c.value }, ...Object.keys(inputs).map(k => ({ [k]: inputs[k].value.trim() }))) : c.value;
        sheet.close();
      } })));
      form.append(el('button', { type: 'button', className: 'quiet', textContent: '取消', onclick: () => sheet.close() }));
      sheet.onclose = () => done(answer);
      sheet.showModal();
    });
  }

  // Sends one operation and waits until the computer has carried it out.
  async function run(payload, waiting) {
    if (busy) return null;
    busy = true; say(waiting);
    try {
      const op = Object.assign({ id: newId() }, payload);
      let result = await api('/api/terminal', op);
      for (let n = 0; n < 60 && result.state === 'queued'; n++) {
        await new Promise(r => setTimeout(r, 400));
        result = await api('/api/terminal', op);
      }
      if (result.error) throw new Error(result.error);
      if (result.state === 'queued') throw new Error('电脑没有响应，请确认电脑端程序在运行');
      say('');
      return result;
    } catch (error) { say(error.message); return null; } finally { busy = false; }
  }
  function open(terminal) { location.href = 'terminal/?id=' + encodeURIComponent(terminal); }
  async function start(tool, session, takeover) {
    const payload = { action: 'start', tool, dir: project };
    if (session) Object.assign(payload, { session, takeover: !!takeover });
    const result = await run(payload, '正在电脑上启动 ' + TOOLS[tool] + '…');
    if (result) open(result.terminal);
  }

  function draw() {
    const device = data.device, ready = device.online && device.enabled;
    $('dot').className = ready ? 'on' : '';
    $('device-text').textContent = !device.online ? '电脑离线：请确认电脑端程序在运行' : !device.enabled ? '电脑端已关闭远程访问' : '电脑在线';
    const names = device.workspaces || [];
    if (!names.includes(project)) project = names[0] || '';
    const bar = $('projects');
    bar.textContent = '';
    names.forEach(name => bar.append(el('button', { type: 'button', textContent: name, ariaPressed: String(name === project), onclick: () => {
      project = name;
      keep('project', name);
      draw();
    } })));
    bar.append(el('button', { type: 'button', className: 'quiet', textContent: '＋ 项目', disabled: !ready, onclick: addProject }));

    const launch = $('launch');
    launch.textContent = '';
    const folder = (device.projects || []).find(p => p.name === project);
    if (project) {
      Object.keys(TOOLS).filter(t => (device.tools || []).includes(t)).forEach((tool, i) => launch.append(
        el('button', { type: 'button', className: i ? '' : 'solid', textContent: '新建 ' + TOOLS[tool], disabled: !ready, onclick: () => start(tool) })));
      if (folder) launch.append(el('small', { textContent: folder.path }));
      if (ready && !(device.tools || []).length) launch.append(el('small', { textContent: '电脑上没有找到 claude、codex 或 PowerShell。' }));
    } else if (ready) launch.append(el('small', { textContent: '还没有项目。点“＋ 项目”添加电脑上的一个文件夹。' }));

    const mine = (data.terminals || []).filter(t => t.dir === project && (t.state === 'running' || t.state === 'starting'));
    const sessions = (data.sessions || []).filter(s => s.dir === project && !s.terminal);
    const live = sessions.filter(s => s.live), saved = sessions.filter(s => !s.live).sort((a, b) => b.updated - a.updated).slice(0, 40);
    fill('phone', mine, t => [t.title, TOOLS[t.tool] + ' · ' + (t.state === 'starting' ? '正在启动' : t.status === 'busy' ? '正在执行' : '等待输入'), t.status === 'busy'],
      t => open(t.id), t => ({ label: '结束', act: async () => { if (await ask('结束这个终端？', '对话会保存在电脑上，可以从历史对话继续。', [{ label: '结束', value: true, kind: 'danger' }])) { await run({ action: 'close', terminal: t.id }, '正在结束…'); refresh(); } } }));
    fill('live', live, s => [s.title, TOOLS[s.tool] + ' · ' + (s.status === 'busy' ? '正在执行' : '电脑上打开着') + ' · ' + ago(s.updated), s.status === 'busy'], takeOver);
    fill('history', saved, s => [s.title, TOOLS[s.tool] + ' · ' + ago(s.updated), false], s => ready && start(s.tool, s.id, false));
    $('empty').hidden = !project || mine.length + live.length + saved.length > 0;
  }
  function fill(id, items, describe, act, extra) {
    const section = $(id), list = section.querySelector('ul');
    section.hidden = !items.length;
    list.textContent = '';
    items.forEach(item => {
      const [title, about, working] = describe(item);
      const row = el('li', null, el('button', { type: 'button', className: 'open', onclick: () => act(item) },
        el('b', { textContent: title }), el('small', { className: working ? 'busy' : '', textContent: about })));
      if (extra) { const more = extra(item); row.append(el('button', { type: 'button', className: 'end', textContent: more.label, onclick: more.act })); }
      list.append(row);
    });
  }
  async function takeOver(session) {
    if (!(data.device.online && data.device.enabled)) return;
    const choices = session.tool === 'claude'
      ? [{ label: '接手，并关闭电脑上的窗口', value: 'close', kind: 'solid' }, { label: '只在手机上打开', value: 'keep' }]
      : [{ label: '在手机上打开', value: 'keep', kind: 'solid' }];
    const choice = await ask('接手这个对话', session.tool === 'claude'
      ? '同一个对话同时在电脑和手机上输入会互相覆盖，建议关闭电脑上的窗口。'
      : '电脑上的 Codex 窗口不会被关闭；请不要两边同时输入。', choices);
    if (choice) start(session.tool, session.id, choice === 'close');
  }
  async function addProject() {
    const candidates = (data.device.candidates || []).slice(0, 4).map(c => ({ label: '添加 ' + c.path, value: c.path }));
    const answer = await ask('添加项目', '填写电脑上的完整文件夹路径；下面是电脑上有对话记录的文件夹。',
      [{ label: '添加填写的路径', value: '', kind: 'solid' }].concat(candidates),
      [{ key: 'path', label: '文件夹路径', placeholder: 'D:\\projects\\demo' }, { key: 'name', label: '名称（可不填）', max: 40 }]);
    if (!answer) return;
    const path = answer.choice || answer.path;
    if (!path) { say('请填写文件夹路径'); return; }
    if (await run({ action: 'project_add', path, name: answer.choice ? '' : answer.name, create: false }, '正在添加…')) refresh();
  }

  async function refresh() {
    clearTimeout(timer);
    try { data = await api('/api/terminal'); draw(); } catch (error) { if (!$('home').hidden) $('device-text').textContent = error.message; }
    if (!$('home').hidden) timer = setTimeout(refresh, document.hidden ? 15000 : 3000);
  }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && !$('home').hidden) refresh(); });

  // Inside the app: leaving goes back to the screen for choosing a computer.
  if (window.RemoteCliNative) { $('logout').textContent = '换电脑'; $('logout').addEventListener('click', () => setTimeout(() => window.RemoteCliNative.disconnect(), 300)); }
  (async () => {
    try {
      // The app hands the password over once, after the "#"; it never travels in a request line.
      const given = new URLSearchParams(location.hash.slice(1)).get('p');
      if (given) {
        history.replaceState(null, '', location.pathname);
        try { await api('/api/login', { password: given }); } catch (error) { showLogin(); $('login-error').textContent = error.message; return; }
      }
      const session = await (await fetch('/api/session', { credentials: 'same-origin' })).json();
      if (!session.signed_in) return showLogin();
      $('home').hidden = false;
      refresh();
    } catch (error) { showLogin(); $('login-error').textContent = '连不上服务，请检查地址'; }
  })();
})();
