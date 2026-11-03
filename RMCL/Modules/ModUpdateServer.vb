Imports System.Security.Cryptography
Imports System.Text.RegularExpressions

''' <summary>
''' 自建更新源（官方源）的授权与接口封装。
''' 服务端见仓库 server/ 目录（PHP 8 + SQLite），接口约定见 server/README.md。
''' 一个验证码默认绑定一台设备；授权到期后服务端拒绝一切检查与下载请求，客户端也不再用本地时间糊弄。
''' </summary>
Friend Module ModUpdateServer

#Region "错误码"

    Public Const ErrNetwork As String = "E_NETWORK"
    Public Const ErrFormat As String = "E_FORMAT"
    Public Const ErrNoToken As String = "E_NO_TOKEN"
    Public Const ErrTokenInvalid As String = "E_TOKEN_INVALID"
    Public Const ErrTokenExpired As String = "E_TOKEN_EXPIRED"
    Public Const ErrTokenDevice As String = "E_TOKEN_DEVICE"
    Public Const ErrExpired As String = "E_EXPIRED"
    Public Const ErrCodeFormat As String = "E_CODE_FORMAT"
    Public Const ErrCodeNotFound As String = "E_CODE_NOT_FOUND"
    Public Const ErrCodeDisabled As String = "E_CODE_DISABLED"
    Public Const ErrDeviceBound As String = "E_DEVICE_BOUND"
    Public Const ErrTooMany As String = "E_TOO_MANY"

    ''' <summary>
    ''' 授权相关的失败。ErrorCode 与服务器返回的 code 一致，便于界面给不同提示。
    ''' </summary>
    Friend Class UpdateLicenseException
        Inherits Exception

        Public ReadOnly Property ErrorCode As String
        ''' <summary>服务器给出的到期时间（Unix 秒），没有则为 0。</summary>
        Public ReadOnly Property ExpireAt As Long

        Public Sub New(ErrorCode As String, Message As String, Optional ExpireAt As Long = 0)
            MyBase.New(Message)
            Me.ErrorCode = If(ErrorCode, "")
            Me.ExpireAt = ExpireAt
        End Sub

        ''' <summary>
        ''' 是否「只要重新输入一次验证码就能继续」：令牌缺失、失效、换设备都属于这一类。
        ''' 已过期、被停用、绑定别的设备则不行，必须去后台续期或解绑。
        ''' </summary>
        Public ReadOnly Property NeedActivate As Boolean
            Get
                Select Case ErrorCode
                    Case ErrNoToken, ErrTokenInvalid, ErrTokenExpired, ErrTokenDevice
                        Return True
                    Case Else
                        Return False
                End Select
            End Get
        End Property
    End Class

#End Region

#Region "数据结构"

    ''' <summary>一次授权的状态。</summary>
    Friend Class LicenseInfo
        Public Token As String = ""
        Public ExpireAt As Long = 0
        Public CodeMask As String = ""
        Public RemainingDays As Integer = 0
        Public DeviceId As String = ""

        Public ReadOnly Property ExpireLocal As DateTime
            Get
                If ExpireAt <= 0 Then Return DateTime.MinValue
                Return DateTimeOffset.FromUnixTimeSeconds(ExpireAt).ToLocalTime().DateTime
            End Get
        End Property

        Public ReadOnly Property ExpireText As String
            Get
                If ExpireAt <= 0 Then Return "未授权"
                Return ExpireLocal.ToString("yyyy-MM-dd HH:mm")
            End Get
        End Property
    End Class

    ''' <summary>官方源上某个版本的发布信息。</summary>
    Friend Class ServerRelease
        Public ReleaseId As Integer = 0
        Public Version As String = ""
        Public Title As String = ""
        Public Notes As String = ""
        Public PublishedAt As Long = 0
        Public FileName As String = "RMCL.exe"
        Public Size As Long = 0
        Public Sha256 As String = ""
        '以下来自 license 字段，仅用于界面展示
        Public ExpireAt As Long = 0
        Public RemainingDays As Integer = 0
        Public CodeMask As String = ""
    End Class

#End Region

#Region "本地状态"

    Private DeviceIdCache As String = Nothing

    ''' <summary>
    ''' 官方源地址，直接内置在客户端里。
    ''' 不提供修改入口：地址填错只会让检查更新彻底失效，而用户无从判断该填什么。
    ''' 换域名或加 HTTPS 时改这里，重新编译即可，服务端不用动。
    ''' </summary>
    Public Const ServerBaseUrl As String = "http://update.mc.lxray.top"

    ''' <summary>当前使用的官方源地址（无结尾斜杠）。</summary>
    Public ReadOnly Property ConfiguredServerUrl As String
        Get
            Return ServerBaseUrl.TrimEnd("/"c)
        End Get
    End Property

    ''' <summary>
    ''' 设备标识：优先由 Windows 的 MachineGuid 派生，取不到时退回随机值并存进设置。
    ''' 换系统或重装会变，此时让用户在后台点「解绑」即可，不必换验证码。
    ''' </summary>
    Public ReadOnly Property DeviceId As String
        Get
            If DeviceIdCache IsNot Nothing Then Return DeviceIdCache
            Dim Saved As String = ""
            Try
                Saved = Settings.Get(Of String)("UpdateDeviceId")
            Catch ex As Exception
                Logger.Warn(ex, "读取设备标识失败")
            End Try
            If Not String.IsNullOrEmpty(Saved) Then
                DeviceIdCache = Saved
                Return Saved
            End If
            Dim Seed As String = ReadMachineGuid()
            If String.IsNullOrEmpty(Seed) Then Seed = Guid.NewGuid().ToString("N")
            Dim Id As String = Sha256Text("RMCL-UPDATE|" & Seed).Substring(0, 32)
            Try
                Settings.Set("UpdateDeviceId", Id)
            Catch ex As Exception
                Logger.Warn(ex, "保存设备标识失败")
            End Try
            DeviceIdCache = Id
            Return Id
        End Get
    End Property

    Private Function ReadMachineGuid() As String
        Try
            Using BaseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                   Microsoft.Win32.RegistryView.Registry64)
                Using Key = BaseKey.OpenSubKey("SOFTWARE\Microsoft\Cryptography")
                    Dim Value = Key?.GetValue("MachineGuid")
                    If Value IsNot Nothing Then Return Value.ToString()
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "读取 MachineGuid 失败，改用随机设备标识")
        End Try
        Return ""
    End Function

    Private Function Sha256Text(Text As String) As String
        Using Sha As SHA256 = SHA256.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Encoding.UTF8.GetBytes(Text))).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    ''' <summary>计算文件的 SHA-256（小写十六进制），用于校验下载到的更新包。</summary>
    Public Function Sha256File(Path As String) As String
        Using Stream As New FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)
            Using Sha As SHA256 = SHA256.Create()
                Return BitConverter.ToString(Sha.ComputeHash(Stream)).Replace("-", "").ToLowerInvariant()
            End Using
        End Using
    End Function

    Public Function CurrentToken() As String
        Try
            Return Settings.Get(Of String)("UpdateLicenseToken")
        Catch ex As Exception
            Logger.Warn(ex, "读取更新授权令牌失败")
            Return ""
        End Try
    End Function

    Public Function CurrentExpire() As Long
        Try
            Return Settings.Get(Of Long)("UpdateLicenseExpire")
        Catch ex As Exception
            Logger.Warn(ex, "读取更新授权到期时间失败")
            Return 0L
        End Try
    End Function

    Public Function CurrentMask() As String
        Try
            Return Settings.Get(Of String)("UpdateLicenseMask")
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ''' <summary>本地是否能确认「还有效」。真正的判定始终以服务器为准。</summary>
    Public ReadOnly Property HasValidLicense As Boolean
        Get
            Return Not String.IsNullOrEmpty(CurrentToken()) AndAlso CurrentExpire() > GetUnixTimestampUtc()
        End Get
    End Property

    Public Function RemainingDaysValue() As Integer
        Dim Expire As Long = CurrentExpire()
        If Expire <= 0 Then Return 0
        Dim Left As Long = Expire - GetUnixTimestampUtc()
        If Left <= 0 Then Return 0
        Return CInt(Math.Ceiling(Left / 86400.0))
    End Function

    ''' <summary>给设置页显示的一行授权状态。</summary>
    Public Function LicenseSummary() As String
        If String.IsNullOrEmpty(CurrentToken()) Then Return "未授权"
        Dim Expire As Long = CurrentExpire()
        If Expire <= 0 Then Return "未授权"
        Dim Text As String = DateTimeOffset.FromUnixTimeSeconds(Expire).ToLocalTime().DateTime.ToString("yyyy-MM-dd HH:mm")
        If Expire <= GetUnixTimestampUtc() Then Return $"授权已到期（{Text}）"
        Return $"已授权，剩余 {RemainingDaysValue()} 天（{Text} 到期）"
    End Function

    Public Sub ClearLicense()
        Try
            Settings.Set("UpdateLicenseToken", "")
            Settings.Set("UpdateLicenseExpire", 0L)
            Settings.Set("UpdateLicenseMask", "")
            Logger.Info("已清除本地更新授权")
        Catch ex As Exception
            Logger.Warn(ex, "清除更新授权失败")
        End Try
    End Sub

