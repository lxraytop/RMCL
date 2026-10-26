Imports System.Diagnostics
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

''' <summary>
''' 樱花穿透（SakuraFrp / natfrp）客户端封装。
'''
''' 用法与官方一致：spawn 官方 frpc，命令行 `frpc -f &lt;访问密钥&gt;:&lt;隧道 ID&gt; --disable_log_color`，
''' 然后解析输出：启动成功会打印 "start proxy success" 与远程地址，
''' 失败会打印 "invalid token" / "tunnel not exists" / "login to server failed"。
'''
''' 隧道列表通过官方 API v4 读取（需要访问密钥，Header 用 Bearer）。
''' 文档：https://doc.natfrp.com/frpc/usage.html 、 https://api.natfrp.com/docs/
''' </summary>
Public Module ModFrp

#Region "常量"

    ''' <summary>樱花穿透 API 基地址。</summary>
    Public Const ApiBase As String = "https://api.natfrp.com/v4"

    ''' <summary>官方 frpc 的版本目录（nginx 目录列表，可从中解析出最新版本）。</summary>
    Public Const FrpcBase As String = "https://nya.globalslb.net/natfrp/client/frpc/"

    ''' <summary>目录列表取不到时使用的版本（已验证可用）。</summary>
    Public Const FrpcFallbackVersion As String = "0.51.0-sakura-15.1"

    ''' <summary>下载完整性下限，实际约 14 MB。</summary>
    Public Const FrpcMinSize As Long = 10 * 1024 * 1024

    ''' <summary>最多保留多少条日志。</summary>
    Private Const MaxLogs As Integer = 200

#End Region

#Region "状态"

    Public Enum FrpStatus
        ''' <summary>尚未启动。</summary>
        Idle
        ''' <summary>正在连接。</summary>
        Starting
        ''' <summary>隧道已建立。</summary>
        Running
        ''' <summary>访问密钥无效。</summary>
        AuthFailed
        ''' <summary>隧道不存在或已被禁用。</summary>
        TunnelOffline
        ''' <summary>客户端异常。</summary>
        [Error]
        ''' <summary>已停止。</summary>
        Stopped
    End Enum

    Private ReadOnly StateLock As New Object
    Private StatusValue As FrpStatus = FrpStatus.Idle
    Private RemoteValue As String = ""
    Private MessageValue As String = "尚未启动"
    Private ReadOnly LogList As New List(Of String)
    Private Session As Process = Nothing

    ''' <summary>当前状态。</summary>
    Public ReadOnly Property Status As FrpStatus
        Get
            SyncLock StateLock
                Return StatusValue
            End SyncLock
        End Get
    End Property

    ''' <summary>隧道建立后由 frpc 输出的远程地址（host:port），未建立时为空。</summary>
    Public ReadOnly Property RemoteAddress As String
        Get
            SyncLock StateLock
                Return RemoteValue
            End SyncLock
        End Get
    End Property

    ''' <summary>状态的文字说明。</summary>
    Public ReadOnly Property StatusMessage As String
        Get
            SyncLock StateLock
                Return MessageValue
            End SyncLock
        End Get
    End Property

    ''' <summary>是否正在运行（含启动中）。</summary>
    Public ReadOnly Property IsActive As Boolean
        Get
            Dim S As FrpStatus
            SyncLock StateLock
                S = StatusValue
            End SyncLock
            Return S = FrpStatus.Starting OrElse S = FrpStatus.Running
        End Get
    End Property

    ''' <summary>取一份日志快照，最新的在最后。</summary>
    Public Function GetLogs() As List(Of String)
        SyncLock StateLock
            Return LogList.ToList()
        End SyncLock
    End Function

    Public Sub ClearLogs()
        SyncLock StateLock
            LogList.Clear()
        End SyncLock
    End Sub

    Private Sub SetStatus(NewStatus As FrpStatus, Optional NewMessage As String = Nothing, Optional NewRemote As String = Nothing)
        SyncLock StateLock
            StatusValue = NewStatus
            If NewMessage IsNot Nothing Then MessageValue = NewMessage
            If NewRemote IsNot Nothing Then RemoteValue = NewRemote
        End SyncLock
    End Sub

    Private Sub AddLog(Text As String)
        If String.IsNullOrWhiteSpace(Text) Then Return
        SyncLock StateLock
            LogList.Add($"{Now:HH:mm:ss}  {Text}")
            If LogList.Count > MaxLogs Then LogList.RemoveRange(0, LogList.Count - MaxLogs)
        End SyncLock
    End Sub

    ''' <summary>把状态翻译成给用户看的一句话。</summary>
    Private Function MessageOf(S As FrpStatus) As String
        Select Case S
            Case FrpStatus.Starting
                Return "正在连接樱花穿透……"
            Case FrpStatus.Running
                Dim Addr As String = RemoteAddress
                Return If(Addr = "", "隧道已建立，等待远程地址……", $"已连接，远程地址 {Addr}")
            Case FrpStatus.AuthFailed
                Return "访问密钥无效或已失效，请到樱花穿透用户信息页重新复制"
            Case FrpStatus.TunnelOffline
                Return "隧道不存在或已被禁用，请检查隧道 ID 与隧道状态"
            Case FrpStatus.Error
                Return "樱花穿透客户端异常退出"
            Case FrpStatus.Stopped
                Return "已停止"
            Case Else
                Return "尚未启动"
        End Select
    End Function

