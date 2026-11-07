# Friend Gateway v2

好友模块重构的服务端。FastAPI + WebSocket + SQLite(WAL)，替代原来的 PHP + HTTP 轮询实现。

设计文档见 `docs/FRIEND-V2-PLAN.md`。

## 为什么换栈

原实现的问题不是缺接口，是缺「状态 + 事件」：

- 客户端每次操作后都要重新拉一次列表，界面永远落后网络一个来回；
- 未读、在线、会话预览分别维护，靠轮询对齐，必然出现红点不消失、预览不更新；
- 消息、会话、好友关系在三张表里各自写 DELETE，漏一处就留脏数据。

腾讯云 IM Web SDK 的做法是**内部维护一份 store，变更先落 store 再派发事件**。桌面端没有 Web SDK 可用，
所以这一层由网关承担：REST 写库 + 分配 `seq` + WebSocket 推送，客户端只订阅事件改本地 store。

## 目录

```
server-v2/
├── app/
│   ├── main.py      FastAPI 入口 + WS 协议（hello / resume / ping）
│   ├── api.py       REST 接口（认证 / 好友 / 申请 / 分组 / 黑名单 / 会话 / 消息 / 群）
│   ├── hub.py       连接管理、事件写入、presence 广播
│   ├── tim.py       腾讯云 IM REST（UserSig / 单聊 / 群 / 关系链镜像，可关闭）
│   ├── db.py        SQLite 连接与事务
│   ├── config.py    配置加载
│   └── security.py  密码哈希 / 令牌 / 验证码
├── schema.sql
├── manage.py        init / invite / user
├── tests/smoke.py   端到端冒烟（30 项断言）
└── config.example.json
```

## 本地跑起来

```bash
cd server-v2
pip install -r requirements.txt
cp config.example.json config.json      # 按需改 db / tim / debug_code
python manage.py init
python manage.py invite 3 1             # 可选：生成邀请码
python -m uvicorn app.main:app --host 127.0.0.1 --port 8099
```

另开一个终端：

```bash
python tests/smoke.py
# 期望：结果：30 通过 / 0 失败
```

## 配置

```json
{
  "db": "data/friend.db",
  "token_ttl_days": 30,
  "debug_code": true,              // true 时验证码随接口返回，仅开发环境
  "presence_grace_seconds": 30,    // 断线宽限，避免好友列表闪烁
  "event_retention_days": 7,       // 事件可补发窗口
  "require_invite": true,
  "tim": {
    "enabled": true,
    "sdkappid": 1600171310,
    "key_file": "../server/data/tim.key",   // 密钥文件，不要写进仓库
    "admin": "administrator",
    "group_type": "Private",
    "friendship_mirror": false
  }
}
```

`tim.enabled=false` 时消息仍可正常收发（只走本网关），只是不再通过 IM 做多端/离线推送。

## WebSocket 协议

```
wss://<host>/ws?token=<登录令牌>

← {"op":"hello","user_id":7,"server_seq":10231}
→ {"op":"resume","since":10190}     // 客户端最后收到的 seq
← {"op":"event","seq":10191,"type":"message.new","data":{...},"ts":...}
← {"op":"resumed","seq":10231}      // 或 {"op":"resync","reason":"event_expired"}
→ {"op":"ping"} / ← {"op":"pong"}
```

事件类型：

`friend.request` `friend.accepted` `friend.removed` `friend.updated`
`friend.group.updated` `group.updated` `group.removed`
`message.new` `message.recalled` `conversation.updated` `conversation.removed`
`presence.changed` `block.updated`

## 部署（VPS）

```bash
# 1. 代码
git clone -b refactor/friend-im-v2 <repo> /opt/rmcl-friend
cd /opt/rmcl-friend/server-v2
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
cp config.example.json config.json     # 改 debug_code=false，填 tim.key 路径
.venv/bin/python manage.py init

# 2. systemd
cat >/etc/systemd/system/rmcl-friend.service <<'EOF'
[Unit]
Description=RMCL Friend Gateway
After=network.target

[Service]
WorkingDirectory=/opt/rmcl-friend/server-v2
ExecStart=/opt/rmcl-friend/server-v2/.venv/bin/uvicorn app.main:app --host 127.0.0.1 --port 8099 --workers 1
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF
systemctl enable --now rmcl-friend
```

> **必须单 worker**：presence 与 WS 连接表在进程内存里，多 worker 会各算各的。
> 需要横向扩展时，把 `hub.py` 换成 Redis pub/sub，接口不变。

nginx：

```nginx
server {
    listen 443 ssl http2;
    server_name im.mc.lxray.top;

    location /ws {
        proxy_pass http://127.0.0.1:8099;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 300s;
    }

    location / {
        proxy_pass http://127.0.0.1:8099;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

## 与腾讯云 IM 的分工

| 能力 | 归属 |
| --- | --- |
| 好友关系、分组、黑名单、申请 | 本网关（体验版 20 好友上限，不能托管） |
| 在线状态 | 本网关（TIM 用户状态需旗舰版） |
| 消息落库、历史、未读 | 本网关 |
| 消息投递 / 多端同步 / 离线推送 | TIM REST（失败不影响本地一致性） |
| 群消息通道 | TIM 群（群元数据仍在本地） |
| 关系链镜像 | 可选，`tim.friendship_mirror`，默认关 |

## 冒烟覆盖

注册 → 加好友 → 申请未读/已读 → 同意 → 备注/分组 → WS 实时消息 → 未读与已读 →
`client_msg_id` 幂等 → 撤回 → 断线补发 → 建群/成员可见/踢人/越权拦截 → 删好友后不能发消息。
