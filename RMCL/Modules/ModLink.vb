Imports System.Reflection
Imports System.Runtime.InteropServices

''' <summary>
''' 联机功能的公共支持（基于 EasyTier 的虚拟局域网）。
''' 开源版不联网拉取配置，这里内置了联机所需的全部参数与下载地址。
''' </summary>
Public Module ModLink

#Region "版本与路径"

    ''' <summary>
    ''' 当前使用的 EasyTier 版本。
    ''' </summary>
    Public Const LinkVersion As String = "2.6.4"

    ''' <summary>
    ''' 联机模块所在的文件夹路径，以 \ 结尾。
    ''' </summary>
    Public ReadOnly Property PathEasyTier As String
        Get
            Return Paths.AppDataThenName & "EasyTier\" & LinkVersion & "\"
        End Get
    End Property
    ''' <summary>
    ''' 联机模块核心进程。
    ''' </summary>
    Public ReadOnly Property PathEasyTierCore As String
        Get
            Return PathEasyTier & "easytier-core.exe"
        End Get
    End Property
    ''' <summary>
    ''' 联机模块命令行工具。
    ''' </summary>
    Public ReadOnly Property PathEasyTierCli As String
        Get
            Return PathEasyTier & "easytier-cli.exe"
        End Get
    End Property

    ''' <summary>
    ''' 联机模块是否已经就绪。
    ''' </summary>
    Public ReadOnly Property IsEasyTierReady As Boolean
        Get
            Return IO.File.Exists(PathEasyTierCore) AndAlso IO.File.Exists(PathEasyTierCli)
        End Get
    End Property

    ''' <summary>
    ''' 获取当前 CPU 架构对应的 EasyTier 发布包标识。
    ''' </summary>
    Public Function LinkGetArchitecture() As String
        Select Case GetType(String).Assembly.GetName().ProcessorArchitecture
            Case ProcessorArchitecture.X86
                Return "i686"
            Case ProcessorArchitecture.Arm
                Return "arm64"
            Case Else
                Return "x86_64"
        End Select
    End Function

    ''' <summary>
    ''' 获取联机模块的下载地址，按优先级排列。
    ''' </summary>
    Public Function LinkGetDownloadUrls() As String()
        Dim FileName As String = $"easytier-windows-{LinkGetArchitecture()}-v{LinkVersion}.zip"
        Return {
            $"https://github.com/EasyTier/EasyTier/releases/download/v{LinkVersion}/{FileName}",
            $"https://staticassets.naids.com/resources/pclce/static/easytier/{FileName}",
            $"https://s3.pysio.online/pcl2-ce/static/easytier/{FileName}"
        }
    End Function

    ''' <summary>
    ''' 将下载好的联机模块压缩包解压到目标文件夹，成功后删除压缩包。
    ''' </summary>
    Public Sub LinkInstallEasyTier(ZipPath As String)
        Dim TempPath As String = IO.Path.Combine(IO.Path.GetTempPath(), "RMCL-EasyTier-" & Guid.NewGuid().ToString("N"))
        Try
            IO.Directory.CreateDirectory(TempPath)
            ZipFile.ExtractToDirectory(ZipPath, TempPath)
            '压缩包内一般带有版本号的顶层文件夹，直接定位核心程序所在的目录
            Dim CoreFile As String = IO.Directory.EnumerateFiles(TempPath, "easytier-core.exe", SearchOption.AllDirectories).FirstOrDefault
            If CoreFile Is Nothing Then Throw New Exception("联机模块压缩包中不包含 easytier-core.exe，请更换下载源后重试")
            Dim SourcePath As String = IO.Path.GetDirectoryName(CoreFile)
            IO.Directory.CreateDirectory(PathEasyTier)
            For Each FilePath As String In IO.Directory.EnumerateFiles(SourcePath)
                IO.File.Copy(FilePath, IO.Path.Combine(PathEasyTier, IO.Path.GetFileName(FilePath)), True)
            Next
            Logger.Info($"已安装联机模块到 {PathEasyTier}")
            IO.File.Delete(ZipPath)
        Finally
            Try
                If IO.Directory.Exists(TempPath) Then IO.Directory.Delete(TempPath, True)
            Catch ex As Exception
                Logger.Warn(ex, "清理联机模块临时目录失败")
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' 结束所有由 RMCL 启动或残留在自身目录下的联机模块进程。
    ''' 不会影响其他启动器所使用的 EasyTier。
    ''' </summary>
    Public Sub LinkKillStaleProcesses(Optional CurrentPid As Integer = -1)
        For Each ProcessObject As Process In Process.GetProcessesByName("easytier-core")
            If ProcessObject.Id = CurrentPid Then Continue For
            Try
                '只清理从本启动器的联机模块目录启动的进程
                Dim FilePath As String = Nothing
                Try
                    FilePath = ProcessObject.MainModule?.FileName
                Catch ex As Exception
                    Continue For '无权限读取路径，说明不是本程序启动的
                End Try
                If String.IsNullOrEmpty(FilePath) OrElse
                   Not FilePath.StartsWith(PathEasyTier, StringComparison.OrdinalIgnoreCase) Then Continue For
                Logger.Info($"停止残留的联机模块，PID：{ProcessObject.Id}")
                ProcessObject.Kill()
                ProcessObject.WaitForExit(1000)
            Catch ex As Exception
                Logger.Warn(ex, $"结束联机模块进程失败（PID {ProcessObject.Id}）")
            Finally
                ProcessObject.Close()
            End Try
        Next
    End Sub

