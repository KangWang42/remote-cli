#!/usr/bin/env bash
# remote-cli on Linux in one command: the relay and the agent of this computer, both as
# services of your own user, then the code for the phone to scan. Run it from a clone:
#
#   git clone https://github.com/KangWang42/remote-cli
#   bash remote-cli/agent-linux/install.sh --dir ~/projects/demo --url https://relay.example.com
#
#   install.sh [--dir PATH[=NAME]]... [--name NAME] [--port N] [--url URL | --tunnel | --lan]
#   install.sh pair         show the address, password and QR again
#   install.sh status       are the services running, is the computer online
#   install.sh uninstall    stop and remove both; --purge also removes settings and the password
#
# How the phone reaches this computer:
#   --url URL   you have a domain that a reverse proxy sends to 127.0.0.1:PORT (see README)
#   --tunnel    a Cloudflare quick tunnel; needs cloudflared installed. Its address changes
#               whenever the tunnel restarts: run "install.sh pair" and scan again
#   --lan       the relay listens on every address and the phone uses http://<this ip>:PORT.
#               Not encrypted: for a home network or a VPN, not for the open internet
# Without one of them: --tunnel when cloudflared is installed, else --lan.
#
# Nothing is installed system-wide and nothing needs root. Running it again updates the
# programs and keeps the password, the projects and the settings.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
share="$HOME/.local/share/remote-cli-agent"
state="$HOME/.local/state/remote-cli-agent"
relay_state="$HOME/.local/state/remote-cli-relay"
units="$HOME/.config/systemd/user"
services=(remote-cli-agent remote-cli-relay remote-cli-tunnel)

say() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }

need_systemd() {
    command -v systemctl >/dev/null || fail "这台电脑没有 systemd，请按 README 的手动步骤运行。"
    systemctl --user show-environment >/dev/null 2>&1 \
        || fail "连不上当前用户的 systemd（通过 su 切换用户时常见）。请直接以这个用户登录后再运行。"
}

# One value of config.json, printed; empty when it is not there.
setting() {
    python3 - "$state/config.json" "$1" <<'PY'
import json, sys
try:
    value = json.load(open(sys.argv[1], encoding="utf-8")).get(sys.argv[2], "")
except (OSError, ValueError):
    value = ""
print(value if isinstance(value, (str, int)) and not isinstance(value, bool) else "")
PY
}

# The newest address cloudflared announced, waited for up to 40 seconds.
tunnel_address() {
    local found=""
    for _ in $(seq 40); do
        found="$(journalctl --user -u remote-cli-tunnel --no-pager -o cat -n 200 2>/dev/null \
            | grep -o 'https://[a-z0-9-]*\.trycloudflare\.com' | tail -1 || true)"
        [ -n "$found" ] && break
        sleep 1
    done
    printf '%s' "$found"
}

set_pair_url() {
    python3 - "$state/config.json" "$1" <<'PY'
import json, os, sys
path = sys.argv[1]
cfg = json.load(open(path, encoding="utf-8"))
cfg["PairUrl"] = sys.argv[2]
with open(path + ".tmp", "w", encoding="utf-8") as stream:
    json.dump(cfg, stream, ensure_ascii=False, indent=1)
os.replace(path + ".tmp", path)
PY
}

pair() {
    [ -f "$share/agent.py" ] || fail "还没有安装，请先运行 install.sh。"
    if systemctl --user is-active --quiet remote-cli-tunnel 2>/dev/null; then
        local address; address="$(tunnel_address)"
        [ -n "$address" ] || fail "公网通道还没有给出地址，请看 journalctl --user -u remote-cli-tunnel"
        set_pair_url "$address"
    fi
    python3 "$share/agent.py" --pair --data "$state"
}

status() {
    need_systemd
    for unit in "${services[@]}"; do
        [ -f "$units/$unit.service" ] && say "$unit: $(systemctl --user is-active "$unit" 2>/dev/null || true)"
    done
    [ -f "$state/config.json" ] || { say "还没有安装。"; return; }
    python3 - "$(setting Server)" "$state/password.txt" <<'PY'
import json, sys, urllib.request
def call(path, data=None, token=""):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    body = json.dumps(data).encode() if data is not None else None
    with urllib.request.urlopen(urllib.request.Request(sys.argv[1] + path, data=body, headers=headers), timeout=10) as answer:
        return json.load(answer)
try:
    token = call("/api/login", {"password": open(sys.argv[2], encoding="utf-8").read().strip()})["token"]
    device = call("/api/terminal", token=token)["device"]
    print("电脑：%s，可用工具：%s，项目：%s" % ("在线" if device.get("online") else "不在线",
          "、".join(device.get("tools") or []) or "无", "、".join(device.get("workspaces") or []) or "无"))
except Exception as error:
    print("问不到中转：%s" % error)
PY
}

