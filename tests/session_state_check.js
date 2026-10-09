/* Regression checks for the visible-session/retained-lock split. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');
const app = fs.readFileSync(path.join(root, 'web/app.js'), 'utf8');
const page = fs.readFileSync(path.join(root, 'web/index.html'), 'utf8');

assert.match(app, /function sessionActivity\(s\)/);
assert.match(app, /sessions\.filter\(s => sessionActivity\(s\) === 'active'\)/,
  'only explicitly active sessions may enter the activity list');
assert.match(app, /sessions\.filter\(s => sessionActivity\(s\) === 'locked'\)/,
  'retained writer locks must remain available as locked history');
assert.match(app, /Codex 共享 app-server 仍占用写入锁/,
  'shared locks need an actionable explanation before takeover');
assert.match(page, /<details id="background" class="held-sessions"/);
assert.match(page, /历史会话仍被后台锁定/);
console.log('session state checks passed');
