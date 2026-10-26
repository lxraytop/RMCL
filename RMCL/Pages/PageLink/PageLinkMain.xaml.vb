Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Net.Sockets
Imports System.Reflection
Imports RMCL

Public Class PageLinkMain
    Private Const INVITE_CODE_VERSION As Integer = 2

    '===============================
    '  状态机与前端页面
    '===============================

#Region "状态管理"

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum
    Public Shared LinkState As LinkStates = LinkStates.Waiting

    ''' <summary>
    ''' 切换到指定的状态。
    ''' </summary>
    Private Sub ChangeState(NewState As LinkStates)
        If LinkState = NewState Then Return
        Dim OldState = LinkState
        LinkState = NewState
        Logger.Info($"主状态由 {OldState} 变更为 {NewState}")
        '触发状态切换
        RunInUi(
        Sub()
            Select Case NewState
                Case LinkStates.Waiting
                    SwitchToWaiting(OldState)
                Case LinkStates.Loading
                    SwitchToLoading(OldState)
                Case LinkStates.Failed
                    SwitchToFailed(OldState)
                Case LinkStates.Finished
                    SwitchToFinished(OldState)
            End Select
        End Sub)
    End Sub

    '让加载器的 成功/失败 事件触发状态切换
    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, LinkLoader, AutoRun:=False)
        AddHandler LinkLoader.OnStateChangedUi,
        Sub(Loader As LoaderBase, NewState As LoadState, OldState As LoadState)
            Select Case NewState
                Case LoadState.Finished
                    ChangeState(LinkStates.Finished)
                Case LoadState.Failed
                    ChangeState(LinkStates.Failed)
            End Select
        End Sub
    End Sub

    '页面动画兼容
    Private Sub UpdatePanelVisibility() Handles Me.PageEnter
        PanSelect.Visibility = If(LinkState = LinkStates.Waiting, Visibility.Visible, Visibility.Collapsed)
        PanFinish.Visibility = If(LinkState = LinkStates.Finished, Visibility.Visible, Visibility.Collapsed)
        '进入页面时同步一次可用的游戏版本（可能刚下载了新版本）
        RunInUi(AddressOf RefreshLaunchVersionList)
    End Sub

#End Region

#Region "初始页面 (Waiting)"

    '由任意状态切换到 Waiting
    Private Sub SwitchToWaiting(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        SyncLock LinkLoader.LockState
            LinkLoader.State = LoadState.Waiting
        End SyncLock
        ProcessStop()
        '页面切换
        If OldState <> LinkStates.Finished Then
            '从完成页面退出时，PageOnContentExit 结束会触发 PageEnter 事件，这会调用 UpdatePanelVisibility
            '如果在现在提前调用了，会丢失完成页面的退出动画
            UpdatePanelVisibility()
        End If
        PageOnContentExit()
    End Sub

    '===============================
    '  页面 UI
    '===============================

#Region "创建房间"

    ''' <summary>
    ''' 是否正在扫描局域网世界。
    ''' </summary>
    Private IsScanningWorld As Boolean = False

    '点击创建
    Private Sub BtnCreate_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Port As Integer = GetSelectedWorldPort()
        If Port <= 0 Then
            Hint("请先在游戏里对局域网开放，再点「刷新」选择世界", HintType.Red)
            Return
        End If
        CreateRoom(Port)
    End Sub

    '手动输入端口
    Private Sub BtnInputPort_Click(sender As Object, e As MouseButtonEventArgs)
        Dim LastPort As Integer = Settings.Get(Of Integer)("LinkLastCreatePort")
        Dim Port As String = MyMsgBoxInput("输入端口",
            $"在单人游戏的暂停菜单选择 {vbLQ}对局域网开放{vbRQ}，然后输入端口数字。{vbCrLf}若已有检测到的世界，直接在上面选择即可。",
            ValidateRules:=New Collection(Of Validate) From {New ValidateInteger(1024, 65535)},
            HintText:=If(LastPort > 0, $"上一次使用的端口：{LastPort}", "端口号"))
        If Port Is Nothing Then Return
        Dim PortValue As Integer = Val(Port)
        If PortValue < 1024 OrElse PortValue > 65535 Then Return
        Settings.Set("LinkLastCreatePort", PortValue)
        '作为一项加进下拉框并选中
        ComboWorldList.Items.Add(New MyComboBoxItem With {.Content = $"手动指定的端口 {PortValue}", .Tag = PortValue})
        ComboWorldList.SelectedIndex = ComboWorldList.Items.Count - 1
        LabWorldHint.Text = $"将使用端口 {PortValue} 创建房间。"
    End Sub

    '刷新局域网世界列表
    Private Sub BtnScanWorld_Click(sender As Object, e As MouseButtonEventArgs)
        RefreshWorldList(Manual:=True)
    End Sub

    '取当前选中的端口，未选中返回 0
    Private Function GetSelectedWorldPort() As Integer
        Dim Item As MyComboBoxItem = TryCast(ComboWorldList.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then Return 0
        Return Val(Item.Tag)
    End Function

    ''' <summary>
    ''' 扫描本机正在运行的局域网世界，填充下拉框。
    ''' </summary>
    Private Sub RefreshWorldList(Manual As Boolean)
        If IsScanningWorld Then Return
        IsScanningWorld = True
        If Manual Then
            LabWorldHint.Text = "正在检测局域网世界……"
            BtnScanWorld.IsEnabled = False
        End If
        RunInNewThread(
        Sub()
            Dim Worlds As List(Of LinkWorld) = LinkDiscoverWorlds()
            RunInUi(
            Sub()
                IsScanningWorld = False
                BtnScanWorld.IsEnabled = True
                Dim LastSelected As Integer = GetSelectedWorldPort()
                ComboWorldList.Items.Clear()
                For Each World As LinkWorld In Worlds
                    ComboWorldList.Items.Add(New MyComboBoxItem With {.Content = World.Display, .Tag = World.Port})
                Next
                If Worlds.Any Then
                    '优先保持原来的选择
                    Dim RestoreIndex As Integer = -1
                    For i = 0 To ComboWorldList.Items.Count - 1
                        If Val(CType(ComboWorldList.Items(i), MyComboBoxItem).Tag) = LastSelected Then RestoreIndex = i
                    Next
                    ComboWorldList.SelectedIndex = If(RestoreIndex >= 0, RestoreIndex, 0)
                    LabWorldHint.Text = $"检测到 {Worlds.Count} 个局域网世界，选中后点「创建」。"
                Else
                    LabWorldHint.Text = "没有检测到局域网世界。请先在游戏里按 Esc → 对局域网开放，然后点「刷新」；也可以点「手动端口」直接填。"
                End If
            End Sub)
        End Sub, "Link World Scan")
    End Sub

    ''' <summary>
    ''' 以指定端口创建房间。
    ''' </summary>
    Private Sub CreateRoom(Port As Integer)
        If Port < 1024 OrElse Port > 65535 Then Return
        '基础信息
        IsServerSide = True
        ServerPort = Port
        Settings.Set("LinkLastCreatePort", Port)
        Dim GenerateRandomCode =
        Function() As String
            Dim Result As String = ""
            For i = 1 To 5
                Result &= RandomInteger(0, 35).ConvertRadix(36)
            Next
            '邀请码会被统一转为大写后重新输入，因此这里也统一使用大写
            Return Result.Replace("O", "0").Replace("I", "1").ToUpper()
        End Function
        NetworkName = $"P{ServerPort.ConvertRadix(16).PadLeft(4, "0"c)}-{GenerateRandomCode()}".ToUpper()
        NetworkSecret = GenerateRandomCode()
        Logger.Info($"尝试创建房间，网络名 {NetworkName}，网络密码 {NetworkSecret}，端口 {ServerPort}")
        '启动
        ChangeState(LinkStates.Loading)
    End Sub

#End Region

#Region "加入房间"

    '点击加入
    Private Sub BtnJoin_Click(sender As Object, e As MouseButtonEventArgs)
        DoJoin()
    End Sub

    '从剪贴板填入邀请码
    Private Sub BtnJoinPaste_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Text As String = ClipboardGetText()
        If String.IsNullOrEmpty(Text) Then
            Hint("剪贴板里没有文本", HintType.Red)
            Return
        End If
        TextJoinCode.Text = FixCodeFormat(Text)
        TextJoinCode.CaretIndex = TextJoinCode.Text.Length
    End Sub

    '清空输入框
    Private Sub BtnJoinClear_Click(sender As Object, e As MouseButtonEventArgs)
        TextJoinCode.Text = ""
        TextJoinCode.Focus()
    End Sub

    '回车直接加入
    Private Sub TextJoinCode_KeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter Then
            e.Handled = True
            DoJoin()
        End If
    End Sub

    ''' <summary>
    ''' 读取输入框中的邀请码并加入房间。
    ''' </summary>
    Private Sub DoJoin()
        Dim Code As String = If(TextJoinCode.Text, "").Trim()
        If String.IsNullOrEmpty(Code) Then
            Code = Settings.Get(Of String)("LinkLastInviteCode")
            If String.IsNullOrEmpty(Code) Then
                Hint("请先填写邀请码", HintType.Red)
                TextJoinCode.Focus()
                Return
            End If
            TextJoinCode.Text = Code
        End If
        Join(Code)
    End Sub

    '页面进入时恢复上一次的邀请码，便于直接重连
    Private Sub CardJoin_Loaded() Handles CardJoin.Loaded
        '1. 恢复上一次使用的邀请码
        If String.IsNullOrEmpty(TextJoinCode.Text) Then
            Dim LastInviteCode As String = Settings.Get(Of String)("LinkLastInviteCode")
            If Not String.IsNullOrEmpty(LastInviteCode) Then TextJoinCode.Text = LastInviteCode
        End If
        '2. 从剪贴板识别到的邀请码：填入并直接加入
        If AutoJoinCode Is Nothing Then Return
        If LinkState = LinkStates.Waiting Then
            TextJoinCode.Text = AutoJoinCode
            JoinInternal(AutoJoinCode)
        End If
        AutoJoinCode = Nothing
    End Sub

    '从外部（自定义事件）调用：弹框询问邀请码
    Private Shared LastCode As String = Nothing
    ''' <summary>
    ''' 询问邀请码，并切换到联机页加入房间。
    ''' </summary>
    Public Shared Sub Join()
        Dim Code As String = MyMsgBoxInput("输入邀请码", "输入房主发给你的邀请码。",
            HintText:=If(String.IsNullOrEmpty(LastCode), "", "使用上一次的邀请码"))
        If Not String.IsNullOrEmpty(LastCode) AndAlso Code IsNot Nothing AndAlso Code = "" Then Code = LastCode
        If Code Is Nothing Then Return
        Join(Code)
    End Sub
    Public Sub JoinInternal(Code As String)
        If Code Is Nothing Then Return
        If LinkState <> LinkStates.Waiting Then Return '已经在房间中或正在连接
        '基础格式校验
        Dim ValidateResult = ValidateCodeFormat(Code)
        If ValidateResult IsNot Nothing Then
            Hint(ValidateResult, HintType.Red)
            Return
        End If
        Code = FixCodeFormat(Code)
        Logger.Info($"实际使用的邀请码：{Code}")
        '基础信息
        IsServerSide = False
        ServerPort = Code.Substring(1, 4).Upper.ConvertRadix(16, 10)
        NetworkName = Code.Substring(0, 11)
        NetworkSecret = Code.Substring(12, 5)
        Logger.Info($"尝试加入房间，网络名 {NetworkName}，网络密码 {NetworkSecret}，端口 {ServerPort}")
        '启动
        LastCode = Code
        Settings.Set("LinkLastInviteCode", Code)
        ChangeState(LinkStates.Loading)
    End Sub
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If Code Is Nothing Then Return "邀请码为空！"
        Code = FixCodeFormat(Code)
        '判断类型
        If Not (Code.Length >= 14 AndAlso Code(0) = "P"c AndAlso Code(5) = "-"c AndAlso Code(11) = "-"c) Then
            If Code.StartsWithF("U/") Then 'HMCL
                Return "请让房主使用 RMCL 创建房间！"
            ElseIf Code.Length = 10 Then 'RMCL CE
                Return "请让房主使用非社区版的 RMCL 创建房间！"
            Else
                Return "邀请码有误，请让房主使用 RMCL 创建房间！"
            End If
        End If
        '校验版本
        If Code.Length >= 23 AndAlso Code(17) = "-"c AndAlso
            Val(Code.Substring(18, 2)) > INVITE_CODE_VERSION Then Return "你的 RMCL 版本太老了，请在更新 RMCL 之后再联机！"
        Return Nothing
    End Function
    Private Shared Function FixCodeFormat(Code As String) As String
        Code = Code.Between("【", "】").Between("[", "]") '从完整消息中提取
        Code = Code.Upper.Replace("O", "0").Replace("I", "1") '输入修正
        '版本 1 兼容
        If Code.Length >= 17 AndAlso (Code.Length < 23 OrElse (Code.Length >= 18 AndAlso Code(17) <> "-"c)) Then
            Code = Code.Substring(0, 17) & "-0105E"
        End If
        Return Code
    End Function

    '自动加入

    ''' <summary>
    ''' 切换到联机页并立即加入指定房间。
    ''' </summary>
    Public Shared Sub Join(Code As String)
        If LinkState <> LinkStates.Waiting Then
            Hint("你已经在联机房间中了！", HintType.Red)
        ElseIf FrmMain.PageCurrent = FormMain.PageType.Setup AndAlso FrmMain.PageCurrentSub = FormMain.PageSubType.SetupLink AndAlso FrmLinkMain IsNot Nothing Then
            FrmLinkMain.JoinInternal(Code)
        Else
            AutoJoinCode = Code
            FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLink)
            '若联机页面已经创建并加载过，Loaded 事件不会再触发，需要直接加入
            If FrmLinkMain IsNot Nothing AndAlso FrmLinkMain.IsLoaded Then
                FrmLinkMain.JoinInternal(Code)
                AutoJoinCode = Nothing
            End If
        End If
    End Sub
    '在进入页面时自动尝试加入房间
    Private Shared AutoJoinCode As String = Nothing

#End Region

#End Region

#Region "加载页面 (Loading | Failed)"

    '由 Waiting 或 Failed 状态切换到 Loading
    Private Sub SwitchToLoading(OldState As LinkStates)
        '清理变量
        Peers = Nothing
        FailCount = 0
        '加载器与进程状态
        LinkLoader.Start(IsForceRestart:=True)
        '页面切换会由 Loader 调用 MyPageRight 来触发
        'UI 更新
        UpdateProgressBar(0)
        LabLoadTitle.Text = If(IsServerSide, "创建房间中", "加入房间中")
        UpdateLoadingPage("正在初始化……", "准备初始化")
    End Sub

    '由 Loading 状态切换到 Failed
    Private Sub SwitchToFailed(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        ProcessStop()
        'UI 更新
        UpdateProgressBar(1)
        '显示错误信息
        LabLoadTitle.Text = FailReason
        Dim Brief As String = LinkLoader.Error.GetDisplay(False)
        Dim InnerEx As Exception = LinkLoader.Error
        If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Do Until InnerEx.InnerException Is Nothing
            InnerEx = InnerEx.InnerException
            If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Loop
        LabLoadDesc.Text = If(Brief.StartsWithF("$"), Brief.TrimStart("$"), LinkLoader.Error.GetDisplay(True))
        Logger.Warn(LinkLoader.Error, LabLoadTitle.Text)
    End Sub

    '===============================
    '  页面 UI
    '===============================

    Private Sub UpdateLoadingPage(Title As String, FailBrief As String)
        If FailReason = FailBrief & "失败" Then Return
        FailReason = FailBrief & "失败"
        Logger.Info($"开始步骤：{Title}")
        RunInUiWait(
        Sub()
            If Not LabLoadDesc.IsLoaded Then Return
            LabLoadDesc.Text = Title
        End Sub)
    End Sub
    Private FailReason As String = "准备初始化"

    '取消加载
    Private Sub BtnLoadCancel_Click() Handles BtnLoadCancel.Click
        ChangeState(LinkStates.Waiting)
    End Sub

    '点击重试
    Private Sub CardLoad_MouseLeftButtonUp() Handles CardLoad.MouseLeftButtonUp
        ChangeState(LinkStates.Loading)
    End Sub

    '进度条
    Private Sub UpdateProgressBar(Optional Value As Double = -1)
        If Value = -1 Then Value = LinkLoader.Progress
        Dim DisplayingProgress As Double = ColumnProgressA.Width.Value
        If Math.Round(Value - DisplayingProgress, 3) = 0 Then Return
        If DisplayingProgress <= Value Then
            Dim NewProgress As Double = If(Value = 1, 1, DisplayingProgress +
                (Value - DisplayingProgress) ^ 2 * 0.5)
            AniStart({
                AaGridLengthWidth(ColumnProgressA, NewProgress - ColumnProgressA.Width.Value, 200),
                AaGridLengthWidth(ColumnProgressB, (1 - NewProgress) - ColumnProgressB.Width.Value, 200)
            }, "Link Progress")
        Else
            ColumnProgressA.Width = New GridLength(Value, GridUnitType.Star)
            ColumnProgressB.Width = New GridLength(1 - Value, GridUnitType.Star)
            AniStop("Link Progress")
        End If
    End Sub
    Private Sub CardLoad_SizeChanged() Handles CardLoad.SizeChanged
        RectProgressClip.Rect = New Rect(0, 0, CardLoad.ActualWidth, 12)
    End Sub

#End Region

#Region "完成页面 (Finished)"

    '由 Loading 状态切换到 Finished
    Private Sub SwitchToFinished(OldState As LinkStates)
        '由于只可能从加载器的完成事件触发，不需要管加载器
        UpdatePanelVisibility() '页面的实际切换会由 Loader 调用 MyPageRight 来触发

        'UI 更新
        If IsServerSide Then
            LabFinishTitle.Text = "已创建房间"
            LabFinishDesc.Text = $"把邀请码发给朋友，让大家加入房间吧！{vbCrLf}邀请码：{GetInviteCode()}"
            BtnFinishExit.Text = "关闭"
            BtnFinishPing.ToolTip = "网络延迟"
            BtnFinishCopy.Visibility = Visibility.Visible
            Copy() '立即复制邀请码
            '房主的游戏已经开着了，不需要再启动一次
            BtnFinishLaunch.Visibility = Visibility.Collapsed
            LabFinishLaunchHint.Visibility = Visibility.Collapsed
            '下边栏
            BtnFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Visibility = Visibility.Visible
            BtnFinishIp.Visibility = Visibility.Collapsed
            LabFinishIp.Visibility = Visibility.Collapsed
            LabFinishPort.Text = ServerPort
        Else
            LabFinishTitle.Text = "已加入房间"
            LabFinishDesc.Text = $"联机房间会出现在多人游戏页面的最下方。{vbCrLf}若没有出现，可以点击下方的地址复制，在{vbLQ}直接连接{vbRQ}中粘贴。"
            BtnFinishExit.Text = "离开"
            BtnFinishPing.ToolTip = "与房主的延迟"
            BtnFinishCopy.Visibility = Visibility.Collapsed
            '一键启动并进入
            BtnFinishLaunch.Visibility = Visibility.Visible
            LabFinishLaunchHint.Visibility = Visibility.Visible
            RefreshLaunchVersionList()
            '下边栏
            BtnFinishPort.Visibility = Visibility.Collapsed
            LabFinishPort.Visibility = Visibility.Collapsed
            LabFinishIp.Text = ClientAddress
            BtnFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Visibility = Visibility.Visible
        End If
        Update() '立即刷新
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '退出
    Private Sub BtnFinishExit_Click() Handles BtnFinishExit.Click
        TryExit(False, False)
    End Sub
    ''' <summary>
    ''' 退出联机。
    ''' 返回是否弹出了警告窗口并且玩家选择了取消。
    ''' </summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then Return False
        If Not Silent Then
            If IsServerSide AndAlso PeopleCount > 1 Then
                If MyMsgBox("你确定要关闭联机房间吗？" & vbCrLf & "所有玩家都需要重新输入邀请码才可加入游戏！", "退出联机", "确定", "取消", IsWarn:=True) = 2 Then Return True
            ElseIf Closing Then
                If MyMsgBox(If(IsServerSide, "你确定要关闭联机房间吗？", "你确定要离开联机房间吗？"), "退出联机", "确定", "取消") = 2 Then Return True
            End If
        End If
        ChangeState(LinkStates.Waiting)
        Return False
    End Function

    '复制邀请码
    Private Sub Copy() Handles BtnFinishCopy.Click
        Dim CodeText As String = $"在 RMCL 启动器中输入邀请码【{GetInviteCode()}】，即可加入联机房间！"
        ClipboardSet(CodeText, False)
        Settings.Set("LinkLastAutoJoinInviteCode", CodeText)
        Hint("已复制邀请码！", HintType.Green)
    End Sub
    Private Function GetInviteCode() As String
        '前两段为网络名与密码，中间为协议版本，末三位保留为节点标识（当前固定为 000）
        Return $"{NetworkName}-{NetworkSecret}-{INVITE_CODE_VERSION.ToString.PadLeft(2, "0"c)}000"
    End Function

    '复制 IP
    Private Sub BtnFinishIp_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishIp.MouseLeftButtonUp
        ClipboardSet(ClientAddress, False)
        Hint("已复制服务器地址！", HintType.Green)
    End Sub

#End Region

#Region "启动游戏并进入"

    ''' <summary>
    ''' 是否正在由代码刷新「游戏版本」下拉框，用于忽略由此产生的事件。
    ''' </summary>
    Private IsRefreshingVersion As Boolean

    ''' <summary>
    ''' 刷新「游戏版本」下拉框，并尽量保留玩家上一次的选择。
    ''' </summary>
    Private Sub RefreshLaunchVersionList()
        If ComboLaunchVersion Is Nothing Then Return
        '版本列表还没加载时先让它开始加载，加载完成后会自动再次刷新
        If McInstanceList Is Nothing OrElse Not McInstanceList.Any Then
            If McInstanceListLoader.State <> LoadState.Loading Then
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            End If
            Return
        End If
        '按版本选择页面中的卡片顺序整理
        Dim Versions As New List(Of McInstance)
        For Each CardType As McInstanceCardType In {McInstanceCardType.Star, McInstanceCardType.API,
                                                    McInstanceCardType.OriginalLike, McInstanceCardType.Rubbish,
                                                    McInstanceCardType.Fool}
            Dim CardList As List(Of McInstance) = Nothing
            If Not McInstanceList.TryGetValue(CardType, CardList) OrElse CardList Is Nothing Then Continue For
            For Each Instance As McInstance In CardList
                If Instance Is Nothing OrElse Instance.State = McInstance.McInstanceState.Error Then Continue For
                If Not Versions.Contains(Instance) Then Versions.Add(Instance)
            Next
        Next
        '重建下拉框
        Dim SavedName As String = Settings.Get(Of String)("LinkLaunchVersion")
        Dim Target As MyComboBoxItem = Nothing
        IsRefreshingVersion = True
        ComboLaunchVersion.Items.Clear()
        For Each Instance As McInstance In Versions
            Dim Item As New MyComboBoxItem With {.Content = Instance.Name, .Tag = Instance}
            ComboLaunchVersion.Items.Add(Item)
            If Instance.Name = SavedName Then Target = Item
        Next
        '没记录过就用启动页当前选择的版本，再不行就用第一个
        If Target Is Nothing AndAlso McInstanceSelected IsNot Nothing Then
            Target = ComboLaunchVersion.Items.OfType(Of MyComboBoxItem)().
                FirstOrDefault(Function(i) DirectCast(i.Tag, McInstance) Is McInstanceSelected)
        End If
        If Target Is Nothing AndAlso ComboLaunchVersion.Items.Count > 0 Then
            Target = TryCast(ComboLaunchVersion.Items(0), MyComboBoxItem)
        End If
        ComboLaunchVersion.SelectedItem = Target
        IsRefreshingVersion = False
        UpdateLaunchHint()
    End Sub

    '记住选择
    Private Sub ComboLaunchVersion_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboLaunchVersion.SelectionChanged
        If IsRefreshingVersion Then Return
        Dim Item = TryCast(ComboLaunchVersion.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then Return
        Settings.Set("LinkLaunchVersion", Item.Content.ToString())
        UpdateLaunchHint()
    End Sub

    ''' <summary>
    ''' 获取当前选择用于启动的游戏版本；没有可用版本时返回 Nothing。
    ''' </summary>
    Private Function GetLaunchInstance() As McInstance
        Dim Item = TryCast(ComboLaunchVersion.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then Return Nothing
        Return TryCast(Item.Tag, McInstance)
    End Function

    ''' <summary>
    ''' 刷新完成页上的启动提示。
    ''' </summary>
    Private Sub UpdateLaunchHint()
        If LabFinishLaunchHint Is Nothing Then Return
        If IsServerSide Then
            LabFinishLaunchHint.Text = ""
            Return
        End If
        Dim Instance As McInstance = GetLaunchInstance()
        If Instance Is Nothing Then
            LabFinishLaunchHint.Text = "还没有可用的 Minecraft 版本，请先在「启动」页面下载或添加一个版本。"
        Else
            LabFinishLaunchHint.Text = $"点「启动并进入」会用 {Instance.Name} 打开游戏，进入后自动连接本房间。"
        End If
    End Sub

    Private Sub BtnFinishLaunch_Click(sender As Object, e As MouseButtonEventArgs)
        LaunchAndJoin()
    End Sub

    ''' <summary>
    ''' 用选定的版本启动游戏，并在启动后直接进入当前联机房间。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Public Sub LaunchAndJoin()
        If LinkState <> LinkStates.Finished OrElse IsServerSide Then Return
        If McLaunchLoader.State = LoadState.Loading Then
            Hint("已经有游戏正在启动了，请稍候！", HintType.Red)
            Return
        End If
        Dim Instance As McInstance = GetLaunchInstance()
        If Instance Is Nothing Then
            Hint("请先在「加入房间」中选择一个要启动的游戏版本！", HintType.Red)
            Return
        End If
        If ClientPort <= 0 Then
            Hint("还没有拿到联机端口，请重新加入房间！", HintType.Red)
            Return
        End If
        'EasyTier 已经把房间内的服务器映射到了本机的这个端口，直接连它即可
        Dim Target As String = $"127.0.0.1:{ClientPort}"
        Logger.Info($"准备启动 {Instance.Name} 并进入联机房间（{Target}）")
        '切回启动页，让玩家能看到启动进度
        FrmMain.PageChange(FormMain.PageType.Launch)
        '稍等页面切换动画结束再启动，避免两套动画互相覆盖
        Dim Timer As New System.Windows.Threading.DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(120)}
        AddHandler Timer.Tick,
            Sub(Sender2 As Object, E2 As EventArgs)
                Timer.Stop()
                Try
                    If McLaunchStart(New McLaunchOptions With {.Instance = Instance, .ServerIp = Target}) Then
                        Hint($"正在启动 {Instance.Name}，进入游戏后会自动连接联机房间。", HintType.Blue)
                    End If
                Catch ex As Exception
                    Logger.Error(ex, "启动游戏以进入联机房间失败")
                End Try
            End Sub
        Timer.Start()
    End Sub

#End Region

    '===============================
    '  后端
    '===============================

    ''' <summary>
    ''' 当前是房主（服务端）还是加入者（客户端）。
    ''' </summary>
    Private IsServerSide As Boolean
    ''' <summary>
    ''' 房主指定的、需要暴露到虚拟网络中的端口号。
    ''' </summary>
    Private ServerPort As Integer
    ''' <summary>
    ''' 加入者本地映射后的端口号。
    ''' </summary>
    Private ClientPort As Integer
    ''' <summary>
    ''' 联机模块监听传入连接所使用的端口号。
    ''' </summary>
    Private ListenPort As Integer
    ''' <summary>
    ''' RPC 端口号。
    ''' </summary>
    Private RPCPort As Integer
    ''' <summary>
    ''' 在客户端映射后的地址。
    ''' 除非已作为客户端启动 EasyTier，否则一直为 Nothing。
    ''' </summary>
    Private ClientAddress As String = Nothing
    ''' <summary>
    ''' 网络信息。
    ''' </summary>
    Private NetworkName As String, NetworkSecret As String

#Region "加载"

    Private WithEvents LinkLoader As New LoaderCombo(Of Integer)("联机", {
        New LoaderTask(Of Integer, Integer)("检查联机模块", AddressOf InitCheck) With {.ProgressWeight = 3},
        New LoaderTask(Of Integer, List(Of NetFile))("准备下载联机模块", AddressOf InitPrepareDownload) With {.ProgressWeight = 7},
        New LoaderDownload("下载联机模块", New List(Of NetFile)) With {.ProgressWeight = 35},
        New LoaderTask(Of Integer, Integer)("启动联机模块", AddressOf InitLaunch) With {.ProgressWeight = 55}
    })

    '1. 检查联机模块
    Private Sub InitCheck(Task As LoaderTask(Of Integer, Integer))
        UpdateLoadingPage("正在检查联机模块……", "检查联机模块")
        '清理上次异常退出时残留的进程，避免端口被占用
        LinkKillStaleProcesses()
        If IsEasyTierReady Then Return
        Logger.Info($"联机模块尚未安装，准备下载（CPU 架构：{LinkGetArchitecture()}）")
    End Sub

    '2. 获取需要下载的文件
    Private Sub InitPrepareDownload(Task As LoaderTask(Of Integer, List(Of NetFile)))
        Dim RequiredFiles As New List(Of NetFile)
        If Not IsEasyTierReady Then
            UpdateLoadingPage("正在准备下载联机模块……", "准备下载联机模块")
            IO.Directory.CreateDirectory(PathEasyTier)
            RequiredFiles.Add(New NetFile(
                LinkGetDownloadUrls(),
                PathEasyTier & "EasyTier.zip",
                New FileChecker With {.MinSize = 1024 * 1024 * 5}))
        End If
        Task.Output = RequiredFiles
    End Sub

    '3. 启动联机模块
    Private Sub InitLaunch(Task As LoaderTask(Of Integer, Integer))
        '解压文件
        Dim ZipPath As String = PathEasyTier & "EasyTier.zip"
        If IO.File.Exists(ZipPath) Then
            UpdateLoadingPage("正在解压联机模块……", "解压联机模块")
            LinkInstallEasyTier(ZipPath)
        End If
        If Not IsEasyTierReady Then Throw New Exception("联机模块文件不完整，请重试")
        Task.Progress = 0.05
        '获取节点列表
        UpdateLoadingPage("正在获取节点列表……", "获取节点列表")
        Dim RawPeers As List(Of String)
        Dim CustomPeers As String = Settings.Get(Of String)("LinkCustomPeer")
        If String.IsNullOrWhiteSpace(CustomPeers) Then
            RawPeers = GetTargetPeers()
        Else
            If Not IsServerSide Then Hint("房主可能没有使用自定义节点设置，请确认你们的自定义节点设置是否一致！")
            RawPeers = CustomPeers.Split("，,;；".ToCharArray).
                Select(Function(p) p.Trim).Where(Function(p) Not String.IsNullOrEmpty(p)).ToList()
            Logger.Info($"使用自定义节点：{CustomPeers}")
        End If
        If RawPeers Is Nothing OrElse Not RawPeers.Any Then Throw New Exception("没有可用的联机节点，请检查网络连接")
        '若刚刚的节点测试显示全部不可达，先提醒一句，避免用户白等
        If NodeResults IsNot Nothing AndAlso NodeResults.Any AndAlso
           Not NodeResults.Any(Function(r) r.Success) AndAlso NodeResults.Any(Function(r) r.Reliable) Then
            Hint("节点测试显示所有节点都不可达，连接很可能失败", HintType.Red)
        End If
        Task.Progress = 0.12
        '获取空闲端口
        UpdateLoadingPage("正在启动联机模块……", "启动联机模块")
        'EasyTier 的 --listeners 传一个端口，实际会在这一个端口及其后 3 个连续端口上监听
        '（tcp / udp / wg+ws / quic+wss / faketcp），实测 16000 会占 16000-16003。
        '所以这里要多留一段连续空闲端口，否则 ListenPort+1..+3 可能已被别的程序占用，
        '表现就是联机模块一启动就退出。
        Dim FreePorts = FindFreePorts(9, ServerPort)
        ClientPort = FreePorts(0)
        RPCPort = FreePorts(1)
        ListenPort = FreePorts(4) '后 4 个（4..7）留给 EasyTier 的监听端口段
        '启动进程
        ProcessStart(BuildArguments(RawPeers))
        Task.Progress = 0.15
        '等待连接
        Dim TimeoutCounter As Integer = 0
        Do
            Thread.Sleep(500) '为避免还没启动好先跑 CLI 了，先等半秒
            '刷新信息
            CheckCrash()
            RefreshPeerLoader.WaitForExit(IsForceRestart:=True)
            '查找目标节点
            Dim Ping = GetPeerPing()
            If Ping > 0 Then
                Logger.Info($"已与目标建立连接，当前 Ping 为 {Ping:0.0}ms")
                Exit Do '退出循环
            End If
            '更新进度
            Dim LastProgress = Task.Progress
            Dim PeerCount As Integer = If(Peers Is Nothing, -1, Peers.Where(Function(p) p.Ping > 0).Count)
            Select Case PeerCount
                Case -1 'CLI 无返回
                    Task.Progress = (Task.Progress + 0.02).Clamp(0.15, 0.25)
                Case 0 'CLI 有返回，但未连接到任何节点
                    UpdateLoadingPage("正在连接到节点……", "连接节点")
                    Task.Progress = (Task.Progress + 0.02).Clamp(If(IsServerSide, 0.5, 0.3), If(IsServerSide, 0.95, 0.5))
                Case Else '已连接到节点，但未连接到房主
                    UpdateLoadingPage("正在连接到房主……", "连接房主")
                    Task.Progress = (Task.Progress + 0.02).Clamp(Math.Min(0.45 + PeerCount * 0.05, 0.65), 0.95)
            End Select
            '超时判定
            If LastProgress <> Task.Progress Then
                TimeoutCounter = 0
            ElseIf TimeoutCounter < 20 * 2 Then
                TimeoutCounter += 1
            Else '进度停滞超过 20s
                Select Case PeerCount
                    Case -1 'CLI 无返回
                        Panic("无法启动联机模块", "近期日志：" & vbCrLf & LogHistory.Join(vbCrLf))
                    Case 0 'CLI 有返回，但未连接到任何节点
                        Panic("无法连接到节点", BuildNodeFailureDetail())
                    Case Else '已连接到节点，但未连接到房主
                        Panic("无法连接到房主", $"可能的原因：{vbCrLf}- 你或者房主的网络环境不佳{vbCrLf}- 房主已关闭房间{vbCrLf}- 邀请码输错了")
                End Select
            End If
        Loop Until Task.IsCanceled
        If Task.IsCanceled Then Throw New OperationCanceledException
        '等待连接稳定，最多 5s
        If IsServerSide Then Return
        UpdateLoadingPage("连接优化中……", "优化连接")
        Task.Progress = 0.999
        For i = 1 To 50
            Dim Server = GetTargetPeer()
            If Server IsNot Nothing AndAlso Not Server.Relay AndAlso Server.Ping < 100 Then Return '结束
            If Task.IsCanceled Then Throw New OperationCanceledException
            Thread.Sleep(100)
        Next
    End Sub
    ''' <summary>
    ''' 获取需要连接的社区节点列表。
    ''' 优先使用自定义节点，否则使用内置节点并补充社区订阅中的节点。
    ''' EasyTier 官方的公共节点与节点状态接口均已停服，故不再依赖它们。
    ''' </summary>
    Private Function GetTargetPeers() As List(Of String)
        Dim Result As List(Of String) = LinkGetCandidatePeers()
        If Result Is Nothing OrElse Not Result.Any Then Result = LinkDefaultPeers.ToList()
        Logger.Info($"联机节点：{Result.Join("，")}")
        Return Result
    End Function

    ''' <summary>
    ''' 解析「额外端口」设置。
    ''' 整合包里的语音等 Mod 往往再开一个端口（如 Simple Voice Chat 的 24454），
    ''' 这些端口必须在房主放行、加入者做本机同号映射，否则客户端会卡在加入世界或直接断开。
    ''' </summary>
    Private Function GetExtraPorts() As List(Of Integer)
        Dim Result As New List(Of Integer)
        Dim Raw As String = Settings.Get(Of String)("LinkExtraPorts")
        If String.IsNullOrWhiteSpace(Raw) Then Return Result
        For Each Part As String In Raw.Split({","c, "，"c, " "c, ";"c, "；"c, vbTab}, StringSplitOptions.RemoveEmptyEntries)
            Dim Port As Integer = Val(Part.Trim())
            If Port < 1 OrElse Port > 65535 Then Continue For
            If Port = ServerPort Then Continue For '游戏端口本来就在转发，不必重复
            If Result.Contains(Port) Then Continue For
            Result.Add(Port)
        Next
        Return Result
    End Function

    ''' <summary>
    ''' EasyTier 的 machine-id。同一网络内必须唯一，用于区分节点；
    ''' 首次生成后持久保存，避免同一台机器每次启动都被当成不同节点。
    ''' </summary>
    Private MachineIdValue As String = Nothing
    Private Function GetMachineId() As String
        If MachineIdValue IsNot Nothing Then Return MachineIdValue
        Dim Id As String = Settings.Get(Of String)("LinkMachineId")
        If String.IsNullOrWhiteSpace(Id) Then
            Id = Guid.NewGuid().ToString("N")
            Settings.Set("LinkMachineId", Id)
        End If
        MachineIdValue = Id
        Return Id
    End Function

    ''' <summary>
    ''' 构造联机模块的启动参数。
    ''' 传输层参数对齐 PCL2-CE 的 ETController（no-tun + quic/kcp 代理 + smoltcp + zstd），
    ''' 那套组合经实际联机验证可用，不要随意增删。
    ''' </summary>
    Private Function BuildArguments(Peers As List(Of String)) As String
        Dim Args As New StringBuilder()
        Args.Append(" --no-tun") '不创建 TUN 设备，无需管理员权限，也不会影响本机网络
        Args.Append($" --network-name={NetworkName}")
        Args.Append($" --network-secret={NetworkSecret}")
        Args.Append($" --relay-network-whitelist {NetworkName}")
        Args.Append(" --private-mode true")
        '传输层：与 PCL2-CE 一致
        Args.Append(" --enable-quic-proxy")
        Args.Append(" --enable-kcp-proxy")
        Args.Append(" --use-smoltcp")
        Args.Append(" --encryption-algorithm chacha20")
        Args.Append(" --compression zstd")
        Args.Append(" --multi-thread")
        Args.Append($" --machine-id={GetMachineId()}")
        Args.Append($" --listeners {ListenPort}")
        Args.Append($" --rpc-portal 127.0.0.1:{RPCPort}")
        If Settings.Get(Of Integer)("LinkLatencyMode") = 1 Then Args.Append(" --latency-first")
        '主机名用于在房间内区分身份：以 Client 开头即为加入者
        Dim HostName As String = If(IsServerSide, "Server-", "Client-") & RandomInteger(100000, 999999).ToString()
        Args.Append($" --hostname={HostName}")

        Dim Extras As List(Of Integer) = GetExtraPorts()
        If IsServerSide Then
            Args.Append($" -i {LinkHostIP}")
            '房主把游戏端口与额外端口一起放行进虚拟网络。
            'whitelist 是 clap 的多值参数，按「每个端口一组参数」重复传入最稳，不依赖逗号分隔的解析行为
            For Each Port As Integer In (New List(Of Integer) From {ServerPort}).Concat(Extras)
                Args.Append($" --tcp-whitelist {Port}")
                Args.Append($" --udp-whitelist {Port}")
            Next
        Else
            Args.Append(" -d")
            Args.Append(" --tcp-whitelist 0")
            Args.Append(" --udp-whitelist 0")
            '把房主的端口映射到本地的空闲端口
            ClientAddress = $"localhost:{ClientPort}"
            Args.Append($" --port-forward tcp://127.0.0.1:{ClientPort}/{LinkHostIP}:{ServerPort}")
            Args.Append($" --port-forward udp://127.0.0.1:{ClientPort}/{LinkHostIP}:{ServerPort}")
            '额外端口按同号映射：Mod 会用服务端配置的端口去连 127.0.0.1，本地必须监听同一个号才对得上
            Dim UsedPorts As List(Of Integer) = Nothing
            For Each Extra As Integer In Extras
                If UsedPorts Is Nothing Then UsedPorts = GetUsedPorts()
                If UsedPorts.Contains(Extra) OrElse Extra = ClientPort Then
                    Logger.Warn($"额外端口 {Extra} 已被本机占用，跳过转发")
                    Continue For
                End If
                Args.Append($" --port-forward tcp://127.0.0.1:{Extra}/{LinkHostIP}:{Extra}")
                Args.Append($" --port-forward udp://127.0.0.1:{Extra}/{LinkHostIP}:{Extra}")
            Next
        End If
        If Extras.Any Then Logger.Info($"额外转发端口：{Extras.Join("，")}")
        For Each PeerUrl As String In Peers
            Args.Append($" -p={PeerUrl}")
        Next
        Logger.Info($"联机模块启动参数：{Args}")
        Return Args.ToString()
    End Function

#End Region

#Region "节点测试"

    ''' <summary>
    ''' 最近一次节点测试的结果。为 Nothing 表示尚未测试过。
    ''' </summary>
    Private NodeResults As List(Of LinkNodeResult) = Nothing
    Private IsNodeTesting As Boolean = False

    '手动测试
    Private Sub BtnTestNode_Click(sender As Object, e As MouseButtonEventArgs)
        RunNodeTest(Manual:=True)
    End Sub

    '点击顶部胶囊重新测试
    Private Sub CardNodeState_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs)
        RunNodeTest(Manual:=True)
    End Sub

    ''' <summary>
    ''' 在后台测试所有候选节点，完成后刷新界面。
    ''' </summary>
    Private Sub RunNodeTest(Manual As Boolean)
        If IsNodeTesting Then Return
        IsNodeTesting = True
        LabNodeState.Text = "节点测试中…"
        If Manual Then LabNodeSummary.Text = "正在测试……"
        RunInNewThread(
        Sub()
            Dim Results As List(Of LinkNodeResult) = Nothing
            Try
                Results = LinkTestPeers(LinkGetCandidatePeers(), 3000)
            Catch ex As Exception
                Logger.Warn(ex, "测试联机节点失败")
            End Try
            RunInUi(
            Sub()
                IsNodeTesting = False
                NodeResults = Results
                RefreshNodeList(Manual)
            End Sub)
        End Sub, "Link Node Test")
    End Sub

    ''' <summary>
    ''' 把节点测试结果刷新到界面上。
    ''' </summary>
    Private Sub RefreshNodeList(Manual As Boolean)
        StackNodeList.Children.Clear()
        If NodeResults Is Nothing OrElse Not NodeResults.Any Then
            LabNodeState.Text = "节点未知"
            LabNodeSummary.Text = "没有可测试的节点，请检查「高级设置」中的自定义节点。"
            Return
        End If
        For Each Result As LinkNodeResult In NodeResults
            Dim Info As String
            If Result.Success Then
                Info = $"{Result.Protocol.ToUpper()} · 可达 · {Result.Latency} ms"
            ElseIf Not Result.Reliable Then
                Info = "UDP 类节点，无法直接测速"
            Else
                Info = "不可达 · " & Result.Note
            End If
            StackNodeList.Children.Add(New MyListItem With {
                .Title = Result.Url,
                .Info = Info,
                .Type = MyListItem.CheckType.None,
                .MinPaddingRight = 20
            })
        Next
        '结论
        Dim ReachableCount As Integer = NodeResults.Where(Function(r) r.Success).Count
        Dim ReliableList As List(Of LinkNodeResult) = NodeResults.Where(Function(r) r.Reliable).ToList()
        Dim ReliableFailed As Integer = ReliableList.Where(Function(r) Not r.Success).Count
        If ReachableCount > 0 Then
            LabNodeState.Text = $"节点 {ReachableCount}/{NodeResults.Count} 可达"
            LabNodeSummary.Text = $"{ReachableCount}/{NodeResults.Count} 个节点可达，可以尝试创建房间。"
        ElseIf ReliableList.Any AndAlso ReliableFailed = ReliableList.Count Then
            LabNodeState.Text = "节点均不可达"
            LabNodeSummary.Text = "所有节点均不可达。当前网络很可能封锁了联机端口（校园网、公司网络较常见），" &
                "建议改用手机热点等其它网络，或在「高级设置」中填写你自己搭建的节点。"
        Else
            LabNodeState.Text = "节点状态未知"
            LabNodeSummary.Text = "没有探测到可达的 TCP 节点。UDP 类节点无法直接测速，实际连接情况以创建房间的结果为准。"
        End If
        If Manual Then
            Logger.Info($"节点测试完成：{NodeResults.Count} 个节点，{ReachableCount} 个可达")
        End If
    End Sub

    ''' <summary>
    ''' 生成「无法连接到节点」时的详细说明，附带节点测试结果与排查建议。
    ''' </summary>
    Private Function BuildNodeFailureDetail() As String
        Dim Detail As New StringBuilder()
        Detail.AppendLine("联机模块没有连接到任何节点，因此房间无法建立。")
        If NodeResults IsNot Nothing AndAlso NodeResults.Any Then
            Detail.AppendLine()
            Detail.AppendLine("最近的节点测试结果：")
            For Each Result As LinkNodeResult In NodeResults
                Detail.AppendLine($"  {Result.Url} → {If(Result.Success, $"{Result.Latency} ms", If(Result.Reliable, "不可达", "无法测速"))}")
            Next
            If Not NodeResults.Any(Function(r) r.Success) AndAlso
               NodeResults.Any(Function(r) r.Reliable) Then
                Detail.AppendLine()
                Detail.AppendLine("所有节点都不可达，通常是当前网络封锁了这些端口（校园网、公司网络较常见）。")
                Detail.AppendLine("可以尝试：")
                Detail.AppendLine("· 换成手机热点等其它网络")
                Detail.AppendLine("· 在「高级设置 → 自定义节点」填写你自己搭建的 EasyTier 节点")
                Return Detail.ToString()
            End If
        End If
        Detail.AppendLine()
        Detail.AppendLine("请检查网络环境是否良好，或换用自定义节点后重试。")
        Return Detail.ToString()
    End Function

#End Region

#Region "进程与日志"

    ''' <summary>
    ''' EasyTier Core 进程。
    ''' </summary>
    Private Shared ProcessCore As Process
    Private Shared ProcessOutputHandle As AutoResetEvent, ProcessErrorHandle As AutoResetEvent

    ''' <summary>
    ''' 若程序正在运行，则结束程序进程。
    ''' 即使失败也不会抛出异常。
    ''' </summary>
    Public Shared Sub ProcessStop()
        Try
            '关闭未捕获的进程（仅限本启动器联机模块目录下的进程）
            LinkKillStaleProcesses(If(ProcessCore Is Nothing, -1, ProcessCore.Id))
            '关闭由自身启动的进程
            If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then
                Logger.Info($"停止所启动的联机模块，PID：{ProcessCore.Id}")
                ProcessCore.Kill()
                ProcessCore.Close()
                ProcessCore = Nothing
            End If
            '释放资源
            ProcessOutputHandle?.Dispose()
            ProcessErrorHandle?.Dispose()
        Catch ex As Exception
            Logger.Warn(ex, "停止联机模块失败")
        End Try
    End Sub
    ''' <summary>
    ''' 出现意外错误，给出错误信息并结束联机。
    ''' </summary>
    Private Sub Panic(Brief As String, Detail As String)
        '常见原因分析
        If Detail.Contains("failed to listen on ") Then
            Detail = $"监听端口失败。{vbCrLf}请点击重试，如果还是出现此错误，可以重启电脑解决。{vbCrLf}{vbCrLf}{Detail}"
        End If
        '显示信息
        If LinkState = LinkStates.Loading Then
            FailReason = Brief
            LinkLoader.Failed(New Exception(Detail))
        Else
            MyMsgBox(Detail, "联机出错：" & Brief, IsWarn:=True)
            ChangeState(LinkStates.Waiting)
        End If
    End Sub

    ''' <summary>
    ''' 启动进程。
    ''' </summary>
    Private Sub ProcessStart(Arguments As String)
        Dim Info = New ProcessStartInfo With {
            .FileName = PathEasyTierCore,
            .Arguments = Arguments,
            .WorkingDirectory = PathEasyTier,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardError = True,
            .RedirectStandardOutput = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        Logger.Info($"正在启动 EasyTier：{Arguments}")
        ProcessCore = New Process With {.StartInfo = Info}
        Dim LogLineHandler =
        Sub(sender As Object, e As DataReceivedEventArgs, Handle As AutoResetEvent)
            Try
                If e.Data Is Nothing Then
                    Handle.[Set]()
                    Return
                End If
                ProcessLogLine(e.Data)
            Catch unused As ObjectDisposedException
            Catch ex As Exception
                Logger.Warn(ex, "读取 EasyTier 信息失败")
            End Try
        End Sub
        ProcessOutputHandle = New AutoResetEvent(False)
        AddHandler ProcessCore.OutputDataReceived, Sub(sender, e) LogLineHandler(sender, e, ProcessOutputHandle)
        ProcessErrorHandle = New AutoResetEvent(False)
        AddHandler ProcessCore.ErrorDataReceived, Sub(sender, e) LogLineHandler(sender, e, ProcessErrorHandle)
        ProcessCore.Start()
        ProcessCore.BeginOutputReadLine()
        ProcessCore.BeginErrorReadLine()
    End Sub
    Private Sub ProcessLogLine(Line As String)
        '记录日志
        Logger.Info(Line)
        LogHistory.Enqueue(Line)
        If LogHistory.Count >= 10 Then LogHistory.Dequeue()
        '检查日志内容
        If Line.ContainsIgnoreCase("new peer connection added") Then
            Logger.Warn($"已建立连接：{If(Line.RegexSeek("(?<=remote_addr.+?"")[^""}]{3,}"), Line)}")
            Update()
        ElseIf Line.ContainsIgnoreCase("peer connection removed") Then
            Logger.Warn($"已断开连接：{If(Line.RegexSeek("(?<=remote_addr.+?"")[^""}]{3,}"), Line)}")
            Update()
        End If
    End Sub
    Private LogHistory As New Queue(Of String)(10)

#End Region

#Region "CLI 节点信息获取"

    Private Class Peer
        ''' <summary>
        ''' 该节点的类型。
        ''' </summary>
        Public ReadOnly Type As Types
        Public Enum Types
            ''' <summary>
            ''' 客户端。
            ''' </summary>
            Client
            ''' <summary>
            ''' 服务器。
            ''' </summary>
            Server
            ''' <summary>
            ''' 公共节点。
            ''' </summary>
            Misc
            ''' <summary>
            ''' 自己，或者自己的重复（它可能返回多个自己）。
            ''' </summary>
            Self
        End Enum
        ''' <summary>
        ''' 与该节点的延迟 (ms)。
        ''' 若是自己则为 0。
        ''' </summary>
        Public ReadOnly Ping As Double
        ''' <summary>
        ''' 名称。
        ''' </summary>
        Public ReadOnly Name As String
        ''' <summary>
        ''' 是否通过中继连接。
        ''' </summary>
        Public ReadOnly Relay As Boolean
        ''' <summary>
        ''' NAT 类型。
        ''' </summary>
        Public ReadOnly NATType As NATTypes

        ''' <summary>
        ''' 从 CLI 给出的信息分析对应的数据。
        ''' </summary>
        Public Sub New(Info As JObject)
            '类别
            Dim PeerName As String = If(Info("hostname")?.ToString(), "")
            Dim Cost As String = If(Info("cost")?.ToString(), "")
            Dim Ipv4 As String = If(Info("ipv4")?.ToString(), "")
            If Cost.Equals("Local", StringComparison.OrdinalIgnoreCase) Then
                Type = Types.Self
            ElseIf PeerName.StartsWithF("Client") Then
                Type = Types.Client
            ElseIf Ipv4 = LinkHostIP Then '只允许这个 IP 作为房主，避免网络被多个房主搞炸的情况
                Type = Types.Server
            Else
                Type = Types.Misc
            End If
            '基础信息
            Double.TryParse(If(Info("lat_ms")?.ToString(), ""), NumberStyles.Any, CultureInfo.InvariantCulture, Ping)
            Name = PeerName
            Relay = Cost.ContainsIgnoreCase("relay")
            Try
                NATType = EnumUtils.FromString(Of NATTypes)(Info("nat_type")?.ToString())
            Catch ex As Exception
                NATType = NATTypes.Unknown
            End Try
        End Sub
        Public Overrides Function ToString() As String
            Return $"{Type} - {Name} - Ping {Ping:0.0}ms [中继? {Relay}] - NAT {NATType}"
        End Function
    End Class
    Private Enum NATTypes
        'https://github.com/EasyTier/EasyTier/blob/6bb2fd9a15ab2499bdeabdcc3a925e9bd9aebf50/easytier/src/proto/common.proto#L129
        Unknown = 0
        OpenInternet = 1
        NoPAT = 2
        FullCone = 3
        Restricted = 4
        PortRestricted = 5
        Symmetric = 6
        SymUdpFirewall = 7
        SymmetricEasyInc = 8
        SymmetricEasyDec = 9
        ''' <summary>
        ''' 尚未获取。
        ''' </summary>
        Pending = 10
    End Enum

    ''' <summary>
    ''' 当前的节点列表，使用 RefreshPeerLoader 来刷新。
    ''' 若尚未成功获取过则为 Nothing，但保证在加载完成后至少是一个列表。
    ''' </summary>
    Private Peers As List(Of Peer) = Nothing
    ''' <summary>
    ''' 自己的 NAT 类型。
    ''' </summary>
    Private NATType As NATTypes = NATTypes.Pending

    ''' <summary>
    ''' 调用 EasyTier CLI 获取已连接节点信息。
    ''' 这会过滤掉尚未完成连接的其他节点以及自己。
    ''' 若在加载完成后连续获取失败 3 次，则会强制断开连接。
    ''' </summary>
    Private RefreshPeerLoader As New LoaderTask(Of Integer, List(Of Peer))("EasyTier CLI", AddressOf RefreshPeer)
    Private Sub RefreshPeer()
        Try
            Dim CliResult As String = GetCliPeers()
            '解析
            If Not CliResult.Contains("lat_ms") Then Throw New Exception("CLI 调用失败：" & vbCrLf & CliResult)
            If GetUuid() Mod If(ModeDebug, 23, 103) = 0 Then Logger.Info($"CLI 输出抽样：{vbCrLf}{CliResult}")
            Dim NewPeers As New List(Of Peer)
            For Each Line As JObject In CType(CliResult.DeserializeJson(), JArray)
                Try
                    Dim Peer = New Peer(Line)
                    If Peer.Type = Peer.Types.Self Then
                        NATType = Peer.NATType '记录自己的 NAT
                    Else
                        NewPeers.Add(Peer)
                    End If
                Catch exx As Exception
                    Logger.Warn(exx, $"错误的信息（{Line}）")
                End Try
            Next
            '完成
            Peers = NewPeers
            FailCount = 0
        Catch ex As Exception
            Logger.Warn(ex, "获取节点信息失败")
            If LinkState = LinkStates.Finished Then
                FailCount += 1
                If FailCount >= 4 Then Panic("获取节点信息失败", ex.Message)
            End If
        End Try
    End Sub
    ''' <summary>
    ''' 调用联机模块的 CLI 获取房间内的节点信息，返回其原始 JSON 文本。
    ''' </summary>
    Private Function GetCliPeers() As String
        Dim Result As String = ""
        Using Proc As New Process()
            Proc.StartInfo = New ProcessStartInfo With {
                .FileName = PathEasyTierCli,
                .Arguments = $"--rpc-portal 127.0.0.1:{RPCPort} -o json peer",
                .WorkingDirectory = PathEasyTier,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            Proc.Start()
            If Not Proc.WaitForExit(3000) Then
                Try
                    Proc.Kill()
                Catch ex As Exception
                End Try
                Throw New Exception("联机模块信息获取超时")
            End If
            Result = Proc.StandardOutput.ReadToEnd() & Proc.StandardError.ReadToEnd()
        End Using
        Return Result.Trim()
    End Function
    ''' <summary>
    ''' CLI 调用连续失败的次数。
    ''' </summary>
    Private FailCount As Integer = 0

    ''' <summary>
    ''' 根据当前的节点列表获取 Ping 值。
    ''' 服务端会返回所有节点中最低的 Ping，客户端会返回服务端的 Ping。
    ''' 0 代表尚不可用。
    ''' </summary>
    Private Function GetPeerPing() As Double
        Dim Peer = GetTargetPeer()
        If Peer Is Nothing Then Return 0
        Return Peer.Ping
    End Function
    ''' <summary>
    ''' 服务端会返回所有节点中 Ping 大于 0 最低的那一个，客户端会返回服务端。
    ''' 若没有则为 Nothing。
    ''' </summary>
    Private Function GetTargetPeer() As Peer
        If Peers Is Nothing Then Return Nothing
        Dim Targets As IEnumerable(Of Peer)
        If IsServerSide Then
            Targets = Peers.Where(Function(p) p.Ping > 0)
        Else
            Targets = Peers.Where(Function(p) p.Type = Peer.Types.Server AndAlso p.Ping > 0)
        End If
        If Not Targets.Any Then Return Nothing
        '返回 Ping 且最低的那个
        Dim MinPing = Targets.Min(Function(p) p.Ping)
        Return Targets.First(Function(p) p.Ping = MinPing)
    End Function

    ''' <summary>
    ''' 获取当前房间中的人数，包括自己。
    ''' 0 代表不可用。
    ''' </summary>
    Private ReadOnly Property PeopleCount As Integer
        Get
            If Peers Is Nothing Then Return 0
            Return Peers.Where(Function(p) p.Type <> Peer.Types.Misc).Count() + 1 '加上自己
        End Get
    End Property

#End Region

#Region "定时任务"

    '启动
    Private Sub StartTimerThread() Handles Me.Loaded
        Static IsTimerStarted As Boolean = False
        If IsTimerStarted Then Return
        RunInNewThread(
        Sub()
            Dim Counter As Integer = 0
            Do While True
                Try
                    Thread.Sleep(200)
                    Counter += 200
                    '每秒执行一次
                    If Counter >= 1000 Then
                        Counter = 0
                        Update()
                    End If
                    '每 200ms 更新进度条
                    If LinkState = LinkStates.Loading Then RunInUi(AddressOf UpdateProgressBar)
                Catch ex As Exception
                    Logger.Error(ex, "联机模块主时钟出错")
                    Thread.Sleep(10000)
                End Try
            Loop
        End Sub, "Link Timer")
    End Sub

    '每秒或进入页面时触发
    Private Sub Update() Handles Me.Loaded
        If LinkState <> LinkStates.Finished Then Return
        '重新获取信息
        SyncLock RefreshPeerLoader.LockState
            If RefreshPeerLoader.State <> LoadState.Loading Then RefreshPeerLoader.Start(IsForceRestart:=True)
        End SyncLock
        '更新 UI
        If FrmMain.PageCurrent = FormMain.PageType.Setup Then
            RunInUi(
            Sub()
                'Logo 旋转动画
                AniStart(AaRotateTransform(ImgFinishLogo, 500, 5000), "Link Logo Rotation")
                'Ping
                Dim Ping As Double = GetPeerPing()
                Dim Connecting As Boolean = Ping Mod 500 = 0 OrElse $"{Ping:0.0}" = "1.0" OrElse FailCount > 0
                If Connecting Then
                    LabFinishPing.Text = "连接优化中"
                Else
                    LabFinishPing.Text = If(Ping >= 10, $"{Ping:0} ms", $"{Ping:0.0} ms")
                End If
                '人数显示
                LabFinishPlayer.Text = PeopleCount & "  人"
                '------------------------
                '  提示条
                '------------------------
                HintFinish.Visibility = Visibility.Collapsed
                If IsServerSide Then Return '服务端没有需要显示的提示
                If Connecting Then Return '连接稳定过程中不显示提示
                '中继提示
                Dim Server As Peer = GetTargetPeer()
                If Server Is Nothing Then Return
                If Server.Relay Then
                    If NATType >= NATTypes.Symmetric AndAlso Server.NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "你和房主的网络环境都不太好，"
                    ElseIf NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "你的网络环境不太好，"
                    ElseIf Server.NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "房主的网络环境不太好，"
                    Else
                        HintFinish.Text = "你或者房主的网络环境不太好，"
                    End If
                    If String.IsNullOrWhiteSpace(Settings.Get(Of String)("LinkCustomPeer")) Then
                        HintFinish.Text &= "正使用社区节点进行中继。"
                    Else
                        HintFinish.Text &= "正通过自定义节点进行中继。"
                    End If
                    HintFinish.Visibility = Visibility.Visible
                    HintFinish.Theme = MyHint.Themes.Yellow
                    Return
                End If
                '环境比房主更好的提示
                If NATType >= NATTypes.PortRestricted Then Return '自身的 NAT 为 2 或更好
                If Server.NATType < NATTypes.PortRestricted Then Return '房主的 NAT 为 3 或更差
                Dim OtherPlayers = Peers.Where(Function(p) p.Type = Peer.Types.Client).ToList()
                If Not OtherPlayers.Any() Then Return '房间里还有其他玩家
                If OtherPlayers.Any(Function(p) p.Relay) Then Return '自己可以与所有玩家打洞
                If OtherPlayers.All(Function(p) p.NATType < NATTypes.PortRestricted) Then Return '任意其他玩家的 NAT 为 3 或更差
                HintFinish.Visibility = Visibility.Visible
                HintFinish.Theme = MyHint.Themes.Blue
                HintFinish.Text = "你的网络环境比房主更好！如果你来当房主，其他玩家或许能更加流畅！"
            End Sub)
        End If
        '检查核心状态
        CheckCrash()
        '检查节点状态
        If Peers Is Nothing Then Return '尚未成功获取到节点信息，由 FailCount 负责处理
        If Not Peers.Any Then
            Panic("网络连接已断开", "请检查你的网络环境是否正常。")
            Return
        End If
        If Not IsServerSide AndAlso Not Peers.Any(Function(p) p.Type = Peer.Types.Server) Then
            MyMsgBox("房主已离开房间！", "联机结束")
            ChangeState(LinkStates.Waiting)
            Return
        End If
        '广播联机房间端口
        If Not IsServerSide Then
            Try
                Static BroadcastSocket As New Socket(SocketType.Dgram, ProtocolType.Udp)
                BroadcastSocket.SendTo(
                    Encoding.UTF8.GetBytes($"[MOTD]RMCL 联机房间[/MOTD][AD]{ClientPort}[/AD]"),
                    SocketFlags.None,
                    New IPEndPoint(IPAddress.Loopback, 4445))
            Catch ex As Exception
                Logger.Warn(ex, "广播联机房间端口失败")
            End Try
        End If
    End Sub
    ''' <summary>
    ''' 检查联机模块是否崩溃。
    ''' </summary>
    Private Sub CheckCrash()
        If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then Return
        Panic("联机模块已崩溃", "近期日志：" & vbCrLf & LogHistory.Join(vbCrLf))
    End Sub

#End Region

#Region "设置页支持"

    '作为设置页的子页面加载时刷新设置项
    Private Sub PageLinkMain_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Reload()
        '版本列表是异步加载的，加载完成后要回来刷新「游戏版本」下拉框
        If Not IsInstanceListHooked Then
            IsInstanceListHooked = True
            AddHandler McInstanceListLoader.LoadingStateChanged,
                Sub()
                    If McInstanceListLoader.State = LoadState.Finished Then RunInUi(AddressOf RefreshLaunchVersionList)
                End Sub
        End If
        RefreshLaunchVersionList()
        '首次进入时自动做一次节点测试与局域网世界扫描（只在后台跑，不阻塞界面）
        Static HasInitialized As Boolean = False
        If HasInitialized Then Return
        HasInitialized = True
        If LinkState = LinkStates.Waiting Then
            RunNodeTest(Manual:=False)
            RefreshWorldList(Manual:=False)
        End If
    End Sub

    ''' <summary>
    ''' 是否已经监听了版本列表的加载事件。
    ''' </summary>
    Private IsInstanceListHooked As Boolean

    ''' <summary>
    ''' 从设置中刷新界面上的选项。
    ''' </summary>
    Public Sub Reload()
        Try
            SettingService.RefreshSettings(Me)
        Catch ex As Exception
            Logger.Warn(ex, "刷新联机设置失败")
        End Try
    End Sub

    ''' <summary>
    ''' 初始化联机页的所有设置。
    ''' </summary>
    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Logger.Info("已初始化联机页设置")
            Hint("已初始化联机页设置！", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化联机页设置失败", LogBehavior.Alert)
        End Try
        Reload()
    End Sub

#End Region

End Class
