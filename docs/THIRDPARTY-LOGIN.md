# 第三方登录改造方案（多认证服务器 + 直选切换）

> 分支：`feature/thirdparty-login-multiserver`（基于 `main`，基线 `ace15ea` 2.18.2.0）
> 目标：修复「主页无法直接选第三方登录」与「拖拽 authlib 只能覆盖单版本设置、账号密码跨服务器串号」两类问题。

---

## 一、分支与提交规划

| 项 | 内容 |
| --- | --- |
| 分支名 | `feature/thirdparty-login-multiserver`（沿用仓库既有 `feature/xxx` 习惯） |
| 基线 | `main` @ `ace15ea` |
| 提交拆分 | ① `feat(auth): 新增 AuthServerList 多服务器/多账号存储与旧数据迁移`<br>② `feat(auth): 主页登录方式支持直接选择第三方并用已有会话启动`<br>③ `feat(auth): 拖拽 authlib 链接自动切换服务器并入库`<br>④ `fix(auth): 切换认证服务器不再误清登录态；账号密码按服务器隔离`<br>⑤ `docs(auth): 第三方登录改造说明` |
| 版本号 | 合入时 `Modules/Base/ModBase.vb` 的 `VersionCode` +1，`UpgradeSub` 挂一次迁移 |

之所以用 `feature/` 而不是 `fix/`：本次不只是修 bug，还引入了「认证服务器列表」这一新持久化结构，属于功能级改动。

---

## 二、现状定位（问题 → 代码位置）

| 现象 | 根因 | 位置 |
| --- | --- | --- |
| 主页只有「正版 / 离线」两个可点选项，第三方登录只能靠版本设置里锁死 | `PanType` 只放了 `RadioLoginType5`（正版）、`RadioLoginType0`（离线）；`LoginPageType=3/4` 时直接隐藏 `PanType`，只显示静态胶囊 `PanTypeOne` | `Pages/PageLaunch/PageLaunchLeft.xaml:43-56`、`PageLaunchLeft.xaml.vb:289-370` |
| 即使补一个第三方单选，点了也会掉登录态（需重新输密码） | `McInstanceSelected` 的 Setter 在 `VersionServerLogin=4` 且 `VersionServerAuthServer <> CacheAuthServerServer` 时无条件 `Settings.Set("CacheAuthAccess","")`；切换顺序稍错就先清 token 再赋值 | `Modules/Minecraft/ModMinecraft.vb:200-215` |
| 拖拽 authlib 链接只改「当前版本」的四项设置，且每次都弹窗确认 | 拖拽分支只写 `VersionServerLogin/VersionServerAuthServer/VersionServerAuthRegister/VersionServerAuthName`，没有全局服务器清单 | `FormMain.xaml.vb:1018-1060` |
| 拖入授权文件（`file:///`）后提示「无法确定适合的操作」 | `file:///` 分支直接进 `FileDrag`，`FileDrag` 里没有 authlib 文本/元数据识别 | `FormMain.xaml.vb:1056-1060`、`FormMain.xaml.vb:1083-1160` |
| 账号密码跨服务器串号 | `LoginAuthEmail` / `LoginAuthPass` 是全局扁平的 `¨` 分隔列表，按**索引**与密码对齐，与服务器无关；换服务器后列表仍是同一份 | `Pages/PageSetup/Settings.vb:77-78`、`PageLoginAuth.xaml.vb:12-24,57-63`、`ModLaunch.vb:633-660` |
| 会话本身也是全局单例 | `CacheAuthAccess/Client/Uuid/Name/Username/Pass` 只保存「最后一次登录的服务器」，无法同时保留 LittleSkin 与其他服的会话 | `Pages/PageSetup/Settings.vb:61-69` |

---

## 三、数据模型：新增 `AuthServerList`

新增一项设置（加密 + 注册表，与 `CacheAuth*` 同级）：

```vb
New Setting("AuthServerList", "[]", Source:=Sources.Registry, Encrypted:=True)
```

JSON 结构（服务器为一级、账号为二级，天然满足「每个条目账号密码独立」）：

```json
[
  {
    "server": "https://littleskin.cn/api/yggdrasil",
    "name": "LittleSkin",
    "register": "https://littleskin.cn/auth/register",
    "updated": 1762137600,
    "accounts": [
      {
        "user": "someone@example.com",
        "pass": "******",
        "uuid": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
        "name": "Steve",
        "access": "******",
        "client": "******",
        "last": 1762137600
      }
    ]
  }
]
```

