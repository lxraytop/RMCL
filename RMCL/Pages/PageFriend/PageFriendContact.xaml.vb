Public Class PageFriendContact

    Private LastRefresh As Long = 0
    Private RealtimeHooked As Boolean = False

    Private Sub PageFriendContact_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not RealtimeHooked Then
            AddHandler ModFriend.RealtimeActivity, AddressOf FriendRealtimeActivity
            RealtimeHooked = True
        End If
        ModFriend.StartRealtime()
        RefreshAll(True)
    End Sub

    '收到好友申请 / 同意 / 删除等事件时刷新列表
    Private Sub FriendRealtimeActivity()
        RunInUi(Sub() RefreshAll(True))
    End Sub

    Private Sub PageFriendContact_PageEnter() Handles Me.PageEnter
        RefreshAll(False)
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As EventArgs) Handles BtnRefresh.Click
        RefreshAll(True)
    End Sub

    Private Sub RefreshAll(Force As Boolean)
        If Not ModFriend.IsLoggedIn Then Return
        If Not Force AndAlso GetTimeMs() - LastRefresh < 3000 Then Return
        LastRefresh = GetTimeMs()
        RunInNewThread(
        Sub()
            Try
                Dim Contacts As List(Of FriendContact) = ModFriend.GetContacts()
                Dim Requests As List(Of FriendRequest) = Nothing
                Try
                    Requests = ModFriend.GetIncomingRequests()
                Catch ex As Exception
                    Logger.Warn(ex, "读取好友请求失败")
                End Try
                RunInUi(
                Sub()
                    BuildContacts(Contacts)
                    BuildRequests(Requests)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabContactHint.Text = "加载失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Contacts")
    End Sub

#Region "通讯录"

    Private Sub BuildContacts(Contacts As List(Of FriendContact))
        PanContacts.Children.Clear()
        LabContactHint.Text = If(Contacts.Count = 0, "通讯录还是空的，先加几个好友吧", $"{Contacts.Count} 位好友")
        For Each Contact As FriendContact In Contacts
            Dim Row As New Grid With {.Height = 40, .Margin = New Thickness(0, 0, 0, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Dim NameRow As New StackPanel With {.Orientation = Orientation.Horizontal}
            NameRow.Children.Add(New TextBlock With {
                .Text = If(String.IsNullOrEmpty(Contact.Remark), Contact.Nickname, $"{Contact.Remark}（{Contact.Nickname}）"),
                .FontSize = 13,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Info.Children.Add(NameRow)
            Info.Children.Add(New TextBlock With {
                .Text = Contact.Email,
                .FontSize = 11,
                .Opacity = 0.55,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Row.Children.Add(Info)

            Dim BtnChat As New MyButton With {
                .Text = "聊天", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .ColorType = MyButton.ColorState.Highlight, .Margin = New Thickness(0, 0, 8, 0)
            }
            AddHandler BtnChat.Click, Sub(sender As Object, e As MouseButtonEventArgs) FrmFriendLeft.OpenChat(0, Contact.Id.ToString(), Contact.Nickname)
            Grid.SetColumn(BtnChat, 1)
            Row.Children.Add(BtnChat)

            Dim BtnDelete As New MyButton With {
                .Text = "删除", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0)
            }
            AddHandler BtnDelete.Click, Sub(sender As Object, e As MouseButtonEventArgs) DeleteFriend(Contact)
            Grid.SetColumn(BtnDelete, 2)
            Row.Children.Add(BtnDelete)

            PanContacts.Children.Add(Row)
        Next
    End Sub

    Private Sub DeleteFriend(Contact As FriendContact)
        If MyMsgBox($"删除好友「{Contact.Nickname}」？删除后聊天记录仍会保留在本地。", "删除好友", "删除", "取消", IsWarn:=True) <> 1 Then Return
        RunInNewThread(
        Sub()
            Try
                ModFriend.DeleteFriend(Contact.Id)
                RunInUi(Sub() RefreshAll(True))
            Catch ex As Exception
                RunInUi(Sub() Hint("删除失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Delete")
    End Sub

#End Region

#Region "好友请求"

    Private Sub BuildRequests(Items As List(Of FriendRequest))
        PanRequests.Children.Clear()
        If Items Is Nothing OrElse Items.Count = 0 Then
            LabRequestEmpty.Visibility = Visibility.Visible
            Return
        End If
        LabRequestEmpty.Visibility = Visibility.Collapsed
        For Each Request As FriendRequest In Items

            Dim Row As New Grid With {.Height = 42, .Margin = New Thickness(0, 0, 0, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Info.Children.Add(New TextBlock With {
                .Text = $"{Request.FromName}（{Request.FromEmail}）",
                .FontSize = 13,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Info.Children.Add(New TextBlock With {
                .Text = If(String.IsNullOrEmpty(Request.Message), "请求加你为好友", Request.Message),
                .FontSize = 11,
                .Opacity = 0.55,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Row.Children.Add(Info)

            Dim BtnAccept As New MyButton With {
                .Text = "同意", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .ColorType = MyButton.ColorState.Highlight, .Margin = New Thickness(0, 0, 8, 0)
            }
            AddHandler BtnAccept.Click, Sub(sender As Object, e As MouseButtonEventArgs) AnswerRequest(Request.Id, True)
            Grid.SetColumn(BtnAccept, 1)
            Row.Children.Add(BtnAccept)

            Dim BtnReject As New MyButton With {
                .Text = "拒绝", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0)
            }
            AddHandler BtnReject.Click, Sub(sender As Object, e As MouseButtonEventArgs) AnswerRequest(Request.Id, False)
            Grid.SetColumn(BtnReject, 2)
            Row.Children.Add(BtnReject)

            PanRequests.Children.Add(Row)
        Next
    End Sub

    Private Sub AnswerRequest(RequestId As Long, Accept As Boolean)
        RunInNewThread(
        Sub()
            Try
                If Accept Then
                    ModFriend.AcceptRequest(RequestId)
                Else
                    ModFriend.RejectRequest(RequestId)
                End If
                RunInUi(
                Sub()
                    Hint(If(Accept, "已同意好友请求", "已拒绝好友请求"), HintType.Green, False)
                    RefreshAll(True)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() Hint("操作失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Answer Request")
    End Sub

#End Region

#Region "搜索"

    Private Sub BtnSearch_Click(sender As Object, e As EventArgs) Handles BtnSearch.Click
        DoSearch()
    End Sub

    Private Sub TextKeyword_KeyDown(sender As Object, e As KeyEventArgs) Handles TextKeyword.PreviewKeyDown
        If e.Key = Key.Enter Then
            e.Handled = True
            DoSearch()
        End If
    End Sub

    Private Sub DoSearch()
        Dim Keyword As String = TextKeyword.Text.Trim()
        If Keyword.Length = 0 Then Return
        LabSearchHint.Text = "正在搜索……"
        PanSearchResult.Children.Clear()
        RunInNewThread(
        Sub()
            Try
                Dim Items As List(Of FriendContact) = ModFriend.SearchUsers(Keyword)
                RunInUi(Sub() BuildSearchResult(Items))
            Catch ex As Exception
                RunInUi(Sub() LabSearchHint.Text = "搜索失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Search")
    End Sub

    Private Sub BuildSearchResult(Items As List(Of FriendContact))
        PanSearchResult.Children.Clear()
        If Items Is Nothing OrElse Items.Count = 0 Then
            LabSearchHint.Text = "没有找到匹配的用户。"
            Return
        End If
        LabSearchHint.Text = $"找到 {Items.Count} 个用户。"
        Dim Incoming As Integer = 0
        For Each Contact As FriendContact In Items
            Dim Row As New Grid With {.Height = 38, .Margin = New Thickness(0, 0, 0, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Info.Children.Add(New TextBlock With {.Text = $"{Contact.Nickname}（{Contact.Email}）", .FontSize = 13})
            Row.Children.Add(Info)

            '关系取值由 ModFriend 统一换算过：""（可加）/ friend / pending / incoming / self
            Dim Relation As String = If(Contact.Relation, "").Trim().ToLowerInvariant()
            Dim BtnText As String
            Dim CanAdd As Boolean = False
            Select Case Relation
                Case "friend"
                    BtnText = "已是好友"
                Case "pending"
                    BtnText = "已发送申请"
                Case "incoming"
                    BtnText = "对方已申请"
                    Incoming += 1
                Case "self"
                    BtnText = "这是你"
                Case Else
                    BtnText = "加好友"
                    CanAdd = True
            End Select
            Dim Btn As New MyButton With {
                .Text = BtnText,
                .MinWidth = 96, .Height = 28, .Padding = New Thickness(10, 0, 10, 0),
                .IsEnabled = CanAdd
            }
            If CanAdd Then Btn.ColorType = MyButton.ColorState.Highlight
            AddHandler Btn.Click, Sub(sender As Object, e As MouseButtonEventArgs) AddFriend(Contact)
            Grid.SetColumn(Btn, 1)
            Row.Children.Add(Btn)
            PanSearchResult.Children.Add(Row)
        Next
        If Incoming > 0 Then
            LabSearchHint.Text = $"找到 {Items.Count} 个用户。" &
                If(Incoming = Items.Count, "对方已经向你发送过申请，到下方「好友请求」里同意即可。", "")
        End If
    End Sub

    Private Sub AddFriend(Contact As FriendContact)
        RunInNewThread(
        Sub()
            Try
                ModFriend.AddFriend(Contact.Id, "你好，我是 " & ModFriend.MyName)
                RunInUi(
                Sub()
                    LabSearchHint.Text = $"已向 {Contact.Nickname} 发送好友请求。"
                    DoSearch()
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabSearchHint.Text = "发送失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Add")
    End Sub

#End Region

End Class
