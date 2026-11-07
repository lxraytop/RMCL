# 好友模块重构方案（Friend IM v2）

> 分支：`refactor/friend-im-v2`
> 目标：用腾讯云 IM 官方 Web 示例的「单一数据源 + 事件驱动」模型，替换现有基于 HTTP 轮询、多份真相的伪 IM 实现。
> 本文档先给结论，再给差距、架构、数据模型、接口、阶段计划与验收标准。
>
> **11-07 范围收敛**：按「只做体验版能做到的功能」的要求，去掉在线状态、好友分组、
> 好友关系链（备注/黑名单/镜像）与漫游；**未读红点保留**（用户明确要求）。最终范围见第 12 节。第 1–10 节保留原始分析，
> 用于说明为什么砍掉这些能力，以及将来要加回来时的设计依据。

---

## 0. 结论先行

现有好友模块「形同虚设」不是因为功能少，而是**架构错了**：

| 症状 | 真实原因 |
| --- | --- |
| 会话卡片和聊天页永远差几秒 | 每次都要 `RefreshConversations()` 走一次网络往返；没有本地状态，界面是服务端返回值的投影 |
| 红点有时不消失 | 未读只在服务端维护，客户端靠下一次轮询覆盖 |
| 恢复记录后界面没反应 | 恢复只写磁盘文件，没有改内存状态，也没有事件通知 UI |
| 漫游上报恒为 0 | 「本机缓存」和「云端历史」是两套数据，写入路径还不一致（已在 2.20.2.1 修，但模型仍旧割裂） |
| 删了好友会话还在 | 会话、好友关系、资料分散在三张表，删除时靠人工补 DELETE，漏一处就残留 |
| 群成员变更后另一端不刷新 | 没有推送通道，只能等轮询 |

腾讯云 IM Web SDK 的做法是：**SDK 内部维护一份 store，所有变更先改 store，再派发事件，UI 只订阅事件**。我们缺的就是这一层。

结论：**不在旧代码上打补丁，新建 `server-v2` 网关 + 客户端 `FriendStore`，用事件流驱动两者。**

---

## 1. 腾讯云 IM Web 示例的功能范围

### 1.1 关系链（好友）能力

官方 Web SDK（`tim-js-sdk`）与 TUIKit 的联系人模块提供以下能力：

| 能力 | SDK API | 事件 |
| --- | --- | --- |
| 好友列表（含缓存） | `getFriendList` | `TIM.EVENT.FRIEND_LIST_UPDATED` |
| 好友资料 | `getFriendProfile` | — |
| 校验关系 | `checkFriend` | — |
| 添加好友（验证消息 / 备注 / 分组 / 添加方式 / 单向或双向） | `addFriend({to, source, wording, remark, groupName, type})` | `FRIEND_APPLICATION_LIST_UPDATED`（需验证）或 `FRIEND_LIST_UPDATED`（免验证） |
| 删除好友（单向 / 双向，支持批量） | `deleteFriend({userIDList, type})` | `FRIEND_LIST_UPDATED` |
| 修改好友（备注、分组） | `updateFriend` | `FRIEND_LIST_UPDATED` |
| 好友申请列表（收到 + 发出） | `getFriendApplicationList` | `FRIEND_APPLICATION_LIST_UPDATED` |
| 同意 / 拒绝 / 删除申请 | `acceptFriendApplication` / `refuseFriendApplication` / `deleteFriendApplication` | 同上 |
| 申请已读上报 | `setFriendApplicationRead` | `FRIEND_APPLICATION_LIST_UPDATED` |
| 好友分组（增删改查、成员增删） | `getFriendGroupList` / `createFriendGroup` / `deleteFriendGroup` / `renameFriendGroup` / `addToFriendGroup` / `removeFromFriendGroup` | `FRIEND_GROUP_LIST_UPDATED` |
| 黑名单 | `addToBlacklist` / `removeFromBlacklist` / `getBlacklist` | `BLACKLIST_UPDATED` |

### 1.2 TUIKit 联系人页的界面结构

```
联系人
├── 好友申请        ← 有未处理时红点，进入可「同意 / 拒绝 / 查看来源与验证消息」
├── 我的好友        ← 按好友分组折叠，显示备注名 / 昵称 / 在线状态点
│   └── 点击 → 资料卡：发消息、备注、移动分组、删除好友、加入黑名单
├── 我的群组        ← 我加入的群，点击直接进群会话
└── 新的联系人      ← 可选：推荐 / 最近联系人
```

交互要点（这是「像微信」的关键）：

1. **发消息入口统一**：资料卡 → 发消息，直接创建/复用会话并跳到聊天页，返回时会话列表已经有一条。
2. **申请即红点**：收到申请 → 联系人 tab 红点 + 申请条目红点，点进去即 `setFriendApplicationRead`。
3. **同意后双侧即时**：A 同意 → B 的好友列表立即出现 A（事件推送，不刷新）。
4. **删除是双向确认**：删除好友时同步删会话或保留会话但标记「非好友」。
5. **资料卡是唯一编辑入口**：备注、分组都在资料卡改，改完列表立即变。

### 1.3 套餐限制（必须先确认，直接影响方案）

