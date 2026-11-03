# 构建说明（RMCL 2.19.2.1）

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
| `dist/RMCL 2.19.2.1/RMCL.exe` | 5.68 MB | 主程序，版本信息 `2.19.2.1 / Ray Minecraft Launcher` |
| `dist/RMCL 2.19.2.1/RMCL.exe.config` | 1.5 KB | 绑定重定向 |
| `dist/RMCL-2.19.2.1-win.zip` | 2.55 MB | 上述两个文件的压缩包（sha256 `5e627b0580da…`） |

对应的 Release：<https://github.com/lxraytop/RMCL/releases/tag/v2.19.2.1>，
资产 `RMCL.exe`（5,957,632 B，sha256 `7583b178b9e4…`）与 `RMCL-2.19.2.1-win.zip`（2,672,173 B）。

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
