Imports System.Windows.Threading

Public Class PageFriendChat

    ''' <summary>
    ''' 从通讯录/群组页跳转过来时，先记在这里，等页面加载完再打开。
    ''' </summary>
    Public PendingOpen As Tuple(Of Integer, String, String) = Nothing

    Private CurrentType As Integer = -1
    Private CurrentKey As String = ""
    Private CurrentTitle As String = ""
    Private Limit As Integer = 60

    Private ReadOnly Messages As New List(Of FriendMessage)
    Private ReadOnly Conversations As New List(Of FriendConversation)

    Private PollTimer As DispatcherTimer
    Private IsPolling As Boolean = False
    Private IsSending As Boolean = False
    Private LastConvRefresh As Long = 0

    Private Sub PageFriendChat_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If PollTimer Is Nothing Then
            PollTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(6)}
            AddHandler PollTimer.Tick, Sub() PollOnce()
        End If
        '恢复上次的会话列表折叠状态
        SetConvCollapsed(Settings.Get(Of Boolean)("FriendConvCollapsed"), False)
        If PendingOpen IsNot Nothing Then
            ApplyPendingOpen()
        Else
            RefreshConversations(True)
        End If
    End Sub

    Private Sub PageFriendChat_PageEnter() Handles Me.PageEnter
        If Not ModFriend.IsLoggedIn Then Return
        If PollTimer IsNot Nothing Then PollTimer.Start()
        PollOnce()
        RefreshConversations(False)
    End Sub

    Private Sub PageFriendChat_PageExit() Handles Me.PageExit
        If PollTimer IsNot Nothing Then PollTimer.Stop()
        ModFriend.LocalFlush()
    End Sub

    Public Sub ApplyPendingOpen()
        If PendingOpen Is Nothing Then Return
        Dim Target = PendingOpen
        PendingOpen = Nothing
        OpenConversation(Target.Item1, Target.Item2, Target.Item3)
    End Sub

#Region "会话列表折叠"

    ''' <summary>
    ''' 收起 / 展开左侧会话列表。收起后聊天区自动占满整行。
    ''' </summary>
    Private Sub BtnToggleConv_Click(sender As Object, e As EventArgs) Handles BtnToggleConv.Click
        SetConvCollapsed(CardConv.Visibility = Visibility.Visible, True)
    End Sub

    Private Sub SetConvCollapsed(Collapsed As Boolean, Optional Save As Boolean = True)
        If CardConv Is Nothing Then Return
        If Collapsed Then
            CardConv.Visibility = Visibility.Collapsed
            ColConv.Width = New GridLength(0)
            ColConvGap.Width = New GridLength(0)
            BtnToggleConv.Text = "展开会话"
        Else
            CardConv.Visibility = Visibility.Visible
            ColConv.Width = New GridLength(208)
            ColConvGap.Width = New GridLength(10)
            BtnToggleConv.Text = "收起会话"
        End If
        If Save Then Settings.Set("FriendConvCollapsed", Collapsed)
    End Sub

#End Region

