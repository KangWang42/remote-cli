# Third-party components

| Component | Version | License | Where |
| --- | --- | --- | --- |
| xterm.js (`@xterm/xterm`) | 5.5.0 | MIT | `web/terminal/vendor/xterm.js`, `xterm.css` |
| `@xterm/addon-fit` | 0.10.0 | MIT | `web/terminal/vendor/addon-fit.js` |
| `@xterm/addon-webgl` | 0.18.0 | MIT | `web/terminal/vendor/addon-webgl.js` |
| JetBrains Mono (`@fontsource/jetbrains-mono`) | 5.2.5 | OFL-1.1 | `web/terminal/vendor/jetbrains-mono-*.woff2` |
| Python embeddable package | 3.12.10 | PSF-2.0 | inside the Windows installer only (`python/`) |

The license texts of the first four are next to the files; `web/terminal/vendor/sources.json` records where
each file came from and its integrity hash. The Windows installer includes an unmodified subset of the official
embeddable Python from python.org (files the relay never loads are left out); its license is at
https://docs.python.org/3.12/license.html.

`cloudflared` (Apache-2.0) is not included. The Windows program downloads it from Cloudflare's GitHub releases
only when you choose the public-tunnel mode and confirm.

Claude Code and Codex are separate products of Anthropic and OpenAI. This project starts whichever of them is
installed on your computer; it does not include or modify them.
