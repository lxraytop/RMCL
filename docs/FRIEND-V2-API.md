# 好友网关 v2 接口清单

线上入口：`https://immc.lxray.top`

- 业务接口前缀：`/api/v1`
- 后台管理：`/admin`（网页）+ `/admin/api/*`（JSON，Cookie 会话）
- 实时通道：`/ws?token=<auth_token>`
- 交互式文档：`/docs`（Swagger UI，已改为读取本地静态资源，不再依赖 jsdelivr）
- 机器可读结构：`/openapi.json`

## 认证

除 `/health`、`/auth/*` 外，所有 `/api/v1` 接口都要带：

```
Authorization: Bearer <token>
```

`token` 来自 `/auth/login`、`/auth/register`、`/auth/rayskin` 的返回值，默认 30 天有效。

错误统一为：

```json
{"error": {"code": "E_NOT_FRIEND", "message": "你们还不是好友"}}
```

常见错误码：`E_AUTH` 401、`E_PARAM` 400、`E_NOT_FRIEND` 403、`E_NOT_MEMBER` 403、
`E_NOAUTH` 403、`E_NOT_FOUND` 404、`E_STATE` 400、`E_EXPIRE` 400（撤回超 2 分钟）、
`E_CODE` 400（验证码错）、`E_INVITE` 400（邀请码无效）、`E_MAIL` 500（邮件未配置/发送失败）。

## 账号

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/v1/auth/send-code` | 发验证码。`{email, purpose}`，`debug_code=true` 时直接回 `debug_code` |
| POST | `/api/v1/auth/register` | 注册。`{email, password, nickname, code, invite, device}`，回 `token` + `user` |
| POST | `/api/v1/auth/login` | 密码登录。`{email, password, device}` |
| POST | `/api/v1/auth/rayskin` | RaySkin 一键登录。`{email, uuid, name, server, server_id}` 或 `{..., access_token}` |
| POST | `/api/v1/auth/logout` | 注销当前 token |
| GET | `/api/v1/me` | 自己的资料 + 邀请码用量 + 未读总数 + 待处理申请数 |

## 好友与申请

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/search?q=` | 按邮箱/昵称搜人，每条带 `relation`：`none / requested / incoming / friend` |
| GET | `/api/v1/friends` | 好友列表（平铺 `items`，带 `conv`） |
| GET | `/api/v1/friends/{id}` | 单个好友详情，非好友 404 |
| DELETE | `/api/v1/friends/{id}` | 删好友（双向删除 + 推送 `friend.removed`） |
| GET | `/api/v1/friend-requests?box=in\|out&include_handled=false` | 好友申请。默认只返回 `pending`，`include_handled=true` 可查历史 |
| POST | `/api/v1/friend-requests` | 发申请。`{to_id, wording}`；重复发送返回 `duplicated: true` 不新建 |
| POST | `/api/v1/friend-requests/{id}/accept` | 同意：建双向好友 + 建会话 + 推送 `friend.accepted` |
| POST | `/api/v1/friend-requests/{id}/reject` | 拒绝 |

## 会话与消息

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/conversations` | 会话列表（标题、最后一条、未读、群成员数） |
| POST | `/api/v1/conversations/{conv}/read` | 已读，回 `{ok, unread}`（剩余未读总数） |
| DELETE | `/api/v1/conversations/{conv}` | 从列表隐藏（消息仍保留） |
| GET | `/api/v1/messages?conv=&before=&limit=` | 拉历史，`conv` 形如 `0:12` / `1:45`，`before` 为消息 id 游标 |
| POST | `/api/v1/messages` | 发消息。`{conv, body, client_msg_id}`，`client_msg_id` 用于幂等 |
| POST | `/api/v1/messages/{id}/recall` | 撤回（只能撤自己的、2 分钟内，对方看到 `kind=recalled`） |
| GET | `/api/v1/history?keyword=&scope=&before=&limit=` | 消息检索，`scope` 取值 `all / friend / group` |

> 注意单聊两套键：**消息表**用与视角无关的 `min_max`（如 `12_34`），**会话表**用各自视角的对方 id。
> 客户端拿到消息后要换算一次，否则同一条会话会被当成两个。

## 群组

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/groups` | 我加入的群 |
| POST | `/api/v1/groups` | 建群。`{name, members:[id]}`，成员必须是好友 |
| GET | `/api/v1/groups/{id}` | 群详情 + 成员（含 `role`，1 = 群主） |
| PATCH | `/api/v1/groups/{id}` | 改群名（仅群主） |
| POST | `/api/v1/groups/{id}/members` | 拉人入群（仅群主，且必须是好友） |
| DELETE | `/api/v1/groups/{id}/members/{uid}` | 踢人 / 自己退群 |

## 运维

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/health` | 自检：配置来源、TIM、SMTP、WS 连接数 |
| GET | `/api/v1/snapshot` | 一次性拉全量：`me / friends / conversations / requests / server_seq`（重连兜底） |

## 后台管理 `/admin/api`

先 `POST /admin/api/login {password}` 拿 Cookie，其余接口靠 Cookie 鉴权。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/admin/api/overview` | 用户/群/消息/邀请码总量与最近动态 |
| GET/POST | `/admin/api/invites` | 查 / 生成邀请码 |
| POST | `/admin/api/invites/{code}/toggle` | 启用停用 |
| DELETE | `/admin/api/invites/{code}` | 删除 |
| GET | `/admin/api/users?q=` | 搜用户 |
| PATCH | `/admin/api/users/{uid}` | 改昵称 / 状态 / 重置密码 |
| DELETE | `/admin/api/users/{uid}` | 删用户（级联清好友、会话、消息） |
| GET | `/admin/api/groups` | 群列表 |
| DELETE | `/admin/api/groups/{gid}` | 解散群 |
| GET | `/admin/api/messages?q=&limit=` | 全局搜消息 |
| DELETE | `/admin/api/messages/{mid}` | 删单条消息 |

## WebSocket

握手：`wss://immc.lxray.top/ws?token=<token>`

```
← {"op":"hello","user_id":12,"server_seq":345}
→ {"op":"resume","since":300}      ← 补发 seq>300 的事件 + {"op":"resumed","seq":345}
→ {"op":"ping"}                    ← {"op":"pong"}
← {"op":"event","seq":346,"type":"message.new","data":{...}}
← {"op":"resync","reason":"event_expired"}   事件已被清理，需拉 /snapshot
```

事件类型：`message.new`、`message.recalled`、`conversation.updated`、`conversation.removed`、
`friend.request`、`friend.request.handled`、`friend.accepted`、`friend.removed`、
`group.updated`、`group.removed`。

客户端 25 秒 ping 一次；服务端 90 秒收不到任何帧即断开。重连必须用本地记录的
`last_seq` 发 `resume`，而不是 `hello.server_seq`。

## 关于 `/docs` 白屏

FastAPI 默认的 `/docs` 页面本体只有 900 字节，Swagger UI 的 JS/CSS 从
`https://cdn.jsdelivr.net/...` 加载。国内访问 jsdelivr 基本不通，所以页面虽然返回 200，
但打开是白屏。

现在已把 `swagger-ui-bundle.js` 与 `swagger-ui.css` 放进 `server-v2/static/swagger/`，
`/docs` 改为引用 `/static/swagger/*`，`/redoc` 直接 302 到 `/docs`。
部署时除 `app/` 外要一并上传 `static/` 目录，否则会回落到 CDN（仍然白屏）。
