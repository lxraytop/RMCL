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

- 资源搜索源恢复为「全部 / CurseForge / Modrinth」（默认「全部」），CurseForge 走 `ModSecret.vb` 内置的 API Key，错误文案同步区分两个来源。
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

## 十一、第五轮：外观一致性、死引用清理与深色适配

### 11.1 标题字标放大到上游尺寸

第四轮重绘的 RMCL 字标字高只有 16.6（上游 PCL 为 18），视觉上比原版小一圈。
现保留路径本身，加 `LayoutTransform` 等比放大 `1.0843` 倍（16.6 → 18），
`StrokeThickness` 同步由 2.2 调为 2.03，放大后的实际笔宽仍为 2.2，与上游字重一致。

### 11.2 移除「主页 → 预设」

- `PageSetupUI.xaml`：删除「预设」单选与整个 `PanCustomPreset` 下拉，四列布局改三列。
- `PageSetupUI.OnMainPageTypeChanged`：删除 `Case 3`，未知取值统一按空白处理。
- `PageLaunchRight.RefreshReal`：删除 `Case 3` 下 90 余行的预设清单（含 20 多个第三方主页地址）。
- `Settings.vb`：删除 `UiCustomPreset` 设置项；`FormMain` 启动迁移把遗留的 `UiCustomType = 3` 重置为 0。

### 11.3 清理教学文件中的死引用

`Resources/Custom.xaml` 会被 `ExtractResources` 原样释放给用户，其中引用了本版本并不存在的功能：

| 原内容 | 问题 | 处理 |
| --- | --- | --- |
| `EventType="打开帮助"` ×3 | 枚举中已无该成员（内置帮助已移除），主页加载会失败 | 换成 `打开网页` / 改为文字说明 |
| `EventType="内存优化"` | 同上 | 改为 `弹出提示` |
| `EventType="清理垃圾"` | 同上 | 改为 `弹出提示` |

同时把 `EventType` 全部取值、常用替换标记改为正文文字列举（不写花括号，避免被 `ArgumentReplace` 提前替换），
并把 `PageSetupUI` / `PageSetupLaunch` / `PageInstanceSetup` 中 11 处「在 更多 → 帮助 中搜索替换标记」
的说明改为指向「设置 → 个性化」生成的 `Custom.xaml`；`PageInstanceExport` 中同样引用帮助 JSON 的
「整合包制作指南」按钮改为「什么是整合包」+ 提示条。

### 11.4 修复「清理游戏垃圾 → 重新统计」

`RefreshJunkInfo` 会写 `LabJunkInfo.Text`，却被 `RunInNewThread` 直接调用，触发跨线程访问异常；
异常又被 `Catch` 里同样跨线程的写操作再次抛出，于是文本停在「正在统计垃圾文件……」。
现拆分为 `GetRubbishText()`（纯计算，可后台调用）与 `RefreshJunkInfoAsync()`（后台统计 + `RunInUi` 回写），
按钮在统计期间显示「统计中……」并禁用。

### 11.5 功能页按钮尺寸

「设置 → 功能」两处按钮原先放在无高度约束的 `StackPanel` 里，且第二个按钮 `MinWidth="110"`，
与个性化页的 `MinWidth="140" Height="35"` 不一致。现统一为 `MinWidth="140" Height="35" Padding="13,0"`，
外层改用固定 `Height="35"` 的 `Grid` + `SharedSizeGroup="Button"`。

### 11.6 深色模式补全

| 组件 | 问题 | 处理 |
| --- | --- | --- |
| `MyIconButton.Theme = Black` | 填充固定为 `ARGB(160,0,0,0)`，深色顶栏上完全看不见 | 新增 `MonoColor()`：浅色 `160,0,0,0` / 深色 `190,255,255,255`，指向态再提亮；替换全部 4 处分支 |
| `ThemeRefreshMain` | 原先在深色下把最小化 / 关闭切成 `Themes.White`，而 `White` 用的是 `ColorBrush8`（深色下是深蓝），同样不可见 | 删除该切换，改为 `RefreshMonoIcons()` 递归刷新整窗单色图标 |
| 错误弹窗等（`MyMsgText/Input/Select`、`MyMsgLogin`） | 底色写死 `#FBFBFB`、正文写死 `#FF5C5C5C` | 新增 `ColorBrushDialogBackground` / `ColorBrushDialogText` 动态资源，随外观重算 |

