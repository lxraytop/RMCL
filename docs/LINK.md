# 联机功能说明（RMCL 2.18.0.0）

RMCL 的联机沿用上游 PCL 的思路：用 **EasyTier** 在玩家之间建立一层虚拟局域网（P2P + 公共节点中继），
再把房主的局域网端口通过 `--port-forward` 映射到加入者的本地端口，从而让朋友直接进入你的单人游戏，
**不需要公网 IP、不需要端口映射、不需要管理员权限**。

入口：`设置 → 联机`（与「启动」「个性化」「其他」「功能」同级）。

## 一、页面与代码位置

| 位置 | 作用 |
| --- | --- |
| `Pages/PageLink/PageLinkMain.xaml` | 联机页面：加入房间 / 创建房间 / 高级设置 / 房间状态 |
| `Pages/PageLink/PageLinkMain.xaml.vb` | 状态机、EasyTier 进程管理、CLI 信息读取、局域网广播、节点测试 |
| `Pages/PageSetup/PageSetupLeft.xaml` | 设置左侧导航中的「联机」项（Tag = 1 → `PageSubType.SetupLink`） |
| `Modules/ModLink.vb` | EasyTier 版本、下载地址、安装解压、节点清单与测试、局域网世界探测 |
| `FormMain.xaml` / `FormMain.xaml.vb` | 顶部导航不再包含联机；`PageType.Setup = 2` |

页面沿用了原有的四态机：`Waiting → Loading → Finished / Failed`，
由 `LoaderCombo` 驱动「检查模块 → 准备下载 → 下载 → 启动」四个步骤。

### 页面布局（2.18 重构）

布局参考 PCL CE 的联机页，按「最常用的操作放最上面」排列：

```
┌──────────────────────────────┐
│                    [节点 1/1 可达]  ← 胶囊，点一下重新测试
│ ┌─ 加入房间 ────────────────┐ │
│ │ 说明文字                   │ │
│ │ [邀请码输入框] [清空][粘贴][加入] │
│ │ 游戏版本  [版本下拉框]       │ │
│ └───────────────────────────┘ │
│ ┌─ 创建房间 ────────────────┐ │
│ │ 说明文字                   │ │
│ │ [世界下拉框] [手动端口][刷新][创建] │
│ │ 检测结果提示                │ │
│ └───────────────────────────┘ │
│ ┌─ 高级设置（默认折叠）──────┐ │
│ │ 节点测试列表 + [测试节点]    │ │
│ │ ──────────────────────    │ │
│ │ 连接模式 / 自定义节点        │ │
│ └───────────────────────────┘ │
│   EasyTier 官网 | 搭建共享节点 | 社区节点 │
└──────────────────────────────┘
```

加入房间改为**页内输入**（原来是弹窗），支持回车提交、粘贴、清空。

### 记忆功能

| 记什么 | 存在哪 | 表现 |
| --- | --- | --- |
| 上次用的邀请码 | `LinkLastInviteCode` | 再次进入页面时自动回填输入框 |
| 上次创建用的端口 | `LinkLastCreatePort` | 手动端口弹窗的默认提示值；没检测到世界时也会提示上次用过哪个 |
| 自动检测局域网世界 | 实时扫描，不落盘 | 下拉框直接列出本机开着的 MC 世界，选中即可创建 |

### 自动发现局域网世界

`ModLink.LinkDiscoverWorlds` 三步：

1. 找到所有 `java` / `javaw` 进程；
2. 用 `GetExtendedTcpTable`（`TCP_TABLE_OWNER_PID_LISTENER`）取这些进程正在监听的端口；
3. 对每个端口跑一次 **Minecraft 状态协议**（握手 + status），成功的才认为是一个世界。

结果形如 `1 - 新的世界 · 1.20.1 · 1/8 人 · 端口 58610`，
失败则退回「端口 xxx」，因此不会把进程里的其他端口误列出来。

### 一键启动并进入（加入者）

加入房间成功后，完成页会比房主多出两样东西：一行提示 + 一个「启动并进入」按钮。

- **版本下拉框**在「加入房间」卡片里，列出本机所有可用版本（按版本选择页的卡片顺序：
  收藏 → 可装 Mod → 原版 → 其它 → 愚人节），选择结果记在 `LinkLaunchVersion`；
  没记录时跟随启动页当前选中的版本。
