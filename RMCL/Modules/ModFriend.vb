''' <summary>
''' 好友模块：账号、通讯录、群组、消息与会话的客户端封装。
'''
''' 数据来源：Friend Gateway v2（server-v2/），REST 做命令、WebSocket 做事件。
''' 客户端只维护一份状态，收到事件后由页面重绘，不再靠定时轮询猜变化。
''' </summary>
Public Module ModFriend

#Region "常量"

    ''' <summary>
    ''' 好友网关地址。部署 server-v2 后把这里改成实际地址（通常是 https://im.example.com）。
    ''' </summary>
    Public Const DefaultFriendServer As String = "https://im.mc.lxray.top"

    Private Const ApiPrefix As String = "/api/v1"

    ''' <summary>单个会话在本地最多保留的消息条数。</summary>
    Private Const LocalMessageLimit As Integer = 400

#End Region

#Region "状态"

    Public Class FriendApiException
        Inherits Exception
        Public Property Code As String
        Public Sub New(Code As String, Message As String)
            MyBase.New(Message)
            Me.Code = Code
        End Sub
    End Class

    ''' <summary>好友网关地址，去掉末尾斜杠。</summary>
    Public ReadOnly Property FriendServer As String
        Get
            Return DefaultFriendServer.TrimEnd("/"c)
        End Get
    End Property

    Public ReadOnly Property FriendToken As String
        Get
            Return Settings.Get(Of String)("FriendToken")
        End Get
    End Property

    Public ReadOnly Property IsLoggedIn As Boolean
        Get
            Return Not String.IsNullOrEmpty(FriendToken)
        End Get
    End Property

    Public ReadOnly Property MyId As Long
        Get
            Dim Obj As JObject = FriendSelf
            If Obj Is Nothing Then Return 0
            Return Obj.Value(Of Long)("id")
        End Get
    End Property

    Public ReadOnly Property MyName As String
        Get
            Dim Obj As JObject = FriendSelf
            If Obj Is Nothing Then Return ""
            Return Obj.Value(Of String)("nickname")
        End Get
    End Property

    Public ReadOnly Property MyEmail As String
        Get
            Dim Obj As JObject = FriendSelf
            If Obj Is Nothing Then Return ""
            Return Obj.Value(Of String)("email")
        End Get
    End Property

    '用户信息按原始 JSON 缓存，避免每次取 MyId 都重新解析
    Private CachedSelfRaw As String = Nothing
    Private CachedSelf As JObject = Nothing

    ''' <summary>当前登录用户信息（来自服务端）。</summary>
    Public ReadOnly Property FriendSelf As JObject
        Get
            Dim Raw As String = Settings.Get(Of String)("FriendSelfJson")
            If Raw = CachedSelfRaw Then Return CachedSelf
            CachedSelfRaw = Raw
            CachedSelf = Nothing
            If Not String.IsNullOrWhiteSpace(Raw) AndAlso Raw <> "{}" Then
                Try
                    CachedSelf = JObject.Parse(Raw)
                Catch
                    CachedSelf = Nothing
                End Try
            End If
            Return CachedSelf
        End Get
    End Property

    Public Sub FriendLoginSucceed(Token As String, User As JObject)
        Dim OldId As Long = 0
        Try
            OldId = MyId
        Catch
        End Try
        Settings.Set("FriendToken", Token)
        Settings.Set("FriendSelfJson", If(User Is Nothing, "{}", User.ToString(Formatting.None)))
        If User IsNot Nothing AndAlso User.Value(Of Long)("id") <> OldId Then
            '换账号后序号与本地缓存都要重置，避免把上一个账号的消息算进来
            Settings.Set("FriendLastSeq", 0L)
            Settings.Set("FriendLastMsgId", 0L)
        End If
        StartRealtime()
    End Sub

    ''' <summary>重新拉取当前账号信息。</summary>
    Public Sub RefreshSelf()
        If Not IsLoggedIn Then Return
        Dim Data As JObject = CallApi(HttpMethod.Get, "/me")
        If Data IsNot Nothing AndAlso Data("user") IsNot Nothing Then
            Settings.Set("FriendSelfJson", CType(Data("user"), JObject).ToString(Formatting.None))
        End If
    End Sub

    Public Sub FriendLogout()
        StopRealtime()
        Settings.Set("FriendToken", "")
        Settings.Set("FriendSelfJson", "{}")
        Settings.Set("FriendLastSeq", 0L)
        Settings.Set("FriendLastMsgId", 0L)
    End Sub

#End Region

#Region "HTTP"

    ''' <summary>调用网关。必须在工作线程调用。</summary>
    Private Function CallApi(Method As HttpMethod, Path As String, Optional Param As JObject = Nothing,
                             Optional NeedAuth As Boolean = True, Optional Timeout As Integer = 15000) As JObject
        Dim Url As String = FriendServer & ApiPrefix & Path
        Dim Body As String = Nothing
        If Method Is HttpMethod.Get Then
            If Param IsNot Nothing AndAlso Param.Count > 0 Then
                Dim Parts As New List(Of String)
                For Each Prop As JProperty In Param.Properties()
                    Dim Value As String = If(Prop.Value.Type = JTokenType.Null, "", Prop.Value.ToString())
                    Parts.Add(Uri.EscapeDataString(Prop.Name) & "=" & Uri.EscapeDataString(Value))
                Next
                Url &= If(Url.Contains("?"), "&", "?") & String.Join("&", Parts)
            End If
        Else
            Body = If(Param Is Nothing, "{}", Param.ToString(Formatting.None))
        End If

        Dim Headers(,) As String = Nothing
        If NeedAuth Then
            Dim Token As String = FriendToken
            If String.IsNullOrEmpty(Token) Then Throw New FriendApiException("E_TOKEN", "尚未登录好友账号")
            Headers = New String(,) {{"Authorization", "Bearer " & Token}}
        End If

        Dim Raw As String
        Try
            Raw = NetRequestByClient(Url, Method, Body, "application/json; charset=utf-8",
                                     "application/json", Timeout, Headers, RequireJson:=True)
        Catch ex As HttpRequestCodeException
            Dim Code As String = "E_HTTP"
            Dim Msg As String = $"好友服务返回 HTTP {CInt(ex.StatusCode)}"
            Try
                Dim ErrRoot As JObject = JObject.Parse(If(ex.Response, ""))
                Dim Err As JObject = TryCast(ErrRoot("error"), JObject)
                If Err IsNot Nothing Then
                    Code = If(String.IsNullOrEmpty(Err.Value(Of String)("code")), Code, Err.Value(Of String)("code"))
                    Msg = If(String.IsNullOrEmpty(Err.Value(Of String)("message")), Msg, Err.Value(Of String)("message"))
                End If
            Catch
                '错误体不是 JSON 时保留状态码文案
            End Try
            Throw New FriendApiException(Code, Msg)
        Catch ex As FormatException
            Throw New FriendApiException("E_BADJSON", "好友服务返回的不是 JSON，请确认网关已部署且地址正确")
        End Try

        Dim Obj As JObject
        Try
            Obj = JObject.Parse(Raw)
        Catch ex As Exception
            Throw New FriendApiException("E_BADJSON", "好友服务返回了无法解析的内容")
        End Try
        Return Obj
    End Function

    ''' <summary>
    ''' 兼容层：把旧的 action 名称映射到网关的 REST 路径。
    ''' 新代码请直接用 CallApi 或下面的强类型方法。
    ''' </summary>
    Public Function FriendApi(Action As String, Optional Param As JObject = Nothing,
                              Optional NeedAuth As Boolean = True, Optional Timeout As Integer = 15000) As JObject
        Select Case Action
            Case "send_code"
                Return CallApi(HttpMethod.Post, "/auth/send-code", Param, False, Timeout)
            Case "register"
                Return CallApi(HttpMethod.Post, "/auth/register", Param, False, Timeout)
            Case "login"
                Return CallApi(HttpMethod.Post, "/auth/login", Param, False, Timeout)
            Case "rayskin_login"
                Return CallApi(HttpMethod.Post, "/auth/rayskin", Param, False, Timeout)
            Case "me"
                Return CallApi(HttpMethod.Get, "/me")
            Case "contacts"
                Dim Data As JObject = CallApi(HttpMethod.Get, "/friends")
                Dim Items As New JArray()
                For Each Token As JToken In DirectCast(If(Data("items"), New JArray()), JArray)
                    Items.Add(ContactJson(CType(Token, JObject), "friend"))
                Next
                Return New JObject From {{"items", Items}}
            Case "conversations"
                Dim Data As JObject = CallApi(HttpMethod.Get, "/conversations")
                Dim Items As New JArray()
                For Each Token As JToken In DirectCast(If(Data("items"), New JArray()), JArray)
                    Dim Obj As JObject = CType(Token, JObject)
                    Obj("online") = False
                    Obj("friend_deleted") = False
                    Obj("roam") = False
                    Items.Add(Obj)
                Next
                Return New JObject From {{"items", Items}}
            Case "requests"
                Dim Data As JObject = CallApi(HttpMethod.Get, "/friend-requests", New JObject From {{"box", "in"}})
                Dim Items As New JArray()
                For Each Token As JToken In DirectCast(If(Data("items"), New JArray()), JArray)
                    Dim Obj As JObject = CType(Token, JObject)
                    Dim UserObj As JObject = TryCast(Obj("user"), JObject)
                    Items.Add(New JObject From {
                        {"id", Obj("id")},
                        {"from_name", If(UserObj Is Nothing, "", UserObj.Value(Of String)("nickname"))},
                        {"from_email", If(UserObj Is Nothing, "", UserObj.Value(Of String)("email"))},
                        {"message", Obj.Value(Of String)("wording")},
                        {"status", Obj.Value(Of String)("status")},
                        {"created_at", Obj("created_at")}
                    })
                Next
                Return New JObject From {{"items", Items}}
            Case "read"
                Dim ConvType As Integer = If(Param Is Nothing, 0, Param.Value(Of Integer)("conv_type"))
                Dim ConvKey As String = If(Param Is Nothing, "", Param.Value(Of String)("conv_key"))
                Return CallApi(HttpMethod.Post, $"/conversations/{ConvType}:{Uri.EscapeDataString(ConvKey)}/read")
            Case "friend_delete"
                Return CallApi(HttpMethod.Delete, "/friends/" & Param.Value(Of Long)("user_id"))
            Case "friend_accept"
                Return CallApi(HttpMethod.Post, $"/friend-requests/{Param.Value(Of Long)("request_id")}/accept")
            Case "friend_reject"
                Return CallApi(HttpMethod.Post, $"/friend-requests/{Param.Value(Of Long)("request_id")}/reject")
            Case "search"
                Dim Data As JObject = CallApi(HttpMethod.Get, "/search", New JObject From {{"q", Param.Value(Of String)("keyword")}})
                Dim Items As New JArray()
                For Each Token As JToken In DirectCast(If(Data("items"), New JArray()), JArray)
                    Items.Add(ContactJson(CType(Token, JObject), "none"))
                Next
                Return New JObject From {{"items", Items}}
            Case "add"
                Return CallApi(HttpMethod.Post, "/friend-requests", New JObject From {
                    {"to_id", Param.Value(Of Long)("user_id")},
                    {"wording", Param.Value(Of String)("message")}
                })
            Case "group_list"
                Return CallApi(HttpMethod.Get, "/groups")
            Case "group_create"
                Dim Data As JObject = CallApi(HttpMethod.Post, "/groups", New JObject From {
                    {"name", Param.Value(Of String)("name")},
                    {"members", If(Param("members"), New JArray())}
                })
                Return New JObject From {{"group", GroupJson(Data)}}
            Case "group_info"
                Return CallApi(HttpMethod.Get, "/groups/" & Param.Value(Of Long)("group_id"))
            Case "group_kick"
                Return CallApi(HttpMethod.Delete, $"/groups/{Param.Value(Of Long)("group_id")}/members/{Param.Value(Of Long)("user_id")}")
            Case "group_add_members"
                Return CallApi(HttpMethod.Post, $"/groups/{Param.Value(Of Long)("group_id")}/members",
                               New JObject From {{"members", If(Param("members"), New JArray())}})
            Case "group_leave"
                Return CallApi(HttpMethod.Delete, $"/groups/{Param.Value(Of Long)("group_id")}/members/{MyId}")
            Case "messages"
                Dim ConvType As Integer = Param.Value(Of Integer)("conv_type")
                Dim ConvKey As String = Param.Value(Of String)("conv_key")
                Return CallApi(HttpMethod.Get, "/messages", New JObject From {
                    {"conv", $"{ConvType}:{ConvKey}"},
                    {"before", Param.Value(Of Long)("before_id")},
                    {"limit", Param.Value(Of Integer)("limit")}
                })
            Case "send"
                Return CallApi(HttpMethod.Post, "/messages", New JObject From {
                    {"conv", $"{Param.Value(Of Integer)("conv_type")}:{Param.Value(Of String)("conv_key")}"},
                    {"body", Param.Value(Of String)("body")},
                    {"client_msg_id", Param.Value(Of String)("client_id")}
                }, True, Timeout)
            Case "recall"
                Return CallApi(HttpMethod.Post, "/messages/" & Param.Value(Of Long)("msg_id") & "/recall")
            Case "history"
                Dim Data As JObject = CallApi(HttpMethod.Get, "/history", New JObject From {
                    {"keyword", Param.Value(Of String)("keyword")},
                    {"scope", Param.Value(Of String)("scope")},
                    {"before", Param.Value(Of Long)("before_id")},
                    {"limit", Param.Value(Of Integer)("limit")}
                })
                Dim Items As JArray = DirectCast(If(Data("items"), New JArray()), JArray)
                Dim NextBefore As Long = 0
                For Each Token As JToken In Items
                    Dim Id As Long = Token.Value(Of Long)("id")
                    If NextBefore = 0 OrElse Id < NextBefore Then NextBefore = Id
                Next
                Return New JObject From {{"items", Items}, {"next_before", NextBefore}}
            Case Else
                Throw New FriendApiException("E_UNKNOWN", "未知的接口：" & Action)
        End Select
    End Function

    Private Function ContactJson(Obj As JObject, DefaultRelation As String) As JObject
        Return New JObject From {
            {"id", Obj("id")},
            {"email", Obj("email")},
            {"nickname", Obj("nickname")},
            {"remark", ""},
            {"relation", If(String.IsNullOrEmpty(Obj.Value(Of String)("relation")), DefaultRelation,
                            Obj.Value(Of String)("relation"))},
            {"online", False}
        }
    End Function

    Private Function GroupJson(Obj As JObject) As JObject
        If Obj("group") IsNot Nothing Then Obj = CType(Obj("group"), JObject)
        Return New JObject From {
            {"id", Obj("id")},
            {"name", Obj("name")},
            {"owner_id", Obj("owner_id")},
            {"member_count", Obj("member_count")},
            {"created_at", If(Obj("created_at") Is Nothing, 0, Obj("created_at"))}
        }
    End Function

    ''' <summary>把异常转成适合提示用户的文案。</summary>
    Public Function FriendErrorText(ex As Exception) As String
        Dim ApiEx As FriendApiException = TryCast(ex, FriendApiException)
        If ApiEx IsNot Nothing Then Return ApiEx.Message
        Dim NetEx As HttpRequestCodeException = TryCast(ex, HttpRequestCodeException)
        If NetEx IsNot Nothing Then Return $"好友服务返回 HTTP {CInt(NetEx.StatusCode)}"
        Return If(String.IsNullOrWhiteSpace(ex.Message), "网络请求失败", ex.Message)
    End Function

#End Region

#Region "实时连接"

    Private RealtimeThread As System.Threading.Thread = Nothing
    Private _RealtimeRunning As Boolean = False
    Private _RealtimeConnected As Boolean = False
    Private ReadOnly RealtimeQueue As New System.Collections.Concurrent.ConcurrentQueue(Of JObject)

    ''' <summary>收到推送时在后台线程触发，页面需自行切回 UI 线程。</summary>
    Public Event RealtimeActivity()

    Public ReadOnly Property RealtimeConnected As Boolean
        Get
            Return _RealtimeConnected
        End Get
    End Property

    Public Sub StartRealtime()
        If Not IsLoggedIn Then Return
        If _RealtimeRunning Then Return
        _RealtimeRunning = True
        RealtimeThread = New System.Threading.Thread(AddressOf RealtimeLoop) With {
            .IsBackground = True, .Name = "Friend Realtime"}
        RealtimeThread.Start()
    End Sub

    Public Sub StopRealtime()
        _RealtimeRunning = False
        _RealtimeConnected = False
    End Sub

    Private Sub RealtimeLoop()
        Dim Backoff As Integer = 1
        While _RealtimeRunning AndAlso IsLoggedIn
            Dim Socket As New System.Net.WebSockets.ClientWebSocket()
            Dim PingTimer As System.Threading.Timer = Nothing
            Try
                Dim Base As String = FriendServer.Replace("https://", "wss://").Replace("http://", "ws://")
                Dim WsUri As New Uri(Base & "/ws?token=" & Uri.EscapeDataString(FriendToken))
                Socket.ConnectAsync(WsUri, System.Threading.CancellationToken.None).GetAwaiter().GetResult()
                _RealtimeConnected = True
                Backoff = 1

                Dim ResumeMsg As New JObject From {{"op", "resume"}, {"since", Settings.Get(Of Long)("FriendLastSeq")}}
                SendSocketText(Socket, ResumeMsg.ToString(Formatting.None))
                PingTimer = New System.Threading.Timer(
                    Sub(state)
                        Try
                            SendSocketText(Socket, PingText)
                        Catch
                        End Try
                    End Sub, Nothing, 25000, 25000)

                Dim Buffer(8191) As Byte
                Dim Builder As New System.Text.StringBuilder()
                While _RealtimeRunning AndAlso Socket.State = System.Net.WebSockets.WebSocketState.Open
                    Dim Segment As New ArraySegment(Of Byte)(Buffer)
                    Dim Result = Socket.ReceiveAsync(Segment, System.Threading.CancellationToken.None).GetAwaiter().GetResult()
                    If Result.MessageType = System.Net.WebSockets.WebSocketMessageType.Close Then Exit While
                    Builder.Append(System.Text.Encoding.UTF8.GetString(Buffer, 0, Result.Count))
                    If Result.EndOfMessage Then
                        Dim Frame As String = Builder.ToString()
                        Builder.Clear()
                        HandleRealtimeFrame(Frame)
                    End If
                End While
            Catch ex As Exception
                Logger.Warn(ex, "好友实时连接断开")
            Finally
                _RealtimeConnected = False
                If PingTimer IsNot Nothing Then PingTimer.Dispose()
                Try
                    Socket.Dispose()
                Catch
                End Try
            End Try

            If Not _RealtimeRunning Then Exit While
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(Backoff))
            Backoff = Math.Min(30, Backoff * 2)
        End While
    End Sub

    Private Const PingText As String = "{""op"":""ping""}"

    Private Sub SendSocketText(Socket As System.Net.WebSockets.ClientWebSocket, Text As String)
        If Socket.State <> System.Net.WebSockets.WebSocketState.Open Then Return
        Dim Bytes As Byte() = System.Text.Encoding.UTF8.GetBytes(Text)
        Dim Segment As New ArraySegment(Of Byte)(Bytes)
        SyncLock Socket
            Socket.SendAsync(Segment, System.Net.WebSockets.WebSocketMessageType.Text, True,
                             System.Threading.CancellationToken.None).GetAwaiter().GetResult()
        End SyncLock
    End Sub

    Private Sub HandleRealtimeFrame(Frame As String)
        Dim Obj As JObject
        Try
            Obj = JObject.Parse(Frame)
        Catch
            Return
        End Try
        Dim Op As String = If(Obj.Value(Of String)("op"), "")
        If Op = "resync" Then
            Settings.Set("FriendLastSeq", 0L)
            RealtimeQueue.Enqueue(New JObject From {{"type", "resync"}})
            RaiseEvent RealtimeActivity()
            Return
        End If
        If Op <> "event" Then Return
        Dim Seq As Long = If(Obj("seq") Is Nothing, 0L, Obj.Value(Of Long)("seq"))
        If Seq > 0 Then Settings.Set("FriendLastSeq", Seq)
        RealtimeQueue.Enqueue(Obj)
        RaiseEvent RealtimeActivity()
    End Sub

