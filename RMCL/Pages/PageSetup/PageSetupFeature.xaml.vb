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
        RunInNewThread(
        Sub()
            Try
                MemoryOptimize(True)
            Catch ex As Exception
                Logger.Warn(ex, "内存优化失败")
            Finally
                RunInUi(
                Sub()
                    BtnMemoryOptimize.Text = "立即优化内存"
                    BtnMemoryOptimize.IsEnabled = True
                    RefreshMemoryInfo()
                End Sub)
            End Try
        End Sub, "内存优化")
    End Sub

#End Region

#Region "清理游戏垃圾"

    Private Sub RefreshJunkInfo()
        Try
            Dim Summary = GetRubbishSummary()
            LabJunkInfo.Text = $"可清理空间合计：{FormatSize(Summary.Total)}" & vbCrLf & Summary.Details.Join(vbCrLf)
        Catch ex As Exception
            Logger.Warn(ex, "统计垃圾文件失败")
            LabJunkInfo.Text = "无法统计垃圾文件。"
        End Try
    End Sub

    Private Sub BtnJunkRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJunkRefresh.Click
        If Not BtnJunkRefresh.IsEnabled Then Return
        BtnJunkRefresh.IsEnabled = False
        RunInNewThread(
        Sub()
            Try
                RefreshJunkInfo()
            Finally
                RunInUi(Sub() BtnJunkRefresh.IsEnabled = True)
            End Try
        End Sub, "统计垃圾")
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
