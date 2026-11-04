Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

''' <summary>
''' 第三方（Authlib-Injector）认证服务器的多服务器、多账号存储与会话切换。
''' 数据保存在加密设置项 AuthServerList 中，结构如下：
''' [{"server":"https://littleskin.cn/api/yggdrasil","name":"LittleSkin","register":"https://littleskin.cn/auth/register",
'''   "updated":0,"accounts":[{"user":"","pass":"","uuid":"","name":"","access":"","client":"","last":0}]}]
''' CacheAuth* 系列设置仍作为「当前会话镜像」保留，切换服务器时通过 Stash / Restore 保持与既有登录、皮肤逻辑兼容。
''' </summary>
Public Module ModAuthServer

    ''' <summary>
    ''' Authlib-Injector 拖拽链接的前缀。
    ''' </summary>
    Public Const AuthlibLinkPrefix As String = "authlib-injector:yggdrasil-server:"

    ''' <summary>
    ''' 默认的第三方认证服务器（RaySkin）。
    ''' </summary>
    Public Const DefaultAuthServer As String = "https://mc.lxray.top/api/yggdrasil"
    ''' <summary>
    ''' 默认认证服务器的显示名。
    ''' </summary>
    Public Const DefaultAuthServerName As String = "RaySkin"
    ''' <summary>
    ''' 次要的第三方认证服务器（LittleSkin），保留作为备选。
    ''' </summary>
    Public Const LegacyAuthServer As String = "https://littleskin.cn/api/yggdrasil"

    '常见皮肤站的显示名，命中时不必再联网取 meta.serverName
    Private ReadOnly ServerNames As New Dictionary(Of String, String) From {
        {"mc.lxray.top", "RaySkin"},
        {"littleskin.cn", "LittleSkin"},
        {"littleskin.com", "LittleSkin"},
        {"littleskin.net", "LittleSkin"},
        {"mcuser.cn", "统一通行证"}
    }

#Region "规范化与取名"

    ''' <summary>
    ''' 把各种写法的认证服务器地址统一为「scheme://host/path」形式，去掉末尾斜杠与 /authserver 后缀。
    ''' 这样拖拽进来的链接与版本设置里已保存的地址不会在列表里重复。
    ''' </summary>
    Public Function AuthServerNormalize(Server As String) As String
        If String.IsNullOrWhiteSpace(Server) Then Return ""
        Dim Result As String = Server.Trim()
        '兼容直接粘贴拖拽链接的写法
        If Result.StartsWithF(AuthlibLinkPrefix) Then
            Result = StringUtils.FormUrlUnescape(Result.Substring(AuthlibLinkPrefix.Length)).Trim()
        End If
        Result = Result.TrimEnd("/"c)
        If Result.EndsWithF("/authserver", True) Then Result = Result.Substring(0, Result.Length - "/authserver".Length).TrimEnd("/"c)
        '统一 scheme 与 host 的大小写
        Dim Matched As Match = Regex.Match(Result, "^(?<scheme>https?)://(?<host>[^/]+)(?<path>.*)$", RegexOptions.IgnoreCase)
        If Matched.Success Then
            Result = Matched.Groups("scheme").Value.ToLowerInvariant() & "://" & Matched.Groups("host").Value.ToLowerInvariant() & Matched.Groups("path").Value
        End If
        Return Result
    End Function

    ''' <summary>
    ''' 取认证服务器对应的主机名。
    ''' </summary>
    Public Function AuthServerHost(Server As String) As String
        Dim Norm As String = AuthServerNormalize(Server)
        Dim Matched As Match = Regex.Match(Norm, "^https?://(?<host>[^/]+)", RegexOptions.IgnoreCase)
        If Matched.Success Then Return Matched.Groups("host").Value
        Return Norm
    End Function

    ''' <summary>
    ''' 猜测认证服务器的显示名：优先用内置映射，其次用域名。
    ''' 不在存储层联网取名，避免拖拽时阻塞 UI 线程。
    ''' </summary>
    Public Function AuthServerGuessName(Server As String) As String
        Dim Host As String = AuthServerHost(Server)
        Dim Known As String = Nothing
        If ServerNames.TryGetValue(Host, Known) Then Return Known
        Return If(Host = "", AuthServerNormalize(Server), Host)
    End Function

    ''' <summary>
    ''' 猜测认证服务器的注册地址，取不到时返回空字符串。
    ''' </summary>
    Public Function AuthServerGuessRegister(Server As String) As String
        Dim Norm As String = AuthServerNormalize(Server)
        If Norm.Contains("/api/yggdrasil") Then Return Norm.Replace("/api/yggdrasil", "/auth/register")
        Return ""
    End Function

#End Region
#Region "列表读写"

    '本次运行是否已完成旧数据迁移
    Private IsListMigrated As Boolean = False

    Private Function AuthServerReadRaw() As JArray
        Try
            Dim Raw As String = Settings.Get(Of String)("AuthServerList")
            If String.IsNullOrWhiteSpace(Raw) Then Return New JArray()
            Dim Token = Raw.DeserializeJson()
            If TypeOf Token Is JArray Then Return CType(Token, JArray)
        Catch ex As Exception
            Logger.Warn(ex, "读取第三方认证服务器列表失败，将按空列表处理")
        End Try
        Return New JArray()
    End Function

    Private Sub AuthServerWriteList(List As JArray)
        Settings.Set("AuthServerList", List.ToString(Newtonsoft.Json.Formatting.None))
    End Sub

    ''' <summary>
    ''' 获取全部认证服务器条目。
    ''' </summary>
    Public Function AuthServerGetAll() As List(Of JObject)
        AuthServerMigrate()
        Dim Result As New List(Of JObject)
        For Each Item In AuthServerReadRaw()
            If TypeOf Item Is JObject Then Result.Add(CType(Item, JObject))
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 读取条目中的某个字段，缺失时返回空字符串。
    ''' </summary>
    Public Function AuthServerValue(Entry As JObject, Key As String) As String
        If Entry Is Nothing Then Return ""
        Dim Token = Entry(Key)
        If Token Is Nothing Then Return ""
        Return Token.ToString().Trim()
    End Function

    Private Function AuthServerFindIn(List As JArray, Server As String) As JObject
        For Each Item In List
            If TypeOf Item Is JObject AndAlso String.Equals(AuthServerNormalize(AuthServerValue(CType(Item, JObject), "server")), Server, StringComparison.OrdinalIgnoreCase) Then
                Return CType(Item, JObject)
            End If
        Next
        Return Nothing
    End Function

    Private Function AuthServerEnsureEntry(List As JArray, Server As String) As JObject
        Dim Entry As JObject = AuthServerFindIn(List, Server)
        If Entry IsNot Nothing Then Return Entry
        Entry = New JObject()
        Entry("server") = Server
        Entry("name") = AuthServerGuessName(Server)
        Entry("register") = AuthServerGuessRegister(Server)
        Entry("updated") = DateTimeOffset.Now.ToUnixTimeSeconds()
        Entry("accounts") = New JArray()
        List.Add(Entry)
        Return Entry
    End Function

    Private Function AuthServerAccountsOf(Entry As JObject) As JArray
        Dim Accounts = Entry("accounts")
        If TypeOf Accounts Is JArray Then Return CType(Accounts, JArray)
        Dim NewList As New JArray()
        Entry("accounts") = NewList
        Return NewList
    End Function

    Private Function AuthServerAccountTime(Account As JObject) As Long
        Dim Time As Long
        If Long.TryParse(AuthServerValue(Account, "last"), Time) Then Return Time
        Return 0L
    End Function

    ''' <summary>
    ''' 添加或更新一个认证服务器条目，保留该服务器下已有的账号。
    ''' </summary>
    Public Function AuthServerUpsert(Server As String, Optional Name As String = Nothing, Optional Register As String = Nothing) As JObject
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" Then Return Nothing
            Dim List As JArray = AuthServerReadRaw()
            Dim Entry As JObject = AuthServerEnsureEntry(List, Norm)
            Dim NewName As String = If(String.IsNullOrWhiteSpace(Name), AuthServerValue(Entry, "name"), Name.Trim())
            If NewName = "" Then NewName = AuthServerGuessName(Norm)
            Entry("name") = NewName
            Dim NewRegister As String = If(String.IsNullOrWhiteSpace(Register), AuthServerValue(Entry, "register"), Register.Trim())
            If NewRegister = "" Then NewRegister = AuthServerGuessRegister(Norm)
            Entry("register") = NewRegister
            Entry("updated") = DateTimeOffset.Now.ToUnixTimeSeconds()
            AuthServerWriteList(List)
            Return Entry
        Catch ex As Exception
            Logger.Error(ex, $"添加认证服务器失败：{Server}")
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 获取某个服务器下的账号，按最近使用时间倒序。
    ''' </summary>
    Public Function AuthServerGetAccounts(Server As String) As List(Of JObject)
        Dim Result As New List(Of JObject)
        Dim Norm As String = AuthServerNormalize(Server)
        If Norm = "" Then Return Result
        Dim Entry As JObject = AuthServerFindIn(AuthServerReadRaw(), Norm)
        If Entry Is Nothing Then Return Result
        For Each Item In AuthServerAccountsOf(Entry)
            If TypeOf Item Is JObject Then
                Dim Account As JObject = CType(Item, JObject)
                If AuthServerValue(Account, "user") <> "" OrElse AuthServerValue(Account, "access") <> "" Then Result.Add(Account)
            End If
        Next
        Return Result.OrderByDescending(Function(Account) AuthServerAccountTime(Account)).ToList()
    End Function

    ''' <summary>
    ''' 把某个服务器下指定账号的会话与密码载入 CacheAuth* 镜像，用于在同一服务器内切换账号。
    ''' </summary>
    Public Function AuthServerSetAccount(Server As String, UserName As String) As Boolean
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" OrElse UserName = "" Then Return False
            Dim Target As JObject = Nothing
            For Each Account As JObject In AuthServerGetAccounts(Norm)
                If String.Equals(AuthServerValue(Account, "user"), UserName, StringComparison.OrdinalIgnoreCase) Then
                    Target = Account
                    Exit For
                End If
            Next
            If Target Is Nothing Then Return False
            Settings.Set("CacheAuthServerServer", Norm)
            Settings.Set("CacheAuthUsername", AuthServerValue(Target, "user"))
            Settings.Set("CacheAuthPass", AuthServerValue(Target, "pass"))
            Settings.Set("CacheAuthUuid", AuthServerValue(Target, "uuid"))
            Settings.Set("CacheAuthName", AuthServerValue(Target, "name"))
            Settings.Set("CacheAuthAccess", AuthServerValue(Target, "access"))
            Settings.Set("CacheAuthClient", AuthServerValue(Target, "client"))
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"切换第三方登录账号失败（{Server} / {UserName}）")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 保存某个服务器下的一个账号。空字符串参数表示「不修改原有值」，避免用空密码覆盖已记住的密码。
    ''' </summary>
    Public Sub AuthServerSaveAccount(Server As String, UserName As String, Password As String,
            Optional Uuid As String = "", Optional Name As String = "", Optional Access As String = "", Optional Client As String = "")
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" Then Return
            If UserName = "" AndAlso Access = "" Then Return
            Dim List As JArray = AuthServerReadRaw()
            Dim Entry As JObject = AuthServerEnsureEntry(List, Norm)
            Dim Accounts As JArray = AuthServerAccountsOf(Entry)
            Dim Account As JObject = Nothing
            For Each Item In Accounts
                If TypeOf Item Is JObject AndAlso String.Equals(AuthServerValue(CType(Item, JObject), "user"), UserName, StringComparison.OrdinalIgnoreCase) Then
                    Account = CType(Item, JObject)
                    Exit For
                End If
            Next
            If Account Is Nothing Then
                Account = New JObject()
                Account("user") = UserName
            End If
            If Password <> "" Then Account("pass") = Password
            If Uuid <> "" Then Account("uuid") = Uuid
            If Name <> "" Then Account("name") = Name
            If Access <> "" Then Account("access") = Access
            If Client <> "" Then Account("client") = Client
            Account("last") = DateTimeOffset.Now.ToUnixTimeSeconds()
            '移到最前，保证默认选中最近使用的账号
            Accounts.Remove(Account)
            Accounts.Insert(0, Account)
            Entry("updated") = DateTimeOffset.Now.ToUnixTimeSeconds()
            AuthServerWriteList(List)
        Catch ex As Exception
            Logger.Error(ex, $"保存第三方登录账号失败（{Server} / {UserName}）")
        End Try
    End Sub

#End Region
#Region "会话切换"

    ''' <summary>
    ''' 把当前 CacheAuth* 会话存回它所属的服务器，切换服务器后仍可免密切回。
    ''' </summary>
    Public Function AuthServerStashCurrent() As Boolean
        Try
            Dim Server As String = AuthServerNormalize(Settings.Get(Of String)("CacheAuthServerServer"))
            If Server = "" Then Return False
            Dim UserName As String = Settings.Get(Of String)("CacheAuthUsername")
            Dim Access As String = Settings.Get(Of String)("CacheAuthAccess")
            If UserName = "" AndAlso Access = "" Then Return False
            AuthServerSaveAccount(Server, UserName, Settings.Get(Of String)("CacheAuthPass"),
                Uuid:=Settings.Get(Of String)("CacheAuthUuid"), Name:=Settings.Get(Of String)("CacheAuthName"),
                Access:=Access, Client:=Settings.Get(Of String)("CacheAuthClient"))
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "暂存第三方登录会话失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 切换当前的第三方认证服务器：先暂存旧会话，再把目标服务器的最近账号写回 CacheAuth* 镜像。
    ''' 传入 Instance 时会同步写入该版本的服务器设置，用于服主锁定登录方式的场景。
    ''' </summary>
    Public Function AuthServerSetCurrent(Server As String, Optional Instance As McInstance = Nothing) As Boolean
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" Then Return False
            '迁移必须在改动 CacheAuthServerServer 之前完成，否则旧账号会被归到新服务器名下
            AuthServerMigrate()
            '必须先把旧会话落库，否则下面清空镜像时会把别的服务器的登录态一起丢掉
            AuthServerStashCurrent()
            Dim Entry As JObject = AuthServerUpsert(Norm)
            If Entry Is Nothing Then Return False
            Dim Accounts As List(Of JObject) = AuthServerGetAccounts(Norm)
            Dim Account As JObject = If(Accounts.Count > 0, Accounts(0), Nothing)
            '先写会话镜像，再写版本设置：McInstanceSelected 的重判以 CacheAuthServerServer 为基准，顺序反了会误清登录态
            Settings.Set("CacheAuthServerServer", Norm)
            Settings.Set("CacheAuthServerName", AuthServerValue(Entry, "name"))
            Settings.Set("CacheAuthServerRegister", AuthServerValue(Entry, "register"))
            Settings.Set("CacheAuthUsername", If(Account Is Nothing, "", AuthServerValue(Account, "user")))
            Settings.Set("CacheAuthPass", If(Account Is Nothing, "", AuthServerValue(Account, "pass")))
            Settings.Set("CacheAuthUuid", If(Account Is Nothing, "", AuthServerValue(Account, "uuid")))
            Settings.Set("CacheAuthName", If(Account Is Nothing, "", AuthServerValue(Account, "name")))
            Settings.Set("CacheAuthAccess", If(Account Is Nothing, "", AuthServerValue(Account, "access")))
            Settings.Set("CacheAuthClient", If(Account Is Nothing, "", AuthServerValue(Account, "client")))
            If Instance IsNot Nothing Then
                Settings.Set("VersionServerAuthServer", Norm, Instance:=Instance)
                Settings.Set("VersionServerAuthName", AuthServerValue(Entry, "name"), Instance:=Instance)
                Settings.Set("VersionServerAuthRegister", AuthServerValue(Entry, "register"), Instance:=Instance)
            End If
            Logger.Info($"已切换到第三方认证服务器：{Norm}（{AuthServerValue(Entry, "name")}）")
            Return True
        Catch ex As Exception
            Logger.Error(ex, $"切换第三方认证服务器失败：{Server}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 从列表中彻底删除某个账号（账号、密码与会话一并删除）。
    ''' 若删除的正是当前使用的账号，同时清空会话镜像，避免拿已删除账号的 token 去启动。
    ''' </summary>
    Public Function AuthServerRemoveAccount(Server As String, UserName As String) As Boolean
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" OrElse UserName = "" Then Return False
            Dim List As JArray = AuthServerReadRaw()
            Dim Entry As JObject = AuthServerFindIn(List, Norm)
            If Entry Is Nothing Then Return False
            Dim Accounts As JArray = AuthServerAccountsOf(Entry)
            Dim Removed As Boolean = False
            For Each Item As JToken In Accounts.ToList()
                If TypeOf Item Is JObject AndAlso String.Equals(AuthServerValue(CType(Item, JObject), "user"), UserName, StringComparison.OrdinalIgnoreCase) Then
                    Accounts.Remove(Item)
                    Removed = True
                End If
            Next
            If Not Removed Then Return False
            Entry("updated") = DateTimeOffset.Now.ToUnixTimeSeconds()
            AuthServerWriteList(List)
            '删掉的正好是当前使用的账号
            If String.Equals(Settings.Get(Of String)("CacheAuthUsername"), UserName, StringComparison.OrdinalIgnoreCase) AndAlso
               AuthServerNormalize(Settings.Get(Of String)("CacheAuthServerServer")) = Norm Then
                Settings.Set("CacheAuthUsername", "")
                Settings.Set("CacheAuthPass", "")
                Settings.Set("CacheAuthUuid", "")
                Settings.Set("CacheAuthName", "")
                Settings.Set("CacheAuthAccess", "")
                Settings.Set("CacheAuthClient", "")
            End If
            Logger.Info($"已删除第三方登录账号：{UserName}（{Norm}）")
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"删除第三方登录账号失败（{Server} / {UserName}）")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 清除某个账号在列表中的会话（保留账号密码，下次可免输账号直接重登）。
    ''' </summary>
    Public Sub AuthServerClearSession(Server As String, UserName As String)
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" OrElse UserName = "" Then Return
            Dim List As JArray = AuthServerReadRaw()
            Dim Entry As JObject = AuthServerFindIn(List, Norm)
            If Entry Is Nothing Then Return
            For Each Item In AuthServerAccountsOf(Entry)
                If TypeOf Item Is JObject AndAlso String.Equals(AuthServerValue(CType(Item, JObject), "user"), UserName, StringComparison.OrdinalIgnoreCase) Then
                    Dim Account As JObject = CType(Item, JObject)
                    Account("access") = ""
                    Account("client") = ""
                    AuthServerWriteList(List)
                    Return
                End If
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"清除第三方登录会话失败（{Server} / {UserName}）")
        End Try
    End Sub

    ''' <summary>
    ''' 确保列表里存在默认的 RaySkin 服务器，只执行一次。
    ''' 玩家把它删掉后不会再自动加回来。
    ''' </summary>
    Public Sub AuthServerEnsureDefault()
        Try
            If Settings.Get(Of Boolean)("AuthServerRaySkinAdded") Then Return
            Settings.Set("AuthServerRaySkinAdded", True)
            AuthServerUpsert(DefaultAuthServer, DefaultAuthServerName)
        Catch ex As Exception
            Logger.Warn(ex, "添加默认认证服务器失败")
        End Try
    End Sub

    ''' <summary>
    ''' 把内置的 RaySkin 认证服务器地址从 http 升级到 https（该域名已启用证书）。
    ''' 只处理 mc.lxray.top，用户自己添加的其他服务器不动。
    ''' </summary>
    Public Sub AuthServerUpgradeBuiltinHttps()
        Try
            If Settings.Get(Of Boolean)("AuthServerHttpsUpgraded") Then Return
            Settings.Set("AuthServerHttpsUpgraded", True)

            Dim List As JArray = AuthServerReadRaw()
            Dim Changed As Integer = 0
            For Each Item In List
                Dim Entry As JObject = TryCast(Item, JObject)
                If Entry Is Nothing Then Continue For
                Dim Server As String = AuthServerValue(Entry, "server")
                If String.IsNullOrEmpty(Server) OrElse Not Server.StartsWithF("http://mc.lxray.top", True) Then Continue For
                Entry("server") = "https://" & Server.Substring("http://".Length)
                Dim Register As String = AuthServerValue(Entry, "register")
                If Not String.IsNullOrEmpty(Register) AndAlso Register.StartsWithF("http://mc.lxray.top", True) Then
                    Entry("register") = "https://" & Register.Substring("http://".Length)
                End If
                Changed += 1
            Next
            If Changed = 0 Then Return

            AuthServerWriteList(List)
            Logger.Info($"已把 {Changed} 个 RaySkin 服务器地址升级为 HTTPS")

            '会话镜像里的地址也要一起换，否则注入到游戏的仍是旧的 http 地址
            Dim Current As String = Settings.Get(Of String)("CacheAuthServerServer")
            If Not String.IsNullOrEmpty(Current) AndAlso Current.StartsWithF("http://mc.lxray.top", True) Then
                Settings.Set("CacheAuthServerServer", "https://" & Current.Substring("http://".Length))
            End If
        Catch ex As Exception
            Logger.Warn(ex, "升级 RaySkin 服务器地址到 HTTPS 失败")
        End Try
    End Sub

    ''' <summary>
    ''' 从列表中删除整台认证服务器（连同它下面保存的账号）。
    ''' 若删的是当前使用的服务器，会自动切到剩下的第一台；一台不剩时清空会话。
    ''' </summary>
    Public Function AuthServerRemoveServer(Server As String) As Boolean
        Try
            Dim Norm As String = AuthServerNormalize(Server)
            If Norm = "" Then Return False
            Dim List As JArray = AuthServerReadRaw()
            Dim Entry As JObject = AuthServerFindIn(List, Norm)
            If Entry Is Nothing Then Return False
            List.Remove(Entry)
            AuthServerWriteList(List)
            Logger.Info($"已删除第三方认证服务器：{Norm}")
            '删的是当前使用的服务器时，要么切到剩下的，要么把会话清空
            If AuthServerNormalize(Settings.Get(Of String)("CacheAuthServerServer")) = Norm Then
                '先断开缓存，否则接下来的 stash 会把刚删掉的服务器又写回来
                Settings.Set("CacheAuthServerServer", "")
                Settings.Set("CacheAuthServerName", "")
                Settings.Set("CacheAuthServerRegister", "")
                Settings.Set("CacheAuthUsername", "")
                Settings.Set("CacheAuthPass", "")
                Settings.Set("CacheAuthUuid", "")
                Settings.Set("CacheAuthName", "")
                Settings.Set("CacheAuthAccess", "")
                Settings.Set("CacheAuthClient", "")
                Dim Rest As List(Of JObject) = AuthServerGetAll()
                If Rest.Count > 0 Then
                    AuthServerSetCurrent(AuthServerValue(Rest(0), "server"), If(McInstanceSelected, Nothing))
                ElseIf McInstanceSelected IsNot Nothing Then
                    '一台都不剩：版本的服务器设置也要清掉，否则 AuthServerCurrent 还会返回已删除的地址
                    Settings.Set("VersionServerAuthServer", "", Instance:=McInstanceSelected)
                    Settings.Set("VersionServerAuthName", "", Instance:=McInstanceSelected)
                    Settings.Set("VersionServerAuthRegister", "", Instance:=McInstanceSelected)
                End If
            End If
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"删除第三方认证服务器失败（{Server}）")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 当前应当使用的认证服务器：优先取版本设置，其次取会话缓存。
    ''' </summary>
    Public Function AuthServerCurrent() As String
        Dim FromVersion As String = ""
        If McInstanceSelected IsNot Nothing Then FromVersion = Settings.Get(Of String)("VersionServerAuthServer", Instance:=McInstanceSelected)
        If AuthServerNormalize(FromVersion) <> "" Then Return AuthServerNormalize(FromVersion)
        Return AuthServerNormalize(Settings.Get(Of String)("CacheAuthServerServer"))
    End Function

    ''' <summary>
    ''' 当前会话是否属于指定的认证服务器。
    ''' </summary>
    Public Function AuthServerSessionMatch(Server As String) As Boolean
        Dim Norm As String = AuthServerNormalize(Server)
        If Norm = "" Then Return False
        If Settings.Get(Of String)("CacheAuthAccess") = "" Then Return False
        Return AuthServerNormalize(Settings.Get(Of String)("CacheAuthServerServer")) = Norm
    End Function

#End Region
#Region "旧数据迁移"

    ''' <summary>
    ''' 把旧版的「单一服务器 + 全局扁平账号密码列表」迁移到 AuthServerList，只执行一次。
    ''' </summary>
    Public Sub AuthServerMigrate()
        If IsListMigrated Then Return
        IsListMigrated = True
        Try
            '先保证默认的 RaySkin 存在，这样全新配置也有可用的第三方服务器
            AuthServerEnsureDefault()
            '再把老用户列表里的 http://mc.lxray.top 升级成 https
            AuthServerUpgradeBuiltinHttps()
            If Settings.Get(Of Boolean)("AuthServerListMigrated") Then Return
            Dim Server As String = AuthServerNormalize(Settings.Get(Of String)("CacheAuthServerServer"))
            Dim Emails As String() = If(Settings.Get(Of String)("LoginAuthEmail") = "", New String() {}, Settings.Get(Of String)("LoginAuthEmail").Split("¨"))
            Dim Passwords As String() = If(Settings.Get(Of String)("LoginAuthPass") = "", New String() {}, Settings.Get(Of String)("LoginAuthPass").Split("¨"))
            If Server = "" AndAlso Emails.Length = 0 Then
                Settings.Set("AuthServerListMigrated", True)
                Return
            End If
            '没有任何服务器信息时按 LittleSkin 兜底，避免旧账号彻底丢失
            If Server = "" Then Server = AuthServerNormalize(LegacyAuthServer)
            AuthServerUpsert(Server, Settings.Get(Of String)("CacheAuthServerName"), Settings.Get(Of String)("CacheAuthServerRegister"))
            For i = 0 To Emails.Length - 1
                If String.IsNullOrWhiteSpace(Emails(i)) Then Continue For
                AuthServerSaveAccount(Server, Emails(i).Trim(), If(i < Passwords.Length, Passwords(i).Trim(), ""))
            Next
            AuthServerStashCurrent()
            Settings.Set("AuthServerListMigrated", True)
            Logger.Info($"已迁移第三方登录账号到服务器列表（{Server}，{Emails.Length} 个账号）")
        Catch ex As Exception
            Logger.Warn(ex, "迁移第三方登录账号列表失败")
        End Try
    End Sub

#End Region
#Region "拖拽处理"

    ''' <summary>
    ''' 处理拖入的 authlib 授权链接：校验、入库、切换登录方式并刷新界面。
    ''' </summary>
    ''' <param name="Link">拖入的原始文本，可以带 authlib-injector:yggdrasil-server: 前缀。</param>
    ''' <param name="Target">要开启第三方登录的版本；为 Nothing 时只入库不切换。</param>
    Public Function AuthlibDrop(Link As String, Optional Target As McInstance = Nothing) As Boolean
        Try
            Dim Norm As String = AuthServerNormalize(Link)
            If Norm = "" Then
                Hint("拖入的第三方登录链接为空，请重新拖入！", HintType.Red)
                Return False
            End If
            Dim ErrorMessage As String = New ValidateHttp().Validate(Norm)
            If Not String.IsNullOrEmpty(ErrorMessage) Then
                Hint($"拖入的第三方登录地址无效（{Norm}）：{ErrorMessage}", HintType.Red)
                Return False
            End If
            '保留拖拽时的提示弹窗：先让玩家确认，再入库并切换
            Dim ServerName As String = AuthServerGuessName(Norm)
            Dim TargetName As String = If(Target Is Nothing, "启动器", $"版本 {Target.Name}")
            If MyMsgBox($"是否要在{TargetName}中添加并切换到第三方登录？" & vbCrLf &
                        $"认证服务器：{ServerName}" & vbCrLf &
                        $"服务器地址：{Norm}" & vbCrLf & vbCrLf &
                        "添加后可在主页登录方式栏直接切换，也可以在 版本设置 → 设置 → 服务器选项 中修改。",
                        "第三方登录", "确定", "取消") = 2 Then Return False
            Dim Entry As JObject = AuthServerUpsert(Norm)
            If Entry Is Nothing Then
                Hint("添加第三方登录服务器失败，请查看日志了解详情！", HintType.Red)
                Return False
            End If
            ServerName = AuthServerValue(Entry, "name")
            AuthServerSetCurrent(Norm, Target)
            If Target Is Nothing Then
                Hint($"已添加认证服务器 {ServerName}，下载游戏后即可使用", HintType.Green)
            Else
                Settings.Set("VersionServerLogin", 4, Instance:=Target)
                Settings.Set("LoginType", McLoginType.Auth)
                Hint($"已添加并切换到 {ServerName} 登录", HintType.Green)
            End If
            '刷新正在显示的界面
            Select Case FrmMain.PageCurrent.Page
                Case FormMain.PageType.InstanceSetup
                    If FrmInstanceSetup IsNot Nothing AndAlso FrmMain.PageCurrentSub = FormMain.PageSubType.InstanceSetup Then FrmInstanceSetup.Reload()
                Case FormMain.PageType.Launch
                    '切服后不能保留旧的账号密码输入，否则会拿上一个服务器的密码去登录
                    RunInUi(Sub() If FrmLaunchLeft IsNot Nothing Then FrmLaunchLeft.RefreshPage(False, True))
            End Select
            Return True
        Catch ex As Exception
            Logger.Error(ex, "处理 Authlib 拖拽失败")
            Hint("添加第三方登录失败，请查看日志了解详情！", HintType.Red)
            Return False
        End Try
    End Function

#End Region

End Module