#End Region

#Region "数据模型"

    Public Enum FriendLinkKind
        Invite
        Tcp
        Http
        Server
    End Enum

    Public Class FriendLink
        Public Kind As FriendLinkKind
        Public Value As String
        Public Label As String
    End Class

    Public Class FriendMessage
        Public Id As Long
        Public ConvType As Integer
        Public ConvKey As String
        Public FromId As Long
        Public FromName As String
        Public Body As String
        Public Kind As String = "text"
        Public CreatedAt As Long
        Public Pending As Boolean
        Public Failed As Boolean
        Public IsMine As Boolean

        ''' <summary>是否已被发送方撤回。</summary>
        Public ReadOnly Property Recalled As Boolean
            Get
                Return String.Equals(Kind, "recalled", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property
    End Class

    Public Class FriendConversation
        Public ConvType As Integer
        Public ConvKey As String
        Public Title As String
        Public LastBody As String
        Public LastAt As Long
        Public Unread As Integer
        Public MemberCount As Integer
        Public Online As Boolean
        ''' <summary>好友关系已解除（账号还在），会话仍保留，界面上标记为「已删除的好友」。</summary>
        Public FriendDeleted As Boolean
        ''' <summary>是否开启了聊天记录漫游。</summary>
        Public Roam As Boolean
        Public ReadOnly Property Key As String
            Get
                Return ConvType & "|" & ConvKey
            End Get
        End Property
    End Class

    Public Class FriendContact
        Public Id As Long
        Public Email As String
        Public Nickname As String
        Public Remark As String
        Public Relation As String
        Public Online As Boolean
    End Class

    Public Class FriendGroup
        Public Id As Long
        Public Name As String
        Public OwnerId As Long
        Public MemberCount As Integer
        Public CreatedAt As Long
    End Class

    Public Class FriendGroupMember
        Public Id As Long
        Public Email As String
        Public Nickname As String
        Public Role As Integer
    End Class

    Public Function ParseMessage(Obj As JObject) As FriendMessage
        Dim Item As New FriendMessage With {
            .Id = Obj.Value(Of Long)("id"),
            .ConvType = Obj.Value(Of Integer)("conv_type"),
            .ConvKey = Obj.Value(Of String)("conv_key"),
            .FromId = Obj.Value(Of Long)("from_id"),
            .FromName = Obj.Value(Of String)("from_name"),
            .Body = Obj.Value(Of String)("body"),
            .Kind = If(Obj.Value(Of String)("kind"), "text"),
            .CreatedAt = Obj.Value(Of Long)("created_at")
        }
        Item.IsMine = Item.FromId = MyId
        Return Item
    End Function

    Public Function ParseContact(Obj As JObject) As FriendContact
        Return New FriendContact With {
            .Id = Obj.Value(Of Long)("id"),
            .Email = Obj.Value(Of String)("email"),
            .Nickname = Obj.Value(Of String)("nickname"),
            .Remark = Obj.Value(Of String)("remark"),
            .Relation = Obj.Value(Of String)("relation"),
            .Online = Obj.Value(Of Boolean)("online")
        }
    End Function

    Public Function ParseGroup(Obj As JObject) As FriendGroup
        Return New FriendGroup With {
            .Id = Obj.Value(Of Long)("id"),
            .Name = Obj.Value(Of String)("name"),
            .OwnerId = Obj.Value(Of Long)("owner_id"),
            .MemberCount = Obj.Value(Of Integer)("member_count"),
            .CreatedAt = Obj.Value(Of Long)("created_at")
        }
    End Function

#End Region

#Region "业务接口"

    Public Function GetConversations() As List(Of FriendConversation)
        Dim Result As New List(Of FriendConversation)
        Dim Data As JObject = CallApi(HttpMethod.Get, "/conversations")
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Dim Obj As JObject = CType(Token, JObject)
            Result.Add(New FriendConversation With {
                .ConvType = Obj.Value(Of Integer)("conv_type"),
                .ConvKey = Obj.Value(Of String)("conv_key"),
                .Title = Obj.Value(Of String)("title"),
                .LastBody = Obj.Value(Of String)("last_body"),
                .LastAt = Obj.Value(Of Long)("last_at"),
                .Unread = Obj.Value(Of Integer)("unread"),
                .MemberCount = Obj.Value(Of Integer)("member_count"),
                .Online = False,
                .FriendDeleted = False,
                .Roam = False
            })
        Next
        Return Result
    End Function

    Public Function GetContacts() As List(Of FriendContact)
        Dim Result As New List(Of FriendContact)
        Dim Data As JObject = CallApi(HttpMethod.Get, "/friends")
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Result.Add(ParseContact(CType(Token, JObject)))
        Next
        Return Result
    End Function

    ''' <summary>拉取群成员（含角色，1 = 群主）。</summary>
    Public Function GetGroupMembers(GroupId As Long) As List(Of FriendGroupMember)
        Dim Result As New List(Of FriendGroupMember)
        Dim Data As JObject = CallApi(HttpMethod.Get, "/groups/" & GroupId)
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("members"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Dim Obj As JObject = CType(Token, JObject)
            Result.Add(New FriendGroupMember With {
                .Id = Obj.Value(Of Long)("id"),
                .Email = Obj.Value(Of String)("email"),
                .Nickname = Obj.Value(Of String)("nickname"),
                .Role = Obj.Value(Of Integer)("role")
            })
        Next
        Return Result
    End Function

    Public Function GetGroups() As List(Of FriendGroup)
        Dim Result As New List(Of FriendGroup)
        Dim Data As JObject = CallApi(HttpMethod.Get, "/groups")
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Result.Add(ParseGroup(CType(Token, JObject)))
        Next
        Return Result
    End Function

    ''' <summary>拉取某个会话的历史消息（BeforeId 为 0 表示最新一页）。</summary>
    Public Function GetMessages(ConvType As Integer, ConvKey As String,
                                Optional BeforeId As Long = 0, Optional Limit As Integer = 50) As List(Of FriendMessage)
        Dim Result As New List(Of FriendMessage)
        Dim Param As New JObject From {
            {"conv", $"{ConvType}:{ConvKey}"}, {"before", BeforeId}, {"limit", Limit}
        }
        Dim Data As JObject = CallApi(HttpMethod.Get, "/messages", Param)
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Result.Add(ParseMessage(CType(Token, JObject)))
        Next
        Return Result
    End Function

    ''' <summary>发送消息。返回服务端落库后的消息。</summary>
    Public Function SendMessage(ConvType As Integer, ConvKey As String, Body As String) As FriendMessage
        Dim Param As New JObject From {
            {"conv", $"{ConvType}:{ConvKey}"},
            {"body", Body},
            {"client_msg_id", Guid.NewGuid().ToString("N")}
        }
        Dim Data As JObject = CallApi(HttpMethod.Post, "/messages", Param, True, 20000)
        If Data Is Nothing OrElse Data("message") Is Nothing Then
            Throw New FriendApiException("E_SEND", "消息发送失败")
        End If
        Dim Msg As FriendMessage = ParseMessage(CType(Data("message"), JObject))
        LocalAppend(Msg)
        Return Msg
    End Function

    ''' <summary>一次推送处理的结果：新消息、被撤回的消息 ID、是否需要全量重拉。</summary>
    Public Class FriendPollResult
        Public ReadOnly Messages As New List(Of FriendMessage)
        Public ReadOnly Recalls As New List(Of Long)
        Public Resync As Boolean
    End Class

    ''' <summary>
    ''' 取出实时连接缓存的事件。不再发网络请求，所以可以在 UI 线程调用。
    ''' </summary>
    Public Function Poll() As FriendPollResult
        Dim Result As New FriendPollResult()
        If Not _RealtimeRunning Then StartRealtime()
        Dim Obj As JObject = Nothing
        Do While RealtimeQueue.TryDequeue(Obj)
            Dim Kind As String = If(Obj.Value(Of String)("type"), "")
            Dim Data As JObject = TryCast(Obj("data"), JObject)
            Select Case Kind
                Case "message.new"
                    If Data IsNot Nothing AndAlso Data("message") IsNot Nothing Then
                        Dim Msg As FriendMessage = ParseMessage(CType(Data("message"), JObject))
                        Result.Messages.Add(Msg)
                        LocalAppend(Msg)
                    End If
                Case "message.recalled"
                    If Data IsNot Nothing Then
                        Dim Id As Long = Data.Value(Of Long)("message_id")
                        Result.Recalls.Add(Id)
                        LocalRecall(Id)
                    End If
                Case "resync"
                    Result.Resync = True
            End Select
        Loop
        Return Result
    End Function

    ''' <summary>撤回自己发的消息（服务端限制 2 分钟内）。</summary>
    Public Sub RecallMessage(MsgId As Long)
        If MsgId <= 0 Then Return
        CallApi(HttpMethod.Post, "/messages/" & MsgId & "/recall")
        LocalRecall(MsgId)
    End Sub

    ''' <summary>已读上报（清红点）。</summary>
    Public Sub MarkRead(ConvType As Integer, ConvKey As String)
        CallApi(HttpMethod.Post, $"/conversations/{ConvType}:{Uri.EscapeDataString(ConvKey)}/read")
    End Sub

    ''' <summary>删除会话（只从列表移除，消息按会话历史保留）。</summary>
    Public Sub DeleteConversation(ConvType As Integer, ConvKey As String)
        CallApi(HttpMethod.Delete, $"/conversations/{ConvType}:{Uri.EscapeDataString(ConvKey)}")
        LocalClearConversation(ConvType, ConvKey)
    End Sub

#End Region

#Region "本地缓存"

    Private ReadOnly LocalLock As New Object
    Private ReadOnly LocalCache As New Dictionary(Of String, List(Of FriendMessage))
    Private ReadOnly LocalDirty As New HashSet(Of String)

    Private Function LocalKey(ConvType As Integer, ConvKey As String) As String
        '按账号隔离，避免同一台电脑上换号后看到上一个账号的聊天缓存
        Return MyId & "_" & ConvType & "_" & New String(ConvKey.Where(Function(c) Char.IsLetterOrDigit(c)).ToArray())
    End Function

    ''' <summary>
    ''' 聊天记录目录。放在启动器自己的数据目录下，而不是 %TEMP%——
    ''' 「设置 - 功能 - 清理游戏垃圾」会清空 PathTemp，放那里会被一起删掉。
    ''' </summary>
    Public ReadOnly Property LocalFolder As String
        Get
            Return Paths.Base & "RMCL\Friend\"
        End Get
    End Property

    Private Function LocalPath(ConvType As Integer, ConvKey As String) As String
        Return LocalFolder & "cache\" & LocalKey(ConvType, ConvKey) & ".json"
    End Function

    ''' <summary>旧版本把聊天记录放在临时目录，读取时顺手迁移过来。</summary>
    Private Function LegacyLocalPath(ConvType As Integer, ConvKey As String) As String
        Return PathTemp & "Friend\cache\" & LocalKey(ConvType, ConvKey) & ".json"
    End Function

    ''' <summary>
    ''' 载入某个会话的本地缓存（同步、极快，只有几十 KB）。
    ''' </summary>
    Public Function LocalLoad(ConvType As Integer, ConvKey As String) As List(Of FriendMessage)
        SyncLock LocalLock
            Dim Key As String = LocalKey(ConvType, ConvKey)
            Dim Cached As List(Of FriendMessage) = Nothing
            If LocalCache.TryGetValue(Key, Cached) Then Return New List(Of FriendMessage)(Cached)

            Dim Result As New List(Of FriendMessage)
            Try
                Dim File As String = LocalPath(ConvType, ConvKey)
                If Not FileUtils.Exists(File) Then
                    '旧版本存在 %TEMP%\RMCL\Friend 下，复制到新位置，避免升级后历史记录看起来丢失
                    Dim Legacy As String = LegacyLocalPath(ConvType, ConvKey)
                    If FileUtils.Exists(Legacy) Then
                        Try
                            Directory.CreateDirectory(LocalFolder & "cache")
                            FileUtils.Write(File, FileUtils.TryReadAsString(Legacy))
                            Logger.Info($"已迁移旧的聊天记录缓存：{Legacy}")
                        Catch ex As Exception
                            Logger.Warn(ex, "迁移旧聊天记录缓存失败")
                        End Try
                    End If
                End If
                If FileUtils.Exists(File) Then
                    Dim Arr As JArray = JArray.Parse(FileUtils.TryReadAsString(File))
                    For Each Token As JToken In Arr
                        Dim Obj As JObject = CType(Token, JObject)
                        Dim Msg As FriendMessage = ParseMessage(Obj)
                        Msg.IsMine = Msg.FromId = MyId
                        Result.Add(Msg)
                    Next
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"读取好友聊天本地缓存失败（{ConvKey}）")
            End Try
            Result.Sort(Function(a, b) a.Id.CompareTo(b.Id))
            LocalCache(Key) = Result
            Return New List(Of FriendMessage)(Result)
        End SyncLock
    End Function

    ''' <summary>
    ''' 把消息追加进本地缓存（内存）。真正的落盘由 LocalFlush 批量完成。
    ''' </summary>
    Public Sub LocalAppend(Msg As FriendMessage)
        If Msg Is Nothing OrElse String.IsNullOrEmpty(Msg.ConvKey) Then Return
        SyncLock LocalLock
            Dim Key As String = LocalKey(Msg.ConvType, Msg.ConvKey)
            Dim List As List(Of FriendMessage) = Nothing
            If Not LocalCache.TryGetValue(Key, List) Then
                List = New List(Of FriendMessage)
                LocalCache(Key) = List
            End If
            If Msg.Id > 0 AndAlso List.Any(Function(m) m.Id = Msg.Id) Then Return
            '本地待发送的消息（Id=0）用 client_id 之外的引用去重：同一条引用只加一次
            If Msg.Id = 0 AndAlso List.Any(Function(m) m Is Msg) Then Return
            List.Add(Msg)
            List.Sort(Function(a, b) a.Id.CompareTo(b.Id))
            If List.Count > LocalMessageLimit Then List.RemoveRange(0, List.Count - LocalMessageLimit)
            LocalDirty.Add(Key)
        End SyncLock
    End Sub

    ''' <summary>
    ''' 批量把消息并入本地缓存并落盘。用于打开会话时把服务端拉到的历史补进本机。
    ''' </summary>
    Public Sub LocalAppendRange(List As IEnumerable(Of FriendMessage))
        If List Is Nothing Then Return
        Dim Any As Boolean = False
        For Each Msg As FriendMessage In List
            If Msg Is Nothing OrElse Msg.Id <= 0 Then Continue For
            If IsDeleted(Msg.Id) Then Continue For
            LocalAppend(Msg)
            Any = True
        Next
        If Any Then LocalFlush()
    End Sub

    ''' <summary>
    ''' 把内存中的改动批量写入磁盘。可在页面退出或轮询后调用，避免每条消息都写文件。
    ''' </summary>
    Public Sub LocalFlush()
        SyncLock LocalLock
            If LocalDirty.Count = 0 Then Return
            Try
                Dim Dir As String = LocalFolder & "cache"
                If Not Directory.Exists(Dir) Then Directory.CreateDirectory(Dir)
                For Each Key As String In LocalDirty.ToList()
                    Dim List As List(Of FriendMessage) = Nothing
                    If Not LocalCache.TryGetValue(Key, List) Then Continue For
                    Dim Arr As New JArray()
                    For Each Msg As FriendMessage In List
                        If Msg.Id <= 0 Then Continue For '还没发出去的消息不落盘
                        Arr.Add(New JObject From {
                            {"id", Msg.Id}, {"conv_type", Msg.ConvType}, {"conv_key", Msg.ConvKey},
                            {"from_id", Msg.FromId}, {"from_name", Msg.FromName}, {"body", Msg.Body},
                            {"kind", Msg.Kind}, {"created_at", Msg.CreatedAt}
                        })
                    Next
                    FileUtils.Write(LocalFolder & "cache\" & Key & ".json", Arr.ToString(Formatting.None))
                Next
                LocalDirty.Clear()
            Catch ex As Exception
                Logger.Warn(ex, "写入好友聊天本地缓存失败")
            End Try
        End SyncLock
    End Sub

    ''' <summary>只清掉某个会话的本机缓存文件。</summary>
    Public Sub LocalClearConversation(ConvType As Integer, ConvKey As String)
        SyncLock LocalLock
            Dim Key As String = LocalKey(ConvType, ConvKey)
            LocalCache.Remove(Key)
            LocalDirty.Remove(Key)
            Try
                Dim File As String = LocalPath(ConvType, ConvKey)
                If FileUtils.Exists(File) Then FileUtils.Delete(File)
            Catch ex As Exception
                Logger.Warn(ex, "删除本机聊天缓存失败")
            End Try
        End SyncLock
    End Sub

    ''' <summary>把缓存里的某条消息标记为已撤回。</summary>
    Public Sub LocalRecall(MsgId As Long)
        If MsgId <= 0 Then Return
        SyncLock LocalLock
            For Each Pair As KeyValuePair(Of String, List(Of FriendMessage)) In LocalCache
                Dim Target As FriendMessage = Pair.Value.FirstOrDefault(Function(m) m.Id = MsgId)
                If Target IsNot Nothing AndAlso Not Target.Recalled Then
                    Target.Kind = "recalled"
                    Target.Body = ""
                    LocalDirty.Add(Pair.Key)
                End If
            Next
        End SyncLock
    End Sub

    ''' <summary>从本地缓存里删掉某条消息（只影响本机，不动服务端记录）。</summary>
    Public Sub LocalRemove(MsgId As Long)
        If MsgId <= 0 Then Return
        MarkDeleted(MsgId)
        SyncLock LocalLock
            For Each Pair As KeyValuePair(Of String, List(Of FriendMessage)) In LocalCache
                If Pair.Value.RemoveAll(Function(m) m.Id = MsgId) > 0 Then LocalDirty.Add(Pair.Key)
            Next
        End SyncLock
    End Sub

#Region "本地已删除的消息"

    '本地手动删掉的消息 id。服务端仍保留着备份，恢复/合并时要跳过，否则会被「复活」
    Private DeletedIds As HashSet(Of Long) = Nothing
    Private ReadOnly DeletedLock As New Object
    Private Const DeletedLimit As Integer = 2000

    Private Sub EnsureDeletedLoaded()
        If DeletedIds IsNot Nothing Then Return
        DeletedIds = New HashSet(Of Long)()
        Try
            Dim Raw As String = Settings.Get(Of String)("FriendDeletedIds")
            If Not String.IsNullOrWhiteSpace(Raw) Then
                For Each Token As JToken In JArray.Parse(Raw)
                    DeletedIds.Add(Token.Value(Of Long)())
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取已删除消息列表失败")
        End Try
    End Sub

    ''' <summary>记住某条消息已在本地删除。</summary>
    Public Sub MarkDeleted(MsgId As Long)
        If MsgId <= 0 Then Return
        SyncLock DeletedLock
            EnsureDeletedLoaded()
            If Not DeletedIds.Add(MsgId) Then Return
            '超过上限时只保留最近的一半，避免设置项无限膨胀
            Dim Keep As List(Of Long) = DeletedIds.OrderByDescending(Function(x) x).Take(DeletedLimit).ToList()
            DeletedIds.Clear()
            For Each Id As Long In Keep
                DeletedIds.Add(Id)
            Next
            Dim Arr As New JArray()
            For Each Id As Long In Keep
                Arr.Add(Id)
            Next
            Settings.Set("FriendDeletedIds", Arr.ToString(Formatting.None))
        End SyncLock
    End Sub

    ''' <summary>这条消息是不是被本机删过（恢复与合并时要跳过）。</summary>
    Public Function IsDeleted(MsgId As Long) As Boolean
        If MsgId <= 0 Then Return False
        SyncLock DeletedLock
            EnsureDeletedLoaded()
            Return DeletedIds.Contains(MsgId)
        End SyncLock
    End Function

    Public Function DeletedSnapshot() As List(Of Long)
        SyncLock DeletedLock
            EnsureDeletedLoaded()
            Return DeletedIds.ToList()
        End SyncLock
    End Function

#End Region

#End Region

    ''' <summary>清空内存缓存（不删磁盘）。</summary>
    Public Sub LocalClear()
        SyncLock LocalLock
            LocalCache.Clear()
            LocalDirty.Clear()
        End SyncLock
    End Sub

    ''' <summary>清除本机保存的聊天记录（内存 + 磁盘）。服务端历史不受影响。</summary>
    Public Function LocalClearAll() As Boolean
        LocalClear()
        Try
            Dim Dir As String = LocalFolder & "cache"
            If Directory.Exists(Dir) Then Directory.Delete(Dir, True)
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "清除本地聊天记录失败")
            Return False
        End Try
    End Function

    ''' <summary>本机聊天记录的存放目录，界面上用于告知用户。</summary>
    Public ReadOnly Property LocalFolderForDisplay As String
        Get
            Return LocalFolder & "cache"
        End Get
    End Property

#Region "漫游（已下线）"

    '体验版自带云端漫游，不再由客户端上传/恢复，这里保留空实现避免旧页面编译失败。

    Public Class RoamUploadResult
        Public LocalCount As Integer
        Public Added As Integer
    End Class

    Public Function SetRoam(ConvType As Integer, ConvKey As String, Enabled As Boolean) As Integer
        Return 0
    End Function

    Public Function UploadRoam(ConvType As Integer, ConvKey As String) As RoamUploadResult
        Return New RoamUploadResult()
    End Function

    Public Function RestoreRoam(ConvType As Integer, ConvKey As String) As Integer
        Return 0
    End Function

    Public Function RestoreLocalHistory(Optional SinceId As Long = 0) As Integer
        Return 0
    End Function

#End Region

#Region "链接识别与导入"

    Private ReadOnly InviteRegex As New Text.RegularExpressions.Regex(
        "P[0-9A-Fa-f]{4}-[0-9A-Za-z]{5}-[0-9A-Za-z]{5}", Text.RegularExpressions.RegexOptions.Compiled)
    Private ReadOnly TcpRegex As New Text.RegularExpressions.Regex(
        "(?:tcp|udp|ws|wss|quic|wg|faketcp|ring)://[^\s<>""']+", Text.RegularExpressions.RegexOptions.Compiled Or Text.RegularExpressions.RegexOptions.IgnoreCase)
    Private ReadOnly HttpRegex As New Text.RegularExpressions.Regex(
        "https?://[^\s<>""']+", Text.RegularExpressions.RegexOptions.Compiled Or Text.RegularExpressions.RegexOptions.IgnoreCase)
    'frp-xxx.com:12345 这类穿透地址，也当作可以直接进入的联机服务器
    Private ReadOnly FrpRegex As New Text.RegularExpressions.Regex(
        "\bfrp-[a-z0-9][a-z0-9\-]*\.[a-z]{2,10}:\d{2,5}\b", Text.RegularExpressions.RegexOptions.Compiled Or Text.RegularExpressions.RegexOptions.IgnoreCase)

    ''' <summary>
    ''' 从一段聊天文本里识别可以一键导入的内容：联机邀请码、tcp 节点、HTTP(S) 服务器地址。
    ''' 每类最多取一个，避免一条消息里堆一排按钮。
    ''' </summary>
    Public Function DetectLinks(Text As String) As List(Of FriendLink)
        Dim Result As New List(Of FriendLink)
        If String.IsNullOrWhiteSpace(Text) Then Return Result

        Dim Invite = InviteRegex.Match(Text)
        If Invite.Success Then
            Result.Add(New FriendLink With {.Kind = FriendLinkKind.Invite, .Value = Invite.Value.ToUpper(), .Label = "导入邀请码"})
        End If

        Dim Tcp = TcpRegex.Match(Text)
        Dim TcpValue As String = ""
        If Tcp.Success Then
            TcpValue = Tcp.Value.TrimEnd(")"c, "。"c, "，"c, ","c, "."c)
            Result.Add(New FriendLink With {.Kind = FriendLinkKind.Tcp, .Value = TcpValue, .Label = "使用该节点"})
        End If

        Dim Frp = FrpRegex.Match(Text)
        Dim FrpValue As String = ""
        If Frp.Success Then
            FrpValue = Frp.Value
            Result.Add(New FriendLink With {.Kind = FriendLinkKind.Server, .Value = FrpValue, .Label = "进入服务器"})
        End If

        For Each Match As Text.RegularExpressions.Match In HttpRegex.Matches(Text)
            Dim Url As String = Match.Value.TrimEnd(")"c, "。"c, "，"c, ","c, "."c)
            If TcpValue <> "" AndAlso Url = TcpValue Then Continue For
            '同一个 frp 地址已经被单独识别过，避免出现两个按钮
            If FrpValue <> "" AndAlso Url.Contains(FrpValue) Then Continue For
            '消息里可能同时贴了多个链接；只取第一个可用的
            Result.Add(New FriendLink With {.Kind = FriendLinkKind.Http, .Value = Url, .Label = "打开服务器"})
            Exit For
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 执行导入。必须在 UI 线程调用。
    ''' </summary>
    Public Sub ImportLink(Link As FriendLink)
        If Link Is Nothing Then Return
        Select Case Link.Kind
            Case FriendLinkKind.Invite
                '先选版本，再进房间；进入后仍然可以在完成页换版本
                Dim Instance As McInstance = AskInstance("选择加入房间要用的版本")
                If Instance Is Nothing Then Return
                Settings.Set("LinkLaunchVersion", Instance.Name)
                PageLinkMain.Join(Link.Value)
                Hint("正在使用邀请码加入房间……", HintType.Green, False)

            Case FriendLinkKind.Tcp
                Settings.Set("LinkCustomPeer", Link.Value)
                If FrmLinkMain IsNot Nothing AndAlso FrmLinkMain.IsLoaded Then
                    FrmLinkMain.TextCustomPeer.Text = Link.Value
                End If
                FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLink)
                Hint("已切换为自定义节点：" & Link.Value, HintType.Green)

            Case FriendLinkKind.Http
                ImportServerAddress(ParseServerAddress(Link.Value), Link.Value)

            Case FriendLinkKind.Server
                ImportServerAddress(Link.Value, Link.Value)
        End Select
    End Sub

    ''' <summary>
    ''' 从 HTTP(S) 链接里取出 host:port。
    ''' </summary>
    Private Function ParseServerAddress(Url As String) As String
        Dim UriObj As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, UriObj) OrElse String.IsNullOrEmpty(UriObj.Host) Then Return ""
        Dim Address As String = UriObj.Host
        If UriObj.Port > 0 AndAlso UriObj.Port <> 80 AndAlso UriObj.Port <> 443 Then Address &= ":" & UriObj.Port
        Return Address
    End Function

    ''' <summary>
    ''' 选择游戏版本后直接启动并进入指定服务器。
    ''' </summary>
    Private Sub ImportServerAddress(Address As String, RawUrl As String)
        If String.IsNullOrEmpty(Address) Then
            Hint("链接格式无法识别：" & RawUrl, HintType.Red)
            Return
        End If
        Dim Instance As McInstance = AskInstance("选择启动版本")
        If Instance Is Nothing Then Return
        If MyMsgBox($"将使用「{Instance.Name}」启动游戏，并自动进入 {Address}。", "打开服务器", "启动", "取消") <> 1 Then Return
        FrmMain.PageChange(FormMain.PageType.Launch)
        RunInUi(Sub()
                    McLaunchStart(New McLaunchOptions With {.Instance = Instance, .ServerIp = Address})
                End Sub)
    End Sub

    ''' <summary>
    ''' 弹出版本选择框。必须在 UI 线程调用。
    ''' </summary>
    Public Function AskInstance(Title As String) As McInstance
        If McInstanceList Is Nothing OrElse Not McInstanceList.Any Then
            If McInstanceListLoader.State <> LoadState.Loading Then
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            End If
            Hint("正在加载版本列表，请稍后再试", HintType.Blue)
            Return Nothing
        End If
        Dim Instances As List(Of McInstance) =
            McInstanceList.SelectMany(Function(kv) kv.Value).
                Where(Function(i) i IsNot Nothing AndAlso i.State <> McInstance.McInstanceState.Error).
                ToList()
        If Instances.Count = 0 Then
            Hint("还没有安装任何游戏版本，无法继续", HintType.Red)
            Return Nothing
        End If
        Dim Options As New List(Of IMyRadio)
        For i = 0 To Instances.Count - 1
            Options.Add(New MyRadioBox With {.Text = $"{Instances(i).Name}（{Instances(i).Info}）", .Tag = i})
        Next
        Dim Selected As Integer? = MyMsgBoxSelect(Options, Title, "使用该版本", "取消")
        If Selected Is Nothing Then Return Nothing
        Dim Index As Integer = CInt(Selected)
        If Index < 0 OrElse Index >= Instances.Count Then Return Nothing
        Return Instances(Index)
    End Function

#End Region

End Module