新增 `Modules/ThirdParty/ModAuthServer.vb`（或 `Modules/Minecraft/ModAuthServer.vb`）作为唯一读写入口：

| 方法 | 作用 |
| --- | --- |
| `NormalizeServer(url)` | 去首尾空白、去尾部 `/`、剥离尾部 `/authserver`、host 小写 —— 保证「拖拽来的 URL」与「版本设置里的 URL」不会入库成两条 |
| `GetAll()` / `Find(url)` | 读取、按归一化 URL 查条目 |
| `UpsertServer(url, name, register)` | 添加或更新服务器，保留已有 `accounts` |
| `SaveAccount(url, user, pass, uuid, name, access, client)` | 按 `user` 覆盖或插入账号并置 `last` |
| `GetAccounts(url)` | 供账号下拉框使用（按 `last` 倒序） |
| `SetCurrent(url)` | 先 `StashCurrent()` 把在用的 `CacheAuth*` 落回原服务器条目，再把目标服务器的最近账号写回 `CacheAuth*` 与 `CacheAuthServerServer/Name/Register` |
| `MigrateLegacy()` | 幂等迁移：把现有 `LoginAuthEmail/LoginAuthPass` + `CacheAuthServerServer/Name/Register` + `CacheAuth*` 合成首个条目；迁移后 `LoginAuthEmail/Pass` 只读不写 |

`CacheAuth*` 继续作为「当前会话镜像」保留，`ModLaunch`、`ModMinecraft`、皮肤加载（`McSkinGetAddress(UUID, "Auth")`）、`PageLoginAuthSkin` 都不用改签名，改动面可控。

---

## 四、功能一：主页直接选择第三方登录

### 4.1 UI

`PageLaunchLeft.xaml` 的 `PanType` 增加第三个胶囊，列定义由 5 列扩为 7 列（`1*,Auto,Auto,Auto,Auto,1*`），三个胶囊居中：

```xml
<local:MyRadioButton x:Name="RadioLoginType3" Tag="LoginType/3" Grid.Column="4"
                     ColorType="Highlight" Text="第三方" Margin="4,0"
                     LogoScale="1.1" Logo="M834.5 684.1..." />
```

要点：**控件名必须是 `RadioLoginType3`**，这样 `RefreshPage` 末尾
`FindName("RadioLoginType" & LoginType)`（`PageLaunchLeft.xaml.vb:368`）能自动命中 `McLoginType.Auth = 3`，不必额外写映射。

### 4.2 状态机改造（`PageLaunchLeft.xaml.vb`）

1. `PageLaunchLeft_Loaded` 第 109-111 行：除 `Legacy/Ms` 外，若 `LoginType = Auth` 且当前版本 `LoginPageType = 0`，勾选 `RadioLoginType3`。
2. `RefreshPage` 的 `Case 0` 分支改为三态判定，并把选择回写 `LoginPageType`：
   - `RadioLoginType5` → `Ms` / `MsSkin`
   - `RadioLoginType0` → `Legacy`
   - `RadioLoginType3` → `Net`（= `Auth`）；`CacheAuthAccess = ""` 时用 `PageType.Auth`，否则 `PageType.AuthSkin`；再 `Settings.Set("LoginType", McLoginType.Auth)`
   - **关键：不要把 `VersionServerLogin` 改成 4**。只在 `LoginType` 层记录选择、`LoginPageType` 记忆上次选择，这样 `McInstanceSelected` Setter 的重判分支不会触发，登录态得以保留；版本设置里「服务器选项」的锁定语义也不被破坏。
3. `RadioLoginType_Change` 追加 `RadioLoginType3.Check`，鼠标触发时 `RefreshPage(True, True)`。
4. 新增 `SwitchAuthServer(Server As String)`：
   - `NormalizeServer` → `AuthServerStore.UpsertServer` → `SetCurrent`（含 stash 当前会话）
   - 依次写 `CacheAuthServerServer / Name / Register`，最后才写 `LoginType`
   - 调用 `RefreshPage(False, True)` + `SkinAuth.Start()` 刷新头像与昵称
   - 全程 `AniControlEnabled += 1` 包裹，避免控件事件把 `CacheAuthAccess` 清空

### 4.3 「点一下就进游戏」的判定

已有会话时链路应完全免密：

```
点「第三方」→ LoginType=Auth、PageType.AuthSkin
   → McLoginAble() 因 CacheAuthAccess<>"" 返回 ""（ModLaunch.vb:428-431）→ 启动按钮可用
   → McLoginInput() 走 PageLoginAuthSkin.GetLoginData()，BaseUrl = CacheAuthServerServer + "/authserver"
   → McLoginServerStart 先 /validate 通过 → 直接进游戏
```

