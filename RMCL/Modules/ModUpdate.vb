Imports System.Text.RegularExpressions

''' <summary>
''' 启动器更新：从 GitHub Release 检查新版本、下载新的 exe，并在退出时替换自身。
''' 发布约定：Release 资产里直接放 RMCL.exe（不再打包 zip），所以这里只挑 .exe 资产。
''' </summary>
Friend Module ModUpdate

#Region "声明"

    ''' <summary>
    ''' 更新源仓库。
    ''' </summary>
    Public Const UpdateRepo As String = "lxraytop/RMCL"
    ''' <summary>
    ''' 获取最新 Release 的接口地址。
    ''' </summary>
    Public Const UpdateApi As String = "https://api.github.com/repos/lxraytop/RMCL/releases/latest"
    ''' <summary>
    ''' 发布页地址（没有新版本或获取失败时供用户手动查看）。
    ''' </summary>
    Public Const UpdatePage As String = "https://github.com/lxraytop/RMCL/releases/latest"
    ''' <summary>
    ''' 更新下载的最大并发线程数。
    ''' 全局默认是 ToolDownloadThread + 1（63 + 1 = 64），那是为批量小文件设计的；
    ''' 单个 5 MB 文件被切成 64 段、每段一个请求，反而会撞上镜像按 IP 的连接数限速，
    ''' 实测 64 并发比 32 并发慢 4 倍，这里压到 24。
    ''' </summary>
    Public Const UpdateThreadLimit As Integer = 24

    ''' <summary>
    ''' 更新源。GitHub 的两个源不需要授权；官方源是自建服务器，下载前必须通过验证码授权。
    ''' </summary>
    Public Enum UpdateSource
        ''' <summary>GitHub 原生：检查与下载都走 github.com。</summary>
        GitHubNative = 0
        ''' <summary>GitHub 镜像：检查与下载都先经用户自填的镜像地址，失败再回退官方。</summary>
        GitHubMirror = 1
        ''' <summary>官方源：自建更新服务器，需要验证码授权，授权到期后拒绝更新。</summary>
        Official = 2
    End Enum

    ''' <summary>默认镜像地址，用户可以在设置里改。</summary>
    Public Const DefaultMirrorBase As String = "https://gh-proxy.com/"

    ''' <summary>当前选择的更新源。</summary>
    Public ReadOnly Property CurrentUpdateSource As UpdateSource
        Get
            Try
                Select Case Settings.Get(Of Integer)("UpdateSourceMode")
                    Case 1
                        Return UpdateSource.GitHubMirror
                    Case 2
                        Return UpdateSource.Official
                    Case Else
                        Return UpdateSource.GitHubNative
                End Select
            Catch ex As Exception
                Logger.Warn(ex, "读取更新源设置失败")
                Return UpdateSource.GitHubNative
            End Try
        End Get
    End Property

    ''' <summary>镜像地址，保证以 / 结尾且带协议。</summary>
    Public ReadOnly Property MirrorBase As String
        Get
            Dim Raw As String = ""
            Try
                Raw = Settings.Get(Of String)("UpdateMirrorUrl")
            Catch ex As Exception
                Logger.Warn(ex, "读取镜像地址失败")
            End Try
            If String.IsNullOrWhiteSpace(Raw) Then Raw = DefaultMirrorBase
            Raw = Raw.Trim().Replace("\", "/")
            If Not Raw.StartsWithF("http://", True) AndAlso Not Raw.StartsWithF("https://", True) Then
                Raw = "https://" & Raw
            End If
            If Not Raw.EndsWithF("/") Then Raw &= "/"
            Return Raw
        End Get
    End Property

    Private Function MirrorUrl(Original As String) As String
        If String.IsNullOrEmpty(Original) Then Return Original
        Return MirrorBase & Original
    End Function

    ''' <summary>当前源是否只能通过授权使用。</summary>
    Public ReadOnly Property NeedsLicense As Boolean
        Get
            Return CurrentUpdateSource = UpdateSource.Official
        End Get
    End Property

    ''' <summary>更新源的一行说明，供设置页与提示使用。</summary>
    Public ReadOnly Property SourceDescription As String
        Get
            Select Case CurrentUpdateSource
                Case UpdateSource.GitHubNative
                    Return $"GitHub 原生源（{UpdateRepo}）"
                Case UpdateSource.GitHubMirror
                    Return $"GitHub 镜像源（{MirrorBase}）"
                Case Else
                    Dim Url As String = ModUpdateServer.ConfiguredServerUrl
                    Return If(Url = "", "官方源（尚未填写服务器地址）", $"官方源（{Url}）")
            End Select
        End Get
    End Property

    ''' <summary>
    ''' 一次更新检查的结果。
    ''' </summary>
    Public Class UpdateInfo
        ''' <summary>版本号，已去掉 tag 的 v 前缀，例如 "2.16.0.1"。</summary>
        Public Version As String = ""
        ''' <summary>Release 标题。</summary>
        Public Title As String = ""
        ''' <summary>Release 公告（已去掉 Markdown 标记，可直接显示）。</summary>
        Public Notes As String = ""
        ''' <summary>发布页地址。</summary>
        Public PageUrl As String = UpdatePage
        ''' <summary>exe 资产的文件名。</summary>
        Public AssetName As String = ""
        ''' <summary>exe 资产的下载地址。</summary>
        Public AssetUrl As String = ""
        ''' <summary>exe 资产的字节数，0 表示未知。</summary>
        Public AssetSize As Long = 0
        ''' <summary>发布时间（原样字符串，仅用于日志）。</summary>
        Public PublishedAt As String = ""
        ''' <summary>发布时间，已换算到本地时区；取不到时为 Nothing。</summary>
        Public PublishedLocal As DateTime? = Nothing
        ''' <summary>官方源上的发布 ID，用于换取下载票据；非官方源为 0。</summary>
        Public ReleaseId As Integer = 0
        ''' <summary>发布包的大小写无关 SHA-256；为空表示服务器没提供，跳过校验。</summary>
        Public Sha256 As String = ""
        ''' <summary>是否来自自建官方源（需要授权、下载前换票据）。</summary>
        Public IsOfficial As Boolean = False
        ''' <summary>是否带有可下载的 exe。</summary>
        Public ReadOnly Property HasAsset As Boolean
            Get
                If IsOfficial Then Return ReleaseId > 0
                Return Not String.IsNullOrEmpty(AssetUrl)
            End Get
        End Property
    End Class

    '待安装的新版本（退出时由脚本替换）
    Private PendingExe As String = ""
    Private PendingRestart As Boolean = False

#End Region

#Region "共享状态"

    ''' <summary>
    ''' 更新流程的状态。自动更新与设置页共用同一份，界面据此显示进度。
    ''' </summary>
    Public Enum UpdateStatus
        ''' <summary>还没查过。</summary>
        Idle = 0
        ''' <summary>正在查询 Release。</summary>
        Checking = 1
        ''' <summary>已是最新版本。</summary>
        UpToDate = 2
        ''' <summary>发现新版本，等待用户决定。</summary>
        Available = 3
        ''' <summary>正在下载。</summary>
        Downloading = 4
        ''' <summary>已下载，退出时安装。</summary>
        Downloaded = 5
        ''' <summary>检查或下载失败。</summary>
        Failed = 6
    End Enum

    Private StateLock As New Object
    Private StatusValue As UpdateStatus = UpdateStatus.Idle
    Private LatestValue As UpdateInfo = Nothing
    Private ProgressValue As Double = 0
    Private MessageValue As String = ""
    Private StagedValue As String = ""
    Private PhaseValue As String = ""
    Private ReadOnly StateListeners As New List(Of Action)

    ''' <summary>当前状态。</summary>
    Public ReadOnly Property Status As UpdateStatus
        Get
            SyncLock StateLock
                Return StatusValue
            End SyncLock
        End Get
    End Property

    ''' <summary>最近一次检查到的版本信息，未检查过时为 Nothing。</summary>
    Public ReadOnly Property Latest As UpdateInfo
        Get
            SyncLock StateLock
                Return LatestValue
            End SyncLock
        End Get
    End Property

    ''' <summary>下载进度，0-1。</summary>
    Public ReadOnly Property Progress As Double
        Get
            SyncLock StateLock
                Return ProgressValue
            End SyncLock
        End Get
    End Property

    ''' <summary>最后一次失败的原因，没有失败时为空。</summary>
    Public ReadOnly Property Message As String
        Get
            SyncLock StateLock
                Return MessageValue
            End SyncLock
        End Get
    End Property

    ''' <summary>
    ''' 当前进行到哪一步（如「正在连接下载源……」）。
    ''' 连接建立、分段调度这些阶段没有字节进度，只靠百分比会以为卡死了。
    ''' </summary>
    Public ReadOnly Property Phase As String
        Get
            SyncLock StateLock
                Return PhaseValue
            End SyncLock
        End Get
    End Property

    ''' <summary>已下载待安装的新版本文件路径，未下载时为空。</summary>
    Public ReadOnly Property StagedPath As String
        Get
            SyncLock StateLock
                Return StagedValue
            End SyncLock
        End Get
    End Property

    ''' <summary>
    ''' 注册状态变化的回调。回调会在设置状态的线程上执行，界面需要自己切回 UI 线程。
    ''' </summary>
    Public Sub RegisterListener(Handler As Action)
        If Handler Is Nothing Then Return
        SyncLock StateLock
            If Not StateListeners.Contains(Handler) Then StateListeners.Add(Handler)
        End SyncLock
    End Sub

    Public Sub UnregisterListener(Handler As Action)
        If Handler Is Nothing Then Return
        SyncLock StateLock
            StateListeners.Remove(Handler)
        End SyncLock
    End Sub

    ''' <summary>
    ''' 更新状态并通知所有监听者。
    ''' </summary>
    ''' <param name="NewProgress">-1 表示不变。</param>
    Private Sub SetStatus(NewStatus As UpdateStatus, Optional NewMessage As String = Nothing,
                          Optional NewLatest As UpdateInfo = Nothing, Optional NewProgress As Double = -1)
        Dim Handlers As Action()
        SyncLock StateLock
            StatusValue = NewStatus
            If NewMessage IsNot Nothing Then MessageValue = NewMessage
            If NewLatest IsNot Nothing Then LatestValue = NewLatest
            If NewProgress >= 0 Then ProgressValue = NewProgress
            Handlers = StateListeners.ToArray()
        End SyncLock
        InvokeListeners(Handlers)
    End Sub

    ''' <summary>
    ''' 只更新阶段文字并通知界面。连接建立、分段调度这些阶段没有字节进度，
    ''' 不给出文字的话界面会一直停在 0%。
    ''' </summary>
    Private Sub SetPhase(Value As String)
        Dim Handlers As Action()
        SyncLock StateLock
            If PhaseValue = Value Then Return
            PhaseValue = Value
            Handlers = StateListeners.ToArray()
        End SyncLock
        InvokeListeners(Handlers)
    End Sub

    Private Sub InvokeListeners(Handlers As Action())
        For Each Handler As Action In Handlers
            Try
                Handler()
            Catch ex As Exception
                Logger.Warn(ex, "刷新更新界面失败")
            End Try
        Next
    End Sub

#End Region

#Region "检查"

    ''' <summary>
    ''' 按当前设置的更新源获取最新版本。失败时抛出异常；
    ''' 官方源的授权问题会抛 ModUpdateServer.UpdateLicenseException，供上层区分处理。
    ''' </summary>
    Public Function FetchLatest() As UpdateInfo
        If CurrentUpdateSource = UpdateSource.Official Then
            Return FetchLatestFromServer()
        Else
            Return FetchLatestFromGitHub()
        End If
    End Function

    ''' <summary>官方源：先校验授权，再取最新版本。</summary>
    Private Function FetchLatestFromServer() As UpdateInfo
        Dim Rel As ModUpdateServer.ServerRelease = ModUpdateServer.FetchRelease()
        Dim Info As UpdateInfo = BuildServerInfo(Rel)
        Logger.Info($"检查更新（官方源）：最新版本 {Info.Version}，文件 {Info.AssetName}，" &
                    $"剩余授权 {Rel.RemainingDays} 天")
        Return Info
    End Function

    ''' <summary>把官方源返回的发布信息转成统一的 UpdateInfo。</summary>
    Private Function BuildServerInfo(Rel As ModUpdateServer.ServerRelease) As UpdateInfo
        Dim Info As New UpdateInfo With {
            .Version = Rel.Version,
            .Title = If(String.IsNullOrEmpty(Rel.Title), "RMCL " & Rel.Version, Rel.Title),
            .Notes = CleanNotes(Rel.Notes),
            .PageUrl = ModUpdateServer.ConfiguredServerUrl,
            .AssetName = Rel.FileName,
            .AssetSize = Rel.Size,
            .Sha256 = Rel.Sha256,
            .ReleaseId = Rel.ReleaseId,
            .IsOfficial = True
        }
        If Rel.PublishedAt > 0 Then
            Info.PublishedAt = Rel.PublishedAt.ToString()
            Info.PublishedLocal = DateTimeOffset.FromUnixTimeSeconds(Rel.PublishedAt).ToLocalTime().DateTime
        End If
        Return Info
    End Function

    ''' <summary>
    ''' 官方源下按版本号取公告。更新完成、重启后由 ShowNotesAfterRestart 调用。
    ''' 优先走 release 接口精确匹配；服务端还是旧版时退回 latest，只有 latest 正好是这一版才用。
    ''' </summary>
    Private Function FetchByVersionFromServer(Version As String) As UpdateInfo
        Try
            Return BuildServerInfo(ModUpdateServer.FetchReleaseByVersion(Version))
        Catch ex As Exception
            Logger.Warn(ex, $"按版本获取 {Version} 的更新公告失败，改用最新版本兜底")
        End Try

        Try
            Dim Rel As ModUpdateServer.ServerRelease = ModUpdateServer.FetchRelease()
            If String.Equals(Rel.Version, Version, StringComparison.OrdinalIgnoreCase) Then
                Logger.Info($"官方源最新版本正好是刚装上的 {Version}，用它的公告")
                Return BuildServerInfo(Rel)
            End If
            Logger.Info($"官方源最新版本是 {Rel.Version}，与刚装上的 {Version} 不同，不展示公告")
        Catch ex As Exception
            Logger.Warn(ex, "兜底获取最新版本信息也失败")
        End Try
        Return Nothing
    End Function

    ''' <summary>GitHub：原生源直连，镜像源先经镜像再回退官方。</summary>
    Private Function FetchLatestFromGitHub() As UpdateInfo
        Dim UseMirror As Boolean = CurrentUpdateSource = UpdateSource.GitHubMirror
        Dim Raw As JObject = Nothing
        Dim MirrorFailure As Exception = Nothing

        If UseMirror Then
            Try
                Raw = NetRequestByClientRetry(MirrorUrl(UpdateApi), RequireJson:=True).DeserializeJson()
            Catch ex As Exception
                '很多镜像只代理 Release 资产，不代理 api.github.com，所以失败就回退官方接口
                MirrorFailure = ex
                Logger.Warn(ex, $"经镜像获取版本信息失败，回退官方接口：{MirrorUrl(UpdateApi)}")
            End Try
        End If

        If Raw Is Nothing Then
            Try
                Raw = NetRequestByClientRetry(UpdateApi, RequireJson:=True).DeserializeJson()
            Catch ex As Exception
                Throw New Exception($"无法连接更新服务器（{UpdateRepo}）", If(MirrorFailure Is Nothing, ex, New AggregateException(MirrorFailure, ex)))
            End Try
        End If

        If Raw Is Nothing Then Throw New Exception("更新服务器返回了空内容")
        Dim Info As UpdateInfo = ParseRelease(Raw)
        Logger.Info($"检查更新：最新版本 {Info.Version}，exe 资产 {(If(Info.HasAsset, Info.AssetName, "无"))}")
        Return Info
    End Function

    ''' <summary>
    ''' 按版本号获取对应的 Release，用于更新完成后展示该版本的公告。
    ''' 失败返回 Nothing：拿不到公告不应该影响启动。
    ''' </summary>
    Public Function FetchByTag(Version As String) As UpdateInfo
        If String.IsNullOrEmpty(Version) Then Return Nothing
        '官方源没有 tag 概念，按版本号查自己的服务端
        If CurrentUpdateSource = UpdateSource.Official Then Return FetchByVersionFromServer(Version)
        'tag 通常带 v 前缀，也兼容不带前缀的情况
        For Each Url As String In {
                $"https://api.github.com/repos/{UpdateRepo}/releases/tags/v{Version}",
                $"https://api.github.com/repos/{UpdateRepo}/releases/tags/{Version}"}
            Try
                Dim Raw As JObject = NetRequestByClientRetry(Url, RequireJson:=True).DeserializeJson()
                If Raw IsNot Nothing Then Return ParseRelease(Raw)
            Catch ex As Exception
                Logger.Warn(ex, $"按版本获取更新公告失败：{Url}")
            End Try
        Next
        Return Nothing
    End Function

    ''' <summary>
    ''' 把 Release 的 JSON 解析成 UpdateInfo。
    ''' </summary>
    Private Function ParseRelease(Raw As JObject) As UpdateInfo
        Dim Info As New UpdateInfo With {
            .Version = If(Raw("tag_name")?.ToString, "").Trim().TrimStart("v"c),
            .Title = If(Raw("name")?.ToString, "").Trim(),
            .Notes = CleanNotes(If(Raw("body")?.ToString, "")),
            .PageUrl = If(Raw("html_url")?.ToString, UpdatePage),
            .PublishedAt = If(Raw("published_at")?.ToString, "")
        }
        If String.IsNullOrEmpty(Info.Title) Then Info.Title = "RMCL " & Info.Version

        '发布时间：Json.NET 以 DateTimeZoneHandling.Local 解析，值已经是本地时间（Kind=Local）。
        '不能走 ToString() → CDate() → ToLocalTime()：字符串会丢掉时区，CDate 得到 Unspecified，
        'ToLocalTime 又会把它当 UTC 再加一次时差（曾经因此把 11:56 显示成 19:56）。
        '这里直接取 Token 的 DateTime 值，按 Kind 决定是否需要转换。
        Dim DateToken = TryCast(Raw("published_at"), JValue)
        If DateToken IsNot Nothing Then
            If DateToken.Type = JTokenType.Date Then
                Dim Value As DateTime = DateToken.Value(Of DateTime)()
                Select Case Value.Kind
                    Case DateTimeKind.Utc
                        Info.PublishedLocal = Value.ToLocalTime()
                    Case DateTimeKind.Local
                        Info.PublishedLocal = Value
                    Case Else
                        Info.PublishedLocal = DateTime.SpecifyKind(Value, DateTimeKind.Local)
                End Select
            ElseIf DateToken.Type = JTokenType.String Then
                Dim Offset As DateTimeOffset
                If DateTimeOffset.TryParse(DateToken.Value(Of String)(), Offset) Then Info.PublishedLocal = Offset.LocalDateTime
            End If
        End If

        '挑出 exe 资产；如果有多个，优先选名字里带 RMCL 的
        Dim Assets = TryCast(Raw("assets"), JArray)
        If Assets IsNot Nothing Then
            Dim Candidates = Assets.Where(Function(a) If(a?("name")?.ToString, "").EndsWithF(".exe", True)).ToList()
            Dim Target = Candidates.FirstOrDefault(Function(a) If(a?("name")?.ToString, "").IndexOf("RMCL", StringComparison.OrdinalIgnoreCase) >= 0)
            If Target Is Nothing Then Target = Candidates.FirstOrDefault()
            If Target IsNot Nothing Then
                Info.AssetName = If(Target("name")?.ToString, "")
                Info.AssetUrl = If(Target("browser_download_url")?.ToString, "")
                Info.AssetSize = If(Target("size") Is Nothing, 0L, Target("size").ToObject(Of Long)())
            End If
        End If
        Return Info
    End Function

    ''' <summary>
    ''' 远程版本是否比当前版本新。
    ''' </summary>
    Public Function IsNewer(RemoteVersion As String) As Boolean
        Dim Remote As Version = Nothing
        Dim Local As Version = Nothing
        If Not Version.TryParse(RemoteVersion, Remote) Then Return False
        If Not Version.TryParse(VersionBaseName, Local) Then Return True
        Return Remote > Local
    End Function

    ''' <summary>
    ''' 把 Release 公告里的 Markdown 标记去掉，变成可以直接显示的纯文本。
    ''' </summary>
    Private Function CleanNotes(RawText As String) As String
        '没有公告就返回空串，由调用方决定怎么处理 —— 以前这里返回「该版本没有提供更新公告。」，
        '结果「更新完成」弹窗被这条兜底文案骗成「有公告」，白弹一个空窗
        If String.IsNullOrEmpty(RawText) Then Return ""
        Dim Text As String = RawText.Replace(vbCrLf, vbLf)
        Text = Regex.Replace(Text, "^#{1,6}[ \t]*", "", RegexOptions.Multiline)         '标题
        Text = Regex.Replace(Text, "^\s*[-*+][ \t]+", "· ", RegexOptions.Multiline)     '无序列表
        Text = Regex.Replace(Text, "\[([^\]]+)\]\((https?://[^\)]+)\)", "$1")           '链接只留文字
        Text = Text.Replace("**", "").Replace("__", "").Replace("`", "")               '粗体与行内代码
        Text = Regex.Replace(Text, "\n{3,}", vbLf & vbLf)
        Return Text.Trim()
    End Function

#End Region

#Region "下载与安装"

    ''' <summary>
    ''' 生成下载源列表。
    ''' 官方源：先用授权换一张短时票据，地址里带票据，过期就下载不了；
    ''' GitHub 原生：官方优先，连不上 github 资产时退回镜像；
    ''' GitHub 镜像：镜像优先，失败再退回官方。
    ''' </summary>
    Private Function BuildDownloadUrls(Info As UpdateInfo) As List(Of String)
        Dim Urls As New List(Of String)

        If Info.IsOfficial Then
            '票据只有几分钟有效期，所以放在真正开始下载时再换，不能在检查更新时提前换
            Urls.Add(ModUpdateServer.CreateTicket(Info.ReleaseId))
            Return Urls
        End If

        Dim Url As String = Info.AssetUrl
        If String.IsNullOrEmpty(Url) Then Return Urls

        Dim IsGitHub As Boolean = Url.Contains("github.com/") OrElse Url.Contains("githubusercontent.com")
        If Not IsGitHub Then
            Urls.Add(Url)
            Return Urls
        End If

        If CurrentUpdateSource = UpdateSource.GitHubMirror Then
            Urls.Add(MirrorUrl(Url))
            Urls.Add(Url)
        Else
            Urls.Add(Url)
            Urls.Add(MirrorUrl(Url))
        End If
        Return Urls
    End Function

    '正在进行的下载任务，供进度窗口的「取消下载」使用
    Private ActiveTask As LoaderDownload = Nothing

    ''' <summary>
    ''' 取消正在进行的更新下载。
    ''' </summary>
    Public Sub CancelDownload()
        Try
            ActiveTask?.Cancel()
            Logger.Info("用户取消了更新下载")
        Catch ex As Exception
            Logger.Warn(ex, "取消更新下载失败")
        End Try
    End Sub

    ''' <summary>
    ''' 下载更新到缓存目录，返回本地文件路径。失败时抛出异常。
    ''' </summary>
    ''' <param name="OnProgress">下载进度回调，参数为 0-1。</param>
    Public Function DownloadUpdate(Info As UpdateInfo, Optional OnProgress As Action(Of Double) = Nothing) As String
        If Not Info.HasAsset Then Throw New Exception("该版本没有提供 exe 下载资源")
        Dim Folder As String = PathTemp & "Update\"
        DirectoryUtils.Create(Folder)
        Dim Target As String = Folder & "RMCL_new.exe"
        FileUtils.Delete(Target)

        Dim Urls As List(Of String) = BuildDownloadUrls(Info)
        If Urls.Count = 0 Then Throw New Exception("该版本没有提供可用的下载地址")

        Dim File As New NetFile(Urls, Target, SimulateBrowserHeaders:=True)
        Dim Task As New LoaderDownload("下载启动器更新", New List(Of NetFile) From {File})
        ActiveTask = Task
        '压低并发后再开始，见 UpdateThreadLimit 的说明
        NetTaskThreadLimitOverride = UpdateThreadLimit
        SetPhase("正在连接下载源……")
        Try
            'LoaderDownload 的 Progress 是只读重写属性（setter 直接抛异常），基类的 ProgressChanged
            '永远不会触发，所以这里主动轮询它的 Progress；_Progress 由 NetManager 每 0.1 秒刷新一次
            Task.Start()
            Dim Downloading As Boolean = False
            Do While Task.State = LoadState.Loading
                If OnProgress IsNot Nothing Then
                    Dim Value As Double = Task.Progress
                    If Double.IsNaN(Value) OrElse Value < 0 Then Value = 0
                    '从连接/调度阶段进入真正的字节传输，此时才把阶段文字换掉
                    If Not Downloading AndAlso Value > 0.001 Then
                        Downloading = True
                        SetPhase("正在下载……")
                    End If
                    OnProgress(Math.Min(1.0, Value))
                End If
                Thread.Sleep(100)
            Loop
            If Task.State = LoadState.Canceled Then Throw New OperationCanceledException("更新下载已取消")
            If Task.Error IsNot Nothing Then Throw New Exception(Task.Error.Message, Task.Error)
            If OnProgress IsNot Nothing Then OnProgress(1)
        Finally
            Task.Cancel()
            ActiveTask = Nothing
            NetTaskThreadLimitOverride = 0
        End Try

        SetPhase("正在校验文件……")
        If Not FileUtils.Exists(Target) Then Throw New Exception("更新文件下载失败，请稍后重试")
        Dim Length As Long = FileUtils.GetInfo(Target).Length
        If Length < 512 * 1024 Then
            FileUtils.Delete(Target)
            Throw New Exception($"下载的更新文件不完整（{FormatSize(Length)}），请稍后重试")
        End If
        If Info.AssetSize > 0 AndAlso Length <> Info.AssetSize Then
            FileUtils.Delete(Target)
            Throw New Exception($"下载的更新文件大小与发布信息不一致（{FormatSize(Length)} / {FormatSize(Info.AssetSize)}），请稍后重试")
        End If

        '官方源会给出 sha256：大小对得上不代表内容对得上，中间被替换过就必须丢掉
        If Not String.IsNullOrEmpty(Info.Sha256) Then
            SetPhase("正在校验文件完整性……")
            Dim Actual As String = ModUpdateServer.Sha256File(Target)
            If Not String.Equals(Actual, Info.Sha256, StringComparison.OrdinalIgnoreCase) Then
                FileUtils.Delete(Target)
                Throw New Exception($"更新文件的 SHA-256 校验未通过，已丢弃该文件（实际 {Actual.Substring(0, Math.Min(12, Actual.Length))}…，" &
                                    $"期望 {Info.Sha256.Substring(0, Math.Min(12, Info.Sha256.Length))}…）")
            End If
            Logger.Info("更新文件 SHA-256 校验通过")
        End If
        Logger.Info($"更新文件下载完成：{Target}（{FormatSize(Length)}）")
        SetPhase("")
        Return Target
    End Function

    ''' <summary>
    ''' 登记一个已下载的新版本，等程序退出时替换。
    ''' </summary>
    ''' <param name="Restart">替换完成后是否自动重新启动启动器。</param>
    ''' <param name="Version">即将装上的版本号，用于重启后展示该版本的更新公告。</param>
    Public Sub StageUpdate(NewExePath As String, Restart As Boolean, Optional Version As String = "")
        PendingExe = NewExePath
        PendingRestart = Restart
        '记住这一版：重启后若当前版本号等于它，说明刚由更新器装上，就弹窗展示这次的更新内容。
        '必须连 exe 路径一起记 —— 这个设置存在 HKCU\Software\RMCLDebug，是全部副本共用的，
        '只记版本号的话，别的副本启动时会把它当成「自己的记录」处理掉。
        If Not String.IsNullOrEmpty(Version) Then Settings.Set("ToolLauncherUpdateInstalled", Version & "|" & PathExe)
        Logger.Info($"已登记待安装的更新：{NewExePath}（重启={Restart}，版本={If(Version, "未知")}）")
    End Sub

    ''' <summary>
    ''' 是否已经登记了待安装的更新。
    ''' </summary>
    Public ReadOnly Property HasPendingUpdate As Boolean
        Get
            Return Not String.IsNullOrEmpty(PendingExe) AndAlso FileUtils.Exists(PendingExe)
        End Get
    End Property

    ''' <summary>
    ''' 在程序退出前调用：写一个替换脚本，等当前进程释放 exe 后再覆盖它。
    ''' 脚本会反复重试，因此这里启动后不需要等待，进程可以直接退出。
    ''' </summary>
    ''' <returns>是否成功启动了替换脚本。</returns>
    Public Function ApplyPendingUpdate() As Boolean
        If Not HasPendingUpdate Then Return False
        Try
            Dim ExeDir As String = Path.GetDirectoryName(PathExe) & "\"
            Dim ExeName As String = Path.GetFileName(PathExe)

            '优先把新版本复制到 exe 同目录：这样 move 是同卷重命名，而且脚本里不出现任何目录路径。
            '之前把绝对路径（常含中文目录）写进 .bat，而 cmd 按系统 ANSI 代码页读取脚本，
            '用 UTF-8 写出的中文路径会变成乱码，move 每次都找不到文件 —— 表现为「下载成功但版本没变」。
            Try
                Dim StagedNearby As String = ExeDir & "RMCL.update.exe"
                Dim ScriptNearby As String = ExeDir & "RMCL.update.bat"
                System.IO.File.Copy(PendingExe, StagedNearby, True)
                Dim NearbyLines As New List(Of String) From {
                    "@echo off",
                    "setlocal",
                    "for /l %%i in (1,1,60) do (",
                    "  ping -n 2 127.0.0.1 >nul",
                    $"  move /y ""%~dp0RMCL.update.exe"" ""%~dp0{ExeName}"" >nul 2>&1 && goto done",
                    ")",
                    "goto end",
                    ":done",
                    If(PendingRestart, $"start """" ""%~dp0{ExeName}""", "rem no restart"),
                    ":end",
                    "del ""%~f0"" >nul 2>&1"
                }
                FileUtils.Write(ScriptNearby, NearbyLines.Join(vbCrLf), Text.Encoding.Default)
                Logger.Info($"已写入更新脚本：{ScriptNearby}（重启={PendingRestart}，同目录模式）")
                StartProcess(New ProcessStartInfo("cmd.exe", "/c """ & ScriptNearby & """") With {
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .WindowStyle = ProcessWindowStyle.Hidden
                })
                PendingExe = ""
                Return True
            Catch ex As Exception
                '安装目录不可写（例如放在 Program Files）时退回缓存目录方案
                Logger.Warn(ex, "把新版本放到 exe 同目录失败，改用缓存目录方案")
            End Try

            Dim Folder As String = PathTemp & "Update\"
            DirectoryUtils.Create(Folder)
            Dim Script As String = Folder & "apply.bat"
            Dim Lines As New List(Of String) From {
                "@echo off",
                "setlocal",
                $"set ""NEW={PendingExe}""",
                $"set ""EXE={PathExe}""",
                "for /l %%i in (1,1,60) do (",
                "  ping -n 2 127.0.0.1 >nul",
                "  move /y ""%NEW%"" ""%EXE%"" >nul 2>&1 && goto done",
                ")",
                "goto end",
                ":done",
                If(PendingRestart, "start """" ""%EXE%""", "rem no restart"),
                ":end",
                "del ""%~f0"" >nul 2>&1"
            }
            '脚本里有中文路径，必须按系统 ANSI 写出，否则 cmd 读到的是乱码
            FileUtils.Write(Script, Lines.Join(vbCrLf), Text.Encoding.Default)
            Logger.Info($"已写入更新脚本：{Script}（重启={PendingRestart}，缓存目录模式）")
            StartProcess(New ProcessStartInfo("cmd.exe", "/c """ & Script & """") With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .WindowStyle = ProcessWindowStyle.Hidden
            })
            PendingExe = ""
            Return True
        Catch ex As Exception
            Logger.Error(ex, "启动更新脚本失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 检查更新。结果写入共享状态；本方法会阻塞在网络请求上，调用方应放到后台线程。
    ''' </summary>
    ''' <param name="Manual">是否由用户主动点击「检查更新」触发。手动检查会用 Hint 明确告知结果，启动时的静默检查不打扰用户。</param>
    Public Sub Check(Optional Manual As Boolean = False)
        If Status = UpdateStatus.Checking OrElse Status = UpdateStatus.Downloading Then Return
        SetStatus(UpdateStatus.Checking, "")

        Dim Attempt As Integer = 0
        Do
            Attempt += 1
            Try
                '官方源：本地没有有效令牌时先要授权。启动时的静默检查不打扰用户，直接跳过
                If CurrentUpdateSource = UpdateSource.Official AndAlso Not ModUpdateServer.HasValidLicense Then
                    If Not Manual Then
                        SetStatus(UpdateStatus.Failed, "官方源尚未授权，已跳过自动检查", Nothing, 0)
                        Logger.Info("自动检查更新：官方源尚未授权，本次跳过")
                        Return
                    End If
                    If Not AskLicenseCode() Then
                        SetStatus(UpdateStatus.Failed, "尚未授权，无法使用官方源检查更新", Nothing, 0)
                        Return
                    End If
                End If

                Dim Info As UpdateInfo = FetchLatest()
                If Info Is Nothing OrElse String.IsNullOrEmpty(Info.Version) Then
                    SetStatus(UpdateStatus.Failed, "更新服务器没有返回版本信息", Nothing, 0)
                    If Manual Then Hint("检查更新失败：更新服务器没有返回版本信息", HintType.Red)
                    Return
                End If
                If IsNewer(Info.Version) Then
                    SetStatus(UpdateStatus.Available, "", Info, 0)
                    If Manual Then Hint($"发现新版本 {Info.Version}（当前 {VersionBaseName}）", HintType.Blue)
                Else
                    SetStatus(UpdateStatus.UpToDate, "", Info, 0)
                    If Manual Then Hint($"已是最新版本（{VersionBaseName}）", HintType.Green)
                End If
                Return

            Catch lex As ModUpdateServer.UpdateLicenseException
                Logger.Warn(lex, $"官方源授权校验失败（{lex.ErrorCode}）")

                '两类情况都值得让用户重输一次验证码：
                '  1) 令牌失效（换机、管理员解绑）—— 原来那张码还能用
                '  2) 授权到期 / 被停用 / 绑在别的设备 —— 用户可能已经从管理员那里拿到了新的码
                Dim CanRetry As Boolean = lex.NeedActivate OrElse IsRenewalRequired(lex.ErrorCode)
                If Manual AndAlso Attempt = 1 AndAlso CanRetry Then
                    If AskLicenseCode(lex.Message, Renewal:=Not lex.NeedActivate) Then
                        Continue Do
                    End If
                    '用户放弃续期：别让他卡在「官方源用不了、又不知道怎么办」的状态，
                    '直接退回 GitHub 官方源，然后用新源重新检查一次
                    FallbackToGitHubSource(lex.Message)
                    Continue Do
                End If

                '已到期或被停用：本地令牌已经没有意义，清掉，界面就会显示未授权
                If lex.ErrorCode = ModUpdateServer.ErrExpired OrElse lex.ErrorCode = ModUpdateServer.ErrCodeDisabled Then
                    ModUpdateServer.ClearLicense()
                End If
                SetStatus(UpdateStatus.Failed, lex.Message, Nothing, 0)
                If Manual Then Hint($"检查更新失败：{lex.Message}", HintType.Red)
                Return

            Catch ex As Exception
                Logger.Warn(ex, "检查启动器更新失败")
                SetStatus(UpdateStatus.Failed, ex.Message, Nothing, 0)
                If Manual Then Hint($"检查更新失败：{ex.Message}", HintType.Red)
                Return
            End Try
        Loop While Attempt < 2
    End Sub

    ''' <summary>
    ''' 弹输入框让用户填验证码并激活，返回是否成功。
    ''' 内部会切到 UI 线程弹窗，所以必须从后台线程调用（Check 本身就在后台线程跑）。
    ''' </summary>
    Private Function AskLicenseCode(Optional Reason As String = "", Optional Renewal As Boolean = False) As Boolean
        Dim Input As String = RunInUiWait(Function()
            Dim Text As String
            If Renewal Then
                Text = "当前授权已不可用（可能是到期、被停用，或已经绑在别的设备上）。" & vbCrLf &
                       "如果已经续期或拿到了新的验证码，可以直接在这里输入。" & vbCrLf &
                       "暂时不续期的话点「取消」，会自动改用 GitHub 官方源继续检查更新。"
            Else
                Text = "官方源需要授权后才能检查与下载更新。" & vbCrLf &
                       "请输入管理员提供的验证码；一张码默认绑定一台设备，" & vbCrLf &
                       "授权到期后自动失效，需要续期才能继续更新。"
            End If
            If Not String.IsNullOrEmpty(Reason) Then Text = Reason & vbCrLf & vbCrLf & Text
            Return MyMsgBoxInput(If(Renewal, "更新授权已失效", "更新授权"), Text, "", Nothing,
                                 "XXXX-XXXX-XXXX-XXXX", "激活", "取消")
        End Function)
        If String.IsNullOrEmpty(Input) Then
            Logger.Info("用户取消了更新授权输入")
            Return False
        End If

        Dim State As ModUpdateServer.LicenseInfo = Nothing
        Dim Failure As String = ""
        Try
            SetPhase("正在向官方源激活……")
            Logger.Info("正在向官方源激活更新授权……")
            State = ModUpdateServer.Activate(Input)
        Catch ex As ModUpdateServer.UpdateLicenseException
            Failure = ex.Message
        Catch ex As Exception
            Failure = ex.Message
        Finally
            SetPhase("")
        End Try

        If State Is Nothing Then
            RunInUiWait(Sub() MyMsgBox("激活失败：" & Failure, "更新授权", "知道了", IsWarn:=True))
            Return False
        End If
        Hint($"授权成功，剩余 {State.RemainingDays} 天（{State.ExpireText} 到期）", HintType.Green)
        Return True
    End Function

    ''' <summary>
    ''' 这些错误码意味着「当前授权不能用了，但换一张码有机会继续」：
    ''' 到期、被停用、绑在别的设备、码被删。换机类（令牌失效）另由 NeedActivate 判断。
    ''' </summary>
    Private Function IsRenewalRequired(ErrorCode As String) As Boolean
        Select Case ErrorCode
            Case ModUpdateServer.ErrExpired, ModUpdateServer.ErrCodeDisabled, _
                 ModUpdateServer.ErrDeviceBound, ModUpdateServer.ErrCodeNotFound
                Return True
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>
    ''' 用户放弃续期时，把更新源切回 GitHub 原生源并提示。
    ''' 这里只改设置、不继续检查 —— 由调用方用新源再跑一轮，避免两条链路互相等。
    ''' </summary>
    Private Sub FallbackToGitHubSource(Reason As String)
        Try
            ModUpdateServer.ClearLicense()
            Settings.Set("UpdateSourceMode", CInt(UpdateSource.GitHubNative))
            Logger.Info($"官方源授权不可用（{Reason}），已自动切换为 GitHub 官方源")
            Hint("已自动切换为 GitHub 官方源，后续更新将从 GitHub 检查", HintType.Blue)
        Catch ex As Exception
            Logger.Warn(ex, "自动切换更新源失败")
        End Try
    End Sub

    ''' <summary>
    ''' 下载最新版本并登记为待安装。状态会同步到界面；本方法会阻塞，调用方应放到后台线程。
    ''' </summary>
    Public Sub StartDownload()
        If Status = UpdateStatus.Downloading Then Return
        Dim Info As UpdateInfo = Latest
        If Info Is Nothing OrElse Not Info.HasAsset Then Throw New Exception("当前没有可下载的更新")
        LastProgressNotified = -1
        ProgressCallbackCount = 0
        SetStatus(UpdateStatus.Downloading, "", Info, 0)
        Try
            Dim LocalPath As String = DownloadUpdate(Info, AddressOf OnDownloadProgress)
            StageUpdate(LocalPath, False, Info.Version)
            SyncLock StateLock
                StagedValue = LocalPath
            End SyncLock
            Logger.Info($"更新下载完成，进度回调共 {ProgressCallbackCount} 次，最终 {LastProgressNotified * 100:F0}%")
            SetStatus(UpdateStatus.Downloaded, "", Info, 1)
        Catch ex As Exception
            Logger.Warn(ex, "下载启动器更新失败")
            SetStatus(UpdateStatus.Failed, ex.Message, Info, 0)
            Throw
        End Try
    End Sub

    '上一次推送进度的百分比，用于过滤过密的刷新
    Private LastProgressNotified As Double = -1
    '进度回调次数，仅用于日志自检：下载完成时能确认回调链路是否真的在工作
    Private ProgressCallbackCount As Integer = 0
    ''' <summary>
    ''' 下载引擎的进度回调：按 1% 步长推送到共享状态，避免每收到一段数据就刷一次界面。
    ''' </summary>
    Private Sub OnDownloadProgress(Value As Double)
        ProgressCallbackCount += 1
        If ProgressCallbackCount = 1 Then Logger.Info($"更新下载进度回调已触发（首个进度 {Value * 100:F1}%）")
        If Value < 0.999 AndAlso Value - LastProgressNotified < 0.01 Then Return
        LastProgressNotified = Value
        SetStatus(Status, Nothing, Nothing, Value)
    End Sub

    ''' <summary>
    ''' 启动时自动检查：发现新版本时弹窗展示公告并询问，确认后才下载。
    ''' 不记忆「已经提示过」，只要还有更新就每次都提示 —— 否则一旦某次安装没落地，
    ''' 用户会因为「下载过」被永久静音，看起来就是自动更新失效。
    ''' </summary>
    Public Sub AutoCheck()
        RunInNewThread(
        Sub()
            '官方源尚未授权时既不该打扰用户，也不该把状态写成「失败」，直接跳过
            If CurrentUpdateSource = UpdateSource.Official AndAlso Not ModUpdateServer.HasValidLicense Then
                Logger.Info("自动更新：官方源尚未授权，本次跳过")
                Return
            End If
            Check()
            Dim Info As UpdateInfo = Latest
            If Info Is Nothing Then
                Logger.Info("自动更新：没有取到版本信息，本次不提示")
                Return
            End If
            If Status <> UpdateStatus.Available Then
                Logger.Info($"自动更新：当前 {VersionBaseName}，最新 {Info.Version}，无需提示")
                Return
            End If
            RunInUi(Sub() AskUpdate(Info))
        End Sub, "自动检查更新")
    End Sub

    ''' <summary>
    ''' 启动时调用：如果这一版是刚由更新器装上的，弹窗展示该版本的更新公告。
    ''' 记录里带 exe 路径，只认当前这份程序的记录 —— 设置存在 HKCU\Software\RMCLDebug，
    ''' 是全部副本共用的，不带路径就会被别的副本当成自己的记录处理掉。
    ''' </summary>
    Public Sub ShowNotesAfterRestart()
        Dim Raw As String = ""
        Try
            Raw = Settings.Get(Of String)("ToolLauncherUpdateInstalled")
        Catch ex As Exception
            Logger.Warn(ex, "读取刚更新的版本记录失败")
        End Try
        If String.IsNullOrEmpty(Raw) Then Return

        '格式：版本|exe完整路径。旧版本只写了版本号，没有 |，按通用记录兼容处理
        Dim Installed As String = Raw
        Dim Owner As String = ""
        Dim SplitAt As Integer = Raw.IndexOf("|"c)
        If SplitAt >= 0 Then
            Installed = Raw.Substring(0, SplitAt)
            Owner = Raw.Substring(SplitAt + 1)
        End If

        '别的副本留下的记录：它还没来得及重启，轮不到我处理。
        '只有那份 exe 已经不在了，才当作失效清掉
        If Not String.IsNullOrEmpty(Owner) AndAlso Not PathUtils.IsSame(Owner, PathExe) Then
            If FileUtils.Exists(Owner) Then
                Logger.Info($"更新完成记录属于另一份程序（{Owner}），本次不处理")
                Return
            End If
            Settings.Set("ToolLauncherUpdateInstalled", "")
            Logger.Info($"更新完成记录指向的程序已不存在（{Owner}），已清除")
            Return
        End If

        '版本对不上，说明替换没落地（或用户自己换了 exe），这条记录已经过期
        If Not String.Equals(Installed, VersionBaseName, StringComparison.OrdinalIgnoreCase) Then
            Settings.Set("ToolLauncherUpdateInstalled", "")
            Logger.Info($"更新完成记录（{Installed}）与当前版本（{VersionBaseName}）不符，已清除")
            Return
        End If
        Settings.Set("ToolLauncherUpdateInstalled", "")
        Logger.Info($"检测到刚更新到 {Installed}，准备展示该版本的更新公告")

        RunInNewThread(
        Sub()
            Dim Info As UpdateInfo = Nothing
            Try
                Info = FetchByTag(Installed)
            Catch ex As Exception
                Logger.Warn(ex, $"获取 {Installed} 的更新公告失败")
            End Try
            RunInUi(Sub() ShowUpdateNotes(Installed, Info))
        End Sub, "展示更新公告")
    End Sub

    ''' <summary>
    ''' 展示「已更新到某版本」的公告弹窗（UI 线程）。公告取不到时只报版本号，不打扰太久。
    ''' </summary>
    Private Sub ShowUpdateNotes(Version As String, Info As UpdateInfo)
        Try
            '没有公告就不弹窗了，只给一个气泡 —— 空弹窗除了打断用户没有别的作用
            If Info Is Nothing OrElse String.IsNullOrEmpty(Info.Notes) Then
                Logger.Info($"已更新到 {Version}，但该版本没有更新公告，跳过弹窗")
                Hint($"已更新到 RMCL {Version}", HintType.Green)
                Return
            End If

            '弹窗高度随文本自适应，公告太长会把按钮挤出屏幕，所以这里截断；
            '完整内容留给「查看公告」按钮跳发布页。截断点回退到最近的换行，避免把句子切成两半
            Dim Notes As String = Info.Notes
            Dim Truncated As Boolean = Notes.Length > 800
            If Truncated Then
                Notes = Notes.Substring(0, 800)
                Dim LastBreak As Integer = Notes.LastIndexOf(vbLf)
                If LastBreak > 560 Then Notes = Notes.Substring(0, LastBreak)
                Notes = Notes.TrimEnd() & "……"
            End If
            Dim Text As String = $"已更新到 RMCL {Info.Version}。" & vbCrLf & vbCrLf &
                                 "本次更新内容：" & vbCrLf & Notes
            If Truncated Then Text &= vbCrLf & vbCrLf & "（公告较长，点「查看公告」看完整内容）"
            MyMsgBox(Text, "更新完成", "知道了", "查看公告",
                     Button2Action:=Sub() OpenWebsite(Info.PageUrl))
        Catch ex As Exception
            Logger.Error(ex, "展示更新公告失败")
        End Try
    End Sub

    ''' <summary>
    ''' 弹窗展示更新公告，并让用户决定是否更新（UI 线程）。
    ''' </summary>
    Private Sub AskUpdate(Info As UpdateInfo)
        Try
            Dim Notes As String = Info.Notes
            If Notes.Length > 600 Then
                Notes = Notes.Substring(0, 600) & "……" & vbCrLf & "（公告较长，完整内容请点「查看公告」）"
            End If
            Dim Text As String = $"发现新版本 {Info.Version}（当前 {VersionBaseName}）"
            If Not String.IsNullOrEmpty(Notes) Then Text &= vbCrLf & vbCrLf & Notes
            Text &= vbCrLf & vbCrLf & "更新会在下载完成后、退出启动器时自动安装。"
            Dim Result As Integer = MyMsgBox(Text, "发现新版本", "立即更新", "稍后", "查看公告",
                                            Button3Action:=Sub() OpenWebsite(Info.PageUrl))
            If Result <> 1 Then
                Logger.Info($"用户选择稍后更新 {Info.Version}")
                Return
            End If
            DownloadAndPrompt(Info)
        Catch ex As Exception
            Logger.Error(ex, "弹窗提示更新失败")
        End Try
    End Sub

    ''' <summary>
    ''' 后台下载，完成后弹窗询问是否立即重启安装。自动更新与设置页手动更新共用这一条路径。
    ''' </summary>
    Public Sub DownloadAndPrompt(Info As UpdateInfo)
        If Info Is Nothing Then Info = Latest
        If Info Is Nothing Then Return
        If Status = UpdateStatus.Downloading Then Return
        '先把进度窗口弹出来，再从后台开始下载：自动更新是从启动弹窗触发的，
        '那时用户可能停在任意页面，塞在设置页里的进度条根本看不到。
        RunInUi(
        Sub()
            Try
                Dim Dialog As New MyUpdateProgress With {.Owner = FrmMain}
                Dialog.Show()
            Catch ex As Exception
                Logger.Warn(ex, "打开更新进度窗口失败")
            End Try
        End Sub)
        RunInNewThread(
        Sub()
            Try
                StartDownload()
                Dim LocalPath As String = StagedPath
                If String.IsNullOrEmpty(LocalPath) Then Return
                RunInUi(Sub() PromptInstallAfterDownload(Info, LocalPath))
            Catch lex As ModUpdateServer.UpdateLicenseException
                '换票据时被拒：授权到期、被停用或换了设备。本地令牌在这种情况已经没用了
                Logger.Warn(lex, $"官方源下载被拒绝（{lex.ErrorCode}）")
                If lex.ErrorCode = ModUpdateServer.ErrExpired OrElse lex.ErrorCode = ModUpdateServer.ErrCodeDisabled Then
                    ModUpdateServer.ClearLicense()
                End If
                Dim Message As String = lex.Message
                Dim NeedActivate As Boolean = lex.NeedActivate
                SetStatus(UpdateStatus.Failed, Message, Info, 0)
                RunInUi(Sub()
                            Hint("更新下载失败：" & Message, HintType.Red)
                            MyMsgBox("无法继续下载：" & Message & vbCrLf & vbCrLf &
                                     If(NeedActivate,
                                        "请到「设置 → 功能 → 软件更新」点「输入验证码」重新激活后再试。",
                                        "授权到期或被停用后，更新会被服务器直接拒绝。请联系管理员续期。"),
                                     "更新授权", "知道了", IsWarn:=True)
                        End Sub)
            Catch ex As Exception
                RunInUi(Sub() Hint("更新下载失败：" & ex.Message, HintType.Red))
            End Try
        End Sub, "下载更新")
    End Sub

    ''' <summary>
    ''' 下载完成后询问是否立即重启安装（UI 线程）。自动更新与设置页手动更新共用同一套收尾。
    ''' </summary>
    ''' <param name="OnDeferred">用户选择「稍后」时的回调，供调用方刷新自己的界面文本。</param>
    Public Sub PromptInstallAfterDownload(Info As UpdateInfo, LocalPath As String, Optional OnDeferred As Action = Nothing)
        If MyMsgBox($"RMCL {Info.Version} 已下载完成，退出启动器后会自动安装。" & vbCrLf & "现在重启安装吗？",
                    "安装更新", "立即重启", "稍后") = 1 Then
            StageUpdate(LocalPath, True, If(Info?.Version, ""))
            FrmMain.EndProgram(False)
        Else
            StageUpdate(LocalPath, False, If(Info?.Version, ""))
            Hint($"更新 {Info.Version} 将在退出启动器时自动安装", HintType.Green)
            If OnDeferred IsNot Nothing Then OnDeferred()
        End If
    End Sub

#End Region

End Module
