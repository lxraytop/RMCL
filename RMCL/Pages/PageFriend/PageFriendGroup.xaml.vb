Public Class PageFriendGroup

    Private LastRefresh As Long = 0
    Private ReadOnly Friends As New List(Of FriendContact)
    Private ReadOnly Picks As New Dictionary(Of Long, MyCheckBox)
    Private ManagingGroup As FriendGroup = Nothing
    Private ReadOnly ManagingMembers As New List(Of FriendGroupMember)
    Private ReadOnly InvitePicks As New Dictionary(Of Long, MyCheckBox)

    Private Sub PageFriendGroup_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        Refresh(True)
    End Sub

    Private Sub PageFriendGroup_PageEnter() Handles Me.PageEnter
        Refresh(False)
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As EventArgs) Handles BtnRefresh.Click
        Refresh(True)
    End Sub

    Private Sub Refresh(Force As Boolean)
        If Not ModFriend.IsLoggedIn Then Return
        If Not Force AndAlso GetTimeMs() - LastRefresh < 3000 Then Return
        LastRefresh = GetTimeMs()
        RunInNewThread(
        Sub()
            Try
                Dim Groups As List(Of FriendGroup) = ModFriend.GetGroups()
                Dim Contacts As List(Of FriendContact) = ModFriend.GetContacts()
                RunInUi(
                Sub()
                    Friends.Clear()
                    Friends.AddRange(Contacts)
                    BuildFriendPick()
                    BuildGroups(Groups)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabGroupHint.Text = "加载失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Groups")
    End Sub

    Private Sub BuildFriendPick()
        PanFriendPick.Children.Clear()
        Picks.Clear()
        If Friends.Count = 0 Then
            PanFriendPick.Children.Add(New TextBlock With {
                .Text = "通讯录里还没有好友，先去「通讯录」加几个人吧。", .FontSize = 12, .Opacity = 0.6, .Margin = New Thickness(2, 4, 0, 0)
            })
            Return
        End If
        For Each Contact As FriendContact In Friends
            Dim Check As New MyCheckBox With {
                .Text = Contact.Nickname,
                .Height = 24,
                .Margin = New Thickness(0, 0, 0, 2),
                .Tag = Contact.Id
            }
            PanFriendPick.Children.Add(Check)
            Picks(Contact.Id) = Check
        Next
    End Sub

    Private Sub BuildGroups(Groups As List(Of FriendGroup))
        PanGroups.Children.Clear()
        LabGroupHint.Text = If(Groups.Count = 0, "还没有加入任何群聊", $"{Groups.Count} 个群聊")
        For Each Item As FriendGroup In Groups
            Dim Row As New Grid With {.Height = 40, .Margin = New Thickness(0, 0, 0, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Info.Children.Add(New TextBlock With {.Text = Item.Name, .FontSize = 13, .TextTrimming = TextTrimming.CharacterEllipsis})
            Info.Children.Add(New TextBlock With {
                .Text = $"{Item.MemberCount} 位成员" & If(Item.OwnerId = ModFriend.MyId, " · 我是群主", ""),
                .FontSize = 11, .Opacity = 0.55
            })
            Row.Children.Add(Info)

            Dim BtnChat As New MyButton With {
                .Text = "聊天", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .ColorType = MyButton.ColorState.Highlight, .Margin = New Thickness(0, 0, 8, 0)
            }
            AddHandler BtnChat.Click, Sub(sender As Object, e As MouseButtonEventArgs) FrmFriendLeft.OpenChat(1, Item.Id.ToString(), Item.Name)
            Grid.SetColumn(BtnChat, 1)
            Row.Children.Add(BtnChat)

            Dim BtnMembers As New MyButton With {
                .Text = "成员", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .Margin = New Thickness(0, 0, 8, 0)
            }
            AddHandler BtnMembers.Click, Sub(sender As Object, e As MouseButtonEventArgs) ShowMembers(Item)
            Grid.SetColumn(BtnMembers, 2)
            Row.Children.Add(BtnMembers)

            If Item.OwnerId = ModFriend.MyId Then
                Dim BtnManage As New MyButton With {
                    .Text = "管理", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .Margin = New Thickness(0, 0, 8, 0)
                }
                AddHandler BtnManage.Click, Sub(sender As Object, e As MouseButtonEventArgs) OpenManage(Item)
                Grid.SetColumn(BtnManage, 3)
                Row.Children.Add(BtnManage)
            End If

            Dim BtnLeave As New MyButton With {
                .Text = "退出", .MinWidth = 62, .Height = 28, .Padding = New Thickness(10, 0, 10, 0)
            }
            AddHandler BtnLeave.Click, Sub(sender As Object, e As MouseButtonEventArgs) LeaveGroup(Item)
            Grid.SetColumn(BtnLeave, 4)
            Row.Children.Add(BtnLeave)

            PanGroups.Children.Add(Row)
        Next
    End Sub

    Private Sub BtnCreateGroup_Click(sender As Object, e As EventArgs) Handles BtnCreateGroup.Click
        Dim Name As String = TextGroupName.Text.Trim()
        If Name.Length < 2 OrElse Name.Length > 24 Then
            LabCreateHint.Text = "群名称需要 2-24 个字符。"
            Return
        End If
        Dim Members As New JArray()
        For Each Pair As KeyValuePair(Of Long, MyCheckBox) In Picks
            If Pair.Value.Checked Then Members.Add(Pair.Key)
        Next
        If Members.Count = 0 Then
            LabCreateHint.Text = "至少选择一位好友。"
            Return
        End If
        LabCreateHint.Text = "正在创建……"
        BtnCreateGroup.IsEnabled = False
        RunInNewThread(
        Sub()
            Try
                Dim Param As New JObject From {{"name", Name}, {"members", Members}}
                Dim Data As JObject = ModFriend.FriendApi("group_create", Param, True, 20000)
                Dim Group As FriendGroup = ModFriend.ParseGroup(CType(Data("group"), JObject))
                RunInUi(
                Sub()
                    LabCreateHint.Text = $"群聊「{Group.Name}」已创建。"
                    TextGroupName.Text = ""
                    BtnCreateGroup.IsEnabled = True
                    Refresh(True)
                    FrmFriendLeft.OpenChat(1, Group.Id.ToString(), Group.Name)
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    LabCreateHint.Text = "创建失败：" & ModFriend.FriendErrorText(ex)
                    BtnCreateGroup.IsEnabled = True
                End Sub)
            End Try
        End Sub, "Friend Create Group")
    End Sub

    Private Sub ShowMembers(Item As FriendGroup)
        RunInNewThread(
        Sub()
            Try
                Dim Data As JObject = ModFriend.FriendApi("group_info", New JObject From {{"group_id", Item.Id}})
                Dim Lines As New List(Of String)
                Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("members"), JArray))
                If Arr IsNot Nothing Then
                    For Each Token As JToken In Arr
                        Dim Obj As JObject = CType(Token, JObject)
                        Lines.Add($"{Obj.Value(Of String)("nickname")}（{Obj.Value(Of String)("email")}）" &
                                  If(Obj.Value(Of Integer)("role") = 1, " · 群主", ""))
                    Next
                End If
                RunInUi(Sub() MyMsgBox(String.Join(vbCrLf, Lines), $"群成员 · {Item.Name}", "关闭"))
            Catch ex As Exception
                RunInUi(Sub() Hint("读取群成员失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Group Members")
    End Sub

#Region "群成员管理"

    ''' <summary>
    ''' 打开某个群的成员管理面板（仅群主可见入口）。
    ''' </summary>
    Private Sub OpenManage(Item As FriendGroup)
        ManagingGroup = Item
        CardManage.Visibility = Visibility.Visible
        LabManageTitle.Text = $"群成员管理 · {Item.Name}"
        PanMembers.Children.Clear()
        PanInvite.Children.Clear()
        InvitePicks.Clear()
        LabManageHint.Text = "正在加载成员……"
        LoadManageData()
    End Sub

    Private Sub LoadManageData()
        If ManagingGroup Is Nothing Then Return
        Dim GroupId As Long = ManagingGroup.Id
        RunInNewThread(
        Sub()
            Try
                Dim Members As List(Of FriendGroupMember) = ModFriend.GetGroupMembers(GroupId)
                Dim Contacts As List(Of FriendContact) = ModFriend.GetContacts()
                RunInUi(
                Sub()
                    If ManagingGroup Is Nothing OrElse ManagingGroup.Id <> GroupId Then Return
                    ManagingMembers.Clear()
                    ManagingMembers.AddRange(Members)
                    BuildManagingMembers()
                    BuildInvitePicks(Contacts)
                    LabManageHint.Text = $"{Members.Count} 位成员，可勾选好友后邀请入群。"
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabManageHint.Text = "加载失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Group Manage")
    End Sub

    Private Sub BuildManagingMembers()
        PanMembers.Children.Clear()
        For Each Member As FriendGroupMember In ManagingMembers
            Dim Row As New Grid With {.Height = 36, .Margin = New Thickness(0, 0, 0, 3)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Info.Children.Add(New TextBlock With {
                .Text = Member.Nickname & If(Member.Role = 1, "（群主）", ""),
                .FontSize = 13,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Info.Children.Add(New TextBlock With {.Text = Member.Email, .FontSize = 11, .Opacity = 0.55})
            Row.Children.Add(Info)

            If Member.Id <> ModFriend.MyId Then
                Dim BtnKick As New MyButton With {
                    .Text = "移出", .MinWidth = 62, .Height = 26, .Padding = New Thickness(10, 0, 10, 0)
                }
                AddHandler BtnKick.Click, Sub(sender As Object, e As MouseButtonEventArgs) KickMember(Member)
                Grid.SetColumn(BtnKick, 1)
                Row.Children.Add(BtnKick)
            End If
            PanMembers.Children.Add(Row)
        Next
    End Sub

    Private Sub BuildInvitePicks(Contacts As List(Of FriendContact))
        PanInvite.Children.Clear()
        InvitePicks.Clear()
        Dim Existing As New HashSet(Of Long)(ManagingMembers.Select(Function(m) m.Id))
        Dim Candidates As List(Of FriendContact) = Contacts.Where(Function(c) Not Existing.Contains(c.Id)).ToList()
        If Candidates.Count = 0 Then
            PanInvite.Children.Add(New TextBlock With {
                .Text = "没有可邀请的好友（好友都已在群里）。", .FontSize = 12, .Opacity = 0.6, .Margin = New Thickness(2, 2, 0, 0)
            })
            Return
        End If
        For Each Contact As FriendContact In Candidates
            Dim Check As New MyCheckBox With {
                .Text = $"{Contact.Nickname}（{Contact.Email}）",
                .Height = 24,
                .Margin = New Thickness(0, 0, 0, 2),
                .Tag = Contact.Id
            }
            PanInvite.Children.Add(Check)
            InvitePicks(Contact.Id) = Check
        Next
    End Sub

    Private Sub KickMember(Member As FriendGroupMember)
        If ManagingGroup Is Nothing Then Return
        If MyMsgBox($"将「{Member.Nickname}」移出群聊？", "移出成员", "移出", "取消", IsWarn:=True) <> 1 Then Return
        Dim GroupId As Long = ManagingGroup.Id
        RunInNewThread(
        Sub()
            Try
                ModFriend.FriendApi("group_kick", New JObject From {{"group_id", GroupId}, {"user_id", Member.Id}})
                RunInUi(
                Sub()
                    Hint($"已将 {Member.Nickname} 移出群聊", HintType.Green, False)
                    LoadManageData()
                    Refresh(True)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabManageHint.Text = "移出失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Group Kick")
    End Sub

    Private Sub BtnInvite_Click(sender As Object, e As EventArgs) Handles BtnInvite.Click
        If ManagingGroup Is Nothing Then Return
        Dim Members As New JArray()
        For Each Pair As KeyValuePair(Of Long, MyCheckBox) In InvitePicks
            If Pair.Value.Checked Then Members.Add(Pair.Key)
        Next
        If Members.Count = 0 Then
            LabManageHint.Text = "请先勾选要邀请的好友。"
            Return
        End If
        Dim GroupId As Long = ManagingGroup.Id
        LabManageHint.Text = "正在邀请……"
        RunInNewThread(
        Sub()
            Try
                Dim Param As New JObject From {{"group_id", GroupId}, {"members", Members}}
                ModFriend.FriendApi("group_add_members", Param)
                RunInUi(
                Sub()
                    Hint("邀请已发送，成员已入群", HintType.Green, False)
                    LoadManageData()
                    Refresh(True)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabManageHint.Text = "邀请失败：" & ModFriend.FriendErrorText(ex))
            End Try
        End Sub, "Friend Group Invite")
    End Sub

    Private Sub BtnManageClose_Click(sender As Object, e As EventArgs) Handles BtnManageClose.Click
        ManagingGroup = Nothing
        CardManage.Visibility = Visibility.Collapsed
    End Sub

#End Region

    Private Sub LeaveGroup(Item As FriendGroup)
        If MyMsgBox($"退出群聊「{Item.Name}」？", "退出群聊", "退出", "取消", IsWarn:=True) <> 1 Then Return
        RunInNewThread(
        Sub()
            Try
                ModFriend.FriendApi("group_leave", New JObject From {{"group_id", Item.Id}})
                RunInUi(Sub() Refresh(True))
            Catch ex As Exception
                RunInUi(Sub() Hint("退出失败：" & ModFriend.FriendErrorText(ex), HintType.Red))
            End Try
        End Sub, "Friend Leave Group")
    End Sub

End Class
