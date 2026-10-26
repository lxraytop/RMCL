Public Class PageSetupFrp

    Private ReadOnly RefreshTimer As New Windows.Threading.DispatcherTimer
    Private Tunnels As New List(Of ModFrp.FrpTunnel)
    Private Nodes As New List(Of ModFrp.FrpNode)
    Private CurrentKey As String = ""
    Private IsLoggedIn As Boolean = False
    Private IsBusy As Boolean = False

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
            '有保存的密钥就直接进入已登录状态：输入框不再出现，用户主动退出登录才会回来
            CurrentKey = Settings.Get(Of String)("FrpAccessKey")
            If CurrentKey <> "" Then
                IsLoggedIn = True
                RefreshLoginUi()
                LoadAccount()
            Else
                IsLoggedIn = False
                RefreshLoginUi()
            End If
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

#Region "登录状态"

    ''' <summary>未登录只显示密钥输入框，已登录则换成账号条；两者互斥。</summary>
    Private Sub RefreshLoginUi()
        PanLogin.Visibility = If(IsLoggedIn, Visibility.Collapsed, Visibility.Visible)
        PanAccount.Visibility = If(IsLoggedIn, Visibility.Visible, Visibility.Collapsed)
        If Not IsLoggedIn Then PanCreate.Visibility = Visibility.Collapsed
        If IsLoggedIn Then LabAccount.Text = $"已登录 · 密钥 {ModFrp.MaskKey(CurrentKey)}"
        RefreshUi()
    End Sub

    Private Sub BtnLogin_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLogin.Click
        Dim Key As String = TextAccessKey.Text.Trim()
        If Key = "" Then
            Hint("请先填写樱花穿透的访问密钥", HintType.Red)
            TextAccessKey.Focus()
            Return
        End If
        '确认后才落盘并隐藏输入框
        CurrentKey = Key
        Settings.Set("FrpAccessKey", Key)
        IsLoggedIn = True
        TextAccessKey.Text = ""
        RefreshLoginUi()
        LoadAccount()
    End Sub

    Private Sub BtnLogout_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLogout.Click
        If MyMsgBox("退出后本机会忘记访问密钥，正在运行的隧道也会停止。确定退出吗？", "退出登录", "退出", "取消") <> 1 Then Return
        Try
            If ModFrp.IsActive Then ModFrp.StopSession()
        Catch ex As Exception
            Logger.Warn(ex, "退出登录时停止樱花穿透失败")
        End Try
        Settings.Set("FrpAccessKey", "")
        CurrentKey = ""
        TextAccessKey.Text = ""
        Tunnels.Clear()
        Nodes.Clear()
        ComboTunnel.Items.Clear()
        ComboNode.Items.Clear()
        IsLoggedIn = False
        LabTunnelHint.Text = "先填写访问密钥，再点「确认并读取隧道」。"
        RefreshLoginUi()
        Hint("已退出樱花穿透账号", HintType.Green)
    End Sub

    Private Sub BtnReloadTunnels_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnReloadTunnels.Click
        LoadAccount()
    End Sub

#End Region

#Region "读取隧道与节点"

    ''' <summary>后台读取隧道与节点，回来后一次性填界面。</summary>
    Private Sub LoadAccount()
        If CurrentKey = "" OrElse IsBusy Then Return
        IsBusy = True
        LabTunnelHint.Text = "正在读取隧道……"
        RefreshUi()
        Dim Key As String = CurrentKey
        RunInNewThread(
        Sub()
            Dim TunnelResult As List(Of ModFrp.FrpTunnel) = Nothing
            Dim NodeResult As List(Of ModFrp.FrpNode) = Nothing
            Dim Failure As Exception = Nothing
            Try
                TunnelResult = ModFrp.FetchTunnels(Key)
            Catch ex As Exception
                Failure = ex
            End Try
            '节点只在新建隧道时用得上，读不到不影响启动
            Try
                NodeResult = ModFrp.FetchNodes(Key)
            Catch ex As Exception
                Logger.Warn(ex, "读取樱花穿透节点失败")
            End Try
            RunInUi(
            Sub()
                IsBusy = False
                If Failure IsNot Nothing Then
                    Logger.Warn(Failure, "读取樱花穿透隧道失败")
                    LabTunnelHint.Text = "读取失败：" & Failure.Message
                    Hint("读取隧道失败：" & Failure.Message, HintType.Red)
                    RefreshUi()
                    Return
                End If
                Tunnels = TunnelResult
                Nodes = If(NodeResult, New List(Of ModFrp.FrpNode))
                FillTunnels()
                FillNodes()
                RefreshUi()
            End Sub)
        End Sub, "读取樱花穿透账号")
    End Sub

    Private Sub FillTunnels()
        ComboTunnel.Items.Clear()
        If Tunnels.Count = 0 Then
            LabTunnelHint.Text = "这个账号下还没有隧道，点「新建隧道」可以直接建一条 TCP 隧道。"
            Return
        End If
        For Each Item As ModFrp.FrpTunnel In Tunnels
            ComboTunnel.Items.Add(New MyComboBoxItem With {.Content = Item.ToString, .Tag = Item.Id})
        Next
        '优先恢复上次选择的隧道（新建成功后也会写这里，于是自动选中新隧道）
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

    Private Sub FillNodes()
        ComboNode.Items.Clear()
        For Each Item As ModFrp.FrpNode In Nodes
            '离线或已满载的节点不能建隧道，直接不列出来
            If Not Item.Online OrElse Not Item.CanCreate Then Continue For
            ComboNode.Items.Add(New MyComboBoxItem With {.Content = Item.ToString, .Tag = Item.Id})
        Next
        If ComboNode.Items.Count > 0 Then ComboNode.SelectedIndex = 0
    End Sub

