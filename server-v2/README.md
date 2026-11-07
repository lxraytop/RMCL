# Friend Gateway v2（Lite）

好友模块重构的服务端。FastAPI + WebSocket + SQLite(WAL)，替代原来的 PHP + HTTP 轮询实现。

**范围**：只做腾讯云 IM 体验版能覆盖的能力 —— 好友列表、加/删好友、好友申请、单聊、群聊、会话、撤回、历史分页。
**不含**：未读红点、在线状态、好友分组、备注、黑名单、关系链镜像、聊天记录漫游。

设计文档见 `docs/FRIEND-V2-PLAN.md`（第 12 节为最终范围）。

## 为什么换栈

原实现的问题不是缺接口，是缺「状态 + 事件」：

- 客户端每次操作后都要重新拉一次列表，界面永远落后网络一个来回；
- 消息、会话、好友关系分三张表各写各的，删除时靠人工补 DELETE，漏一处就留脏数据；
- 断线只能靠轮询「撞」回来，漏了也无从判断。

腾讯云 IM Web SDK 的做法是**内部维护一份 store，变更先落 store 再派发事件**。
桌面端没有 Web SDK 可用，所以这一层由网关承担：REST 写库 + 分配 `seq` + WebSocket 推送，
客户端只订阅事件改本地 store。

## 目录

```
server-v2/
├── app/
│   ├── main.py      FastAPI 入口 + WS 协议（hello / resume / ping）
│   ├── api.py       REST（认证 / 好友 / 申请 / 会话 / 消息 / 群组 / 搜索）
│   ├── hub.py       连接管理 + 事件写入 + 补发水位
│   ├── tim.py       腾讯云 IM REST（UserSig / 单聊 / 群，可关闭）
│   ├── db.py        SQLite 连接与事务
│   ├── config.py    配置加载
│   └── security.py  密码哈希 / 令牌 / 验证码
├── schema.sql       12 张表
├── manage.py        init / invite / user
├── tests/smoke.py   端到端冒烟（31 项断言）
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

另开终端：

```bash
python tests/smoke.py
# 期望：结果：31 通过 / 0 失败
```

## 配置

```json
{
  "db": "data/friend.db",
  "token_ttl_days": 30,
  "debug_code": true,            // true 时验证码随接口返回，仅开发环境
  "event_retention_days": 7,     // 事件可补发窗口
  "require_invite": true,
  "tim": {
    "enabled": true,
    "sdkappid": 1600171310,
    "key_file": "../server/data/tim.key",   // 密钥文件，不要进仓库
    "admin": "administrator",
    "group_type": "Private"
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

事件：

| 事件 | 触发 | 客户端处理 |
| --- | --- | --- |
| `friend.request` | 收到好友申请 | 申请列表插入一条 |
| `friend.accepted` | 对方同意 | 好友列表插入 + 建会话 |
| `friend.request.handled` | 申请被拒绝 | 把发出的申请标记为 rejected |
| `friend.removed` | 被删 / 主动删 | 移除好友条目 |
| `message.new` | 新消息 | 追加消息 + 更新会话摘要 |
| `message.recalled` | 撤回 | 气泡改为「已撤回」 |
| `group.updated` | 建群 / 改名 / 成员变化 | 刷新群条目与会话标题 |
| `group.removed` | 被踢 / 解散 | 移除群与会话 |
| `conversation.removed` | 本机删除会话 | 从列表移除 |

## 部署（VPS）

```bash
git clone -b refactor/friend-im-v2 <repo> /opt/rmcl-friend
cd /opt/rmcl-friend/server-v2
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
cp config.example.json config.json     # debug_code=false，填 tim.key 路径
.venv/bin/python manage.py init

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

> **必须单 worker**：WS 连接表在进程内存里，多 worker 会让同一个人连到不同进程。
> 需要横向扩展时把 `hub.py` 换成 Redis pub/sub，接口不变。

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
| 好友关系、申请 | 本网关（体验版 20 好友上限，无法托管全量） |
| 消息落库、历史分页 | 本网关 |
| 消息投递 / 多端同步 / 离线推送 | TIM REST（失败不影响本地一致性） |
| 群消息通道 | TIM 群（群元数据仍在本地） |
| 未读 / 在线 / 分组 / 黑名单 | 不实现 |

## 冒烟覆盖

注册 → 加好友（申请、发出/收到列表、同意）→ 好友列表平铺 → WS 实时消息 →
会话摘要更新 → `client_msg_id` 幂等 → 撤回与会话预览 → 断线补发 →
建群/成员可见/踢人/越权拦截 → 删好友后拒发。
