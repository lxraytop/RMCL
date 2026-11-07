# 宝塔面板部署（Friend Gateway v2）

适用：宝塔 Linux 面板 8.x/9.x + Nginx，仓库 <https://github.com/lxraytop/RMCL>。

> 只部署 `server-v2/`，不要把整个仓库当网站根目录。
> 网关是常驻进程（FastAPI + WebSocket），不是 PHP 站点，**不能用「PHP 项目」方式跑**。

## 0. 前提

| 项 | 要求 |
| --- | --- |
| Python | **3.11 或 3.12**（代码用了 `str \| None` 等 3.10+ 语法，3.8/3.9 会语法错误） |
| 端口 | 对外只开 80 / 443；8099 只监听 127.0.0.1 |
| 域名 | 例如 `im.mc.lxray.top`，A 记录指向服务器 IP |
| 宝塔插件 | Nginx、Python 项目管理器（软件商店里叫「Python项目管理器」或「Python项目」） |

## 1. 拉代码

宝塔「终端」里执行：

```bash
mkdir -p /www/wwwroot && cd /www/wwwroot
git clone -b refactor/friend-im-v2 https://github.com/lxraytop/RMCL.git rmcl-friend
cd rmcl-friend/server-v2
```

- 分支**必须**是 `refactor/friend-im-v2`，`main` 上还是旧代码。
- 仓库是私有的就先在 GitHub 生成 token，用 `https://<token>@github.com/lxraytop/RMCL.git`。

## 2. 建 Python 项目

宝塔 → 软件商店 → Python 项目管理器 → **添加项目**：

| 字段 | 填什么 |
| --- | --- |
| 项目名称 | 随意，例如 `RMCLIM` |
| Python 环境 | 选一个 **3.11/3.12** 的环境（没有就点「环境管理」新建） |
| 启动方式 | **命令行启动**（不要选 uwsgi/gunicorn） |
| 项目路径 | `/www/wwwroot/rmcl-friend/server-v2` |
| 启动命令 | 见下方，**必须写完整命令，不能只写 `app.main:app`** |
| 环境变量 | 无 |
| 启动用户 | `www` |
| 开机启动 | 勾选 |

启动命令（把 `/www/server/pyporject_evn/IM/bin/python` 换成你的环境实际路径）：

```bash
/www/server/pyporject_evn/IM/bin/python -m uvicorn app.main:app --host 127.0.0.1 --port 8099 --workers 1
```

> 找不到环境路径时，在「环境管理」里看，或终端执行 `ls /www/server/pyporject_evn/`。
> 也可以用项目自带的虚拟环境（更稳，见 2.1）。

### 2.1 安装依赖（必须做）

面板不会自动装 `requirements.txt`。终端执行：

```bash
cd /www/wwwroot/rmcl-friend/server-v2
# 方式 A：用面板的 Python 环境
/www/server/pyporject_evn/IM/bin/pip install -r requirements.txt

# 方式 B：项目内自建虚拟环境（然后把启动命令改成 .venv/bin/python）
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
```

装完 `.../bin/python -c "import fastapi, uvicorn, httpx"` 不报错即可。

保存后项目应显示「运行中」。若启动失败，先看项目日志，再对照第 9 节排查。

> **必须单进程**：WebSocket 连接表在进程内存里。不要用 `--workers 4` 或多进程 gunicorn，
> 否则同一个人可能连到不同进程，推送会丢。项目设置里 workers 保持 1。

## 3. 配置

```bash
cd /www/wwwroot/rmcl-friend/server-v2
cp config.example.json config.json
```

编辑 `config.json`：

```json
{
  "db": "data/friend.db",
  "token_ttl_days": 30,
  "debug_code": false,
  "event_retention_days": 7,
  "require_invite": true,
  "tim": {
    "enabled": true,
    "sdkappid": 1600171310,
    "key_file": "/www/wwwroot/rmcl-friend/server/data/tim.key",
    "admin": "administrator",
    "group_type": "Private"
  }
}
```

说明：

- `debug_code` 上线**必须** `false`，否则验证码会直接回传给客户端。
- `tim.key_file` 指向腾讯云 IM 密钥文件。没有这个文件时把 `tim.enabled` 设为 `false`，消息仍能收发，只是不走 IM 多端推送。
- 邮件配置单独放 `config.smtp.json`（见下），不要写进 `config.json`，避免误传。

`config.smtp.json`（同目录，**不要提交到仓库**）：

```json
{
  "host": "smtp.qq.com",
  "port": 465,
  "encryption": "ssl",
  "user": "rayedu-reply@foxmail.com",
  "pass": "邮箱授权码",
  "from": "rayedu-reply@foxmail.com",
  "from_name": "RaySkin"
}
```

没有这个文件时，注册验证码只写服务器日志，线上用户收不到。

## 4. 初始化数据库与邀请码

宝塔 Python 项目的虚拟环境一般在 `/www/server/pyporject_evn/<项目名>_venv/bin/python`，
也可以直接在「终端」里用系统 python（`sqlite3` 是标准库，不依赖第三方包）：