#End Region

#Region "客户端"

    ''' <summary>frpc 的本地路径（放在缓存目录，随缓存清理重新下载）。</summary>
    Public ReadOnly Property FrpcPath As String
        Get
            Return PathTemp & "Frp\frpc.exe"
        End Get
    End Property

    ''' <summary>
    ''' 解析当前最新的 frpc 版本号并从官方 CDN 下载。
    ''' 不能写死版本：官方会下线旧版本目录（0.51.0-sakura-14 已经 404），
    ''' 写死会导致更新后一次性失效。取不到目录列表时退回内置版本。
    ''' </summary>
    Public Function ResolveFrpcVersion() As String
        Try
            Dim Html As String = NetRequestByClient(FrpcBase, Timeout:=12000, MakeLog:=False)
            Dim Best As String = ""
            Dim BestParts As Integer() = Nothing
            For Each M As Match In Regex.Matches(Html, "href=""(\d+\.\d+\.\d+-sakura-\d+(?:\.\d+)?)/""")
                Dim Ver As String = M.Groups(1).Value
                Dim Parts As Integer() = VersionParts(Ver)
                If Parts Is Nothing Then Continue For
                If BestParts Is Nothing OrElse CompareVersion(Parts, BestParts) > 0 Then
                    Best = Ver
                    BestParts = Parts
                End If
            Next
            If Best <> "" Then Return Best
        Catch ex As Exception
            Logger.Warn(ex, "读取樱花穿透客户端版本列表失败，改用内置版本")
        End Try
        Return FrpcFallbackVersion
    End Function

    Private Function VersionParts(Ver As String) As Integer()
        Dim M As Match = Regex.Match(Ver, "^(\d+)\.(\d+)\.(\d+)-sakura-(\d+)(?:\.(\d+))?$")
        If Not M.Success Then Return Nothing
        Return {Val(M.Groups(1).Value), Val(M.Groups(2).Value), Val(M.Groups(3).Value),
                Val(M.Groups(4).Value), If(M.Groups(5).Success, Val(M.Groups(5).Value), 0)}
    End Function

    Private Function CompareVersion(A As Integer(), B As Integer()) As Integer
        For i As Integer = 0 To Math.Min(A.Length, B.Length) - 1
            If A(i) <> B(i) Then Return A(i).CompareTo(B(i))
        Next
        Return A.Length.CompareTo(B.Length)
    End Function

    ''' <summary>拼出 Windows amd64 客户端的下载地址。</summary>
    Public Function ResolveFrpcUrl() As String
        Return FrpcBase & ResolveFrpcVersion() & "/frpc_windows_amd64.exe"
    End Function

    ''' <summary>确保 frpc 已就绪，未就绪时从官方地址下载。会阻塞，调用方应放在后台线程。</summary>
    Public Function EnsureFrpc(Optional OnLog As Action(Of String) = Nothing) As String
        Dim Target As String = FrpcPath
        If FileUtils.Exists(Target) AndAlso FileUtils.GetInfo(Target).Length > FrpcMinSize Then Return Target
        DirectoryUtils.Create(PathTemp & "Frp\")
        OnLog?.Invoke("正在获取樱花穿透客户端……")
        Dim Url As String = ResolveFrpcUrl()
        Logger.Info($"正在下载樱花穿透客户端：{Url}")
        OnLog?.Invoke("正在下载樱花穿透客户端，约 14 MB……")
        Try
            NetDownloadByLoader(Url, Target)
        Catch ex As Exception
            Throw New Exception($"樱花穿透客户端下载失败：{ex.Message}。官方下载地址：{Url}")
        End Try
        If Not FileUtils.Exists(Target) Then Throw New Exception("樱花穿透客户端下载失败，请检查网络后重试")
        If FileUtils.GetInfo(Target).Length < FrpcMinSize Then
            FileUtils.Delete(Target)
            Throw New Exception("樱花穿透客户端下载不完整，请重试")
        End If
        OnLog?.Invoke("樱花穿透客户端已就绪")
        Logger.Info("樱花穿透客户端已就绪")
        Return Target
    End Function

    ''' <summary>延迟清理：记录状态供界面显示，不阻塞。</summary>
    Public Function IsFrpcReady() As Boolean
        Return FileUtils.Exists(FrpcPath) AndAlso FileUtils.GetInfo(FrpcPath).Length > FrpcMinSize
    End Function

    ''' <summary>访问密钥打码，只保留前 2 后 2，避免完整出现在日志里。</summary>
    Public Function MaskKey(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return ""
        If Value.Length <= 4 Then Return New String("*"c, Value.Length)
        Return Value.Substring(0, 2) & "***" & Value.Substring(Value.Length - 2)
    End Function

#End Region

#Region "启动与停止"

    ''' <summary>
    ''' 启动隧道。本地端口由樱花面板里的隧道配置决定，这里只负责把隧道挂起来。
    ''' 会阻塞在客户端下载与进程创建上，调用方应放在后台线程。
    ''' </summary>
    Public Sub StartSession(AccessKey As String, TunnelId As String)
        AccessKey = If(AccessKey, "").Trim()
        TunnelId = If(TunnelId, "").Trim()
        If AccessKey = "" Then Throw New Exception("请先填写访问密钥")
        If TunnelId = "" Then Throw New Exception("请先选择要启动的隧道")
        SyncLock StateLock
            If Session IsNot Nothing Then Throw New Exception("樱花穿透已在运行中，请先停止")
        End SyncLock

        Dim Exe As String = EnsureFrpc(Sub(Text) SetStatus(FrpStatus.Starting, Text))
        Dim Args As String = $"-f {AccessKey}:{TunnelId} --disable_log_color"
        Dim Psi As New ProcessStartInfo(Exe, Args) With {
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8,
            .WorkingDirectory = PathTemp & "Frp\"
        }
        Dim P As Process
        Try
            P = Process.Start(Psi)
        Catch ex As Exception
            SetStatus(FrpStatus.Error, "樱花穿透客户端启动失败：" & ex.Message)
            Throw New Exception("樱花穿透客户端启动失败：" & ex.Message)
        End Try
        If P Is Nothing Then Throw New Exception("樱花穿透客户端启动失败")

        SyncLock StateLock
            Session = P
            LogList.Clear()
            RemoteValue = ""
        End SyncLock
        SetStatus(FrpStatus.Starting, "正在连接樱花穿透……")
        AddLog($"启动命令：frpc -f {MaskKey(AccessKey)}:{TunnelId}")

        P.EnableRaisingEvents = True
        AddHandler P.Exited, Sub(Sender, E) OnProcessExited(P)

        '两个流各一条线程：BeginOutputReadLine 的编码处理不如直接 ReadLine 可控
        RunInNewThread(Sub() ReadStream(P.StandardOutput, "stdout"), "樱花穿透 stdout")
        RunInNewThread(Sub() ReadStream(P.StandardError, "stderr"), "樱花穿透 stderr")
    End Sub

    Private Sub ReadStream(Reader As IO.StreamReader, StreamName As String)
        Try
            Do While True
                Dim Line As String = Reader.ReadLine()
                If Line Is Nothing Then Exit Do
                HandleLine(Line)
            Loop
        Catch ex As Exception
            Logger.Warn(ex, $"读取樱花穿透 {StreamName} 失败")
        End Try
    End Sub

    Private Sub OnProcessExited(P As Process)
        Dim Code As Integer = -1
        Try
            Code = P.ExitCode
        Catch
        End Try
        AddLog($"frpc 已退出（退出码 {Code}）")
        Dim StillCurrent As Boolean
        SyncLock StateLock
            StillCurrent = (Session Is P)
            If StillCurrent Then Session = Nothing
        End SyncLock
        If Not StillCurrent Then Return
        Select Case Status
            Case FrpStatus.AuthFailed, FrpStatus.TunnelOffline
                '保留失败原因，不要被"已停止"覆盖
            Case FrpStatus.Stopped
                '用户主动停止
            Case Else
                If Code = 0 Then
                    SetStatus(FrpStatus.Stopped, "已停止")
                Else
                    SetStatus(FrpStatus.Error, $"樱花穿透客户端异常退出（退出码 {Code}），可展开日志查看原因")
                End If
        End Select
    End Sub

    ''' <summary>停止隧道。不阻塞，进程由 taskkill 兜底结束。</summary>
    Public Sub StopSession()
        Dim P As Process
        SyncLock StateLock
            P = Session
            Session = Nothing
        End SyncLock
        If P Is Nothing Then Return
        SetStatus(FrpStatus.Stopped, "已停止")
        AddLog("正在停止樱花穿透……")
        Try
            If Not P.HasExited Then
                'frpc 可能派生子进程，parent.Kill 不一定能全部收掉，用 taskkill /T 兜底
                Try
                    Process.Start(New ProcessStartInfo("taskkill", $"/T /F /PID {P.Id}") With {
                        .CreateNoWindow = True, .UseShellExecute = False, .WindowStyle = ProcessWindowStyle.Hidden
                    })
                Catch
                    P.Kill()
                End Try
            End If
        Catch ex As Exception
            Logger.Warn(ex, "停止樱花穿透失败")
        End Try
    End Sub

#End Region

#Region "日志解析"

    ''' <summary>解析一行 frpc 输出：更新状态与远程地址。</summary>
    Private Sub HandleLine(Raw As String)
        '去掉 ANSI 颜色控制符
        Dim Text As String = Regex.Replace(Raw, "\x1b\[[0-9;]*[A-Za-z]", "").Trim()
        If Text = "" Then Return
        AddLog(Text)

        Dim NewStatus As FrpStatus? = Nothing
        If Regex.IsMatch(Text, "invalid token|login to server failed|authorization failed|认证失败", RegexOptions.IgnoreCase) Then
            NewStatus = FrpStatus.AuthFailed
        ElseIf Regex.IsMatch(Text, "tunnel not exists|tunnel offline|proxy not found|隧道不存在|隧道已被禁用", RegexOptions.IgnoreCase) Then
            NewStatus = FrpStatus.TunnelOffline
        ElseIf Regex.IsMatch(Text, "start proxy (error|failed)|隧道启动失败", RegexOptions.IgnoreCase) Then
            NewStatus = FrpStatus.Error
        ElseIf Regex.IsMatch(Text, "\b(start )?proxy success\b|隧道启动成功", RegexOptions.IgnoreCase) Then
            NewStatus = FrpStatus.Running
        End If

        Dim Addr As String = ParseAddress(Text)
        If Addr <> "" AndAlso Addr <> RemoteAddress Then
            SetStatus(If(NewStatus, Status), Nothing, Addr)
            Logger.Info($"樱花穿透远程地址：{Addr}")
        End If

        If NewStatus.HasValue Then
            Logger.Info($"樱花穿透状态：{NewStatus.Value}")
            SetStatus(NewStatus.Value, MessageOf(NewStatus.Value))
        End If
    End Sub

    ''' <summary>
    ''' 从日志行里挑出远程地址。只接受 frpc 打印的地址，排除本地目标、时间戳与文档链接。
    ''' 带「远程地址」「需要输 IP:端口」等上下文，或域名属于樱花自己的域时才算。
    ''' </summary>
    Private Function ParseAddress(Text As String) As String
        Dim Contextual As Boolean = Regex.IsMatch(Text, "需要输\s*IP\s*[:：]\s*端口|远程地址|remote address|(电信|联通|移动)\s*[:：]", RegexOptions.IgnoreCase)
        For Each M As Match In Regex.Matches(Text, "((?:[a-z0-9-]+\.)+[a-z0-9-]+)\s*:\s*(\d{1,5})(?!\d)", RegexOptions.IgnoreCase)
            Dim Host As String = M.Groups(1).Value
            Dim Port As Integer = Val(M.Groups(2).Value)
            If Host.StartsWithF("127.") OrElse
               Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) OrElse
               Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) Then Continue For
            If Port <= 0 OrElse Port > 65535 Then Continue For
            Dim Known As Boolean = Regex.IsMatch(Host, "\.(natfrp\.cloud|nyatwork\.cn|frp-hub\.com|frp\.com|frp\.net)$", RegexOptions.IgnoreCase)
            If Contextual OrElse Known Then Return Host & ":" & Port
        Next
        Return ""
    End Function

