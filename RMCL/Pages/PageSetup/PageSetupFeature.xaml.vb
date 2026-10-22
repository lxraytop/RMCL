Public Class PageSetupFeature

    Private Sub PageSetupFeature_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '重复加载部分
        PanBack.ScrollToHome()
        RefreshMemoryInfo()
        RefreshUpdateInfo()

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

#Region "软件更新"

    ''' <summary>
    ''' 最近一次成功检查到的版本信息。
    ''' </summary>
    Private CurrentUpdate As ModUpdate.UpdateInfo = Nothing

    ''' <summary>
    ''' 刷新版本信息文本（只读本地状态，不发起网络请求）。
    ''' </summary>
    Private Sub RefreshUpdateInfo()
        If ModUpdate.HasPendingUpdate Then
            LabUpdateInfo.Text = $"当前版本：{VersionBaseName}" & vbCrLf & "已下载新的版本，退出启动器后会自动完成安装。"
        Else
            LabUpdateInfo.Text = $"当前版本：{VersionBaseName}" & vbCrLf & $"更新源：{ModUpdate.UpdateRepo}（GitHub Release）"
        End If
    End Sub

    Private Sub BtnUpdateCheck_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUpdateCheck.Click
        If Not BtnUpdateCheck.IsEnabled Then Return
        BtnUpdateCheck.IsEnabled = False
        BtnUpdateCheck.Text = "检查中……"
        LabUpdateInfo.Text = "正在从 GitHub 检查更新……"
        RunInNewThread(
        Sub()
            Dim Info As ModUpdate.UpdateInfo = Nothing
            Dim Err As String = Nothing
            Try
                Info = ModUpdate.FetchLatest()
            Catch ex As Exception
                Err = ex.Message
                Logger.Warn(ex, "检查启动器更新失败")
            End Try
            RunInUi(Sub() ShowUpdateResult(Info, Err))
        End Sub, "检查更新")
    End Sub

    ''' <summary>
    ''' 把检查结果显示到界面上（UI 线程）。
    ''' </summary>
    Private Sub ShowUpdateResult(Info As ModUpdate.UpdateInfo, Err As String)
        BtnUpdateCheck.Text = "检查更新"
        BtnUpdateCheck.IsEnabled = True
        If Err IsNot Nothing Then
            CurrentUpdate = Nothing
            BtnUpdateInstall.Visibility = Visibility.Collapsed
            LabUpdateNotesTitle.Visibility = Visibility.Collapsed
            PanUpdateNotes.Visibility = Visibility.Collapsed
            LabUpdateInfo.Text = $"检查更新失败：{Err}{vbCrLf}当前版本：{VersionBaseName}"
            Return
        End If

        CurrentUpdate = Info
        Dim TimeText As String = ""
        If Info.PublishedLocal.HasValue Then TimeText = "，发布于 " & Info.PublishedLocal.Value.ToString("yyyy-MM-dd HH:mm")

        If Not ModUpdate.IsNewer(Info.Version) Then
            BtnUpdateInstall.Visibility = Visibility.Collapsed
            LabUpdateNotesTitle.Visibility = Visibility.Collapsed
            PanUpdateNotes.Visibility = Visibility.Collapsed
            LabUpdateInfo.Text = $"当前版本：{VersionBaseName}（已是最新版本）" & vbCrLf & $"最新版本：{Info.Version}{TimeText}"
            Hint("启动器已是最新版本", HintType.Green)
            Return
        End If

        Dim InfoText As String = $"当前版本：{VersionBaseName}" & vbCrLf & $"最新版本：{Info.Version}{TimeText}"
        If Not Info.HasAsset Then InfoText &= vbCrLf & "该版本没有提供 exe 资源，请到发布页手动下载。"
        LabUpdateInfo.Text = InfoText
        BtnUpdateInstall.Visibility = If(Info.HasAsset, Visibility.Visible, Visibility.Collapsed)
        TextUpdateNotes.Text = Info.Notes
        LabUpdateNotesTitle.Visibility = Visibility.Visible
        PanUpdateNotes.Visibility = Visibility.Visible
        Hint($"发现新版本 {Info.Version}", HintType.Blue)
    End Sub

    Private Sub BtnUpdateInstall_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUpdateInstall.Click
        Dim Info As ModUpdate.UpdateInfo = CurrentUpdate
        If Info Is Nothing OrElse Not Info.HasAsset Then Return
        If Not BtnUpdateInstall.IsEnabled Then Return
        Dim SizeText As String = If(Info.AssetSize > 0, FormatSize(Info.AssetSize), "大小未知")
        If MyMsgBox($"即将下载 RMCL {Info.Version}（{SizeText}）。" & vbCrLf &
                    "下载完成后可以立即重启安装，也可以选择在退出启动器时自动安装。",
                    "安装更新", "下载", "取消") <> 1 Then Return

        BtnUpdateInstall.IsEnabled = False
        BtnUpdateInstall.Text = "下载中……"
        LabUpdateInfo.Text = $"正在下载 RMCL {Info.Version}……"
        RunInNewThread(
        Sub()
            Dim LocalPath As String = Nothing
            Dim Err As String = Nothing
            Try
                LocalPath = ModUpdate.DownloadUpdate(Info,
                    Sub(Value As Double) RunInUi(Sub() BtnUpdateInstall.Text = $"下载中 {Value * 100:F0}%"))
            Catch ex As Exception
                Err = ex.Message
                Logger.Warn(ex, "下载启动器更新失败")
            End Try
            RunInUi(Sub() FinishUpdateDownload(Info, LocalPath, Err))
        End Sub, "下载更新")
    End Sub

    ''' <summary>
    ''' 下载结束后的收尾（UI 线程）。
    ''' </summary>
    Private Sub FinishUpdateDownload(Info As ModUpdate.UpdateInfo, LocalPath As String, Err As String)
        BtnUpdateInstall.Text = "下载并安装"
        BtnUpdateInstall.IsEnabled = True
        If Err IsNot Nothing Then
            LabUpdateInfo.Text = $"下载更新失败：{Err}"
            Return
        End If
        '与自动更新共用同一套「下载后询问是否立即重启」的收尾
        ModUpdate.PromptInstallAfterDownload(Info, LocalPath,
            Sub() LabUpdateInfo.Text = $"当前版本：{VersionBaseName}" & vbCrLf & $"待安装：{Info.Version}（退出启动器后自动安装）")
    End Sub

    Private Sub BtnUpdatePage_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUpdatePage.Click
        OpenWebsite(If(CurrentUpdate?.PageUrl, ModUpdate.UpdatePage))
    End Sub

#End Region

End Class
