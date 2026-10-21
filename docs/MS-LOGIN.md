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
| `authorization_declined` | 用户在网页上点了拒绝 | 重新登录并授权 |
| `expired_token` / 一直 `authorization_pending` | 代码过期（默认 15 分钟） | 关掉弹窗重来 |
| `该账号未拥有 Minecraft` | 账号没有 Minecraft Java 版 | 需先在 minecraft.net 购买/绑定 |
| 提示「需要使用密码登录」 | 账号开启了无密码登录，网页端要求改密码 | 按弹窗引导到 account.live.com 设置密码 |

## 六、安全提醒

- 客户端 ID 是**公开信息**，写进开源仓库不算泄露；真正需要保密的是客户端 Secret —— 本方案不使用 Secret。
- 登录令牌（`access_token` / `refresh_token`）保存在本机注册表 `HKCU\Software\RMCLDebug` 下，且经过 DES 加密；
  换机器或共享账号时请清理该键。
- 若你的应用被大量滥用，可在 Azure 里删除该应用注册，所有使用此 ID 的客户端会立即失效。
