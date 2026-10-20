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

## 八、第三轮：液态玻璃 UI 与功能页（当前 HEAD）

### 8.1 新增「设置 → 功能」子页

| 位置 | 说明 |
| --- | --- |
| `Pages/PageSetup/PageSetupFeature.xaml(.vb)` | 新建页面：内存优化 + 清理游戏垃圾两块卡片 |
| `Modules/ModFeature.vb` | 通用实现：`MemoryOptimize` / `MemoryOptimizeInternal` / `MemoryUsedMb` / `RubbishClear` / `GetRubbishSummary` / `FormatSize` |
| `PageSetupLeft.xaml(.vb)` | 新增第 5 个导航项 `ItemFeature`（Tag=4），路由到 `PageSubType.SetupFeature` |
| `FormMain.xaml.vb` | `PageSubType.SetupFeature = 4` |
| `ModLaunch.vb` | 启动前内存优化改用 `MemoryOptimize()` |

「启动游戏前自动优化内存」与「设置 → 启动」页的 `LaunchArgumentRam` 共用同一开关，避免两处配置互相打架。
功能页本身不再额外引入开关项（曾短暂存在的 `FeatureMemoryOnStart` / `FeatureClean*` 已删除）。

### 8.2 液态玻璃视觉

- `Application.xaml` 玻璃令牌：`ColorBrushGlassCard`（四段由 `#EDFFFFFF` 渐隐到 `#BCFFFFFF`）/
  `GlassRim`（斜向 `#D8FFFFFF → #29FFFFFF` 高光边）/ `GlassInner`（`#5CFFFFFF` 内衬）/
  `GlassSidebar` / `GlassHairline`。
- `ModSecret.ThemeRefreshMain()` 用 `BuildMeshBackground()` 生成「极光网格」底：
  一层线性渐变打底，再叠 3 个 `RadialGradientBrush` 色斑（由 `BlobBrush()` 生成，中心高饱和、边缘 alpha 归零），
  主题色变化时整块背景随之重算，而不是单纯换一层雾。
- 顶栏改为**浅色**玻璃（低饱和主题色微染 + 顶部高光），侧栏 alpha 降到 122，让卡片能透出底色形成层级。
- `MyCard` 圆角 13 → 16，阴影留在外侧 margin 且 hover 时透明度 0.14 → 0.3；卡片内部新增 `MainSheen`
  竖向白色渐变（alpha 88 → 26 → 0）模拟玻璃表面反光，插入 `MainGrid` 以保证层级正确。
- `MyButton` 的 `PanFore` 圆角 10 → 12；卡片 / 按钮 / 输入框 / 提示 / 下拉框 / 滚动条圆角统一提升并配 1px 高光描边。

### 8.3 主题切换

- 仅保留 蓝 / 青 / 绿 / 黄 / 红 五个预设（`0/1/2/3/10`）与「自定义颜色」（`14`）。
- 自定义模式才显示 色调 / 饱和度 / 亮度 滑条；拖动即时调用 `ThemeRefresh(ThemeCustom)`，
  修复了「点主题没反应」的问题。

### 8.4 其它

- 资源搜索源仅保留 Modrinth（`PageResource` 下拉框只剩一项，`ResourceSearcher` 错误文案同步）。
- 标题栏字标由 `PCL` 笔画路径改为 `RMCL` 字形，默认 `UiLogoText` 改为 `RMCL`。

## 十、第四轮：细节修正与「修改」页修复

### 10.1 微软登录

`ModSecret.OAuthClientId` 默认值设为 `ab25b229-f826-46db-b646-01035c3a3e52`，
仍支持 `RMCL_MS_CLIENT_ID` 环境变量覆盖。「开源版本说明」弹窗同步删去 OAuth 相关文案，
现仅提示联网通知需要自建服务端。

### 10.2 标题字标

原 `ShapeTitleLogo` 同时设置了 `Stretch="Uniform"` 与固定 `Width/Height`，导致字形被非等比压缩而"扭曲"；
且 `Stroke="White"` 在浅色玻璃顶栏上几乎不可见。现改为：

- 重绘 `R M C L` 四字母笔画路径（`Stroke` 描边而非填充），
- `Stretch="None"` + `Width="55" Height="21"` 精确尺寸，`Margin="19,0,0,0"` 与导航栏对齐，
- `Stroke="{DynamicResource ColorBrush1}"` 跟随主题色，`StrokeLineJoin/StartLineCap/EndLineCap="Round"` 圆角笔帽，
- 标题栏内 `BtnTitleClose` / `BtnTitleMin` 由 `Theme="White"` 改 `"Black"`，导航项加 `ColorType="Highlight"`。

### 10.3 功能页回归上游实现

`ModFeature.vb` 不再自造清理策略，改为对齐上游 PCL：

| 功能 | 上游对齐的实现 |
| --- | --- |
| 内存优化 | 有管理员权限 → `MemoryOptimizeInternal`；否则 `RunAsAdmin("--memory")` 提权后由子进程执行，退出码即释放的 KB |
| `Application.xaml.vb` | 新增 `--memory` 参数分支：记录 `AvailablePhysicalMemory` → 优化 → `Environment.Exit(释放 KB)` |
| 工作集回收 | `GC.Collect()` ×2 + `WaitForPendingFinalizers`，再对 `Process.GetProcesses` 逐个调用 `psapi!EmptyWorkingSet` |
| 垃圾清理 | 清 `PathTemp\Cache\`、`PathTemp`（跳过 cache 子目录）、`Paths.Base & "RMCL\"` 下的 `Log*.txt` |

被占用文件会计数并提示"已跳过"，不中断整轮清理。

### 10.4 「版本设置 → 修改」崩溃修复

`PageDownloadModify` 打开时会 `await DlOptiFabricLoader`，而该加载器原先请求
CurseForge 项目 `322385`。移除 CurseForge 后请求返回 403，异常把整页加载打断。
现改为 Modrinth 的 `optifabric` 并包 `Try/Catch`，失败时返回空列表 —— 页面正常打开，
只是没有 OptiFabric 可选组件。日志中已从 `状态改变：Failed` 变为 `Finished`。

## 九、构建与推送

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