| 能力 | 体验版（免费） | 专业版 ¥1499/月 | 旗舰版 ¥2999/月 |
| --- | --- | --- | --- |
| 用户数 | 100 | 不限 | 不限 |
| 单个用户好友数 | **20** | 3000 | 3000 |
| 群成员上限 | 20 | 200 | 2000 |
| 漫游消息 | 7 天 | 7 天 | 30 天 |
| **用户状态（在线）** | ✗ | ✗ | **✓** |
| 已读回执 | ✗ | ✗ | ✓ |

当前 SDKAppID `1600171310` 是体验版。这意味着：

- **不能把在线状态交给 TIM**（`getUserStatus` 在体验版/专业版不可用），必须自建 presence。
- **好友关系不能完全托管给 TIM**（20 人上限），只能自建 + 可选镜像。
- 群成员上限 20，超过要升套餐；自建群元数据可先行，TIM 群仅做消息通道。

> 因此方案定为：**好友关系、分组、黑名单、在线状态、会话未读由自建网关持有；TIM 只承担消息投递、多端同步与群消息通道。**
> 这与 TUIKit 的「SDK 持有状态」在职责上等价 —— 我们的网关就是那个 SDK 的服务端替身。

---

## 2. 现有实现 vs 官方示例：差距清单

| # | 能力 | 官方 Web/TUIKit | 现有 RMCL | 差距等级 |
| --- | --- | --- | --- | --- |
| 1 | 好友列表 | 分组折叠 + 备注 + 在线点 | 平铺列表，无分组 | **高** |
| 2 | 好友分组 | 增删改查 + 移动好友 | 无 | **高** |
| 3 | 好友申请 | 收到/发出、未读、来源、验证消息 | 只有收到列表，无未读、无发出记录、无来源 | **高** |
| 4 | 添加好友 | 搜索 + 验证消息 + 备注 + 分组 + 添加方式 | 搜索 + 固定文案，无备注/分组 | 中 |
| 5 | 删除好友 | 双向，同步会话与列表 | 单向，会话残留（已打补丁，模型仍割裂） | 中 |
| 6 | 好友资料卡 | 发消息 / 备注 / 分组 / 删除 / 拉黑 | 无资料卡，只有列表行按钮 | **高** |
| 7 | 黑名单 | 完整 | 无 | 中 |
| 8 | 在线状态 | 旗舰版 SDK 订阅 | `last_seen_at` 120 秒近似，轮询才更新 | **高** |
| 9 | 会话列表 | store + `CONVERSATION_LIST_UPDATED` 事件 | 每次网络全量拉取，整表替换 | **高** |
| 10 | 未读数 | 服务端权威 + 事件同步 | 轮询覆盖 + 本地补偿，曾出现不消失 | **高** |
| 11 | 消息发送 | 乐观插入 `sending` → 成功改 `success` | 有 pending，但成功后仍整体刷新会话 | 中 |
| 12 | 消息去重 | SDK 内 `clientMsgID` 幂等 | `client_id` 部分实现，上传又引入内容去重补丁 | 中 |
| 13 | 撤回 | 服务端标记 + 事件广播 | 轮询查最近 10 分钟 recalls，窗口外收不到 | 中 |
| 14 | 群组 | 建群/邀请/踢人/退群/群资料 + 事件 | 自建群 + TIM 群双写，成员变更无推送 | **高** |
| 15 | 漫游 | SDK 云端漫游（按时长） | 自建 `friend_roam` + 手动上传，语义与全量恢复冲突 | **高** |
| 16 | 实时通道 | WebSocket 长连接 | HTTP 3 秒轮询 | **高** |
| 17 | 多端一致 | 同一账号多端事件同步 | 无（后登录端靠轮询追） | 中 |
| 18 | 资料 | 昵称/头像/签名 | 只有昵称 | 低 |
| 19 | 好友搜索 | 按 ID 精确 + 昵称模糊 | 按邮箱/昵称模糊 | 低 |
| 20 | 管理后台 | — | PHP 后台已可用，可保留 | — |

**核心差距是 1、2、3、6、8、9、10、14、15、16** —— 全部指向同一件事：缺少「状态 + 事件」层。

---

## 3. 架构重构

### 3.1 技术选型

| 方案 | 优点 | 缺点 | 结论 |
| --- | --- | --- | --- |
| 继续 PHP | 部署不变 | 无长连接、无事件循环；每请求重建上下文；并发靠 FPM 进程 | ✗ |
| Node.js + ws | 生态好、WS 成熟 | 需 Node 运行时 + node_modules，内存占用较高 | 备选 |
| Go 单二进制 | 部署最简单、内存低 | 需要 Go 工具链维护 | 备选 |
| **Python 3.11+ / FastAPI / WebSocket / SQLite WAL** | 用户已有 FastAPI 经验；async 天然适配 WS fanout；改起来快 | 内存约 80–120 MB | **采用** |

选型理由：
1. 用户现有项目（遥感分类系统）就是 FastAPI，维护成本最低。
2. 单机小社区（<1000 用户）用 SQLite WAL 足够，省掉数据库运维；接口层抽象好可换 PostgreSQL。
3. WS + asyncio 广播是官方 SDK 长连接的等价物，是解决实时性的唯一正解。
4. 与 PHP 后台并存：后台继续跑在 `update.mc.lxray.top`，网关独立子域 `im.mc.lxray.top`。

