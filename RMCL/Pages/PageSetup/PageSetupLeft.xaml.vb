Public Class PageSetupLeft

    Private IsLoad As Boolean = False
    Private IsPageSwitched As Boolean = False '如果在 Loaded 前切换到其他页面，会导致触发 Loaded 时再次切换一次
    Private Sub PageSetupLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '是否处于隐藏的子页面
        Dim IsHiddenPage As Boolean = IsPageHidden(PageID)
        If PageSetupUI.HiddenForceShow Then IsHiddenPage = False
        '若页面错误，或尚未加载，则继续
        If IsLoad AndAlso Not IsHiddenPage Then Return
        IsLoad = True
        '刷新子页面隐藏情况
        PageSetupUI.HiddenRefresh()
        '选择第一个未被隐藏的子页面
        If IsPageSwitched Then Return
        CheckPage(FirstAvailablePage())
    End Sub
    Private Sub PageOtherLeft_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        IsPageSwitched = False
    End Sub

    ''' <summary>
    ''' 该设置子页面是否被「功能隐藏」关掉了。
    ''' </summary>
    Friend Shared Function IsPageHidden(ID As FormMain.PageSubType) As Boolean
        Select Case ID
            Case FormMain.PageSubType.SetupLaunch
                Return Settings.Get(Of Boolean)("UiHiddenSetupLaunch")
            Case FormMain.PageSubType.SetupLink
                Return Settings.Get(Of Boolean)("UiHiddenSetupLink")
            Case FormMain.PageSubType.SetupFrp
                Return Settings.Get(Of Boolean)("UiHiddenSetupFrp")
            Case FormMain.PageSubType.SetupJava
                Return Settings.Get(Of Boolean)("UiHiddenSetupJava")
            Case FormMain.PageSubType.SetupUI
                Return Settings.Get(Of Boolean)("UiHiddenSetupUi")
            Case FormMain.PageSubType.SetupFeature
                Return Settings.Get(Of Boolean)("UiHiddenSetupFeature")
            Case FormMain.PageSubType.SetupUpdate
                Return Settings.Get(Of Boolean)("UiHiddenSetupUpdate")
            Case FormMain.PageSubType.SetupSystem
                Return Settings.Get(Of Boolean)("UiHiddenSetupSystem")
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>
    ''' 按侧栏顺序找出第一个没被隐藏的子页面。全被隐藏时回落到「启动」，避免设置页进去一片空白。
    ''' </summary>
    Friend Shared Function FirstAvailablePage() As FormMain.PageSubType
        For Each ID As FormMain.PageSubType In {
                FormMain.PageSubType.SetupLaunch,
                FormMain.PageSubType.SetupLink,
                FormMain.PageSubType.SetupFrp,
                FormMain.PageSubType.SetupJava,
                FormMain.PageSubType.SetupUI,
                FormMain.PageSubType.SetupFeature,
                FormMain.PageSubType.SetupUpdate,
                FormMain.PageSubType.SetupSystem}
            If Not IsPageHidden(ID) Then Return ID
        Next
        Return FormMain.PageSubType.SetupLaunch
    End Function

    ''' <summary>
    ''' 勾选侧栏中与指定页面对应的那一项。
    ''' </summary>
    Private Sub CheckPage(ID As FormMain.PageSubType)
        Select Case ID
            Case FormMain.PageSubType.SetupLink
                ItemLink.SetChecked(True, False, False)
            Case FormMain.PageSubType.SetupFrp
                ItemFrp.SetChecked(True, False, False)
            Case FormMain.PageSubType.SetupJava
                ItemJava.SetChecked(True, False, False)
            Case FormMain.PageSubType.SetupUI
                ItemUI.SetChecked(True, False, False)
            Case FormMain.PageSubType.SetupFeature
                ItemFeature.SetChecked(True, False, False)
            Case FormMain.PageSubType.SetupUpdate
                ItemUpdate.SetChecked(True, False, False)
            Case FormMain.PageSubType.SetupSystem
                ItemSystem.SetChecked(True, False, False)
            Case Else
                ItemLaunch.SetChecked(True, False, False)
        End Select
    End Sub

