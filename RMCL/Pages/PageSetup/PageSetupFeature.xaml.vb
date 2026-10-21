Public Class PageSetupFeature

    Private Sub PageSetupFeature_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '重复加载部分
        PanBack.ScrollToHome()
        RefreshMemoryInfo()

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

End Class
