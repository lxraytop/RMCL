Imports System.Windows.Threading

Public Class PageFriendAccount

    Private IsBusy As Boolean = False
    Private CodeTimer As DispatcherTimer
    Private CodeCountdown As Integer = 0

    ''' <summary>
    ''' 当前是否显示登录/注册界面（供左侧栏判断登录成功后要不要切走）。
    ''' </summary>
    Public ReadOnly Property IsLoginFormVisible As Boolean
        Get
            Return PanAuth.Visibility = Visibility.Visible
        End Get
    End Property

    Private Sub PageFriendAccount_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        RefreshLoginForm()
        RefreshUI()
        RefreshRayskinAccount()
        RefreshSelfAsync()
    End Sub

    ''' <summary>
    ''' 登录状态变化后刷新整个页面。
    ''' </summary>
    Public Sub RefreshUI()
        Dim LoggedIn As Boolean = ModFriend.IsLoggedIn
        PanAuth.Visibility = If(LoggedIn, Visibility.Collapsed, Visibility.Visible)
        PanSelf.Visibility = If(LoggedIn, Visibility.Visible, Visibility.Collapsed)
        If Not LoggedIn Then Return

        Dim Self As JObject = ModFriend.FriendSelf
        If Self Is Nothing Then Return
        LabSelfName.Text = Self.Value(Of String)("nickname")
        LabSelfEmail.Text = Self.Value(Of String)("email")
        TextMyInvite.Text = Self.Value(Of String)("invite_code")
        Dim Used As Integer = Self.Value(Of Integer)("invite_used")
        Dim Max As Integer = Self.Value(Of Integer)("invite_max")
        If Max > 0 Then
            LabInviteUse.Text = $"已使用 {Used} / {Max}"
        ElseIf String.IsNullOrEmpty(TextMyInvite.Text) Then
            LabInviteUse.Text = "暂无邀请码"
        ElseIf Used > 0 Then
            LabInviteUse.Text = $"已使用 {Used}（不限次数）"
        Else
            LabInviteUse.Text = "不限使用次数"
        End If
    End Sub

    Private Sub PageFriendAccount_PageEnter() Handles Me.PageEnter
        RefreshSelfAsync()
        RefreshLocalInfo()
    End Sub

    ''' <summary>
    ''' 后台刷新账号信息，让邀请码用量等数据保持最新。
    ''' </summary>
    Private Sub RefreshSelfAsync()
        If Not ModFriend.IsLoggedIn Then Return
        RunInNewThread(
        Sub()
            Try
                ModFriend.RefreshSelf()
                RunInUi(Sub() RefreshUI())
            Catch ex As Exception
                Logger.Warn(ex, "刷新好友账号信息失败")
            End Try
        End Sub, "Friend Refresh Self")
    End Sub

    Private Sub RefreshLoginForm()
        PanLogin.Visibility = Visibility.Visible
        PanRegister.Visibility = Visibility.Collapsed
        PanRayskin.Visibility = Visibility.Collapsed
        BtnTabLogin.ColorType = MyButton.ColorState.Highlight
        BtnTabRegister.ColorType = MyButton.ColorState.Normal
        BtnTabRayskin.ColorType = MyButton.ColorState.Normal
    End Sub

    Private Sub SwitchForm(Target As String)
        PanLogin.Visibility = If(Target = "login", Visibility.Visible, Visibility.Collapsed)
        PanRegister.Visibility = If(Target = "register", Visibility.Visible, Visibility.Collapsed)
        PanRayskin.Visibility = If(Target = "rayskin", Visibility.Visible, Visibility.Collapsed)
        BtnTabLogin.ColorType = If(Target = "login", MyButton.ColorState.Highlight, MyButton.ColorState.Normal)
        BtnTabRegister.ColorType = If(Target = "register", MyButton.ColorState.Highlight, MyButton.ColorState.Normal)
        BtnTabRayskin.ColorType = If(Target = "rayskin", MyButton.ColorState.Highlight, MyButton.ColorState.Normal)
        LabAuthStatus.Text = ""
        If Target = "rayskin" Then RefreshRayskinAccount()
    End Sub

    Private Sub BtnTabLogin_Click(sender As Object, e As EventArgs) Handles BtnTabLogin.Click
        SwitchForm("login")
    End Sub

    Private Sub BtnTabRegister_Click(sender As Object, e As EventArgs) Handles BtnTabRegister.Click
        SwitchForm("register")
    End Sub

    Private Sub BtnTabRayskin_Click(sender As Object, e As EventArgs) Handles BtnTabRayskin.Click
        SwitchForm("rayskin")
    End Sub

    Private Sub BtnOpenRegister_Click(sender As Object, e As EventArgs) Handles BtnOpenRegister.Click
        SwitchForm("register")
    End Sub

    Private Sub BtnOpenLogin_Click(sender As Object, e As EventArgs) Handles BtnOpenLogin.Click
        SwitchForm("login")
    End Sub

#Region "状态与错误"

    Private Sub SetBusy(Value As Boolean)
        IsBusy = Value
        BtnLogin.IsEnabled = Not Value
        BtnRegister.IsEnabled = Not Value
        BtnSendCode.IsEnabled = Not Value AndAlso CodeCountdown = 0
        BtnRayskinLogin.IsEnabled = Not Value
        BtnRestore.IsEnabled = Not Value
        BtnRestoreAll.IsEnabled = Not Value
    End Sub

    Private Sub Status(Text As String, Optional IsError As Boolean = False)
        RunInUi(
        Sub()
            LabAuthStatus.Text = Text
            If IsError Then
                LabAuthStatus.Foreground = New SolidColorBrush(Color.FromRgb(196, 62, 62))
            Else
                LabAuthStatus.Foreground = TryCast(FindResource("ColorBrush3"), Brush)
            End If
        End Sub)
    End Sub

    Private Sub ShowError(ex As Exception)
        Logger.Warn(ex, "好友账号操作失败")
        Status(ModFriend.FriendErrorText(ex), True)
    End Sub

#End Region

#Region "注册与登录"

    Private Sub BtnSendCode_Click(sender As Object, e As EventArgs) Handles BtnSendCode.Click
        Dim Email As String = TextRegEmail.Text.Trim()
        If Not Email.Contains("@") Then
            Status("请先填写正确的邮箱", True)
            Return
        End If
        SetBusy(True)
        Status("正在发送验证码……")
        RunInNewThread(
        Sub()
            Try
                Dim Param As New JObject From {{"email", Email}, {"purpose", "register"}}
                Dim Data As JObject = ModFriend.FriendApi("send_code", Param, False)
                Dim DevCode As String = If(Data Is Nothing, "", Data.Value(Of String)("dev_code"))
                RunInUi(
                Sub()
                    If Not String.IsNullOrEmpty(DevCode) Then
                        TextRegCode.Text = DevCode
                        Status("验证码已发送（开发模式已自动填入）。")
                    Else
                        Status("验证码已发送，请检查邮箱（含垃圾邮件箱）。")
                    End If
                    StartCodeCountdown()
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() ShowError(ex))
            Finally
                RunInUi(Sub() SetBusy(False))
            End Try
        End Sub, "Friend Send Code")
    End Sub

    Private Sub StartCodeCountdown()
        CodeCountdown = 60
        If CodeTimer Is Nothing Then
            CodeTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
            AddHandler CodeTimer.Tick,
            Sub()
                CodeCountdown -= 1
                If CodeCountdown <= 0 Then
                    CodeTimer.Stop()
                    CodeCountdown = 0
                    BtnSendCode.Text = "发送验证码"
                    BtnSendCode.IsEnabled = Not IsBusy
                Else
                    BtnSendCode.Text = $"重新发送（{CodeCountdown}s）"
                    BtnSendCode.IsEnabled = False
                End If
            End Sub
        End If
        CodeTimer.Start()
    End Sub

    Private Sub BtnRegister_Click(sender As Object, e As EventArgs) Handles BtnRegister.Click
        Dim Email As String = TextRegEmail.Text.Trim()
        Dim Code As String = TextRegCode.Text.Trim()
        Dim Name As String = TextRegName.Text.Trim()
        Dim Pass As String = TextRegPass.Password
        Dim Invite As String = TextRegInvite.Text.Trim()
        If Not Email.Contains("@") Then Status("邮箱格式不正确", True) : Return
        If Code.Length < 4 Then Status("请填写邮箱验证码", True) : Return
        If Name.Length < 2 OrElse Name.Length > 16 Then Status("昵称需要 2-16 个字符", True) : Return
        If Pass.Length < 6 Then Status("密码至少 6 位", True) : Return
        If Invite.Length < 4 Then Status("请填写邀请码", True) : Return

        SetBusy(True)
        Status("正在注册……")
        RunInNewThread(
        Sub()
            Try
                Dim Param As New JObject From {
                    {"email", Email}, {"code", Code}, {"nickname", Name},
                    {"password", Pass}, {"invite", Invite},
                    {"device", Settings.Get(Of String)("Identify")}
                }
                Dim Data As JObject = ModFriend.FriendApi("register", Param, False, 20000)
                ModFriend.FriendLoginSucceed(Data.Value(Of String)("token"), CType(Data("user"), JObject))
                RunInUi(
                Sub()
                    Status("注册成功，已自动登录。")
                    RefreshUI()
                    FrmFriendLeft.RefreshAuthState(True)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() ShowError(ex))
            Finally
                RunInUi(Sub() SetBusy(False))
            End Try
        End Sub, "Friend Register")
    End Sub

    Private Sub BtnLogin_Click(sender As Object, e As EventArgs) Handles BtnLogin.Click
        Dim Email As String = TextLoginEmail.Text.Trim()
        Dim Pass As String = TextLoginPass.Password
        If Not Email.Contains("@") Then Status("邮箱格式不正确", True) : Return
        If Pass.Length < 6 Then Status("密码至少 6 位", True) : Return

        SetBusy(True)
        Status("正在登录……")
        RunInNewThread(
        Sub()
            Try
                Dim Param As New JObject From {
                    {"email", Email}, {"password", Pass},
                    {"device", Settings.Get(Of String)("Identify")}
                }
                Dim Data As JObject = ModFriend.FriendApi("login", Param, False, 20000)
                ModFriend.FriendLoginSucceed(Data.Value(Of String)("token"), CType(Data("user"), JObject))
                RunInUi(
                Sub()
                    Status("登录成功。")
                    RefreshUI()
                    FrmFriendLeft.RefreshAuthState(True)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() ShowError(ex))
            Finally
                RunInUi(Sub() SetBusy(False))
            End Try
        End Sub, "Friend Login")
    End Sub

#End Region

#Region "RaySkin 一键登录"

    Private RayskinEmail As String = ""
    Private RayskinPass As String = ""
    Private RayskinServer As String = ""
    Private RayskinAccess As String = ""
    Private RayskinUuid As String = ""
    Private RayskinName As String = ""

    Private Sub BtnRayskinRefresh_Click(sender As Object, e As EventArgs) Handles BtnRayskinRefresh.Click
        RefreshRayskinAccount()
    End Sub

    ''' <summary>
    ''' 读取启动页最近使用的 RaySkin 账号（只读设置项，不上传密码）。
    ''' </summary>
    Private Sub RefreshRayskinAccount()
        Try
            Dim Server As String = ModAuthServer.AuthServerCurrent()
            If String.IsNullOrEmpty(Server) Then Server = ModAuthServer.DefaultAuthServer
            Dim Accounts As List(Of JObject) = ModAuthServer.AuthServerGetAccounts(Server)
            Dim Account As JObject = Accounts.FirstOrDefault()
            If Account Is Nothing Then
                RayskinEmail = "" : RayskinPass = "" : RayskinAccess = ""
                LabRayskinAccount.Text = $"没有找到 {ModAuthServer.AuthServerGuessName(Server)} 的登录记录。请先在「启动」页用 RaySkin 登录一次，再回来一键登录。"
                BtnRayskinLogin.IsEnabled = False
                Return
            End If
            RayskinEmail = Account.Value(Of String)("user")
            RayskinPass = Account.Value(Of String)("pass")
            RayskinAccess = Account.Value(Of String)("access")
            RayskinUuid = Account.Value(Of String)("uuid")
            RayskinName = Account.Value(Of String)("name")
            RayskinServer = Server
            Dim DisplayName As String = Account.Value(Of String)("name")
            LabRayskinAccount.Text = $"服务器：{ModAuthServer.AuthServerGuessName(Server)}" & vbCrLf &
                                     $"邮箱：{RayskinEmail}" & vbCrLf &
                                     $"角色：{If(String.IsNullOrEmpty(DisplayName), "未选择", DisplayName)}"
            BtnRayskinLogin.IsEnabled = Not String.IsNullOrEmpty(RayskinEmail)
        Catch ex As Exception
            Logger.Warn(ex, "读取 RaySkin 账号失败")
            LabRayskinAccount.Text = "读取 RaySkin 账号失败：" & ex.Message
            BtnRayskinLogin.IsEnabled = False
        End Try
    End Sub

    ''' <summary>
    ''' 用 RaySkin 的 Yggdrasil 认证证明账号归属：authenticate → join → 服务端 hasJoined。
    ''' 密码只在客户端与 RaySkin 之间传递，不会发给好友服务端。
    ''' </summary>
    Private Sub BtnRayskinLogin_Click(sender As Object, e As EventArgs) Handles BtnRayskinLogin.Click
        If String.IsNullOrEmpty(RayskinEmail) OrElse String.IsNullOrEmpty(RayskinPass) Then
            Status("没有可用的 RaySkin 登录记录", True)
            Return
        End If
        SetBusy(True)
        Status("正在向 RaySkin 验证账号……")
        Dim Email As String = RayskinEmail
        Dim Pass As String = RayskinPass
        Dim Server As String = RayskinServer
        Dim CachedAccess As String = RayskinAccess
        Dim CachedUuid As String = RayskinUuid
        Dim CachedName As String = RayskinName
        RunInNewThread(
        Sub()
            Try
                Dim Access As String = CachedAccess
                Dim Uuid As String = CachedUuid
                Dim PlayerName As String = CachedName
                Dim ServerId As String = Guid.NewGuid().ToString("N")
                Dim Joined As Boolean = False

                '1. 优先复用启动页缓存的 accessToken：既快，也不会把启动页的会话顶掉
                If Not String.IsNullOrEmpty(Access) AndAlso Not String.IsNullOrEmpty(Uuid) Then
                    Try
                        RayskinJoin(Server, Access, Uuid, ServerId)
                        Joined = True
                        Logger.Info("RaySkin 一键登录：复用本机缓存令牌")
                    Catch ex As HttpRequestCodeException
                        Logger.Info($"RaySkin 缓存令牌已失效（HTTP {CInt(ex.StatusCode)}），改用账号密码重新认证")
                    End Try
                End If

                '2. 缓存不可用：用保存的密码重新认证；多角色时再用 refresh 指定角色
                If Not Joined Then
                    If String.IsNullOrEmpty(Pass) Then
                        Throw New FriendApiException("E_RAYSKIN", "本机 RaySkin 会话已失效，且没有保存密码。请先到「启动」页登录一次。")
                    End If
                    Dim ClientToken As String = Guid.NewGuid().ToString("N")
                    Dim AuthBody As New JObject From {
                        {"agent", New JObject From {{"name", "Minecraft"}, {"version", 1}}},
                        {"username", Email}, {"password", Pass},
                        {"clientToken", ClientToken}, {"requestUser", True}
                    }
                    Dim AuthRaw As String = NetRequestByClient(Server & "/authserver/authenticate", HttpMethod.Post,
                        AuthBody.ToString(Formatting.None), "application/json", "application/json", 20000, Nothing, RequireJson:=True)
                    Dim Auth As JObject = JObject.Parse(AuthRaw)
                    Access = Auth.Value(Of String)("accessToken")
                    If String.IsNullOrEmpty(Access) Then
                        Throw New FriendApiException("E_RAYSKIN", "RaySkin 没有返回 accessToken，请先到「启动」页重新登录")
                    End If
                    Dim Profile As JObject = TryCast(Auth("selectedProfile"), JObject)
                    If Profile Is Nothing Then
                        '多角色账号在 authenticate 时不会自动选角色，需要再用 refresh 指定
                        Dim Chosen As JObject = ChooseRayskinProfile(TryCast(Auth("availableProfiles"), JArray), CachedUuid, CachedName)
                        If Chosen Is Nothing Then
                            Throw New FriendApiException("E_RAYSKIN", "这个 RaySkin 账号下没有可用角色，请先在皮肤站创建角色")
                        End If
                        Dim RefreshBody As New JObject From {
                            {"accessToken", Access}, {"clientToken", ClientToken},
                            {"selectedProfile", New JObject From {
                                {"id", Chosen.Value(Of String)("id")},
                                {"name", Chosen.Value(Of String)("name")}
                            }},
                            {"requestUser", True}
                        }
                        Dim RefreshRaw As String = NetRequestByClient(Server & "/authserver/refresh", HttpMethod.Post,
                            RefreshBody.ToString(Formatting.None), "application/json", "application/json", 20000, Nothing, RequireJson:=True)
                        Dim Refreshed As JObject = JObject.Parse(RefreshRaw)
                        Dim NewAccess As String = Refreshed.Value(Of String)("accessToken")
                        Dim NewProfile As JObject = TryCast(Refreshed("selectedProfile"), JObject)
                        If String.IsNullOrEmpty(NewAccess) OrElse NewProfile Is Nothing Then
                            Throw New FriendApiException("E_RAYSKIN", "RaySkin 未能切换到角色「" & Chosen.Value(Of String)("name") & "」")
                        End If
                        Access = NewAccess
                        Profile = NewProfile
                    End If
                    Uuid = Profile.Value(Of String)("id")
                    PlayerName = Profile.Value(Of String)("name")
                    RayskinJoin(Server, Access, Uuid, ServerId)
                End If

                '3. hasJoined 需要角色名，缓存里没有就补查一次
                If String.IsNullOrEmpty(PlayerName) Then
                    PlayerName = RayskinProfileName(Server, Uuid)
                    If String.IsNullOrEmpty(PlayerName) Then PlayerName = CachedName
                End If

                '4. 由好友服务端调用 hasJoined 完成最终校验
                Dim Param As New JObject From {
                    {"email", Email}, {"uuid", Uuid}, {"name", PlayerName},
                    {"server_id", ServerId}, {"server", Server}
                }
                Dim Data As JObject = ModFriend.FriendApi("rayskin_login", Param, False, 25000)
                ModFriend.FriendLoginSucceed(Data.Value(Of String)("token"), CType(Data("user"), JObject))
                RunInUi(
                Sub()
                    Status("RaySkin 验证通过，已登录。")
                    RefreshUI()
                    FrmFriendLeft.RefreshAuthState(True)
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    Dim ApiEx As FriendApiException = TryCast(ex, FriendApiException)
                    Dim HttpEx As HttpRequestCodeException = TryCast(ex, HttpRequestCodeException)
                    If ApiEx IsNot Nothing Then
                        Status(ApiEx.Message, True)
                    ElseIf HttpEx IsNot Nothing Then
                        Status(RayskinHttpError(HttpEx), True)
                        Logger.Warn(ex, "RaySkin 一键登录被拒绝")
                    Else
                        Status("RaySkin 验证失败：密码可能已失效，请到「启动」页重新登录 RaySkin 后再试。", True)
                        Logger.Warn(ex, "RaySkin 一键登录失败")
                    End If
                End Sub)
            Finally
                RunInUi(Sub() SetBusy(False))
            End Try
        End Sub, "Friend RaySkin Login")
    End Sub

    ''' <summary>
    ''' 让 RaySkin 记录一次 join 会话，供服务端 hasJoined 校验。
    ''' </summary>
    Private Sub RayskinJoin(Server As String, Access As String, Uuid As String, ServerId As String)
        Dim JoinBody As New JObject From {
            {"accessToken", Access}, {"selectedProfile", Uuid}, {"serverId", ServerId}
        }
        NetRequestByClient(Server & "/sessionserver/session/minecraft/join", HttpMethod.Post,
            JoinBody.ToString(Formatting.None), "application/json", "*/*", 15000)
    End Sub

    ''' <summary>
    ''' 查询角色的名字（缓存里缺少角色名时使用）。
    ''' </summary>
    Private Function RayskinProfileName(Server As String, Uuid As String) As String
        Try
            Dim Raw As String = NetRequestByClient(
                Server & "/sessionserver/session/minecraft/profile/" & Uri.EscapeDataString(Uuid),
                HttpMethod.Get, Nothing, Nothing, "application/json", 15000, Nothing, RequireJson:=True)
            Return JObject.Parse(Raw).Value(Of String)("name")
        Catch ex As Exception
            Logger.Warn(ex, "查询 RaySkin 角色名失败")
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 从 availableProfiles 中选一个角色：优先本机缓存过的角色，其次同名角色，只有一个时直接用。
    ''' 有多个且都对不上时弹窗让用户选。
    ''' </summary>
    Private Function ChooseRayskinProfile(Profiles As JArray, CachedUuid As String, CachedName As String) As JObject
        If Profiles Is Nothing OrElse Profiles.Count = 0 Then Return Nothing
        Dim Items As List(Of JObject) = Profiles.OfType(Of JObject)().ToList()
        If Items.Count = 0 Then Return Nothing

        If Not String.IsNullOrEmpty(CachedUuid) Then
            Dim Matched As JObject = Items.FirstOrDefault(
                Function(p) String.Equals(p.Value(Of String)("id"), CachedUuid, StringComparison.OrdinalIgnoreCase))
            If Matched IsNot Nothing Then Return Matched
        End If
        If Not String.IsNullOrEmpty(CachedName) Then
            Dim Matched As JObject = Items.FirstOrDefault(
                Function(p) String.Equals(p.Value(Of String)("name"), CachedName, StringComparison.OrdinalIgnoreCase))
            If Matched IsNot Nothing Then Return Matched
        End If
        If Items.Count = 1 Then Return Items(0)

        Dim Result As JObject = Nothing
        RunInUiWait(
        Sub()
            Dim Options As New List(Of IMyRadio)
            For Each Profile As JObject In Items
                Options.Add(New MyRadioBox With {.Text = Profile.Value(Of String)("name")})
            Next
            Dim Selected As Integer? = MyMsgBoxSelect(Options, "选择 RaySkin 角色", "使用该角色", "取消")
            If Selected IsNot Nothing AndAlso Selected >= 0 AndAlso Selected < Items.Count Then
                Result = Items(CInt(Selected))
            End If
        End Sub)
        Return Result
    End Function

    ''' <summary>
    ''' 把 RaySkin 的 HTTP 错误翻译成用户能看懂的提示。
    ''' </summary>
    Private Function RayskinHttpError(ex As HttpRequestCodeException) As String
        Dim ErrorName As String = ""
        Dim ErrorMessage As String = ""
        Try
            If Not String.IsNullOrWhiteSpace(ex.Response) Then
                Dim Obj As JObject = JObject.Parse(ex.Response)
                ErrorName = If(Obj.Value(Of String)("error"), "")
                ErrorMessage = If(Obj.Value(Of String)("errorMessage"), "")
            End If
        Catch
            '错误体不是 JSON 时忽略
        End Try
        Select Case ErrorName
            Case "ForbiddenOperationException"
                Return "RaySkin 账号或密码已失效，请到「启动」页重新登录后再试。"
            Case "IllegalArgumentException"
                Return "RaySkin 认为账号格式不正确：" & If(String.IsNullOrEmpty(ErrorMessage), "请检查邮箱", ErrorMessage)
            Case Else
                If CInt(ex.StatusCode) = 403 Then Return "RaySkin 拒绝了这次登录，请到「启动」页重新登录后再试。"
                Return $"RaySkin 返回 HTTP {CInt(ex.StatusCode)}，请稍后再试。"
        End Select
    End Function

#End Region

#Region "账号操作"

    Private Sub BtnLogout_Click(sender As Object, e As EventArgs) Handles BtnLogout.Click
        If MyMsgBox("退出后需要重新登录才能收发消息，本地聊天记录不会被删除。", "退出登录", "退出", "取消", IsWarn:=True) <> 1 Then Return
        ModFriend.LocalFlush()
        ModFriend.FriendLogout()
        RefreshLoginForm()
        RefreshUI()
        FrmFriendLeft.RefreshAuthState(True)
        Status("已退出登录。")
    End Sub

    Private Sub BtnRefreshInvite_Click(sender As Object, e As EventArgs) Handles BtnRefreshInvite.Click
        RefreshSelfAsync()
        Hint("正在刷新邀请码用量……", HintType.Blue, False)
    End Sub

    Private Sub BtnCopyInvite_Click(sender As Object, e As EventArgs) Handles BtnCopyInvite.Click
        If String.IsNullOrEmpty(TextMyInvite.Text) Then Return
        Clipboard.SetText(TextMyInvite.Text)
        Hint("邀请码已复制", HintType.Green, False)
    End Sub

    Private Sub BtnRestore_Click(sender As Object, e As EventArgs) Handles BtnRestore.Click
        RestoreHistory(False)
    End Sub

    Private Sub BtnRestoreAll_Click(sender As Object, e As EventArgs) Handles BtnRestoreAll.Click
        RestoreHistory(True)
    End Sub

    ''' <summary>
    ''' 从服务端把历史消息拉回本地缓存。
    ''' </summary>
    Private Sub RestoreHistory(FromBeginning As Boolean)
        If Not ModFriend.IsLoggedIn Then Return
        SetBusy(True)
        LabRestoreStatus.Text = "正在从服务器恢复……"
        RunInNewThread(
        Sub()
            Try
                Dim Since As Long = If(FromBeginning, 0L, Settings.Get(Of Long)("FriendLastMsgId"))
                Dim Count As Integer = ModFriend.RestoreLocalHistory(Since)
                RunInUi(
                Sub()
                    LabRestoreStatus.Text = If(Count > 0, $"已恢复 {Count} 条消息到本地。", "没有需要恢复的新消息。")
                    SetBusy(False)
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    LabRestoreStatus.Text = "恢复失败：" & ModFriend.FriendErrorText(ex)
                    SetBusy(False)
                End Sub)
            End Try
        End Sub, "Friend Restore History")
    End Sub

