<h1 align="center">Remote CLI</h1>

<p align="center">
  <b>在手机上使用电脑里的终端、Claude Code 和 Codex。</b><br>
  电脑装一个程序，手机装一个 App，扫码连上。
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/KangWang42/remote-cli?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC&color=7aa2f7" alt="最新版本"></a>
  <img src="https://img.shields.io/badge/%E7%94%B5%E8%84%91-Windows%2010%20%2F%2011%20%C2%B7%20Linux-9ece6a" alt="电脑：Windows 10 / 11、Linux">
  <img src="https://img.shields.io/badge/%E6%89%8B%E6%9C%BA-Android%208.0%2B-9ece6a" alt="手机：Android 8.0 及以上">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/KangWang42/remote-cli?label=%E8%AE%B8%E5%8F%AF&color=8189ad" alt="许可：MIT"></a>
</p>

<p align="center">
  <a href="#下载">下载</a> ·
  <a href="#快速开始">快速开始</a> ·
  <a href="docs/GUIDE.md">使用说明</a> ·
  <a href="docs/LINUX.md">Linux</a> ·
  <a href="docs/FAQ.md">常见问题</a> ·
  <a href="#安全">安全</a>
</p>

<p align="center"><i>Use your computer's terminal, Claude Code and Codex from your phone. One program on the computer, one app on the phone. The interface is in Chinese for now.</i></p>

<p align="center">
  <img src="docs/images/overview.png" alt="一台电脑的首页：等你确认的任务可以直接在卡片上回答" width="31%">
  &nbsp;
  <img src="docs/images/look.png" alt="电脑上打开着的对话：先看内容，再决定要不要在手机上继续" width="31%">
  &nbsp;
  <img src="docs/images/files-word.png" alt="Word 文件带格式的预览" width="31%">
</p>

## 为什么用它

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>一部手机，多台电脑</h3>
      办公室、家里、实验室的电脑各扫一次码，App 全部记住。工作台把它们放在一起：每台是否在线、各有几个任务在等你，一屏看完。
    </td>
    <td width="50%" valign="top">
      <h3>一步跳到要处理的任务</h3>
      所有电脑上正在运行的任务排成一列，等你确认的在最前，其次是已完成还没看过的。点一下直接进入终端，也可以就地结束；已结束的终端单独放在“最近使用”里。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>离开也不中断</h3>
      终端运行在电脑上。关掉 App、锁屏、换网络，任务照常执行；再打开 App，接着刚才的画面继续。
    </td>
    <td width="50%" valign="top">
      <h3>不需要服务器</h3>
      同一个 Wi-Fi 下直连；人在外面，一键建立 Cloudflare 临时隧道，不用注册账号；有自己的服务器，可以部署中转获得固定地址。
    </td>
  </tr>
</table>

| 功能 | 说明 |
| --- | --- |
| 真实终端 | 手机上看到的就是电脑上 `claude`、`codex` 或 shell 的画面，可以输入、按键、翻看 |
| 接着电脑上的对话 | 列出 Claude Code 和 Codex 保存的对话，点开就继续；电脑上正开着的，先看内容再决定要不要接到手机上 |
| 手机和电脑共用终端 | 同一个终端在手机和电脑上是同一个画面，谁输入都行，不用来回交接 |
| 状态一目了然 | 等你确认、已完成、正在执行、等待输入各有一种颜色 |
| 终端连接状态 | 显示连接方式、断线重连次数和最近测得的中转延迟 |
| 工作台管理 | 进行中与历史终端分开，直接结束终端，按电脑和项目展开或收起 |
| 不用进终端就能回答 | 程序问“是否允许”时，提问原文和“允许 / 拒绝”直接出现在列表的卡片上 |
| 任务提醒 | 开启后，离开 App 时有任务等你确认或做完，手机会收到通知 |
| 查看文件 | 在手机上翻看项目文件夹：代码、Markdown、笔记本、表格、PDF、图片，也可以保存到手机；Word 和 PowerPoint 带格式显示 |
| 六套外观 | 两套明亮、四套深色，文字大小和行距可调 |
| 软件内更新 | 电脑端和 App 都会检查新版本，下载并校验后安装 |

## 下载

在 [Releases](../../releases/latest) 下载：

| 文件 | 装在哪 |
| --- | --- |
| `RemoteCli-Setup-x.y.z.exe` | Windows 10 / 11（64 位）电脑。装到当前用户目录，不需要管理员权限 |
| `RemoteCli-Android.apk` | 安卓 8.0 及以上的手机 |

