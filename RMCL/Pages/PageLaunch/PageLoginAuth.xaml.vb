Imports Newtonsoft.Json.Linq

Public Class PageLoginAuth
    Private IsFirstLoad As Boolean = True
    ''' <summary>
    ''' 正在刷新界面时忽略控件的变更事件，避免程序化赋值被当成用户输入。
    ''' </summary>
    Private IsReloading As Boolean = False
    ''' <summary>
    ''' 服务器下拉框当前显示的顺序，与 ComboServer 的索引一一对应。
    ''' </summary>
    Private ServerEntries As New List(Of JObject)
    ''' <summary>
    ''' 上一次填充输入时使用的认证服务器，用于判断是否需要重置账号密码。
    ''' </summary>
    Private LastServer As String = Nothing

    ''' <summary>
    ''' 刷新页面显示的所有信息。
    ''' </summary>
    Public Sub Reload(KeepInput As Boolean)
        IsReloading = True
        Try
            '记住密码
            CheckRemember.Checked = Settings.Get(Of Boolean)("LoginRemember")
            RefreshServerList()
            RefreshAccountList(KeepInput)
            ReloadRegisterButton()
        Finally
            IsReloading = False
        End Try
        IsFirstLoad = False
    End Sub

    ''' <summary>
    ''' 刷新服务器下拉框，并定位到当前使用的认证服务器。
    ''' </summary>
    Private Sub RefreshServerList()
        ServerEntries = AuthServerGetAll()
        Dim Items As New List(Of AuthServerItem)
        For Each Entry As JObject In ServerEntries
            Dim Name As String = AuthServerValue(Entry, "name")
            Dim Host As String = AuthServerHost(AuthServerValue(Entry, "server"))
            Items.Add(New AuthServerItem With {
                .Server = AuthServerValue(Entry, "server"),
                .Display = If(Name = "" OrElse Name = Host, Host, Name & " · " & Host)})
        Next
        ComboServer.ItemsSource = If(Items.Count = 0, Nothing, Items)
        ComboServer.SelectedIndex = -1
        Dim Current As String = AuthServerCurrent()
        For i = 0 To ServerEntries.Count - 1
            If AuthServerNormalize(AuthServerValue(ServerEntries(i), "server")) = Current Then
                ComboServer.SelectedIndex = i
                Exit For
            End If
        Next
    End Sub
    ''' <summary>
    ''' 删除下拉框里指定的认证服务器（会先弹确认窗口）。
    ''' </summary>
    Private Sub BtnServerDelete_Click(sender As Object, e As EventArgs)
        Dim Server As String = If(sender.Tag, "").ToString()
        If Server = "" Then Return
        Dim AccountCount As Integer = AuthServerGetAccounts(Server).Count
        ComboServer.IsDropDownOpen = False
        If MyMsgBox($"确定要删除认证服务器 {AuthServerGuessName(Server)} 吗？" & vbCrLf &
                    $"地址：{Server}" & vbCrLf & vbCrLf &
                    If(AccountCount > 0, $"它下面保存的 {AccountCount} 个账号与密码会一并删除。" & vbCrLf, "") &
                    "删除后无法恢复，需要时可以重新拖入授权链接添加。",
                    "删除服务器", "删除", "取消") = 2 Then Return
        If Not AuthServerRemoveServer(Server) Then
            Hint("删除认证服务器失败，请查看日志了解详情！", HintType.Red)
            Return
        End If
        '刷新服务器列表、账号列表，以及主页的登录方式与头像
        Reload(False)
        If FrmLaunchLeft IsNot Nothing Then FrmLaunchLeft.RefreshPage(True, False)
    End Sub

    ''' <summary>
    ''' 当前服务器下已保存的账号，顺序与账号下拉框一致。
    ''' </summary>
    Private Function GetAccountList() As List(Of JObject)
        Return AuthServerGetAccounts(AuthServerCurrent()).Where(Function(Account) AuthServerValue(Account, "user") <> "").ToList()
    End Function

    ''' <summary>
    ''' 刷新账号下拉框。账号与密码都取自当前服务器自己的条目，因此不同服务器之间不会串号。
    ''' </summary>
    Private Sub RefreshAccountList(KeepInput As Boolean)
        '认证服务器变了就必须重置输入：两个服务器可能有同一个邮箱、不同密码，保留旧输入会拿错密码去登录
        Dim Server As String = AuthServerCurrent()
        If Not String.Equals(LastServer, Server, StringComparison.OrdinalIgnoreCase) Then
            If LastServer IsNot Nothing Then Logger.Info($"认证服务器已切换，重置账号输入：{LastServer} → {Server}")
            LastServer = Server
            KeepInput = False
            '即使没开「记住密码」也要清空，否则会把上一个服务器的密码提交出去
            TextPass.Password = ""
        End If
        Dim Accounts As List(Of JObject) = GetAccountList()
        Dim Users As String() = Accounts.Select(Function(A) AuthServerValue(A, "user")).ToArray()
        If KeepInput AndAlso Not IsFirstLoad Then
            '保留输入，只刷新下拉框列表
            Dim Input As String = ComboName.Text
            ComboName.ItemsSource = If(Users.Length = 0, Nothing, Users)
            ComboName.Text = Input
        Else
            ComboName.ItemsSource = If(Users.Length = 0, Nothing, Users)
            If Users.Length = 0 Then
                ComboName.Text = ""
                TextPass.Password = ""
            Else
                ComboName.Text = Users(0)
                If Settings.Get(Of Boolean)("LoginRemember") Then TextPass.Password = AuthServerValue(Accounts(0), "pass")
            End If
        End If
    End Sub

    ''' <summary>
    ''' 获取当前页面的登录信息。
    ''' </summary>
    Public Shared Function GetLoginData() As McLoginServer
        Dim Server As String = AuthServerCurrent() & "/authserver"
        If FrmLoginAuth Is Nothing Then
            Return New McLoginServer(McLoginType.Auth) With {.Token = "Auth", .BaseUrl = Server, .UserName = "", .Password = "", .Description = "Authlib-Injector", .Type = McLoginType.Auth}
        Else
            Return New McLoginServer(McLoginType.Auth) With {.Token = "Auth", .BaseUrl = Server, .UserName = FrmLoginAuth.ComboName.Text.Replace("¨", "").Trim, .Password = FrmLoginAuth.TextPass.Password.Replace("¨", "").Trim, .Description = "Authlib-Injector", .Type = McLoginType.Auth}
        End If
    End Function
    ''' <summary>
    ''' 当前页面的登录信息是否有效。
    ''' </summary>
    Public Shared Function IsVaild(LoginData As McLoginServer) As String
        If AuthServerCurrent() = "" Then Return "请先选择或添加一个第三方认证服务器！"
        If LoginData.UserName = "" Then Return "账号不能为空！"
        If LoginData.Password = "" Then Return "密码不能为空！"
        Return ""
    End Function
    Public Function IsVaild() As String
        Return IsVaild(GetLoginData())
    End Function

    '保存输入信息
    Private Sub ComboName_TextChanged(sender As Object, e As TextChangedEventArgs) Handles ComboName.TextChanged
        If IsReloading Then Return
        If sender.Text = "" Then TextPass.Password = ""
        If AniControlEnabled = 0 Then Settings.Set("CacheAuthAccess", "")  '迫使其不进行 Validate
    End Sub
    Private Sub TextPass_PasswordChanged(sender As Object, e As RoutedEventArgs) Handles TextPass.PasswordChanged
        If IsReloading Then Return
        If AniControlEnabled = 0 Then Settings.Set("CacheAuthAccess", "")
    End Sub
    Private Sub ComboName_SelectionChanged(sender As MyComboBox, e As SelectionChangedEventArgs) Handles ComboName.SelectionChanged
        If IsReloading Then Return
        If sender.SelectedIndex = -1 OrElse Not Settings.Get(Of Boolean)("LoginRemember") Then
            TextPass.Password = ""
            Return
        End If
        Dim Accounts As List(Of JObject) = GetAccountList()
        If sender.SelectedIndex >= Accounts.Count Then Return
        Dim SelectedUser As String = AuthServerValue(Accounts(sender.SelectedIndex), "user")
        '换账号时把该账号自己的会话与密码换上来，避免拿上一个账号的 token 去启动
        AuthServerSetAccount(AuthServerCurrent(), SelectedUser)
        TextPass.Password = AuthServerValue(Accounts(sender.SelectedIndex), "pass")
    End Sub
    ''' <summary>
    ''' 切换当前使用的第三方认证服务器。
    ''' </summary>
    Private Sub ComboServer_SelectionChanged(sender As MyComboBox, e As SelectionChangedEventArgs) Handles ComboServer.SelectionChanged
        If IsReloading Then Return
        If sender.SelectedIndex < 0 OrElse sender.SelectedIndex >= ServerEntries.Count Then Return
        Dim Target As String = AuthServerValue(ServerEntries(sender.SelectedIndex), "server")
        If AuthServerNormalize(Target) = AuthServerCurrent() Then Return
        If Not AuthServerSetCurrent(Target, McInstanceSelected) Then Return
        '把该服务器自己的账号与密码换上来，不会带到别的服务器
        IsReloading = True
        Try
            RefreshAccountList(False)
        Finally
            IsReloading = False
        End Try
        If FrmLaunchLeft IsNot Nothing Then FrmLaunchLeft.RefreshPage(True, False)
        PageLaunchLeft.SkinAuth.Start()
    End Sub
    Private Sub CheckBoxChange(sender As MyCheckBox, e As Object) Handles CheckRemember.Change
        If AniControlEnabled = 0 Then Settings.Set(sender.Tag, sender.Checked)
    End Sub

    '链接处理
    Private Sub ComboName_TextChanged() Handles ComboName.TextChanged
        BtnLink.Content = If(ComboName.Text = "", "注册账号", "找回密码")
    End Sub
    Private Sub Btn_Click(sender As Object, e As EventArgs) Handles BtnLink.Click
        Dim Register As String = Settings.Get(Of String)("CacheAuthServerRegister")
        If BtnLink.Content = "注册账号" Then
            If Register <> "" Then OpenWebsite(Register)
        Else
            If Register <> "" Then OpenWebsite(Register.Replace("/auth/register", "/auth/forgot"))
        End If
    End Sub
    '切换注册按钮可见性
    Private Sub ReloadRegisterButton() Handles Me.Loaded
        Dim Address As String = Settings.Get(Of String)("CacheAuthServerRegister")
        BtnLink.Visibility = If(String.IsNullOrEmpty(New ValidateHttp().Validate(Address)), Visibility.Visible, Visibility.Collapsed)
    End Sub

End Class

''' <summary>
''' 认证服务器下拉框的条目：Server 用于删除，Display 用于显示。
''' </summary>
Public Class AuthServerItem
    Public Property Server As String
    Public Property Display As String
    Public Overrides Function ToString() As String
        Return Display
    End Function
End Class