#End Region

#Region "新建隧道"

    Private Sub BtnNewTunnel_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnNewTunnel.Click
        PanCreate.Visibility = If(PanCreate.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
        If PanCreate.Visibility = Visibility.Visible Then
            If TextCreateName.Text.Trim() = "" Then TextCreateName.Text = "Minecraft"
            If ComboNode.Items.Count = 0 Then Hint("没有可用的服务节点，稍后再试或到樱花网站创建", HintType.Red)
            '展开时顺手识别一次：用户这会儿多半就是刚在游戏里开了局域网
            If TextCreateLocalPort.Text.Trim() = "" Then DetectLocalPort()
            TextCreateLocalPort.Focus()
        End If
    End Sub

    ''' <summary>是否正在识别端口，避免连点。</summary>
    Private IsDetectingPort As Boolean

    Private Sub BtnDetectPort_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnDetectPort.Click
        DetectLocalPort()
    End Sub

    ''' <summary>
    ''' 从正在运行的 Minecraft 里读出局域网端口，填进「本地端口」。
    ''' 复用联机页那套探测：先找 java / javaw 进程监听的端口，再用状态协议确认是 MC 服务器。
    ''' 识别不到也不拦着，用户可以直接手填。
    ''' </summary>
    Private Sub DetectLocalPort()
        If IsDetectingPort Then Return
        IsDetectingPort = True
        BtnDetectPort.IsEnabled = False
        LabCreatePortHint.Text = "正在查找正在运行的 Minecraft……"
        RunInNewThread(
        Sub()
            Dim Worlds As List(Of ModLink.LinkWorld) = Nothing
            Dim Failure As Exception = Nothing
            Try
                Worlds = ModLink.LinkDiscoverWorlds()
            Catch ex As Exception
                Failure = ex
            End Try
            RunInUi(
            Sub()
                IsDetectingPort = False
                BtnDetectPort.IsEnabled = True
                If Failure IsNot Nothing Then
                    Logger.Warn(Failure, "识别 Minecraft 局域网端口失败")
                    LabCreatePortHint.Text = "自动识别失败，请手动填写端口。"
                    Return
                End If
                If Worlds Is Nothing OrElse Worlds.Count = 0 Then
                    LabCreatePortHint.Text = "没有找到正在运行的 Minecraft 局域网世界。请先在游戏里按 Esc → 对局域网开放，再点「自动识别」；也可以直接填端口。"
                    Return
                End If
                '优先用已确认是 MC 服务器的那个（有 MOTD），「未识别的端口」只作兜底
                Dim Best As ModLink.LinkWorld = Worlds.FirstOrDefault(
                    Function(W) Not String.IsNullOrWhiteSpace(W.Motd) AndAlso W.Motd <> "未识别的端口")
                If Best Is Nothing Then Best = Worlds(0)
                TextCreateLocalPort.Text = Best.Port.ToString()
                If Worlds.Count = 1 Then
                    LabCreatePortHint.Text = $"已识别：{Best.Display}。远程端口留空则由樱花分配。"
                Else
                    Dim Others As String = Worlds.Where(Function(W) W IsNot Best).Select(Function(W) W.Port.ToString()).Join("，")
                    LabCreatePortHint.Text = $"已填入 {Best.Display}；另外还检测到 {Others}，需要的话手动改。"
                End If
            End Sub)
        End Sub, "识别局域网端口")
    End Sub

    Private Sub BtnCancelCreate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCancelCreate.Click
        PanCreate.Visibility = Visibility.Collapsed
    End Sub

    ''' <summary>删除当前选中的隧道。会从樱花账号里永久删掉，必须先二次确认。</summary>
    Private Sub BtnDeleteTunnel_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnDeleteTunnel.Click
        Dim Item As MyComboBoxItem = TryCast(ComboTunnel.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then
            Hint("请先选中要删除的隧道", HintType.Red)
            Return
        End If
        Dim TunnelId As Integer = Val(Item.Tag)
        Dim Target As ModFrp.FrpTunnel = Tunnels.FirstOrDefault(Function(T) T.Id = TunnelId)
        Dim TunnelName As String = If(Target IsNot Nothing, Target.Name, "#" & TunnelId)
        Dim IsRunningThis As Boolean = (ModFrp.RunningTunnelId = TunnelId)
        Dim Extra As String = If(IsRunningThis, vbCrLf & "这条隧道正在运行，删除前会先断开。", "")
        If MyMsgBox($"确定要从樱花账号永久删除隧道「{TunnelName}」（#{TunnelId}）吗？" & vbCrLf &
                    "这会删除樱花穿透服务器上的隧道配置，无法撤销；其他隧道不受影响。" & Extra,
                    "删除隧道", "删除", "取消") <> 1 Then Return

        '只停正在跑的这一条，别的隧道不动
        If IsRunningThis Then
            Try
                ModFrp.StopSession()
            Catch ex As Exception
                Logger.Warn(ex, "删除隧道前停止樱花穿透失败")
            End Try
        End If

        IsBusy = True
        RefreshUi()
        RunInNewThread(
        Sub()
            Try
                Dim DisconnectPending As Boolean = ModFrp.DeleteTunnel(CurrentKey, TunnelId)
                RunInUi(
                Sub()
                    IsBusy = False
                    '删掉的正好是记住的那条，重置后再刷新就会落到第一条
                    If Settings.Get(Of Integer)("FrpLastTunnelId") = TunnelId Then Settings.Set("FrpLastTunnelId", 0)
                    If DisconnectPending Then
                        Hint($"隧道「{TunnelName}」已删除，远程连接稍后断开", HintType.Green)
                    Else
                        Hint($"隧道「{TunnelName}」已删除", HintType.Green)
                    End If
                    LoadAccount()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "删除樱花穿透隧道失败")
                RunInUi(
                Sub()
                    IsBusy = False
                    Hint("删除隧道失败：" & ex.Message, HintType.Red)
                    RefreshUi()
                End Sub)
            End Try
        End Sub, "删除樱花穿透隧道")
    End Sub

    Private Sub BtnCreateTunnel_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCreateTunnel.Click
        Dim Name As String = TextCreateName.Text.Trim()
        Dim NodeItem As MyComboBoxItem = TryCast(ComboNode.SelectedItem, MyComboBoxItem)
        Dim LocalPortText As String = TextCreateLocalPort.Text.Trim()
        Dim RemotePortText As String = TextCreateRemotePort.Text.Trim()

        If Name.Length = 0 Then
            Hint("请填写隧道名称", HintType.Red)
            TextCreateName.Focus()
            Return
        End If
        If NodeItem Is Nothing Then
            Hint("请选择服务节点", HintType.Red)
            Return
        End If
        Dim LocalPort As Integer = Val(LocalPortText)
        If LocalPort < 1 OrElse LocalPort > 65535 Then
            Hint("本地端口需要是 1-65535，也就是游戏里对局域网开放后显示的端口", HintType.Red)
            TextCreateLocalPort.Focus()
            Return
        End If
        Dim RemotePort As Integer = 0
        If RemotePortText <> "" Then
            RemotePort = Val(RemotePortText)
            If RemotePort < 1 OrElse RemotePort > 65535 Then
                Hint("远程端口需要是 1-65535，或者留空由樱花分配", HintType.Red)
                TextCreateRemotePort.Focus()
                Return
            End If
        End If

        Dim NodeId As Integer = Val(NodeItem.Tag)
        IsBusy = True
        RefreshUi()
        RunInNewThread(
        Sub()
            Try
                Dim Created As ModFrp.FrpTunnel = ModFrp.CreateTunnel(CurrentKey, Name, NodeId, LocalPort, RemotePort)
                RunInUi(
                Sub()
                    IsBusy = False
                    PanCreate.Visibility = Visibility.Collapsed
                    TextCreateRemotePort.Text = ""
                    '记住新隧道，刷新后自动选中它
                    Settings.Set("FrpLastTunnelId", Created.Id)
                    Hint($"隧道「{Name}」已创建（#{Created.Id}）", HintType.Green)
                    LoadAccount()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "创建樱花穿透隧道失败")
                RunInUi(
                Sub()
                    IsBusy = False
                    Hint("创建隧道失败：" & ex.Message, HintType.Red)
                    RefreshUi()
                End Sub)
            End Try
        End Sub, "创建樱花穿透隧道")
    End Sub

#End Region

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

            BtnLogin.IsEnabled = Not IsBusy
            BtnReloadTunnels.IsEnabled = Not IsBusy
            BtnLogout.IsEnabled = Not IsBusy
            BtnNewTunnel.IsEnabled = Not IsBusy
            BtnDeleteTunnel.IsEnabled = Not IsBusy AndAlso ComboTunnel.SelectedItem IsNot Nothing
            BtnCreateTunnel.IsEnabled = Not IsBusy
            BtnStart.IsEnabled = Not IsBusy AndAlso Not ModFrp.IsActive AndAlso ComboTunnel.SelectedItem IsNot Nothing
            BtnStop.IsEnabled = Not IsBusy AndAlso ModFrp.IsActive
            BtnCopy.IsEnabled = Not String.IsNullOrEmpty(Address)

            Dim LogText As String = String.Join(vbCrLf, ModFrp.GetLogs())
            If TextLog.Text <> LogText Then TextLog.Text = LogText
        Catch ex As Exception
            Logger.Warn(ex, "刷新樱花穿透界面失败")
        End Try
    End Sub

#End Region

#Region "启停"

    Private Sub ComboTunnel_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboTunnel.SelectionChanged
        Dim Item As MyComboBoxItem = TryCast(ComboTunnel.SelectedItem, MyComboBoxItem)
        If Item IsNot Nothing Then Settings.Set("FrpLastTunnelId", Val(Item.Tag))
        RefreshUi()
    End Sub

    Private Sub BtnStart_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStart.Click
        If CurrentKey = "" Then
            Hint("请先登录樱花穿透", HintType.Red)
            Return
        End If
        Dim Item As MyComboBoxItem = TryCast(ComboTunnel.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then
            Hint("请先选中一条隧道", HintType.Red)
            Return
        End If
        Dim TunnelId As String = Val(Item.Tag).ToString()
        IsBusy = True
        RefreshUi()
        RunInNewThread(
        Sub()
            Try
                ModFrp.StartSession(CurrentKey, TunnelId)
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
