# 构建说明（RMCL 2.20.2.2）

RMCL 基于上游 2.14.0.0 开源基线重构，技术栈与上游一致：**.NET Framework 4.8 + WPF + VB.NET**（主工程为传统 vbproj，`RMCLCS` 与 `MeloongCore` 为 SDK 风格 csproj）。

## 一、环境要求

| 组件 | 说明 |
| --- | --- |
| Visual Studio 2022 生成工具 | 需勾选「.NET 桌面开发」工作负载（含 MSBuild 17、Roslyn VB/C# 编译器、WPF 标记编译任务） |
| .NET Framework 4.8 目标包 | `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8` |
| 网络 | 首次编译需还原 NuGet 包（Newtonsoft.Json、Ookii.Dialogs.Wpf、ThrottleDebounce、PolySharp 等） |

本机安装命令（管理员 PowerShell）：

```powershell
winget install --id Microsoft.VisualStudio.2022.BuildTools -e `
  --override "--wait --quiet --norestart --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --includeRecommended --add Microsoft.VisualStudio.Component.Wpf"
```

## 二、编译命令

```powershell
# 1. 还原 NuGet 包
msbuild RMCL.sln -t:Restore -p:Configuration=Release -p:Platform="Any CPU"

# 2. 编译
msbuild RMCL.sln -t:Build -p:Configuration=Release -p:Platform="Any CPU" -p:RestorePackages=false
```

或直接单项目编译（与上游 CI 一致）：

```powershell
msbuild RMCL\RMCL.vbproj -p:Configuration=Release
```

产物路径固定为 `RMCL\bin\RMCL.exe`（`OutputPath` 为 `bin\`，不带配置子目录）。

## 三、产物说明

`RMCL.exe` 为**单文件分发**：Newtonsoft.Json、Ookii.Dialogs.Wpf、CacheCow、NAudio、Imazen.WebP、ThrottleDebounce、RMCLCS、MeloongCore 等依赖通过 `Application.xaml.vb` 中的 `AppDomain.AssemblyResolve` + `My.Resources` 内嵌加载，运行时不需要额外 DLL（`bin` 下出现的 DLL/PDB 只是编译副产物）。仅需随包附带 `RMCL.exe.config`（程序集绑定重定向）。

产物为单文件 `RMCL.exe`，运行时不需要额外 DLL（`bin` 下出现的 DLL/PDB 只是编译副产物）。仅需随包附带 `RMCL.exe.config`（程序集绑定重定向）。

## 四、已知提示

- `warning MSB3884: 无法找到规则集文件 BasicDesignGuidelineRules.ruleset`：仅代码分析规则集缺失，不影响编译与运行；如需消除，在项目属性关闭「启用代码分析」即可。
- CurseForge API Key 已内置默认值于 `Modules/ModSecret.vb`，仍可用环境变量 `RMCL_CURSEFORGE_API_KEY` 覆盖；微软登录 ClientId 同理（`RMCL_MS_CLIENT_ID`）。
- 资源搜索源为 **全部 / CurseForge / Modrinth**（默认「全部」），CurseForge 分支依赖内置 API Key；整合包安装中的 CurseForge 清单（`manifest.json`）解析同样使用该 Key。
- 单例检查使用互斥体 `RMCL_SingletonMutex`：已有实例在运行时再次启动会**以退出码 4 直接退出**，这是正常行为，不是崩溃。

## 四之二、发布约定（重要）

启动器内置了「设置 → 功能 → 软件更新」，2.19 起提供三种更新源（设置项 `UpdateSourceMode`）：

| 值 | 更新源 | 说明 |
| --- | --- | --- |
| 0 | GitHub 原生源 | 检查与下载都直连 `github.com`，下载失败时自动改用镜像 |
| 1 | GitHub 镜像源 | 优先经用户自填的镜像地址（默认 `gh-proxy.com`）下载，失败回退官方 |
| 2 | 官方源（需授权） | 自建更新服务器，验证码授权 + 到期拒绝更新，代码在 `server/` |

### 走 GitHub 时

- 每个 Release **必须直接上传 `RMCL.exe`**（zip 可以照旧保留，但更新器不会用它）；
- 资产名建议就叫 `RMCL.exe`；若同版本有多个 exe，更新器优先选名字里带 `RMCL` 的那个；
- 更新器读取 `tag_name` 与资源大小，tag 需要是 `v2.18.1.1` 这类可被 `Version.TryParse` 解析的形式；
- 下载后会把当前 exe 替换掉：用户点「立即重启安装」会重启，选择「稍后」则在退出启动器时替换，下次启动生效；
- **国内直连 `objects.githubusercontent.com` 经常失败**（实测 10~20 s 后连接中断），镜像源就是为这种情况准备的，地址可在设置里改。

### 走自建官方源时

1. 按 `server/README.md` 把 PHP 服务部署好，进管理后台上传 `RMCL.exe`、填版本号与公告；
2. 在后台签发验证码（一码一设备，可设「激活后 N 天」或「固定到期日」）；
3. 客户端「更新源」选「官方源（需要授权码）」，输入验证码激活；地址内置在 `ModUpdateServer.ServerBaseUrl`，换域名要重发客户端；
4. 授权到期后服务器拒绝 `latest` 与 `ticket` 请求，客户端会提示续期，不会下到一半才失败。

与 GitHub 源的差别：官方源多了 `sha256` 校验（后台发布时自动计算），下载地址是 10 分钟有效的票据，不走任何反代。

客户端实现分布在 `Modules/ModUpdate.vb`（三种源的分发、下载、安装）与 `Modules/ModUpdateServer.vb`（授权、设备标识、票据、错误码映射）。

## 五、本地已产出的成品

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `dist/RMCL 2.20.2.2/RMCL.exe` | 5.91 MB | 主程序，版本信息 `2.20.2.2 / Ray Minecraft Launcher` |
| `dist/RMCL 2.20.2.2/RMCL.exe.config` | 1.5 KB | 绑定重定向 |
| `dist/RMCL-2.20.2.2-win.zip` | 2.63 MB | 上述两个文件的压缩包 |

对应的 Release：<https://github.com/lxraytop/RMCL/releases/tag/v2.20.2.2>，
资产 `RMCL.exe`（6,198,784 B，sha256 `526703eec3b0afa7…`）与
`RMCL-2.20.2.2-win.zip`（2,754,367 B，sha256 `d09abfd9f88df3a4…`）。

### 2.20.2.2 主要变更

好友模块整体切到 v2 网关（`server-v2/`，FastAPI + WebSocket），客户端默认服务地址
`https://immc.lxray.top`，旧的 `action=friend_*` 轮询接口与 `FriendApi(action)` 兼容层全部移除。

