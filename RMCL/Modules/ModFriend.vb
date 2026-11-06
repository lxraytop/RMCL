''' <summary>
''' 好友模块：账号、通讯录、群组、消息与会话的客户端封装。
'''
''' 设计原则（与需求「最大性能、最小占用」对应）：
''' - 所有网络请求都只在工作线程发起，绝不在 UI 线程等待；
''' - 消息统一走自建服务端的增量接口（since_id），一次轮询拉全部会话的新消息；
''' - 会话/联系人/群组只在需要时拉取，且带内存缓存；
''' - 历史消息按会话落盘缓存，切会话时先显示本地缓存再补拉；
''' - 聊天记录不会随窗口关闭而丢失，账号页提供「恢复本地聊天记录」。
''' </summary>
Public Module ModFriend

#Region "常量"

    ''' <summary>
    ''' 默认好友服务地址。部署 server/ 后，把这里改成实际地址即可；
    ''' 用户也可以在「设置 → 功能 → 好友页面」里覆盖。
    ''' </summary>
    Public Const DefaultFriendServer As String = "https://update.mc.lxray.top/"

    ''' <summary>
    ''' 单个会话在本地最多保留的消息条数。
    ''' </summary>
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

    ''' <summary>
    ''' 好友服务地址，去掉末尾斜杠。
    ''' </summary>
    ''' <summary>
    ''' 好友服务地址固定为内置值，不读取设置，也不在界面上展示。
    ''' </summary>
    Public ReadOnly Property FriendServer As String
        Get
            Return DefaultFriendServer
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

    ''' <summary>
    ''' 当前登录用户信息（来自服务端）。
    ''' </summary>
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
        Dim OldId As Long = MyId
        Settings.Set("FriendToken", Token)
        Settings.Set("FriendSelfJson", If(User Is Nothing, "{}", User.ToString(Formatting.None)))
        '换账号时重置增量指针，避免把上一个账号的消息算进来
        If User IsNot Nothing AndAlso User.Value(Of Long)("id") <> OldId Then Settings.Set("FriendLastMsgId", 0L)
    End Sub

    ''' <summary>
    ''' 重新拉取当前账号信息（邀请码用量、昵称等）。
    ''' </summary>
    Public Sub RefreshSelf()
        If Not IsLoggedIn Then Return
        Dim Data As JObject = FriendApi("me")
        If Data IsNot Nothing AndAlso Data("user") IsNot Nothing Then
            Settings.Set("FriendSelfJson", CType(Data("user"), JObject).ToString(Formatting.None))
        End If
    End Sub

    Public Sub FriendLogout()
        Settings.Set("FriendToken", "")
        Settings.Set("FriendSelfJson", "{}")
        Settings.Set("FriendLastMsgId", 0L)
    End Sub

#End Region

#Region "网络"

    ''' <summary>
    ''' 调用好友服务端接口。必须在工作线程调用。
    ''' </summary>
    Public Function FriendApi(Method As String, Optional Param As JObject = Nothing,
                              Optional NeedAuth As Boolean = True, Optional Timeout As Integer = 15000) As JObject
        Dim Url As String = FriendServer
        If Not Url.EndsWith("index.php", StringComparison.OrdinalIgnoreCase) Then Url &= "/index.php"
        Url &= "?action=friend_" & Method

        Dim Headers(,) As String = Nothing
        If NeedAuth Then
            Dim Token As String = FriendToken
            If String.IsNullOrEmpty(Token) Then Throw New FriendApiException("E_TOKEN", "尚未登录好友账号")
            Headers = New String(,) {{"X-Friend-Token", Token}}
        End If

        Dim Body As String = If(Param Is Nothing, "{}", Param.ToString(Formatting.None))
        Dim Raw As String
        Try
            Raw = NetRequestByClient(Url, HttpMethod.Post, Body, "application/json; charset=utf-8",
                                     "application/json", Timeout, Headers, RequireJson:=True)
        Catch ex As HttpRequestCodeException
            Throw New FriendApiException("E_HTTP", $"好友服务返回 HTTP {CInt(ex.StatusCode)}，请确认服务端已部署最新版本")
        Catch ex As FormatException
            Throw New FriendApiException("E_BADJSON", "好友服务返回的不是 JSON，服务端可能还没有部署好友功能")
        End Try
        Dim Obj As JObject
        Try
            Obj = JObject.Parse(Raw)
        Catch ex As Exception
            Throw New FriendApiException("E_BADJSON", "好友服务返回了无法解析的内容")
        End Try

        If Obj("ok") IsNot Nothing AndAlso Not Obj.Value(Of Boolean)("ok") Then
            Dim Err As JObject = TryCast(Obj("error"), JObject)
            If Err Is Nothing Then Throw New FriendApiException("E_UNKNOWN", "好友服务请求失败")
            Throw New FriendApiException(Err.Value(Of String)("code"), Err.Value(Of String)("message"))
        End If
        Return TryCast(Obj("data"), JObject)
    End Function

    ''' <summary>
    ''' 把异常转成适合提示用户的文案。
    ''' </summary>
    Public Function FriendErrorText(ex As Exception) As String
        Dim ApiEx As FriendApiException = TryCast(ex, FriendApiException)
        If ApiEx IsNot Nothing Then Return ApiEx.Message
        Dim NetEx As HttpRequestCodeException = TryCast(ex, HttpRequestCodeException)
        If NetEx IsNot Nothing Then Return $"好友服务返回 HTTP {CInt(NetEx.StatusCode)}"
        Return If(String.IsNullOrWhiteSpace(ex.Message), "网络请求失败", ex.Message)
    End Function

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
        Dim Data As JObject = FriendApi("conversations")
        If Data Is Nothing Then Return Result
        Dim Arr As JArray = TryCast(Data("items"), JArray)
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
                .Online = Obj.Value(Of Boolean)("online")
            })
        Next
        Return Result
    End Function

    Public Function GetContacts() As List(Of FriendContact)
        Dim Result As New List(Of FriendContact)
        Dim Data As JObject = FriendApi("contacts")
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Result.Add(ParseContact(CType(Token, JObject)))
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 拉取群成员（含角色，1 = 群主）。
    ''' </summary>
    Public Function GetGroupMembers(GroupId As Long) As List(Of FriendGroupMember)
        Dim Result As New List(Of FriendGroupMember)
        Dim Data As JObject = FriendApi("group_info", New JObject From {{"group_id", GroupId}})
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
        Dim Data As JObject = FriendApi("group_list")
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Result.Add(ParseGroup(CType(Token, JObject)))
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 拉取某个会话的历史消息（before_id 为 0 表示最新一页）。
    ''' </summary>
    Public Function GetMessages(ConvType As Integer, ConvKey As String, Optional BeforeId As Long = 0, Optional Limit As Integer = 50) As List(Of FriendMessage)
        Dim Result As New List(Of FriendMessage)
        Dim Param As New JObject From {{"conv_type", ConvType}, {"conv_key", ConvKey}, {"before_id", BeforeId}, {"limit", Limit}}
        Dim Data As JObject = FriendApi("messages", Param)
        Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Result.Add(ParseMessage(CType(Token, JObject)))
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 发送消息。返回服务端落库后的消息。
    ''' </summary>
    Public Function SendMessage(ConvType As Integer, ConvKey As String, Body As String) As FriendMessage
        Dim Param As New JObject From {
            {"conv_type", ConvType}, {"conv_key", ConvKey}, {"body", Body},
            {"client_id", Guid.NewGuid().ToString("N")}
        }
        Dim Data As JObject = FriendApi("send", Param, True, 20000)
        If Data Is Nothing OrElse Data("message") Is Nothing Then Throw New FriendApiException("E_SEND", "消息发送失败")
        Dim Msg As FriendMessage = ParseMessage(CType(Data("message"), JObject))
        LocalAppend(Msg)
        Return Msg
    End Function

    ''' <summary>一次轮询的结果：新消息 + 被撤回的消息 ID。</summary>
    Public Class FriendPollResult
        Public ReadOnly Messages As New List(Of FriendMessage)
        Public ReadOnly Recalls As New List(Of Long)
    End Class

    ''' <summary>
    ''' 增量拉取新消息。返回本次新增的消息（可能来自多个会话）与撤回事件。
    ''' </summary>
    Public Function Poll() As FriendPollResult
        Dim Result As New FriendPollResult()
        Dim Since As Long = Settings.Get(Of Long)("FriendLastMsgId")
        Dim Param As New JObject From {{"since_id", Since}, {"limit", 100}}
        Dim Data As JObject = FriendApi("poll", Param, True, 20000)
        If Data Is Nothing Then Return Result
        Dim Arr As JArray = TryCast(Data("messages"), JArray)
        If Arr IsNot Nothing Then
            For Each Token As JToken In Arr
                Dim Msg As FriendMessage = ParseMessage(CType(Token, JObject))
                Result.Messages.Add(Msg)
                LocalAppend(Msg)
            Next
        End If
        Dim RecallArr As JArray = TryCast(Data("recalls"), JArray)
        If RecallArr IsNot Nothing Then
            For Each Token As JToken In RecallArr
                Dim MsgId As Long = Token.Value(Of Long)()
                Result.Recalls.Add(MsgId)
                LocalRecall(MsgId)
            Next
        End If
        Dim MaxId As Long = Since
        If Data("max_id") IsNot Nothing AndAlso Data("max_id").Type <> JTokenType.Null Then MaxId = Data.Value(Of Long)("max_id")
        If MaxId > Since Then Settings.Set("FriendLastMsgId", MaxId)
        Return Result
    End Function

    ''' <summary>撤回自己发的消息（服务端限制在 2 分钟内）。</summary>
    Public Sub RecallMessage(MsgId As Long)
        If MsgId <= 0 Then Return
        FriendApi("recall", New JObject From {{"msg_id", MsgId}})
        LocalRecall(MsgId)
    End Sub

    ''' <summary>
    ''' 从服务端恢复历史消息到本地缓存（账号页的「恢复本地聊天记录」）。
    ''' 返回新增条数。
    ''' </summary>
    Public Function RestoreLocalHistory(Optional SinceId As Long = 0) As Integer
        Dim Added As Integer = 0
        Dim Cursor As Long = SinceId
        '服务端每页最多 500 条，这里最多翻 20 页（1 万条），够覆盖绝大多数使用场景
        For Page As Integer = 1 To 20
            Dim Param As New JObject From {{"since_id", Cursor}, {"limit", 500}}
            Dim Data As JObject = FriendApi("sync", Param, True, 30000)
            If Data Is Nothing Then Exit For
            Dim Arr As JArray = TryCast(Data("messages"), JArray)
            If Arr Is Nothing OrElse Arr.Count = 0 Then Exit For
            For Each Token As JToken In Arr
                LocalAppend(ParseMessage(CType(Token, JObject)))
                Added += 1
            Next
            Dim NewMax As Long = Cursor
            If Data("max_id") IsNot Nothing AndAlso Data("max_id").Type <> JTokenType.Null Then NewMax = Data.Value(Of Long)("max_id")
            If NewMax <= Cursor Then Exit For
            Cursor = NewMax
            If Cursor > Settings.Get(Of Long)("FriendLastMsgId") Then Settings.Set("FriendLastMsgId", Cursor)
            If Arr.Count < 500 Then Exit For
        Next
        LocalFlush()
        Return Added
    End Function

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
        SyncLock LocalLock
            For Each Pair As KeyValuePair(Of String, List(Of FriendMessage)) In LocalCache
                If Pair.Value.RemoveAll(Function(m) m.Id = MsgId) > 0 Then LocalDirty.Add(Pair.Key)
            Next
        End SyncLock
    End Sub

    ''' <summary>
    ''' 清空所有本地缓存（退出账号时可调用，目前保留以便换号后重新同步）。
    ''' </summary>
    Public Sub LocalClear()
        SyncLock LocalLock
            LocalCache.Clear()
            LocalDirty.Clear()
        End SyncLock
    End Sub

    ''' <summary>
    ''' 清除本机保存的聊天记录（内存 + 磁盘）。服务端的历史记录不受影响，
    ''' 重新打开会话时会从服务端重新拉取。
    ''' </summary>
    Public Function LocalClearAll() As Boolean
        LocalClear()
        Try
            Dim Dir As String = LocalFolder & "cache"
            If Directory.Exists(Dir) Then Directory.Delete(Dir, True)
            '顺手清掉旧版本的临时目录，避免残留
            Dim Legacy As String = PathTemp & "Friend"
            If Directory.Exists(Legacy) Then Directory.Delete(Legacy, True)
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "清除本地聊天记录失败")
            Return False
        End Try
    End Function

    ''' <summary>本地聊天记录的存放目录，界面上用于告知用户。</summary>
    Public ReadOnly Property LocalFolderForDisplay As String
        Get
            Return LocalFolder & "cache"
        End Get
    End Property

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
