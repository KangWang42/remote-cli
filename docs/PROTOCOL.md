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
The code shown on the computer is `remotecli://connect?u=<address>&p=<password>`.

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

### Output

- `GET /api/terminal?terminal=<id>&after=<seq>&wait=<seconds>`: output after sequence number `after`. With
  `wait` (at most 25) the answer is held until there is something new.
- `GET /api/terminal/stream?terminal=<id>&after=<seq>`: one response that stays open (`text/event-stream`).
  Each line `data: {...}` is one piece; a line without output is sent when the state changes and every 15
  seconds. The relay ends the response after about 55 seconds; ask again with the latest `after`.

Both give

```json
{"device": {...}, "terminal": {...}, "chunks": [{"seq": 13, "data": "..."}], "reset": false, "after": 13}
```

`data` is what the program wrote to its terminal, escape sequences included. Sequence numbers are consecutive.
`reset: true` means earlier output is no longer kept: clear the screen and continue from what is returned.
One answer carries at most about 180,000 characters; `after < terminal.seq` means more is waiting.

### Operations

`POST /api/terminal` `{"action": "...", "id": "<16 to 32 hex digits>", ...}` → `{"id", "terminal", "state", "error"}`.
`id` is chosen by the caller; sending the same operation again returns its current state instead of doing it
twice. `state` is `queued` until the computer has done it, then `done` or `error`.

| `action` | Fields |
| --- | --- |
| `start` | `tool` (`claude`, `codex`, `shell`), `dir` (a workspace name); optional `session` (continue that conversation), `takeover` (end the program that has it open on the computer first), `history` (let the tool show its own list) |
| `input` | `terminal`, `data` (at most 16,000 characters) |
| `resize` | `terminal`, `cols` (20–240), `rows` (6–100) |
| `rename` | `terminal`, `title` |
| `close` | `terminal` |
| `project_add` | `path`, optional `name`, `create` |
| `project_rename` | `name`, `to` |
| `project_remove` | `name` |

## Computer

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

The relay keeps `terminals.json` (the terminals and their recent output, up to about 2,000,000 characters per
running terminal), `sessions.json` (hashes of sign-in tokens) and, when it made the password itself,
`password.txt`, all in its data folder with owner-only permissions where the system supports them. It writes
no terminal input or output to any log.
