Public Class PageSetupJava

    Private ReadOnly RefreshTimer As New Windows.Threading.DispatcherTimer
    Private DownloadList As New List(Of ModJava.JavaDownloadInfo)
    Private DistTargets As New List(Of ModJava.JavaDistTarget)
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
        InitSourceList()
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

        Dim BtnOpen As New MyButton With {
            .Text = "打开", .MinWidth = 56, .Height = 26,
            .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 6, 0)
        }
        BtnOpen.ToolTip = "在资源管理器里打开这个 Java 所在的文件夹"
        AddHandler BtnOpen.Click, Sub() OpenJavaFolder(Entry)

        Dim BtnDelete As New MyButton With {
            .Text = "删除", .MinWidth = 56, .Height = 26,
            .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 6, 0)
        }
        Dim Managed As Boolean = IsManagedJava(Entry)
        BtnDelete.IsEnabled = Managed
        BtnDelete.ToolTip = If(Managed, "删除这个由 RMCL 下载的 Java（连同文件夹）", "只有 RMCL 下载到 Java 文件夹里的版本可以在这里删除")
        AddHandler BtnDelete.Click, Sub() DeleteJava(Entry)

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

        Ops.Children.Add(BtnOpen)
        Ops.Children.Add(BtnDelete)
        Ops.Children.Add(BtnUp)
        Ops.Children.Add(BtnDown)
        Ops.Children.Add(BtnRemove)
        Grid.SetColumn(Ops, 1)
        Layout.Children.Add(Ops)

        Row.Child = Layout
        Return Row
    End Function

    ''' <summary>某个 Java 所在的 JDK 根目录（Java.Folder 指向的是 bin）。</summary>
    Private Function JavaRoot(Entry As Java) As String
        Dim Folder As String = If(Entry.Folder, "").TrimEnd("\"c)
        If Folder.EndsWith("bin", StringComparison.OrdinalIgnoreCase) Then
            Dim Parent As String = IO.Path.GetDirectoryName(Folder)
            If Not String.IsNullOrEmpty(Parent) Then Return Parent.TrimEnd("\"c) & "\"
        End If
        Return Folder & "\"
    End Function

    ''' <summary>是否由 RMCL 下载到自己的 Java 目录（只有这种才允许在这里删除）。</summary>
    Private Function IsManagedJava(Entry As Java) As Boolean
        Try
            Return JavaRoot(Entry).StartsWith(DownloadRoot, StringComparison.OrdinalIgnoreCase)
        Catch
            Return False
        End Try
    End Function

    Private Sub OpenJavaFolder(Entry As Java)
        Try
            System.Diagnostics.Process.Start("explorer.exe", JavaRoot(Entry))
        Catch ex As Exception
            Hint("打开文件夹失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    ''' <summary>删除由 RMCL 下载的 Java（连同文件夹）。</summary>
    Private Sub DeleteJava(Entry As Java)
        If Not IsManagedJava(Entry) Then Return
        Dim Root As String = JavaRoot(Entry)
        If MyMsgBox($"确定要删除 {Entry} 吗？{vbCrLf}会整个删掉 {Root}，无法恢复。", "删除 Java", "删除", "取消", IsWarn:=True) = 2 Then Return
        Try
            DirectoryUtils.Delete(Root)
            Configs.JavaList.Edit(Sub(ByRef List) List.RemoveIf(Function(x) PathUtils.IsSame(x.Folder, Entry.Folder)))
            ModJava.UpdateJavaLists()
            UpdateJavaList()
            Hint("已删除该 Java", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, $"删除 Java 失败：{Root}")
            Hint("删除失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

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

    ''' <summary>当前选中的下载源序号：0 = Mojang 官方运行时，1~4 = 第三方 JDK。</summary>
    Private Function SelectedSourceIndex() As Integer
        Dim Item As MyComboBoxItem = TryCast(ComboJavaSource.SelectedItem, MyComboBoxItem)
        If Item Is Nothing Then Return 0
        If TypeOf Item.Tag Is Integer Then Return DirectCast(Item.Tag, Integer)
        Return 0
    End Function

    Private Sub InitSourceList()
        If ComboJavaSource Is Nothing OrElse ComboJavaSource.Items.Count > 0 Then Return
        For i As Integer = 0 To ModJava.JavaDistSourceNames.Length - 1
            ComboJavaSource.Items.Add(New MyComboBoxItem With {.Content = ModJava.JavaDistSourceNames(i), .Tag = i})
        Next
        ComboJavaSource.SelectedIndex = 0
    End Sub

    Private Sub ComboJavaSource_SelectionChanged(sender As MyComboBox, e As SelectionChangedEventArgs) Handles ComboJavaSource.SelectionChanged
        '换源之后版本列表要重新取
        IsDownloadListLoaded = False
        If PanJavaDownload IsNot Nothing AndAlso PanJavaDownload.Visibility = Visibility.Visible Then LoadDownloadList()
    End Sub

    Private Sub BtnJavaDownloadToggle_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJavaDownloadToggle.Click
        PanJavaDownload.Visibility = If(PanJavaDownload.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
        If PanJavaDownload.Visibility = Visibility.Visible AndAlso Not IsDownloadListLoaded Then LoadDownloadList()
    End Sub

    ''' <summary>读取所选下载源的可用版本并填入下拉框。</summary>
    Private Sub LoadDownloadList()
        If IsDownloadListLoading Then Return
        Dim SourceIndex As Integer = SelectedSourceIndex()
        IsDownloadListLoading = True
        ComboJavaVersion.Items.Clear()
        LabJavaDownloadStatus.Text = "正在获取可下载的 Java 版本……"
        RunInNewThread(
        Sub()
            Dim MojangList As List(Of ModJava.JavaDownloadInfo) = Nothing
            Dim NewDistList As List(Of ModJava.JavaDistTarget) = Nothing
            Dim Failure As Exception = Nothing
            Try
                If SourceIndex = 0 Then
                    MojangList = ModJava.GetJavaDownloadList()
                Else
                    NewDistList = ModJava.GetJavaDistTargets(SourceIndex)
                End If
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
                DownloadList = If(MojangList, New List(Of ModJava.JavaDownloadInfo))
                DistTargets = If(NewDistList, New List(Of ModJava.JavaDistTarget))
                IsDownloadListLoaded = True
                ComboJavaVersion.Items.Clear()
                If SourceIndex = 0 Then
                    '按大版本去重，只保留每个版本线里最新的一个
                    Dim Seen As New HashSet(Of Integer)
                    For Each Info As ModJava.JavaDownloadInfo In DownloadList
                        If Not Seen.Add(Info.JavaVersion.Major) Then Continue For
                        ComboJavaVersion.Items.Add(New MyComboBoxItem With {
                            .Content = $"{Info.DisplayName}（{Info.ComponentName}）",
                            .Tag = Info
                        })
                    Next
                Else
                    For Each Target As ModJava.JavaDistTarget In DistTargets
                        ComboJavaVersion.Items.Add(New MyComboBoxItem With {.Content = Target.Display, .Tag = Target})
                    Next
                End If
                If ComboJavaVersion.Items.Count > 0 Then ComboJavaVersion.SelectedIndex = 0
                If ComboJavaVersion.Items.Count = 0 Then
                    LabJavaDownloadStatus.Text = "这个源暂时没有可下载的版本，换一个源试试。"
                Else
                    LabJavaDownloadStatus.Text = $"共 {ComboJavaVersion.Items.Count} 个版本可选，选好后点「开始下载」。"
                End If
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
        If TypeOf Item.Tag Is ModJava.JavaDistTarget Then
            DownloadDist(DirectCast(Item.Tag, ModJava.JavaDistTarget))
        ElseIf TypeOf Item.Tag Is ModJava.JavaDownloadInfo Then
            DownloadMojang(DirectCast(Item.Tag, ModJava.JavaDownloadInfo))
        End If
    End Sub

    ''' <summary>下载 Mojang 官方运行时（按文件清单下载）。</summary>
    Private Sub DownloadMojang(Target As ModJava.JavaDownloadInfo)
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
                '下载失败时 loader 会顺手把没下完的目录清理掉，这时如果继续往下走，
                '只会得到「无法识别为可用的 Java」，容易让人以为是 Java 本身有问题
                If Loader.State = LoadState.Failed Then
                    Throw New Exception("下载 Java 失败：" & If(Loader.Error Is Nothing, "未知原因", Loader.Error.Message))
                End If
                Dim BaseFolder As String = DownloadRoot & Target.ComponentName
                Dim BinFolder As String = If(FileUtils.Exists(BaseFolder & "\bin\java.exe"), BaseFolder & "\bin\", BaseFolder)
                Installed = RegisterJava(BinFolder)
                If Installed Is Nothing Then Failure = New Exception($"下载完成但无法识别为可用的 Java，请检查目录：{BinFolder}")
            Catch ex As Exception
                Failure = ex
            End Try

            RunInUi(
            Sub()
                FinishDownload(Failure, Installed)
            End Sub)
        End Sub, "下载 Java")
    End Sub

    ''' <summary>下载第三方 JDK（zip 直链，解压后自动登记）。</summary>
    Private Sub DownloadDist(Target As ModJava.JavaDistTarget)
        IsDownloading = True
        BtnJavaDownloadStart.IsEnabled = False
        BtnJavaDownloadToggle.IsEnabled = False
        LabJavaDownloadStatus.Text = $"正在下载 {Target.Display}……"
        UpdateJavaList()

        RunInNewThread(
        Sub()
            Dim Failure As Exception = Nothing
            Dim Installed As Java = Nothing
            Try
                Dim Loader As LoaderCombo(Of Integer) = ModJava.CreateJavaZipLoader(Target, DownloadRoot)
                CurrentLoader = Loader
                Try
                    Loader.WaitForExit(IsForceRestart:=True)
                Finally
                    Loader.Cancel()
                    CurrentLoader = Nothing
                End Try
                If Loader.State = LoadState.Failed Then
                    Throw New Exception("下载 Java 失败：" & If(Loader.Error Is Nothing, "未知原因", Loader.Error.Message))
                End If
                Dim BinFolder As String = ModJava.FindJavaBinFolder(DownloadRoot & Target.Folder & "\")
                If String.IsNullOrEmpty(BinFolder) Then Throw New Exception("压缩包里没有找到 java.exe，可能这个源的文件结构变了")
                Installed = RegisterJava(BinFolder)
                If Installed Is Nothing Then Failure = New Exception($"下载完成但无法识别为可用的 Java，请检查目录：{BinFolder}")
            Catch ex As Exception
                Failure = ex
            End Try

            RunInUi(
            Sub()
                FinishDownload(Failure, Installed)
            End Sub)
        End Sub, "下载 Java")
    End Sub

    ''' <summary>把一个 java.exe 所在目录登记进 Java 列表（置顶）。识别失败返回 Nothing。</summary>
    Private Function RegisterJava(BinFolder As String) As Java
        Dim NewJava As New Java(BinFolder)
        If Not NewJava.CheckAsync().Run() Then Return Nothing
        Configs.JavaRemovedList.Edit(Sub(ByRef List) List.RemoveIf(Function(f) PathUtils.IsSame(f, NewJava.Folder)))
        Configs.JavaList.Edit(
        Sub(ByRef List)
            List.Remove(NewJava)
            List.Insert(0, NewJava)
        End Sub)
        Return NewJava
    End Function

    ''' <summary>下载结束后的统一收尾（按钮状态、提示、列表刷新）。</summary>
    Private Sub FinishDownload(Failure As Exception, Installed As Java)
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
    End Sub

#End Region

End Class
