Imports System.IO

''' <summary>
''' 版本设置 · 存档管理。
'''
''' 列表按「上次游玩 / 名称 / 大小」排序，每行可以直接打开文件夹、备份、还原、复制、删除；
''' 目录内容变化（进游戏存过档、外部拷入存档）会由文件监视器自动刷新。
''' 备份落在 exe 同级的 backup\saves 下。
''' </summary>
Public Class PageInstanceSaves
    Implements IRefreshable

    Friend Enum SortMethod
        LastPlayed = 0
        Name = 1
        Size = 2
    End Enum

    Private Saves As New List(Of ModSaveUtil.SaveEntry)
    Private Showing As New List(Of ModSaveUtil.SaveEntry)
    Private CurrentSort As SortMethod = SortMethod.LastPlayed
    Private SearchText As String = ""

    Private Ready As Boolean = False
    Private Scanning As Boolean = False
    Private InstancePath As String = ""

    Private Watcher As FileSystemWatcher = Nothing
    Private WatcherTimer As Windows.Threading.DispatcherTimer = Nothing

    Public Sub New()
        InitializeComponent()
        BtnSort.Text = "排序：" & GetSortName(CurrentSort)
        WatcherTimer = New Windows.Threading.DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(300)}
        AddHandler WatcherTimer.Tick, Sub(s As Object, e As EventArgs)
                                          WatcherTimer.Stop()
                                          Reload()
                                      End Sub
        Ready = True
    End Sub

#Region "生命周期"

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        SetupWatcher()
        Reload()
    End Sub

    Private Sub Page_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        WatcherTimer.Stop()
        If Watcher IsNot Nothing Then
            Watcher.EnableRaisingEvents = False
            Watcher.Dispose()
            Watcher = Nothing
        End If
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Reload()
    End Sub

    Private Sub SetupWatcher()
        If Watcher IsNot Nothing Then Return
        Try
            Dim Instance As McInstance = PageInstanceLeft.Instance
            If Instance Is Nothing Then Return
            Dim Root As String = ModSaveUtil.GetRoot(Instance)
            Directory.CreateDirectory(Root)

            Watcher = New FileSystemWatcher(Root) With {
                .IncludeSubdirectories = False,
                .NotifyFilter = NotifyFilters.DirectoryName Or NotifyFilters.LastWrite
            }
            AddHandler Watcher.Created, AddressOf OnFileSystemChanged
            AddHandler Watcher.Deleted, AddressOf OnFileSystemChanged
            AddHandler Watcher.Renamed, AddressOf OnFileSystemChanged
            Watcher.EnableRaisingEvents = True
        Catch ex As Exception
            Logger.Warn(ex, "监视存档目录失败")
        End Try
    End Sub

    Private Sub OnFileSystemChanged(sender As Object, e As FileSystemEventArgs)
        '游戏写存档时会连发很多次事件，做个防抖
        RunInUi(Sub()
                    WatcherTimer.Stop()
                    WatcherTimer.Start()
                End Sub)
    End Sub

    Private Sub Reload()
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        If Scanning Then Return

        Scanning = True
        InstancePath = Instance.PathIndie

        RunInNewThread(
        Sub()
            Dim Result As List(Of ModSaveUtil.SaveEntry) = ModSaveUtil.LoadSaves(Instance)
            RunInUi(Sub()
                        Scanning = False
                        If PageInstanceLeft.Instance Is Nothing OrElse PageInstanceLeft.Instance.PathIndie <> InstancePath Then Return
                        Saves = Result
                        RefreshUI()
                    End Sub)
        End Sub, "读取存档列表")
    End Sub

#End Region

#Region "刷新界面"

    Private Sub RefreshUI()
        If Not Ready Then Return

        Dim Key As String = SearchText.ToLowerInvariant()
        Showing = Saves.Where(Function(S) String.IsNullOrEmpty(Key) OrElse
                                          S.Name.ToLowerInvariant().Contains(Key)).ToList()
        Showing.Sort(AddressOf Compare)

        If Saves.Count = 0 Then
            PanContent.Visibility = Visibility.Collapsed
            PanNoWorld.Visibility = Visibility.Visible
            Return
        End If
        PanContent.Visibility = Visibility.Visible
        PanNoWorld.Visibility = Visibility.Collapsed

        PanList.Children.Clear()
        For Each Entry As ModSaveUtil.SaveEntry In Showing
            PanList.Children.Add(BuildItem(Entry))
        Next
        PanListBack.Title = $"存档（{Showing.Count}）"
    End Sub

    Private Function Compare(a As ModSaveUtil.SaveEntry, b As ModSaveUtil.SaveEntry) As Integer
        Select Case CurrentSort
            Case SortMethod.Name
                Return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            Case SortMethod.Size
                Return b.SizeBytes.CompareTo(a.SizeBytes)
            Case Else
                Return b.LastPlayed.CompareTo(a.LastPlayed)
        End Select
    End Function

    Private Function BuildItem(Entry As ModSaveUtil.SaveEntry) As MyListItem
        Dim Item As New MyListItem With {
            .Title = Entry.Name,
            .Info = ModSaveUtil.BuildInfo(Entry),
            .Logo = Entry.Logo,
            .Height = 44,
            .Type = MyListItem.CheckType.None
        }

        Dim BtnOpen As New MyIconButton With {.Logo = IconFolder, .Width = 22, .Height = 22, .ToolTip = "打开文件夹"}
        AddHandler BtnOpen.Click, Sub(s As Object, ev As EventArgs)
                                      Try
                                          OpenExplorer(Entry.Folder)
                                      Catch ex As Exception
                                          Logger.Warn(ex, "打开存档文件夹失败")
                                      End Try
                                  End Sub

        Dim BtnBackup As New MyIconButton With {.Logo = IconBackup, .Width = 22, .Height = 22, .ToolTip = "备份"}
        AddHandler BtnBackup.Click, Sub(s As Object, ev As EventArgs) BackupOne(Entry)

        Dim BtnRestore As New MyIconButton With {
            .Logo = IconRestore, .Width = 22, .Height = 22, .ToolTip = "从备份还原",
            .CanClick = Entry.BackupCount > 0
        }
        AddHandler BtnRestore.Click, Sub(s As Object, ev As EventArgs) RestoreOne(Entry)

        Dim BtnCopy As New MyIconButton With {.Logo = IconCopy, .Width = 22, .Height = 22, .ToolTip = "复制存档目录"}
        AddHandler BtnCopy.Click, Sub(s As Object, ev As EventArgs)
                                      If ModSaveUtil.CopySaveToClipboard(Entry) Then
                                          Hint("已把存档目录复制到剪贴板，可以粘贴到别处", HintType.Green)
                                      Else
                                          Hint("复制失败", HintType.Red)
                                      End If
                                  End Sub

        Dim BtnDelete As New MyIconButton With {.Logo = IconTrash, .Width = 22, .Height = 22, .ToolTip = "删除"}
        AddHandler BtnDelete.Click, Sub(s As Object, ev As EventArgs) DeleteOne(Entry)

        Item.Buttons = New MyIconButton() {BtnOpen, BtnBackup, BtnRestore, BtnCopy, BtnDelete}
        Return Item
    End Function

#End Region

#Region "操作"

    Private Function CheckEntry(Entry As ModSaveUtil.SaveEntry) As Boolean
        If Entry Is Nothing Then Return False
        If Directory.Exists(Entry.Folder) Then Return True
        Hint($"存档「{Entry.Name}」的目录已经不在了，已重新读取", HintType.Red)
        Reload()
        Return False
    End Function

    Private Sub BackupOne(Entry As ModSaveUtil.SaveEntry)
        If Not CheckEntry(Entry) Then Return
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return

        '游戏运行时备份可能得到不一致的存档，先提醒一次
        If IO.File.Exists(Path.Combine(Entry.Folder, "session.lock")) Then
            If MyMsgBox("检测到 session.lock，游戏可能正在运行。" & vbCrLf & vbCrLf &
                        "运行中备份可能得到不一致的存档，建议先退出游戏。",
                        "备份存档", "仍然备份", "取消", IsWarn:=True) <> 1 Then Return
        End If

        RunInNewThread(
        Sub()
            Try
                Dim Target As String = ModSaveUtil.BackupSave(Instance, Entry)
                RunInUi(Sub()
                            Hint($"已备份「{Entry.Name}」", HintType.Green)
                            Reload()
                        End Sub)
            Catch ex As Exception
                Logger.Warn(ex, $"备份存档失败：{Entry.Name}")
                RunInUi(Sub() Hint("备份失败：" & ex.Message, HintType.Red))
            End Try
        End Sub, "备份存档")
    End Sub

    Private Sub RestoreOne(Entry As ModSaveUtil.SaveEntry)
        If Not CheckEntry(Entry) Then Return
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return

        Dim Backups As List(Of FileInfo) = ModSaveUtil.ListBackups(Instance, Entry.Name)
        If Backups.Count = 0 Then
            Hint("这个存档还没有备份，先点备份按钮", HintType.Red)
            Return
        End If

        Dim Selections As IEnumerable(Of IMyRadio) = Backups.Select(
            Function(File As FileInfo) CType(New MyRadioBox With {
                .Text = $"{File.LastWriteTime:yyyy-MM-dd HH:mm}（{FormatSize(File.Length)}）"
            }, IMyRadio))
        Dim Choice As Integer? = MyMsgBoxSelect(Selections, "还原存档（覆盖前会自动备份当前状态）", "还原", "取消", IsWarn:=True)
        If Choice Is Nothing Then Return
        If Choice.Value < 0 OrElse Choice.Value >= Backups.Count Then Return

        Dim ZipPath As String = Backups(Choice.Value).FullName
        RunInNewThread(
        Sub()
            Try
                ModSaveUtil.RestoreSave(Instance, Entry, ZipPath)
                RunInUi(Sub()
                            Hint($"已还原存档「{Entry.Name}」", HintType.Green)
                            Reload()
                        End Sub)
            Catch ex As Exception
                Logger.Warn(ex, $"还原存档失败：{Entry.Name}")
                RunInUi(Sub() Hint("还原失败：" & ex.Message, HintType.Red))
            End Try
        End Sub, "还原存档")
    End Sub

    Private Sub DeleteOne(Entry As ModSaveUtil.SaveEntry)
        If MyMsgBox($"确定删除存档「{Entry.Name}」吗？" & vbCrLf & vbCrLf &
                    "存档会移到回收站；之前的备份仍保留在 backup\saves 里。",
                    "删除存档", "删除", "取消", IsWarn:=True) <> 1 Then Return

        If ModSaveUtil.DeleteSave(Entry) Then
            Hint("已把存档移到回收站", HintType.Green)
        Else
            Hint("删除失败，存档可能正被占用", HintType.Red)
        End If
        Reload()
    End Sub

    Private Sub BtnManageOpen_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            Dim Root As String = ModSaveUtil.GetRoot(Instance)
            Directory.CreateDirectory(Root)
            OpenExplorer(Root)
        Catch ex As Exception
            Logger.Warn(ex, "打开存档文件夹失败")
        End Try
    End Sub

    Private Sub BtnPaste_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return

        Dim Files As Collections.Specialized.StringCollection = Clipboard.GetFileDropList()
        If Files Is Nothing OrElse Files.Count = 0 Then
            Hint("剪贴板里没有文件夹，先在资源管理器里复制一个存档目录", HintType.Red)
            Return
        End If

        RunInNewThread(
        Sub()
            Dim Count As Integer = ModSaveUtil.PasteSaves(Instance, Files)
            RunInUi(Sub()
                        If Count > 0 Then
                            Hint($"已粘贴 {Count} 个存档", HintType.Green)
                        Else
                            Hint("没有可粘贴的存档目录", HintType.Red)
                        End If
                        Reload()
                    End Sub)
        End Sub, "粘贴存档")
    End Sub

    Private Sub BtnSort_Click(sender As Object, e As RouteEventArgs)
        Dim Menu As New ContextMenu()
        For Each Method As SortMethod In [Enum].GetValues(GetType(SortMethod))
            Dim Local As SortMethod = Method
            Dim Item As New MyMenuItem With {.Header = GetSortName(Local)}
            AddHandler Item.Click, Sub(s As Object, ev As RoutedEventArgs)
                                       CurrentSort = Local
                                       BtnSort.Text = "排序：" & GetSortName(Local)
                                       RefreshUI()
                                   End Sub
            Menu.Items.Add(Item)
        Next
        Menu.PlacementTarget = BtnSort
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        Menu.IsOpen = True
    End Sub

    Private Sub SearchBox_TextChanged(sender As Object, e As EventArgs)
        SearchText = SearchBox.Text.Trim()
        RefreshUI()
    End Sub

    Private Shared Function GetSortName(Method As SortMethod) As String
        Select Case Method
            Case SortMethod.Name
                Return "名称"
            Case SortMethod.Size
                Return "大小"
            Case Else
                Return "上次游玩"
        End Select
    End Function

#End Region

#Region "图标"

    Private Const IconFolder As String =
        "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"
    Private Const IconTrash As String =
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"
    Private Const IconCopy As String =
        "M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"
    Private Const IconBackup As String =
        "M17 3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z"
    Private Const IconRestore As String =
        "M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z"

#End Region

End Class
