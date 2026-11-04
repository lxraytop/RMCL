Imports System.IO

''' <summary>
''' 版本设置 · 资源包 / 光影包管理。
'''
''' 同一个页面按传输进来的资源类型工作，两种资源共用一套扫描、筛选、排序与批量操作。
''' 关键行为：
''' · 筛选按钮只在有对应条目时出现，不会出现「点进 0 项的空筛选就回不去」；
''' · 筛选后列表为空时只有列表区收起，筛选栏保留；
''' · 排序走下拉菜单（文件名 / 资源名 / 添加时间 / 文件大小），文件夹始终置顶；
''' · 删除走回收站。
''' </summary>
Public Class PageInstanceCompResource
    Implements IRefreshable

    Friend Enum FilterType
        All = 0
        Enabled = 1
        Disabled = 2
    End Enum

    Friend Enum SortMethod
        FileName = 0
        CompName = 1
        AddTime = 2
        FileSize = 3
    End Enum

    Private ReadOnly CompType As ModLocalComp.CompType
    Private ReadOnly TypeName As String

    Private Files As New List(Of ModLocalComp.LocalCompFile)
    Private SelectedKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private Filter As FilterType = FilterType.All
    Private CurrentSort As SortMethod = SortMethod.CompName
    Private SearchText As String = ""

    ''' <summary>XAML 解析完成前不刷界面：那时控件字段还是 Nothing。</summary>
    Private Ready As Boolean = False
    Private Scanning As Boolean = False
    ''' <summary>是否已经有过一次「读到结果并刷新了界面」的读取，用来区分 XAML 默认态与真实数据态。</summary>
    Private Scanned As Boolean = False
    Private InstancePath As String = ""

    Friend Sub New(Type As ModLocalComp.CompType)
        InitializeComponent()
        CompType = Type
        TypeName = ModLocalComp.GetTypeName(Type)

        '初始化筛选与排序的显示，此时 Ready 仍为 False，事件只会记录状态不会刷界面
        BtnFilterAll.Checked = True
        BtnSort.Text = "排序：" & GetSortName(CurrentSort)
        Ready = True
    End Sub

#Region "生命周期"

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '页面跨版本复用，每次显示都重新读一次
        Reload()
    End Sub

    ''' <summary>
    ''' 进场时兜底检查界面骨架。上次读取若被意外打断，可能留下「列表区、空状态、加载卡片全折叠」
    ''' 的夹生状态，看着就是一片空白，这里补一次收尾。
    '''
    ''' 必须在 Scanned 之后才动手：首次进场时 Loaded（进而 Reload）很可能还没执行，
    ''' 此时各卡片仍是 XAML 默认的显隐值，据此「收尾」会把正在用的加载卡片提前藏掉，
    ''' 反而让页面空掉。
    ''' </summary>
    Private Sub Page_Enter() Handles Me.PageEnter
        If Not Ready OrElse Scanning OrElse Not Scanned Then Return
        ShowCard(PanLoad, False)
        ShowCard(CardSelect, False)
        If Files.Count = 0 Then
            '空目录就该看到空状态卡片，而不是一片空白
            PanAllBack.Visibility = Visibility.Collapsed
            ShowCard(PanEmpty, True)
        Else
            ShowCard(PanEmpty, False)
            PanAllBack.Visibility = Visibility.Visible
        End If
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Reload()
    End Sub

    Private Sub Reload()
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        If Scanning Then Return

        Scanning = True
        InstancePath = Instance.PathIndie
        ShowCard(PanLoad, True)
        PanAllBack.Visibility = Visibility.Collapsed
        ShowCard(PanEmpty, False)

        RunInNewThread(
        Sub()
            Dim Result As List(Of ModLocalComp.LocalCompFile) = Nothing
            Try
                Result = ModLocalComp.LoadFiles(Instance, CompType)
                '图标要在后台取，读 zip 不能放 UI 线程
                For Each Entry As ModLocalComp.LocalCompFile In Result
                    ModLocalComp.GetIcon(Entry, CompType)
                Next
            Catch ex As Exception
                '读目录失败也必须把 Scanning 复位：否则之后每次 Reload 都会在入口被拦掉，
                '页面永远停在「只剩一张加载卡片」的状态，连刷新都救不回来
                Logger.Error(ex, $"读取{TypeName}失败")
            End Try
            RunInUi(Sub()
                        Scanning = False
                        If Result Is Nothing Then
                            '读取失败：显示可读的空状态，而不是把界面留在折叠 + 加载中的夹生态
                            ShowCard(PanLoad, False)
                            Files.Clear()
                            SelectedKeys.Clear()
                            Scanned = True
                            RefreshUI()
                            TxtEmptyTitle.Text = $"读取{TypeName}失败"
                            TxtEmptyDescription.Text = "请点右上角的刷新按钮重试。"
                            PanAllBack.Visibility = Visibility.Collapsed
                            ShowCard(PanEmpty, True)
                            Return
                        End If
                        '换版本 / 已切走：本次读取作废，但界面要收尾到「非加载中」，
                        '否则 PanAllBack 会一直折叠，下次进来就是一片空白
                        If PageInstanceLeft.Instance Is Nothing OrElse PageInstanceLeft.Instance.PathIndie <> InstancePath Then
                            ShowCard(PanLoad, False)
                            PanAllBack.Visibility = Visibility.Visible
                            Return
                        End If
                        ShowCard(PanLoad, False)
                        PanAllBack.Visibility = Visibility.Visible

                        Files = Result
                        SelectedKeys.Clear()
                        Scanned = True
                        '先换数据再复位筛选：复位本身会触发一次刷新，此时用的已是新列表
                        Filter = FilterType.All
                        BtnFilterAll.Checked = True
                        SearchText = ""
                        SearchBox.Text = ""
                        RefreshUI()
                    End Sub)
        End Sub, $"读取{TypeName}")
    End Sub

