# 微软正版登录配置指南

RMCL 的正版登录沿用上游 PCL 的实现：**OAuth 2.0 设备代码流（Device Code Flow）**。
启动器自己不需要内置浏览器，也不接触用户密码，只需要一个「应用（客户端）ID」。

相关代码位置：

| 位置 | 作用 |
| --- | --- |
| `Modules/ModSecret.vb` → `OAuthClientId` | 客户端 ID，默认已内置，可用环境变量 `RMCL_MS_CLIENT_ID` 覆盖 |
| `Modules/Minecraft/ModLaunch.vb` → `MsLoginStep1New` | 请求 `devicecode`，拿到 `user_code` 与验证网址 |
| `Pages/PageLaunch/MyMsgLogin.xaml.vb` | 登录弹窗，轮询 `oauth2/v2.0/token` 换取令牌 |
| `ModLaunch.MsLoginStep1Refresh` | 用 `refresh_token` 静默续期（免重复登录） |

请求参数：

```
POST https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode
client_id={OAuthClientId}
tenant=/consumers
scope=XboxLive.signin offline_access
```

> 注意 `tenant=/consumers`：**只支持个人 Microsoft 帐户**，注册应用时必须允许个人账户。

---

## 一、在 Azure 注册应用（一次性，约 3 分钟）

1. 用**任意微软账号**打开 <https://portal.azure.com>，登录后搜索并进入 **Microsoft Entra ID**（旧名 Azure Active Directory）。
2. 左侧选择 **应用注册（App registrations）** → **新注册（New registration）**。
3. 填写：
   - **名称**：随便填，例如 `RMCL Launcher`（用户登录时会看到这个名字，建议起个好认的）。
   - **受支持的帐户类型**：选择 **「仅个人 Microsoft 帐户」**。
     - 若你的账号类型不允许只选个人，可选 **「任何组织目录中的帐户和个人 Microsoft 帐户」**，
       此时仍需保证登录时走 `/consumers`，否则组织账号会在兑换令牌这一步失败。
   - **重定向 URI**：**留空**。设备代码流属于公共客户端，不需要回调地址。
4. 点击 **注册**，在概览页复制 **「应用程序(客户端) ID」**，形如 `ab25b229-f826-46db-b646-01035c3a3e52`。

## 二、开启公共客户端流（关键，漏了就会登录失败）

1. 在该应用的左侧选择 **身份验证（Authentication）**。
2. 拉到最下方 **高级设置** → **允许公共客户端流（Allow public client flows）** → 选择 **是**。
3. 点击顶部 **保存**。

> 不开这一项，兑换令牌时会返回 `AADSTS7000218: The request body must contain ... client_assertion`，
> 表现就是「网页登录成功了，但启动器一直转圈后报错」。

## 三、把客户端 ID 填进启动器

三种方式，任选其一（优先级从高到低）：

1. **环境变量**（不改代码，适合临时测试）：
   ```bat
   set RMCL_MS_CLIENT_ID=你的客户端ID
   RMCL.exe
   ```
2. **改源码默认值**：`Modules/ModSecret.vb`
   ```vb
   Public OAuthClientId As String = If(Environment.GetEnvironmentVariable("RMCL_MS_CLIENT_ID"),
       "你的客户端ID")
   ```
3. 直接改 `OAuthClientId` 的默认字符串（当前仓库已内置 `ab25b229-f826-46db-b646-01035c3a3e52`）。

改完重新编译即可。**不需要生成客户端密码（Secret）**，设备代码流不使用 Secret。

## 四、验证流程

1. 打开 RMCL → 「启动」页 → 登录方式选择 **正版** → 点击登录。
2. 弹窗会显示一个 8 位代码（已自动复制），并自动打开 `https://www.microsoft.com/link`。
3. 在网页里粘贴代码 → 用要登录的微软账号确认 → 授权。
4. 启动器依次完成 6 个步骤：
   1. OAuth 令牌
   2. XBL（Xbox Live）认证
   3. XSTS 授权
   4. 用 XSTS 换取 Minecraft 服务令牌
   5. 拉取玩家档案（UUID / 皮肤 / 披风）
   6. 写入登录缓存

成功后「启动」页头部会显示正版皮肤头像；勾选「记住登录」后，下次启动会用 `refresh_token` 静默续期。

## 五、常见错误对照