Token 过期时退化为 `/refresh`，再不行才用列表里该服务器保存的密码做普通登录——这正是「账号密码按服务器独立保存」带来的收益。

需要额外加的一致性保护：`McLoginServerStart` 入口校验 `Input.BaseUrl` 的归一化前缀与 `CacheAuthServerServer` 是否一致，不一致则不使用缓存的 `CacheAuthAccess`（避免把 A 服 token 发给 B 服导致 403 后再走密码登录的 10 秒等待）。

### 4.4 静态胶囊的增强（可选，建议同批做）

`Case 4` 时 `LabTypeOne` 现在只显示服务器名；改为 `「服务器名 · 角色名」`，并让 `PanTypeOne` 可点击弹出服务器列表（复用 `MyMsgBoxSelect` + `MyRadioBox`），实现「版本锁定第三方服，但可在多服之间切换角色」。

---

## 五、功能二：拖拽登录自动切换 + 服务器入库

### 5.1 拖拽链路改造（`FormMain.xaml.vb`）

`FrmMain_Drop` 的 `authlib-injector:yggdrasil-server:` 分支重写为：

1. `StringUtils.FormUrlUnescape` 解码 → `ValidateHttp` 校验 → `NormalizeServer`。
2. `AuthServerStore.UpsertServer(url, name, register)` 入库（已存在则只更新 `name/register`，不清 `accounts`）。
   - `name` 解析顺序：`littleskin.cn → "LittleSkin"` 等内置映射；未命中时直接用域名（**不联网取 `meta.serverName`**，避免拖拽时阻塞 UI 线程；需要更友好的名字可在 `ServerNames` 表里追加）。
   - `register`：`url.Replace("/api/yggdrasil", "/auth/register")`，取不到就留空（`ReloadRegisterButton` 已按 URL 有效性隐藏按钮）。
3. 自动切换：`Target = If(PageCurrent = InstanceSetup, PageInstanceLeft.Instance, McInstanceSelected)`；
   - 有版本：`Settings.Set("VersionServerLogin", 4, Instance:=Target)` → `SwitchAuthServer(url)`
   - 无版本：仍入库 + `SetCurrent(url)`，提示「已添加服务器，下载游戏后即可使用」
4. 文案：去掉原 `MyMsgBox` 确认框（拖拽是明确意图），改为 Green `Hint`：「已添加并切换到 LittleSkin 登录（共 2 个服务器）」。
5. 刷新：`PageCurrent = InstanceSetup` 时 `FrmInstanceSetup.Reload()`；`PageCurrent = Launch` 时 `FrmLaunchLeft.RefreshPage(True, True)`。

### 5.2 支持拖入文件

`file:///` 分支与 `FileDrop` 分支最终都进 `FileDrag`，在 `FileDrag` 的「主页 → Mod → 整合包」判断之前插入：

```vb
' authlib 授权信息：纯文本链接 / .txt / .json（meta.serverName）
If {"txt", "json", ""}.Contains(Extension) Then
    Dim Text = FileUtils.ReadFile(FilePath, Encoding.UTF8).Trim
    If Text.StartsWithF("authlib-injector:yggdrasil-server:") Then AuthlibDrop(Text) : Return
End If
```

把 5.1 的逻辑抽成 `AuthlibDrop(RawText)` 供 `FrmMain_Drop` 与 `FileDrag` 共用，避免两处重复。

### 5.3 账号密码按服务器独立保存

| 位置 | 现状 | 改为 |
| --- | --- | --- |
| `PageLoginAuth.Reload` | `ComboName.ItemsSource = LoginAuthEmail.Split("¨")` | `AuthServerStore.GetAccounts(CurrentServer)` 的 user 列表；一个账号都没有时 `ItemsSource = Nothing` |
| `PageLoginAuth.ComboName_SelectionChanged` | 按 `SelectedIndex` 从全局 `LoginAuthPass` 取密码 | 从同一服务器条目的 `accounts(index).pass` 取，`LoginRemember` 关闭时置空 |
| `PageLoginAuth.xaml` | 只有账号/密码两行 | 顶部加服务器选择行（`MyComboBox`，`ItemsSource = GetAll().name`，切换即 `SwitchAuthServer`） |
| `ModLaunch.McLoginServerStart` 的 `LoginFinish` 段 | 把账号密码写进扁平 `LoginAuthEmail/Pass` | 改为 `AuthServerStore.SaveAccount(当前 BaseUrl, UserName, Password, …)`；`LoginAuthEmail/Pass` 仅在 `MigrateLegacy` 中读取 |
| `PageLoginAuthSkin.ExitLogin` | 清 `CacheAuthAccess/Uuid/Name` | 追加：清当前服务器条目的 `access/client`（保留 `user/pass`，下次一键重登） |
| `PageLoginAuthSkin.Reload` | 显示 `CacheAuthName` | 追加显示 `CacheAuthServerName`，便于确认当前在哪个服 |

