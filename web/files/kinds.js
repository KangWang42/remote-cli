/* What a file is, by its name: how it is shown and which picture it gets in the list. Kept apart from the page so
   that it can be checked without a browser. */
(function (root) {
  'use strict';
  const CODE = {
    js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript', json: 'json', jsonl: 'json', ipynb: 'json',
    py: 'python', pyw: 'python', r: 'r', rmd: 'markdown', qmd: 'markdown', jl: 'julia', do: 'stata', ado: 'stata', sas: 'sas', m: 'matlab',
    c: 'c', h: 'c', cc: 'cpp', cpp: 'cpp', hpp: 'cpp', cs: 'csharp', java: 'java', kt: 'kotlin', kts: 'kotlin', go: 'go', rs: 'rust', swift: 'swift',
    rb: 'ruby', php: 'php', pl: 'perl', lua: 'lua', vb: 'vbnet', sql: 'sql', sh: 'bash', bash: 'bash', zsh: 'bash', ps1: 'powershell', psm1: 'powershell', bat: 'plaintext', cmd: 'plaintext',
    html: 'xml', htm: 'xml', xml: 'xml', svg: 'xml', vue: 'xml', css: 'css', scss: 'scss', less: 'less', yaml: 'yaml', yml: 'yaml', toml: 'ini', ini: 'ini', cfg: 'ini', conf: 'ini', properties: 'ini',
    tex: 'latex', bib: 'latex', diff: 'diff', patch: 'diff', graphql: 'graphql', gradle: 'kotlin', txt: 'plaintext', log: 'plaintext', text: 'plaintext', lock: 'plaintext', env: 'ini'
  };
  // Files without an ending that are text all the same.
  const NAMED = { dockerfile: 'dockerfile', makefile: 'makefile', license: 'plaintext', readme: 'plaintext', notice: 'plaintext', authors: 'plaintext', changelog: 'plaintext',
    '.gitignore': 'plaintext', '.gitattributes': 'plaintext', '.editorconfig': 'ini', '.npmrc': 'ini', '.env': 'ini', '.rprofile': 'r', '.rhistory': 'r', '.bashrc': 'bash', '.zshrc': 'bash' };
  const VIEW = { md: 'markdown', markdown: 'markdown', mdx: 'markdown', docx: 'word', xlsx: 'sheet', xlsm: 'sheet', xls: 'sheet', ods: 'sheet', csv: 'sheet', tsv: 'sheet', pptx: 'slides', pdf: 'pdf',
    png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image', bmp: 'image', ico: 'image', svg: 'image', avif: 'image' };
  const TYPES = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', bmp: 'image/bmp', ico: 'image/x-icon', svg: 'image/svg+xml', avif: 'image/avif', pdf: 'application/pdf' };
  // The largest file that is fetched for each way of showing it; text is read by a person, the others are drawn.
  const LIMIT = { code: 3e6, markdown: 3e6, image: 30e6, pdf: 60e6, word: 40e6, sheet: 30e6, slides: 60e6 };

  function ending(name) { const at = name.lastIndexOf('.'); return at > 0 ? name.slice(at + 1).toLowerCase() : ''; }
  /** { view, language, type, limit } for a file name; view is "" when the file is not one this page knows how to show. */
  function kind(name) {
    const lower = name.toLowerCase(), ext = ending(name);
    let view = VIEW[ext] || '', language = '';
    if (!view && (CODE[ext] || NAMED[lower])) { view = 'code'; language = CODE[ext] || NAMED[lower]; }
    if (view === 'markdown') language = 'markdown';
    return { view, language, ext, type: TYPES[ext] || 'application/octet-stream', limit: LIMIT[view] || 0 };
  }
  /** The picture in the list: one of folder, code, text, markdown, word, sheet, slides, pdf, image, archive, other. */
  function picture(name, folder) {
    if (folder) return 'folder';
    const k = kind(name);
    if (k.view === 'code') return k.language === 'plaintext' ? 'text' : 'code';
    if (k.view) return k.view;
    return ['zip', '7z', 'rar', 'gz', 'tar', 'tgz', 'bz2', 'xz'].includes(k.ext) ? 'archive' : 'other';
  }
  function size(bytes) {
    if (bytes < 1024) return bytes + ' B';
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024, at = 0;
    while (value >= 1024 && at < units.length - 1) { value /= 1024; at++; }
    return (value >= 100 ? Math.round(value) : value.toFixed(1).replace(/\.0$/, '')) + ' ' + units[at];
  }
  /** Folders first, then by name as a person reads it ("2" before "10"), by time or by size. */
  function order(entries, by) {
    const names = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' });
    return entries.slice().sort((a, b) => (b.dir - a.dir) || (by === 'time' ? b.modified - a.modified : by === 'size' ? b.size - a.size : 0) || names.compare(a.name, b.name));
  }
  /** Text from the bytes of a file: UTF-8 and UTF-16 by their marks, and the Chinese Windows encoding when UTF-8 does not fit. */
  function text(bytes) {
    if (bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder('utf-16le').decode(bytes.subarray(2));
    if (bytes[0] === 0xfe && bytes[1] === 0xff) return new TextDecoder('utf-16be').decode(bytes.subarray(2));
    try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    catch (error) { try { return new TextDecoder('gb18030').decode(bytes); } catch (missing) { return new TextDecoder('utf-8').decode(bytes); } }
  }
  /** Whether bytes that came without a known ending look like text a person could read. */
  function readable(bytes) {
    const part = bytes.subarray(0, 4096);
    let odd = 0;
    for (const b of part) { if (b === 0) return false; if (b < 9 || (b > 13 && b < 32 && b !== 27)) odd++; }
    return odd <= part.length / 50;
  }
  const api = { kind, picture, size, order, text, readable };
  if (typeof module === 'object' && module.exports) module.exports = api; else root.RemoteCliKinds = api;
})(typeof window === 'undefined' ? globalThis : window);