#End Region

#Region "接口调用"

    Private Function ApiUrl(Action As String) As String
        Return ConfiguredServerUrl & "/index.php?action=" & Action
    End Function

    ''' <summary>
    ''' 激活验证码。成功后会保存令牌与到期时间，失败抛 UpdateLicenseException。
    ''' </summary>
    Public Function Activate(Code As String) As LicenseInfo
        Dim Normalized As String = NormalizeCode(Code)
        If Normalized = "" Then
            Throw New UpdateLicenseException(ErrCodeFormat, "验证码格式不正确，应为 4 组 4 位字符")
        End If

        Dim Body As New JObject()
        Body("code") = Normalized
        Body("device_id") = DeviceId
        Body("device_name") = MachineName()
        Body("app_version") = VersionBaseName

        Dim Data As JObject = RequestJson(ApiUrl("activate"), HttpMethod.Post, Body.ToString())
        Dim Info As LicenseInfo = ParseLicense(Data)
        If String.IsNullOrEmpty(Info.Token) Then
            Throw New UpdateLicenseException(ErrFormat, "服务器没有返回授权凭证，请检查服务端版本")
        End If
        SaveLicense(Info)
        Logger.Info($"更新授权激活成功：{Info.CodeMask}，到期 {Info.ExpireText}（设备 {DeviceId.Substring(0, 8)}…）")
        Return Info
    End Function

    ''' <summary>校验当前令牌并刷新到期时间。</summary>
    Public Function Verify() As LicenseInfo
        Dim Body As New JObject()
        Body("token") = CurrentToken()
        Body("device_id") = DeviceId
        Dim Data As JObject = RequestJson(ApiUrl("verify"), HttpMethod.Post, Body.ToString())
        Dim Info As LicenseInfo = ParseLicense(Data)
        If Info.Token = "" Then Info.Token = CurrentToken()
        SaveLicense(Info)
        Return Info
    End Function

    ''' <summary>取官方源上的最新版本。</summary>
    Public Function FetchRelease() As ServerRelease
        Dim TokenValue As String = CurrentToken()
        If String.IsNullOrEmpty(TokenValue) Then
            Throw New UpdateLicenseException(ErrNoToken, "尚未授权，请先输入验证码")
        End If
        Dim Url As String = ApiUrl("latest") & "&token=" & Uri.EscapeDataString(TokenValue) &
                            "&device_id=" & Uri.EscapeDataString(DeviceId)
        Dim Data As JObject = RequestJson(Url)
        Dim Rel As JObject = TryCast(Data("release"), JObject)
        If Rel Is Nothing Then
            Throw New UpdateLicenseException("E_NO_RELEASE", "更新服务器上还没有发布任何版本")
        End If
        Dim Asset As JObject = TryCast(Rel("asset"), JObject)
        Dim Info As New ServerRelease With {
            .ReleaseId = CInt(JsonLong(Rel, "id")),
            .Version = If(Rel("version")?.ToString, "").Trim(),
            .Title = If(Rel("title")?.ToString, "").Trim(),
            .Notes = If(Rel("notes")?.ToString, ""),
            .PublishedAt = JsonLong(Rel, "published_at"),
            .FileName = If(Asset?("name")?.ToString, "RMCL.exe"),
            .Size = JsonLong(Asset, "size"),
            .Sha256 = If(Asset?.Item("sha256")?.ToString, "")
        }
        If String.IsNullOrEmpty(Info.Version) Then
            Throw New UpdateLicenseException(ErrFormat, "更新服务器没有返回版本号")
        End If

        '顺手刷新本地到期时间，界面上的「剩余 N 天」就不会一直停在旧值
        Dim Lic As JObject = TryCast(Data("license"), JObject)
        If Lic IsNot Nothing Then
            Info.ExpireAt = JsonLong(Lic, "expire_at")
            Info.RemainingDays = CInt(JsonLong(Lic, "remaining_days"))
            Info.CodeMask = If(Lic("code_mask")?.ToString, "")
            If Info.ExpireAt > 0 AndAlso Info.ExpireAt <> CurrentExpire() Then
                Try
                    Settings.Set("UpdateLicenseExpire", Info.ExpireAt)
                Catch ex As Exception
                    Logger.Warn(ex, "刷新授权到期时间失败")
                End Try
            End If
        End If
        Logger.Info($"官方源：最新版本 {Info.Version}，文件 {Info.FileName}（{FormatSize(Info.Size)}）")
        Return Info
    End Function

    ''' <summary>
    ''' 换取下载票据，返回可直接交给下载引擎的地址。票据有效期只有几分钟，所以每次下载前都新换一张。
    ''' </summary>
    Public Function CreateTicket(ReleaseId As Integer) As String
        Dim Body As New JObject()
        Body("token") = CurrentToken()
        Body("device_id") = DeviceId
        Body("release_id") = ReleaseId
        Dim Data As JObject = RequestJson(ApiUrl("ticket"), HttpMethod.Post, Body.ToString())
        Dim Url As String = If(Data("url")?.ToString, "")
        If String.IsNullOrEmpty(Url) Then
            Throw New UpdateLicenseException("E_TICKET", "更新服务器没有返回下载地址")
        End If
        Return Url
    End Function