#Region "页面切换"

    ''' <summary>
    ''' 当前页面的编号。从左往右从 0 开始计算。
    ''' </summary>
    Public PageID As FormMain.PageSubType
    Public Sub New()
        InitializeComponent()
        '选择第一个未被隐藏的子页面
        PageID = FirstAvailablePage()
    End Sub

    ''' <summary>
    ''' 勾选事件改变页面。
    ''' </summary>
    Private Sub PageCheck(sender As MyListItem, e As EventArgs) Handles ItemLaunch.Check, ItemSystem.Check, ItemUI.Check, ItemLink.Check, ItemFeature.Check, ItemUpdate.Check, ItemFrp.Check, ItemJava.Check
        '尚未初始化控件属性时，sender.Tag 为 Nothing，会跳过切换，且由于 PageID 默认为 0 而切换到第一个页面
        '若使用 IsLoaded，则会导致模拟点击不被执行（模拟点击切换页面时，控件的 IsLoaded 为 False）
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    ''' <summary>
    ''' 获取当前导航指定的右页面。
    ''' </summary>
    Public Function PageGet(Optional ID As FormMain.PageSubType = -1)
        If ID = -1 Then ID = PageID
        Select Case ID
            Case FormMain.PageSubType.SetupLaunch
                If FrmSetupLaunch Is Nothing Then FrmSetupLaunch = New PageSetupLaunch
                Return FrmSetupLaunch
            Case FormMain.PageSubType.SetupUI
                If FrmSetupUI Is Nothing Then FrmSetupUI = New PageSetupUI
                Return FrmSetupUI
            Case FormMain.PageSubType.SetupLink
                If FrmLinkMain Is Nothing Then FrmLinkMain = New PageLinkMain
                Return FrmLinkMain
            Case FormMain.PageSubType.SetupSystem
                If FrmSetupSystem Is Nothing Then FrmSetupSystem = New PageSetupSystem
                Return FrmSetupSystem
            Case FormMain.PageSubType.SetupFeature
                If FrmSetupFeature Is Nothing Then FrmSetupFeature = New PageSetupFeature
                Return FrmSetupFeature
            Case FormMain.PageSubType.SetupUpdate
                If FrmSetupUpdate Is Nothing Then FrmSetupUpdate = New PageSetupUpdate
                Return FrmSetupUpdate
            Case FormMain.PageSubType.SetupFrp
                If FrmSetupFrp Is Nothing Then FrmSetupFrp = New PageSetupFrp
                Return FrmSetupFrp
            Case FormMain.PageSubType.SetupJava
                If FrmSetupJava Is Nothing Then FrmSetupJava = New PageSetupJava
                Return FrmSetupJava
            Case Else
                Throw New Exception("未知的设置子页面种类：" & ID)
        End Select
    End Function

    ''' <summary>
    ''' 切换现有页面。
    ''' </summary>
    Public Sub PageChange(ID As FormMain.PageSubType)
        If PageID = ID Then
            '同一项再次点击：顺手修复可能因切换动画被中断而停在空白状态的右侧页面
            FrmMain.PageRightEnsureVisible()
            Return
        End If
        AniControlEnabled += 1
        IsPageSwitched = True
        Try

            Select Case ID
                Case FormMain.PageSubType.SetupLaunch
                    If IsNothing(FrmSetupLaunch) Then FrmSetupLaunch = New PageSetupLaunch
                    PageChangeRun(FrmSetupLaunch)
                Case FormMain.PageSubType.SetupUI
                    If IsNothing(FrmSetupUI) Then FrmSetupUI = New PageSetupUI
                    PageChangeRun(FrmSetupUI)
                Case FormMain.PageSubType.SetupLink
                    If IsNothing(FrmLinkMain) Then FrmLinkMain = New PageLinkMain
                    PageChangeRun(FrmLinkMain)
                Case FormMain.PageSubType.SetupSystem
                    If IsNothing(FrmSetupSystem) Then FrmSetupSystem = New PageSetupSystem
                    PageChangeRun(FrmSetupSystem)
                Case FormMain.PageSubType.SetupFeature
                    If IsNothing(FrmSetupFeature) Then FrmSetupFeature = New PageSetupFeature
                    PageChangeRun(FrmSetupFeature)
                Case FormMain.PageSubType.SetupUpdate
                    If IsNothing(FrmSetupUpdate) Then FrmSetupUpdate = New PageSetupUpdate
                    PageChangeRun(FrmSetupUpdate)
                Case FormMain.PageSubType.SetupFrp
                    If IsNothing(FrmSetupFrp) Then FrmSetupFrp = New PageSetupFrp
                    PageChangeRun(FrmSetupFrp)
                Case FormMain.PageSubType.SetupJava
                    If IsNothing(FrmSetupJava) Then FrmSetupJava = New PageSetupJava
                    PageChangeRun(FrmSetupJava)
                Case Else
                    Throw New Exception("未知的设置子页面种类：" & ID)
            End Select

            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换分页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub
    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight") '停止主页面的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        Dim Old As MyPageRight = TryCast(FrmMain.PanMainRight.Child, MyPageRight)
        If Old IsNot Nothing Then Old.PageOnExit()
        AniStart({
            AaCode(
            Sub()
                FrmMain.PageRightAttach(FrmMain.PageRight)
            End Sub, 130),
            AaCode(
            Sub()
                '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                FrmMain.PageRightEnter(FrmMain.PageRight)
            End Sub, 30, True)
        }, "PageLeft PageChange")
        FrmMain.PageChangeFallback(FrmMain.PageRight)
    End Sub