---

## 六、受影响模块清单

| 文件 | 改动性质 | 说明 |
| --- | --- | --- |
| `RMCL/Modules/ThirdParty/ModAuthServer.vb` | **新增** | `AuthServerList` 读写、归一化、迁移、会话切换 |
| `RMCL/Pages/PageSetup/Settings.vb` | 修改 | 注册 `AuthServerList` |
| `RMCL/Pages/PageLaunch/PageLaunchLeft.xaml` | 修改 | `PanType` 增 `RadioLoginType3`，列定义调整 |
| `RMCL/Pages/PageLaunch/PageLaunchLeft.xaml.vb` | 修改 | `RefreshPage` 三态、`RadioLoginType_Change`、`PageChangeToLaunching` 文案、`SwitchAuthServer` |
| `RMCL/Pages/PageLaunch/PageLoginAuth.xaml(.vb)` | 修改 | 服务器选择行 + 账号列表按服隔离 |
| `RMCL/Pages/PageLaunch/PageLoginAuthSkin.xaml.vb` | 修改 | 显示服务器名、退出登录只清当前会话 |
| `RMCL/Modules/Minecraft/ModLaunch.vb` | 修改 | `McLoginAble/McLoginInput` 支持 Auth 直选、`McLoginServerStart` 校验 BaseUrl 归属、登录成功回写列表 |
| `RMCL/Modules/Minecraft/ModMinecraft.vb` | 修改 | `McInstanceSelected` Setter 的 token 清除条件收紧（加 `LoginType=Auth` 与归一化比较、`AniControlEnabled` 守护） |
| `RMCL/FormMain.xaml.vb` | 修改 | `FrmMain_Drop` authlib 分支重写、`FileDrag` 增 authlib 文本识别、`AuthlibDrop` 抽公共方法 |
| `RMCL/Pages/PageInstance/PageInstanceSetup.xaml(.vb)` | 可选修改 | 服务器选项改为从列表选择（保留手填） |
| `RMCL/Modules/Base/ModBase.vb` + `FormMain.UpgradeSub` | 修改 | 版本码 +1 与一次性迁移调用 |
| `docs/THIRDPARTY-LOGIN.md` | 新增 | 本文件 |

---

## 七、验证方式

### 7.1 编译

```powershell
msbuild RMCL.sln -t:Restore -p:Configuration=Release -p:Platform="Any CPU"
msbuild RMCL.sln -t:Build   -p:Configuration=Release -p:Platform="Any CPU" -p:RestorePackages=false
```

要求：0 error；`MSB3884`（规则集缺失）为既有告警可忽略；产物 `RMCL\bin\RMCL.exe` 可启动、标题正常。

### 7.2 功能用例

| # | 场景 | 步骤 | 期望 |
| --- | --- | --- | --- |
| 1 | 已有 LittleSkin 会话直选 | 先在版本设置里用 LittleSkin 登录一次并退出启动器，重开 → 主页点「第三方」 | 立刻显示角色头像与「LittleSkin · 角色名」，启动按钮可用，点启动**不弹账号密码**直接进游戏 |
| 2 | 会话过期免密重登 | 手动改系统时间或等待 token 过期 → 点「第三方」→ 启动 | 自动 refresh，失败后使用列表保存的密码登录，全程无需输入 |
| 3 | 从未用过第三方 | 全新配置，主页点「第三方」 | 进入账号页，顶部服务器下拉为空并提示「拖拽授权链接或手动添加服务器」，不崩溃、不写脏数据 |
| 4 | 拖拽切服 | 从 LittleSkin 站点拖 `authlib-injector:yggdrasil-server:...` 到窗口 | 无弹窗；Hint 提示已添加并切换；主页胶囊变为「LittleSkin」；`AuthServerList` 中仅一条 LittleSkin |
| 5 | 拖拽去重 | 同一链接连拖 3 次 | 列表仍 1 条，`accounts` 不丢，`updated` 刷新 |
| 6 | 拖入文件 | 把含该链接的 `.txt` / 浏览器下载的链接文件拖入 | 与用例 4 等价 |
| 7 | 多服独立账号 | 添加服务器 A（账号 a@x）与 B（账号 b@y），分别登录 | A 的账号下拉只有 a@x、密码为 A 那条；切到 B 后为 b@y；两侧互不串号 |
| 8 | 切换不掉登录态 | A 已登录 → 切到 B → 再切回 A | 回 A 时仍是原会话（`CacheAuthAccess` 未清），头部按钮直接可启动 |
| 9 | 版本锁定兼容 | 版本设置里登录方式为「第三方」，主页显示静态胶囊 | 胶囊文案含服务器名与角色名（4.4），点击可切服；行为与改造前兼容 |
| 10 | 无版本时拖拽 | 未下载任何版本时拖入链接 | 提示需先下载游戏，但服务器已入库，下载后可在账号页选中 |