由于 `Themes.Black` 由固定黑改为随外观反转，返回、清空、恢复等 20 余个图标按钮一并得到修正。

## 十二、第六轮：恢复 CurseForge 资源搜索与整合包安装（2.16.0.0）

第三轮把搜索源收窄为「仅 Modrinth」，本轮按 `1ed567c` 的 diff 逐条还原，并把 API Key 内置：

| 位置 | 改动 |
| --- | --- |
| `Pages/PageDownload/Resource/PageResource.xaml` | 来源下拉框恢复 `全部(3)` / `CurseForge(1)` / `Modrinth(2)`，默认「全部」；`HintError` 默认文案恢复双源描述 |
| `Pages/PageDownload/Resource/PageResource.xaml.vb` | `.Sources` 由硬编码 `ResourcePlatforms.Modrinth` 改回读取下拉框 `Tag` |
| `Modules/Resource/ResourceSearcher.vb` | 恢复双源错误文案分支（CurseForge 失败时提示只显示了 Modrinth 的内容，并提示检查 API Key） |
| `Modules/ModSecret.vb` | `CurseForgeAPIKey` 内置默认值（环境变量 `RMCL_CURSEFORGE_API_KEY` 仍优先）；`x-api-key` 增加空值短路与 `mod.mcimirror.top/curseforge` 镜像判定 |
| `Modules/Minecraft/ModModpack.vb` | 修 `ModJson?("FileName")` → `("fileName")`：JObject 区分大小写，原写法恒为 false，导致 `.jar` 的类型兜底失效 |

后端（`InstallPackCurseForge`、`GetCurseForgeAddress`、`ResourceProject.FromProjectId`、`ParseCurseForgeDownloadUrls`）自始至终完整，本轮未改。

已知限制：CurseForge 对「作者关闭第三方分发」的文件返回 `downloadUrl = null`，`ResourceVersion.FromPlatformJson` 会据此拼一个猜测的 CDN 地址，安装时这类文件会下载失败；彻底解决需另做「列出清单让用户手动下载」的流程。

## 十三、第七轮：内存优化释放量口径修正（2.16.0.1）

**症状**：未取得管理员权限时点「立即优化内存」，第一次会报出远超整机占用的释放量（32 GB 机器、可用 20.6 GB，却报「共释放 14.53 GB」）。

**根因**：`MemoryOptimizeInternal` 原先把每个进程 `WorkingSet64` 的减少量直接累加。工作集包含 DLL、运行时、共享内存等**共享页**，同一批页面会在几十个进程里各算一次；第一次清理时这类软页最多，累加值便能超过整机实际占用，再清一次已无软页可清，于是表现为「只有第一次的数字离谱」。与权限无因果关系 —— 只是非管理员每次都要走提权子进程这条路，第一次点必然撞上。

**被否掉的两种口径**：

| 口径 | 实测结果 | 结论 |
| --- | --- | --- |
| 累加整个工作集的减少量 | 32 GB 机器算出 14.53 GB | 共享页重复计数，弃用 |
| 系统可用物理内存的增量 | 清掉 721 MB 工作集，可用内存只涨 59 MB（等 3 s 也不涨） | 私有脏页先进 modified / standby 列表等写回页面文件，短期不计入可用，弃用 |

**现口径**：`psapi!GetProcessMemoryInfo` + `PROCESS_MEMORY_COUNTERS_EX2`（Win8.1+）的 `PrivateWorkingSetSize`（专用工作集）逐进程求减少量之和。专用工作集不含共享页，跨进程求和不会重复计数；读取失败时退回 `WorkingSet64`，两者都不可读返回 -1（必须与「专用工作集被清到 0」区分，否则进程恰好退出会被算成释放了它的全部占用）。

**验证**：同一套算法跑本机真实进程，123 个可读进程专用工作集合计 1.65 GB，算出释放 1.62 GB，而系统实际占用 10.08 GB —— 数值落在物理上可能成立的范围内，旧口径同一场景会报 14.53 GB。

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