#End Region

    Public Sub Reset(sender As Object, e As EventArgs)
        Select Case Val(sender.Tag)
            Case FormMain.PageSubType.SetupLaunch
                If MyMsgBox("是否要初始化 启动 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupLaunch) Then FrmSetupLaunch = New PageSetupLaunch
                    FrmSetupLaunch.Reset()
                    ItemLaunch.Checked = True
                End If
            Case FormMain.PageSubType.SetupUI
                If MyMsgBox("是否要初始化 个性化 页面的所有设置？该操作不可撤销。" & vbCrLf & "（背景图片与音乐、主页等外部文件不会被删除）", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupUI) Then FrmSetupUI = New PageSetupUI
                    FrmSetupUI.Reset()
                    ItemUI.Checked = True
                End If
            Case FormMain.PageSubType.SetupSystem
                If MyMsgBox("是否要初始化 其他 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupSystem) Then FrmSetupSystem = New PageSetupSystem
                    FrmSetupSystem.Reset()
                    ItemSystem.Checked = True
                End If
            Case FormMain.PageSubType.SetupLink
                If MyMsgBox("是否要初始化 联机 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmLinkMain) Then FrmLinkMain = New PageLinkMain
                    FrmLinkMain.Reset()
                    ItemLink.Checked = True
                End If
            Case FormMain.PageSubType.SetupFeature
                If MyMsgBox("是否要初始化 功能 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupFeature) Then FrmSetupFeature = New PageSetupFeature
                    FrmSetupFeature.Reset()
                    ItemFeature.Checked = True
                End If
            Case FormMain.PageSubType.SetupUpdate
                If MyMsgBox("是否要初始化 更新 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupUpdate) Then FrmSetupUpdate = New PageSetupUpdate
                    FrmSetupUpdate.Reset()
                    ItemUpdate.Checked = True
                End If
        End Select
    End Sub

End Class