### 3.2 拓扑

```
┌──────────────┐   HTTPS REST（命令/查询）   ┌────────────────────┐
│ RMCL 客户端  │ ──────────────────────────► │  Friend Gateway    │
│ FriendStore  │ ◄────────────────────────── │  FastAPI + SQLite  │
└──────┬───────┘   WSS 事件流（推送/补发）    └─────┬────────┬─────┘
       │                                            │        │
       │  UserSig（登录后下发）                      │        │ REST（服务端签名）
       └──────────────► 腾讯云 IM ◄────────────────┘        │
                          ▲                                 │
                          │ 消息回调（多端一致性兜底）        │
                          └─────────────────────────────────┘
```

关键点：

- 客户端**不保存 IM 密钥**，只拿 `userSig`（有效期 7 天，登录时下发）。
- 所有写操作先落网关数据库，再异步投递 TIM；TIM 失败不影响本地一致性。
- TIM 消息回调进网关，补齐「用户在别的端/官方客户端发的消息」。

### 3.3 单一数据源 + 事件流（对标 SDK）

腾讯云 IM Web SDK：

```
调用 API → SDK 内部 store 变更 → 派发 EVENT → UI 重渲染
```

Friend IM v2：

```
调用 REST → 网关写入 DB + 分配 seq → 写入 event_log → WS 推给相关用户
                                                    ↓
                                     客户端 FriendStore.Apply(event) → 触发 Changed → UI 重绘
```

客户端只有 `FriendStore` 一份状态：

```vb
FriendStore
├── Me
├── Friends         Dictionary(Of Long, FriendItem)      ' 含 remark/group/online
├── FriendGroups    List(Of FriendGroup)
├── Requests        List(Of FriendRequest)               ' 收到 + 发出
├── Conversations   Dictionary(Of String, Conversation)  ' key = "0:123" / "1:45"
├── Messages        Dictionary(Of String, List(Of Message))
├── Groups          Dictionary(Of Long, ChatGroup)
└── Event Apply(evt)   ' 唯一的状态变更入口
```

UI 不再自己发请求改界面，只做两件事：**调命令** + **订阅 `Changed`**。

### 3.4 事件协议

所有推送统一信封：

```json
{ "seq": 10231, "type": "message.new", "data": { }, "ts": 1762500000 }
```

| 事件 | 触发 | 携带 |
| --- | --- | --- |
| `hello` | WS 建连 | `user_id`、`server_seq` |
| `resume` | 客户端重连声明 | `since` |
| `friend.request` | 收到新申请 | 申请对象 + 申请人资料 |
| `friend.accepted` | 对方同意 | 新好友 + 分组 + 会话 |
| `friend.removed` | 被删 / 主动删 | `user_id`、是否双向 |
| `friend.updated` | 备注 / 分组变更 | 好友对象 |
| `group.updated` | 建群 / 成员变更 / 解散 | 群对象 + 成员数 |
| `message.new` | 新消息 | 消息体 + 会话摘要 |
| `message.recalled` | 撤回 | `message_id` + 会话 |
| `conversation.updated` | 未读 / 置顶 / 已读 | 会话摘要 |
| `presence.changed` | 上线下线 | `user_id` + `online` |

**可靠性三件套**：

1. **seq 单调递增**：`event_log.seq` 由 SQLite AUTOINCREMENT 保证；客户端记录 `last_seq`。
2. **断线补发**：重连时发 `{"op":"resume","since":last_seq}`，网关回放 `event_log`，超过保留期则回 `{"op":"resync"}` 让客户端拉一次全量快照。
3. **心跳**：25 秒 ping / 10 秒超时，两次失败即重连（指数退避 1s→30s）。

这三点替代了「3 秒轮询 + 猜哪里漏了」。

### 3.5 与腾讯云 IM SDK 的对接方式

| 场景 | 实现 |
| --- | --- |
| 登录 | 网关用 `sdkappid + key` 生成 UserSig（TLS 2.0，已验证算法）随登录响应下发；客户端 IM 初始化用它 |
| 单聊发消息 | 客户端 → 网关 `POST /messages`；网关落库 + 分配 seq + WS 推送，再异步调 `openim/sendmsg` 做多端/离线推送 |
| 群聊发消息 | 同上，异步调 `group_open_http_svc/send_group_msg` |
| 建群 | 网关调 `group_open_http_svc/create_group`（Owner=创建者），把 `GroupId` 存进 `chat_group.tim_group_id`；TIM 不可用时降级为纯本地群 |
| 成员变更 | `add_group_member` / `delete_group_member` + 本地表，双写但以本地为准 |
| 关系链镜像（可选） | `v4/sns/friend_import`（导入）、`sns/friend_delete`、`sns/friend_get_list`；开关 `tim.friendship_mirror`，默认关，等升级套餐再开 |
| 多端一致性 | 配置 TIM 消息回调地址 `POST /api/v1/tim/callback`，校验签名后落库并推送 |
| 在线状态 | **不用 TIM**（体验版不支持），网关按 WS 连接数判定，30 秒宽限期 |

> 落地前需用一次真实调用校验 `v4/sns/*` 的接口名与参数（关系链 REST 文档变更过），代码里已隔离在 `tim.py`，改一处即可。

---

## 4. 数据模型

