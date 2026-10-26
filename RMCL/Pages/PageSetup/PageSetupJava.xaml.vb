Public Class PageSetupJava

    Private ReadOnly RefreshTimer As New Windows.Threading.DispatcherTimer
    Private DownloadList As New List(Of ModJava.JavaDownloadInfo)
    Private IsDownloadListLoading As Boolean
    Private IsDownloadListLoaded As Boolean
    Private IsDownloading As Boolean
    Private CurrentLoader As LoaderCombo(Of Integer)

    ''' <summary>下载目标根目录：RMCL.exe 同级的 Java 文件夹。</summary>
    Private ReadOnly Property DownloadRoot As String
        Get
            Return Paths.Base & "Java\"
        End Get
    End Property

    Private Sub PageSetupJava_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If RefreshTimer.Interval = TimeSpan.Zero Then
            RefreshTimer.Interval = TimeSpan.FromMilliseconds(500)
            AddHandler RefreshTimer.Tick, AddressOf RefreshTimer_Tick
        End If
        RefreshTimer.Start()
        LabJavaTarget.Text = $"下载位置：{DownloadRoot}（RMCL.exe 同级的 Java 文件夹，下载后会自动加入下面的列表）"
        UpdateJavaList()
        If Not IsDownloadListLoaded Then LoadDownloadList()
    End Sub

    Private Sub PageSetupJava_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        RefreshTimer.Stop()
    End Sub

    Private Sub RefreshTimer_Tick(sender As Object, e As EventArgs)
        '下载中只更新进度文字，避免每 500ms 重建列表导致按钮闪烁
        If Not IsDownloading OrElse CurrentLoader Is Nothing Then Return
        Try
            Dim Progress As Double = Math.Max(0, Math.Min(1, CurrentLoader.Progress))
            LabJavaDownloadStatus.Text = $"正在下载 Java…… {Progress * 100:F0}%"
        Catch ex As Exception
            Logger.Warn(ex, "读取 Java 下载进度失败")
        End Try
    End Sub

#Region "列表"

    ''' <summary>
    ''' 重建 Java 列表。由页面加载、操作后以及 Java 扫描结束时调用。
    ''' </summary>
    Public Sub UpdateJavaList()
        If PanJavaList Is Nothing OrElse Not PanJavaList.IsLoaded Then Return
        Try
            PanJavaList.Children.Clear()
            Dim Searching As Boolean = JavaListRefreshWorker.Running
            Dim Entries As List(Of Java) = Configs.JavaList.Get().ToList()

            If Searching Then
                LabJavaSummary.Text = "正在扫描本机的 Java……"
            ElseIf Entries.Count > 0 Then
                LabJavaSummary.Text = $"共找到 {Entries.Count} 个可用的 Java。启动游戏时会按下面的顺序挑选兼容的版本。"
            Else
                LabJavaSummary.Text = "尚未找到任何 Java。"
            End If

            LabJavaEmpty.Visibility = If(Not Searching AndAlso Entries.Count = 0, Visibility.Visible, Visibility.Collapsed)
            BtnJavaRescan.IsEnabled = Not Searching AndAlso Not IsDownloading
            BtnJavaImport.IsEnabled = Not Searching AndAlso Not IsDownloading

            For i As Integer = 0 To Entries.Count - 1
                PanJavaList.Children.Add(BuildJavaRow(Entries(i), i, Entries.Count))
            Next
        Catch ex As Exception
            Logger.Warn(ex, "刷新 Java 列表失败")
        End Try
    End Sub

    ''' <summary>一行 Java：左侧标题与路径，右侧上移、下移、移除。</summary>
    Private Function BuildJavaRow(Entry As Java, Index As Integer, Total As Integer) As UIElement
        Dim Row As New Border With {
            .CornerRadius = New CornerRadius(10),
            .Padding = New Thickness(12, 8, 10, 8),
            .Margin = New Thickness(0, 0, 0, 6)
        }
        Row.SetResourceReference(Border.BackgroundProperty, "ColorBrushGlassInner")

        Dim Layout As New Grid
        Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Layout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim Info As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
        Dim Title As New TextBlock With {
            .Text = Entry.ToString,
            .FontSize = 12.5,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        Title.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
        Dim PathText As New TextBlock With {
            .Text = Entry.Folder,
            .FontSize = 11,
            .Opacity = 0.6,
            .Margin = New Thickness(0, 3, 0, 0),
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        Info.Children.Add(Title)
        Info.Children.Add(PathText)
        Layout.Children.Add(Info)

        Dim Ops As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
        Dim BtnUp As New MyButton With {
            .Text = "上移", .MinWidth = 62, .Height = 26,
            .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 6, 0)
        }
        BtnUp.IsEnabled = Index > 0
        AddHandler BtnUp.Click, Sub() MoveJava(Index, -1)

        Dim BtnDown As New MyButton With {
            .Text = "下移", .MinWidth = 62, .Height = 26,
            .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 6, 0)
        }
        BtnDown.IsEnabled = Index < Total - 1
        AddHandler BtnDown.Click, Sub() MoveJava(Index, 1)

        Dim BtnRemove As New MyButton With {
            .Text = "移除", .MinWidth = 62, .Height = 26, .Padding = New Thickness(8, 0, 8, 0)
        }
        AddHandler BtnRemove.Click, Sub() ModJava.ManuallyRemoveJava(Entry)

        Ops.Children.Add(BtnUp)
        Ops.Children.Add(BtnDown)
        Ops.Children.Add(BtnRemove)
        Grid.SetColumn(Ops, 1)
        Layout.Children.Add(Ops)

        Row.Child = Layout
        Return Row
    End Function

    ''' <summary>调整某个 Java 在列表中的位置，靠上的会被优先选用。</summary>
    Private Sub MoveJava(Index As Integer, Delta As Integer)
        Try
            Dim Entries As List(Of Java) = Configs.JavaList.Get().ToList()
            Dim Target As Integer = Index + Delta
            If Target < 0 OrElse Target >= Entries.Count Then Return
            Dim Item As Java = Entries(Index)
            Entries.RemoveAt(Index)
            Entries.Insert(Target, Item)
            '整体写回，避免依赖集合自身的插入删除语义
            Configs.JavaList.Set(New ConcurrentList(Of Java)(Entries))
            ModJava.UpdateJavaLists()
            UpdateJavaList()
        Catch ex As Exception
            Logger.Warn(ex, "调整 Java 顺序失败")
            Hint("调整顺序失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnJavaRescan_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaRescan.Click
        LabJavaSummary.Text = "正在扫描本机的 Java……"
        ModJava.ManuallySearchJava()
        UpdateJavaList()
    End Sub

    Private Sub BtnJavaImport_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaImport.Click
        ModJava.ManuallyImportJava()
        UpdateJavaList()
    End Sub

#End Region

#Region "下载"

    Private Sub BtnJavaDownloadToggle_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaDownloadToggle.Click
        PanJavaDownload.Visibility = If(PanJavaDownload.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
        If PanJavaDownload.Visibility = Visibility.Visible AndAlso Not IsDownloadListLoaded Then LoadDownloadList()
    End Sub

    ''' <summary>读取 Mojang 提供的可下载 Java 列表并填入下拉框。</summary>
    Private Sub LoadDownloadList()
        If IsDownloadListLoading Then Return
        IsDownloadListLoading = True
        ComboJavaVersion.Items.Clear()
        LabJavaDownloadStatus.Text = "正在获取可下载的 Java 版本……"
        RunInNewThread(
        Sub()
            Dim Result As List(Of ModJava.JavaDownloadInfo) = Nothing
            Dim Failure As Exception = Nothing
            Try
                Result = ModJava.GetJavaDownloadList()
            Catch ex As Exception
                Failure = ex
            End Try
            RunInUi(
            Sub()
                IsDownloadListLoading = False
                If Failure IsNot Nothing Then
                    Logger.Warn(Failure, "获取 Java 下载列表失败")
                    LabJavaDownloadStatus.Text = "获取版本列表失败：" & Failure.Message
                    Return
                End If
                DownloadList = Result
                IsDownloadListLoaded = True
                ComboJavaVersion.Items.Clear()
                '按大版本去重，只保留每个版本线里最新的一个
                Dim Seen As New HashSet(Of Integer)
                For Each Item As ModJava.JavaDownloadInfo In DownloadList
                    If Not Seen.Add(Item.JavaVersion.Major) Then Continue For
                    ComboJavaVersion.Items.Add(New MyComboBoxItem With {
                        .Content = $"{Item.DisplayName}（{Item.ComponentName}）",
                        .Tag = Item
                    })
                Next
                If ComboJavaVersion.Items.Count > 0 Then ComboJavaVersion.SelectedIndex = 0
                LabJavaDownloadStatus.Text = $"共 {ComboJavaVersion.Items.Count} 个版本可选，选好后点「开始下载」。"
            End Sub)
        End Sub, "获取 Java 下载列表")
    End Sub

    Private Sub BtnJavaDownloadStart_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaDownloadStart.Click
        If IsDownloading Then Return
        Dim Item As MyComboBoxItem = TryCast(ComboJavaVersion.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then
            Hint("请先选择一个要下载的 Java 版本", HintType.Red)
            Return
        End If
        Dim Target As ModJava.JavaDownloadInfo = DirectCast(Item.Tag, ModJava.JavaDownloadInfo)
        IsDownloading = True
        BtnJavaDownloadStart.IsEnabled = False
        BtnJavaDownloadToggle.IsEnabled = False
        LabJavaDownloadStatus.Text = $"正在下载 {Target.DisplayName}……"
        UpdateJavaList()

        RunInNewThread(
        Sub()
            Dim Failure As Exception = Nothing
            Dim Installed As Java = Nothing
            Try
                Dim Loader As LoaderCombo(Of Integer) = ModJava.CreateJavaDownloadLoader(Target, DownloadRoot)
                CurrentLoader = Loader
                Try
                    Loader.WaitForExit(IsForceRestart:=True)
                Finally
                    Loader.Cancel()
                    CurrentLoader = Nothing
                End Try
                '下载位置不在自动扫描范围里，这里手动登记
                Dim Folder As String = DownloadRoot & Target.ComponentName
                Dim NewJava As New Java(Folder)
                If NewJava.CheckAsync().Run() Then
                    Configs.JavaRemovedList.Edit(Sub(ByRef List) List.RemoveIf(Function(f) PathUtils.IsSame(f, NewJava.Folder)))
                    Configs.JavaList.Edit(
                    Sub(ByRef List)
                        List.Remove(NewJava)
                        List.Insert(0, NewJava)
                    End Sub)
                    Installed = NewJava
                Else
                    Failure = New Exception("下载完成但无法识别为可用的 Java，请检查目录：" & Folder)
                End If
            Catch ex As Exception
                Failure = ex
            End Try

            RunInUi(
            Sub()
                IsDownloading = False
                BtnJavaDownloadStart.IsEnabled = True
                BtnJavaDownloadToggle.IsEnabled = True
                If Failure IsNot Nothing Then
                    Logger.Warn(Failure, "下载 Java 失败")
                    LabJavaDownloadStatus.Text = "下载失败：" & Failure.Message
                    Hint("下载 Java 失败：" & Failure.Message, HintType.Red)
                Else
                    LabJavaDownloadStatus.Text = $"已安装：{Installed}（{Installed.Folder}），已置于列表顶部。"
                    Hint("Java 下载完成，已加入列表", HintType.Green)
                End If
                ModJava.UpdateJavaLists()
                UpdateJavaList()
            End Sub)
        End Sub, "下载 Java")
    End Sub

#End Region

End Class
