Public Class PageSetupFrp

    Private ReadOnly RefreshTimer As New Windows.Threading.DispatcherTimer
    Private Tunnels As New List(Of ModFrp.FrpTunnel)
    Private IsBusy As Boolean

    Private Sub PageSetupFrp_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If RefreshTimer.Interval = TimeSpan.Zero Then
            RefreshTimer.Interval = TimeSpan.FromMilliseconds(600)
            AddHandler RefreshTimer.Tick, AddressOf RefreshTimer_Tick
        End If
        RefreshTimer.Start()

        Static IsLoaded As Boolean = False
        If Not IsLoaded Then
            IsLoaded = True
            TextAccessKey.Text = Settings.Get(Of String)("FrpAccessKey")
            '页面可能是在隧道已运行的状态下被重新打开的，这里按当前状态回填
            Dim LastId As Integer = Settings.Get(Of Integer)("FrpLastTunnelId")
            If LastId > 0 Then LabTunnelHint.Text = $"上次使用的隧道 #{LastId}，点「读取我的隧道」可以重新选择。"
        End If
        RefreshUi()
    End Sub

    Private Sub PageSetupFrp_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        '只停刷新，不停隧道：切换设置页不应该把联机断掉
        RefreshTimer.Stop()
    End Sub

    Private Sub RefreshTimer_Tick(sender As Object, e As EventArgs)
        RefreshUi()
    End Sub

#Region "界面刷新"

    Private Sub RefreshUi()
        Try
            LabStatus.Text = ModFrp.StatusMessage

            Dim Address As String = ModFrp.RemoteAddress
            If String.IsNullOrEmpty(Address) Then
                LabRemote.Visibility = Visibility.Collapsed
            Else
                LabRemote.Visibility = Visibility.Visible
                LabRemote.Text = "远程地址：" & Address
            End If

            BtnStart.IsEnabled = Not IsBusy AndAlso Not ModFrp.IsActive AndAlso ComboTunnel.SelectedItem IsNot Nothing
            BtnStop.IsEnabled = Not IsBusy AndAlso ModFrp.IsActive
            BtnCopy.IsEnabled = Not String.IsNullOrEmpty(Address)
            BtnLoadTunnels.IsEnabled = Not IsBusy

            Dim LogText As String = String.Join(vbCrLf, ModFrp.GetLogs())
            If TextLog.Text <> LogText Then TextLog.Text = LogText
        Catch ex As Exception
            Logger.Warn(ex, "刷新樱花穿透界面失败")
        End Try
    End Sub

#End Region

#Region "账号与隧道"

    Private Sub TextAccessKey_TextChanged(sender As Object, e As TextChangedEventArgs) Handles TextAccessKey.TextChanged
        Settings.Set("FrpAccessKey", TextAccessKey.Text.Trim())
    End Sub

    Private Sub BtnLoadTunnels_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLoadTunnels.Click
        Dim AccessKey As String = TextAccessKey.Text.Trim()
        If AccessKey = "" Then
            Hint("请先填写樱花穿透的访问密钥", HintType.Red)
            TextAccessKey.Focus()
            Return
        End If
        IsBusy = True
        BtnLoadTunnels.Text = "读取中……"
        LabTunnelHint.Text = "正在向樱花穿透读取隧道列表……"
        RefreshUi()
        RunInNewThread(
        Sub()
            Try
                Dim Result As List(Of ModFrp.FrpTunnel) = ModFrp.FetchTunnels(AccessKey)
                RunInUi(
                Sub()
                    IsBusy = False
                    BtnLoadTunnels.Text = "读取我的隧道"
                    Tunnels = Result
                    FillTunnels()
                    RefreshUi()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "读取樱花穿透隧道失败")
                RunInUi(
                Sub()
                    IsBusy = False
                    BtnLoadTunnels.Text = "读取我的隧道"
                    LabTunnelHint.Text = "读取失败：" & ex.Message
                    Hint("读取隧道失败：" & ex.Message, HintType.Red)
                    RefreshUi()
                End Sub)
            End Try
        End Sub, "读取樱花穿透隧道")
    End Sub

    Private Sub FillTunnels()
        ComboTunnel.Items.Clear()
        If Tunnels.Count = 0 Then
            LabTunnelHint.Text = "这个账号下还没有隧道。请到樱花穿透网站创建一条 TCP 隧道，本地端口填 Minecraft 对局域网开放后显示的端口。"
            Return
        End If
        For Each Item As ModFrp.FrpTunnel In Tunnels
            ComboTunnel.Items.Add(New MyComboBoxItem With {.Content = Item.ToString, .Tag = Item.Id})
        Next
        '优先恢复上次选择的隧道
        Dim LastId As Integer = Settings.Get(Of Integer)("FrpLastTunnelId")
        Dim TargetIndex As Integer = 0
        For i As Integer = 0 To ComboTunnel.Items.Count - 1
            If Val(CType(ComboTunnel.Items(i), MyComboBoxItem).Tag) = LastId Then
                TargetIndex = i
                Exit For
            End If
        Next
        ComboTunnel.SelectedIndex = TargetIndex
        LabTunnelHint.Text = $"共 {Tunnels.Count} 条隧道。选中后点「启动隧道」；本地端口由隧道自身的配置决定。"
    End Sub

    Private Sub ComboTunnel_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboTunnel.SelectionChanged
        Dim Item As MyComboBoxItem = TryCast(ComboTunnel.SelectedItem, MyComboBoxItem)
        If Item IsNot Nothing Then Settings.Set("FrpLastTunnelId", Val(Item.Tag))
        RefreshUi()
    End Sub

#End Region

#Region "启停"

    Private Sub BtnStart_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStart.Click
        Dim AccessKey As String = TextAccessKey.Text.Trim()
        If AccessKey = "" Then
            Hint("请先填写樱花穿透的访问密钥", HintType.Red)
            Return
        End If
        Dim Item As MyComboBoxItem = TryCast(ComboTunnel.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then
            Hint("请先点「读取我的隧道」并选中一条隧道", HintType.Red)
            Return
        End If
        Dim TunnelId As String = Val(Item.Tag).ToString()
        IsBusy = True
        RefreshUi()
        RunInNewThread(
        Sub()
            Try
                ModFrp.StartSession(AccessKey, TunnelId)
            Catch ex As Exception
                Logger.Warn(ex, "启动樱花穿透失败")
                RunInUi(Sub() Hint("启动失败：" & ex.Message, HintType.Red))
            Finally
                RunInUi(
                Sub()
                    IsBusy = False
                    RefreshUi()
                End Sub)
            End Try
        End Sub, "启动樱花穿透")
    End Sub

    Private Sub BtnStop_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStop.Click
        ModFrp.StopSession()
        RefreshUi()
    End Sub

    Private Sub BtnCopy_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCopy.Click
        Dim Address As String = ModFrp.RemoteAddress
        If String.IsNullOrEmpty(Address) Then Return
        Try
            Clipboard.SetText(Address)
            Hint("远程地址已复制", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, "复制樱花穿透远程地址失败")
            Hint("复制失败，请手动选中地址", HintType.Red)
        End Try
    End Sub

    Private Sub BtnClearLog_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnClearLog.Click
        ModFrp.ClearLogs()
        RefreshUi()
    End Sub

    Private Sub BtnOpenSite_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenSite.Click
        OpenWebsite("https://www.natfrp.com/")
    End Sub

#End Region

End Class