SQLite（WAL）。所有时间戳为 Unix 秒。

```sql
-- 账号
CREATE TABLE user (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  email         TEXT NOT NULL COLLATE NOCASE UNIQUE,
  nickname      TEXT NOT NULL,
  avatar        TEXT NOT NULL DEFAULT '',
  signature     TEXT NOT NULL DEFAULT '',
  pass_hash     TEXT NOT NULL DEFAULT '',
  invite_code   TEXT NOT NULL DEFAULT '',
  rayskin_uuid  TEXT NOT NULL DEFAULT '',
  status        TEXT NOT NULL DEFAULT 'active',   -- active / disabled
  created_at    INTEGER NOT NULL,
  last_seen_at  INTEGER NOT NULL DEFAULT 0
);

-- 令牌（可多端）
CREATE TABLE auth_token (
  token       TEXT PRIMARY KEY,
  user_id     INTEGER NOT NULL,
  device      TEXT NOT NULL DEFAULT '',
  created_at  INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL
);

-- 好友申请：一行代表一次申请，状态机驱动
CREATE TABLE friend_request (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  from_id     INTEGER NOT NULL,
  to_id       INTEGER NOT NULL,
  wording     TEXT NOT NULL DEFAULT '',
  source      TEXT NOT NULL DEFAULT '',   -- search / group / qrcode / rayskin
  remark      TEXT NOT NULL DEFAULT '',
  group_id    INTEGER NOT NULL DEFAULT 0,
  status      TEXT NOT NULL DEFAULT 'pending',  -- pending / accepted / rejected / expired
  read_at     INTEGER NOT NULL DEFAULT 0,       -- 接收方已读
  created_at  INTEGER NOT NULL,
  handled_at  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_req_to   ON friend_request(to_id, status);
CREATE INDEX idx_req_from ON friend_request(from_id, status);

-- 好友关系：双向两行，便于各自维护备注与分组
CREATE TABLE friendship (
  user_id     INTEGER NOT NULL,
  friend_id   INTEGER NOT NULL,
  remark      TEXT NOT NULL DEFAULT '',
  group_id    INTEGER NOT NULL DEFAULT 0,
  created_at  INTEGER NOT NULL,
  PRIMARY KEY (user_id, friend_id)
);

-- 好友分组
CREATE TABLE friend_group (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER NOT NULL,
  name       TEXT NOT NULL,
  sort       INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX idx_fg ON friend_group(user_id, name);

-- 黑名单
CREATE TABLE block (
  user_id    INTEGER NOT NULL,
  blocked_id INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (user_id, blocked_id)
);

-- 会话（每人一份）
CREATE TABLE conversation (
  user_id     INTEGER NOT NULL,
  conv_type   INTEGER NOT NULL,          -- 0 单聊 / 1 群聊
  conv_key    TEXT NOT NULL,             -- 单聊=对方 user_id，群聊=group_id
  last_msg_id INTEGER NOT NULL DEFAULT 0,
  last_body   TEXT NOT NULL DEFAULT '',
  last_at     INTEGER NOT NULL DEFAULT 0,
  unread      INTEGER NOT NULL DEFAULT 0,
  pinned      INTEGER NOT NULL DEFAULT 0,
  muted       INTEGER NOT NULL DEFAULT 0,
  hidden      INTEGER NOT NULL DEFAULT 0, -- 删除会话（保留消息）
  PRIMARY KEY (user_id, conv_type, conv_key)
);

-- 消息（唯一事实来源）
CREATE TABLE message (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  conv_type     INTEGER NOT NULL,
  conv_key      TEXT NOT NULL,
  from_id       INTEGER NOT NULL,
  to_id         INTEGER NOT NULL DEFAULT 0,
  group_id      INTEGER NOT NULL DEFAULT 0,
  client_msg_id TEXT NOT NULL DEFAULT '',
  body          TEXT NOT NULL DEFAULT '',
  kind          TEXT NOT NULL DEFAULT 'text',  -- text / image / system / recalled
  created_at    INTEGER NOT NULL,
  recalled_at   INTEGER NOT NULL DEFAULT 0,
  tim_msg_id    TEXT NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX idx_msg_client ON message(from_id, client_msg_id) WHERE client_msg_id <> '';
CREATE INDEX idx_msg_conv ON message(conv_type, conv_key, id);

-- 群
CREATE TABLE chat_group (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  name         TEXT NOT NULL,
  owner_id     INTEGER NOT NULL,
  avatar       TEXT NOT NULL DEFAULT '',
  notice       TEXT NOT NULL DEFAULT '',
  tim_group_id TEXT NOT NULL DEFAULT '',
  created_at   INTEGER NOT NULL
);

CREATE TABLE group_member (
  group_id  INTEGER NOT NULL,
  user_id   INTEGER NOT NULL,
  role      INTEGER NOT NULL DEFAULT 0,   -- 1 群主 / 0 成员
  joined_at INTEGER NOT NULL,
  PRIMARY KEY (group_id, user_id)
);

-- 事件日志（补发用）
CREATE TABLE event_log (
  seq      INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id  INTEGER NOT NULL,
  type     TEXT NOT NULL,
  payload  TEXT NOT NULL,
  ts       INTEGER NOT NULL
);
CREATE INDEX idx_evt_user ON event_log(user_id, seq);

-- presence
CREATE TABLE presence (
  user_id     INTEGER PRIMARY KEY,
  conn_count  INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);
```