#End Region

#Region "本地缓存"

    ''' <summary>刷新本机聊天记录的路径、会话数与占用大小。</summary>
    Private Sub RefreshLocalInfo()
        Dim Folder As String = ModFriend.LocalFolderForDisplay
        LabLocalPath.Text = "本机缓存位置：" & Folder
        Dim Size As Long = 0
        Dim Count As Integer = 0
        Try
            If Directory.Exists(Folder) Then
                For Each File As String In Directory.EnumerateFiles(Folder, "*.json")
                    Size += New FileInfo(File).Length
                    Count += 1
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "统计本地聊天记录失败")
        End Try
        LabLocalSize.Text = If(Count = 0, "暂无本地缓存", $"{Count} 个会话 · {ModFeature.FormatSize(Size)}")
    End Sub

    Private Sub BtnClearLocal_Click(sender As Object, e As EventArgs) Handles BtnClearLocal.Click
        If MyMsgBox("只会删除本机缓存的聊天记录，服务器上的历史记录仍然保留。" & vbCrLf &
                    "清除后重新打开会话会再次从服务器同步。", "清除本地聊天记录", "清除", "取消", IsWarn:=True) <> 1 Then Return
        If ModFriend.LocalClearAll() Then
            Hint("已清除本地聊天记录", HintType.Green)
        Else
            Hint("清除失败，可能有聊天记录文件正被占用", HintType.Red)
        End If
        RefreshLocalInfo()
    End Sub

    Private Sub BtnOpenLocal_Click(sender As Object, e As EventArgs) Handles BtnOpenLocal.Click
        Dim Folder As String = ModFriend.LocalFolderForDisplay
        Try
            If Not Directory.Exists(Folder) Then Directory.CreateDirectory(Folder)
            Process.Start("explorer.exe", Folder)
        Catch ex As Exception
            Hint("打开文件夹失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

#End Region

End Class
