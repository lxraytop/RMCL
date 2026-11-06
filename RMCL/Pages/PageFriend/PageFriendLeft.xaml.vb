Public Class PageFriendLeft

    ''' <summary>
    ''' 当前正在显示的子页面。
    ''' </summary>
    Public PageID As FormMain.PageSubType = FormMain.PageSubType.FriendChat

    Private IsSwitching As Boolean = False

    Public Sub New()
        InitializeComponent()
        '首次进入时直接落在正确的页面上，避免先显示聊天再弹回账号
        PageID = If(ModFriend.IsLoggedIn, FormMain.PageSubType.FriendChat, FormMain.PageSubType.FriendAccount)
    End Sub

    Private Sub PageFriendLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        RefreshAuthState()
    End Sub

    ''' <summary>
    ''' 未登录时只保留「账号」（登录/注册），登录后恢复全部入口。
    ''' </summary>
    Public Sub RefreshAuthState(Optional AutoSwitchToChat As Boolean = False)
        Dim LoggedIn As Boolean = ModFriend.IsLoggedIn
        ItemChat.Visibility = If(LoggedIn, Visibility.Visible, Visibility.Collapsed)
        ItemContact.Visibility = If(LoggedIn, Visibility.Visible, Visibility.Collapsed)
        ItemGroup.Visibility = If(LoggedIn, Visibility.Visible, Visibility.Collapsed)
        ItemHistory.Visibility = If(LoggedIn, Visibility.Visible, Visibility.Collapsed)
        ItemAccount.Visibility = Visibility.Visible

        If Not LoggedIn Then
            If PageID <> FormMain.PageSubType.FriendAccount Then
                ItemAccount.SetChecked(True, False, False)
                PageChange(FormMain.PageSubType.FriendAccount)
            End If
        ElseIf AutoSwitchToChat AndAlso PageID = FormMain.PageSubType.FriendAccount Then
            '刚刚登录成功：自动进入消息页
            ItemChat.SetChecked(True, False, False)
            PageChange(FormMain.PageSubType.FriendChat)
        End If
    End Sub

    Private Sub PageCheck(sender As MyListItem, e As EventArgs) Handles ItemChat.Check, ItemContact.Check, ItemGroup.Check, ItemHistory.Check, ItemAccount.Check
        If sender.Tag Is Nothing OrElse IsSwitching Then Return
        If Not ModFriend.IsLoggedIn AndAlso Val(sender.Tag) <> FormMain.PageSubType.FriendAccount Then
            ItemAccount.SetChecked(True, False, False)
            Return
        End If
        PageChange(Val(sender.Tag))
    End Sub

    ''' <summary>
    ''' 获取指定编号对应的右页面。
    ''' </summary>
    Public Function PageGet(Optional ID As FormMain.PageSubType = -1) As MyPageRight
        If ID = -1 Then ID = PageID
        '未登录时无论请求哪个子页面，都先显示账号页
        If Not ModFriend.IsLoggedIn Then ID = FormMain.PageSubType.FriendAccount
        Select Case ID
            Case FormMain.PageSubType.FriendContact
                If FrmFriendContact Is Nothing Then FrmFriendContact = New PageFriendContact
                Return FrmFriendContact
            Case FormMain.PageSubType.FriendGroup
                If FrmFriendGroup Is Nothing Then FrmFriendGroup = New PageFriendGroup
                Return FrmFriendGroup
            Case FormMain.PageSubType.FriendHistory
                If FrmFriendHistory Is Nothing Then FrmFriendHistory = New PageFriendHistory
                Return FrmFriendHistory
            Case FormMain.PageSubType.FriendAccount
                If FrmFriendAccount Is Nothing Then FrmFriendAccount = New PageFriendAccount
                Return FrmFriendAccount
            Case Else
                If FrmFriendChat Is Nothing Then FrmFriendChat = New PageFriendChat
                Return FrmFriendChat
        End Select
    End Function

    ''' <summary>
    ''' 切换到指定的好友子页面。
    ''' </summary>
    Public Sub PageChange(ID As FormMain.PageSubType)
        If PageID = ID Then
            FrmMain.PageRightEnsureVisible()
            Return
        End If
        IsSwitching = True
        AniControlEnabled += 1
        Try
            Dim Target As MyPageRight = PageGet(ID)
            PageChangeRun(Target)
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换好友子页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
            IsSwitching = False
        End Try
    End Sub

    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight")
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        Dim Old As MyPageRight = TryCast(FrmMain.PanMainRight.Child, MyPageRight)
        If Old IsNot Nothing Then Old.PageOnExit()
        AniStart({
            AaCode(Sub() FrmMain.PageRightAttach(FrmMain.PageRight), 130),
            AaCode(Sub() FrmMain.PageRightEnter(FrmMain.PageRight), 30, True)
        }, "PageLeft PageChange")
        FrmMain.PageChangeFallback(FrmMain.PageRight)
    End Sub

    ''' <summary>
    ''' 打开与某个好友或群组的聊天。
    ''' </summary>
    Public Sub OpenChat(ConvType As Integer, ConvKey As String, Title As String)
        If Not ModFriend.IsLoggedIn Then
            ItemAccount.SetChecked(True, False, False)
            PageChange(FormMain.PageSubType.FriendAccount)
            Return
        End If
        If FrmFriendChat Is Nothing Then FrmFriendChat = New PageFriendChat
        FrmFriendChat.PendingOpen = New Tuple(Of Integer, String, String)(ConvType, ConvKey, Title)
        ItemChat.SetChecked(True, False, False)
        If PageID = FormMain.PageSubType.FriendChat Then
            FrmFriendChat.ApplyPendingOpen()
        Else
            PageChange(FormMain.PageSubType.FriendChat)
            RunInUi(Sub() If FrmFriendChat IsNot Nothing Then FrmFriendChat.ApplyPendingOpen())
        End If
    End Sub

End Class
