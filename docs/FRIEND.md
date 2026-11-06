# 好友功能（RMCL Friend）

好友页面是**默认隐藏**的顶级页面，在「设置 → 功能 → 好友页面」里勾选「显示好友页面」后才会出现在顶部栏，随时可以关闭。关闭不会删除任何聊天记录。

- 客户端：`RMCL/Modules/ModFriend.vb` + `RMCL/Pages/PageFriend/*`
- 服务端：`server/lib/friend.php`、`server/lib/tencent_im.php`
- 管理后台：`server/admin.php` 的「好友」标签页（旧入口 `admin-friend.php` 会 302 跳转过去）

---

## 1. 功能与实现位置

| 需求 | 实现 |
| --- | --- |
| 邀请码 + 邮箱验证注册 | `friend_send_code` / `friend_register`，邀请码记录在 `friend_invite`，邮箱验证码只存 `password_hash` |
| RaySkin 一键登录 | 客户端走 Yggdrasil `authenticate` + `join`，服务端调用 `sessionserver/.../hasJoined` 校验；密码不经过好友服务端 |
| 好友添加 / 通讯录 | `friend_add`、`friend_requests`、`friend_accept`、`friend_reject`、`friend_contacts` |
| 创建群聊 | `friend_group_create`，同时调用腾讯云 IM 的 `create_group`，失败不影响本地群聊 |
| 好友 / 群聊 | `friend_send`、`friend_poll`、`friend_messages`，消息落 `friend_message` |
| 历史记录查询 | `friend_history`（关键词 + 会话范围 + 翻页）与账号页的「恢复本地聊天记录」`friend_sync` |
| 联机邀请码自动识别 | 聊天消息中匹配 `Pxxxx-xxxxx-xxxxx`，点「导入邀请码」直接调用 `PageLinkMain.Join` |
| TCP 链接自动识别 | 匹配 `tcp://host:port`（也识别 udp/ws/wss/quic/wg/faketcp/ring），点「使用该节点」写入 `LinkCustomPeer` 并跳到联机页 |
| HTTP(S) 链接自动识别 | 解析 host/port，弹出游戏版本选择，确认后用 `McLaunchStart` 的 `ServerIp` 直接进入服务器 |
| `frp-xxx.com:12345` | 同样识别为可进入的服务器地址，选版本后直接启动 |
| 邀请码导入 | 先选游戏版本再进房；进入房间后完成页仍可换版本 |
| 邀请码分享 | 联机完成页「分享给好友」，把邀请码发到好友或群聊 |
| 消息右键菜单 | 复制 / 转发 / 撤回 / 删除（删除只影响本机） |
| 撤回消息 | `friend_recall`，仅限自己 2 分钟内发的；服务端标记 `kind='recalled'`，对方下次轮询即看到 |
| 清除本地聊天记录 | 账号页「清除本地聊天记录」，只删本机缓存，服务器历史不受影响 |

### 消息通道

```
RMCL 客户端 ──HTTPS──> server/index.php?action=friend_*
                         │
                         ├─ SQLite：friend_message / friend_conversation（唯一事实来源）
                         └─ 腾讯云 IM REST（尽力而为）
                              openim/sendmsg、group_open_http_svc/*、sns/*
```

腾讯云 IM 只负责实时转发；**历史记录、未读数、会话列表全部由自建服务端落库**。因此：

- 没配置 `sdkappid` / `key` 时功能依然可用（客户端每 6 秒增量轮询一次）；
- IM 调用失败不会导致消息丢失；
- 换电脑后在「账号 → 恢复本地聊天记录」即可从服务端补回历史。

---

## 2. 部署服务端

1. 把 `server/` 整个目录上传到站点（与更新服务可以共用同一个目录）。
2. 编辑 `config.php`，确认以下两项：
   - `data_dir` 可写（SQLite 与邮件日志都写在这里）；
   - `friend.rayskin_hosts` 包含你实际使用的皮肤站域名。