#End Region

#Region "官方 API"

    ''' <summary>一条樱花隧道。</summary>
    Public Class FrpTunnel
        Public Id As Integer
        Public Name As String = ""
        Public NodeId As Integer
        Public NodeName As String = ""
        Public Online As Boolean
        Public Banned As Boolean
        Public LocalPort As Integer
        Public Remote As String = ""

        Public Overrides Function ToString() As String
            Dim Parts As New List(Of String) From {$"{Name}（#{Id}）"}
            If NodeName <> "" Then Parts.Add(NodeName)
            If LocalPort > 0 Then Parts.Add($"本地 {LocalPort}")
            If Banned Then
                Parts.Add("已封禁")
            ElseIf Not Online Then
                Parts.Add("离线")
            End If
            Return String.Join(" · ", Parts)
        End Function
    End Class

    ''' <summary>读取当前账号的隧道列表。会阻塞，调用方应放在后台线程。</summary>
    Public Function FetchTunnels(AccessKey As String) As List(Of FrpTunnel)
        AccessKey = If(AccessKey, "").Trim()
        If AccessKey = "" Then Throw New Exception("请先填写访问密钥")
        Dim Raw As JToken = FrpApiRequest("/tunnels", AccessKey)
        Dim Result As New List(Of FrpTunnel)
        If Raw.Type <> JTokenType.Array Then Throw New Exception("樱花穿透接口返回了意料之外的格式")
        For Each Item As JToken In CType(Raw, JArray)
            Dim T As New FrpTunnel With {
                .Id = Val(Item("id")?.ToString),
                .Name = If(Item("name")?.ToString, ""),
                .NodeId = Val(Item("node")?.ToString),
                .Online = Item("online")?.Value(Of Boolean) = True,
                .Banned = Val(Item("status")?.ToString) = 2,
                .LocalPort = Val(Item("local_port")?.ToString),
                .Remote = If(Item("remote")?.ToString, "")
            }
            Result.Add(T)
        Next
        '顺带补上节点名，失败不影响主流程
        Try
            Dim Nodes As JToken = FrpApiRequest("/nodes", AccessKey)
            If Nodes.Type = JTokenType.Object Then
                For Each T In Result
                    Dim N As JToken = CType(Nodes, JObject)(T.NodeId.ToString)
                    If N IsNot Nothing Then T.NodeName = If(N("name")?.ToString, "")
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取樱花穿透节点列表失败")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 发一次带密钥的 API 请求，把非 2xx 的错误翻译成用户能看懂的话。
    ''' Body 为 Nothing 时用 GET，否则用 POST + JSON。
    ''' 请求带 no-cache：项目里的 HttpClient 挂了 CacheCow，不加会读到旧的隧道列表。
    ''' </summary>
    Private Function FrpApiRequest(Path As String, AccessKey As String, Optional Body As JObject = Nothing) As JToken
        Dim Headers As String(,) = {
            {"Authorization", "Bearer " & AccessKey},
            {"Cache-Control", "no-cache"}
        }
        Try
            Dim Raw As String
            If Body Is Nothing Then
                Raw = NetRequestByClient(ApiBase & Path, Headers:=Headers,
                    Accept:="application/json", RequireJson:=True)
            Else
                Raw = NetRequestByClient(ApiBase & Path, Method:=HttpMethod.Post, Content:=Body.ToString(),
                    ContentType:="application/json", Headers:=Headers,
                    Accept:="application/json", RequireJson:=True)
            End If
            Return JToken.Parse(Raw)
        Catch ex As Exception
            '接口在鉴权失败时仍会返回 {"code":401,"msg":"访问密钥无效"}，从异常文本里把它捞出来
            Dim M As Match = Regex.Match(If(ex.Message, ""), """msg""\s*:\s*""([^""]*)""")
            If M.Success Then
                Dim Detail As String = M.Groups(1).Value
                If Detail.Contains("密钥") Then Throw New Exception("访问密钥无效，请退出登录后重新输入")
                Throw New Exception("樱花穿透接口返回：" & Detail)
            End If
            Throw New Exception("连接樱花穿透接口失败：" & ex.Message)
        End Try
    End Function

    ''' <summary>樱花的一个服务节点。</summary>
    Public Class FrpNode
        Public Id As Integer
        Public Name As String = ""
        Public Host As String = ""
        Public Description As String = ""
        Public Vip As Integer
        Public IsFree As Boolean
        Public Online As Boolean
        Public Load As Integer = -1
        Public CanCreate As Boolean

        Public Overrides Function ToString() As String
            Dim Parts As New List(Of String) From {$"{Name}（#{Id}）"}
            Parts.Add(If(IsFree, "免费", "专业版"))
            If Load >= 0 Then Parts.Add(Load & "%")
            If Description <> "" Then Parts.Add(Description)
            If Not Online Then Parts.Add("离线")
            If Not CanCreate Then Parts.Add("已满")
            Return String.Join(" · ", Parts)
        End Function
    End Class

    ''' <summary>
    ''' 读取节点列表。/nodes 给节点信息（vip、flag 位），/node/stats 给在线与负载。
    ''' 统计拿不到不影响主流程，退回用 flag 的离线位判断。
    ''' </summary>
    Public Function FetchNodes(AccessKey As String) As List(Of FrpNode)
        AccessKey = If(AccessKey, "").Trim()
        If AccessKey = "" Then Throw New Exception("请先填写访问密钥")
        Dim Raw As JToken = FrpApiRequest("/nodes", AccessKey)
        If Raw.Type <> JTokenType.Object Then Throw New Exception("樱花穿透节点接口返回了意料之外的格式")

        Dim Stats As New Dictionary(Of Integer, JToken)
        Try
            Dim S As JToken = FrpApiRequest("/node/stats", AccessKey)
            If S("nodes") IsNot Nothing Then
                For Each N As JToken In CType(S("nodes"), JArray)
                    Stats(Val(N("id")?.ToString)) = N
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取樱花穿透节点状态失败")
        End Try

        Dim Result As New List(Of FrpNode)
        For Each Prop As JProperty In CType(Raw, JObject).Properties
            Dim Item As JToken = Prop.Value
            If Item Is Nothing OrElse Item.Type <> JTokenType.Object Then Continue For
            Dim Id As Integer = Val(Prop.Name)
            If Id <= 0 Then Continue For
            Dim Flags As Integer = Val(Item("flag")?.ToString)
            Dim Vip As Integer = Val(Item("vip")?.ToString)
            Dim Node As New FrpNode With {
                .Id = Id,
                .Name = If(Item("name")?.ToString, ""),
                .Host = If(Item("host")?.ToString, ""),
                .Description = If(Item("description")?.ToString, ""),
                .Vip = Vip,
                .IsFree = (Vip = 0),
                .Online = (Flags And (1 << 9)) = 0,
                .CanCreate = (Flags And (1 << 2)) <> 0
            }
            Dim St As JToken = Nothing
            If Stats.TryGetValue(Id, St) AndAlso St IsNot Nothing Then
                Node.Online = Val(St("online")?.ToString) >= 0
                Node.Load = Val(St("load")?.ToString)
            End If
            Result.Add(Node)
        Next
        '免费优先、在线优先、ID 升序：下拉框最上面就是能直接用的节点
        Return Result.OrderByDescending(Function(N) N.IsFree).
                      ThenByDescending(Function(N) N.Online).
                      ThenBy(Function(N) N.Id).ToList()
    End Function

    ''' <summary>
    ''' 创建一条 TCP 隧道。会阻塞，调用方应放在后台线程。
    ''' 不做自动重试：POST 重试可能创建出重复隧道。
    ''' </summary>
    Public Function CreateTunnel(AccessKey As String, Name As String, NodeId As Integer,
                                 LocalPort As Integer, Optional RemotePort As Integer = 0) As FrpTunnel
        AccessKey = If(AccessKey, "").Trim()
        Name = If(Name, "").Trim()
        If AccessKey = "" Then Throw New Exception("请先登录樱花穿透")
        If Name.Length = 0 OrElse Name.Length > 64 Then Throw New Exception("隧道名称需要 1-64 个字符")
        If NodeId <= 0 Then Throw New Exception("请选择服务节点")
        If LocalPort < 1 OrElse LocalPort > 65535 Then Throw New Exception("本地端口需要是 1-65535")
        If RemotePort < 0 OrElse RemotePort > 65535 Then Throw New Exception("远程端口需要是 1-65535")

        Dim Body As New JObject From {
            {"name", Name},
            {"type", "tcp"},
            {"node", NodeId},
            {"local_ip", "127.0.0.1"},
            {"local_port", LocalPort}
        }
        '留空则由樱花分配
        If RemotePort > 0 Then Body("remote") = RemotePort.ToString()

        Dim Result As JToken = FrpApiRequest("/tunnels", AccessKey, Body)
        Dim Id As Integer = Val(Result("id")?.ToString)
        If Id <= 0 Then Throw New Exception("樱花穿透没有返回隧道 ID，请到网站确认是否已创建，不要重复提交")
        Logger.Info($"樱花穿透隧道已创建：#{Id} {Name}（节点 {NodeId}，本地 {LocalPort}）")
        Return New FrpTunnel With {
            .Id = Id,
            .Name = If(Result("name")?.ToString, Name),
            .NodeId = NodeId,
            .LocalPort = LocalPort,
            .Online = True
        }
    End Function

#End Region

End Module
