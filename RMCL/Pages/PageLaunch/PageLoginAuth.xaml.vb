Public Class PageLoginAuth
    Private IsFirstLoad As Boolean = True
    ''' <summary>
    ''' 刷新页面显示的所有信息。
    ''' </summary>
    Public Sub Reload(KeepInput As Boolean)
        '记住密码
        CheckRemember.Checked = Settings.Get(Of Boolean)("LoginRemember")
        RefreshServerList()
        If KeepInput AndAlso Not IsFirstLoad Then '避免第一次就以 KeepInput 的方式加载，导致文本框里没东西
            '保留输入，只刷新下拉框列表
            Dim Input As String = ComboName.Text
            ComboName.ItemsSource = If(Settings.Get(Of String)("LoginAuthEmail") = "", Nothing, Settings.Get(Of String)("LoginAuthEmail").Split("¨"))
            ComboName.Text = Input
        Else
            '不保留输入，刷新列表后自动选择第一项
            If Settings.Get(Of String)("LoginAuthEmail") = "" Then
                ComboName.ItemsSource = Nothing
            Else
                ComboName.ItemsSource = Settings.Get(Of String)("LoginAuthEmail").Split("¨")
                ComboName.Text = Settings.Get(Of String)("LoginAuthEmail").BeforeFirst("¨")
                If Settings.Get(Of Boolean)("LoginRemember") Then TextPass.Password = Settings.Get(Of String)("LoginAuthPass").BeforeFirst("¨").Trim
            End If
        End If
        IsFirstLoad = False
    End Sub
    ''' <summary>
    ''' 获取当前页面的登录信息。
    ''' </summary>
    Public Shared Function GetLoginData() As McLoginServer
        '用统一的规范化取址，避免拿到空地址后拼出 "/authserver" 这种相对 URI
        Dim Base As String = AuthServerBase()
        Dim Server As String = If(Base = "", "", Base & "/authserver")
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
        If String.IsNullOrEmpty(LoginData.BaseUrl) Then Return "还没有设置第三方认证服务器，请先在「服务器」里选一个，或点「+ 添加服务器……」。"
        If LoginData.UserName = "" Then Return "账号不能为空！"
        If LoginData.Password = "" Then Return "密码不能为空！"
        Return ""
    End Function
    Public Function IsVaild() As String
        Return IsVaild(GetLoginData())
    End Function

    '保存输入信息
    Private Sub ComboName_TextChanged(sender As Object, e As TextChangedEventArgs) Handles ComboName.TextChanged
        If sender.Text = "" Then TextPass.Password = ""
        If AniControlEnabled = 0 Then Settings.Set("CacheAuthAccess", "")  '迫使其不进行 Validate
    End Sub
    Private Sub TextPass_PasswordChanged(sender As Object, e As RoutedEventArgs) Handles TextPass.PasswordChanged
        If AniControlEnabled = 0 Then Settings.Set("CacheAuthAccess", "")
    End Sub
    Private Sub ComboName_SelectionChanged(sender As MyComboBox, e As SelectionChangedEventArgs) Handles ComboName.SelectionChanged
        If sender.SelectedIndex = -1 OrElse Not Settings.Get(Of Boolean)("LoginRemember") Then
            TextPass.Password = ""
        Else
            TextPass.Password = Settings.Get(Of String)("LoginAuthPass").ToString.Split("¨")(sender.SelectedIndex).Trim
        End If
    End Sub
    Private Sub CheckBoxChange(sender As MyCheckBox, e As Object) Handles CheckRemember.Change
        If AniControlEnabled = 0 Then Settings.Set(sender.Tag, sender.Checked)
    End Sub

    '链接处理
    Private Sub ComboName_TextChanged() Handles ComboName.TextChanged
        BtnLink.Content = If(ComboName.Text = "", "注册账号", "找回密码")
    End Sub
    Private Sub Btn_Click(sender As Object, e As EventArgs) Handles BtnLink.Click
        If BtnLink.Content = "注册账号" Then
            OpenWebsite(If(McInstanceSelected IsNot Nothing, Settings.Get(Of String)("VersionServerAuthRegister", Instance:=McInstanceSelected), Settings.Get(Of String)("CacheAuthServerRegister")))
        Else
            Dim Website As String = If(McInstanceSelected IsNot Nothing, Settings.Get(Of String)("VersionServerAuthRegister", Instance:=McInstanceSelected), Settings.Get(Of String)("CacheAuthServerRegister"))
            OpenWebsite(Website.Replace("/auth/register", "/auth/forgot"))
        End If
    End Sub
    '切换注册按钮可见性
    Private Sub ReloadRegisterButton() Handles Me.Loaded
        Dim Address As String = If(McInstanceSelected IsNot Nothing, Settings.Get(Of String)("VersionServerAuthRegister", Instance:=McInstanceSelected), Settings.Get(Of String)("CacheAuthServerRegister"))
        BtnLink.Visibility = If(String.IsNullOrEmpty(New ValidateHttp().Validate(Address)), Visibility.Visible, Visibility.Collapsed)
    End Sub