- 全面校正 v2 接口契约：修复「加好友」按钮点不动（`relation` 取值没换算）、单聊消息
  `conv_key` 与会话键两套体系导致推送匹配不上、历史检索 `scope` 失效、验证码字段名不符、
  好友申请列表混入已处理记录。
- 实时推送改用 WebSocket：`hello → resume → event`，断线自动重连补拉，移除在线状态、
  好友分组、漫游等体验版不支持的功能；未读红点保留，以服务端为准。
- 修复退出登录崩溃、WS 连上就断（`FriendLastSeq` 未注册导致 `KeyNotFoundException`）。
- 修复清除本地聊天记录后会话卡片仍显示旧消息，切回页面还会把内存副本写回磁盘「复活」。
- 修复两个账号来回切换时未读红点叠加：新登录从 `hello.server_seq` 起步不重放历史事件；
  会话代号 + 主动断开旧 socket 防止双连接重复计数；换号时清空事件队列与各页面缓存。
- 清理旧服务端遗留代码：删掉漫游空实现、恒为 False 的在线字段、只写不读的
  `FriendLastMsgId`，好友请求与历史结果改为强类型。
- 服务端新增 `/admin` 网页管理后台、健康检查诊断，并把 `/docs` 的 Swagger 资源改成本地静态文件
  （国内访问 jsdelivr 会白屏）。
- 接口清单见 `docs/FRIEND-V2-API.md`，宝塔部署见 `server-v2/DEPLOY-BT.md`。

### 2.20.2.1 主要变更

- 修复 RaySkin 一键登录在部分网络下报 `HTTP 405`：这些环境会拦截
  `POST /sessionserver/session/minecraft/join`。现在 join 失败（403 / 404 / 405）会自动降级，
  改为把 `accessToken` 交给好友服务端，由服务端调用 `/authserver/validate` +
  `/sessionserver/.../profile` 完成校验；令牌失效时再用保存的密码重新认证重试一次。
- 一键登录前会探测认证服务器的真实根地址（返回 JSON 含 `skinDomains`），
  自动纠正「只填域名、漏了 `/api/yggdrasil`」这类写法差异；换台电脑不用重新配置。
- RaySkin 的四个请求改用浏览器 UA，并不再携带启动器内部使用的假 Referer，
  减少被站点 WAF 拦截的概率。
- 好友服务地址末尾多余的斜杠已去掉，避免拼出 `https://host//index.php`。
- 404 / 405 的提示会直接给出出错 URL，并把 URL 与响应体前 300 字写入日志，便于定位。
- 服务端 `friend_rayskin_login` 支持 `access_token` 模式，新增 `friend.rayskin_allow_token`
  开关（默认开启），关闭后只允许标准 join 流程。
