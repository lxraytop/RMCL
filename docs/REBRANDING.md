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
- `MeloongCore` 由 Git 子模块改为随仓库内置（移除 `.gitmodules`），克隆后无需 `--recursive`
- 移除 `patches/`（上游历史发布补丁）与 `最新正式版.zip`（上游发行包）

## 四、品牌标识

- `RMCL/Images/icon.ico`：重制为 RMCL 六边形图标（16/20/24/32/40/48/64/96/128/256 全尺寸）
- `RMCL/Resources/Font.ttf`：字体 name 表重写为 `RMCL English` / `RMCLEnglish`，与 `Application.xaml`
  中的 `Resources/#RMCL English` 一致，界面字体不会回退
- `RMCL/Images/Heads/` 整个目录（上游关于页的角色头像资源）已删除，工程内不再引用

## 五、功能裁剪（第二轮重构）

1. **移除「更多」页**：`Pages/PageOther/` 全部页面、`PageType.Other` / `PageType.HelpDetail` 枚举、
   标题栏第四个导航按钮，以及 `FormMain.xaml.vb` 中对应的路由分支一并删除。
2. **移除内置帮助**：`ModMain.vb` 的帮助加载器、`Resources/Help.zip` 资源、`ModEvent.vb` 的
   `打开帮助` / `刷新帮助` 事件、`MyListItem` 的帮助项加载逻辑全部删除。
3. **移除更新与上报**：`ModSecret.vb` 的 `ClsBaseUrl`、`UpdateCheckByButton` / `UpdateRestart` /
   `UpdateReplace`、`Telemetry` 及所有调用点、设置项 `SystemSystemUpdate` / `SystemSystemTelemetry`
   与「设置 → 其他」中对应的下拉框、复选框、检查更新按钮全部删除。
4. **解除主题限制**：移除主题单选按钮的 `IsEnabled="False"`、`ToolTipService.ShowOnDisabled`、
   遮罩层与解锁引导，`ThemeCheckAll` / `ThemeCheckOne` / `ThemeUnlock` 桩函数删除，所有主题默认可用。
5. **清零贡献者列表**：关于页、头像资源、`CurrentRank` 相关的赞助等级展示与「求赞助」弹窗全部移除。

## 六、为兼容性有意保留的内容

1. **`{pcl_*}` 替换标记别名**：`ModMain.vb` 在 `{rmcl_*}` 之后追加了旧标记的替换，
   社区已有的自定义主页模板无需修改即可继续工作。
2. **`LICENCE` 全文**：上游分发许可原文保留，未改写。

## 七、已知残留（不影响运行）

- 主页预设中若涉及第三方站点（`pclhomeplazaoss.*`、`pclsub.sodamc.com`、`files.mcimirror.top/PCL` 等），
  其 URL 属于外部托管的社区内容，改名会导致预设失效，故按原样保留。
- 联机节点列表接口 `easytier.meloong.com` 与 Minecraft 新闻源 `mcnews.meloong.com` 为外部服务地址，
  不属于品牌文案，保留以保证功能可用。

## 八、构建与推送

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