Linux 电脑不用下载安装包，见下面的[快速开始](#linux-电脑)。

两个文件都没有购买代码签名证书：Windows 会提示“未知发布者”（点“更多信息 → 仍要运行”），安卓会提示“未知来源”（允许本次安装）。发布页的 `SHA256SUMS.txt` 可以核对文件，也可以[从源码自己构建](docs/DEVELOPMENT.md)。

## 快速开始

### Windows 电脑

1. **安装并打开。** 运行 `RemoteCli-Setup-x.y.z.exe`，装完自动打开主窗口。
2. **添加项目文件夹。** 在“项目”页点“添加文件夹”，或把文件夹拖进窗口。手机只能在这里列出的文件夹里开终端。
3. **选连接方式。** 在“连接”页上方选一种，状态变绿、出现二维码就可以连了。

   | 方式 | 什么时候用 |
   | --- | --- |
   | 局域网直连 | 手机和电脑连着同一个 Wi-Fi，最快 |
   | 公网隧道 | 人在外面，手头没有服务器；不需要注册账号 |
   | 自有中转 | 有自己的服务器：地址固定，配一次以后不用再扫码；手机和电脑都在国内时延迟比隧道低得多。一条命令装好，见[自己部署中转](docs/SELF_HOSTING.md) |

4. **手机扫码。** 安装 `RemoteCli-Android.apk`，点“扫码添加电脑”，对准电脑窗口里的二维码。

<p align="center">
  <img src="docs/images/windows-projects.png" alt="电脑端：项目文件夹" width="48%">
  &nbsp;
  <img src="docs/images/windows.png" alt="电脑端：连接方式和二维码" width="48%">
</p>

### Linux 电脑

```bash
curl -fsSL https://raw.githubusercontent.com/KangWang42/remote-cli/main/agent-linux/install.sh | bash
```

在要被手机使用的那台 Linux 上运行这一行，等它在终端里显示二维码，用手机 App 扫它。不需要域名、账号和 root：公网地址由 Cloudflare 的临时隧道提供，程序装在你自己的用户目录里。需要 Python 3.9 及以上和 systemd。项目文件夹可以连上后在手机里添加。有自己的域名、只在家里的网络使用、卸载和更新，见 [Linux 电脑端](docs/LINUX.md)。

## 手机上的样子

<p align="center">
  <img src="docs/images/project.png" alt="一个项目：新建终端和历史对话" width="23%">
  &nbsp;
  <img src="docs/images/terminal.png" alt="终端页" width="23%">
  &nbsp;
  <img src="docs/images/files-slides.png" alt="PowerPoint 文件的预览" width="23%">
  &nbsp;
  <img src="docs/images/list-dark.png" alt="深色外观下的首页" width="23%">
</p>

App 有三层：**工作台**（所有电脑）→ **一台电脑**（它的项目和任务）→ **终端**。常用的路径是打开 App、在工作台点一个任务、直接进终端。每个页面上的按钮和状态颜色的含义见[使用说明](docs/GUIDE.md)。

## 文档

| 文档 | 内容 |
| --- | --- |
| [使用说明](docs/GUIDE.md) | 安装与连接的细节、工作台、项目、终端页、查看文件、更新、停止和卸载 |
| [Linux 电脑端](docs/LINUX.md) | 安装脚本的参数、设置项、与 Windows 电脑端的差别 |
| [常见问题](docs/FAQ.md) | 连不上、1033 错误、更新失败、电脑上打开着的对话等 |
| [自己部署中转](docs/SELF_HOSTING.md) | 在自己的服务器上安装中转、配置 https（有域名或只有 IP 两种做法）、电脑端和手机怎么接入 |
| [组成、构建和测试](docs/DEVELOPMENT.md) | 目录结构、从源码构建、全部测试命令 |
| [接口](docs/PROTOCOL.md) | 手机、中转、电脑三方之间的协议，想写别的客户端或别的系统的电脑端看这里 |

## 安全

这个工具让拿到地址和密码的人在你的电脑上执行命令，请当作 SSH 密码一样对待。

- 密码由程序随机生成，在电脑上用 Windows 的 DPAPI 加密保存；中转只保存登录令牌的哈希。
- 同一地址连续输错 6 次密码会被锁 15 分钟，所有地址合计也有上限。
- 局域网直连走的是不加密的 http，只在可信的网络里用。在外面请用公网隧道或自己的 https 中转。
- 公网隧道的地址是随机的，但能被知道地址的人访问到登录页，保护它的只有密码。
- 手机只能在你添加的项目文件夹里启动终端，但终端启动后和你坐在电脑前一样，可以访问整台电脑。
- 查看文件只能读你添加的项目文件夹里面的内容；文件夹里指向别处的链接不会被打开。文件内容经中转传给手机，中转不保存。
- 中转和电脑端都不记录终端的输入输出到日志；中转把最近的终端画面保存在它的数据目录里，供手机重连后恢复显示。

发现安全问题请通过 GitHub 的私密漏洞报告提交。

## 已知限制

- 带窗口、自带中转和公网通道的电脑端只有 Windows。Linux 是后台服务形式，功能与 Windows 电脑端有所不同，见 [Linux 电脑端](docs/LINUX.md)。macOS 的电脑端还没有写。
- 没有 iOS App。
- 界面只有中文。
- 任务提醒要在工作台里手动开启，由 App 自己在后台定时查看，不是服务器推送；有些手机会很快停掉后台应用，需要在系统里允许它后台运行。
- 你在电脑上自己开的命令行窗口，手机只能看它的对话内容，不能接着那个窗口用；要在手机上继续，得先结束它。想两边共用，请从手机或电脑端“活动”页新建终端。Codex 的“另开一份继续”依赖支持 `fork` 的 Codex CLI（核对版本 0.161.0）。
- 经公网时，按键到回显的延迟主要是网络往返（手机 → 中转 → 电脑 → 中转 → 手机），局域网直连最快。
- 同时最多 8 个终端。
- 查看文件是只读的，不能在手机上编辑、上传或删除。Word 和 PowerPoint 的预览是手机页面自己排的：不分页，PowerPoint 的母版样式和图表可能走样。旧格式的 doc、ppt 不能预览，但可以保存到手机后用别的应用打开。

## 许可

MIT，见 `LICENSE`。第三方组件见 `THIRD_PARTY.md`。Claude Code 和 Codex 分别是 Anthropic 和 OpenAI 的产品，本项目只是启动你电脑上已经安装的那一个。
