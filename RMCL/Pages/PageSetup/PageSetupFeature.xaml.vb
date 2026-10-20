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

        ScanJunk(False)
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
                                 $"系统可用内存：{Available} MB / 共 {Total} MB"
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
        Dim Before As Long = MemoryUsedMb()
        BtnMemoryOptimize.IsEnabled = False
        BtnMemoryOptimize.Text = "正在优化……"
        RunInNewThread(
        Sub()
            Try
                MemoryOptimize()
            Catch ex As Exception
                Logger.Warn(ex, "内存优化失败")
            Finally
                RunInUi(
                Sub()
                    BtnMemoryOptimize.Text = "立即优化内存"
                    BtnMemoryOptimize.IsEnabled = True
                    RefreshMemoryInfo()
                    Dim Freed As Long = Math.Max(0L, Before - MemoryUsedMb())
                    Hint($"内存优化完成，释放约 {Freed} MB", HintType.Green)
                End Sub)
            End Try
        End Sub, "内存优化")
    End Sub

#End Region

#Region "清理游戏垃圾"

    ''' <summary>
    ''' 统计各清理位置占用的空间。
    ''' </summary>
    Private Sub ScanJunk(Optional ShowHint As Boolean = True)
        Dim Details As New List(Of String)
        Dim Total As Long = 0
        For Each Target In GetCleanTargets()
            Dim Size As Long = GetCleanTargetSize(Target)
            Total += Size
            Details.Add($"{Target.Name}：{FormatSize(Size)}")
        Next
        LabJunkInfo.Text = $"可释放空间合计：{FormatSize(Total)}" & vbCrLf & Details.Join(vbCrLf)
        If ShowHint Then Hint("扫描完成", HintType.Green, False)
    End Sub

    Private Sub BtnJunkScan_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJunkScan.Click
        If Not BtnJunkScan.IsEnabled Then Return
        BtnJunkScan.IsEnabled = False
        RunInNewThread(
        Sub()
            Try
                ScanJunk()
            Finally
                RunInUi(Sub() BtnJunkScan.IsEnabled = True)
            End Try
        End Sub, "扫描垃圾")
    End Sub

    Private Sub BtnJunkClean_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJunkClean.Click
        If Not BtnJunkClean.IsEnabled Then Return
        If Not (Settings.Get(Of Boolean)("FeatureCleanTemp") OrElse Settings.Get(Of Boolean)("FeatureCleanCache") OrElse
                Settings.Get(Of Boolean)("FeatureCleanLog")) Then
            Hint("请先勾选至少一项要清理的内容！", HintType.Red)
            Return
        End If
        If MyMsgBox("确定要清理勾选的内容吗？该操作不可撤销。", "清理确认", "清理", "取消", IsWarn:=True) <> 1 Then Return
        BtnJunkClean.IsEnabled = False
        BtnJunkClean.Text = "正在清理……"
        RunInNewThread(
        Sub()
            Dim Freed As Long = 0
            Try
                Freed = CleanCheckedJunk()
            Catch ex As Exception
                Logger.Warn(ex, "清理垃圾失败")
            Finally
                RunInUi(
                Sub()
                    BtnJunkClean.Text = "立即清理"
                    BtnJunkClean.IsEnabled = True
                    ScanJunk(False)
                    Hint($"清理完成，释放约 {FormatSize(Freed)}", HintType.Green)
                End Sub)
            End Try
        End Sub, "清理垃圾")
    End Sub

#End Region

End Class