**设计要点**

- `friendship` 双向两行 → 删除好友是两次删除，或一次事务删两行；备注/分组天然各管各的。
- `conversation.hidden` 而不是 DELETE → 恢复历史/收到新消息时可以重新浮出，且消息不丢。
- `message` 是唯一事实来源，`conversation` 只是它的投影（可由 `last_msg_id` 重算）。
- `event_log` 按人写入（fanout on write），读的时候 `WHERE user_id=? AND seq>?` 一条索引搞定。

---

## 5. 接口设计

统一前缀 `/api/v1`，JSON，`Authorization: Bearer <token>`。
错误：HTTP 4xx/5xx + `{"error":{"code":"E_XXX","message":"..."}}`。

### 5.1 认证

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/auth/register` | 邀请码 + 邮箱验证码 + 密码 |
| POST | `/auth/login` | 邮箱 + 密码，返回 token + user + userSig |
| POST | `/auth/rayskin` | RaySkin 一键登录（沿用现有 hasJoined / validate 双路径） |
| POST | `/auth/logout` | 吊销 token |
| GET | `/me` | 当前用户 + 未读总数 + 好友申请未读数 |

### 5.2 好友

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/friends` | 按分组返回，含 `online`、`remark`、`conversation_key` |
| GET | `/friends/{id}` | 资料卡（备注、分组、关系状态、是否拉黑） |
| PATCH | `/friends/{id}` | 改备注 / 移动分组 |
| DELETE | `/friends/{id}` | 双向删除，可选 `keep_conversation` |
| GET | `/search?q=` | 邮箱/昵称精确+模糊，返回关系状态 |

### 5.3 申请

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/friend-requests?box=in\|out` | 收到 / 发出 |
| POST | `/friend-requests` | `{to_id, wording, remark, group_id, source}` |
| POST | `/friend-requests/{id}/accept` | 同意 → 建双向关系 + 会话 + 事件 |
| POST | `/friend-requests/{id}/reject` | 拒绝 |
| POST | `/friend-requests/read` | 全部标记已读，清零红点 |

### 5.4 分组与黑名单

| 方法 | 路径 |
| --- | --- |
| GET / POST | `/friend-groups` |
| PATCH / DELETE | `/friend-groups/{id}` |
| GET / POST / DELETE | `/blocks`、`/blocks/{user_id}` |

### 5.5 会话与消息

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/conversations` | 含未读、最后一条、在线、群人数 |
| POST | `/conversations/{conv}/read` | 已读，返回剩余未读总数 |
| DELETE | `/conversations/{conv}` | 只隐藏会话，不删消息 |
| GET | `/messages?conv=0:123&before=&limit=` | 倒序分页 |
| POST | `/messages` | `{conv, client_msg_id, body}`，**幂等**：同一 `client_msg_id` 直接返回原消息 |
| POST | `/messages/{id}/recall` | 2 分钟内，本人 |

### 5.6 群

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET / POST | `/groups` | 我的群 / 建群（可带成员） |
| GET / PATCH | `/groups/{id}` | 群资料（群主可改） |
| POST | `/groups/{id}/members` | 邀请（群主或成员？默认群主，可配） |
| DELETE | `/groups/{id}/members/{uid}` | 踢人（群主）/ 退群（自己） |
| POST | `/groups/{id}/leave` | 退群 |

### 5.7 WebSocket

```
wss://im.mc.lxray.top/ws?token=<token>

→ {"op":"hello","user_id":7,"server_seq":10231}
→ {"op":"resume","since":10190}
← {"op":"event","seq":10191,...} × N
← {"op":"resumed","seq":10231}
← {"op":"ping"} / → {"op":"pong"}
```

---

## 6. 客户端重构

### 6.1 页面结构（对标 TUIKit 联系人页）

```
好友
├── 消息         会话列表（未读、置顶、最后一条）
├── 通讯录
│   ├── 好友申请   ← 红点，收到 / 发出两个 tab
│   ├── 我的好友   ← 分组折叠，备注 + 在线点
│   └── 我的群组
├── 群聊         我创建/加入的群，成员管理
└── 账号         资料、邀请码、漫游、缓存
```

资料卡独立为浮层（`PageFriendProfile`），不再塞在列表行里。

### 6.2 状态与命令分离

```vb
' 命令：只发请求，不碰 UI
FriendStore.AddFriend(toId, wording, remark, groupId)
FriendStore.AcceptRequest(reqId)

' 事件：唯一改状态的地方
FriendStore.Apply(evt)

' UI：只订阅
AddHandler Store.Changed, AddressOf RenderAll
```

### 6.3 乐观更新与幂等

| 动作 | 客户端行为 |
| --- | --- |
| 发消息 | 立即插入 `pending`（`client_msg_id` = GUID），`message.ack` 到达后原地替换为 `sent`，失败标 `failed` 可重试 |
| 已读 | 本地先清零 + 重绘，再发请求；失败回滚 |
| 同意申请 | 本地先移除申请、插入好友，`friend.accepted` 校准 |
| 撤回 | 本地先把气泡改成「已撤回」，失败回滚 |