### 7.3 回归

- 正版登录、离线登录、统一通行证三条链路不受影响（尤其 `LoginType` 全局值在重启后的恢复）。
- 版本设置 → 服务器选项：切换登录方式后 `VersionServerLogin` 写回正确，`OnVersionServerLoginChanged` 不误触发清 token。
- 皮肤加载：`SkinAuth` 读取 `CacheAuthName/Uuid` 正常，切服后头像随之刷新。
- 旧用户升级：`CacheAuthServerServer + LoginAuthEmail/Pass` 被迁移为首个条目；迁移只执行一次（`AuthServerListMigrated` 标记）。

### 7.4 日志与数据检查

- `RMCL\Log1.txt` 关键字：`Authlib 拖拽`、`已切换到认证服务器`、`服务器改变，要求重新登录` 出现次数符合预期（正常切换不应出现第三条）。
- 注册表 `HKCU\Software\RMCL`（或 `Setup.ini`，视 `Sources.Registry` 落盘方式）中 `AuthServerList` 为合法 JSON、账号密码为密文。

---

## 八、风险与暂不做

| 风险 | 处理 |
| --- | --- |
| `CacheAuth*` 是 DPAPI 加密的全局镜像，多处（皮肤、`McLoginName`）依赖它 | 保留镜像语义，仅新增列表存储；切换时做 stash/restore，避免大范围改签名 |
| 三个胶囊在 317px 宽度下可能挤 | 先按 `Margin="3,0"` + `LogoScale=1.1` 排版，必要时把「第三方」缩为「三方」，或改为「正版 / 离线 / 第三方」等宽三列 |
| 服务器改名（元数据请求失败）导致列表出现 `littleskin.cn` | 内置 hosts→name 映射表，命中即用，按需追加 |
| 一次拖入多个链接 | 只取第一条，其余忽略并 Hint 说明（与现有 `FileDrag` 单文件语义一致） |
| 服务端强制指定登录方式的版本 | `LoginPageType` 为 1/2/3 的版本**不显示**第三方胶囊，保持服主锁定语义 |

---

## 九、实现记录（已落地）

分支 `feature/thirdparty-login-multiserver` 上的实际改动：

| 文件 | 状态 | 内容 |
| --- | --- | --- |
| `RMCL/Modules/ThirdParty/ModAuthServer.vb` | 新增 | `AuthServerNormalize / AuthServerUpsert / AuthServerSaveAccount / AuthServerGetAccounts / AuthServerSetAccount / AuthServerSetCurrent / AuthServerStashCurrent / AuthServerClearSession / AuthServerMigrate / AuthlibDrop` |
| `RMCL/Pages/PageSetup/Settings.vb` | 改 | 新增 `AuthServerList`（加密）、`AuthServerListMigrated` |
| `RMCL/Pages/PageLaunch/PageLaunchLeft.xaml` | 改 | `PanType` 由 5 列改 6 列，新增 `RadioLoginType3`（第三方，卡片图标） |
| `RMCL/Pages/PageLaunch/PageLaunchLeft.xaml.vb` | 改 | `RefreshPage` 三态判定、`EnsureAuthServerForLogin`、`PanTypeOne_Click`（多服切换）、启动页文案显示服务器名、`RadioLoginType_Change` 增第三方 |
| `RMCL/Pages/PageLaunch/PageLoginAuth.xaml(.vb)` | 改 | 新增「服务器」下拉；账号与密码改按服务器条目读取；切服、切账号均落库；`IsReloading` 防止程序化赋值触发清 token |
| `RMCL/Pages/PageLaunch/PageLoginAuthSkin.xaml.vb` | 改 | `GetLoginData` 用 `AuthServerCurrent()`；退出登录只清当前服务器会话（保留账号密码） |
| `RMCL/Modules/Minecraft/ModLaunch.vb` | 改 | `McLoginAble / McLoginInput` 加「会话必须属于当前服务器」判定；`McLoginServerStart` 换服时先 stash、不同服不复用缓存 token；登录成功按 Token 分流（Auth 写列表，Nide 保留旧扁平列表） |
| `RMCL/Modules/Minecraft/ModMinecraft.vb` | 改 | `McInstanceSelected` 的 Authlib 重判改为归一化比较；换服时 stash + 载入目标服务器账号，不再粗暴清 `CacheAuthAccess` |
| `RMCL/FormMain.xaml.vb` | 改 | 拖拽文本走 `AuthlibDrop`；`FileDrag` 支持拖入 `.txt/.json` 授权文件 |
| `RMCL/RMCL.vbproj` | 改 | 登记新源码文件 |