#End Region

#Region "内部实现"

    Private Function MachineName() As String
        Try
            Return Environment.MachineName
        Catch ex As Exception
            Return "未知设备"
        End Try
    End Function

    ''' <summary>去掉空格、横线与可能粘贴进来的 RMCL 前缀，统一大写。</summary>
    Public Function NormalizeCode(Raw As String) As String
        If String.IsNullOrWhiteSpace(Raw) Then Return ""
        Dim Plain As String = Regex.Replace(Raw.ToUpperInvariant(), "[^A-Z0-9]", "")
        If Plain.StartsWithF("RMCL") AndAlso Plain.Length = 20 Then Plain = Plain.Substring(4)
        If Plain.Length <> 16 Then Return ""
        Return String.Join("-", Enumerable.Range(0, 4).Select(Function(i) Plain.Substring(i * 4, 4)))
    End Function

    Private Function RequestJson(Url As String, Optional Method As HttpMethod = Nothing, Optional Body As String = Nothing) As JObject
        If Method Is Nothing Then Method = HttpMethod.Get
        Dim Raw As String
        Try
            Raw = NetRequestByClient(Url:=Url, Method:=Method, Content:=Body,
                                     ContentType:=If(Body Is Nothing, Nothing, "application/json"),
                                     Accept:="application/json", Timeout:=15000, RequireJson:=True)
        Catch ex As HttpRequestCodeException
            Throw ConvertError(ex)
        Catch ex As UpdateLicenseException
            Throw
        Catch ex As Exception
            If ex.IsBadNetwork Then
                Throw New UpdateLicenseException(ErrNetwork, "无法连接更新服务器，请检查网络或稍后重试")
            End If
            Throw New UpdateLicenseException(ErrNetwork, "无法连接更新服务器：" & ex.Message)
        End Try
        Return ParseEnvelope(Raw)
    End Function

    ''' <summary>把服务端返回的 {ok:true,data:{...}} 拆出 data。</summary>
    Private Function ParseEnvelope(Raw As String) As JObject
        Dim Root As JObject = Nothing
        Try
            Root = Raw.DeserializeJson()
        Catch ex As Exception
            Logger.Warn(ex, "解析更新服务器响应失败")
        End Try
        If Root Is Nothing Then
            Throw New UpdateLicenseException(ErrFormat, "更新服务器返回了无法识别的内容，请检查服务器地址是否正确")
        End If

        Dim OkToken = Root("ok")
        If OkToken Is Nothing OrElse OkToken.Type <> JTokenType.Boolean OrElse Not OkToken.Value(Of Boolean)() Then
            Dim Code As String = ""
            Dim Message As String = ""
            Dim ErrObj As JObject = TryCast(Root("error"), JObject)
            If ErrObj IsNot Nothing Then
                Code = If(ErrObj("code")?.ToString, "")
                Message = If(ErrObj("message")?.ToString, "")
            End If
            Throw New UpdateLicenseException(If(Code, "E_UNKNOWN"), If(Message, "更新服务器返回了失败结果"))
        End If

        Dim Data As JObject = TryCast(Root("data"), JObject)
        If Data Is Nothing Then Data = New JObject()
        Return Data
    End Function

    ''' <summary>HTTP 非 2xx 时，从响应体里取出服务端的错误码。</summary>
    Private Function ConvertError(Ex As HttpRequestCodeException) As UpdateLicenseException
        Dim Code As String = ""
        Dim Message As String = ""
        Dim Expire As Long = 0
        Try
            If Not String.IsNullOrEmpty(Ex.Response) Then
                Dim Root As JObject = Ex.Response.DeserializeJson()
                Dim ErrObj As JObject = If(Root Is Nothing, Nothing, TryCast(Root("error"), JObject))
                If ErrObj IsNot Nothing Then
                    Code = If(ErrObj("code")?.ToString, "")
                    Message = If(ErrObj("message")?.ToString, "")
                    Expire = JsonLong(ErrObj, "expire_at")
                End If
            End If
        Catch ex2 As Exception
            Logger.Warn(ex2, "解析更新服务器的错误响应失败")
        End Try
        If String.IsNullOrEmpty(Message) Then
            Message = $"更新服务器返回错误（{CInt(Ex.StatusCode)} {Ex.StatusCode}）"
        End If
        If String.IsNullOrEmpty(Code) Then Code = "E_HTTP_" & CInt(Ex.StatusCode)
        Return New UpdateLicenseException(Code, Message, Expire)
    End Function

    Private Function ParseLicense(Data As JObject) As LicenseInfo
        Return New LicenseInfo With {
            .Token = If(Data("token")?.ToString, ""),
            .ExpireAt = JsonLong(Data, "expire_at"),
            .CodeMask = If(Data("code_mask")?.ToString, ""),
            .RemainingDays = CInt(JsonLong(Data, "remaining_days")),
            .DeviceId = If(Data("device_id")?.ToString, "")
        }
    End Function

    Private Sub SaveLicense(Info As LicenseInfo)
        Try
            If Not String.IsNullOrEmpty(Info.Token) Then Settings.Set("UpdateLicenseToken", Info.Token)
            If Info.ExpireAt > 0 Then Settings.Set("UpdateLicenseExpire", Info.ExpireAt)
            If Not String.IsNullOrEmpty(Info.CodeMask) Then Settings.Set("UpdateLicenseMask", Info.CodeMask)
        Catch ex As Exception
            Logger.Warn(ex, "保存更新授权失败")
        End Try
    End Sub

    Private Function JsonLong(Parent As JObject, Key As String) As Long
        If Parent Is Nothing Then Return 0
        Dim Token As JToken = Parent(Key)
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return 0
        Try
            Return Token.Value(Of Long)()
        Catch ex As Exception
            Return 0L
        End Try
    End Function

#End Region

End Module
