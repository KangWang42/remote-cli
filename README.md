# Remote CLI

在手机上使用电脑里的终端、Claude Code 和 Codex。电脑装一个程序，手机装一个 App，扫码连上。

*Use your computer's terminal, Claude Code and Codex from your phone. One program on the computer, one app on the phone. The interface is in Chinese for now.*

- 真实终端：电脑上用 ConPTY 运行 `claude`、`codex` 或 PowerShell，手机上看到的就是它的画面，可以输入、按键、翻看、语音输入。
- 离开后继续运行：关掉 App，电脑上的任务不停；回来接着看。
- 接着电脑上的对话：列出项目文件夹里 Claude Code 和 Codex 保存的对话，点开就在手机上继续；电脑上正开着的 Claude Code 对话可以接手并关闭电脑上的窗口。
- 不需要服务器：同一个 Wi-Fi 下直连；或者一键建立 Cloudflare 临时隧道，在外面也能连。有自己服务器的可以部署中转，地址固定。

## 安装

在 [Releases](../../releases) 下载：

| 文件 | 装在哪 |
| --- | --- |
| `RemoteCli-Setup-x.y.z.exe` | Windows 10 / 11（64 位）电脑。装到当前用户目录，不需要管理员权限 |
| `RemoteCli-Android.apk` | 安卓 8.0 及以上的手机 |

安装程序和 APK 没有购买代码签名证书：Windows 会提示“未知发布者”，安卓会提示“未知来源”。介意的话可以按下面“自己构建”从源码生成。

## 使用

1. 电脑上打开 Remote CLI，在“项目文件夹”里添加要用的文件夹。手机只能在这些文件夹里开终端。
2. 选一种连接方式：

   | 方式 | 适合 | 说明 |
   | --- | --- | --- |
   | 局域网直连 | 手机和电脑在同一个 Wi-Fi | 最快。首次启动时 Windows 防火墙会询问，选“允许” |
   | 公网隧道 | 在外面用，没有服务器 | 点“下载隧道程序”，程序从 Cloudflare 的 GitHub 发布页下载 `cloudflared`，建立一个临时的 https 地址。不需要账号；地址每次启动都会变；Cloudflare 不对临时隧道的可用性做保证 |
   | 自有中转 | 有自己的服务器，想要固定地址 | 见下面“自己部署中转” |

3. 手机装好 App，用系统相机扫电脑上的二维码，会跳回 App 并连上。相机不识别时，在 App 里手动输入电脑上显示的地址和密码。
4. 在 App 里选项目，新建 Claude Code、Codex 或 PowerShell 终端，或点开一条已有对话。

终端页：下面一行是 Esc、方向键、Tab、Ctrl+C 等按键；输入框里输入 `/` 会提示常用命令；手指上下拖动翻看之前的内容；话筒是系统语音识别；右上角菜单可以换外观、重命名、结束终端。

关闭电脑上的窗口只是收到托盘；在托盘图标上点右键选“退出”才会停止。取消“允许手机访问”可以立刻挡住手机。

## 安全

这个工具让拿到地址和密码的人在你的电脑上执行命令，请当作 SSH 密码一样对待。

- 密码由程序随机生成，在电脑上用 Windows 的 DPAPI 加密保存；中转只保存登录令牌的哈希。
- 同一地址连续输错 6 次密码会被锁 15 分钟，所有地址合计也有上限。
- 局域网直连走的是不加密的 http，只在可信的网络里用。在外面请用公网隧道或自己的 https 中转。
- 公网隧道的地址是随机的，但能被知道地址的人访问到登录页，保护它的只有密码。
- 手机只能在你添加的项目文件夹里启动终端，但终端启动后和你坐在电脑前一样，可以访问整台电脑。
- 中转和电脑端都不记录终端的输入输出到日志；中转把最近的终端画面保存在它的数据目录里，供手机重连后恢复显示。

发现安全问题请通过 GitHub 的私密漏洞报告提交。

## 组成

```
手机 App  ──HTTP(S)──▶  中转 relay（电脑上自带，或你自己的服务器）  ◀──HTTP(S)──  电脑端程序（运行终端）
```

| 目录 | 内容 |
| --- | --- |
| `agent-windows/` | 电脑端：`RemoteCliApp.cs`（窗口、托盘、连接方式）、`RemoteCliAgent.cs`（终端与对话扫描）、`QrCode.cs`、`Setup.cs`（安装程序）。C#，用 Windows 自带的编译器构建 |
| `relay/` | 中转：`server.py`（登录与 HTTP）、`relay.py`（终端状态与输出的转发）。只用 Python 标准库 |
| `web/` | App 里显示的页面：列表页和终端页（xterm.js）。由中转提供给 App |
| `android/` | 安卓 App：选择电脑、扫码回调、语音识别，其余是上面的页面 |
| `docs/PROTOCOL.md` | 三方之间的接口，想写别的客户端或别的系统的电脑端看这里 |

电脑和手机都只向中转发起请求，电脑不需要公网地址或端口映射。

## 自己部署中转

需要 Python 3.9 及以上，没有其他依赖。

```bash
git clone https://github.com/KangWang42/remote-cli && cd remote-cli
RCLI_PASSWORD='一个足够长的密码' python3 relay/server.py --host 127.0.0.1 --port 8722 --data /var/lib/remote-cli
```

用 nginx、Caddy 等把一个 https 域名反向代理到 `127.0.0.1:8722`。终端输出走的是保持打开的响应，nginx 需要 `proxy_buffering off;` 和不短于 60 秒的 `proxy_read_timeout`。然后在电脑端选“自有中转”，填这个地址，再点“换一个密码”填入同一个密码。

不设 `RCLI_PASSWORD` 时，首次启动会生成一个密码，打印出来并保存在数据目录的 `password.txt`。

没有窗口的电脑端 `RemoteCliAgent.exe` 也在安装目录里，适合只用自有中转、想自己用计划任务启动的情况；它读取 `%LOCALAPPDATA%\RemoteCli\config.json`，密码用 `RemoteCliAgent.exe --set-password` 从标准输入写入。

## 自己构建

```powershell
# 电脑端（两个 exe），只需要 Windows 自带的 .NET Framework 4.x
powershell -ExecutionPolicy Bypass -File agent-windows\build.ps1

# 安装程序：会从 python.org 下载官方的嵌入式 Python 并核对哈希
python tools\package_windows.py

# 安卓：需要 JDK 11+ 和 Android SDK（build-tools 36.0.0、platforms/android-36），不需要 Gradle
powershell -ExecutionPolicy Bypass -File android\build.ps1 -Jdk <JDK 目录> -Sdk <SDK 目录>

# 测试
python -m unittest discover -s relay -p "test_*.py"
python tests\e2e_windows.py
```

## 已知限制

- 电脑端目前只有 Windows。macOS 和 Linux 的电脑端还没有写；接口在 `docs/PROTOCOL.md`。
- 没有 iOS App。
- 界面只有中文。
- 接手电脑上正开着的对话并关闭电脑端窗口，只支持 Claude Code；Codex 的对话可以在手机上打开，但电脑上的窗口不会被关。
- 经公网时，按键到回显的延迟主要是网络往返（手机 → 中转 → 电脑 → 中转 → 手机），局域网直连最快。
- 同时最多 8 个终端。

## 许可

MIT，见 `LICENSE`。第三方组件见 `THIRD_PARTY.md`。Claude Code 和 Codex 分别是 Anthropic 和 OpenAI 的产品，本项目只是启动你电脑上已经安装的那一个。