### 关键行为

- 主页点「第三方」：有服务器就直接用它的最近会话（`AuthSkin`），启动免密；一个服务器都没有时自动以 LittleSkin 兜底并给出提示。
- 拖拽 `authlib-injector:yggdrasil-server:...`：不再弹确认框，直接入库 + 切到该服务器 + 为该版本开启第三方登录，Hint 提示结果；同链接重复拖拽不会产生重复条目。
- 账号隔离：`AuthServerList` 里每个服务器各自一组账号，密码与 token 都挂在该条目下，切服不会串号；退出登录只清会话不清密码。

### 构建验证说明

- 本机 `dotnet build` **不适用于**本工程：旧式 `vbproj` 的 WPF 标记编译依赖 Visual Studio 的 `Microsoft.WinFX.targets`，用 dotnet SDK 编译不会生成 XAML 设计器字段，会产出 500+ 个 `BC30506 / BC30590`（全是「找不到 WithEvents 变量 / 事件」的假报错），与代码本身无关。
- 本次采用「复用 `obj/Release` 中既有 `.g.vb` + 临时补上 `ComboServer` / `RadioLoginType3` 两个设计器字段」的方式做了完整 VB 类型检查：**0 error，仅 2 个既有警告**（`MSB3884` 规则集缺失、`BC42305` 重复 XML 注释）。
- 正式出包仍需在装有 VS 2022 BuildTools 的环境执行 `docs/BUILD.md` 里的构建命令（本会话的沙箱策略禁止在终端直接调用该编译器可执行文件）。

---

## 十、追加修复：第三方登录时保留登录方式切换栏

### 现象

版本设置为「第三方登录」（`VersionServerLogin = 4`）时，`RefreshPage` 走 `Case 4`：只显示一行静态胶囊（服务器名），并把 `PanType` 整块隐藏。于是主页看不到「正版 / 离线 / 第三方」的切换栏。

由于拖拽授权链接会写 `VersionServerLogin = 4`，**拖过一次之后就再也切不回正版或离线**，只能进版本设置改。

### 顺带发现的启动崩溃隐患

| 环节 | 判定依据 | 后果 |
| --- | --- | --- |
| 下载 `authlib-injector.jar` | `VersionServerLogin = 4`（`ModMinecraft.vb:1810`） | 从主页胶囊选第三方（版本仍为 0）时不下载 jar |
| 启动注入 `-javaagent` | `McLoginLoader.Output.Type = "Auth"`（`ModLaunch.vb:1268`） | 照常注入 |

两者不一致 → **注入指向不存在的 jar，启动失败**。另外注入用的服务器地址只读 `VersionServerAuthServer`，版本设置为空时同样是空地址。

### 改动

1. `RefreshPage` 的 `Case 4` 改为显示胶囊栏（第三方选中、`PanTypeOne` 隐藏），ToolTip 提示当前认证服务器；会话不匹配时回退到账号页。
2. 主页点「第三方」时同步写 `VersionServerLogin = 4`，版本设置页与实际登录方式保持一致；在第三方状态下点「正版 / 离线」，`RadioLoginType_Change` 把版本设置写回 `0`。
3. 启动注入的服务器地址改用 `AuthServerCurrent()`（版本设置优先，回退到会话所属服务器）。
4. `authlib-injector.jar` 的下载判定加兜底：当前实例且 `LoginType = Auth` 时也下载。
5. `PageLaunchLeft_Loaded` 不再无条件按全局 `LoginType` 选中第三方胶囊，只有版本本身是第三方时才默认选中，避免把别的版本用过的登录方式强加到当前版本。

