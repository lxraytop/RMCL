Public Class PageFriendContact

    Private LastRefresh As Long = 0

    Private Sub PageFriendContact_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        RefreshAll(True)
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
                Dim Requests As JArray = Nothing
                Try
                    Dim Data As JObject = ModFriend.FriendApi("requests")
                    If Data IsNot Nothing Then Requests = TryCast(Data("items"), JArray)
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
            NameRow.Children.Add(New System.Windows.Shapes.Ellipse With {
                .Width = 7, .Height = 7,
                .Fill = If(Contact.Online, New SolidColorBrush(Color.FromRgb(46, 160, 67)), New SolidColorBrush(Color.FromRgb(160, 166, 176))),
                .Margin = New Thickness(0, 0, 5, 0),
                .VerticalAlignment = VerticalAlignment.Center
            })
            NameRow.Children.Add(New TextBlock With {
                .Text = If(String.IsNullOrEmpty(Contact.Remark), Contact.Nickname, $"{Contact.Remark}（{Contact.Nickname}）"),
                .FontSize = 13,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            NameRow.Children.Add(New TextBlock With {
                .Text = If(Contact.Online, "在线", "离线"),
                .FontSize = 10.5,
                .Opacity = If(Contact.Online, 0.85, 0.45),
                .Margin = New Thickness(5, 0, 0, 0),
                .VerticalAlignment = VerticalAlignment.Center
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
                ModFriend.FriendApi("friend_delete", New JObject From {{"user_id", Contact.Id}})
                RunInUi(Sub() RefreshAll(True))
            Catch ex As Exception
                RunInUi(Sub() Hint("删除失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Delete")
    End Sub

#End Region

#Region "好友请求"

    Private Sub BuildRequests(Items As JArray)
        PanRequests.Children.Clear()
        If Items Is Nothing OrElse Items.Count = 0 Then
            LabRequestEmpty.Visibility = Visibility.Visible
            Return
        End If
        LabRequestEmpty.Visibility = Visibility.Collapsed
        For Each Token As JToken In Items
            Dim Obj As JObject = CType(Token, JObject)
            Dim RequestId As Long = Obj.Value(Of Long)("id")
            Dim Name As String = Obj.Value(Of String)("from_name")
            Dim Email As String = Obj.Value(Of String)("from_email")
            Dim Message As String = Obj.Value(Of String)("message")

            Dim Row As New Grid With {.Height = 42, .Margin = New Thickness(0, 0, 0, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Info.Children.Add(New TextBlock With {
                .Text = $"{Name}（{Email}）",
                .FontSize = 13,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Info.Children.Add(New TextBlock With {
                .Text = If(String.IsNullOrEmpty(Message), "请求加你为好友", Message),
                .FontSize = 11,
                .Opacity = 0.55,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Row.Children.Add(Info)

            Dim BtnAccept As New MyButton With {
                .Text = "同意", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .ColorType = MyButton.ColorState.Highlight, .Margin = New Thickness(0, 0, 8, 0)
            }
            AddHandler BtnAccept.Click, Sub(sender As Object, e As MouseButtonEventArgs) AnswerRequest(RequestId, True)
            Grid.SetColumn(BtnAccept, 1)
            Row.Children.Add(BtnAccept)

            Dim BtnReject As New MyButton With {
                .Text = "拒绝", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0)
            }
            AddHandler BtnReject.Click, Sub(sender As Object, e As MouseButtonEventArgs) AnswerRequest(RequestId, False)
            Grid.SetColumn(BtnReject, 2)
            Row.Children.Add(BtnReject)

            PanRequests.Children.Add(Row)
        Next
    End Sub

    Private Sub AnswerRequest(RequestId As Long, Accept As Boolean)
        RunInNewThread(
        Sub()
            Try
                ModFriend.FriendApi(If(Accept, "friend_accept", "friend_reject"), New JObject From {{"request_id", RequestId}})
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
                Dim Data As JObject = ModFriend.FriendApi("search", New JObject From {{"keyword", Keyword}})
                Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
                RunInUi(Sub() BuildSearchResult(Arr))
            Catch ex As Exception
                RunInUi(Sub() LabSearchHint.Text = "搜索失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Search")
    End Sub

    Private Sub BuildSearchResult(Items As JArray)
        PanSearchResult.Children.Clear()
        If Items Is Nothing OrElse Items.Count = 0 Then
            LabSearchHint.Text = "没有找到匹配的用户。"
            Return
        End If
        LabSearchHint.Text = $"找到 {Items.Count} 个用户。"
        For Each Token As JToken In Items
            Dim Obj As JObject = CType(Token, JObject)
            Dim Contact As FriendContact = ModFriend.ParseContact(Obj)
            Dim Row As New Grid With {.Height = 38, .Margin = New Thickness(0, 0, 0, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Info.Children.Add(New TextBlock With {.Text = $"{Contact.Nickname}（{Contact.Email}）", .FontSize = 13})
            Row.Children.Add(Info)

            Dim Relation As String = Contact.Relation
            Dim Btn As New MyButton With {
                .Text = If(Relation = "friend", "已是好友", If(Relation = "pending", "已发送申请", If(Relation = "self", "这是你", "加好友"))),
                .MinWidth = 96, .Height = 28, .Padding = New Thickness(10, 0, 10, 0),
                .IsEnabled = String.IsNullOrEmpty(Relation)
            }
            If String.IsNullOrEmpty(Relation) Then Btn.ColorType = MyButton.ColorState.Highlight
            AddHandler Btn.Click, Sub(sender As Object, e As MouseButtonEventArgs) AddFriend(Contact)
            Grid.SetColumn(Btn, 1)
            Row.Children.Add(Btn)
            PanSearchResult.Children.Add(Row)
        Next
    End Sub

    Private Sub AddFriend(Contact As FriendContact)
        RunInNewThread(
        Sub()
            Try
                ModFriend.FriendApi("add", New JObject From {{"user_id", Contact.Id}, {"message", "你好，我是 " & ModFriend.MyName}})
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