- 修复已注销账号的会话清不掉：后台删账号时同步删除别人会话里指向他的记录，
  `conversations` 接口也会兜底清理；好友关系解除但账号还在时标记为「已删除的好友」，可右键删除会话。
- 修复本机聊天记录形同虚设：打开会话拉到的历史从未落盘，只有轮询新消息才写缓存，
  导致离线看不到记录、漫游上传恒为 0 条。现在 `MergeRemote` 会把历史批量写入本机缓存。
- 本地删除的聊天记录不会再被云端备份恢复：删除的 id 记入 `FriendDeletedIds`，
  打开会话、恢复历史、漫游恢复都会跳过，服务端 `sync` 也支持 `exclude` 参数。
- 新增聊天记录漫游：默认关闭，单聊任一方开启即长期保存；账号页可对每个会话单独开启、
  上传本机记录、或从云端恢复到本机。上传按 `client_id` 与内容双重去重。
- 修复已读后会话红点不消失：`read` 返回后立即把本地未读清零并重绘，不再等下一次轮询。
- 后台维护页新增「清空所有云端聊天记录」，需输入「清空」二次确认。

### 2.20.2.0 主要变更

- 新增「好友」顶级页面（默认隐藏，设置 → 功能最底部可开启）：账号、消息、通讯录、群聊、历史记录五个子页。
- 账号支持邀请码 + 邮箱验证码注册、邮箱密码登录，以及 RaySkin 一键登录（复用启动页会话，
  多角色账号自动走 `refresh` 选角色，密码不经过好友服务端）。
- 好友与群聊：搜索添加、请求处理、创建群聊、群主可邀请好友入群 / 移出成员；
  通讯录与会话显示在线状态，群会话标题带成员数。
- 消息右键菜单：复制 / 转发 / 撤回 / 删除。撤回限本人 2 分钟内发送的消息，
  服务端标记后对方下次轮询即显示「对方撤回了一条消息」；删除只影响本机。
- 聊天里的内容可一键导入：`Pxxxx-xxxxx-xxxxx` 邀请码（先选版本再进房）、
  `tcp://host:port` 节点、`frp-xxx.com:12345` 与 `http(s)://` 服务器地址（选版本后直接进服）。
- 联机页完成页新增「游戏版本」下拉框与「分享给好友」，进房后仍可换版本再启动，
  也能把当前邀请码直接发到好友或群聊。
- 修复聊天记录会被「清理游戏垃圾」误删：本机缓存从 `%TEMP%\RMCL\Friend` 迁到
  `<启动器目录>\RMCL\Friend\cache`，旧数据自动迁移，清理逻辑显式跳过该目录；
  账号页可查看缓存位置与占用，并「清除本地聊天记录」（只删本机，服务器历史不受影响）。
- 会话列表支持收起 / 展开，状态记忆；聊天气泡改为自己绿色、对方蓝色，适配深浅主题。
- 服务端新增好友模块（`server/lib/friend.php`）：注册登录、邀请码、邮箱验证码（支持 SMTP 直连）、
  RaySkin `hasJoined` 校验、好友与群组、消息落库与历史检索、增量同步；腾讯云 IM 仅做转发，
  未配置时自动降级，聊天记录始终以 SQLite 为准。
- 管理后台「好友」标签页并入 `admin.php?tab=friends`（用户 / 邀请码 / 群组 / 维护），
  `admin-friend.php` 改为 302 兼容跳转；服务地址在客户端硬编码，不可见也不可改。

### 2.20.1.0 主要变更

- 设置里新增「更新」独立子页面：版本信息、一键复制版本信息、更新源与官方源授权、
  检查更新 / 下载并安装 / 立即重启安装、下载进度与更新公告，原来挤在「功能」页里的
  软件更新整块迁出。
- 设置 → 功能新增两项：「创建快捷方式」可在桌面或开始菜单生成指向当前 exe 的快捷方式，
  「下载自定义文件」可填直链下载到指定目录，显示百分比、已下载 / 总大小与实时速度，
  随时可取消，失败或取消都不会留下半个文件。
- 「功能隐藏」补齐当前版本的全部页面与子页面：设置子页面（穿透 / Java / 功能 / 更新等）、
  下载子页面、版本设置子页面（设置 / Mod 管理 / 存档 / 截图 / 资源包 / 光影包 / 导出）
  均可单独隐藏，并按层级分组排列；被隐藏的当前子页面会自动回落到可用页。

### 2.20.0.1 主要变更

- 修复切换资源包 / 光影包时右侧偶发整片白屏且无法恢复：页面切换的收尾回调写在
  动画组的 AaCode 里，而 `AniStart` 遇到同名组会直接 `AniStop` 丢弃旧组，快速连续
  切换时旧页已退场、新页从未挂上；同时 `PageOnEnter` 在 `PageExit` 状态直接抛异常
  （被动画引擎吞掉），在 `ContentEnter` 又直接返回，状态一旦错位就再也进不来。
  现在页面挂载后立刻置 `Opacity = 1`，`PageOnEnter` 按状态自愈，并加了独立的 600ms
  兜底回调；同一分页被重复点击时也会就地恢复显示，不必重启程序。
