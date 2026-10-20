# RMCL 品牌重构说明

本项目（**Ray Minecraft Launcher**，简称 **RMCL**）以 [PCL（Plain Craft Launcher）](https://github.com/Meloong-Git/PCL)
标签 `2.14.0.0` 正式版源码为基准，完成整体品牌重构。功能逻辑、界面结构与版本特性保持与上游 2.14.0.0 一致。

## 一、基准与仓库

| 项目 | 值 |
| --- | --- |
| 上游仓库 | `https://github.com/Meloong-Git/PCL` |
| 基准版本 | `2.14.0.0`（tag，commit `ade6e04`） |
| 目标仓库 | `https://github.com/lxraytop/RMCL` |
| 产品全称 | Ray Minecraft Launcher |
| 产品简称 | RMCL |
| 程序集 / 可执行文件 | `RMCL.exe`、`RMCLCS.dll` |
| 根命名空间 | `RMCL` |

## 二、命名映射

| 原（PCL） | 现（RMCL） |
| --- | --- |
| `Plain Craft Launcher 2`（工程/目录/程序集名） | `RMCL` |
| `Plain Craft Launcher`（产品全称） | `Ray Minecraft Launcher` |
| `PCL` / `PCL2`（简称、文案） | `RMCL` |
| 命名空间 `PCL` | `RMCL` |
| `PCLCS`（C# 子工程） | `RMCLCS` |
| `PclLogger` / `PclMutex` / `ItemAboutPcl` / `CheckOptionsPcl` / `IncludePCL` 等标识符 | `RmclLogger` / `RmclMutex` / `ItemAboutRmcl` / `CheckOptionsRmcl` / `IncludeRMCL` |
| 数据目录 `PCL\` | `RMCL\` |
| 实例配置 `PCL\Setup.ini`、`PCL\Logo.png`、`PCL\Custom.xaml` 等 | `RMCL\Setup.ini`、`RMCL\Logo.png`、`RMCL\Custom.xaml` |
| 版本忽略标记 `.pclignore` | `.rmclignore` |
| 自定义主页替换标记 `{pcl_version}` 等 | `{rmcl_version}`（保留 `{pcl_*}` 别名） |
| 互斥体 `PCL_SingletonMutex` | `RMCL_SingletonMutex` |
| 注册表隔离键 `PCLDebug` | `RMCLDebug` |
| 环境变量 `PCL_MS_CLIENT_ID` / `PCL_CURSEFORGE_API_KEY` | `RMCL_MS_CLIENT_ID` / `RMCL_CURSEFORGE_API_KEY` |
| 内嵌字体族 `PCL English` | `RMCL English` |
| 下载 UA `PCL2/{ver}` | `RMCL/{ver}` |
| 仓库地址 `github.com/Meloong-Git/PCL` | `github.com/lxraytop/RMCL` |

## 三、文件与目录变更

- `Plain Craft Launcher 2.sln` → `RMCL.sln`
- `Plain Craft Launcher 2/` → `RMCL/`
- `Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj` → `RMCL/RMCL.vbproj`
- `PCLCS/` → `RMCLCS/`，`PCLCS.csproj` → `RMCLCS.csproj`
- `Modules/Base/PclLogger.vb` → `Modules/Base/RmclLogger.vb`
- `Images/Heads/PCL2.png` → `Images/Heads/RMCL.png`
- `MeloongCore` 由 Git 子模块改为随仓库内置（移除 `.gitmodules`），克隆后无需 `--recursive`
- 移除 `patches/`（上游历史发布补丁）与 `最新正式版.zip`（上游 PCL 发行包）

## 四、品牌标识

- `RMCL/Images/icon.ico`：重制为 RMCL 六边形图标（16/20/24/32/40/48/64/96/128/256 全尺寸）
- `RMCL/Images/Heads/Logo.png`（关于页启动器标志）与 `RMCL/Images/Heads/RMCL.png`：同步重制
- `RMCL/Resources/Font.ttf`：字体 name 表重写为 `RMCL English` / `RMCLEnglish`，与 `Application.xaml`
  中的 `Resources/#RMCL English` 一致，界面字体不会回退

## 五、为兼容性有意保留的内容

1. **`{pcl_*}` 替换标记别名**：`ModMain.vb` 在 `{rmcl_*}` 之后追加了旧标记的替换，
   社区已有的自定义主页模板无需修改即可继续工作。
2. **`LICENCE` 全文**：上游《PCL 分发有限许可》原文保留，未改写。
3. **关于页署名**：原作者「龙腾猫跃」「HerobrineXia」条目保留并标注为 PCL 原作者/原协力开发者，
   赞助按钮指向原作者爱发电（文案改为「赞助原作者」）。
4. **版权声明**：原始著作权登记信息保留，并补充「RMCL 是基于 PCL 源代码的第三方二次开发版本」。

## 六、已知残留（不影响运行）

- `RMCL/Resources/Help.zip` 为上游帮助文档包，内容中仍以 PCL 称呼；CI 构建时会从
  `LTCatt/PCL2Help` 重新拉取覆盖。
- 主页预设中若涉及第三方站点（`pclhomeplazaoss.*`、`pclsub.sodamc.com`、`files.mcimirror.top/PCL` 等），
  其 URL 属于外部依赖，按原样保留。
- CI 构建步骤仍需克隆 `LTCatt/PCL2Help` 以生成帮助包。

## 七、构建与推送

```bash
# 构建（Windows + VS2022 / MSBuild + .NET Framework 4.8 开发工具包）
dotnet restore RMCLCS/RMCLCS.csproj
msbuild "RMCL\RMCL.vbproj" -p:Configuration=Release
# 产物：RMCL\obj\Release\RMCL.exe

# 推送
git remote set-url origin https://github.com/lxraytop/RMCL.git
git push -u origin main
```

> 仓库默认分支为 `main`，首次推送前请先在 GitHub 上创建 `lxraytop/RMCL` 空仓库。
