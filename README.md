# Ray Minecraft Launcher

[![Stars](https://img.shields.io/github/stars/lxraytop/RMCL?style=flat&label=Stars&labelColor=444444&color=eac54f)](https://github.com/lxraytop/RMCL)
[![Issues](https://img.shields.io/github/issues/lxraytop/RMCL?style=flat&label=Issues&labelColor=444444&color=1F883D)](https://github.com/lxraytop/RMCL/issues)

**RMCL**（Ray Minecraft Launcher）是一个 Minecraft 启动器，基于 [PCL（Plain Craft Launcher）](https://github.com/Meloong-Git/PCL) 开源代码的 **2.14.0.0 正式版**进行品牌重构。

包含 UI 库、动画模块、下载模块、Minecraft 启动模块等完整实现，功能与版本特性与上游 2.14.0.0 保持一致。

<br/>

### 项目结构

| 目录 | 说明 |
| --- | --- |
| `RMCL/` | 启动器主工程（VB.NET + WPF，程序集 `RMCL.exe`，根命名空间 `RMCL`） |
| `RMCLCS/` | C# 支撑库（Java 检测、版本清单、资源管理），程序集 `RMCLCS.dll` |
| `MeloongCore/` | 底层通用库（已随仓库提供，无需额外拉取子模块） |
| `.github/` | 构建流水线与 Issue 模板 |

<br/>

### 构建

环境要求：Windows + Visual Studio 2022（或 MSBuild）、.NET Framework 4.8 开发工具包。

```bash
git clone https://github.com/lxraytop/RMCL.git
cd RMCL
dotnet restore RMCLCS/RMCLCS.csproj
msbuild "RMCL\RMCL.vbproj" -p:Configuration=Release
```

产物为 `RMCL\obj\Release\RMCL.exe`；也可直接打开 `RMCL.sln` 在 Visual Studio 中编译。

需要注意：

- 微软登录所需的 `OAuthClientId` 与 CurseForge 的 `CurseForgeAPIKey` 默认从环境变量
  `RMCL_MS_CLIENT_ID`、`RMCL_CURSEFORGE_API_KEY` 读取，未设置时相应功能不可用（与上游开源基线一致）。
- 该开源基线不包含自动更新、赞助解锁与匿名上报的实现。

<br/>

### 品牌重构说明

相对上游 2.14.0.0，本项目完成品牌重构，功能逻辑未作改动：

- 产品名：`PCL` / `Plain Craft Launcher` → `RMCL` / `Ray Minecraft Launcher`
- 程序集、命名空间、工程与解决方案文件名 → `RMCL` / `RMCLCS`
- 运行数据目录：`PCL\` → `RMCL\`（`%AppData%`、`%ProgramData%`、版本目录内的 `RMCL\Setup.ini` 等）
- 图标、启动器 Logo 与内嵌字体族名（`RMCL English`）均已重制
- 版本忽略标记 `.pclignore` → `.rmclignore`；自定义主页替换标记 `{pcl_*}` → `{rmcl_*}`（同时保留 `{pcl_*}` 别名，兼容社区已有主页模板）
- 仓库、议题与开源地址均指向本仓库

为遵守上游《PCL 分发有限许可》，以下内容按原样保留：`LICENCE` 全文、关于页面中对原作者（龙腾猫跃）与协力开发者的署名及其赞助链接、原始版权声明。

<br/>

### 相关资源

- [RMCL 仓库](https://github.com/lxraytop/RMCL)
- [RMCL 议题反馈](https://github.com/lxraytop/RMCL/issues)
- [上游 PCL 项目](https://github.com/Meloong-Git/PCL)
- [帮助文档库](https://github.com/LTCatt/PCL2Help)：启动器内置帮助文档的存储库

<br/>

### 许可

本项目遵循上游《PCL 分发有限许可》，详见 [LICENCE](LICENCE)。