- 修复版本没有安装任何资源包 / 光影包时页面一片空白：空状态卡片原先被写在
  `PanAllBack` 内部，而「没有资源」时恰好会把 `PanAllBack` 折叠，等于把空状态自己
  一起藏掉。现改为与列表容器平级，会正常显示「这里还没有资源包」与安装按钮。
- 修复资源包 / 光影包读取目录失败后页面永久停在加载卡片、刷新也无效的问题
  （`Scanning` 标志未复位，后续读取全被入口拦掉）。
- 顺带把折叠控件残留的透明度一起复位，避免卡片再次显示时仍全透明。

### 2.20.0.0 主要变更

- 新增版本资源管理页：存档、截图、资源包、光影包四个页面，
  分别支持备份/删除/定位、网格预览、启用禁用与批量操作。
- 修复「打开文件夹」实际打开上级目录的问题，改为直接进入目标目录。
- 修复资源包/光影包页底部操作栏偶发不显示（退场动画把卡片透明度留在 0）。
- 修复备份存档误报「游戏正在运行」（`session.lock` 残留导致），改为实际探测文件占用。
- 默认服务地址切到 HTTPS；联网通知支持按钮与服务端已读。

### 2.19.2.1 主要变更

- 更新公告统一以 GitHub Release 为准：无论当前用的是 GitHub 源还是官方源，
  「更新完成」弹窗展示的都是 GitHub 上该版本的正文；GitHub 取不到时才退回当前源。
- 「发布页 / 查看公告」一律指向 GitHub 的发布页，不再指向自建服务器的首页。
- 修复：服务端没写公告时会先弹一个只有「该版本没有提供更新公告」的空窗；
  现在没有公告就只给一个气泡提示，不打断。
- 移除 PCL 遗留的「内置更新日志」弹窗（`FormMain.ShowUpdateLog`）：
  它每次版本号变化都会弹一个「没有更新日志……」，和真正的 GitHub 公告前后脚出现。

### 2.19.2.0 主要变更

- 修复：用官方源更新完成后看不到该版本的更新公告（服务端新增 `?action=release` 按版本查询）。
- 官方源授权到期、被停用或绑在别的设备时，检查更新会提示输入新的验证码；
  如果选择放弃，会自动切回 GitHub 官方源并继续检查，不会再卡在「官方源用不了」的状态。
- 新增联网通知：公告文案全部由更新服务器下发，客户端不硬编码。启动后延迟 4 秒拉取，
  4 小时内不重复请求；多条同时生效时只弹级别最高的一条，展示后回报触达次数。
- 服务端新增「联网通知」管理页（标题/正文/级别/生效窗口/版本区间/仅一次），
  通知不需要授权即可接收，未激活或已到期的用户同样能收到。

### 2.19.1.0 主要变更

- 新增自建更新源「官方源」：检查与下载更新前需要通过验证码授权，一张码默认绑定一台设备，
  授权到期后服务器直接拒绝，客户端提示续期。
- 更新源改为三选一：GitHub 原生源、GitHub 镜像源（镜像地址可在设置里改）、官方源（需要授权码）；
  下载失败会在镜像与官方之间自动回退。
- 官方源下载使用短时票据，下载完成后校验 SHA-256，文件被替换会直接丢弃。
- 设置页新增授权状态（剩余天数与到期时间）、「输入验证码」「解除本地授权」；
  官方源地址内置在客户端，不在界面上显示，也不可修改。
- 仓库新增 `server/`：PHP 8 + SQLite 的更新服务端，含验证码签发/解绑/续期、
  版本发布（可一键从 GitHub Release 同步 exe 与公告）、按用户统计下载次数。

### 2.19.0.0 主要变更

- 第三方登录改为「多认证服务器 + 多账号」模型：新增加密设置 `AuthServerList`，服务器与账号密码按服务器分别保存，切换服务器不再串号。
- 主页登录方式栏新增「第三方」胶囊，可直接用已登录的会话启动；第三方状态下仍显示切换栏，可随时切回正版/离线。
- 拖拽 Authlib 授权链接：弹窗确认后自动把服务器加入列表并切换，支持拖入 `.txt` / `.json` 授权文件。
- 认证服务器与账号均可在界面上删除（带确认），切换服务器后账号头像与信息立即刷新。
- 默认认证服务器改为 RaySkin（`http://mc.lxray.top/api/yggdrasil`），LittleSkin 作为次要选项保留。
- 修复：切服后首次启动提示密码错误（沿用上一个服务器的密码）。