### 验证

- 主页在第三方状态下能直接切回正版 / 离线，切回后版本设置的「服务器选项」同步变回「正版或离线」。
- 从主页胶囊首次选第三方 → 启动前会下载 `authlib-injector.jar`，不再报找不到 jar。
- 已有会话时点第三方仍免密进入；无会话时落到账号页。

---

## 十一、登录页三项调整

### 1. 恢复拖拽提示弹窗

`AuthlibDrop` 在入库前先弹确认框（`MyMsgBox`），内容包含目标版本、服务器名与地址：

```
是否要在版本 XXX 中添加并切换到第三方登录？
认证服务器：LittleSkin
服务器地址：https://littleskin.cn/api/yggdrasil
```

取消则整条链路不执行（不入库、不切换）；确定后才添加 + 切换 + Hint 提示。拖入 `.txt` 授权文件走同一入口，因此同样会弹窗。

### 2. 展示当前认证服务器

`PanType`（登录方式胶囊）与其下方一行文本一起包进 `StackPanel`，新增 `LabAuthServer`：

- 内容：`LittleSkin（littleskin.cn） · Steve`，即「服务器名（域名） · 当前角色」；
- 只在登录方式为第三方时显示（`LoginType = Auth`），正版 / 离线 / 统一通行证下自动折叠，不影响原有高度；
- 点击这一行可在多个认证服务器之间切换（`MyMsgBoxSelect`），弥补 `Case 4` 不再显示静态胶囊后丢失的切服入口；
- ToolTip 显示完整服务器地址。

### 3. 切换后头像与账号信息即时刷新

根因：`RefreshPage` 在「目标页面 == 当前页面」时直接 `Return`，切服或登录完成后既不重刷页面内容，也不重新拉取皮肤，于是要等启动游戏、页面被重建时才更新。

改为：页面相同时先 `PageGet(PageCurrent).Reload(KeepInput)`，再在 `AuthSkin` 页触发 `SkinAuth.Start()`；把 `SkinAuth` 的输入（`CacheAuthName` / `CacheAuthUuid`）变化交给 LoaderTask 的 `ShouldStart` 判定，输入不同即重新加载。

### 验证

- `BUILD OK`；`RMCL.exe --test` 退出码 123（自带测试成功码），主页面含新标签正常加载。
- 拖拽 → 弹窗取消：服务器不入库、登录方式不变。
- 拖拽 → 弹窗确定：切换到该服务器，头像与「服务器 · 角色」立即变化，无需启动游戏。

---

## 十二、修复：下拉列表里的 × 删不掉账号

编辑态 `MyComboBox` 用共享模板 `ComboBoxItemTemplateWithDelete`（`Application.xaml:360`），每个下拉项右侧有一个 × 按钮，点击走 `Application.xaml.vb` 的 `MyIconButton_Click`。

该处理器只认旧的三份数据：

| 登录方式 | 删除目标 |
| --- | --- |
| 正版 | `LoginMsJson` |
| 离线 | `LoginLegacyName` |
| 第三方 | `LoginAuthEmail` / `LoginAuthPass`（扁平列表） |

第三方账号现在存于 `AuthServerList`，处理器删的是已经不再写入的旧扁平列表，于是 × 点下去什么也没删，下次 Reload 账号又回来了；顺带还会把 `ItemsSource` 换成空的旧列表。

### 改动

- 新增 `AuthServerRemoveAccount(Server, UserName)`：从 `AuthServerList` 对应服务器条目里移除该账号（账号、密码、会话一并删除）；若删的正是当前正在使用的账号，同时清空 `CacheAuth*` 镜像，避免拿着已删账号的 token 去启动。
- `MyIconButton_Click` 的 `Case Else` 先按 `Token` 分流：`Auth` 走新逻辑（删列表 → 重建账号下拉 → 刷新主页登录方式与头像），`Nide` 保持原有扁平列表逻辑。
- 删除后立即 `FrmLaunchLeft.RefreshPage(True, False)`，胶囊下方那行「服务器 · 角色」同步更新。

### 验证

- 下拉里点 × → 账号从列表消失，重启启动器后不再出现。
- 删除当前正在使用的账号 → 回到账号密码页，启动按钮不再拿旧令牌直接进游戏。
- 同一服务器下其他账号不受影响；其他服务器的账号也不受影响。

---

## 十三、默认服务器改为 RaySkin + 删除确认

### 1. 删除账号前必须确认

`MyIconButton_Click`（账号下拉的 × 按钮）在动手前统一弹确认框：

