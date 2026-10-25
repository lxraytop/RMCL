# 构建说明（RMCL 2.18.0.0）

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

启动器内置了「设置 → 功能 → 软件更新」，更新源是 GitHub 的 Release，**只认 `.exe` 资产**：

- 每个 Release **必须直接上传 `RMCL.exe`**（zip 可以照旧保留，但更新器不会用它）；
- 资产名建议就叫 `RMCL.exe`；若同版本有多个 exe，更新器优先选名字里带 `RMCL` 的那个；
- 更新器读取 `tag_name` 与资源大小，tag 需要是 `v2.17.0.4` 这类可被 `Version.TryParse` 解析的形式；
- 下载后会把当前 exe 替换掉：用户点「立即重启安装」会重启，选择「稍后」则在退出启动器时替换，下次启动生效。
- **国内直连 `objects.githubusercontent.com` 经常失败**（实测 10~20 s 后连接中断），更新器已内置 `gh-proxy.com` 反代作为备用下载源（官方地址仍排在最前）。若反代也失效，改 `Modules/ModUpdate.vb` 里的 `AssetMirrors` 即可。

## 五、本地已产出的成品

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `dist/RMCL 2.18.0.0/RMCL.exe` | 5.57 MB | 主程序，版本信息 `2.18.0.0 / Ray Minecraft Launcher` |
| `dist/RMCL 2.18.0.0/RMCL.exe.config` | 1.5 KB | 绑定重定向 |
| `dist/RMCL-2.18.0.0-win.zip` | 2.51 MB | 上述两个文件的压缩包（sha256 `7abd730e…`） |

对应的 Release：<https://github.com/lxraytop/RMCL/releases/tag/v2.18.0.0>，
两个资产 `RMCL.exe`（5,836,288 B）与 `RMCL-2.18.0.0-win.zip`，按 tag 校验均为 `uploaded`。

冒烟验证：解压后直接运行，主窗口标题为 `Ray Minecraft Launcher　`，初始化日志（`RMCL\Log1.txt`）无异常，Java 探测、Mojang 版本列表加载正常。