- 点「启动并进入」时执行 `PageLinkMain.LaunchAndJoin`：

  ```vb
  PageChange(FormMain.PageType.Launch)      '切回启动页，让玩家看到进度
  McLaunchStart(New McLaunchOptions With {
      .Instance = 选中版本,
      .ServerIp = $"127.0.0.1:{ClientPort}" 'EasyTier 已把房间内的服务映射到这个本地端口
  })
  ```

  这里复用的就是启动设置里「自动进入服务器」的那套逻辑（`McLaunchArgumentMain`）：
  1.20 以上走 `--quickPlayMultiplayer`，更早的版本走 `--server` / `--port`。

**为什么不直接写进版本的「自动进入服务器」设置**：每次联机映射到本机端口都不一样，
写进 `VersionServerEnter` 会留下一个下次启动就连不上的失效地址；
用 `McLaunchOptions.ServerIp` 临时覆盖，只对这一次启动生效。

## 二、运行时流程

### 1. 房主

1. 在游戏内 `Esc → 对局域网开放`，把端口号（如 `54321`）填进「创建房间」的输入框。
2. 生成网络名与密码：

   ```
   网络名 = "P" + 端口(16 进制,4 位) + "-" + 随机码(5 位)
   密码   = 随机码(5 位)
   邀请码 = 网络名-密码-协议版本(2 位)+节点标识(3 位)
   ```

3. EasyTier 以房主身份启动（固定虚拟 IP `10.114.114.114`，只放行该端口）：

   ```
   --no-tun --network-name=<名> --network-secret=<密码>
   --relay-network-whitelist <名> --private-mode true
   --encryption-algorithm chacha20 --multi-thread
   --listeners <空闲端口> --rpc-portal 127.0.0.1:<RPC 端口>
   --hostname=Server-xxxxxx
   -i 10.114.114.114 --tcp-whitelist=<端口> --udp-whitelist=<端口>
   -p <公共节点> ...
   ```

4. 连接成功后自动复制邀请码。

### 2. 加入者

1. 输入邀请码（或复制后切回 RMCL 自动识别）→ 解析出端口、网络名、密码。
2. 以加入者身份启动 EasyTier，并把房主端口映射到本地空闲端口：

   ```
   -d --tcp-whitelist=0 --udp-whitelist=0
   --hostname=Client-xxxxxx
   --port-forward tcp://127.0.0.1:<本地端口>/10.114.114.114:<房主端口>
   --port-forward udp://127.0.0.1:<本地端口>/10.114.114.114:<房主端口>
   ```

3. 每秒向 `127.0.0.1:4445` 发送 MC 局域网广播包
   `[MOTD]RMCL 联机房间[/MOTD][AD]<本地端口>[/AD]`，
   游戏多人列表底部即可出现房间；若未出现，可复制页面上的地址在「直接连接」中粘贴。

## 三、节点选择与节点测试

### 1. 节点从哪来

联机本身不依赖任何"官方服务"，但两个玩家必须能连到**同一台**中继节点才会互相发现。RMCL 的节点来源按优先级：

1. **自定义节点**（`设置 → 联机 → 高级设置 → 自定义节点`）：填写后只使用这些节点，房主与加入者必须一致。
2. **社区订阅**：`ModLink.LinkNodeSubscription`（`https://etnode.zkitefly.eu.org/node1`），
   内容为每行一个节点地址，拉取结果缓存 10 分钟；失败不影响联机。
3. **内置节点**：`ModLink.LinkDefaultPeers`（当前 `tcp://183.66.27.22:39409`，社区维护），
   可被仓库里的 `raytcp.txt` 覆盖（见下）。

#### 远程节点清单 raytcp.txt（2.18.2.0）

内置节点会失效，所以仓库根目录放了一份 `raytcp.txt`，客户端可以按需拉取：

- **只在玩家点击「高级设置 → 更新节点」时联网**，没有任何自动更新，也不开机请求
- 拉取顺序：`cdn.jsdelivr.net/gh/lxraytop/RMCL@main/raytcp.txt` → `raw.githubusercontent.com/...` →
  `gh-proxy.com` 反代（`ModLink.LinkPeerListUrls`）
- 解析规则（`ModLink.LinkParsePeerList`）：一行一个节点，`#` 开头为注释，
  支持 `备注|地址`，非法地址丢弃、去重、最多 `LinkPeerListMaxCount`（16）条