#End Region

#Region "节点"

    ''' <summary>
    ''' 默认的社区中继节点（已实测可连通）。
    ''' EasyTier 官方原先的公共节点 public.easytier.cn / .top 已在 2026 年前后停服（域名已无 A 记录），
    ''' 这里改用仍在运营的社区节点：zkitefly 维护的 EasyTier 公共节点。
    ''' 节点随时可能变化，可在「高级设置 → 自定义节点」中覆盖。
    ''' </summary>
    Public ReadOnly LinkDefaultPeers As String() = {
        "tcp://183.66.27.22:39409"
    }

    ''' <summary>
    ''' 远程节点清单 raytcp.txt 的候选地址，按顺序尝试。
    ''' 实测在国内容易连上的是 gh-proxy 反代，其次 GitHub 原始地址，
    ''' jsDelivr 的仓库 CDN 放在最后（部分地区反而连不上）。
    ''' </summary>
    Public ReadOnly LinkPeerListUrls As String() = {
        "https://gh-proxy.com/https://raw.githubusercontent.com/lxraytop/RMCL/main/raytcp.txt",
        "https://raw.githubusercontent.com/lxraytop/RMCL/main/raytcp.txt",
        "https://cdn.jsdelivr.net/gh/lxraytop/RMCL@main/raytcp.txt"
    }

    ''' <summary>
    ''' 远程节点清单最多接受多少个节点（防止文件被改成一大串）。
    ''' </summary>
    Public Const LinkPeerListMaxCount As Integer = 16

    ''' <summary>
    ''' 解析节点清单文本。
    ''' 一行一个节点，可用 "备注|地址" 的形式加备注；以 # 开头的行与空行会被忽略，
    ''' 格式非法的地址会被丢弃，重复的地址只保留一个。
    ''' </summary>
    Public Function LinkParsePeerList(Text As String) As List(Of String)
        Dim Result As New List(Of String)
        If String.IsNullOrWhiteSpace(Text) Then Return Result
        For Each Line As String In Text.Split(New String() {vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
            Dim Item As String = Line.Trim()
            If Item.Length = 0 OrElse Item.StartsWith("#") Then Continue For
            '允许 "备注|地址"
            Dim BarIndex As Integer = Item.LastIndexOf("|"c)
            If BarIndex >= 0 Then Item = Item.Substring(BarIndex + 1).Trim()
            If Not Item.Contains("://") Then Continue For
            If Not LinkIsValidPeer(Item) Then Continue For
            If Result.Contains(Item) Then Continue For
            Result.Add(Item)
            If Result.Count >= LinkPeerListMaxCount Then Exit For
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 从仓库拉取 raytcp.txt 并解析出节点清单。
    ''' 全部源都失败时抛出异常，由调用方提示；成功时不会自动保存。
    ''' </summary>
    Public Function LinkFetchRemotePeers(Optional Timeout As Integer = 6000) As List(Of String)
        Dim Errors As New List(Of String)
        For Each Url As String In LinkPeerListUrls
            Try
                '加时间戳，尽量绕开 CDN 缓存
                Dim Text As String = NetRequestByClient(Url & "?t=" & Date.Now.Ticks, Timeout:=Timeout, MakeLog:=False)
                Dim Peers As List(Of String) = LinkParsePeerList(Text)
                If Peers.Any Then
                    Logger.Info($"已从 {Url} 获取到 {Peers.Count} 个节点：{Peers.Join("，")}")
                    Return Peers
                End If
                Errors.Add($"{Url}：内容中没有可用的节点")
            Catch ex As Exception
                Logger.Warn(ex, $"拉取节点清单失败：{Url}")
                Errors.Add($"{Url}：{ex.Message}")
            End Try
        Next
        Throw New Exception($"无法获取节点清单，所有源都失败了：{vbCrLf}{Errors.Join(vbCrLf)}")
    End Function

    ''' <summary>
    ''' 把远程节点清单保存为当前的内置节点。
    ''' </summary>
    Public Sub LinkSaveRemotePeers(Peers As IEnumerable(Of String))
        Dim List As List(Of String) = Peers.ToList()
        Settings.Set("LinkRemotePeers", List.Join(vbLf))
        Settings.Set("LinkRemotePeersTime", Date.Now.ToString("yyyy-MM-dd HH:mm"))
        Logger.Info($"内置节点已更新为 {List.Count} 个：{List.Join("，")}")
    End Sub

    ''' <summary>
    ''' 清除远程节点清单，恢复为程序自带的节点。
    ''' </summary>
    Public Sub LinkResetRemotePeers()
        Settings.Set("LinkRemotePeers", "")
        Settings.Set("LinkRemotePeersTime", "")
        Logger.Info("已恢复为程序自带的内置节点")
    End Sub

    ''' <summary>
    ''' 当前生效的内置节点：用户主动更新过就用远程清单，否则用程序自带的。
    ''' </summary>
    Public Function LinkGetDefaultPeers() As List(Of String)
        Dim Saved As String = Settings.Get(Of String)("LinkRemotePeers")
        If Not String.IsNullOrWhiteSpace(Saved) Then
            Dim SavedList As List(Of String) = LinkParsePeerList(Saved.Replace(vbLf, vbCrLf))
            If SavedList.Any Then Return SavedList
        End If
        Return LinkDefaultPeers.ToList()
    End Function

    ''' <summary>
    ''' 内置节点的来源描述，用于设置界面展示。
    ''' </summary>
    Public Function LinkGetPeerSourceText() As String
        Dim Saved As String = Settings.Get(Of String)("LinkRemotePeers")
        Dim SavedTime As String = Settings.Get(Of String)("LinkRemotePeersTime")
        Dim SavedList As List(Of String) = LinkParsePeerList(Saved)
        If SavedList.Any Then
            Return $"内置节点：远程清单（{SavedList.Count} 个，{If(String.IsNullOrEmpty(SavedTime), "更新时间未知", SavedTime)} 更新）"
        End If
        Return $"内置节点：程序自带（{LinkDefaultPeers.Length} 个）"
    End Function

    ''' <summary>
    ''' 社区节点订阅地址，内容为每行一个节点（例如 tcp://ip:port）。
    ''' 拉取失败不影响联机，会退回内置节点。
    ''' </summary>
    Public Const LinkNodeSubscription As String = "https://etnode.zkitefly.eu.org/node1"

    ''' <summary>
    ''' 社区公共节点的在线状态接口（EasyTier 官方，可能已下线，仅作为补充来源）。
    ''' </summary>
    Public Const LinkNodeListApi As String = "https://uptime.easytier.cn/api/nodes?page=1&per_page=1000"

    ''' <summary>
    ''' 房主使用的固定虚拟 IP，用于在房间内唯一标识房主。
    ''' </summary>
    Public Const LinkHostIP As String = "10.114.114.114"

    ''' <summary>
    ''' 单个节点的测试结果。
    ''' </summary>
    Public Class LinkNodeResult
        ''' <summary>节点地址，例如 tcp://host:11010。</summary>
        Public Url As String = ""
        ''' <summary>协议，例如 tcp、udp、ws。</summary>
        Public Protocol As String = ""
        ''' <summary>主机名。</summary>
        Public Host As String = ""
        ''' <summary>端口。</summary>
        Public Port As Integer
        ''' <summary>是否连通。</summary>
        Public Success As Boolean
        ''' <summary>握手耗时 (ms)，失败为 -1。</summary>
        Public Latency As Integer = -1
        ''' <summary>补充说明，例如失败原因或“UDP 无法直接探测”。</summary>
        Public Note As String = ""
        ''' <summary>该结果是否可信。UDP 类节点的 TCP 探测结果仅供参考。</summary>
        Public Reliable As Boolean = True
    End Class

    ''' <summary>
    ''' 解析节点地址，支持 tcp/udp/ws/wss/wg/quic/faketcp/ring 协议与 IPv6。
    ''' </summary>
    Public Function LinkParsePeer(Url As String, ByRef Protocol As String, ByRef Host As String, ByRef Port As Integer) As Boolean
        Protocol = ""
        Host = ""
        Port = 0
        If String.IsNullOrWhiteSpace(Url) Then Return False
        Dim Text As String = Url.Trim()
        Dim SplitIndex As Integer = Text.IndexOf("://", StringComparison.Ordinal)
        If SplitIndex > 0 Then
            Protocol = Text.Substring(0, SplitIndex).ToLower()
            Text = Text.Substring(SplitIndex + 3)
        Else
            Protocol = "tcp"
        End If
        Text = Text.TrimEnd("/"c)
        'IPv6：[::1]:11010
        If Text.StartsWith("[") Then
            Dim CloseIndex As Integer = Text.IndexOf("]"c)
            If CloseIndex < 0 Then Return False
            Host = Text.Substring(1, CloseIndex - 1)
            Dim Rest As String = Text.Substring(CloseIndex + 1)
            If Not Rest.StartsWith(":") Then Return False
            Return Integer.TryParse(Rest.Substring(1), Port) AndAlso Port > 0 AndAlso Port <= 65535
        End If
        Dim ColonIndex As Integer = Text.LastIndexOf(":"c)
        If ColonIndex <= 0 Then Return False
        Host = Text.Substring(0, ColonIndex)
        Return Integer.TryParse(Text.Substring(ColonIndex + 1), Port) AndAlso Port > 0 AndAlso Port <= 65535
    End Function

    ''' <summary>
    ''' 判断节点地址是否可解析。
    ''' </summary>
    Public Function LinkIsValidPeer(Url As String) As Boolean
        Dim Protocol As String = "", Host As String = "", Port As Integer = 0
        Return LinkParsePeer(Url, Protocol, Host, Port)
    End Function

    ''' <summary>
    ''' 测试单个节点的连通性。
    ''' 对基于 TCP 的协议（tcp / ws / wss / faketcp）直接做 TCP 握手计时；
    ''' 对基于 UDP 的协议（udp / wg / quic / ring）同样探测该端口的 TCP 握手，
    ''' 仅作为“主机是否在线”的参考，无法据此判断 UDP 是否可用。
    ''' </summary>
    Public Function LinkTestPeer(Url As String, Optional Timeout As Integer = 3000) As LinkNodeResult
        Dim Result As New LinkNodeResult With {.Url = Url}
        Dim Protocol As String = "", Host As String = "", Port As Integer = 0
        If Not LinkParsePeer(Url, Protocol, Host, Port) Then
            Result.Note = "地址格式有误"
            Return Result
        End If
        Result.Protocol = Protocol
        Result.Host = Host
        Result.Port = Port
        Dim IsUdp As Boolean = Protocol = "udp" OrElse Protocol = "wg" OrElse Protocol = "quic" OrElse Protocol = "ring"
        If IsUdp Then Result.Reliable = False
        Dim Client As New Net.Sockets.TcpClient()
        Dim Watch As Stopwatch = Stopwatch.StartNew()
        Try
            Dim ConnectTask = Client.ConnectAsync(Host, Port)
            If Not ConnectTask.Wait(Timeout) Then
                Watch.Stop()
                Result.Note = If(IsUdp, "TCP 参考探测超时（UDP 状态未知）", "连接超时")
                Return Result
            End If
            Watch.Stop()
            If Client.Connected Then
                Result.Success = True
                Result.Latency = CInt(Watch.ElapsedMilliseconds)
                If IsUdp Then Result.Note = "TCP 参考可达"
            Else
                Result.Note = "连接失败"
            End If
        Catch ex As Exception
            Watch.Stop()
            Dim Inner As Exception = ex
            Do Until Inner.InnerException Is Nothing
                Inner = Inner.InnerException
            Loop
            Result.Note = If(Inner.Message.Length > 40, Inner.Message.Substring(0, 40) & "…", Inner.Message)
        Finally
            Try
                Client.Close()
            Catch ex As Exception
            End Try
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 并发测试一组节点，结果顺序与输入一致。
    ''' </summary>
    Public Function LinkTestPeers(Urls As IEnumerable(Of String), Optional Timeout As Integer = 3000) As List(Of LinkNodeResult)
        Dim DistinctUrls As List(Of String) = Urls.
            Where(Function(u) Not String.IsNullOrWhiteSpace(u)).Distinct().ToList()
        If Not DistinctUrls.Any Then Return New List(Of LinkNodeResult)
        Dim Results(DistinctUrls.Count - 1) As LinkNodeResult
        Parallel.For(0, DistinctUrls.Count,
            Sub(Index As Integer)
                Results(Index) = LinkTestPeer(DistinctUrls(Index), Timeout)
            End Sub)
        Return Results.Where(Function(r) r IsNot Nothing).ToList()
    End Function

    ''' <summary>
    ''' 社区订阅的缓存，避免在测试节点与创建房间时重复拉取。
    ''' </summary>
    Private _SubscriptionCache As List(Of String) = Nothing
    Private _SubscriptionCacheTime As Date = Date.MinValue

    ''' <summary>
    ''' 从社区订阅地址拉取节点列表，失败时返回空列表。结果会缓存 10 分钟。
    ''' </summary>
    Public Function LinkFetchSubscription(Optional Timeout As Integer = 5000) As List(Of String)
        If _SubscriptionCache IsNot Nothing AndAlso (Date.Now - _SubscriptionCacheTime).TotalMinutes < 10 Then
            Return New List(Of String)(_SubscriptionCache)
        End If
        Dim Result As New List(Of String)
        Try
            Dim Text As String = NetRequestByClient(LinkNodeSubscription, Timeout:=Timeout, MakeLog:=False)
            For Each Line As String In Text.Split(New String() {vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                Dim Item As String = Line.Trim()
                If Item.Contains("://") AndAlso LinkIsValidPeer(Item) Then Result.Add(Item)
            Next
            If Result.Any Then Logger.Info($"已从节点订阅获取到 {Result.Count} 个节点：{Result.Join("，")}")
        Catch ex As Exception
            Logger.Warn(ex, "获取社区节点订阅失败，将只使用内置节点")
        End Try
        _SubscriptionCache = Result
        _SubscriptionCacheTime = Date.Now
        Return New List(Of String)(Result)
    End Function

    ''' <summary>
    ''' 获取参与联机的候选节点：填写了自定义节点时只用自定义节点，
    ''' 否则使用内置节点，并尽量补充社区订阅中的节点。
    ''' </summary>
    Public Function LinkGetCandidatePeers(Optional IncludeSubscription As Boolean = True) As List(Of String)
        Dim Custom As String = Settings.Get(Of String)("LinkCustomPeer")
        If Not String.IsNullOrWhiteSpace(Custom) Then
            Return Custom.Split("，,;；".ToCharArray).
                Select(Function(p) p.Trim()).Where(Function(p) Not String.IsNullOrEmpty(p)).ToList()
        End If
        Dim Result As New List(Of String)(LinkGetDefaultPeers())
        If IncludeSubscription Then Result.AddRange(LinkFetchSubscription())
        Return Result.Where(Function(p) Not String.IsNullOrWhiteSpace(p)).Distinct().ToList()
    End Function

#End Region

#Region "局域网世界探测"

    ''' <summary>
    ''' 本机检测到的一个 Minecraft 局域网世界。
    ''' </summary>
    Public Class LinkWorld
        Public Port As Integer
        Public Motd As String = ""
        Public Version As String = ""
        Public Players As String = ""

        ''' <summary>用于下拉框显示的文本。</summary>
        Public ReadOnly Property Display As String
            Get
                If String.IsNullOrWhiteSpace(Motd) Then Return $"端口 {Port}"
                Dim Extra As String = ""
                If Not String.IsNullOrWhiteSpace(Version) Then Extra = " · " & Version
                If Not String.IsNullOrWhiteSpace(Players) Then Extra &= $" · {Players} 人"
                Return $"{Motd}{Extra} · 端口 {Port}"
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return Display
        End Function
    End Class

    <StructLayout(LayoutKind.Sequential)>
    Private Structure TcpTableRow
        Public State As Integer
        Public LocalAddr As Integer
        Public LocalPort As Integer
        Public RemoteAddr As Integer
        Public RemotePort As Integer
        Public OwningPid As Integer
    End Structure

    <DllImport("iphlpapi.dll", SetLastError:=True)>
    Private Function GetExtendedTcpTable(pTcpTable As IntPtr, ByRef pdwSize As Integer, bOrder As Boolean,
                                         ulAf As Integer, tableClass As Integer, reserved As Integer) As Integer
    End Function

    ''' <summary>
    ''' 枚举本机处于监听状态的 TCP 端口及其所属进程 ID。
    ''' </summary>
    Private Function LinkGetListeningPorts() As List(Of KeyValuePair(Of Integer, Integer))
        Dim Result As New List(Of KeyValuePair(Of Integer, Integer))
        Try
            Dim Size As Integer = 0
            '先取所需缓冲区大小（返回 122 表示缓冲区不足，属正常流程）
            GetExtendedTcpTable(IntPtr.Zero, Size, True, 2, 3, 0)
            If Size <= 0 Then Return Result
            Dim Buffer As IntPtr = Marshal.AllocHGlobal(Size)
            Try
                'tableClass = 3 表示 TCP_TABLE_OWNER_PID_LISTENER，只返回监听中的连接
                If GetExtendedTcpTable(Buffer, Size, True, 2, 3, 0) <> 0 Then Return Result
                Dim Count As Integer = Marshal.ReadInt32(Buffer)
                Dim RowPtr As IntPtr = IntPtr.Add(Buffer, 4)
                Dim RowSize As Integer = Marshal.SizeOf(Of TcpTableRow)()
                For i = 1 To Count
                    Dim Row As TcpTableRow = Marshal.PtrToStructure(Of TcpTableRow)(RowPtr)
                    '端口以网络字节序存放在低 16 位
                    Dim Port As Integer = ((Row.LocalPort And &HFFFF) >> 8) Or ((Row.LocalPort And &HFF) << 8)
                    If Port > 0 Then Result.Add(New KeyValuePair(Of Integer, Integer)(Port, Row.OwningPid))
                    RowPtr = IntPtr.Add(RowPtr, RowSize)
                Next
            Finally
                Marshal.FreeHGlobal(Buffer)
            End Try
        Catch ex As Exception
            Logger.Warn(ex, "枚举本机监听端口失败")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 用 Minecraft 状态协议探测指定端口，成功时返回世界信息，否则返回 Nothing。
    ''' </summary>
    Public Function LinkPingWorld(Port As Integer, Optional Timeout As Integer = 1500) As LinkWorld
        Try
            Using Client As New Net.Sockets.TcpClient()
                If Not Client.ConnectAsync("127.0.0.1", Port).Wait(Timeout) Then Return Nothing
                Client.ReceiveTimeout = Timeout
                Using Stream As Net.Sockets.NetworkStream = Client.GetStream()
                    '握手包：protocol = -1（不限制版本），nextState = 1（status）
                    Dim Handshake As New MemoryStream()
                    LinkWriteVarInt(Handshake, 0)
                    LinkWriteVarInt(Handshake, -1)
                    LinkWriteString(Handshake, "127.0.0.1")
                    Handshake.WriteByte(CByte(Port >> 8))
                    Handshake.WriteByte(CByte(Port And &HFF))
                    LinkWriteVarInt(Handshake, 1)
                    LinkSendPacket(Stream, Handshake.ToArray())
                    '状态请求：空包体
                    LinkSendPacket(Stream, New Byte() {0})
                    '响应：长度 + 包 ID + JSON 字符串
                    Dim Length As Integer = LinkReadVarInt(Stream)
                    If Length <= 0 OrElse Length > 4 * 1024 * 1024 Then Return Nothing
                    If LinkReadVarInt(Stream) <> 0 Then Return Nothing
                    Dim JsonLength As Integer = LinkReadVarInt(Stream)
                    If JsonLength <= 0 OrElse JsonLength > Length Then Return Nothing
                    Dim JsonBytes(JsonLength - 1) As Byte
                    Dim ReadCount As Integer = 0
                    Do While ReadCount < JsonLength
                        Dim Got As Integer = Stream.Read(JsonBytes, ReadCount, JsonLength - ReadCount)
                        If Got <= 0 Then Return Nothing
                        ReadCount += Got
                    Loop
                    Dim Json As JObject = Encoding.UTF8.GetString(JsonBytes).DeserializeJson()
                    Dim World As New LinkWorld With {.Port = Port}
                    World.Version = Json("version")?("name")?.ToString()
                    World.Motd = LinkExtractMotd(Json("description"))
                    Dim Online As String = Json("players")?("online")?.ToString()
                    Dim Max As String = Json("players")?("max")?.ToString()
                    If Not String.IsNullOrEmpty(Online) Then World.Players = $"{Online}/{Max}"
                    Return World
                End Using
            End Using
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 提取 MOTD 文本（description 可能是字符串，也可能是带 text / extra 的对象）。
    ''' </summary>
    Private Function LinkExtractMotd(Desc As JToken) As String
        If Desc Is Nothing Then Return ""
        Dim Text As String = ""
        If Desc.Type = JTokenType.String Then
            Text = Desc.ToString()
        Else
            Text = If(Desc("text")?.ToString(), "")
            Dim Extra As JArray = TryCast(Desc("extra"), JArray)
            If Extra IsNot Nothing Then
                For Each Item As JToken In Extra
                    If Item.Type = JTokenType.String Then
                        Text &= Item.ToString()
                    Else
                        Text &= If(Item("text")?.ToString(), "")
                    End If
                Next
            End If
        End If
        '去掉颜色代码
        Text = System.Text.RegularExpressions.Regex.Replace(Text, "§.", "")
        Text = Text.Replace(vbCrLf, " ").Replace(vbLf, " ").Replace(vbCr, " ").Trim()
        If Text.Length > 40 Then Text = Text.Substring(0, 40) & "…"
        Return Text
    End Function

    Private Sub LinkWriteVarInt(Stream As Stream, Value As Integer)
        '注意：protocol version 需要写 -1，负数要按补码当作 UInt32 处理。
        '直接用 CUInt(-1) 会抛 OverflowException，导致整个探测被 catch 吞掉、判定为“不是 MC”。
        Dim Unsigned As UInteger = BitConverter.ToUInt32(BitConverter.GetBytes(Value), 0)
        Do
            Dim Temp As Byte = CByte(Unsigned And &H7FUI)
            Unsigned >>= 7
            If Unsigned <> 0UI Then Temp = CByte(Temp Or &H80)
            Stream.WriteByte(Temp)
        Loop While Unsigned <> 0UI
    End Sub

    Private Sub LinkWriteString(Stream As Stream, Text As String)
        Dim Bytes As Byte() = Encoding.UTF8.GetBytes(Text)
        LinkWriteVarInt(Stream, Bytes.Length)
        Stream.Write(Bytes, 0, Bytes.Length)
    End Sub

    Private Function LinkReadVarInt(Stream As Stream) As Integer
        Dim Result As Integer = 0, ReadCount As Integer = 0
        Do
            Dim Current As Integer = Stream.ReadByte()
            If Current < 0 Then Throw New IO.EndOfStreamException("连接已断开")
            Result = Result Or ((Current And &H7F) << (7 * ReadCount))
            ReadCount += 1
            If ReadCount > 5 Then Throw New Exception("VarInt 长度非法")
            If (Current And &H80) = 0 Then Return Result
        Loop
    End Function

    Private Sub LinkSendPacket(Stream As Stream, Body As Byte())
        LinkWriteVarInt(Stream, Body.Length)
        Stream.Write(Body, 0, Body.Length)
        Stream.Flush()
    End Sub

    ''' <summary>
    ''' 探测本机正在运行的 Minecraft 局域网世界（扫描 java / javaw 进程监听的端口，
    ''' 再用状态协议确认其确为 MC 服务器）。
    ''' </summary>
    Public Function LinkDiscoverWorlds(Optional Timeout As Integer = 1200) As List(Of LinkWorld)
        Dim Result As New List(Of LinkWorld)
        Try
            '1. 找出所有 Java 进程
            Dim Pids As New HashSet(Of Integer)
            For Each ProcessName As String In {"java", "javaw"}
                Try
                    For Each JavaProcess As Process In Process.GetProcessesByName(ProcessName)
                        Pids.Add(JavaProcess.Id)
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, $"枚举 {ProcessName} 进程失败")
                End Try
            Next
            If Not Pids.Any Then Return Result
            '2. 取这些进程正在监听的端口
            Dim Ports As New List(Of Integer)
            For Each Item As KeyValuePair(Of Integer, Integer) In LinkGetListeningPorts()
                If Pids.Contains(Item.Value) AndAlso Not Ports.Contains(Item.Key) Then Ports.Add(Item.Key)
            Next
            If Not Ports.Any Then Return Result
            '3. 并行用状态协议验证（端口数量有限，给个上限防止极端情况拖太久）
            Dim Candidates As List(Of Integer) = Ports.Take(16).ToList()
            Dim Probes(Candidates.Count - 1) As LinkWorld
            Parallel.For(0, Candidates.Count,
                Sub(Index As Integer)
                    Probes(Index) = LinkPingWorld(Candidates(Index), Timeout)
                End Sub)
            Result.AddRange(Probes.Where(Function(w) w IsNot Nothing))
            '4. 一个都没识别出来时，仍然把 Java 进程的端口列出来供手动选择
            '   （例如 1.6 及更早的版本，或装了特殊 Mod 导致状态协议不响应）
            If Not Result.Any Then
                For Each Port As Integer In Candidates
                    Result.Add(New LinkWorld With {.Port = Port, .Motd = "未识别的端口"})
                Next
                Logger.Warn($"Java 进程的 {Result.Count} 个端口都没有响应状态协议，仍列为候选")
            End If
            Result.Sort(Function(a, b) a.Port.CompareTo(b.Port))
            Logger.Info($"检测到 {Result.Count} 个局域网世界：{Result.Select(Function(w) $"{w.Port}({w.Motd})").Join("，")}")
        Catch ex As Exception
            Logger.Warn(ex, "探测局域网世界失败")
        End Try
        Return Result
    End Function

#End Region

End Module
