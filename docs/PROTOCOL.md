# Protocol

Three parties: the **viewer** (the phone app's pages), the **relay** (`relay/server.py`) and the **computer**
(`agent-windows/`). The viewer and the computer both make requests to the relay; the relay never connects to
either and never runs a command. Anything that speaks this protocol can replace one of the three: another
client, or a computer-side program for macOS or Linux.

All bodies are JSON in UTF-8. Errors are `{"error": "text for the person"}` with status 400, 401, 403, 429 or 500.

## Signing in

| Request | Result |
| --- | --- |
| `POST /api/login` `{"password": "..."}` | `{"ok": true, "token": "..."}` and a cookie `rcli`. Six wrong passwords from one address, or forty in total, lock sign-in for 15 minutes (429) |
| `GET /api/session` | `{"signed_in": bool, "version": "x.y.z"}` |
| `POST /api/logout` | forgets the token |

Later requests carry the cookie or `Authorization: Bearer <token>`. A token is good for 90 days from its last
use. `POST` requests must have `Content-Type: application/json`; when an `Origin` header is present it must be
the relay itself. The computer signs in the same way as the viewer.

The app passes the password to its page once as `/#p=<password>`; the page signs in and removes it.
The code shown on the computer is `remotecli://connect?u=<address>&p=<password>&n=<computer name>`; the name is optional and lets a phone that knows several computers tell them apart (the same name with a new address replaces the old address).

## Viewer

### Overview

`GET /api/terminal` →

```json
{"device": {"online": true, "enabled": true, "workspaces": ["demo"], "tools": ["claude", "codex", "shell"],
            "projects": [{"name": "demo", "path": "D:\\demo", "fixed": true, "exists": true}],
            "candidates": [{"name": "notes", "path": "D:\\notes", "updated": 1700000000000, "tools": ["claude"]}]},
 "terminals": [{"id": "...", "tool": "shell", "dir": "demo", "title": "PowerShell", "state": "running", "status": "idle", "seq": 12, "cols": 80, "rows": 24, "session": ""}],
 "sessions": [{"id": "<uuid>", "tool": "claude", "dir": "demo", "title": "...", "updated": 1700000000000, "live": false, "status": "", "terminal": ""}]}
```

`workspaces` are the project names a terminal may be started in. `sessions` are conversations the tools saved on
the computer; `live` means a program on the computer has it open, `terminal` is set when one of the relay's
terminals already shows it. `state` is `starting`, `running` or `closed`; `status` is `busy`, `idle` or empty.

Agents may advertise `info.features`: `codex-fork`, `codex-takeover`, and `terminal-exit`. These are forwarded
under `device.features`. Sessions may include `can_takeover`, `ownership_known`, and `takeover_reason`.
An agent verifies Codex's actual writer lock and process ownership before advertising takeover, then checks
again before terminating the selected independent CLI. Shared app servers and phone descendants are protected.
Process identifiers and command lines are never sent to the viewer. A closed terminal may include `exit_code`;
a nonzero value produces a visible error while preserving the output.

Each terminal also says what it is doing:

| `phase` | Meaning |
| --- | --- |
| `starting` | not running yet |
| `busy` | the program is working: it says so itself (Claude Code), or it has written output in the last 4 seconds |
| `confirm` | it has been quiet for 1.5 seconds with a yes/no question at the end of its screen. This is read from the text on the screen and can be wrong |
| `idle` | it waits for the next message; `done: true` when it worked before that |
| `ended`, `failed` | it has ended; `failed` with an exit code other than 0 |

`said` is one line of what a running terminal's program last said or did, read from the screen the relay keeps for it; it is empty for a terminal that is not running and is only part of the list of all terminals.

`phase_at` is when the phase last changed (milliseconds since 1970). A viewer can keep the `phase_at` it last showed for a terminal to tell "finished, not looked at yet" from "waiting".

### Output

Session records carry optional `host`: `cli` is a verified standalone CLI, `shared` is a Codex desktop/editor backend retaining the writer lock, `remote` is a CLI under another terminal host, and `unknown` means the lock is held without verified ownership. `live` only describes a held writer/session; it does not prove that a visible window displays the conversation. Shared/unknown/other-remote sessions appear separately from active CLI terminals. An absent `host` on old agents must not be interpreted as a visible Codex window. Takeover rechecks ownership and refuses other terminal hosts as well as shared backends.

- `GET /api/terminal/ws?terminal=<id>&after=<seq>`: preferred WebSocket transport. Upgrade with
  version 13, a valid `Sec-WebSocket-Key`, the login cookie (or bearer token), and an `Origin` matching
  the relay. Output messages have `t: "out"` plus the same fields below. A state message arrives
  immediately, followed by output as it arrives and idle heartbeats. Ping frames keep an idle connection
  alive. Reconnect from the last `after` if the connection closes; connections renew after about an hour.
- `GET /api/terminal?terminal=<id>&after=<seq>&wait=<seconds>`: output after sequence number `after`. With
  `wait` (at most 25) the answer is held until there is something new.
- `GET /api/terminal/stream?terminal=<id>&after=<seq>`: one response that stays open (`text/event-stream`).
  Each line `data: {...}` is one piece; a line without output is sent when the state changes and every 15
  seconds. The relay ends the response after about 55 seconds; ask again with the latest `after`.

All three give

```json
{"device": {"online": true, "enabled": true}, "terminal": {...}, "chunks": [{"seq": 13, "data": "..."}], "reset": false, "after": 13}
```

`data` is what the program wrote to its terminal, escape sequences included. Sequence numbers are consecutive.
`reset: true` means earlier output is no longer kept: clear the screen and continue from what is returned.
One answer carries at most about 180,000 characters; `after < terminal.seq` means more is waiting.

### Operations

`POST /api/terminal` `{"action": "...", "id": "<16 to 32 hex digits>", ...}` → `{"id", "terminal", "state", "error"}`.
`id` is chosen by the caller; sending the same operation again returns its current state instead of doing it
twice. `state` is `queued` until the computer has done it, then `done` or `error`.

On an upgraded viewer connection, send the same operation object as a text message (`input`, `resize`,
`rename` and `close` only, for the connection's terminal). The relay replies with
`{"t": "ack", "status": 200, "id": "...", "terminal": "...", "state": "queued", "error": ""}`.
Validation errors return an acknowledgment with status 400; storage failures return 500. A queued
acknowledgment means accepted by the relay, not yet executed by the computer. Inputs can be sent in
order without waiting for each acknowledgment. If an acknowledgment is lost, retry the identical
operation ID and payload over either transport. Text messages are limited to 128,000 UTF-8 bytes.
Cross-origin upgrades and operations for another terminal are refused. Logout invalidates input on an
open connection immediately and stops its output on the next update or heartbeat.

The viewer falls back to HTTP if WebSocket is unavailable. Quick tunnels skip SSE because their edge
buffers it. On other hosts, an SSE connection without its first event within four seconds falls back
to held requests. Switching to the background cancels the active reader; returning resumes from `after`.

| `action` | Fields |
| --- | --- |
| `start` | `tool` (`claude`, `codex`, `shell`), `dir` (a workspace name); optional `session` (continue that conversation), `takeover` (end the owning computer CLI first), `fork` (Codex only: new conversation with the source history), `history` (let the tool show its own list) |
| `input` | `terminal`, `data` (at most 16,000 characters) |
| `resize` | `terminal`, `cols` (20–240), `rows` (6–100) |
| `rename` | `terminal`, `title` |
| `close` | `terminal` |
| `project_add` | `path`, optional `name`, `create` |
| `project_rename` | `name`, `to` |
| `project_remove` | `name` |

## Computer

`fork` requires a session UUID, the `codex-fork` capability, and `takeover=false`. The new terminal starts with
an empty session association; the source remains untouched. The agent only associates a new Codex conversation
when its verified writer belongs to that terminal's process tree, never by picking the first conversation in
the same folder. A locked source cannot be resumed concurrently; choose fork or first release its writer.

`POST /api/terminal/agent`, a few times a second while something happens and about once a second otherwise:

```json
{"info": {"instance": "<32 hex, new at each start>", "enabled": true, "tools": ["claude", "shell"], "workspaces": ["demo"],
          "projects": [...], "candidates": [...]},
 "terminals": [{"id": "...", "state": "running", "status": "idle"}],
 "output": [{"terminal": "...", "seq": 13, "data": "..."}],
 "acks": [{"id": "<operation id>", "error": ""}],
 "sessions": [...]}
```

→ `{"operations": [{"id", "terminal", "action", ...}], "output_ack": {"<terminal>": 13}}`

The computer keeps each piece of output until `output_ack` covers its `seq`, and reports each finished
operation in `acks` until the relay stops sending it. It checks every operation itself: the folder must be
one of its projects, a conversation must exist on disk. The relay only refuses malformed requests.

`POST /api/terminal/agent/pull` `{"instance": "...", "wait": 12}` is held by the relay until an operation is
waiting and returns it at once, so a key press does not wait for the next report.

## Storage

The relay keeps `terminals.json` (the list of terminals), `terminals-output/<terminal>.jsonl` (the recent output of
each, one piece per line, up to about 2,000,000 characters for a running terminal; new pieces are added to the
file rather than the file written again), `sessions.json` (hashes of sign-in tokens) and, when it made the password itself,
`password.txt`, all in its data folder with owner-only permissions where the system supports them. It writes
no terminal input or output to any log.
