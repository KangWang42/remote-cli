# Linux 电脑端

[← 回到首页](../README.md)

在 Linux 服务器或台式机上被手机远控。

Linux 上没有带窗口的程序。电脑端是一个 Python 文件，和中转一起作为你自己这个用户的后台服务运行，不需要 root，也不往系统目录里装东西。需要 Python 3.9 及以上和 systemd，没有其他依赖。

```bash
git clone https://github.com/KangWang42/remote-cli
bash remote-cli/agent-linux/install.sh --dir ~/projects/demo --url https://relay.example.com
```

这一条命令会：把程序放到 `~/.local/share/remote-cli-agent`，生成一个密码，把中转和电脑端注册成两个用户服务并启动，最后在终端里显示地址、密码和二维码。手机 App 点“扫码添加电脑”扫它即可。`--dir` 是允许手机使用的项目文件夹，可以写多次，也可以写成 `路径=名称`。

手机怎样连到这台电脑，三选一：

| 参数 | 适合 | 说明 |
| --- | --- | --- |
| `--url https://你的域名` | 有域名的服务器 | 中转只监听本机，你用 nginx、Caddy 等把域名反向代理到 `127.0.0.1:8722`（nginx 需要的几行见 [自己部署中转](SELF_HOSTING.md)） |
| `--tunnel` | 没有域名，想从外面访问 | 用 Cloudflare 的临时公网通道，需要先装好 `cloudflared`。通道每次重启地址都会变，变了以后运行 `install.sh pair` 重新扫码 |
| `--lan` | 家里的网络或 VPN | 手机直接用 `http://这台电脑的地址:8722`。没有加密，不要用在公网上 |

都不写时：装有 `cloudflared` 用 `--tunnel`，否则用 `--lan`。端口用 `--port` 改，手机上显示的名称用 `--name` 改。

```bash
bash remote-cli/agent-linux/install.sh pair              # 再显示一次地址、密码和二维码
bash remote-cli/agent-linux/install.sh status            # 服务是否在运行，电脑是否在线
bash remote-cli/agent-linux/install.sh uninstall         # 停止并移除；加 --purge 连设置和密码一起删除
```

更新：`git pull` 后再运行一次 `install.sh`，密码、项目和设置都会保留。地址和密码只在终端里显示，不写进服务日志；运行情况看 `journalctl --user -u remote-cli-agent`。

设置在 `~/.local/state/remote-cli-agent/config.json`，改动随时生效，不用重启服务：

| 设置 | 含义 |
| --- | --- |
| `Server` | 电脑端连接的中转地址。用安装脚本时是本机的 `http://127.0.0.1:8722` |
| `PairUrl` | 手机连接中转用的地址，和 `Server` 不同时才需要 |
| `RemoteEnabled` | `true` 才接受手机的操作；改成 `false` 会结束所有手机终端 |
| `Name` | 手机上显示的电脑名称，不写用主机名 |
| `RemoteDirs` | 项目文件夹，写成 `名称=完整路径`。终端只能在这些文件夹里启动；手机上添加的项目另存在同一目录的 `terminal-projects.json` |
| `Shell` | 普通终端用的 shell，不写用 `$SHELL` |
| `RemoteMaxMode` | 从手机启动 Claude Code、Codex 时的权限上限：`read` 只读，`edit` 可改文件，不写用它们自己的默认 |

中转在别的机器上时不用安装脚本：把 `agent.py` 和 `remote-cli-agent.service` 按服务文件开头的说明放好，`Server` 填那台中转的地址即可。

和 Windows 电脑端相比：

| | Linux 电脑端 |
| --- | --- |
| 普通终端 | 有，用你的 shell。手机上这个终端的名称目前仍显示为“PowerShell” |
| Claude Code、Codex | 装在这个用户下（`PATH`、`~/.local/bin`、`~/.npm-global/bin`、nvm 等位置）就会出现在手机上；可以新建对话，也可以继续项目里保存的对话 |
| 查看文件、保存到手机 | 有，只给出项目文件夹之内的内容 |
| 接管电脑上正在使用的对话、Codex 副本 | 没有。正在别处使用的对话会标为占用，请先在电脑上结束使用它的程序 |
| 从手机更新电脑端 | 没有，用 `git pull` 后重新复制 `agent.py` 并重启服务 |
| 发现电脑上其它用过的文件夹 | 没有，项目写在 `RemoteDirs` 里或从手机添加 |

## 测试范围

Linux 电脑端在 Ubuntu 24.04、Python 3.12 上测试过：真实的中转和终端，安装脚本的 `--url` 和 `--lan` 两种方式，以及卸载。下面这些没有实际验证过：

- `--tunnel` 方式；
- 装有 Claude Code 或 Codex 的 Linux：它们的启动与对话列表是用替身程序和按真实格式编写的对话文件测试的；
- 用手机真机连接 Linux 电脑端。
