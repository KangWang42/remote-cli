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
assert.match(app, /手机不能替你关掉它。请先在那个应用里结束这段对话/,
  'a conversation held by an app needs to say what to do on the computer');
assert.match(app, /api\('\/api\/conversation'/, 'a conversation open on the computer is looked at before anything is done to it');
assert.doesNotMatch(app, /写入锁|app-server|归属待确认/, 'the page speaks of what happens on the computer, not of locks');
assert.match(page, /<details id="background" class="held-sessions"/);
assert.match(page, /被电脑上其它程序占用的对话/);
console.log('session state checks passed');