3. 打开 `admin.php` 登录，进入顶部导航的「好友」标签页（用户 / 邀请码 / 群组 / 维护），生成第一批邀请码。
   旧地址 `admin-friend.php` 会自动跳转到这个标签页。
4. 好友服务地址**硬编码**在 `RMCL/Modules/ModFriend.vb` 的 `DefaultFriendServer`，客户端不显示也不允许修改；
   换服务器时需要改这个常量并重新编译客户端。

### 2.1 腾讯云即时通信 IM

1. 在 [IM 控制台](https://console.cloud.tencent.com/im) 创建应用，记下 **SDKAppID**。
2. 「基本信息」里复制**密钥**（仅服务端使用，绝不能下发给客户端）。
3. 填写 `config.php` 的 `friend.tim`：

```php
'tim' => [
    'enabled' => true,
    'sdkappid' => 1600171310,
    'key' => '',                 // 留空则读取下面的密钥文件
    'key_file' => 'tim.key',     // data/tim.key
    'admin' => 'administrator',
    'group_type' => 'Private',
],
```

4. 把密钥写进 `data/tim.key`（**不要**写进 `config.php`，也不要提交到仓库）：

```
# 服务器上执行，或直接用 FTP 上传这个文件
printf '%s' '你的密钥' > data/tim.key
chmod 660 data/tim.key
```

`data/` 目录已被 `.htaccess` 整体拒绝 Web 访问，也在 `.gitignore` 里；nginx 部署请确认 1.4 节的 `deny all` 规则覆盖到它。

5. 打开 `admin.php?tab=friends`，确认「腾讯云 IM：已启用」。

> 当前仓库内的 `SDKAppID` 已填为 `1600171310`，密钥文件需要在部署时单独上传（`server.zip` 不含 `data/`）。
> 该账号已实测通过 account_import、单聊 sendmsg、建群、群消息与解散群。

说明：

- UserSig 由 `tim_usersig()` 生成，算法与官方 `TLSSigAPIv2` 一致（JSON → gzcompress → URL 安全 Base64）。
- 注册 / 登录时会调用 `im_open_login_svc/account_import` 导入 IM 账号（幂等）。
- 群聊创建后把 `GroupId` 写回 `friend_group.im_group_id`。
- 如果服务器出口 IP 在 IM 的「回调 / REST API 白名单」限制内，需要把虚拟主机出口 IP 加进去。

### 2.2 邮箱验证

**安全前提：`data/` 必须禁止 Web 访问。** 站点是 nginx 时 `.htaccess` 不生效，必须在站点配置里加：

```nginx
location ^~ /data/ {
    deny all;
    return 403;
}
```

验证方式：`curl -I https://你的域名/data/rmcl.sqlite` 必须返回 403/404。`data/` 里有 SQLite 数据库
与 `tim.key`，一旦可下载，等于账号、聊天记录、邀请码与 IM 密钥全部泄露。

邮件投递优先读 `server/config.smtp.php`（不进版本库，包含 SMTP 授权码），通过 SSL/STARTTLS 直连发信；
没有该文件时回退到 PHP `mail()`。共享虚拟主机的 `mail()` 常常发不出去，建议一律用 SMTP：

```php
// server/config.smtp.php
return [
    'host' => 'smtp.qq.com', 'port' => 465, 'encryption' => 'ssl',
    'user' => 'xxx@foxmail.com', 'pass' => '邮箱授权码',
    'from' => 'xxx@foxmail.com', 'from_name' => 'RaySkin',
];
```

临时调试也可以把 `friend.debug_code` 设为 `true`（验证码会回传客户端，仅限测试环境，用完改回）。

默认使用 PHP `mail()`。共享虚拟主机常常发不出信，可以：

- 在 `config.php` 里把 `email_enabled` 设为 `false`：验证码会以 `dev_code` 字段回传客户端，并写入 `data/friend-mail.log`（只建议测试环境这么用）；
- 或者保留 `email_enabled = true`，把 `email_from` 改成主机商要求的发信地址。

验证码：6 位数字，10 分钟有效，同一邮箱 60 秒内只能发一次，最多尝试 6 次。

### 2.3 RaySkin 一键登录

1. 客户端从 `AuthServerList` 里取出最近使用的 RaySkin 账号（邮箱 / 密码 / 角色）。
2. 客户端优先复用启动页缓存的 `accessToken` 直接 `join`（不会顶掉启动页会话）；
   缓存失效时才用保存的密码重新 `/authserver/authenticate`。
   Blessing Skin 仅在账号**只有一个角色**时返回 `selectedProfile`，多角色账号需要再调用
   `/authserver/refresh` 并指定 `selectedProfile`，客户端已按这个流程处理。
3. 客户端调用 `/sessionserver/session/minecraft/join`，让 RaySkin 记录一次会话。
4. 好友服务端收到 `email + uuid + name + server_id + server` 后，调用
   `/sessionserver/session/minecraft/hasJoined?username=&serverId=` 校验，确认 `id` 与提交的 UUID 一致。
5. 校验通过后：已有账号则绑定 `rayskin_uuid` 并登录；没有账号且 `rayskin_auto_register = true` 时自动创建。

因此密码只在客户端与 RaySkin 之间传递。若需要更细的皮肤站能力（例如插件内授权），可按
[Blessing Skin 插件文档](https://github.com/bs-community/blessing-skin-plugin-docs) 增加一个 OAuth 端点，
再把 `friend_rayskin_login` 换成对应的 token 校验即可。

---

## 3. 接口

所有接口都是 `POST index.php?action=friend_<name>`，请求体 JSON，响应统一为
`{"ok":true,"data":{...}}` 或 `{"ok":false,"error":{"code","message"}}`（HTTP 200）。
需要登录的接口带请求头 `X-Friend-Token`。

| action | 说明 |
| --- | --- |
| `config` | 服务信息、是否启用 IM、是否需要邀请码 |
| `send_code` | 发送邮箱验证码（`email`、`purpose`） |
| `register` | 注册（`email`、`code`、`nickname`、`password`、`invite`） |
| `login` | 登录（`email`、`password`） |
| `rayskin_login` | RaySkin 校验登录 |
| `me` / `set_profile` / `logout` | 账号信息 |
| `search` / `add` | 搜索用户、发送好友请求 |
| `requests` / `accept` / `reject` | 好友请求列表与处理 |
| `delete_friend` / `contacts` | 删除好友、通讯录 |
| `group_create` / `group_list` / `group_info` / `group_leave` | 群聊 |
| `group_add_members` / `group_kick` | 群主邀请 / 移出成员 |
| `send` / `poll` / `messages` / `read` | 发消息、增量拉取、会话历史、已读 |
| `recall` | 撤回自己 2 分钟内发的消息（`msg_id`） |
| `conversations` | 会话列表（含未读数） |
| `history` | 关键词检索全部历史 |
| `sync` | 恢复本地聊天记录 |

数据表：`friend_user`、`friend_invite`、`friend_token`、`friend_code`、`friend_relation`、`friend_request`、`friend_group`、`friend_group_member`、`friend_message`、`friend_conversation`、`friend_meta`。

### 聊天记录存在哪里

| 位置 | 内容 | 会不会被「清理游戏垃圾」删掉 |
| --- | --- | --- |
| `RMCL\Friend\cache\<账号>_<会话>.json` | 本机聊天缓存，每个会话一个文件，按账号隔离 | **不会**（清理只动 `%TEMP%\RMCL`） |
| 服务端 `data/rmcl.sqlite` 的 `friend_message` | 完整历史，唯一事实来源 | 不会（服务器上） |

客户端首次读取会话时会从旧位置 `%TEMP%\RMCL\Friend\` 自动迁移一次。账号页可以看到当前缓存目录、
会话数量与占用大小，也可以「清除本地聊天记录」或直接打开这个文件夹。

> 早期版本把缓存放在 `%TEMP%\RMCL\Friend`，而「清理游戏垃圾」会清空 `%TEMP%\RMCL` 下除 `cache`
> 之外的所有内容，确实会误删聊天记录。现在缓存已搬到 `RMCL\Friend`，清理逻辑也显式跳过 `friend`，
> 双重保险。

---

## 4. 性能设计

客户端以「最小占用」为约束做了这些取舍：

- **单轮询**：只有一个 6 秒的增量接口（`since_id`），一次拿到所有会话的新消息，不再按会话各开一个请求；
- **只在好友页轮询**：切换到其它顶级页面立即 `Stop()` 定时器；
- **本地优先**：打开会话先渲染本地缓存（每条会话最多 400 条），再由后台补拉最新 60 条；
- **限量渲染**：界面最多渲染 60 条，需要时「查看更早的消息」按 60 条递增；
- **批量落盘**：消息先进内存，页面退出或轮询结束后统一 `LocalFlush()`，避免每条消息一次磁盘 IO；
- **列表重建有节流**：会话列表刷新至少间隔 4 秒，且只在有新消息时触发；
- **无头像网络请求**：界面不拉取任何远程头像，避免流量与解码开销。

服务端：

- `friend_message` 上按 `(conv_type, conv_key, id)`、`to_id`、`from_id`、`group_id` 建索引，轮询与历史查询都走索引；
- 所有查询参数化，令牌只存 `sha256`；
- 消息发送限速（默认 40 条 / 分钟 / 人）；
- 过期令牌在签发时顺手清理。

---

## 5. 自测清单

1. 在 `设置 → 功能` 勾选「显示好友页面」→ 顶部栏出现「好友」；取消勾选 → 顶部栏消失，若正停在好友页会退回启动页。
2. 未登录进入好友页 → 只有「账号」，显示登录 / 注册 / RaySkin 三个入口。
3. 无邀请码注册 → 提示邀请码无效；有邀请码但验证码错误 → 提示验证码不正确。
4. 注册成功后自动登录，账号页显示自己的邀请码。
5. 两个账号互加好友 → 通讯录互现；发消息 → 对方 6 秒内收到；未登录/切页时不应有请求。
6. 建群 → 群成员收到会话；退出群聊 → 群主转移或群解散。
7. 聊天里发送 `P04D2-1A2B3-02000`、`tcp://1.2.3.4:11010`、`https://mc.example.com:25565`、`frp-lxray.com:25565`，消息下方出现对应导入按钮：
   - 邀请码 → 先选版本，再跳到联机页并直接加入；
   - tcp → 联机页自定义节点被填入；
   - http → 弹出游戏版本选择，确认后启动并进入服务器。
8. 账号页「恢复本地聊天记录」→ 关闭启动器、清空 `%TEMP%/RMCL/Friend/cache`，重开后再恢复 → 历史回来。
9. 关闭启动器网络（或断开服务端）→ 已缓存会话仍可浏览，发送会提示失败。

---

## 6. 已知限制

- PHP 共享虚拟主机没有长连接，实时性是「轮询间隔」级别（6 秒）；需要更快可以把 `PageFriendChat` 的 `PollTimer` 调到 2–3 秒。
- 腾讯云 IM 的 REST 接口与自建轮询并存：IM 负责多端推送，客户端当前仍以轮询为准，避免在 WPF 中引入体积较大的 IM SDK。
- RaySkin 一键登录依赖启动页已经登录过一次；没有本地记录时会提示先用邮箱登录。
- 邮箱验证依赖主机 `mail()`，生产环境建议接入 SMTP 插件或改用 `email_enabled=false` + 后台发码。
