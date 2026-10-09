# 自己部署中转

[← 回到首页](../README.md)

有自己的服务器、想要一个固定的 https 地址时使用。Windows 电脑端自带中转，一般不需要这一步。

需要 Python 3.9 及以上，没有其他依赖。

```bash
git clone https://github.com/KangWang42/remote-cli && cd remote-cli
RCLI_PASSWORD='一个足够长的密码' python3 relay/server.py --host 127.0.0.1 --port 8722 --data /var/lib/remote-cli
```

用 nginx、Caddy 等把一个 https 域名反向代理到 `127.0.0.1:8722`，然后在电脑端选“自有中转”，填这个地址，再点“换一个密码”填入同一个密码。nginx 的代理位置需要：

- `proxy_http_version 1.1;`、`proxy_set_header Upgrade $http_upgrade;` 和 `proxy_set_header Connection "upgrade";`，让终端优先使用的 WebSocket 升级请求能够通过；
- `proxy_buffering off;` 和不短于 60 秒的 `proxy_read_timeout`，供不支持 WebSocket 时回退到保持打开的 HTTP 响应。

不设 `RCLI_PASSWORD` 时，首次启动会生成一个密码，打印出来并保存在数据目录的 `password.txt`。

没有窗口的电脑端 `RemoteCliAgent.exe` 也在安装目录里，适合只用自有中转、想自己用计划任务启动的情况；它读取 `%LOCALAPPDATA%\RemoteCli\config.json`，密码用 `RemoteCliAgent.exe --set-password` 从标准输入写入。