```bash
cd /www/wwwroot/rmcl-friend/server-v2
python3 manage.py init
python3 manage.py invite 5 1      # 生成 5 个、每个可用 1 次的邀请码
```

输出形如 `RMCL-XXXX-XXXX`，把它发给要注册的人。

## 5. 目录权限

SQLite 需要写 `data/` 目录：

```bash
chown -R www:www /www/wwwroot/rmcl-friend/server-v2/data
chmod 750 /www/wwwroot/rmcl-friend/server-v2/data
```

## 6. 绑定域名 + HTTPS

宝塔 → 网站 → 添加站点：

- 域名：`im.mc.lxray.top`
- 根目录：随便建一个空目录即可（例如 `/www/wwwroot/im.mc.lxray.top`），**不要**指向 `server-v2`
- PHP 版本：纯静态
- 数据库：不创建

站点建好后 → 设置 → SSL → Let's Encrypt → 申请 → 开启「强制 HTTPS」。

## 7. 反向代理（关键一步）

站点设置 → 反向代理 → 添加反向代理：

| 字段 | 值 |
| --- | --- |
| 代理名称 | `friend` |
| 目标 URL | `http://127.0.0.1:8099` |
| 发送域名 | `$host` |

保存后**必须**给代理加 WebSocket 头，否则握手会 400/断开。找到宝塔生成的代理配置文件：

```
/www/server/panel/vhost/nginx/proxy/im.mc.lxray.top/*.conf
```

把内容改成（或参照 `deploy/nginx-websocket.conf`）：

```nginx
location / {
    proxy_pass http://127.0.0.1:8099;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;

    # WebSocket 必需
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 300s;
    proxy_send_timeout 300s;
    proxy_buffering off;
}
```

改完在宝塔「网站 → 设置 → 配置文件」里点保存（或 `nginx -t && nginx -s reload`）。
**不要**在面板里再点一次「保存反向代理」，有些版本会覆盖手改的内容。

## 8. 验证

```bash
# 1. 本机直连
curl http://127.0.0.1:8099/api/v1/health

# 2. 走域名 + HTTPS
curl https://im.mc.lxray.top/api/v1/health
```

期望返回：

```json
{"ok":true,"tim":{"enabled":true,"has_key":true},"mail":{"enabled":true,"host":"smtp.qq.com"},"ws_connections":0}
```

再验证 WebSocket（浏览器控制台，任意 https 页面）：

```js
const ws = new WebSocket("wss://im.mc.lxray.top/ws?token=test");
ws.onmessage = e => console.log(e.data);
ws.onopen = () => console.log("open");
// 期望先收到 {"op":"hello"...}，再收到 {"op":"error","code":"E_AUTH"}（token 无效是正常的）
ws.onerror = e => console.log("error", e);
```

收到 `hello` 或 `E_AUTH` 都说明 WS 通了；如果 `onerror` 且 nginx 日志有 400，就是第 7 步的头没加。

## 9. 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| 502 Bad Gateway | 项目没跑起来。看 Python 项目日志；或 `curl 127.0.0.1:8099` 是否通 |
| 启动报语法错误 `invalid syntax` | Python 版本太低，换 3.11/3.12 |
| WS 连上就断 / 400 | 反向代理缺 `Upgrade`/`Connection` 头，见第 7 步 |
| 登录 500 | `config.json` 不存在或 JSON 格式错；`tim.key_file` 路径不对 |
| `database is locked` | 开了多 worker，改回单进程 |
| 注册提示「服务端还没有配置邮件服务」 | 缺 `config.smtp.json`，或 `from`/`host` 没填 |
| 验证码收不到 | QQ 邮箱需用**授权码**不是登录密码；看项目日志里的 `SMTP 发送失败` |
| 邀请码无效 | 没执行 `manage.py invite`，或该码已用完 |
| 修改代码不生效 | 在 Python 项目管理器里点「重启」 |

## 10. 更新版本

```bash
cd /www/wwwroot/rmcl-friend
git pull
cd server-v2
# 依赖没变就不用重装；有变化在 Python 项目管理器里点「安装依赖」
```

然后面板里重启项目。

> 数据库结构变更：首次部署无影响。以后如果 `schema.sql` 加字段，需要手动 `ALTER TABLE`
> 或删掉 `data/friend.db` 重建（会丢历史消息）。

## 11. 客户端指向

部署完成后，把客户端里的网关地址改成你的域名，再重新编译：

```vb
' RMCL/Modules/ModFriend.vb
Public Const DefaultFriendServer As String = "https://im.mc.lxray.top"
```

## 12. 备份与安全

- 备份：`data/friend.db`（含账号、好友、消息）定期打包；`config.json`、`config.smtp.json` 一并备份。
- `server-v2` **不要**放进网站根目录，它没有 `.htaccess` 保护，源码和配置会被直接下载。
- 防火墙只放 80/443；确认 `8099` 没有对外映射。
- `config.smtp.json`、`config.json`、`data/` 都已加入 `.gitignore`，不要手动 `git add -f`。
