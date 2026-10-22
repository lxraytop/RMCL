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
        ''' <summary>是否带有可下载的 exe。</summary>
        Public ReadOnly Property HasAsset As Boolean
            Get
                Return Not String.IsNullOrEmpty(AssetUrl)
            End Get
        End Property
    End Class

    '待安装的新版本（退出时由脚本替换）
    Private PendingExe As String = ""
    Private PendingRestart As Boolean = False

#End Region

#Region "检查"

    ''' <summary>
    ''' 获取最新 Release。失败时抛出异常。
    ''' </summary>
    Public Function FetchLatest() As UpdateInfo
        Dim Raw As JObject
        Try
            Raw = NetRequestByClientRetry(UpdateApi, RequireJson:=True).DeserializeJson()
        Catch ex As Exception
            Throw New Exception($"无法连接更新服务器（{UpdateRepo}）", ex)
        End Try
        If Raw Is Nothing Then Throw New Exception("更新服务器返回了空内容")

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
        Logger.Info($"检查更新：最新版本 {Info.Version}，exe 资产 {(If(Info.HasAsset, Info.AssetName, "无"))}")
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
        If String.IsNullOrEmpty(RawText) Then Return "该版本没有提供更新公告。"
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
    ''' 下载更新到缓存目录，返回本地文件路径。失败时抛出异常。
    ''' </summary>
    ''' <param name="OnProgress">下载进度回调，参数为 0-1。</param>
    Public Function DownloadUpdate(Info As UpdateInfo, Optional OnProgress As Action(Of Double) = Nothing) As String
        If Not Info.HasAsset Then Throw New Exception("该版本没有提供 exe 下载资源")
        Dim Folder As String = PathTemp & "Update\"
        DirectoryUtils.Create(Folder)
        Dim Target As String = Folder & "RMCL_new.exe"
        FileUtils.Delete(Target)

        Dim File As New NetFile({Info.AssetUrl}, Target, SimulateBrowserHeaders:=True)
        Dim Task As New LoaderDownload("下载启动器更新", New List(Of NetFile) From {File})
        If OnProgress IsNot Nothing Then
            AddHandler Task.ProgressChanged, Sub(NewProgress As Double, OldProgress As Double) OnProgress(NewProgress)
        End If
        Try
            Task.WaitForExit()
        Finally
            Task.Cancel()
        End Try

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
        Logger.Info($"更新文件下载完成：{Target}（{FormatSize(Length)}）")
        Return Target
    End Function

    ''' <summary>
    ''' 登记一个已下载的新版本，等程序退出时替换。
    ''' </summary>
    ''' <param name="Restart">替换完成后是否自动重新启动启动器。</param>
    Public Sub StageUpdate(NewExePath As String, Restart As Boolean)
        PendingExe = NewExePath
        PendingRestart = Restart
        Logger.Info($"已登记待安装的更新：{NewExePath}（重启={Restart}）")
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
    ''' 在程序退出前调用：写一个临时脚本，等当前进程释放 exe 后再替换它。
    ''' 脚本会反复重试，因此这里启动后不需要等待，进程可以直接退出。
    ''' </summary>
    ''' <returns>是否成功启动了替换脚本。</returns>
    Public Function ApplyPendingUpdate() As Boolean
        If Not HasPendingUpdate Then Return False
        Try
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
            FileUtils.Write(Script, Lines.Join(vbCrLf))
            Logger.Info($"已写入更新脚本：{Script}（重启={PendingRestart}）")
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
    ''' 启动时自动检查：发现新版本时弹窗展示更新公告并询问是否更新，确认后再下载。
    ''' </summary>
    Public Sub AutoCheck()
        Dim Info As UpdateInfo = FetchLatest()
        If Info Is Nothing OrElse String.IsNullOrEmpty(Info.Version) Then Return
        If Not IsNewer(Info.Version) Then
            Logger.Info($"启动器已是最新版本（{VersionBaseName}）")
            Return
        End If
        If Not Info.HasAsset Then
            Logger.Warn($"新版本 {Info.Version} 没有提供 exe 资源，已跳过自动更新")
            Return
        End If
        If Settings.Get(Of String)("ToolLauncherUpdateSkip") = Info.Version Then
            Logger.Info($"新版本 {Info.Version} 已经下载过，本次不再提示")
            Return
        End If
        '弹窗必须在 UI 线程
        RunInUi(Sub() AskUpdate(Info))
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
            Dim Text As String = $"发现新版本 {Info.Version}（当前 {VersionBaseName}）" & vbCrLf & vbCrLf &
                                 Notes & vbCrLf & vbCrLf &
                                 "更新会在下载完成后、退出启动器时自动安装。"
            Dim Result As Integer = MyMsgBox(Text, "发现新版本", "立即更新", "稍后", "查看公告",
                                            Button3Action:=Sub() OpenWebsite(Info.PageUrl))
            If Result <> 1 Then
                Logger.Info($"用户选择稍后更新 {Info.Version}")
                Return
            End If
            DownloadAndStage(Info)
        Catch ex As Exception
            Logger.Error(ex, "弹窗提示更新失败")
        End Try
    End Sub

    ''' <summary>
    ''' 下载完成后询问是否立即重启安装（UI 线程）。自动更新与设置页手动更新共用同一套收尾。
    ''' </summary>
    ''' <param name="OnDeferred">用户选择「稍后」时的回调，供调用方刷新自己的界面文本。</param>
    Public Sub PromptInstallAfterDownload(Info As UpdateInfo, LocalPath As String, Optional OnDeferred As Action = Nothing)
        If MyMsgBox($"RMCL {Info.Version} 已下载完成，退出启动器后会自动安装。" & vbCrLf & "现在重启安装吗？",
                    "安装更新", "立即重启", "稍后") = 1 Then
            StageUpdate(LocalPath, True)
            FrmMain.EndProgram(False)
        Else
            StageUpdate(LocalPath, False)
            Hint($"更新 {Info.Version} 将在退出启动器时自动安装", HintType.Green)
            If OnDeferred IsNot Nothing Then OnDeferred()
        End If
    End Sub

    ''' <summary>
    ''' 在后台下载更新，完成后询问是否立即重启安装。
    ''' </summary>
    Private Sub DownloadAndStage(Info As UpdateInfo)
        RunInNewThread(
        Sub()
            Try
                Dim LocalPath As String = DownloadUpdate(Info)
                Settings.Set("ToolLauncherUpdateSkip", Info.Version)
                RunInUi(Sub() PromptInstallAfterDownload(Info, LocalPath))
            Catch ex As Exception
                Logger.Warn(ex, "自动更新下载失败")
                RunInUi(Sub() Hint("自动更新下载失败：" & ex.Message, HintType.Red))
            End Try
        End Sub, "自动更新")
    End Sub

#End Region

End Module