```
确定要删除保存的账号 xxx 吗？
该账号的密码与登录状态会一并删除，下次需要重新输入，且无法恢复。
[删除] [取消]
```

三种登录方式（正版 / 离线 / 第三方）都走这一处，行为一致。

### 2. 默认认证服务器改为 RaySkin

| 位置 | 改动 |
| --- | --- |
| `ModAuthServer` | 新增常量 `DefaultAuthServer = http://mc.lxray.top/api/yggdrasil`、`DefaultAuthServerName = RaySkin`、`LegacyAuthServer = https://littleskin.cn/api/yggdrasil`；`ServerNames` 增加 `mc.lxray.top → RaySkin` |
| `AuthServerEnsureDefault` | 一次性把 RaySkin 写进列表（设置项 `AuthServerRaySkinAdded` 记录）。玩家删掉后**不会**自动加回来 |
| `AuthServerMigrate` | 迁移旧账号前先 `AuthServerEnsureDefault()`，全新配置也有可用服务器；旧账号没记录服务器时仍按 LittleSkin 归属，避免张冠李戴 |
| `EnsureAuthServerForLogin` | 当前没有服务器时，优先选 RaySkin，其次列表第一台；列表为空则退回离线（不再凭空创建） |
| 版本设置 → 服务器选项 | 主按钮「设置为 RaySkin」，次按钮「设置为 LittleSkin」；下拉项文案改为「第三方登录：Authlib Injector（RaySkin / LittleSkin）」；首次切到第三方时自动填入 RaySkin 三项 |

> 注册链接按「`/api/yggdrasil` → `/auth/register`」推导为 `http://mc.lxray.top/auth/register`。若实际路径不同，改 `AuthServerGuessRegister` 或在版本设置里手填即可。

### 3. 服务器下拉框与邮箱下拉框一致，支持删除

- 条目改用与账号列表相同的 `MyListItem` + 右侧 × 图标（`AuthServerItemTemplate`），通过 `ItemContainerStyle` 只作用于下拉项，闭合状态仍显示纯文本。
- × 的点击走 `PageLoginAuth.BtnServerDelete_Click`：先弹确认（会提示该服务器下有多少账号会被一并删除），再 `AuthServerRemoveServer`。
- 删除当前正在使用的服务器：自动切到剩下的第一台并载入它的账号；一台不剩时清空会话与当前版本的服��器设置，主页退回离线。
- 删除非当前服务器：只移除该条目，不影响正在使用的登录状态。

### 验证

- `BUILD OK`；`--test` 退出码 123。
- 账号下拉点 × → 弹确认 → 取消无变化；确定后账号消失且不再出现。
- 服务器下拉点 × → 弹确认 → 删除后列表刷新；删当前服务器会切到另一台。
- 删除 RaySkin 后重启启动器，它不会自动回来。

---

## 十四、修复：切服后首次启动提示密码错误

### 现象

A、B 两个认证服务器都保存了同一个邮箱但密码不同。当前登录 A，拖入 B 的授权链接切换后，**第一次**点启动提示「密码错误」，**第二次**正常。

### 根因

切服时 `AuthServerSetCurrent` 已经把 B 的账号密码写进 `CacheAuth*`，但页面刷新走的是 `RefreshPage(KeepInput:=True)` → `PageLoginAuth.Reload(True)` → `RefreshAccountList(True)`，`KeepInput` 只更新下拉框列表、**保留密码框里的旧内容**。邮箱相同更让"看起来没变"，于是首次启动拿的是 A 的密码去登录 B。

失败后 `McLoginServerStart` 的「密码错误」分支会调用 `PageLoginAuthSkin.ExitLogin()`，它顺带 `RefreshPage(False, …)`，密码被重置成 B 的值，所以第二次就成功了 —— 这正是「第一次错、第二次对」的由来。

### 改动

1. `PageLoginAuth` 增加 `LastServer` 字段：`RefreshAccountList` 发现当前认证服务器与上次填充时不同，**强制丢弃 KeepInput**，重新填入该服务器自己的账号与密码。这样无论从哪条路径切服都不会残留旧输入。
2. `AuthlibDrop` 刷新主页时改用 `RefreshPage(False, True)`，切服即重置输入。

### 验证

- 两个服务器用同一邮箱、不同密码：A → 拖拽切到 B → 第一次启动就应成功（不再出现密码错误）。
- 切服后密码框内容应变为 B 自己保存的密码（或为空，等待输入）。
- 日志出现「认证服务器已切换，重置账号输入：A → B」。






