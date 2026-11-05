Public Class PageSetupFeature

    Private Sub PageSetupFeature_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '重复加载部分
        PanBack.ScrollToHome()
        RefreshMemoryInfo()
        RefreshShortcutInfo()
        InitCustomDownloadFolder()

        '非重复加载部分
        Static IsLoaded As Boolean = False
        If IsLoaded Then Return
        IsLoaded = True

        AniControlEnabled += 1
        Reload()
        AniControlEnabled -= 1

        RefreshJunkInfo()
    End Sub
    Public Sub Reload()
        SettingService.RefreshSettings(Me)
    End Sub
    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Logger.Info("已初始化功能页设置")
            Hint("已初始化功能页设置！", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化功能页设置失败", LogBehavior.Alert)
        End Try
        Reload()
    End Sub

#Region "内存优化"

    Private Sub RefreshMemoryInfo()
        Try
            Dim Available As Long = CLng(Math.Round(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024))
            Dim Total As Long = CLng(Math.Round(My.Computer.Info.TotalPhysicalMemory / 1024 / 1024))
            LabMemoryInfo.Text = $"启动器当前占用：{MemoryUsedMb()} MB" & vbCrLf &
                                 $"系统可用内存：{Available} MB / 共 {Total} MB" & vbCrLf &
                                 $"管理员权限：{If(WindowsUtils.HasAdminRole(), "已获取", "未获取（优化时会弹出 UAC 提示）")}"
        Catch ex As Exception
            Logger.Warn(ex, "读取内存信息失败")
            LabMemoryInfo.Text = "无法读取内存信息。"
        End Try
    End Sub

    Private Sub BtnMemoryRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnMemoryRefresh.Click
        RefreshMemoryInfo()
        Hint("内存信息已刷新", HintType.Green, False)
    End Sub

    Private Sub BtnMemoryOptimize_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnMemoryOptimize.Click
        If Not BtnMemoryOptimize.IsEnabled Then Return
        BtnMemoryOptimize.IsEnabled = False
        BtnMemoryOptimize.Text = "正在优化……"
        LabMemoryStatus.Text = "正在申请管理员权限并清理进程工作集，请稍候……"
        RunInNewThread(
        Sub()
            Dim Freed As Long = -1
            Dim LastReport As Integer = 0
            Try
                Freed = MemoryOptimize(True,
                Sub(Current As Integer, Total As Integer)
                    '总数为 0 表示当前在等待提权子进程完成
                    If Total <= 0 Then
                        RunInUi(Sub() LabMemoryStatus.Text = "已请求管理员权限，正在等待提权进程完成优化……")
                        Return
                    End If
                    '限制刷新频率，避免上百个进程逐个跨线程刷新界面
                    If Current - LastReport < 24 AndAlso Current < Total Then Return
                    LastReport = Current
                    RunInUi(Sub() LabMemoryStatus.Text = $"正在清理进程工作集…… {Current} / {Total}")
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "内存优化失败")
            Finally
                RunInUi(
                Sub()
                    BtnMemoryOptimize.Text = "立即优化内存"
                    BtnMemoryOptimize.IsEnabled = True
                    If Freed < 0 Then
                        LabMemoryStatus.Text = $"上次优化：{Now.ToLongTimeString()}，未取得管理员权限，未执行优化"
                    ElseIf Freed > 0 Then
                        LabMemoryStatus.Text = $"上次优化：{Now.ToLongTimeString()}，共释放 {FormatSize(Freed)}"
                    Else
                        LabMemoryStatus.Text = $"上次优化：{Now.ToLongTimeString()}，未检测到可释放的内存（当前已较为充足）"
                    End If
                    RefreshMemoryInfo()
                End Sub)
            End Try
        End Sub, "内存优化")
    End Sub

#End Region

#Region "清理游戏垃圾"

    ''' <summary>
    ''' 生成垃圾占用的说明文本。
    ''' 只做计算、不接触任何控件，因此可以在后台线程安全调用。
    ''' </summary>
    Private Function GetRubbishText() As String
        Dim Summary = GetRubbishSummary()
        Return $"可清理空间合计：{FormatSize(Summary.Total)}" & vbCrLf & Summary.Details.Join(vbCrLf)
    End Function

    ''' <summary>
    ''' 在 UI 线程刷新垃圾统计文本。
    ''' </summary>
    Private Sub RefreshJunkInfo()
        Try
            LabJunkInfo.Text = GetRubbishText()
        Catch ex As Exception
            Logger.Warn(ex, "统计垃圾文件失败")
            LabJunkInfo.Text = "无法统计垃圾文件。"
        End Try
    End Sub

    ''' <summary>
    ''' 在后台线程统计垃圾占用，统计完成后再回到 UI 线程更新文本。
    ''' 统计需要遍历缓存与临时目录，直接放在 UI 线程会卡顿；但也不能在后台线程直接改控件（会导致跨线程异常）。
    ''' </summary>
    Private Sub RefreshJunkInfoAsync()
        BtnJunkRefresh.IsEnabled = False
        BtnJunkRefresh.Text = "统计中……"
        LabJunkInfo.Text = "正在统计垃圾文件……"
        RunInNewThread(
        Sub()
            Dim Text As String
            Try
                Text = GetRubbishText()
            Catch ex As Exception
                Logger.Warn(ex, "统计垃圾文件失败")
                Text = "无法统计垃圾文件。"
            End Try
            RunInUi(
            Sub()
                LabJunkInfo.Text = Text
                BtnJunkRefresh.Text = "重新统计"
                BtnJunkRefresh.IsEnabled = True
            End Sub)
        End Sub, "统计垃圾")
    End Sub

    Private Sub BtnJunkRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJunkRefresh.Click
        If Not BtnJunkRefresh.IsEnabled Then Return
        RefreshJunkInfoAsync()
    End Sub

    Private Sub BtnJunkClean_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJunkClean.Click
        If Not BtnJunkClean.IsEnabled Then Return
        If MyMsgBox("即将清理临时文件、下载缓存与启动器日志。" & vbCrLf &
                    "清理不会影响已安装的 Minecraft 版本、Mod 与存档，该操作不可撤销。", "清理游戏垃圾", "清理", "取消") <> 1 Then Return
        BtnJunkClean.IsEnabled = False
        BtnJunkClean.Text = "正在清理……"
        RunInNewThread(
        Sub()
            Try
                RubbishClear(True)
            Catch ex As Exception
                Logger.Warn(ex, "清理游戏垃圾失败")
            Finally
                RunInUi(
                Sub()
                    BtnJunkClean.Text = "清理垃圾"
                    BtnJunkClean.IsEnabled = True
                    RefreshJunkInfo()
                End Sub)
            End Try
        End Sub, "清理垃圾")
    End Sub

#End Region


#Region "创建快捷方式"

    ''' <summary>
    ''' 快捷方式显示名，同时决定桌面与开始菜单里的 .lnk 文件名。
    ''' </summary>
    Private Const ShortcutName As String = "RMCL"

    ''' <summary>
    ''' 快捷方式的落点：桌面或开始菜单。
    ''' </summary>
    Private Shared Function ShortcutPath(OnDesktop As Boolean) As String
        Dim Folder As String =
            If(OnDesktop,
               Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
               Environment.GetFolderPath(Environment.SpecialFolder.Programs))
        Return IO.Path.Combine(Folder, ShortcutName & ".lnk")
    End Function

    ''' <summary>
    ''' 刷新「快捷方式是否已存在」的说明文字。
    ''' </summary>
    Private Sub RefreshShortcutInfo()
        Try
            Dim Desktop As String = ShortcutPath(True)
            Dim StartMenu As String = ShortcutPath(False)
            LabShortcutInfo.Text = $"桌面：{If(IO.File.Exists(Desktop), "已创建", "未创建")}" &
                                   $"　·　开始菜单：{If(IO.File.Exists(StartMenu), "已创建", "未创建")}"
        Catch ex As Exception
            Logger.Warn(ex, "检查快捷方式失败")
            LabShortcutInfo.Text = "无法检查快捷方式。"
        End Try
    End Sub

    Private Sub BtnShortcutDesktop_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnShortcutDesktop.Click
        CreateShortcut(True)
    End Sub

    Private Sub BtnShortcutStartMenu_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnShortcutStartMenu.Click
        CreateShortcut(False)
    End Sub

    ''' <summary>
    ''' 创建 .lnk 快捷方式。
    '''
    ''' 借 WScript.Shell 生成标准快捷方式：只需要改目标、工作目录与图标，比手写 IShellLink 互操作短得多。
    ''' 调用走后期绑定（CallByName），这样在 Option Strict 下也不需要引入 COM 接口定义。
    ''' </summary>
    Private Sub CreateShortcut(OnDesktop As Boolean)
        Dim LinkPath As String = ShortcutPath(OnDesktop)
        Dim Where As String = If(OnDesktop, "桌面", "开始菜单")
        Try
            Dim Folder As String = IO.Path.GetDirectoryName(LinkPath)
            If Not String.IsNullOrEmpty(Folder) Then IO.Directory.CreateDirectory(Folder)

            Dim Shell As Object = Microsoft.VisualBasic.Interaction.CreateObject("WScript.Shell")
            If Shell Is Nothing Then Throw New Exception("无法创建 WScript.Shell 对象")
            Dim Link As Object = CallByName(Shell, "CreateShortCut", CallType.Method, LinkPath)
            CallByName(Link, "TargetPath", CallType.Let, PathExe)
            Dim WorkFolder As String = IO.Path.GetDirectoryName(PathExe)
            If Not String.IsNullOrEmpty(WorkFolder) Then CallByName(Link, "WorkingDirectory", CallType.Let, WorkFolder)
            CallByName(Link, "IconLocation", CallType.Let, PathExe & ",0")
            CallByName(Link, "Description", CallType.Let, "Ray Minecraft Launcher")
            CallByName(Link, "Save", CallType.Method)

            Logger.Info($"已创建快捷方式：{LinkPath}")
            Hint($"已在{Where}创建快捷方式", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, $"创建快捷方式失败：{LinkPath}", LogBehavior.Alert)
            Hint($"创建{Where}快捷方式失败：" & ex.Message, HintType.Red)
        End Try
        RefreshShortcutInfo()
    End Sub

#End Region

#Region "下载自定义文件"

    ''' <summary>正在跑的自定义下载任务，供「取消下载」使用。</summary>
    Private CustomTask As LoaderDownload = Nothing
    Private CustomRunning As Boolean = False

    ''' <summary>
    ''' 首次进入时给保存目录一个合理的默认值：优先「下载」文件夹，其次桌面。
    ''' </summary>
    Private Sub InitCustomDownloadFolder()
        If Not String.IsNullOrEmpty(TextCustomFolder.Text) Then Return
        Try
            Dim Downloads As String = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            TextCustomFolder.Text = If(IO.Directory.Exists(Downloads),
                                        Downloads,
                                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
        Catch ex As Exception
            Logger.Warn(ex, "初始化自定义下载目录失败")
        End Try
    End Sub

    Private Sub BtnCustomFolder_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCustomFolder.Click
        Try
            Dim Dialog As New Ookii.Dialogs.Wpf.VistaFolderBrowserDialog With {
                .Description = "选择下载文件的保存目录",
                .UseDescriptionForTitle = True
            }
            If Dialog.ShowDialog(FrmMain) = True AndAlso Not String.IsNullOrEmpty(Dialog.SelectedPath) Then
                TextCustomFolder.Text = Dialog.SelectedPath
            End If
        Catch ex As Exception
            Logger.Warn(ex, "选择保存目录失败")
            Hint("选择文件夹失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnCustomDownload_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCustomDownload.Click
        If CustomRunning Then Return

        Dim Url As String = TextCustomUrl.Text.Trim()
        Dim Test As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, Test) OrElse
           (Test.Scheme <> Uri.UriSchemeHttp AndAlso Test.Scheme <> Uri.UriSchemeHttps) Then
            Hint("请填入 http 或 https 开头的完整文件地址", HintType.Red)
            TextCustomUrl.Focus()
            Return
        End If

        Dim Folder As String = TextCustomFolder.Text.Trim()
        If String.IsNullOrEmpty(Folder) OrElse Not IO.Directory.Exists(Folder) Then
            Hint("保存目录不存在，请重新选择", HintType.Red)
            Return
        End If

        '没填文件名就按地址最后一段猜一个，猜不出来再用 download.bin 兜底
        Dim Name As String = TextCustomName.Text.Trim()
        If String.IsNullOrEmpty(Name) Then Name = Uri.UnescapeDataString(IO.Path.GetFileName(Test.AbsolutePath))
        If String.IsNullOrEmpty(Name) Then Name = "download.bin"
        For Each Invalid As Char In IO.Path.GetInvalidFileNameChars()
            Name = Name.Replace(Invalid, "_")
        Next

        Dim Target As String = IO.Path.Combine(Folder, Name)
        If IO.File.Exists(Target) AndAlso
           MyMsgBox($"「{Name}」已经存在，是否覆盖？", "下载自定义文件", "覆盖", "取消") <> 1 Then Return

        StartCustomDownload(Url, Target)
    End Sub

    Private Sub BtnCustomCancel_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCustomCancel.Click
        If Not CustomRunning Then Return
        Try
            CustomTask?.Cancel()
            LabCustomProgress.Text = "正在取消……"
        Catch ex As Exception
            Logger.Warn(ex, "取消自定义下载失败")
        End Try
    End Sub

    ''' <summary>
    ''' 用启动器自带的下载器把直链下到指定路径。
    '''
    ''' 走 NetFile + LoaderDownload，与下载游戏文件、检查更新是同一套下载栈（多源、分块、断点续传、
    ''' 失败重试都复用）。先下到 .part，成功后改名，失败或取消不会留下半个目标文件。
    ''' </summary>
    Private Sub StartCustomDownload(Url As String, Target As String)
        CustomRunning = True
        BtnCustomDownload.IsEnabled = False
        BtnCustomDownload.Text = "正在下载……"
        BtnCustomCancel.Visibility = Visibility.Visible
        SetCustomProgress(0, "正在连接下载源……")

        Dim PartPath As String = Target & ".part"

        RunInNewThread(
        Sub()
            Dim Task As LoaderDownload = Nothing
            Dim Failure As Exception = Nothing
            Dim Canceled As Boolean = False
            Try
                Dim File As New NetFile(New List(Of String) From {Url}, PartPath, SimulateBrowserHeaders:=True)
                Task = New LoaderDownload("下载自定义文件", New List(Of NetFile) From {File})
                CustomTask = Task
                Task.Start()

                'LoaderDownload 的进度由 NetManager 每 0.1 秒刷新一次，这里主动轮询
                Dim StartTick As Long = GetTimeMs()
                Do While Task.State = LoadState.Loading
                    Dim Value As Double = Task.Progress
                    If Double.IsNaN(Value) OrElse Value < 0 Then Value = 0
                    '大小与速度都取自 NetFile：它每 200ms 自己算一次速率，比在界面层估算准
                    Dim Done As Long = File.DownloadDone
                    Dim Total As Long = File.FileSize
                    Dim Speed As Long = File.Speed
                    RunInUi(Sub() SetCustomProgress(Value, CustomDownloadText(Value, Done, Total, Speed)))
                    '长时间停在很低的进度上，多半还在连接或探测文件大小
                    If Value < 0.05 AndAlso GetTimeMs() - StartTick > 3000 Then
                        RunInUi(Sub()
                                    If CustomRunning Then LabCustomProgress.Text = "正在连接下载源……"
                                End Sub)
                    End If
                    System.Threading.Thread.Sleep(100)
                Loop

                Canceled = Task.State = LoadState.Canceled
                If Task.Error IsNot Nothing Then Failure = Task.Error
                If Not Canceled AndAlso Failure Is Nothing Then
                    If Not IO.File.Exists(PartPath) Then Throw New Exception("下载结束但没有拿到文件")
                    If IO.File.Exists(Target) Then IO.File.Delete(Target)
                    IO.File.Move(PartPath, Target)
                End If
            Catch ex As Exception
                Failure = ex
            Finally
                Try
                    Task?.Cancel()
                Catch
                End Try
                If Canceled OrElse Failure IsNot Nothing Then
                    Try
                        If IO.File.Exists(PartPath) Then IO.File.Delete(PartPath)
                    Catch
                    End Try
                End If
            End Try

            RunInUi(
            Sub()
                CustomRunning = False
                CustomTask = Nothing
                BtnCustomDownload.IsEnabled = True
                BtnCustomDownload.Text = "开始下载"
                BtnCustomCancel.Visibility = Visibility.Collapsed

                If Canceled Then
                    PanCustomProgress.Visibility = Visibility.Collapsed
                    Hint("已取消下载", HintType.Blue)
                ElseIf Failure IsNot Nothing Then
                    PanCustomProgress.Visibility = Visibility.Collapsed
                    Logger.Warn(Failure, $"下载自定义文件失败：{Url}")
                    Hint("下载失败：" & Failure.Message, HintType.Red)
                Else
                    Dim Size As Long = 0
                    Try
                        If IO.File.Exists(Target) Then Size = New IO.FileInfo(Target).Length
                    Catch
                    End Try
                    If Size <= 0 Then
                        PanCustomProgress.Visibility = Visibility.Collapsed
                        Hint("下载失败：没有拿到文件内容", HintType.Red)
                    Else
                        SetCustomProgress(1, $"下载完成：{IO.Path.GetFileName(Target)}（{FormatSize(Size)}）")
                        Logger.Info($"自定义下载完成：{Target}（{FormatSize(Size)}）")
                        Hint("下载完成：" & IO.Path.GetFileName(Target), HintType.Green)
                    End If
                End If
            End Sub)
        End Sub, "下载自定义文件")
    End Sub

    ''' <summary>
    ''' 拼出下载中的状态文字：百分比 / 已下载与总大小 / 当前速度。
    ''' 服务器没给 Content-Length 时只显示已下载量。
    ''' </summary>
    Private Shared Function CustomDownloadText(Value As Double, Done As Long, Total As Long, Speed As Long) As String
        Dim Parts As New List(Of String) From {$"{Value * 100:F0}%"}
        If Total > 0 Then
            Parts.Add($"{FormatSize(Done)} / {FormatSize(Total)}")
        ElseIf Done > 0 Then
            Parts.Add(FormatSize(Done))
        End If
        If Speed > 0 Then Parts.Add(FormatSize(Speed) & "/s")
        Return "正在下载  " & String.Join("  ", Parts)
    End Function

    ''' <summary>
    ''' 刷新下载进度条。Value 为 0-1。
    ''' </summary>
    Private Sub SetCustomProgress(Value As Double, Message As String)
        PanCustomProgress.Visibility = Visibility.Visible
        ColCustomFill.Width = New GridLength(Math.Max(0.001, Value), GridUnitType.Star)
        ColCustomRest.Width = New GridLength(Math.Max(0.001, 1 - Value), GridUnitType.Star)
        LabCustomProgress.Text = Message
    End Sub

#End Region

End Class
