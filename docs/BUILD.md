# 构建说明（RMCL 2.14.0.0）

RMCL 基于 PCL 2.14.0.0 正式版重构，技术栈与上游一致：**.NET Framework 4.8 + WPF + VB.NET**（主工程为传统 vbproj，`RMCLCS` 与 `MeloongCore` 为 SDK 风格 csproj）。

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

`RMCL\Resources\Help.zip` 为内置帮助文档，上游 CI 会从 `LTCatt/PCL2Help` 重新克隆覆盖；仓库内已包含一份可用副本，本地编译无需额外步骤。

## 四、已知提示

- `warning MSB3884: 无法找到规则集文件 BasicDesignGuidelineRules.ruleset`：仅代码分析规则集缺失，不影响编译与运行；如需消除，在项目属性关闭「启用代码分析」即可。
- 未配置密钥时，正版登录与 CurseForge API 不可用（`Modules/ModSecret.vb` 中通过环境变量 `RMCL_MS_CLIENT_ID`、`RMCL_CURSEFORGE_API_KEY` 注入），启动时会弹出「开源版本说明」提示，属上游既有行为。
- 单例检查使用互斥体 `RMCL_SingletonMutex`：已有实例在运行时再次启动会**以退出码 4 直接退出**，这是正常行为，不是崩溃。

## 五、本地已产出的成品

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `dist/RMCL 2.14.0.0/RMCL.exe` | 5.36 MB | 主程序，版本信息 `2.14.0.0 / Ray Minecraft Launcher` |
| `dist/RMCL 2.14.0.0/RMCL.exe.config` | 1.5 KB | 绑定重定向 |
| `dist/RMCL-2.14.0.0-win.zip` | 2.42 MB | 上述两个文件的压缩包 |

冒烟验证：解压后直接运行，主窗口标题为 `Ray Minecraft Launcher　`，初始化日志（`RMCL\Log1.txt`）无异常，Java 探测、Mojang 版本列表加载正常。