#End Region

#Region "刷新界面"

    Private Function CanPassFilter(Entry As ModLocalComp.LocalCompFile) As Boolean
        Select Case Filter
            Case FilterType.Enabled
                Return Entry.State = ModLocalComp.FileState.Fine
            Case FilterType.Disabled
                Return Entry.State = ModLocalComp.FileState.Disabled
            Case Else
                Return True
        End Select
    End Function

    Private Function MatchSearch(Entry As ModLocalComp.LocalCompFile) As Boolean
        If String.IsNullOrEmpty(SearchText) Then Return True
        Dim Key As String = SearchText.ToLowerInvariant()
        Return Entry.Title.ToLowerInvariant().Contains(Key) OrElse
               Entry.Name.ToLowerInvariant().Contains(Key) OrElse
               Entry.Description.ToLowerInvariant().Contains(Key)
    End Function

    Private Function Compare(a As ModLocalComp.LocalCompFile, b As ModLocalComp.LocalCompFile) As Integer
        '文件夹始终置顶
        If a.IsFolder AndAlso Not b.IsFolder Then Return -1
        If Not a.IsFolder AndAlso b.IsFolder Then Return 1

        Select Case CurrentSort
            Case SortMethod.FileName
                Return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            Case SortMethod.AddTime
                Return b.AddedTime.CompareTo(a.AddedTime)
            Case SortMethod.FileSize
                Return b.SizeBytes.CompareTo(a.SizeBytes)
            Case Else
                Return String.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)
        End Select
    End Function

    Private Sub RefreshUI()
        If Not Ready Then Return

        Dim Showing As List(Of ModLocalComp.LocalCompFile) =
            Files.Where(Function(F) MatchSearch(F) AndAlso CanPassFilter(F)).ToList()
        Showing.Sort(AddressOf Compare)

        '选中项只保留还在显示列表里的
        Dim ShowKeys As New HashSet(Of String)(Showing.Select(Function(F) F.RawPath), StringComparer.OrdinalIgnoreCase)
        SelectedKeys.IntersectWith(ShowKeys)

        PanList.Children.Clear()
        For Each Entry As ModLocalComp.LocalCompFile In Showing
            PanList.Children.Add(BuildItem(Entry))
        Next

        RefreshBars(Showing.Count)
    End Sub

    ''' <summary>
    ''' 显示 / 收起卡片。
    '''
    ''' 页面切换的退场动画会把「当时可见」的卡片 Opacity 渐隐到 0，而进场动画只重置
    ''' 没有折叠的控件。所以卡片以后再显示时，必须自己把 Opacity 复位成 1，
    ''' 否则 Visibility 变成 Visible 也还是全透明的。
    ''' </summary>
    Private Sub ShowCard(Card As MyCard, Show As Boolean)
        If Card Is Nothing Then Return
        If Show Then
            Card.Opacity = 1
            Card.Visibility = Visibility.Visible
        Else
            Card.Visibility = Visibility.Collapsed
        End If
    End Sub

    Private Sub RefreshBars(ShowingCount As Integer)
        '整个目录空 → 居中空状态；只是筛选没命中 → 收起列表区、保留筛选栏
        If Files.Count = 0 Then
            PanAllBack.Visibility = Visibility.Collapsed
            ShowCard(PanEmpty, True)
            TxtEmptyTitle.Text = $"这里还没有{TypeName}"
            TxtEmptyDescription.Text = $"点「从文件安装」选一个{TypeName}，或者去下载页找找。"
            ShowCard(CardSelect, False)
            Return
        End If
        PanAllBack.Visibility = Visibility.Visible
        ShowCard(PanEmpty, False)
        PanList.Visibility = If(ShowingCount > 0, Visibility.Visible, Visibility.Collapsed)

        '计数
        Dim AnyCount As Integer = 0
        Dim EnabledCount As Integer = 0
        Dim DisabledCount As Integer = 0
        For Each Entry As ModLocalComp.LocalCompFile In Files
            AnyCount += 1
            If Entry.State = ModLocalComp.FileState.Fine Then EnabledCount += 1
            If Entry.State = ModLocalComp.FileState.Disabled Then DisabledCount += 1
        Next

        '筛选按钮只在有对应条目（或正停在该筛选上）时出现
        BtnFilterAll.Text = If(String.IsNullOrEmpty(SearchText), $"全部 ({AnyCount})", "搜索结果")
        BtnFilterEnabled.Text = $"已启用 ({EnabledCount})"
        BtnFilterDisabled.Text = $"已禁用 ({DisabledCount})"
        BtnFilterEnabled.Visibility = If(Filter = FilterType.Enabled OrElse
                                        (EnabledCount > 0 AndAlso EnabledCount < AnyCount),
                                        Visibility.Visible, Visibility.Collapsed)
        BtnFilterDisabled.Visibility = If(Filter = FilterType.Disabled OrElse DisabledCount > 0,
                                          Visibility.Visible, Visibility.Collapsed)

        '选中条
        If SelectedKeys.Count = 0 Then
            ShowCard(CardSelect, False)
            PanListBack.Margin = New Thickness(0, 0, 0, 14)
        Else
            LabSelect.Text = $"已选择 {SelectedKeys.Count} 项"
            ShowCard(CardSelect, True)
            '给浮出的操作栏让出位置，避免盖住最后一行
            PanListBack.Margin = New Thickness(0, 0, 0, 95)
        End If
    End Sub

    Private Function BuildItem(Entry As ModLocalComp.LocalCompFile) As MyListItem
        Dim Item As New MyListItem With {
            .Title = Entry.Title,
            .Info = BuildInfoText(Entry),
            .Logo = ModLocalComp.GetIcon(Entry, CompType),
            .Height = 44,
            .Type = MyListItem.CheckType.CheckBox,
            .Tag = Entry,
            .Checked = SelectedKeys.Contains(Entry.RawPath)
        }

        Dim BtnOpen As New MyIconButton With {
            .Logo = IconFolder,
            .Width = 22,
            .Height = 22,
            .Margin = New Thickness(3, 0, 3, 0),
            .ToolTip = "打开所在文件夹"
        }
        AddHandler BtnOpen.Click, Sub(s As Object, ev As EventArgs)
                                      OpenLocation(Entry)
                                  End Sub

        Dim BtnToggle As New MyIconButton With {
            .Logo = If(Entry.State = ModLocalComp.FileState.Fine, IconDisable, IconEnable),
            .Width = 22,
            .Height = 22,
            .Margin = New Thickness(3, 0, 3, 0),
            .ToolTip = If(Entry.State = ModLocalComp.FileState.Fine, "禁用", "启用")
        }
        AddHandler BtnToggle.Click, Sub(s As Object, ev As EventArgs)
                                        SetEnabled(Entry, Entry.State <> ModLocalComp.FileState.Fine)
                                    End Sub

        Dim BtnDelete As New MyIconButton With {
            .Logo = IconTrash,
            .Width = 22,
            .Height = 22,
            .Margin = New Thickness(3, 0, 3, 0),
            .ToolTip = "删除"
        }
        AddHandler BtnDelete.Click, Sub(s As Object, ev As EventArgs)
                                        DeleteEntries({Entry})
                                    End Sub

        Item.Buttons = New MyIconButton() {BtnOpen, BtnToggle, BtnDelete}
        AddHandler Item.Changed, Sub(s As Object, ev As RouteEventArgs) OnItemChanged(Item, Entry)
        Return Item
    End Function

    Private Function BuildInfoText(Entry As ModLocalComp.LocalCompFile) As String
        Dim Parts As New List(Of String) From {
            If(Entry.State = ModLocalComp.FileState.Fine, "已启用", "已禁用"),
            FormatSize(Entry.SizeBytes)
        }
        If Entry.IsFolder Then Parts.Add("文件夹")
        If Not String.IsNullOrEmpty(Entry.Description) Then Parts.Add(Entry.Description)
        Return String.Join(" · ", Parts)
    End Function

    Private Sub OnItemChanged(Item As MyListItem, Entry As ModLocalComp.LocalCompFile)
        If Item.Checked Then
            SelectedKeys.Add(Entry.RawPath)
        Else
            SelectedKeys.Remove(Entry.RawPath)
        End If
        RefreshBars(PanList.Children.Count)
    End Sub

#End Region

#Region "筛选与排序"

    Private Sub ChangeFilter(sender As Object, raiseByMouse As Boolean)
        Filter = CInt(sender.Tag)
        RefreshUI()
    End Sub

    Private Sub SearchBox_TextChanged(sender As Object, e As EventArgs)
        SearchText = SearchBox.Text.Trim()
        RefreshUI()
    End Sub

    Private Shared Function GetSortName(Method As SortMethod) As String
        Select Case Method
            Case SortMethod.FileName
                Return "文件名"
            Case SortMethod.AddTime
                Return "添加时间"
            Case SortMethod.FileSize
                Return "文件大小"
            Case Else
                Return "资源名"
        End Select
    End Function

    Private Sub SetSortMethod(Method As SortMethod)
        CurrentSort = Method
        BtnSort.Text = "排序：" & GetSortName(Method)
        RefreshUI()
    End Sub

    Private Sub BtnSort_Click(sender As Object, e As RouteEventArgs)
        Dim Menu As New ContextMenu()
        For Each Method As SortMethod In [Enum].GetValues(GetType(SortMethod))
            Dim Local As SortMethod = Method
            Dim Item As New MyMenuItem With {.Header = GetSortName(Local)}
            AddHandler Item.Click, Sub(s As Object, ev As RoutedEventArgs) SetSortMethod(Local)
            Menu.Items.Add(Item)
        Next
        Menu.PlacementTarget = BtnSort
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        Menu.IsOpen = True
    End Sub

#End Region

#Region "选择与批量操作"

    Private Sub BtnManageSelectAll_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Items As List(Of MyListItem) = PanList.Children.OfType(Of MyListItem)().ToList()
        If Items.Count = 0 Then Return

        Dim AllSelected As Boolean = Items.All(Function(I) I.Checked)
        For Each Item As MyListItem In Items
            Item.Checked = Not AllSelected
        Next
        '逐个设置会触发 Changed，这里再统一刷一次保证计数准确
        SelectedKeys.Clear()
        For Each Item As MyListItem In Items
            If Item.Checked Then SelectedKeys.Add(DirectCast(Item.Tag, ModLocalComp.LocalCompFile).RawPath)
        Next
        RefreshBars(Items.Count)
    End Sub

    Private Sub BtnSelectCancel_Click(sender As Object, e As RouteEventArgs)
        For Each Item As MyListItem In PanList.Children.OfType(Of MyListItem)()
            Item.Checked = False
        Next
        SelectedKeys.Clear()
        RefreshUI()
    End Sub

    Private Sub BtnSelectEnable_Click(sender As Object, e As RouteEventArgs)
        BatchSetEnabled(True)
    End Sub

    Private Sub BtnSelectDisable_Click(sender As Object, e As RouteEventArgs)
        BatchSetEnabled(False)
    End Sub

    Private Sub BatchSetEnabled(Value As Boolean)
        Dim Targets As List(Of ModLocalComp.LocalCompFile) =
            Files.Where(Function(F) SelectedKeys.Contains(F.RawPath) AndAlso
                                    F.State <> If(Value, ModLocalComp.FileState.Fine, ModLocalComp.FileState.Disabled)).ToList()
        If Targets.Count = 0 Then Return

        Dim Affected As Integer = 0
        For Each Entry As ModLocalComp.LocalCompFile In Targets
            If ModLocalComp.SetFileEnabled(Entry, Value) Then Affected += 1
        Next
        Hint($"已{(If(Value, "启用", "禁用"))} {Affected} 项{TypeName}", HintType.Green)
        Reload()
    End Sub

    Private Sub BtnSelectDelete_Click(sender As Object, e As RouteEventArgs)
        DeleteEntries(Files.Where(Function(F) SelectedKeys.Contains(F.RawPath)).ToList())
    End Sub

    Private Sub DeleteEntries(Targets As IEnumerable(Of ModLocalComp.LocalCompFile))
        Dim List As List(Of ModLocalComp.LocalCompFile) = Targets.ToList()
        If List.Count = 0 Then Return
        If MyMsgBox($"确定删除选中的 {List.Count} 项{TypeName}吗？" & vbCrLf & vbCrLf &
                    "文件会移到回收站，可以从那里还原。",
                    $"删除{TypeName}", "删除", "取消", IsWarn:=True) <> 1 Then Return

        Dim Affected As Integer = 0
        For Each Entry As ModLocalComp.LocalCompFile In List
            If ModLocalComp.DeleteEntry(Entry) Then Affected += 1
        Next
        Hint($"已删除 {Affected} 项{TypeName}", If(Affected > 0, HintType.Green, HintType.Red))
        Reload()
    End Sub

#End Region

#Region "单条操作"

    Private Sub SetEnabled(Entry As ModLocalComp.LocalCompFile, Value As Boolean)
        If Not ModLocalComp.SetFileEnabled(Entry, Value) Then
            Hint("切换启用状态失败，可能有程序正在占用该文件", HintType.Red)
        End If
        Reload()
    End Sub

    Private Sub OpenLocation(Entry As ModLocalComp.LocalCompFile)
        Try
            If Entry.IsFolder Then
                OpenExplorerFolder(Entry.Path)
            Else
                '文件用 /select，正好在资源目录里高亮它
                OpenExplorer(Entry.Path)
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"打开{TypeName}所在文件夹失败")
            Hint("打开文件夹失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnManageOpen_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            Dim Folder As String = ModLocalComp.GetRoot(Instance, CompType)
            Directory.CreateDirectory(Folder)
            OpenExplorerFolder(Folder)
        Catch ex As Exception
            Logger.Warn(ex, $"打开{TypeName}文件夹失败")
        End Try
    End Sub

    Private Sub BtnManageInstall_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return

        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {
            .Title = $"选择要安装的{TypeName}",
            .Filter = $"{TypeName} (*.zip)|*.zip",
            .Multiselect = True
        }
        If Dialog.ShowDialog() <> True Then Return

        Dim Folder As String = ModLocalComp.GetRoot(Instance, CompType)
        Directory.CreateDirectory(Folder)

        Dim Count As Integer = 0
        Dim Skipped As Integer = 0
        For Each FilePath As String In Dialog.FileNames
            Dim Target As String = Path.Combine(Folder, Path.GetFileName(FilePath))
            Try
                '选中的就是目录里已有的那个文件，复制到自身会抛「正由另一进程使用」
                If String.Equals(Path.GetFullPath(FilePath), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase) Then
                    Skipped += 1
                    Continue For
                End If
                IO.File.Copy(FilePath, Target, True)
                Count += 1
            Catch ex As Exception
                Logger.Warn(ex, $"安装{TypeName}失败：{FilePath}")
            End Try
        Next

        Dim Message As String = $"已安装 {Count} 个{TypeName}"
        If Skipped > 0 Then Message &= $"（{Skipped} 个已在目录中，跳过）"
        Hint(Message, If(Count > 0 OrElse Skipped > 0, HintType.Green, HintType.Red))
        Reload()
    End Sub

    Private Sub BtnManageDownload_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Page As FormMain.PageSubType =
            If(CompType = ModLocalComp.CompType.ResourcePack,
               FormMain.PageSubType.DownloadResourcePack,
               FormMain.PageSubType.DownloadShader)
        FrmMain.PageChange(FormMain.PageType.Download, Page)
    End Sub

    Private Sub BtnManageExport_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return

        Dim Lines As New List(Of String)
        Lines.Add($"{TypeName}清单 - {Instance.Name}")
        Lines.Add($"导出时间：{Now:yyyy-MM-dd HH:mm}")
        Lines.Add($"共 {Files.Count} 项")
        Lines.Add("")
        For Each Entry As ModLocalComp.LocalCompFile In Files
            Lines.Add($"{Entry.Title}`t{If(Entry.State = ModLocalComp.FileState.Fine, "已启用", "已禁用")}`t" &
                      $"{FormatSize(Entry.SizeBytes)}`t{Entry.Description}")
        Next

        Try
            Clipboard.SetText(String.Join(vbCrLf, Lines))
            Hint($"已复制 {Files.Count} 项{TypeName}信息到剪贴板", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, "复制资源清单失败")
            Hint("复制失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

#End Region

#Region "图标"

    Private Const IconFolder As String =
        "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"
    Private Const IconTrash As String =
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"
    Private Const IconEnable As String =
        "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z"
    Private Const IconDisable As String =
        "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm5 11H7v-2h10v2z"

#End Region

End Class