服务端 `client_msg_id` 唯一索引保证重复提交只有一条。

### 6.4 需要改的文件

| 文件 | 变更 |
| --- | --- |
| `RMCL/Modules/FriendStore.vb` | 新增，状态 + 事件 + 命令 |
| `RMCL/Modules/FriendApi.vb` | 新增，REST + WS 客户端，替换 `ModFriend` 的 HTTP 部分 |
| `RMCL/Pages/PageFriend/*` | 重写 6 个页面 + 新增 `PageFriendProfile` |
| `RMCL/Pages/PageSetup/Settings.vb` | 新增 `FriendImV2Enabled`、`FriendServerV2` |
| `ModFriend.vb` | 保留 RaySkin / 链接识别的工具函数，删除网络与缓存逻辑 |

---

## 7. 分阶段实施计划

| 阶段 | 内容 | 产出 | 预估 |
| --- | --- | --- | --- |
| **P0 基线** | 建分支、目录、灰度开关、旧模块保留可回滚 | 分支 + 开关 + 编译通过 | 0.5 天 |
| **P1 网关内核** | FastAPI 骨架、SQLite schema、账号/令牌、WS hub、event_log、重连补发、心跳 | `server-v2` 可跑，冒烟脚本通过 | 2 天 |
| **P2 通讯录** | 好友列表/分组/资料卡/备注、申请中心（收到/发出/未读） | 客户端通讯录页替换完成 | 2 天 |
| **P3 会话与消息** | 会话列表、未读、已读、发送幂等、撤回、历史分页、乐观更新 | 聊天页替换完成，两端 <300ms | 2.5 天 |
| **P4 群组** | 建群、邀请、踢人、退群、群资料、TIM 群同步 | 群聊页替换完成 | 1.5 天 |
| **P5 在线与黑名单** | presence 广播、宽限期、黑名单过滤 | 在线点准确率 > 95% | 1 天 |
| **P6 迁移与下线** | 旧数据导入、灰度放量、移除 PHP friend 接口、后台迁移 | 旧接口下线 | 1.5 天 |

总计约 **11 人日**。P1 完成后即可并行做 P2 的 UI。

**灰度策略**

1. `Settings("FriendImV2")` 默认 false → 走旧模块。
2. 内部账号开启 → 双跑，对比消息条数与在线状态。
3. 全量后保留一个版本，再删旧代码。

---

## 8. 验收标准

### P1 网关

- [ ] 两个客户端同时在线，A 发消息，B 在 **300ms 内**收到（同机测试）。
- [ ] 拔网 30 秒后恢复，B 收到断线期间全部消息，无重复、无乱序。
- [ ] `seq` 严格递增，客户端 `last_seq` 与服务端一致。
- [ ] 同一 `client_msg_id` 提交 3 次，数据库只有 1 条。

### P2 通讯录

- [ ] A 搜索 B 并发送申请（带验证消息），B 在 1 秒内收到红点与条目。
- [ ] B 点开申请列表，红点清零；刷新后仍为 0（已持久化）。
- [ ] B 同意 → **双方列表同时出现对方**，无需手动刷新。
- [ ] 修改备注 → 对方列表不变，自己列表立即变；重登后保持。
- [ ] 移动到分组 → 分组内成员数即时更新。
- [ ] 删除好友 → 双方列表移除；会话保留并标「非好友」。
- [ ] 拉黑 → 对方发消息被拒（`E_BLOCKED`），自己收不到。

### P3 会话与消息

- [ ] 发送消息：气泡先出现（pending），成功后变正常，**会话卡片同时更新**（同一帧）。
- [ ] 收到消息时正在翻历史 → 不强制滚到底；在底部 → 自动跟随。
- [ ] 打开会话 → 红点立即消失，切走再回来仍为 0。
- [ ] 撤回 → 双方 1 秒内都变「已撤回」，会话预览同步。
- [ ] 删除会话 → 从列表消失；对方再发消息会重新出现且未读为 1。
- [ ] 历史分页：向上翻 3 页无重复、无缺口。

### P4 群组

- [ ] 建群（含 2 名成员）→ 三人同时看到群会话。
- [ ] 邀请好友 → 被邀请者立即出现该群，群人数 +1。
- [ ] 踢人 → 被踢者会话消失，无法再发消息（`E_NOT_MEMBER`）。
- [ ] 退群 → 自己列表移除，其他人看到人数 -1。

### P5 在线状态

- [ ] A 上线 → B 的好友列表 2 秒内变绿点。
- [ ] A 关闭客户端 → 30 秒宽限期后变灰（避免闪断）。
- [ ] A 断网但进程存活 → 心跳失败后 60 秒内变灰。

---

## 9. 与旧功能的行为对比