uninstall() {
    need_systemd
    for unit in "${services[@]}"; do
        systemctl --user disable --now "$unit" >/dev/null 2>&1 || true
        rm -f "$units/$unit.service"
    done
    systemctl --user daemon-reload
    rm -rf "$share"
    if [ "${1:-}" = "--purge" ]; then
        rm -rf "$state" "$relay_state"
        say "已卸载，设置、密码和终端记录也已删除。"
    else
        say "已卸载。设置和密码留在 $state 和 $relay_state，再次安装会沿用；加 --purge 一并删除。"
    fi
}

case "${1:-}" in
    pair) need_systemd; pair; exit ;;
    status) status; exit ;;
    uninstall) uninstall "${2:-}"; exit ;;
    -h|--help) sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit ;;
esac

dirs=(); name=""; port=""; mode=""; url=""
while [ $# -gt 0 ]; do
    case "$1" in
        --dir) [ $# -ge 2 ] || fail "--dir 后面要跟文件夹"; dirs+=("$2"); shift 2 ;;
        --name) [ $# -ge 2 ] || fail "--name 后面要跟名称"; name="$2"; shift 2 ;;
        --port) [ $# -ge 2 ] || fail "--port 后面要跟端口"; port="$2"; shift 2 ;;
        --url) [ $# -ge 2 ] || fail "--url 后面要跟地址"; mode="url"; url="${2%/}"; shift 2 ;;
        --tunnel) mode="tunnel"; shift ;;
        --lan) mode="lan"; shift ;;
        *) fail "不认识的参数：$1（--help 查看用法）" ;;
    esac
done

need_systemd
command -v python3 >/dev/null || fail "需要 Python 3.9 及以上，这台电脑没有 python3。"
python3 -c 'import sys; sys.exit(sys.version_info < (3, 9))' \
    || fail "需要 Python 3.9 及以上，这里是 $(python3 -c 'import platform; print(platform.python_version())')。"
[ -f "$repo/relay/server.py" ] && [ -d "$repo/web" ] \
    || fail "请在完整的仓库里运行：没有找到 $repo/relay 和 $repo/web。"

first="yes"; [ -f "$state/config.json" ] && first=""
[ -n "$port" ] || port="$(setting Server | sed -n 's#^http://127\.0\.0\.1:\([0-9]*\)$#\1#p')"
[ -n "$port" ] || port=8722
case "$port" in ''|*[!0-9]*) fail "端口无效：$port" ;; esac
if [ -z "$mode" ] && [ -z "$first" ]; then
    mode="keep"                         # run again without a choice: the way it was set up stays
elif [ -z "$mode" ]; then
    if command -v cloudflared >/dev/null; then mode="tunnel"; else mode="lan"; fi
fi
[ "$mode" != "tunnel" ] || command -v cloudflared >/dev/null \
    || fail "--tunnel 需要先安装 cloudflared（https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/）。"
case "$url" in ""|http://*|https://*) ;; *) fail "--url 要以 http:// 或 https:// 开头" ;; esac

# ---- the programs
mkdir -p "$share" "$units"
mkdir -p -m 700 "$state" "$relay_state"
rm -rf "$share/relay" "$share/web"
cp "$here/agent.py" "$share/agent.py"
cp -r "$repo/relay" "$repo/web" "$share/"
find "$share" -name '__pycache__' -prune -exec rm -rf {} +

# ---- the password: made once, kept in files only this user can read, never printed here
if [ ! -s "$state/password.txt" ]; then
    python3 -c 'import secrets; print(secrets.token_urlsafe(18))' \
        | python3 "$share/agent.py" --set-password "$state" >/dev/null
fi
( umask 077; printf 'RCLI_PASSWORD=%s\n' "$(cat "$state/password.txt")" > "$relay_state/env" )