#Region "第三方服务器切换"

    ''' <summary>
    ''' 是否正在由代码填充服务器下拉框（用于忽略由此触发的事件）。
    ''' </summary>
    Private IsRefreshingServer As Boolean

    ''' <summary>
    ''' 刷新「服务器」下拉框：列出添加过的第三方认证服务器，末尾是「+ 添加服务器……」。
    ''' </summary>
    Private Sub RefreshServerList()
        If ComboServer Is Nothing Then Return
        Dim Servers As List(Of AuthServerEntry) = AuthServerList()
        Dim Active As String = ActiveAuthServerUrl()
        IsRefreshingServer = True
        Try
            ComboServer.Items.Clear()
            Dim Target As MyComboBoxItem = Nothing
            For Each Server As AuthServerEntry In Servers
                Dim Item As New MyComboBoxItem With {.Content = Server.Name, .Tag = Server.Url}
                ComboServer.Items.Add(Item)
                If String.Equals(Server.Url, Active, StringComparison.OrdinalIgnoreCase) Then Target = Item
            Next
            ComboServer.Items.Add(New MyComboBoxItem With {.Content = "+ 添加服务器……", .Tag = Nothing})
            '当前地址不在列表里（比如刚通过拖拽/版本设置写入）时，也让它显示出来，避免下拉框显示成别的服务器
            If Target Is Nothing AndAlso Not String.IsNullOrEmpty(Active) Then
                Dim Extra As New MyComboBoxItem With {.Content = $"{AuthServerHost(Active)}（当前）", .Tag = Active}
                ComboServer.Items.Insert(0, Extra)
                Target = Extra
            End If
            If Target Is Nothing AndAlso ComboServer.Items.Count > 0 Then Target = TryCast(ComboServer.Items(0), MyComboBoxItem)
            ComboServer.SelectedItem = Target
        Finally
            IsRefreshingServer = False
        End Try
    End Sub

    Private Sub ComboServer_SelectionChanged(sender As MyComboBox, e As SelectionChangedEventArgs) Handles ComboServer.SelectionChanged
        If IsRefreshingServer Then Return
        Dim Item As MyComboBoxItem = TryCast(ComboServer.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then Return
        Dim Url As String = TryCast(Item.Tag, String)
        If String.IsNullOrEmpty(Url) Then
            '选的是「+ 添加服务器……」
            If Not PageLaunchLeft.PromptAddAuthServer() Then
                RefreshServerList()
                Return
            End If
            Settings.Set("CacheAuthAccess", "")
            RefreshServerList()
            Reload(False)
            Return
        End If
        '切换到已有的服务器：清掉缓存的登录凭据，强制用新服务器的账号重新登录
        SetActiveAuthServer(Url, Item.Content.ToString())
        Settings.Set("CacheAuthAccess", "")
        Hint($"已切换到 {Item.Content}", HintType.Green)
        Reload(False)
    End Sub

#End Region

End Class