#Region "会话列表"

    Private Sub RefreshConversations(Force As Boolean)
        If Not ModFriend.IsLoggedIn Then Return
        If Not Force AndAlso GetTimeMs() - LastConvRefresh < 4000 Then Return
        LastConvRefresh = GetTimeMs()
        RunInNewThread(
        Sub()
            Try
                Dim List As List(Of FriendConversation) = ModFriend.GetConversations()
                RunInUi(
                Sub()
                    Conversations.Clear()
                    Conversations.AddRange(List)
                    BuildConversationList()
                    LabConvHint.Text = If(List.Count = 0, "还没有会话", $"{List.Count} 个会话")
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabConvHint.Text = "会话加载失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Conversations")
    End Sub

    Private Sub BuildConversationList()
        PanConv.Children.Clear()
        For Each Conv As FriendConversation In Conversations
            PanConv.Children.Add(BuildConversationRow(Conv))
        Next
    End Sub

    Private Function BuildConversationRow(Conv As FriendConversation) As FrameworkElement
        Dim Row As New Border With {
            .CornerRadius = New CornerRadius(9),
            .Padding = New Thickness(9, 7, 9, 7),
            .Margin = New Thickness(0, 0, 0, 4),
            .Background = Brushes.Transparent,
            .Cursor = Cursors.Hand,
            .Tag = Conv
        }
        Dim Layout As New Grid()
        Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim Left As New StackPanel()
        Dim TitleRow As New StackPanel With {.Orientation = Orientation.Horizontal}
        Dim TitleText As String = Conv.Title
        If Conv.ConvType = 1 AndAlso Conv.MemberCount > 0 Then TitleText &= $"（{Conv.MemberCount}）"
        If Conv.FriendDeleted Then TitleText &= "（已删除的好友）"
        If Conv.ConvType = 0 Then
            TitleRow.Children.Add(New System.Windows.Shapes.Ellipse With {
                .Width = 7, .Height = 7,
                .Fill = If(Conv.Online, New SolidColorBrush(Color.FromRgb(46, 160, 67)), New SolidColorBrush(Color.FromRgb(160, 166, 176))),
                .Margin = New Thickness(0, 0, 5, 0),
                .VerticalAlignment = VerticalAlignment.Center
            })
        End If
        TitleRow.Children.Add(New TextBlock With {
            .Text = TitleText,
            .FontSize = 13,
            .FontWeight = FontWeights.SemiBold,
            .TextTrimming = TextTrimming.CharacterEllipsis
        })
        If Conv.ConvType = 0 AndAlso Not Conv.FriendDeleted Then
            TitleRow.Children.Add(New TextBlock With {
                .Text = If(Conv.Online, "在线", "离线"),
                .FontSize = 10.5,
                .Opacity = If(Conv.Online, 0.85, 0.45),
                .Margin = New Thickness(5, 0, 0, 0),
                .VerticalAlignment = VerticalAlignment.Center
            })
        End If
        Left.Children.Add(TitleRow)
        Dim Preview As New TextBlock With {
            .Text = If(String.IsNullOrEmpty(Conv.LastBody), "（暂无消息）", Conv.LastBody.Replace(vbCrLf, " ")),
            .FontSize = 11.5,
            .Opacity = 0.58,
            .Margin = New Thickness(0, 2, 0, 0),
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        Left.Children.Add(Preview)
        Layout.Children.Add(Left)

        Dim Right As New StackPanel With {.HorizontalAlignment = HorizontalAlignment.Right, .Margin = New Thickness(6, 1, 0, 0)}
        Dim Time As New TextBlock With {
            .Text = FormatTimeShort(Conv.LastAt),
            .FontSize = 10.5,
            .Opacity = 0.45,
            .HorizontalAlignment = HorizontalAlignment.Right
        }
        Right.Children.Add(Time)
        If Conv.Unread > 0 Then
            Dim Badge As New Border With {
                .CornerRadius = New CornerRadius(8),
                .Background = New SolidColorBrush(Color.FromRgb(216, 74, 74)),
                .Padding = New Thickness(5, 0, 5, 1),
                .Margin = New Thickness(0, 4, 0, 0),
                .HorizontalAlignment = HorizontalAlignment.Right,
                .Child = New TextBlock With {
                    .Text = If(Conv.Unread > 99, "99+", Conv.Unread.ToString()),
                    .FontSize = 10,
                    .Foreground = Brushes.White
                }
            }
            Right.Children.Add(Badge)
        End If
        Grid.SetColumn(Right, 1)
        Layout.Children.Add(Right)
        Row.Child = Layout

        Dim NormalBrush As Brush = Brushes.Transparent
        AddHandler Row.MouseEnter, Sub() Row.Background = TryCast(FindResource("ColorBrushGlassInner"), Brush)
        AddHandler Row.MouseLeave, Sub() Row.Background = NormalBrush
        AddHandler Row.MouseLeftButtonUp,
            Sub()
                Row.Background = NormalBrush
                OpenConversation(Conv.ConvType, Conv.ConvKey, Conv.Title)
            End Sub
        '右键：打开 / 删除会话
        AddHandler Row.ContextMenuOpening,
            Sub(sender As Object, ev As ContextMenuEventArgs)
                Row.ContextMenu = BuildConversationMenu(Conv)
            End Sub
        Return Row
    End Function

    Private Function BuildConversationMenu(Conv As FriendConversation) As ContextMenu
        Dim Menu As New ContextMenu()
        Dim ItemOpen As New MyMenuItem With {.Header = "打开"}
        AddHandler ItemOpen.Click, Sub(sender As Object, e As RoutedEventArgs) OpenConversation(Conv.ConvType, Conv.ConvKey, Conv.Title)
        Menu.Items.Add(ItemOpen)

        Dim ItemDelete As New MyMenuItem With {.Header = "删除会话"}
        AddHandler ItemDelete.Click, Sub(sender As Object, e As RoutedEventArgs) DeleteConversationRow(Conv)
        Menu.Items.Add(ItemDelete)
        Return Menu
    End Function

    ''' <summary>删除会话：服务端会话列表与本机缓存一起清掉。</summary>
    Private Sub DeleteConversationRow(Conv As FriendConversation)
        Dim Tip As String = If(Conv.FriendDeleted,
                               $"「{Conv.Title}」的好友关系已解除，删除后不会再出现在会话列表里。",
                               $"删除与「{Conv.Title}」的会话？本机聊天记录会一并清除，服务器上的漫游记录不受影响。")
        If MyMsgBox(Tip, "删除会话", "删除", "取消", IsWarn:=True) <> 1 Then Return
        Dim ConvType As Integer = Conv.ConvType
        Dim ConvKey As String = Conv.ConvKey
        RunInNewThread(
        Sub()
            Try
                ModFriend.DeleteConversation(ConvType, ConvKey)
                RunInUi(
                Sub()
                    Conversations.RemoveAll(Function(c) c.ConvType = ConvType AndAlso c.ConvKey = ConvKey)
                    BuildConversationList()
                    If CurrentType = ConvType AndAlso CurrentKey = ConvKey Then
                        CurrentType = -1
                        CurrentKey = ""
                        CurrentTitle = ""
                        Messages.Clear()
                        RenderMessages()
                        LabConvTitle.Text = "选择一个会话"
                        LabConvSub.Text = "也可以从通讯录或群聊里打开"
                    End If
                    LabConvHint.Text = If(Conversations.Count = 0, "还没有会话", $"{Conversations.Count} 个会话")
                    Hint("已删除会话", HintType.Green, False)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() Hint("删除会话失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Delete Conversation")
    End Sub

#End Region

#Region "打开会话"

    Private Sub OpenConversation(ConvType As Integer, ConvKey As String, Title As String)
        If ConvType < 0 OrElse String.IsNullOrEmpty(ConvKey) Then Return
        CurrentType = ConvType
        CurrentKey = ConvKey
        CurrentTitle = Title
        Limit = 60

        LabConvTitle.Text = Title
        LabConvSub.Text = If(ConvType = 1, "群聊", "好友会话")
        LabEmpty.Visibility = Visibility.Collapsed

        '先显示本地缓存，界面立刻有内容
        Messages.Clear()
        Messages.AddRange(ModFriend.LocalLoad(ConvType, ConvKey))
        RenderMessages()

        '再补拉服务器上的最新消息
        RunInNewThread(
        Sub()
            Try
                Dim Remote As List(Of FriendMessage) = ModFriend.GetMessages(ConvType, ConvKey, 0, 60)
                RunInUi(
                Sub()
                    If CurrentType <> ConvType OrElse CurrentKey <> ConvKey Then Return
                    MergeRemote(Remote)
                    RenderMessages()
                End Sub)
                '标记已读
                Try
                    ModFriend.FriendApi("read", New JObject From {{"conv_type", ConvType}, {"conv_key", ConvKey}})
                    '服务端清零后立刻同步本地红点，不用等下一次轮询
                    RunInUi(
                    Sub()
                        Dim Target As FriendConversation = Conversations.FirstOrDefault(
                            Function(c) c.ConvType = ConvType AndAlso c.ConvKey = ConvKey)
                        If Target IsNot Nothing AndAlso Target.Unread > 0 Then
                            Target.Unread = 0
                            BuildConversationList()
                        End If
                    End Sub)
                Catch ex As Exception
                    Logger.Warn(ex, "标记好友消息已读失败")
                End Try
            Catch ex As Exception
                RunInUi(Sub() LabConvSub.Text = If(ConvType = 1, "群聊", "好友会话") & " · 同步失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Open Conversation")
    End Sub

    ''' <summary>
    ''' 把服务器消息并入当前列表：保留尚未发送完成的本地消息。
    ''' </summary>
    Private Sub MergeRemote(Remote As List(Of FriendMessage))
        Dim Pending As List(Of FriendMessage) = Messages.Where(Function(m) m.Id <= 0).ToList()
        Dim Dict As New Dictionary(Of Long, FriendMessage)
        For Each Msg As FriendMessage In Remote
            '本机删掉的消息不要因为服务端还有备份就冒出来
            If Msg.Id > 0 AndAlso Not ModFriend.IsDeleted(Msg.Id) Then Dict(Msg.Id) = Msg
        Next
        '本地缓存里可能有服务器只返回了最近 60 条之外的老消息
        For Each Msg As FriendMessage In Messages
            If Msg.Id > 0 AndAlso Not Dict.ContainsKey(Msg.Id) Then Dict(Msg.Id) = Msg
        Next
        Messages.Clear()
        Messages.AddRange(Dict.Values.OrderBy(Function(m) m.Id))
        Messages.AddRange(Pending)
        '把刚拉到的历史补进本机缓存。之前只写轮询收到的新消息，
        '导致本机几乎没记录，漫游上传永远是 0 条、离线也看不到历史。
        ModFriend.LocalAppendRange(Dict.Values.Where(Function(m) m.Id > 0))
    End Sub

#End Region

#Region "渲染"

    Private Sub RenderMessages()
        PanMsg.Children.Clear()
        PanMsg.Children.Add(BtnLoadMore)
        BtnLoadMore.Visibility = If(Messages.Count >= Limit, Visibility.Visible, Visibility.Collapsed)
        PanMsg.Children.Add(LabEmpty)
        LabEmpty.Visibility = If(Messages.Count = 0, Visibility.Visible, Visibility.Collapsed)
        If Messages.Count = 0 Then
            LabEmpty.Text = "还没有聊天记录。发送第一条消息吧。"
        End If

        For Each Msg As FriendMessage In Messages.Skip(Math.Max(0, Messages.Count - Limit))
            PanMsg.Children.Add(BuildMessageRow(Msg))
        Next
        '等布局完成后再滚到底部，避免长消息把最后一条挡在视口外
        Dispatcher.BeginInvoke(New Action(Sub() PanMsgScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background)
    End Sub

    Private Function BuildMessageRow(Msg As FriendMessage) As FrameworkElement
        Dim Bubble As New StackPanel With {
            .MaxWidth = 470,
            .HorizontalAlignment = If(Msg.IsMine, HorizontalAlignment.Right, HorizontalAlignment.Left),
            .Margin = New Thickness(0, 3, 0, 3)
        }

        If Not Msg.IsMine AndAlso CurrentType = 1 Then
            Bubble.Children.Add(New TextBlock With {
                .Text = Msg.FromName,
                .FontSize = 11,
                .Opacity = 0.55,
                .Margin = New Thickness(5, 0, 0, 2)
            })
        End If

        Dim BodyText As String = If(Msg.Recalled,
                                    If(Msg.IsMine, "你撤回了一条消息", "对方撤回了一条消息"),
                                    Msg.Body)
        Dim Body As New TextBlock With {
            .Text = BodyText,
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 13.5,
            .LineHeight = 20
        }
        If Msg.Recalled Then
            Body.Opacity = 0.62
            Body.FontStyle = FontStyles.Italic
        End If
        '自己的消息绿色、对方蓝色；用半透明色，深浅色主题都不会出现白底白字
        Dim Box As New Border With {
            .CornerRadius = New CornerRadius(10),
            .Padding = New Thickness(11, 7, 11, 7),
            .Child = Body,
            .BorderThickness = New Thickness(1),
            .Background = If(Msg.Recalled,
                             New SolidColorBrush(Color.FromArgb(28, 140, 140, 140)),
                             If(Msg.IsMine,
                                New SolidColorBrush(Color.FromArgb(46, 46, 160, 67)),
                                New SolidColorBrush(Color.FromArgb(46, 30, 110, 210)))),
            .BorderBrush = If(Msg.Recalled,
                              New SolidColorBrush(Color.FromArgb(70, 140, 140, 140)),
                              If(Msg.IsMine,
                                 New SolidColorBrush(Color.FromArgb(95, 46, 160, 67)),
                                 New SolidColorBrush(Color.FromArgb(95, 30, 110, 210))))
        }
        If Msg.Pending Then Box.Opacity = 0.62
        '右键菜单按需构建：不为每条消息常驻一个菜单对象
        If Not Msg.Failed Then
            AddHandler Box.ContextMenuOpening,
                Sub(sender As Object, ev As ContextMenuEventArgs)
                    Box.ContextMenu = BuildMessageMenu(Msg)
                End Sub
        End If
        Bubble.Children.Add(Box)

        '链接识别：邀请码 / tcp 节点 / HTTP(S) 服务器
        Dim Links As List(Of FriendLink) = If(Msg.Recalled, New List(Of FriendLink), ModFriend.DetectLinks(Msg.Body))
        If Links.Count > 0 AndAlso Not Msg.Pending Then
            Dim Bar As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(2, 4, 0, 0)}
            For Each Link As FriendLink In Links.Take(3)
                Dim Item As FriendLink = Link
                Dim Btn As New MyTextButton With {
                    .Text = Item.Label,
                    .FontSize = 11.5,
                    .Height = 21,
                    .Padding = New Thickness(7, 0, 7, 0),
                    .Margin = New Thickness(0, 0, 6, 0)
                }
                AddHandler Btn.Click, Sub(sender As Object, ev As EventArgs) ModFriend.ImportLink(Item)
                Bar.Children.Add(Btn)
            Next
            Bubble.Children.Add(Bar)
        End If

        Bubble.Children.Add(New TextBlock With {
            .Text = If(Msg.Failed, "发送失败", FormatTime(Msg.CreatedAt)),
            .FontSize = 10.5,
            .Opacity = 0.45,
            .Margin = New Thickness(4, 3, 4, 0),
            .HorizontalAlignment = If(Msg.IsMine, HorizontalAlignment.Right, HorizontalAlignment.Left)
        })
        Return Bubble
    End Function

#End Region

#Region "消息操作"

    ''' <summary>消息右键菜单：复制 / 转发 / 撤回 / 删除。</summary>
    Private Function BuildMessageMenu(Msg As FriendMessage) As ContextMenu
        Dim Menu As New ContextMenu()

        If Not Msg.Recalled AndAlso Not String.IsNullOrEmpty(Msg.Body) Then
            Dim ItemCopy As New MyMenuItem With {.Header = "复制"}
            AddHandler ItemCopy.Click, Sub(sender As Object, e As RoutedEventArgs) CopyMessage(Msg)
            Menu.Items.Add(ItemCopy)

            Dim ItemForward As New MyMenuItem With {.Header = "转发"}
            AddHandler ItemForward.Click, Sub(sender As Object, e As RoutedEventArgs) ForwardMessage(Msg)
            Menu.Items.Add(ItemForward)
        End If

        '撤回：只能撤自己发的、已发送完成的、2 分钟内的消息
        If Msg.IsMine AndAlso Msg.Id > 0 AndAlso Not Msg.Recalled AndAlso Not Msg.Pending AndAlso
           GetTimeMs() \ 1000 - Msg.CreatedAt <= 120 Then
            Dim ItemRecall As New MyMenuItem With {.Header = "撤回"}
            AddHandler ItemRecall.Click, Sub(sender As Object, e As RoutedEventArgs) RecallMessage(Msg)
            Menu.Items.Add(ItemRecall)
        End If

        Dim ItemDelete As New MyMenuItem With {.Header = "删除"}
        AddHandler ItemDelete.Click, Sub(sender As Object, e As RoutedEventArgs) DeleteMessage(Msg)
        Menu.Items.Add(ItemDelete)

        Return Menu
    End Function

    Private Sub CopyMessage(Msg As FriendMessage)
        Try
            My.Computer.Clipboard.SetText(Msg.Body)
            Hint("已复制到剪贴板", HintType.Green, False)
        Catch ex As Exception
            Hint("复制失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub RecallMessage(Msg As FriendMessage)
        If Msg.Id <= 0 Then Return
        If MyMsgBox("撤回后对方会看到「对方撤回了一条消息」，确定撤回吗？", "撤回消息", "撤回", "取消") <> 1 Then Return
        RunInNewThread(
        Sub()
            Try
                ModFriend.RecallMessage(Msg.Id)
                RunInUi(
                Sub()
                    Msg.Kind = "recalled"
                    Msg.Body = ""
                    RenderMessages()
                    RefreshConversations(True)
                    Hint("已撤回", HintType.Green, False)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() Hint("撤回失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Recall")
    End Sub

    Private Sub DeleteMessage(Msg As FriendMessage)
        If MyMsgBox("只从本机删除这条消息，对方仍然可以看到。", "删除消息", "删除", "取消") <> 1 Then Return
        Messages.RemoveAll(Function(m) m Is Msg OrElse (Msg.Id > 0 AndAlso m.Id = Msg.Id))
        ModFriend.LocalRemove(Msg.Id)
        ModFriend.LocalFlush()
        RenderMessages()
    End Sub

    Private Sub ForwardMessage(Msg As FriendMessage)
        If String.IsNullOrEmpty(Msg.Body) Then Return
        Dim Text As String = Msg.Body
        RunInNewThread(
        Sub()
            Try
                Dim Contacts As List(Of FriendContact) = ModFriend.GetContacts()
                Dim Groups As List(Of FriendGroup) = ModFriend.GetGroups()
                RunInUi(Sub() ShowForwardPicker(Text, Contacts, Groups))
            Catch ex As Exception
                RunInUi(Sub() Hint("读取好友列表失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Forward")
    End Sub

    Private Sub ShowForwardPicker(Text As String, Contacts As List(Of FriendContact), Groups As List(Of FriendGroup))
        Dim Options As New List(Of IMyRadio)
        Dim Targets As New List(Of Tuple(Of Integer, String, String))
        For Each Contact As FriendContact In Contacts
            Options.Add(New MyRadioBox With {.Text = $"好友 · {Contact.Nickname}"})
            Targets.Add(New Tuple(Of Integer, String, String)(0, Contact.Id.ToString(), Contact.Nickname))
        Next
        For Each Group As FriendGroup In Groups
            Options.Add(New MyRadioBox With {.Text = $"群聊 · {Group.Name}（{Group.MemberCount}）"})
            Targets.Add(New Tuple(Of Integer, String, String)(1, Group.Id.ToString(), Group.Name))
        Next
        If Options.Count = 0 Then
            Hint("通讯录里还没有好友或群聊", HintType.Red)
            Return
        End If
        Dim Selected As Integer? = MyMsgBoxSelect(Options, "转发给", "发送", "取消")
        If Selected Is Nothing Then Return
        Dim Index As Integer = CInt(Selected)
        If Index < 0 OrElse Index >= Targets.Count Then Return
        Dim Target = Targets(Index)
        RunInNewThread(
        Sub()
            Try
                ModFriend.SendMessage(Target.Item1, Target.Item2, Text)
                RunInUi(Sub() Hint($"已转发给 {Target.Item3}", HintType.Green))
            Catch ex As Exception
                RunInUi(Sub() Hint("转发失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Forward Send")
    End Sub

#End Region

#Region "发送与轮询"

    Private Sub BtnSend_Click(sender As Object, e As EventArgs) Handles BtnSend.Click
        SendCurrent()
    End Sub

    Private Sub TextInput_KeyDown(sender As Object, e As KeyEventArgs) Handles TextMessage.PreviewKeyDown
        If e.Key <> Key.Enter Then Return
        If Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) Then Return
        e.Handled = True
        SendCurrent()
    End Sub

    Private Sub SendCurrent()
        If IsSending OrElse Not ModFriend.IsLoggedIn Then Return
        If CurrentType < 0 Then
            Hint("请先选择一个会话", HintType.Red)
            Return
        End If
        Dim Text As String = TextMessage.Text.Trim()
        If Text.Length = 0 Then Return
        If Text.Length > 2000 Then
            Hint("单条消息不能超过 2000 个字符", HintType.Red)
            Return
        End If

        Dim Pending As New FriendMessage With {
            .Id = 0,
            .ConvType = CurrentType,
            .ConvKey = CurrentKey,
            .FromId = ModFriend.MyId,
            .FromName = ModFriend.MyName,
            .Body = Text,
            .Kind = "text",
            .CreatedAt = NowUnix(),
            .Pending = True,
            .IsMine = True
        }
        Messages.Add(Pending)
        TextMessage.Text = ""
        IsSending = True
        RenderMessages()

        Dim ConvType As Integer = CurrentType
        Dim ConvKey As String = CurrentKey
        RunInNewThread(
        Sub()
            Try
                Dim Sent As FriendMessage = ModFriend.SendMessage(ConvType, ConvKey, Text)
                RunInUi(
                Sub()
                    Messages.Remove(Pending)
                    If Not Messages.Any(Function(m) m.Id = Sent.Id) Then Messages.Add(Sent)
                    IsSending = False
                    RenderMessages()
                    RefreshConversations(True)
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    Pending.Pending = False
                    Pending.Failed = True
                    IsSending = False
                    RenderMessages()
                    Hint("消息发送失败：" & ModFriend.FriendErrorText(ex), HintType.Red)
                End Sub)
            End Try
        End Sub, "Friend Send")
    End Sub

    Private Sub PollOnce()
        If IsPolling OrElse Not ModFriend.IsLoggedIn Then Return
        If FrmMain.PageCurrent.Page <> FormMain.PageType.Friends Then Return
        IsPolling = True
        RunInNewThread(
        Sub()
            Try
                Dim Polled As ModFriend.FriendPollResult = ModFriend.Poll()
                If Polled.Messages.Count = 0 AndAlso Polled.Recalls.Count = 0 Then Return
                '有当前会话的新消息就追加显示，其它会话只更新列表
                Dim ForCurrent As List(Of FriendMessage) =
                    Polled.Messages.Where(Function(m) m.ConvType = CurrentType AndAlso m.ConvKey = CurrentKey).ToList()
                RunInUi(
                Sub()
                    Dim NeedRender As Boolean = False
                    If ForCurrent.Count > 0 Then
                        For Each Msg As FriendMessage In ForCurrent
                            If Not Messages.Any(Function(m) m.Id = Msg.Id) Then Messages.Add(Msg)
                        Next
                        NeedRender = True
                    End If
                    '撤回：把本地对应消息改成「已撤回」
                    If Polled.Recalls.Count > 0 Then
                        For Each Msg As FriendMessage In Messages
                            If Msg.Id > 0 AndAlso Polled.Recalls.Contains(Msg.Id) AndAlso Not Msg.Recalled Then
                                Msg.Kind = "recalled"
                                Msg.Body = ""
                                NeedRender = True
                            End If
                        Next
                    End If
                    If NeedRender Then RenderMessages()
                    RefreshConversations(True)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "拉取好友新消息失败")
            Finally
                IsPolling = False
                ModFriend.LocalFlush()
            End Try
        End Sub, "Friend Poll")
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As EventArgs) Handles BtnRefresh.Click
        RefreshConversations(True)
        If CurrentType >= 0 Then OpenConversation(CurrentType, CurrentKey, CurrentTitle) Else PollOnce()
    End Sub

    Private Sub BtnLoadMore_Click(sender As Object, e As EventArgs) Handles BtnLoadMore.Click
        '先看看本地还有没有更早的
        If Messages.Count > Limit Then
            Limit += 60
            RenderMessages()
            Return
        End If
        '本地没有了，再从服务端拉一页更早的
        Dim FirstId As Long = Messages.Where(Function(m) m.Id > 0).Select(Function(m) m.Id).DefaultIfEmpty(0).Min()
        If FirstId <= 0 OrElse CurrentType < 0 Then Return
        Dim ConvType As Integer = CurrentType
        Dim ConvKey As String = CurrentKey
        Dim Title As String = CurrentTitle
        BtnLoadMore.IsEnabled = False
        BtnLoadMore.Text = "正在加载……"
        RunInNewThread(
        Sub()
            Try
                Dim Older As List(Of FriendMessage) = ModFriend.GetMessages(ConvType, ConvKey, FirstId, 60)
                RunInUi(
                Sub()
                    If CurrentType <> ConvType OrElse CurrentKey <> ConvKey Then Return
                    For Each Msg As FriendMessage In Older
                        If Msg.Id > 0 AndAlso Not Messages.Any(Function(m) m.Id = Msg.Id) Then Messages.Add(Msg)
                        ModFriend.LocalAppend(Msg)
                    Next
                    NormalizeMessages()
                    Limit += 60
                    RenderMessages()
                    BtnLoadMore.IsEnabled = True
                    BtnLoadMore.Text = "查看更早的消息"
                    If Older.Count = 0 Then Hint("没有更早的消息了", HintType.Blue, False)
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    BtnLoadMore.IsEnabled = True
                    BtnLoadMore.Text = "查看更早的消息"
                    Hint("加载更早的消息失败：" & ModFriend.FriendErrorText(ex), HintType.Red)
                End Sub)
            End Try
        End Sub, "Friend Load More")
    End Sub

    ''' <summary>
    ''' 按消息 ID 排序，未发送完成的消息保持在最后。
    ''' </summary>
    Private Sub NormalizeMessages()
        Dim Pending As List(Of FriendMessage) = Messages.Where(Function(m) m.Id <= 0).ToList()
        Dim Sent As List(Of FriendMessage) = Messages.
            Where(Function(m) m.Id > 0).
            GroupBy(Function(m) m.Id).
            Select(Function(g) g.First()).
            OrderBy(Function(m) m.Id).
            ToList()
        Sent.AddRange(Pending)
        Messages.Clear()
        Messages.AddRange(Sent)
    End Sub

    Private Sub BtnAddFriend_Click(sender As Object, e As EventArgs) Handles BtnAddFriend.Click
        FrmFriendLeft.PageChange(FormMain.PageSubType.FriendContact)
    End Sub

    Private Sub BtnNewGroup_Click(sender As Object, e As EventArgs) Handles BtnNewGroup.Click
        FrmFriendLeft.PageChange(FormMain.PageSubType.FriendGroup)
    End Sub

#End Region

#Region "工具"

    Private Function NowUnix() As Long
        Return CLng((Date.UtcNow - New Date(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds)
    End Function

    Private Function FormatTimeShort(Unix As Long) As String
        If Unix <= 0 Then Return ""
        Try
            Dim D As Date = New Date(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().AddSeconds(Unix)
            If D.Date = Date.Now.Date Then Return D.ToString("HH:mm")
            If (Date.Now - D).TotalDays < 7 Then Return D.ToString("ddd HH:mm")
            Return D.ToString("yyyy-MM-dd")
        Catch
            Return ""
        End Try
    End Function

    Private Function FormatTime(Unix As Long) As String
        If Unix <= 0 Then Return ""
        Try
            Dim D As Date = New Date(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().AddSeconds(Unix)
            If D.Date = Date.Now.Date Then Return D.ToString("HH:mm")
            Return D.ToString("MM-dd HH:mm")
        Catch
            Return ""
        End Try
    End Function

#End Region

End Class
