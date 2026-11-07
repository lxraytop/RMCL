Public Class PageFriendHistory

    Private LastBefore As Long = 0
    Private HasMore As Boolean = False

    Private Sub PageFriendHistory_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
    End Sub

    Private Sub BtnSearch_Click(sender As Object, e As EventArgs) Handles BtnSearch.Click
        LastBefore = 0
        PanResult.Children.Clear()
        Query(False)
    End Sub

    Private Sub TextKeyword_KeyDown(sender As Object, e As KeyEventArgs) Handles TextKeyword.PreviewKeyDown
        If e.Key = Key.Enter Then
            e.Handled = True
            LastBefore = 0
            PanResult.Children.Clear()
            Query(False)
        End If
    End Sub

    Private Sub BtnLoadMore_Click(sender As Object, e As EventArgs) Handles BtnLoadMore.Click
        Query(True)
    End Sub

    Private Sub Query(Append As Boolean)
        If Not ModFriend.IsLoggedIn Then Return
        '服务端只认 all / friend / group，下拉里用的是 0/1/2
        Dim Scope As String = "all"
        Dim Item As MyComboBoxItem = TryCast(ComboScope.SelectedItem, MyComboBoxItem)
        If Item IsNot Nothing Then
            Select Case Val(Item.Tag)
                Case 1
                    Scope = "friend"
                Case 2
                    Scope = "group"
            End Select
        End If
        Dim Param As New JObject From {
            {"keyword", TextKeyword.Text.Trim()},
            {"scope", Scope},
            {"before_id", If(Append, LastBefore, 0L)},
            {"limit", 40}
        }
        LabHint.Text = "正在查询……"
        BtnLoadMore.IsEnabled = False
        RunInNewThread(
        Sub()
            Try
                Dim Data As JObject = ModFriend.FriendApi("history", Param, True, 20000)
                Dim Arr As JArray = If(Data Is Nothing, Nothing, TryCast(Data("items"), JArray))
                Dim MaxId As Long = If(Data Is Nothing, 0L, Data.Value(Of Long)("next_before"))
                RunInUi(
                Sub()
                    If Not Append Then PanResult.Children.Clear()
                    Dim Count As Integer = If(Arr Is Nothing, 0, Arr.Count)
                    If Arr IsNot Nothing Then
                        For Each Token As JToken In Arr
                            PanResult.Children.Add(BuildRow(CType(Token, JObject)))
                        Next
                    End If
                    HasMore = Count >= 40
                    BtnLoadMore.Visibility = If(HasMore, Visibility.Visible, Visibility.Collapsed)
                    BtnLoadMore.IsEnabled = True
                    If Append Then
                        LabHint.Text = $"又加载了 {Count} 条。"
                    Else
                        LabHint.Text = If(Count = 0, "没有找到匹配的记录。", $"找到 {Count} 条记录。")
                    End If
                    If MaxId > 0 Then LastBefore = MaxId
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    LabHint.Text = "查询失败：" & ModFriend.FriendErrorText(ex)
                    BtnLoadMore.IsEnabled = True
                End Sub)
            End Try
        End Sub, "Friend History")
    End Sub

    Private Function BuildRow(Obj As JObject) As FrameworkElement
        Dim ConvType As Integer = Obj.Value(Of Integer)("conv_type")
        Dim ConvKey As String = Obj.Value(Of String)("conv_key")
        Dim Title As String = Obj.Value(Of String)("title")
        Dim FromName As String = Obj.Value(Of String)("from_name")
        Dim Body As String = Obj.Value(Of String)("body")
        Dim CreatedAt As Long = Obj.Value(Of Long)("created_at")

        Dim Row As New Border With {
            .CornerRadius = New CornerRadius(9),
            .Padding = New Thickness(10, 8, 10, 8),
            .Margin = New Thickness(0, 0, 0, 5),
            .Background = TryCast(FindResource("ColorBrushGlassInner"), Brush),
            .Cursor = Cursors.Hand
        }
        Dim Layout As New Grid()
        Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim Stack As New StackPanel()
        Stack.Children.Add(New TextBlock With {
            .Text = $"{Title} · {FromName}",
            .FontSize = 12,
            .Opacity = 0.6,
            .TextTrimming = TextTrimming.CharacterEllipsis
        })
        Stack.Children.Add(New TextBlock With {
            .Text = Body,
            .FontSize = 13,
            .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 2, 0, 0)
        })
        Layout.Children.Add(Stack)

        Dim Time As New TextBlock With {
            .Text = FormatTime(CreatedAt),
            .FontSize = 10.5,
            .Opacity = 0.45,
            .Margin = New Thickness(10, 1, 0, 0),
            .VerticalAlignment = VerticalAlignment.Top
        }
        Grid.SetColumn(Time, 1)
        Layout.Children.Add(Time)
        Row.Child = Layout

        AddHandler Row.MouseLeftButtonUp,
            Sub(sender As Object, e As MouseButtonEventArgs)
                FrmFriendLeft.OpenChat(ConvType, ConvKey, Title)
            End Sub
        Return Row
    End Function

    Private Function FormatTime(Unix As Long) As String
        If Unix <= 0 Then Return ""
        Try
            Dim D As Date = New Date(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().AddSeconds(Unix)
            If D.Date = Date.Now.Date Then Return D.ToString("HH:mm")
            Return D.ToString("yyyy-MM-dd HH:mm")
        Catch
            Return ""
        End Try
    End Function

End Class
