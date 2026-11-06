Public Class PageSetupUI

    Private Sub PageSetupUI_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '重复加载部分
        PanBack.ScrollToHome()
        If ThemeDontClick <> 0 Then
            Dim NewText As String = Nothing
            Select Case ThemeDontClick
                Case 1
                    NewText = "眼瞎白"
                Case 2
                    NewText = "真·滑稽彩"
            End Select
            For Each Control In PanLauncherTheme.Children
                If (TypeOf Control Is MyRadioBox) AndAlso CType(Control, MyRadioBox).IsEnabled Then CType(Control, MyRadioBox).Text = NewText
            Next
        End If

        AniControlEnabled += 1
        Refresh() '#4826
        AniControlEnabled -= 1
        '同步自定义主题滑块的显示状态
        RefreshThemeSliderVisibility()

        '非重复加载部分
        Static Reloaded As Boolean = False
        If Reloaded Then Return
        Reloaded = True

        SliderLoad()



    End Sub
    Public Sub Refresh()
        Try
            SettingService.RefreshSettings(Me)
            BackgroundRefresh(False, False)

            '标题栏
            CheckLogoLeft.Visibility = If(RadioLogoType0.Checked, Visibility.Visible, Visibility.Collapsed)
            PanLogoText.Visibility = If(RadioLogoType2.Checked, Visibility.Visible, Visibility.Collapsed)
            PanLogoChange.Visibility = If(RadioLogoType3.Checked, Visibility.Visible, Visibility.Collapsed)

            '背景音乐
            MusicRefreshUI()

            '主页
            OnMainPageTypeChanged()
        Catch ex As NullReferenceException
            Logger.Error(ex, "个性化设置项存在异常，已被自动重置", LogBehavior.Alert)
            Reset()
        Catch ex As Exception
            Logger.Error(ex, "重载个性化设置时出错")
        End Try
    End Sub
    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Logger.Info("已初始化个性化设置！")
            Hint("已初始化个性化设置", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化个性化设置失败", LogBehavior.Alert)
        End Try
        Refresh()
    End Sub

    '背景图片
    Private Sub BtnUIBgOpen_Click(sender As Object, e As EventArgs) Handles BtnBackgroundOpen.Click
        OpenExplorer(Paths.Base & "RMCL\Pictures\")
    End Sub
    Private Sub BtnBackgroundRefresh_Click(sender As Object, e As EventArgs) Handles BtnBackgroundRefresh.Click
        BackgroundRefresh(True, True)
    End Sub
    Public Sub BackgroundRefreshUI(Show As Boolean, Count As Integer)
        If IsNothing(PanBackgroundOpacity) Then Return
        If Show Then
            PanBackgroundOpacity.Visibility = Visibility.Visible
            PanBackgroundBlur.Visibility = Visibility.Visible
            PanBackgroundSuit.Visibility = Visibility.Visible
            BtnBackgroundClear.Visibility = Visibility.Visible
            CardBackground.Title = "背景图片（" & Count & " 张）"
        Else
            PanBackgroundOpacity.Visibility = Visibility.Collapsed
            PanBackgroundBlur.Visibility = Visibility.Collapsed
            PanBackgroundSuit.Visibility = Visibility.Collapsed
            BtnBackgroundClear.Visibility = Visibility.Collapsed
            CardBackground.Title = "背景图片"
        End If
        CardBackground.TriggerForceResize()
    End Sub
    Private Sub BtnBackgroundClear_Click(sender As Object, e As EventArgs) Handles BtnBackgroundClear.Click
        If MyMsgBox("即将删除背景图片文件夹中的所有文件。" & vbCrLf & "此操作不可撤销，是否确定？", "警告",, "取消", IsWarn:=True) = 1 Then
            DirectoryUtils.Delete(Paths.Base & "RMCL\Pictures")
            BackgroundRefresh(False, True)
            Hint("背景图片已清空！", HintType.Green)
        End If
    End Sub
    ''' <summary>
    ''' 刷新背景图片及设置页 UI。
    ''' </summary>
    ''' <param name="IsHint">是否显示刷新提示。</param>
    ''' <param name="Refresh">是否刷新图片显示。</param>
    Public Shared Sub BackgroundRefresh(IsHint As Boolean, Refresh As Boolean)
        Try

            '获取可用的图片文件
            DirectoryUtils.Create(Paths.Base & "RMCL\Pictures\")
            Dim Pic As New List(Of String)
            For Each File In DirectoryUtils.EnumerateFiles(Paths.Base & "RMCL\Pictures\", True)
                Dim Extension As String = PathUtils.GetExtension(File)
                If Extension <> "ini" AndAlso Extension <> "db" Then Pic.Add(File) '文件夹可能会被加入 .ini 和 thumbs.db
            Next
            '加载
            If Not Pic.Any() Then
                If Refresh Then
                    If FrmMain.ImgBack.Visibility = Visibility.Collapsed Then
                        If IsHint Then Hint("未检测到可用背景图片！", HintType.Red)
                    Else
                        FrmMain.ImgBack.Visibility = Visibility.Collapsed
                        If IsHint Then Hint("背景图片已清除！", HintType.Green)
                    End If
                End If
                If Not IsNothing(FrmSetupUI) Then FrmSetupUI.BackgroundRefreshUI(False, 0)
            Else
                If Refresh Then
                    Dim Address As String = RandomOne(Pic)
                    Try
                        Logger.Info($"加载背景图片：{Address}")
                        FrmMain.ImgBack.Background = New MyBitmap(Address)
                        FrmMain.ImgBack.Visibility = Visibility.Visible
                        FrmMain.UpdateBackgroundAndTitleBar()
                        If IsHint Then Hint("背景图片已刷新：" & PathUtils.GetLastPart(Address), HintType.Green, False)
                    Catch ex As Exception
                        If ex.Message.Contains("参数无效") Then
                            Logger.Error($"刷新背景图片失败，该图片文件可能并非标准格式。{vbCrLf}你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。{vbCrLf}文件：{Address}", LogBehavior.Alert)
                        Else
                            Logger.Error(ex, $"刷新背景图片失败（{Address}）", LogBehavior.Alert)
                        End If
                    End Try
                End If
                If Not IsNothing(FrmSetupUI) Then FrmSetupUI.BackgroundRefreshUI(True, Pic.Count)
            End If

        Catch ex As Exception
            Logger.Error(ex, "刷新背景图片时出现未知错误")
        End Try
    End Sub

    '顶部栏
    Private Sub BtnLogoChange_Click(sender As Object, e As EventArgs) Handles BtnLogoChange.Click
        Dim FileName As String = Dialogs.SelectFile("选择图片", False, filter:={({"png", "jpg", "jpeg", "gif", "webp"}, "常用图片文件")}).FirstOrDefault()
        If String.IsNullOrEmpty(FileName) Then Return
        Dim TargetPath As String = Paths.Base & "RMCL\Logo.png"
        Try
            '复制文件
            FileUtils.Copy(FileName, TargetPath)
            '设置当前显示
            FrmMain.ImageTitleLogo.Source = Nothing '防止因为 Source 属性前后的值相同而不更新 (#5628)
            FrmMain.ImageTitleLogo.Source = TargetPath
        Catch ex As Exception
            If ex.Message.Contains("参数无效") Then
                Logger.Error($"改变标题栏图片失败，该图片文件可能并非标准格式。{vbCrLf}你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。{vbCrLf}文件：{TargetPath}", LogBehavior.Alert)
            Else
                Logger.Error(ex, "设置标题栏图片失败", LogBehavior.Alert)
            End If
            FrmMain.ImageTitleLogo.Source = Nothing
        End Try
    End Sub
    Private Sub RadioLogoType3_Check(sender As Object, e As RouteEventArgs) Handles RadioLogoType3.PreviewCheck
        If Not (AniControlEnabled = 0 AndAlso e.RaiseByMouse) Then Return
Refresh:
        '已有图片则不再选择
        Dim TargetPath As String = Paths.Base & "RMCL\Logo.png"
        If FileUtils.Exists(TargetPath) Then
            Try
                FrmMain.ImageTitleLogo.Source = Nothing '防止因为 Source 属性前后的值相同而不更新 (#5628)
                FrmMain.ImageTitleLogo.Source = TargetPath
            Catch ex As Exception
                If ex.Message.Contains("参数无效") Then
                    Logger.Error($"改变标题栏图片失败，该图片文件可能并非标准格式。{vbCrLf}你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。{vbCrLf}文件：{TargetPath}", LogBehavior.Alert)
                Else
                    Logger.Error(ex, "调整标题栏图片失败", LogBehavior.Alert)
                End If
                FrmMain.ImageTitleLogo.Source = Nothing
                e.Handled = True
                Try
                    FileUtils.Delete(TargetPath)
                Catch exx As Exception
                    Logger.Error(exx, "清理错误的标题栏图片失败", LogBehavior.Alert)
                End Try
            End Try
            Return
        End If
        '没有图片则要求选择
        Dim FileName As String = Dialogs.SelectFile("选择图片", False, filter:={({"png", "jpeg", "jpg", "gif", "webp"}, "常用图片文件")}).FirstOrDefault()
        If String.IsNullOrEmpty(FileName) Then
            FrmMain.ImageTitleLogo.Source = Nothing
            e.Handled = True
        Else
            Try
                FileUtils.Copy(FileName, TargetPath)
                GoTo Refresh
            Catch ex As Exception
                Logger.Error(ex, "复制标题栏图片失败", LogBehavior.Alert)
            End Try
        End If
    End Sub
    Private Sub BtnLogoDelete_Click(sender As Object, e As EventArgs) Handles BtnLogoDelete.Click
        Try
            FileUtils.Delete(Paths.Base & "RMCL\Logo.png")
            RadioLogoType1.SetChecked(True, True)
            Hint("标题栏图片已清空！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "清空标题栏图片失败", LogBehavior.Alert)
        End Try
    End Sub

    '背景音乐
    Private Sub BtnMusicOpen_Click(sender As Object, e As EventArgs) Handles BtnMusicOpen.Click
        OpenExplorer(Paths.Base & "RMCL\Musics\")
    End Sub
    Private Sub BtnMusicRefresh_Click(sender As Object, e As EventArgs) Handles BtnMusicRefresh.Click
        MusicRefreshPlay(True)
    End Sub
    Public Sub MusicRefreshUI()
        If PanBackgroundOpacity Is Nothing Then Return
        If MusicAllList.Any Then
            PanMusicVolume.Visibility = Visibility.Visible
            PanMusicDetail.Visibility = Visibility.Visible
            BtnMusicClear.Visibility = Visibility.Visible
            CardMusic.Title = "背景音乐（" &
                DirectoryUtils.EnumerateFiles(Paths.Base & "RMCL\Musics\", True).Count(Function(f) Not {"ini", "jpg", "txt", "cfg", "lrc", "db", "png"}.Contains(PathUtils.GetExtension(f))) &
                " 首）"
        Else
            PanMusicVolume.Visibility = Visibility.Collapsed
            PanMusicDetail.Visibility = Visibility.Collapsed
            BtnMusicClear.Visibility = Visibility.Collapsed
            CardMusic.Title = "背景音乐"
        End If
        CardMusic.TriggerForceResize()
    End Sub
    Private Sub BtnMusicClear_Click(sender As Object, e As EventArgs) Handles BtnMusicClear.Click
        If MyMsgBox("即将删除背景音乐文件夹中的所有文件。" & vbCrLf & "此操作不可撤销，是否确定？", "警告",, "取消", IsWarn:=True) = 1 Then
            RunInThread(
            Sub()
                Hint("正在删除背景音乐……")
                '停止播放音乐
                MusicNAudio = Nothing
                MusicWaitingList = New List(Of String)
                MusicAllList = New List(Of String)
                Thread.Sleep(200)
                '删除文件
                Try
                    DirectoryUtils.Delete(Paths.Base & "RMCL\Musics")
                    Hint("背景音乐已删除！", HintType.Green)
                Catch ex As Exception
                    Logger.Error(ex, "删除背景音乐失败", LogBehavior.Alert)
                End Try
                Try
                    DirectoryUtils.Create(Paths.Base & "RMCL\Musics\")
                    RunInUi(Sub() MusicRefreshPlay(False))
                Catch ex As Exception
                    Logger.Error(ex, "重建背景音乐文件夹失败", LogBehavior.Alert)
                End Try
            End Sub)
        End If
    End Sub
    Private Sub CheckMusicStart_Change() Handles CheckMusicStart.Change
        If AniControlEnabled <> 0 Then Return
        If CheckMusicStart.Checked Then CheckMusicStop.Checked = False
    End Sub
    Private Sub CheckMusicStop_Change() Handles CheckMusicStop.Change
        If AniControlEnabled <> 0 Then Return
        If CheckMusicStop.Checked Then CheckMusicStart.Checked = False
    End Sub

    '主页
    Private Sub BtnCustomFile_Click(sender As Object, e As EventArgs) Handles BtnCustomFile.Click
        Try
            If FileUtils.Exists(Paths.Base & "RMCL\Custom.xaml") Then
                If MyMsgBox("当前已存在布局文件，继续生成教学文件将会覆盖现有布局文件！", "覆盖确认", "继续", "取消", IsWarn:=True) = 2 Then Return
            End If
            ExtractResources(Paths.Base & "RMCL\Custom.xaml", "Custom")
            Hint("教学文件已生成！", HintType.Green)
            OpenExplorer(Paths.Base & "RMCL\Custom.xaml")
        Catch ex As Exception
            Logger.Error(ex, "生成教学文件失败")
        End Try
    End Sub
    Private Sub BtnCustomRefresh_Click() Handles BtnCustomRefresh.Click
        FrmLaunchRight.ForceRefresh()
        Hint("已刷新主页！", HintType.Green)
    End Sub
    Private Sub BtnCustomTutorial_Click(sender As Object, e As EventArgs) Handles BtnCustomTutorial.Click
        MyMsgBox("1. 点击 生成教学文件 按钮，这会在 RMCL 文件夹下生成 Custom.xaml 布局文件。" & vbCrLf &
                 "2. 使用记事本等工具打开这个文件并进行修改，修改完记得保存。" & vbCrLf &
                 "3. 点击 刷新主页 按钮，查看主页现在长啥样了。" & vbCrLf &
                 vbCrLf &
                 "你可以在生成教学文件后直接刷新主页，对照着进行修改，更有助于理解。" & vbCrLf &
                 "直接将主页文件拖进 RMCL 窗口也可以快捷加载。", "主页自定义教程")
    End Sub
    Public Shared Sub OnMainPageTypeChanged()
        If FrmSetupUI Is Nothing Then Return
        Select Case CInt(Settings.Get(Of Integer)("UiCustomType"))
            Case 0 '无
                FrmSetupUI.PanCustomLocal.Visibility = Visibility.Collapsed
                FrmSetupUI.PanCustomNet.Visibility = Visibility.Collapsed
                FrmSetupUI.HintCustom.Visibility = Visibility.Collapsed
                FrmSetupUI.HintCustomWarn.Visibility = Visibility.Collapsed
            Case 1 '本地
                FrmSetupUI.PanCustomLocal.Visibility = Visibility.Visible
                FrmSetupUI.PanCustomNet.Visibility = Visibility.Collapsed
                FrmSetupUI.HintCustom.Visibility = Visibility.Visible
                FrmSetupUI.HintCustomWarn.Visibility = If(Settings.Get(Of Boolean)("HintCustomWarn"), Visibility.Collapsed, Visibility.Visible)
                FrmSetupUI.HintCustom.Text = $"从 RMCL 文件夹下的 Custom.xaml 读取主页内容。{vbCrLf}你可以手动编辑该文件，向主页添加文本、图片、常用网站、快捷启动等功能。"
                CustomEventService.SetEventType(FrmSetupUI.HintCustom, CustomEvent.EventType.None)
            Case 2 '联网
                FrmSetupUI.PanCustomLocal.Visibility = Visibility.Collapsed
                FrmSetupUI.PanCustomNet.Visibility = Visibility.Visible
                FrmSetupUI.HintCustom.Visibility = Visibility.Visible
                FrmSetupUI.HintCustomWarn.Visibility = If(Settings.Get(Of Boolean)("HintCustomWarn"), Visibility.Collapsed, Visibility.Visible)
                FrmSetupUI.HintCustom.Text = $"从指定网址联网获取主页内容。服主也可以用于动态更新服务器公告。"
                CustomEventService.SetEventType(FrmSetupUI.HintCustom, CustomEvent.EventType.None)
            Case Else '未知取值（含已移除的预设模式）一律按空白处理
                FrmSetupUI.PanCustomLocal.Visibility = Visibility.Collapsed
                FrmSetupUI.PanCustomNet.Visibility = Visibility.Collapsed
                FrmSetupUI.HintCustom.Visibility = Visibility.Collapsed
                FrmSetupUI.HintCustomWarn.Visibility = Visibility.Collapsed
        End Select
        FrmSetupUI.CardCustom.TriggerForceResize()
    End Sub
    Private Sub BtnCustomFolder_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCustomFolder.Click
        Dim CustomPath As String = Paths.Base & "RMCL\Custom.xaml"
        If FileUtils.Exists(CustomPath) Then
            OpenExplorer(CustomPath)
        Else
            OpenExplorer(Paths.Base & "RMCL\")
        End If
    End Sub

    '主题自定义
    Private Sub RadioLauncherTheme14_Change(sender As Object, e As RouteEventArgs) Handles RadioLauncherTheme14.Changed
        RefreshThemeSliderVisibility()
    End Sub
    ''' <summary>
    ''' 仅在选中「自定义」时显示 HSL 滑块。
    ''' </summary>
    Private Sub RefreshThemeSliderVisibility()
        Dim Show As Boolean = RadioLauncherTheme14.Checked
        For Each Control In New FrameworkElement() {LabLauncherHue, SliderLauncherHue, LabLauncherSat, SliderLauncherSat, LabLauncherDelta, SliderLauncherDelta, LabLauncherLight, SliderLauncherLight}
            Control.Visibility = Show.ToVisibility
        Next
        CardLauncher.TriggerForceResize()
    End Sub
    '炫目程度对所有主题（含预设色）都生效，所以这里不能像 HSL 那样强制切到自定义主题
    Private Sub SliderLauncherGradient_Change(sender As Object, user As Boolean) Handles SliderLauncherGradient.Change
        If AniControlEnabled <> 0 OrElse SliderLauncherGradient Is Nothing OrElse Not SliderLauncherGradient.IsLoaded Then Return
        ThemeRefresh()
    End Sub

    Private Sub HSL_Change() Handles SliderLauncherHue.Change, SliderLauncherLight.Change, SliderLauncherSat.Change, SliderLauncherDelta.Change
        If AniControlEnabled <> 0 OrElse SliderLauncherSat Is Nothing OrElse Not SliderLauncherSat.IsLoaded Then Return
        ThemeRefresh(ThemeCustom)
    End Sub

#Region "功能隐藏"

    Private Shared _HiddenForceShow As Boolean = False
    ''' <summary>
    ''' 是否强制显示被禁用的功能。
    ''' </summary>
    Public Shared Property HiddenForceShow As Boolean
        Get
            Return _HiddenForceShow
        End Get
        Set(value As Boolean)
            _HiddenForceShow = value
            HiddenRefresh()
        End Set
    End Property

    ''' <summary>
    ''' 更新功能隐藏带来的显示变化。
    ''' </summary>
    Public Shared Sub HiddenRefresh() Handles Me.Loaded
        If FrmMain.PanTitleSelect Is Nothing OrElse Not FrmMain.PanTitleSelect.IsLoaded Then Return
        Try
            '顶部栏
            Dim FriendsVisible As Boolean = HiddenForceShow OrElse Settings.Get(Of Boolean)("UiPageFriends")
            FrmMain.BtnTitleSelect3.Visibility = FriendsVisible.ToVisibility()
            If Not HiddenForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenPageDownload") AndAlso Settings.Get(Of Boolean)("UiHiddenPageSetup") AndAlso Not FriendsVisible Then
                '顶部栏已被全部隐藏（连同玻璃胶囊容器一起隐藏，避免留下空壳）
                FrmMain.BorderTitleDock.Visibility = Visibility.Collapsed
            Else
                '顶部栏未被全部隐藏
                FrmMain.BorderTitleDock.Visibility = Visibility.Visible
                FrmMain.BtnTitleSelect1.Visibility = If(Not HiddenForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenPageDownload"), Visibility.Collapsed, Visibility.Visible)
                FrmMain.BtnTitleSelect2.Visibility = If(Not HiddenForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenPageSetup"), Visibility.Collapsed, Visibility.Visible)
            End If
            '好友页面被关掉时，如果正停在好友页就先退回启动页
            If Not FriendsVisible AndAlso FrmMain.PageCurrent.Page = FormMain.PageType.Friends Then FrmMain.PageChange(FormMain.PageType.Launch)
            '功能
            FrmLaunchLeft.RefreshButtonsUI()
            If FrmSetupUI IsNot Nothing Then
                FrmSetupUI.CardSwitch.Visibility = If(Not HiddenForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenFunctionHidden"), Visibility.Collapsed, Visibility.Visible)
            End If
            '版本设置子页面
            If FrmInstanceLeft IsNot Nothing Then
                With FrmInstanceLeft
                    .ItemSetup.Visibility = HiddenView("UiHiddenInstanceSetup")
                    .ItemModify.Visibility = HiddenView("UiHiddenFunctionInstanceModify")
                    .ItemSaves.Visibility = HiddenView("UiHiddenInstanceSaves")
                    .ItemScreenshot.Visibility = HiddenView("UiHiddenInstanceScreenshot")
                    .ItemResourcePacks.Visibility = HiddenView("UiHiddenInstanceResourcePacks")
                    .ItemShaderPacks.Visibility = HiddenView("UiHiddenInstanceShaderPacks")
                    .ItemExport.Visibility = HiddenView("UiHiddenInstanceExport")
                    'Mod 管理有「该版本不支持 Mod」的替身项，交给它自己按隐藏设置选择
                    .RefreshModDisabledVisibility()
                    '当前正在看的子页面被隐藏了就退回概览
                    If InstancePageHidden(.PageID) Then .ItemOverall.SetChecked(True, False, False)
                End With
            End If
            '下载子页面
            If FrmDownloadLeft IsNot Nothing Then
                With FrmDownloadLeft
                    .ItemMod.Visibility = HiddenView("UiHiddenDownloadMod")
                    .ItemPack.Visibility = HiddenView("UiHiddenDownloadPack")
                    .ItemDataPack.Visibility = HiddenView("UiHiddenDownloadDataPack")
                    .ItemResourcePack.Visibility = HiddenView("UiHiddenDownloadResourcePack")
                    .ItemShader.Visibility = HiddenView("UiHiddenDownloadShader")
                    '当前正在看的子页面被隐藏了就退回「原版游戏」
                    Dim Current As FormMain.PageSubType = .PageID
                    Dim CurrentHidden As Boolean =
                        (Current = FormMain.PageSubType.DownloadMod AndAlso .ItemMod.Visibility <> Visibility.Visible) OrElse
                        (Current = FormMain.PageSubType.DownloadPack AndAlso .ItemPack.Visibility <> Visibility.Visible) OrElse
                        (Current = FormMain.PageSubType.DownloadDataPack AndAlso .ItemDataPack.Visibility <> Visibility.Visible) OrElse
                        (Current = FormMain.PageSubType.DownloadResourcePack AndAlso .ItemResourcePack.Visibility <> Visibility.Visible) OrElse
                        (Current = FormMain.PageSubType.DownloadShader AndAlso .ItemShader.Visibility <> Visibility.Visible)
                    If CurrentHidden Then .ItemInstall.SetChecked(True, False, False)
                End With
            End If
            '设置子页面
            If FrmSetupLeft IsNot Nothing Then
                Dim Items As New List(Of MyListItem) From {
                    FrmSetupLeft.ItemLaunch, FrmSetupLeft.ItemLink, FrmSetupLeft.ItemFrp, FrmSetupLeft.ItemJava,
                    FrmSetupLeft.ItemUI, FrmSetupLeft.ItemFeature, FrmSetupLeft.ItemUpdate, FrmSetupLeft.ItemSystem}
                For Each Item In Items
                    Item.Visibility = HiddenView(HiddenSetupKey(Item))
                Next
                Dim Available As List(Of MyListItem) = Items.Where(Function(i) i.Visibility = Visibility.Visible).ToList()
                If Available.Count = 0 Then
                    '全被隐藏时至少留「启动」，否则设置页进去就是一片空白
                    FrmSetupLeft.ItemLaunch.Visibility = Visibility.Visible
                    Available.Add(FrmSetupLeft.ItemLaunch)
                End If
                '正在看的子页面被隐藏了就切到第一个可用的（正停在个性化编辑隐藏项时不动，否则一勾就把自己踢走）
                Dim CurrentItem As MyListItem = Items.FirstOrDefault(Function(i) Val(i.Tag) = CInt(FrmSetupLeft.PageID))
                If CurrentItem IsNot Nothing AndAlso CurrentItem.Visibility <> Visibility.Visible AndAlso
                   FrmSetupLeft.PageID <> FormMain.PageSubType.SetupUI Then
                    Available(0).SetChecked(True, False, False)
                End If
                '只剩一个子页面时没必要留侧栏
                FrmSetupLeft.PanItem.Visibility = If(Available.Count < 2 AndAlso Not HiddenForceShow, Visibility.Collapsed, Visibility.Visible)
            End If
            '各个页面的入口
            If FrmMain.PageCurrent = FormMain.PageType.InstanceSelect Then FrmSelectRight.BtnEmptyDownload_Loaded()
            If FrmMain.PageCurrent = FormMain.PageType.Launch Then FrmLaunchLeft.RefreshButtonsUI()
            If FrmMain.PageCurrent = FormMain.PageType.InstanceSetup AndAlso FrmInstanceModDisabled IsNot Nothing Then FrmInstanceModDisabled.BtnDownload_Loaded()
            '备注
            If FrmSetupUI IsNot Nothing Then FrmSetupUI.CardSwitch.Title = If(HiddenForceShow, "功能隐藏（已暂时关闭，按 F12 以重新启用）", "功能隐藏")
        Catch ex As Exception
            Logger.Error(ex, "刷新功能隐藏项目失败")
        End Try
    End Sub

    ''' <summary>
    ''' 按设置决定某个隐藏项该不该显示。F12 临时显示时强制全部可见。
    ''' </summary>
    Private Shared Function HiddenView(Key As String) As Visibility
        If HiddenForceShow Then Return Visibility.Visible
        Return If(Settings.Get(Of Boolean)(Key), Visibility.Collapsed, Visibility.Visible)
    End Function

    ''' <summary>
    ''' 设置子页面的侧栏项对应的隐藏设置键。
    ''' </summary>
    Private Shared Function HiddenSetupKey(Item As MyListItem) As String
        If Item Is FrmSetupLeft.ItemLaunch Then Return "UiHiddenSetupLaunch"
        If Item Is FrmSetupLeft.ItemLink Then Return "UiHiddenSetupLink"
        If Item Is FrmSetupLeft.ItemFrp Then Return "UiHiddenSetupFrp"
        If Item Is FrmSetupLeft.ItemJava Then Return "UiHiddenSetupJava"
        If Item Is FrmSetupLeft.ItemUI Then Return "UiHiddenSetupUi"
        If Item Is FrmSetupLeft.ItemFeature Then Return "UiHiddenSetupFeature"
        If Item Is FrmSetupLeft.ItemUpdate Then Return "UiHiddenSetupUpdate"
        If Item Is FrmSetupLeft.ItemSystem Then Return "UiHiddenSetupSystem"
        Return ""
    End Function

    ''' <summary>
    ''' 版本设置里某个子页面是否已被隐藏（依据侧栏项当前的可见性判断）。
    ''' </summary>
    Private Shared Function InstancePageHidden(ID As FormMain.PageSubType) As Boolean
        Dim Item As MyListItem = Nothing
        Select Case ID
            Case FormMain.PageSubType.InstanceSetup
                Item = FrmInstanceLeft.ItemSetup
            Case FormMain.PageSubType.InstanceModify
                Item = FrmInstanceLeft.ItemModify
            Case FormMain.PageSubType.InstanceSaves
                Item = FrmInstanceLeft.ItemSaves
            Case FormMain.PageSubType.InstanceScreenshot
                Item = FrmInstanceLeft.ItemScreenshot
            Case FormMain.PageSubType.InstanceResourcePacks
                Item = FrmInstanceLeft.ItemResourcePacks
            Case FormMain.PageSubType.InstanceShaderPacks
                Item = FrmInstanceLeft.ItemShaderPacks
            Case FormMain.PageSubType.InstanceExport
                Item = FrmInstanceLeft.ItemExport
            Case FormMain.PageSubType.InstanceMod
                'Mod 管理有两个替身项，都藏起来才算隐藏
                Return FrmInstanceLeft.ItemMod.Visibility <> Visibility.Visible AndAlso
                       FrmInstanceLeft.ItemModDisabled.Visibility <> Visibility.Visible
            Case Else
                Return False
        End Select
        Return Item IsNot Nothing AndAlso Item.Visibility <> Visibility.Visible
    End Function

    'UI 协同改变
    Private Sub HiddenSetupMain() Handles CheckHiddenPageSetup.Change
        '设置主页面：勾上就把所有设置子页面一起勾上，取消时再一起放开
        If CheckHiddenPageSetup.Checked Then
            For Each Item In HiddenSetupItems()
                Item.Checked = True
            Next
        Else
            If HiddenSetupItems().All(Function(i) i.Checked) Then
                For Each Item In HiddenSetupItems()
                    Item.Checked = False
                Next
            End If
        End If
    End Sub
    Private Sub HiddenSetupSub() Handles CheckHiddenSetupLaunch.Change, CheckHiddenSetupSystem.Change, CheckHiddenSetupLink.Change,
                                        CheckHiddenSetupUI.Change, CheckHiddenSetupFeature.Change, CheckHiddenSetupFrp.Change,
                                        CheckHiddenSetupJava.Change, CheckHiddenSetupUpdate.Change
        '设置子页面
        CheckHiddenPageSetup.Checked = HiddenSetupItems().All(Function(i) i.Checked)
    End Sub
    ''' <summary>
    ''' 「设置」主页面下所有可隐藏的子页面勾选项。
    ''' </summary>
    Private Function HiddenSetupItems() As List(Of MyCheckBox)
        Return New List(Of MyCheckBox) From {
            CheckHiddenSetupLaunch, CheckHiddenSetupLink, CheckHiddenSetupFrp, CheckHiddenSetupJava,
            CheckHiddenSetupUI, CheckHiddenSetupFeature, CheckHiddenSetupUpdate, CheckHiddenSetupSystem}
    End Function
    '警告提示
    Private Sub HiddenHint(sender As Object, user As Boolean) Handles CheckHiddenFunctionHidden.Change, CheckHiddenPageSetup.Change, CheckHiddenSetupUI.Change
        If AniControlEnabled = 0 AndAlso sender.Checked Then Hint("按 F12 即可暂时关闭功能隐藏设置。千万别忘了，要不然设置就改不回来了……")
    End Sub

#End Region

    '滑动条
    Private Sub SliderLoad()
        SliderMusicVolume.GetHintText = Function(v) Math.Ceiling(v * 0.1) & "%"
        SliderLauncherTransparent.GetHintText = Function(v) Math.Round(40 + v * 0.1) & "%"
        SliderSidebarOpacity.GetHintText = Function(v) Math.Round(v * 0.1) & "%"
        SliderLauncherHue.GetHintText = Function(v) v & "°"
        SliderLauncherSat.GetHintText = Function(v) v & "%"
        SliderLauncherGradient.GetHintText = Function(v) v & "%"
        SliderLauncherDelta.GetHintText =
        Function(Value As Integer) As String
            If Value > 90 Then
                Return "+" & (Value - 90)
            ElseIf Value = 90 Then
                Return 0
            Else
                Return Value - 90
            End If
        End Function
        SliderLauncherLight.GetHintText =
        Function(Value As Integer) As String
            If Value > 20 Then
                Return "+" & (Value - 20)
            ElseIf Value = 20 Then
                Return 0
            Else
                Return Value - 20
            End If
        End Function
        SliderBackgroundOpacity.GetHintText = Function(v) Math.Round(v * 0.1) & "%"
        SliderBackgroundBlur.GetHintText = Function(v) v & " 像素"
    End Sub

End Class