# ---- settings: made on the first run, afterwards only what was asked for is changed
python3 - "$state/config.json" "$port" "$name" "$mode" "$url" "${dirs[@]+"${dirs[@]}"}" <<'PY'
import json, os, socket, sys
path, port, name, mode, url, dirs = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], sys.argv[6:]
try:
    cfg = json.load(open(path, encoding="utf-8"))
except (OSError, ValueError):
    cfg = {"RemoteEnabled": True, "RemoteDirs": []}
cfg["Server"] = "http://127.0.0.1:" + port
if name or not cfg.get("Name"):
    cfg["Name"] = name or socket.gethostname()
listed = [d for d in cfg.get("RemoteDirs", []) if isinstance(d, str)]
for item in dirs:
    folder, _, label = item.partition("=")
    folder = os.path.abspath(os.path.expanduser(folder))
    if not os.path.isdir(folder):
        sys.exit("文件夹不存在：" + folder)
    label = label or os.path.basename(folder) or folder
    if not any(d.partition("=")[2] == folder for d in listed):
        listed.append("%s=%s" % (label, folder))
cfg["RemoteDirs"] = listed
if mode == "url":
    cfg["PairUrl"] = url
elif mode == "lan":
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("192.0.2.1", 9))          # no packet is sent: this only asks which address would be used
        address = probe.getsockname()[0]
    except OSError:
        address = "127.0.0.1"
    finally:
        probe.close()
    cfg["PairUrl"] = "http://%s:%s" % (address, port)
with open(path + ".tmp", "w", encoding="utf-8") as stream:
    json.dump(cfg, stream, ensure_ascii=False, indent=1)
os.replace(path + ".tmp", path)
PY
chmod 600 "$state/config.json"

# ---- services
listen="127.0.0.1"
if [ "$mode" = "lan" ] || { [ "$mode" = "keep" ] && grep -q -- '--host 0.0.0.0' "$units/remote-cli-relay.service" 2>/dev/null; }; then
    listen="0.0.0.0"
fi
cat > "$units/remote-cli-relay.service" <<EOF
[Unit]
Description=remote-cli relay (what the phone and this computer's agent both connect to)
After=network-online.target
Wants=network-online.target

[Service]
EnvironmentFile=%h/.local/state/remote-cli-relay/env
ExecStart=/usr/bin/env python3 %h/.local/share/remote-cli-agent/relay/server.py --host $listen --port $port --data %h/.local/state/remote-cli-relay --web %h/.local/share/remote-cli-agent/web
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
EOF
cp "$here/remote-cli-agent.service" "$units/remote-cli-agent.service"
if [ "$mode" = "tunnel" ]; then
    cat > "$units/remote-cli-tunnel.service" <<EOF
[Unit]
Description=remote-cli public address (Cloudflare quick tunnel to the relay)
After=network-online.target remote-cli-relay.service
Wants=network-online.target

[Service]
ExecStart=$(command -v cloudflared) tunnel --no-autoupdate --url http://127.0.0.1:$port
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
EOF
elif [ "$mode" != "keep" ]; then
    systemctl --user disable --now remote-cli-tunnel >/dev/null 2>&1 || true
    rm -f "$units/remote-cli-tunnel.service"
fi

systemctl --user daemon-reload
systemctl --user enable --quiet remote-cli-relay remote-cli-agent
systemctl --user restart remote-cli-relay
[ -f "$units/remote-cli-tunnel.service" ] && { systemctl --user enable --quiet remote-cli-tunnel; systemctl --user restart remote-cli-tunnel; }
# the agent signs in at once when the relay already answers
python3 - "$port" <<'PY' || true
import socket, sys, time
for _ in range(50):
    try:
        socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=1).close()
        break
    except OSError:
        time.sleep(0.2)
PY
systemctl --user restart remote-cli-agent
sleep 2
for unit in remote-cli-relay remote-cli-agent; do
    systemctl --user is-active --quiet "$unit" \
        || fail "$unit 没有启动成功，原因见：journalctl --user -u $unit -n 30（端口 $port 被占用时换一个 --port）"
done

if ! loginctl enable-linger "$USER" >/dev/null 2>&1; then
    say "提示：要在没有人登录时也保持运行，请执行一次：sudo loginctl enable-linger $USER"
fi

say "已安装并启动。手机上没有项目可选时，用 --dir 再运行一次，或在 App 里添加项目。"
[ "$listen" = "0.0.0.0" ] && say "注意：现在是未加密的直连（http），只适合家里的网络或 VPN；防火墙需要放行 TCP $port。"
pair