| 场景 | 旧实现（2.20.2.1） | 新实现（v2） |
| --- | --- | --- |
| 收到消息 | 最长 3 秒后轮询到 | WS 推送，<300ms |
| 会话卡片更新 | 每次全量拉取会话列表，整表替换 | 本地事件增量更新，与聊天页同帧 |
| 红点消失 | 请求返回后本地清零，偶发被轮询覆盖 | 服务端权威 + 事件确认，本地先清零，冲突以 seq 大者为准 |
| 好友申请 | 无未读、无发出记录 | 收到/发出、未读持久化、红点 |
| 好友分组 | 无 | 完整增删改查 + 移动 |
| 好友资料 | 无资料卡 | 资料卡：备注/分组/发消息/删除/拉黑 |
| 删除好友 | 单向，会话残留 | 双向，会话标记非好友 |
| 在线状态 | `last_seen_at` 120 秒，轮询才更新 | WS 连接态，2 秒广播，30 秒宽限 |
| 群成员变更 | 对方需手动刷新 | 事件推送，实时 |
| 消息去重 | `client_id` + 内容指纹补丁 | 数据库唯一索引，天然幂等 |
| 历史恢复 | 全量 `sync` 与漫游 `roam_restore` 两套语义 | 统一：历史 = 消息表，漫游 = 保留策略，客户端只调一个分页接口 |
| 断线 | 轮询自然恢复，但可能漏消息 | seq 补发，漏了自动全量 resync |
| 多端 | 无 | 同一账号多端同时收事件 |

---

## 10. 风险与待确认

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| 服务器不支持长连接（共享主机） | 无法用 WS | 降级：同一事件协议走 HTTP 长轮询 `/events?since=`，客户端代码不变（传输层抽象） |
| TIM 体验版 20 好友 / 20 群成员 | 用户量一大就撞墙 | 好友关系自建不受限；群成员超 20 需升套餐或纯自建群消息 |
| 在线状态需旗舰版 | 无法用 TIM 状态 | 自建 presence（已按此设计） |
| 关系链 REST 接口名变更 | 镜像失败 | 隔离在 `tim.py`，默认关闭镜像 |
| 旧数据迁移 | 用户历史丢失 | P6 写一次性导入脚本，按 `client_id` 去重 |
| 双跑期间数据不一致 | 消息重复 | 灰度账号单向写入，旧接口只读 |

**开工前需确认**

1. 是否有可跑常驻进程的服务器（VPS/云服务器）与子域 `im.mc.lxray.top`？
2. TIM 套餐是否维持体验版？若短期不升级，群成员上限 20 需接受。
3. 旧聊天记录是否需要迁移（影响 P6 工作量）。

---

## 11. 实施进度

### P1 网关内核（已完成）

已落地 `server-v2/`：

| 项 | 状态 |
| --- | --- |
| FastAPI + SQLite schema（14 张表） | ✅ |
| 账号 / 令牌 / 验证码 / 邀请码 | ✅ |
| 好友、申请（收到+发出+未读）、分组、备注、黑名单 | ✅ |
| 会话、消息（幂等）、已读、撤回 | ✅ |
| 群组：建群、邀请、踢人、退群、群资料 | ✅ |
| WebSocket：hello / resume / ping / 事件补发 / 事件过期 resync | ✅ |
| presence：连接态 + 断线宽限 + 好友广播 | ✅ |
| TIM：UserSig、单聊、群消息、建群、成员同步、关系链镜像开关 | ✅ |

端到端冒烟 `tests/smoke.py`：**30 项断言全部通过**，覆盖
注册 → 加好友 → 申请未读/已读 → 同意 → 备注/分组 → WS 实时消息 → 未读/已读 →
`client_msg_id` 幂等 → 撤回 → 断线补发 → 建群/踢人/越权 → 删好友后拒发。

调试中修掉的两个实现级缺陷（值得记录）：

1. **单聊会话键的视角问题**：`conversation.conv_key` 必须存「对方 id」（每人一份），
   而 `message.conv_key` 必须是规范键 `min_max`。最初两者共用同一个 key，
   导致消息写进了发送者自己的会话、接收方看不到未读。
2. **补发水位**：`event_log` 清理后，早于水位的 `since` 无法补发，必须回 `resync` 让客户端拉快照。
   已在 `meta.event_floor` 记录水位并在 `resume` 时判断。

### 下一步（P2）

1. 客户端新增 `FriendApi.vb`（REST + WS 客户端）与 `FriendStore.vb`（状态 + Apply(event)）。
2. 重写通讯录页（申请中心 + 分组好友列表 + 资料卡）。
3. 灰度开关 `Settings("FriendImV2")`，旧页面保留可回滚。


---

## 12. 范围收敛（Lite，最终实施范围）

### 12.1 取舍

| 能力 | 是否保留 | 原因 |
| --- | --- | --- |
| 好友列表（平铺） | ✅ | 基础 |
| 搜索用户 / 发起申请 / 同意 / 拒绝 | ✅ | 加好友的最小闭环 |
| 删除好友 | ✅ | 基础 |
| 单聊 / 群聊 / 会话列表 | ✅ | 基础 |
| 消息撤回（2 分钟） | ✅ | 已有交互，成本低 |
| 历史分页 | ✅ | 基础 |
| 建群 / 邀请 / 踢人 / 退群 | ✅ | 体验版支持 |
| 未读红点 / 已读上报 | ✅ | 会话红点是必需交互；已读回执（对方看到「已读」）不做 |
| 在线状态 | ❌ | 体验版不支持用户状态（需旗舰版） |
| 好友分组 | ❌ | 用户明确不要 |
| 备注 / 黑名单 / 关系链镜像 | ❌ | 归入「好友关系」复杂功能，去掉 |
| 聊天记录漫游上传 / 恢复 | ❌ | 体验版本就有 7 天云端漫游，无需自建 |