- 更新结果存在 `LinkRemotePeers` / `LinkRemotePeersTime`，界面显示「远程清单（N 个，时间 更新）」；
  点「恢复默认」清空这两个值，退回程序自带节点
- 解析失败 / 网络失败时**保留原有节点**，只弹一句红色提示，不会把节点清空

> **重要历史背景**：EasyTier 官方的公共节点 `public.easytier.cn` / `public.easytier.top` 在 2026 年前后已停服
> —— `.top` 只是指向 `.cn` 的 CNAME，而 `.cn` 已无 A 记录，EasyTier 自身解析会报 `NoRecordsFound`；
> 官方文档也已改为让用户自建共享节点。PCL CE 能联机是因为它使用**自建节点**
> （其日志中可见 `pysio-alpha.et.pclc.cc:23313/23315`、`public2.easytier.cn:54321`），
> 这些节点由 PCL CE 社区出资维护，不适合第三方客户端长期直接占用，因此 RMCL 使用公开的社区订阅节点，
> 并允许随时改成自定义节点。

### 2. 节点测试

联机页面里有一张「节点测试」卡片（`CardNodeTest`）：

- 进入页面时自动测一次，也可点「测试节点」手动重测；
- 每个节点并行做 **TCP 握手计时**（3 秒超时），显示 `协议 · 可达 · xx ms` 或失败原因；
- `udp:// / wg:// / quic:// / ring://` 类节点无法用 TCP 准确探测，标记为「UDP 类节点，无法直接测速」；
- 结论区会给出判断：
  - 有节点可达 → `x/y 个节点可达，可以尝试创建房间`
  - 所有可测节点均不可达 → 提示当前网络可能封锁了这些端口，建议换网络或填自定义节点

创建房间时，如果上一次测试显示全部不可达，会先弹一条红色提示，避免白等 20 秒；
最终失败时「无法连接到节点」的详情里也会附带节点测试结果与排查建议。

相关实现：`ModLink.LinkTestPeer` / `LinkTestPeers`（并行）/ `LinkParsePeer`（支持 `[IPv6]:port`）。

## 四、EasyTier 的获取

- 版本：`ModLink.LinkVersion`（当前 `2.6.4`）
- 目录：`%AppData%\RMCL\EasyTier\<版本>\`
- 下载源（按顺序尝试）：

  1. GitHub Release `EasyTier/EasyTier`
  2. `staticassets.naids.com`（PCL CE 镜像）
  3. `s3.pysio.online`（PCL CE 备用镜像）

- 解压：`%TEMP%` 中解压后递归查找 `easytier-core.exe`，把同目录文件复制到目标目录，
  再删除压缩包；不会重命名文件，`easytier-cli.exe` 保持原名。
- 清理：`LinkKillStaleProcesses` 只会结束后**位于 RMCL 自身 EasyTier 目录下**的进程，
  不会影响 PCL 等其他启动器正在使用的 EasyTier。

## 五、节点信息与房间状态

房间状态由 `easytier-cli --rpc-portal 127.0.0.1:<RPC> -o json peer` 每秒刷新一次，
字段与 EasyTier 2.6.x 对齐：`hostname / ipv4 / cost / lat_ms / nat_type`。

- `ipv4 = 10.114.114.114` → 房主
- `hostname` 以 `Client` 开头 → 加入者
- 其余为公共节点（不计入房间人数）

据此计算延迟、人数、是否为中继连接，并给出「你的网络环境比房主更好」之类的提示。

## 六、相关的设置项

| 设置项 | 说明 |
| --- | --- |
| `LinkLatencyMode` | 0 = 优先直连，1 = 优先低延迟（`--latency-first`） |
| `LinkCustomPeer` | 自定义节点，逗号分隔 |
| `LinkLastInviteCode` | 上一次使用的邀请码，进入页面时自动回填 |
| `LinkLastCreatePort` | 上一次创建房间用的端口 |
| `LinkLaunchVersion` | 加入房间后「启动并进入」使用的游戏版本 |
| `LinkLastAutoJoinInviteCode` | 上一次复制的邀请码，用于剪贴板自动识别 |
| `UiHiddenSetupLink` | 是否在设置中隐藏联机页面 |

「初始化本页设置」按钮会重置前两项（`SettingService.ResetSettings`），
记忆项（`LinkLastInviteCode` / `LinkLastCreatePort`）不会被清掉。
