''' <summary>
''' 联网通知：所有公告文案都由更新服务器下发，客户端不硬编码任何通知内容。
'''
''' 发送流程：
'''   1. 管理后台「联网通知」新建一条（标题、正文、级别、生效窗口、版本区间、是否只弹一次）；
'''   2. 客户端启动完成 → 延迟 4 秒 → 距上次拉取超过 4 小时才请求 ?action=notices&amp;app_version=x.y.z；
'''   3. 服务端按 enabled + 时间窗口 + 版本区间过滤后返回；
'''   4. 客户端本地再过滤掉已读的（once=1 且 ID 在已读列表里）；
'''   5. 多条同时生效时只弹级别最高、ID 最大的一条，其余留到下次启动；
'''   6. 弹窗展示 → 记录已读 → 回报一次触达（?action=notice_ack）。
'''
''' 触发时机只有一处：启动初始化时调用 StartupCheck。
''' 不做定时轮询，也不在运行过程中反复请求 —— 一个版本一天最多打扰一次。
''' </summary>
Friend Module ModNotice

#Region "参数"

    ''' <summary>
    ''' 启动后延迟多久再拉取。
    ''' 弹窗统一走 MyMsgBox 队列，就算和「发现新版本」撞上也会排队，不会叠在一起。
    ''' </summary>
    Private Const StartupDelayMs As Integer = 4000
    ''' <summary>
    ''' 两次拉取的最小间隔。一次请求只有几百字节，这里主要是防止用户反复重启时刷接口；
    ''' 4 小时能保证当天发布的通知当天就能触达大部分用户，又不会每次都请求。
    ''' </summary>
    Private Const FetchIntervalSec As Long = 4 * 3600
    ''' <summary>本地留存的已读通知 ID 上限，超出后按先进先出丢弃。</summary>
    Private Const MaxReadIds As Integer = 100

#End Region

    ''' <summary>
    ''' 启动时调用一次。内部另起线程并延迟，不阻塞启动流程。
    ''' </summary>
    Public Sub StartupCheck()
        RunInNewThread(
        Sub()
            Try
                If Not ShouldFetchNow() Then
                    Logger.Info("联网通知：距上次拉取不足 4 小时，本次跳过")
                    Return
                End If

                Thread.Sleep(StartupDelayMs)

                Dim Notices As List(Of ModUpdateServer.ServerNotice) = ModUpdateServer.FetchNotices(VersionBaseName)
                MarkFetched()
                If Notices Is Nothing OrElse Notices.Count = 0 Then
                    Logger.Trace("联网通知：服务端没有需要展示的通知")
                    Return
                End If

                Dim ReadIds As List(Of String) = GetReadIds()
                Dim Pending As List(Of ModUpdateServer.ServerNotice) =
                    Notices.Where(Function(n) Not (n.OnceOnly AndAlso ReadIds.Contains(n.Id.ToString()))).ToList()
                If Pending.Count = 0 Then
                    Logger.Trace("联网通知：全部已读，本次不弹窗")
                    Return
                End If

                Dim Target As ModUpdateServer.ServerNotice =
                    Pending.OrderByDescending(Function(n) n.Level).ThenByDescending(Function(n) n.Id).First()
                Logger.Info($"联网通知：展示 #{Target.Id}「{Target.Title}」（级别 {Target.Level}，" &
                            $"另有 {Pending.Count - 1} 条待下次启动）")
                ShowNotice(Target)
            Catch ex As Exception
                Logger.Warn(ex, "拉取联网通知失败")
            End Try
        End Sub, "联网通知")
    End Sub

#Region "拉取与展示"

    Private Function ShouldFetchNow() As Boolean
        Try
            Dim Last As Long = Settings.Get(Of Long)("NoticeLastFetch")
            If Last > 0 AndAlso GetUnixTimestampUtc() - Last < FetchIntervalSec Then Return False
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "读取通知拉取时间失败")
            Return True
        End Try
    End Function

    Private Sub MarkFetched()
        Try
            Settings.Set("NoticeLastFetch", GetUnixTimestampUtc())
        Catch ex As Exception
            Logger.Warn(ex, "记录通知拉取时间失败")
        End Try
    End Sub

    ''' <summary>展示一条通知。本方法在后台线程执行，只有弹窗切到 UI 线程。</summary>
    Private Sub ShowNotice(Notice As ModUpdateServer.ServerNotice)
        Dim Text As String = Notice.Body
        If Not String.IsNullOrEmpty(Notice.Title) Then
            Text = Notice.Title & vbCrLf & vbCrLf & Notice.Body
        End If

        Dim Result As Integer
        Try
            'MyMsgBox 会排队，不会和启动时的更新弹窗叠在一起
            Result = RunInUiWait(Function()
                                     Return MyMsgBox(Text, "通知", "知道了",
                                                     If(Notice.OnceOnly, "", "不再提示"),
                                                     IsWarn:=(Notice.Level >= 2))
                                 End Function)
        Catch ex As Exception
            Logger.Warn(ex, "展示联网通知失败")
            Return
        End Try

        'once=1 的通知看过就记为已读；once=0 的只有用户点「不再提示」才屏蔽
        If Notice.OnceOnly OrElse Result = 2 Then MarkRead(Notice.Id)

        '触达回报，失败不影响任何东西
        ModUpdateServer.AcknowledgeNotice(Notice.Id)
    End Sub

#End Region

#Region "已读记录"

    Private Function GetReadIds() As List(Of String)
        Try
            Dim Raw As String = Settings.Get(Of String)("NoticeReadIds")
            If String.IsNullOrWhiteSpace(Raw) Then Return New List(Of String)
            Return Raw.Split(","c).Where(Function(s) Not String.IsNullOrWhiteSpace(s)).ToList()
        Catch ex As Exception
            Logger.Warn(ex, "读取通知已读记录失败")
            Return New List(Of String)
        End Try
    End Function

    Private Sub MarkRead(Id As Integer)
        Try
            Dim Ids As List(Of String) = GetReadIds()
            Dim Key As String = Id.ToString()
            If Ids.Contains(Key) Then Return
            Ids.Add(Key)
            If Ids.Count > MaxReadIds Then Ids = Ids.Skip(Ids.Count - MaxReadIds).ToList()
            Settings.Set("NoticeReadIds", String.Join(",", Ids))
        Catch ex As Exception
            Logger.Warn(ex, "记录通知已读失败")
        End Try
    End Sub

#End Region

End Module