### 12.2 收敛后的数据模型

`user` / `auth_token` / `auth_code` / `invite` / `friend_request` / `friendship` /
`conversation` / `message` / `chat_group` / `group_member` / `event_log` / `meta`（12 张）。

相比全量方案删掉了：`friend_group`、`block`、`presence`；
`conversation` 保留 `unread`（去掉 `pinned` / `muted`）；`friendship` 去掉 `remark` / `group_id`；
`friend_request` 去掉 `read_at`。

### 12.3 收敛后的接口

| 分组 | 接口 |
| --- | --- |
| 认证 | `POST /auth/send-code`、`/auth/register`、`/auth/login`、`/auth/logout`、`GET /me` |
| 好友 | `GET /friends`、`GET /friends/{id}`、`DELETE /friends/{id}` |
| 申请 | `GET /friend-requests?box=in|out`、`POST /friend-requests`、`POST /friend-requests/{id}/accept`、`/reject` |
| 会话 | `GET /conversations`、`POST /conversations/{conv}/read`、`DELETE /conversations/{conv}` |
| 消息 | `GET /messages`、`POST /messages`、`POST /messages/{id}/recall` |
| 群组 | `GET /groups`、`POST /groups`、`GET /groups/{id}`、`PATCH /groups/{id}`、`POST /groups/{id}/members`、`DELETE /groups/{id}/members/{uid}` |
| 其他 | `GET /search`、`GET /snapshot`、`GET /health` |

删除的接口：`/friend-groups*`、`/blocks*`、`/presence`、`/conversations/{conv}/read`、
`/friend-requests/read`、`PATCH /friends/{id}`。

### 12.4 收敛后的事件

`friend.request`、`friend.accepted`、`friend.request.handled`、`friend.removed`、
`message.new`、`message.recalled`、`group.updated`、`group.removed`、`conversation.removed`。

删除：`presence.changed`、`friend.group.updated`、`block.updated`。
`conversation.updated` 保留，用于多端已读同步。

### 12.5 验收标准（替代第 8 节）

| 场景 | 通过条件 |
| --- | --- |
| 添加好友 | A 搜索 B → 发申请 → B 在 1 秒内收到 `friend.request` 事件；同意后双方列表即时出现对方，无需刷新 |
| 申请重复 | 同一对用户重复提交，只产生一条 `pending`，返回 `duplicated` |
| 删除好友 | 双方列表移除；再发消息返回 `E_NOT_FRIEND` |
| 实时消息 | A 发送后 B 在 300ms 内收到 `message.new`；会话列表 `last_body` 同步更新 |
| 幂等 | 同一 `client_msg_id` 连发 3 次，库里只有 1 条，返回 `duplicated` |
| 撤回 | 2 分钟内可撤回；双方 1 秒内看到「已撤回」，会话预览变 `[已撤回]`；超时返回 `E_EXPIRE` |
| 断线补发 | 断开 30 秒后重连 `resume`，期间消息全部补到，无重复、无乱序 |
| 群聊 | 建群后成员即时看到群；被邀请者出现群会话；被踢后发消息返回 `E_NOT_MEMBER` |
| 越权 | 非群主邀请/踢人返回 `E_NOAUTH`；非成员读群消息返回 `E_NOT_MEMBER` |

### 12.6 与旧行为对比（收敛后）

| 场景 | 旧（PHP + 轮询） | 新（Lite 网关 + WS） |
| --- | --- | --- |
| 收到消息 | 最长 3 秒后轮询到 | WS 推送，<300ms |
| 会话卡片 | 每次全量拉取、整表替换 | 事件驱动，与聊天页同帧 |
| 好友申请 | 无发出记录、无验证消息持久化 | 收到/发出双向可查，带验证消息 |
| 加好友后 | 需手动刷新 | 双方事件推送，即时出现 |
| 断线 | 靠轮询恢复，可能漏消息 | `resume` 按 seq 补发，过期回 `resync` |
| 消息重复 | `client_id` + 内容指纹补丁 | 数据库唯一索引，天然幂等 |
| 未读 | 轮询覆盖，偶发不消失 | 服务端权威 + 事件推送，打开会话即清零 |
| 在线/分组 | 有（但不可靠） | 不提供（按需求移除） |

### 12.7 修订后的阶段计划（替代第 7 节）

| 阶段 | 内容 | 产出 | 预估 |
| --- | --- | --- | --- |
| P0 | 分支、灰度开关、可回滚 | 分支 + `FriendImV2` 开关 | 0.5d ✅ |
| P1 | 网关内核（Lite） | `server-v2` 可跑，31 项冒烟通过 | 2d ✅ |
| P2 | 好友页：平铺列表、搜索添加、申请中心（收到/发出） | 通讯录页替换 | 1.5d |
| P3 | 消息页：会话列表、聊天、发送幂等、撤回、历史分页 | 聊天页替换 | 2d |
| P4 | 群聊页：建群、邀请、踢人、退群 | 群聊页替换 | 1d |
| P5 | 账号页收尾（去掉漫游/缓存/邀请码用量）、迁移与下线旧接口 | 旧 PHP 接口下线 | 1d |

合计约 **8 人日**（原 11 人日，去掉未读/在线/分组/黑名单/漫游后减少 3 天）。