| 现象 / 返回 | 原因 | 处理 |
| --- | --- | --- |
| `AADSTS700016` application not found | 应用只允许组织账号，而请求走的是 `/consumers` | 把帐户类型改为允许个人账户 |
| `AADSTS7000218` 需要 client_assertion | 没开「允许公共客户端流」 | 见第二节 |
| `AADSTS50020` 用户帐户不存在于租户 | 用了工作/学校账号登录个人应用 | 换个人微软账号 |
| 登录名带 `#EXT#`，如 `xxx_qq.com#EXT#@xxx.onmicrosoft.com` | 这是 Entra ID 的**外部来宾账号 UPN**，不是 Microsoft 账户，不能用于正版登录 | 见第七节 |
| `authorization_declined` | 用户在网页上点了拒绝 | 重新登录并授权 |
| `expired_token` / 一直 `authorization_pending` | 代码过期（默认 15 分钟） | 关掉弹窗重来 |
| `该账号未拥有 Minecraft` | 账号没有 Minecraft Java 版 | 需先在 minecraft.net 购买/绑定 |
| 提示「需要使用密码登录」 | 账号开启了无密码登录，网页端要求改密码 | 按弹窗引导到 account.live.com 设置密码 |
| 第 4 步 403，返回 `Invalid app registration` | **Minecraft 服务端有 client_id 白名单**，内置应用 ID 未获 Mojang 授权 | 见第六节，与代理/VPN 无关 |

## 六、`Invalid app registration`（第 4 步 403）

现象：微软网页登录成功，第 1～3 步（OAuth → Xbox Live → XSTS）全部通过，
仅在 `POST /authentication/login_with_xbox` 返回 `403 Forbidden`，响应体为：

```json
{
  "path" : "/authentication/login_with_xbox",
  "errorMessage" : "Invalid app registration, see https://aka.ms/AppRegInfo for more information"
}
```

原因：Minecraft 服务端维护一份 **client_id 白名单**，只有被 Mojang 批准过的应用 ID
才能换取 Minecraft 令牌。新建的 Azure 应用默认不在名单内；曾在内、后被移除的同样会报错。
这一限制在 Minecraft 服务端实施，**无法通过 Entra 的权限、作用域、重定向 URI 等配置绕过**，
也**与用户的网络环境、VPN、加速器完全无关**（前 3 步能通即可证明网络是正常的）。

处理：

1. 到 <https://aka.ms/mce-reviewappid> 提交 App ID 登记表，填写自己的 client_id、
   应用名称、项目地址与用途说明，等待 Mojang Enforcement 加入白名单（通常数天到数周）。
2. 临时验证可用环境变量换成已获批准的 ID：
   ```bat
   set RMCL_MS_CLIENT_ID=已获批准的客户端ID
   RMCL.exe
   ```
3. 不要长期借用其他启动器的 client_id，随时可能被移除而导致整体登录失效。

## 七、`#EXT#` 来宾账号不能用于正版登录

有些 Azure 账号在门户里显示的登录名形如：

```
lxray_qq.com#EXT#@lxrayqq.onmicrosoft.com
```

这不是邮箱地址，而是 **Microsoft Entra ID 的外部来宾（Guest / B2B）账号 UPN**：

| 片段 | 含义 |
| --- | --- |
| `lxray_qq.com` | 真实邮箱 `lxray@qq.com`，`@` 被替换为 `_` |
| `#EXT#` | External，表示该身份是从目录外部邀请进来的来宾 |
| `@lxrayqq.onmicrosoft.com` | 该租户的默认域名（首次用非微软域邮箱登录 Azure 时自动创建） |

**它属于「工作或学校账户」，不是 Microsoft 账户（MSA）**，而 RMCL 走的是
`login.microsoftonline.com/consumers/...`，只接受个人 MSA。用它登录必然失败
（通常报 `AADSTS50020`）。含 `#EXT#` 的登录名**不能**填进登录弹窗。

区分用法：

- **AppID 审核表的「联系邮箱」**：填真实邮箱 `lxray@qq.com`，不要填 `#EXT#` 那串。
  官方要拿它到 Azure 门户核对，而门户里该来宾账号的「邮件」字段就是 `lxray@qq.com`。
- **Tenant ID**：填目录的 **GUID**（Entra ID → 概览 → 租户 ID），不是 `xxx.onmicrosoft.com` 域名。
- **登录游戏**：必须用 Microsoft 账户。若 `lxray@qq.com` 已注册过 MSA，
  网页上会让你在「个人账户」与「工作或学校账户」之间选择，**必须选个人账户**；
  若尚未注册，先去 <https://signup.live.com> 用它注册一个 MSA（QQ 邮箱注册 MSA 是允许的）。

> 建议：申请审核时用一个纯个人 Microsoft 账户登录 Azure 门户创建应用，使联系邮箱、
> 租户、应用三者归属一致，核对环节不会有歧义。

## 八、安全提醒

- 客户端 ID 是**公开信息**，写进开源仓库不算泄露；真正需要保密的是客户端 Secret —— 本方案不使用 Secret。
- 登录令牌（`access_token` / `refresh_token`）保存在本机注册表 `HKCU\Software\RMCLDebug` 下，且经过 DES 加密；
  换机器或共享账号时请清理该键。
- 若你的应用被大量滥用，可在 Azure 里删除该应用注册，所有使用此 ID 的客户端会立即失效。
