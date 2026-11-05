''' <summary>
''' 设置 · 更新。
'''
''' 从「设置 → 功能」里的软件更新卡片独立出来的子页面：只有检查、下载、安装与更新源相关的东西，
''' 不再和内存优化、清理垃圾挤在同一页。
''' </summary>
Public Class PageSetupUpdate

    Private IsLoad As Boolean = False

    Private Sub PageSetupUpdate_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        UpdateUiInit()

        If IsLoad Then
            '第二次及以后进入：只要把设置与状态同步过来
            Reload()
            Return
        End If
        IsLoad = True
        AniControlEnabled += 1
        Reload()
        AniControlEnabled -= 1
    End Sub

    ''' <summary>
    ''' 离开页面时注销更新状态监听，避免页面的旧实例一直挂在回调上。
    ''' </summary>
    Private Sub PageSetupUpdate_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        UpdateUiRelease()
    End Sub

    Public Sub Reload()
        SettingService.RefreshSettings(Me)
        RefreshSourcePanels()
    End Sub

    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Logger.Info("已初始化更新页设置")
            Hint("已初始化更新页设置！", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化更新页设置失败", LogBehavior.Alert)
        End Try
        Reload()
    End Sub

#Region "版本信息"

    ''' <summary>
    ''' 拼出「当前版本」那段文字。最新版本与检查结果由 RefreshUpdateView 追加。
    ''' </summary>
    Private Function VersionInfoText() As String
        Return $"当前版本：{BuildTypeDisplay} {VersionBaseName}（版本代号 {VersionCode}）" & vbCrLf &
               $"更新源：{ModUpdate.SourceDescription}" & vbCrLf &
               $"程序位置：{PathExe}"
    End Function

    Private Sub RefreshVersionInfo()
        Try
            LabVersionInfo.Text = VersionInfoText()
        Catch ex As Exception
            Logger.Warn(ex, "刷新版本信息失败")
            LabVersionInfo.Text = "无法读取版本信息。"
        End Try
    End Sub

    Private Sub BtnCopyVersion_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCopyVersion.Click
        Try
            Dim Text As String = VersionInfoText()
            Dim Info As ModUpdate.UpdateInfo = ModUpdate.Latest
            If Info IsNot Nothing Then Text &= vbCrLf & $"最新版本：{Info.Version}"
            Clipboard.SetText(Text)
            Hint("已复制版本信息到剪贴板", HintType.Green, False)
        Catch ex As Exception
            Logger.Warn(ex, "复制版本信息失败")
            Hint("复制版本信息失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnUpdatePage_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUpdatePage.Click
        OpenWebsite(If(ModUpdate.Latest?.PageUrl, ModUpdate.UpdatePage))
    End Sub

#End Region

#Region "软件更新"

    ''' <summary>
    ''' 状态变化的回调，用于把后台的检查/下载进度同步到界面。
    ''' </summary>
    Private UpdateListener As Action

    Private Sub UpdateUiInit()
        If UpdateListener Is Nothing Then UpdateListener = Sub() RunInUi(Sub() RefreshUpdateView())
        ModUpdate.RegisterListener(UpdateListener)
        RefreshVersionInfo()
        RefreshUpdateView()
    End Sub

    Private Sub UpdateUiRelease()
        If UpdateListener IsNot Nothing Then ModUpdate.UnregisterListener(UpdateListener)
    End Sub

    ''' <summary>下拉框当前选择。直接读控件，避免设置还没落盘时读到旧值。</summary>
    Private Function SelectedSource() As ModUpdate.UpdateSource
        Select Case ComboUpdateSource.SelectedIndex
            Case 1
                Return ModUpdate.UpdateSource.GitHubMirror
            Case 2
                Return ModUpdate.UpdateSource.Official
            Case Else
                Return ModUpdate.UpdateSource.GitHubNative
        End Select
    End Function

    Private Function SourceShortName() As String
        Select Case SelectedSource()
            Case ModUpdate.UpdateSource.GitHubMirror
                Return "GitHub 镜像"
            Case ModUpdate.UpdateSource.Official
                Return "官方源"
            Case Else
                Return "GitHub"
        End Select
    End Function

    ''' <summary>切换更新源时，显示对应的设置区。</summary>
    Private Sub ComboUpdateSource_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        RefreshSourcePanels()
    End Sub

    Private Sub RefreshSourcePanels()
        Try
            Dim Mode As ModUpdate.UpdateSource = SelectedSource()
            PanMirrorSettings.Visibility = If(Mode = ModUpdate.UpdateSource.GitHubMirror, Visibility.Visible, Visibility.Collapsed)
            PanOfficialSettings.Visibility = If(Mode = ModUpdate.UpdateSource.Official, Visibility.Visible, Visibility.Collapsed)
            RefreshLicenseView()
            RefreshVersionInfo()
            RefreshUpdateView()
        Catch ex As Exception
            Logger.Warn(ex, "刷新更新源界面失败")
        End Try
    End Sub

    ''' <summary>
    ''' 设置里的更新源与下拉框选中项不一致时同步过来。
    ''' 退出官方源时会由 ModUpdate 自动切到 GitHub，用户不必自己再去改一次。
    ''' </summary>
    Private Sub SyncSourceSelection()
        Try
            Dim Saved As Integer = CInt(Settings.Get(Of Integer)("UpdateSourceMode"))
            If Saved < 0 OrElse Saved >= ComboUpdateSource.Items.Count Then Return
            If ComboUpdateSource.SelectedIndex = Saved Then Return
            Logger.Info($"更新源已被自动切换为 {Saved}，同步下拉框")
            ComboUpdateSource.SelectedIndex = Saved
        Catch ex As Exception
            Logger.Warn(ex, "同步更新源下拉框失败")
        End Try
    End Sub

    ''' <summary>刷新官方源的授权状态文字。</summary>
    Private Sub RefreshLicenseView()
        Try
            Dim Token As String = ModUpdateServer.CurrentToken()
            Dim Expire As Long = ModUpdateServer.CurrentExpire()

            If String.IsNullOrEmpty(Token) Then
                LabLicenseInfo.Text = "授权状态：未授权"
                LabLicenseDetail.Text = "检查与下载更新前需要通过验证码授权，一张码默认绑定一台设备。"
                BtnLicenseClear.IsEnabled = False
                Return
            End If

            BtnLicenseClear.IsEnabled = True
            If Expire <= GetUnixTimestampUtc() Then
                LabLicenseInfo.Text = "授权状态：已到期"
                LabLicenseDetail.Text = $"{ModUpdateServer.LicenseSummary()}。到期后服务器会拒绝更新，请联系管理员续期。"
                Return
            End If

            LabLicenseInfo.Text = "授权状态：" & ModUpdateServer.LicenseSummary()
            Dim Mask As String = ModUpdateServer.CurrentMask()
            LabLicenseDetail.Text = If(String.IsNullOrEmpty(Mask),
                                       "检查与下载更新都会校验这份授权。",
                                       $"当前验证码：{Mask}")
        Catch ex As Exception
            Logger.Warn(ex, "刷新授权状态失败")
        End Try
    End Sub

    ''' <summary>
    ''' 弹窗输入验证码并激活，返回是否成功。设置页按钮与「下载前授权检查」共用。
    ''' 必须在 UI 线程调用（内部要弹模态框）。
    ''' </summary>
    Private Function ActivateLicenseInteractive() As Boolean
        Dim Input As String = MyMsgBoxInput("更新授权",
            "请输入管理员提供的验证码。一张码默认绑定一台设备；" & vbCrLf &
            "授权到期后会自动失效，需要续期才能继续更新。", "",
            Nothing, "XXXX-XXXX-XXXX-XXXX", "激活", "取消")
        If String.IsNullOrEmpty(Input) Then Return False

        '网络请求不能放在 UI 线程（NetRequestByClient 会直接拒绝），这里另起线程跑完再回来
        Dim State As ModUpdateServer.LicenseInfo = Nothing
        Dim Failure As String = ""
        Dim Worker As New Thread(
            Sub()
                Try
                    State = ModUpdateServer.Activate(Input)
                Catch ex As ModUpdateServer.UpdateLicenseException
                    Failure = ex.Message
                Catch ex As Exception
                    Failure = ex.Message
                End Try
            End Sub)
        Worker.IsBackground = True
        Worker.Start()
        If Not Worker.Join(20000) Then Failure = "激活请求超时，请检查服务器地址与网络连接"

        If State Is Nothing Then
            Logger.Warn($"激活更新授权失败：{Failure}")
            MyMsgBox("激活失败：" & Failure, "更新授权", "知道了", IsWarn:=True)
            Return False
        End If

        Hint($"授权成功，剩余 {State.RemainingDays} 天（{State.ExpireText} 到期）", HintType.Green)
        RefreshLicenseView()
        RefreshUpdateView()
        Return True
    End Function

    Private Sub BtnLicenseActivate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLicenseActivate.Click
        If Not BtnLicenseActivate.IsEnabled Then Return
        ActivateLicenseInteractive()
    End Sub

    Private Sub BtnLicenseClear_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLicenseClear.Click
        If Not BtnLicenseClear.IsEnabled Then Return
        If MyMsgBox("解除后本机需要重新输入验证码才能使用官方源。" & vbCrLf &
                    "验证码本身不会被注销；换机时请让管理员在后台解绑那台设备。",
                    "解除本地授权", "解除", "取消", IsWarn:=True) <> 1 Then Return
        ModUpdateServer.ClearLicense()
        Hint("已解除本地授权", HintType.Green)
        RefreshLicenseView()
        RefreshUpdateView()
    End Sub

    ''' <summary>
    ''' 按共享状态刷新整个页面。自动更新与手动更新走同一份状态，界面不会各说各话。
    ''' </summary>
    Private Sub RefreshUpdateView()
        Try
            '更新源可能被自动切换（例如官方源授权到期后回退到 GitHub），下拉框要跟着走
            SyncSourceSelection()
            Dim Info As ModUpdate.UpdateInfo = ModUpdate.Latest
            Dim CurStatus As ModUpdate.UpdateStatus = ModUpdate.Status
            Dim VersionText As String = $"当前版本：{VersionBaseName}"
            Dim LatestText As String = ""
            If Info IsNot Nothing Then
                LatestText = $"最新版本：{Info.Version}"
                If Info.PublishedLocal.HasValue Then LatestText &= $"，发布于 {Info.PublishedLocal.Value:yyyy-MM-dd HH:mm}"
            End If

            BtnUpdateCheck.IsEnabled = CurStatus <> ModUpdate.UpdateStatus.Checking AndAlso CurStatus <> ModUpdate.UpdateStatus.Downloading
            BtnUpdateCheck.Text = If(CurStatus = ModUpdate.UpdateStatus.Checking, "检查中……", "检查更新")
            BtnUpdateInstall.IsEnabled = CurStatus <> ModUpdate.UpdateStatus.Checking AndAlso CurStatus <> ModUpdate.UpdateStatus.Downloading
            If CurStatus = ModUpdate.UpdateStatus.Downloading Then
                PanUpdateProgress.Visibility = Visibility.Visible
                ColUpdateFill.Width = New GridLength(Math.Max(0.001, ModUpdate.Progress), GridUnitType.Star)
                ColUpdateRest.Width = New GridLength(Math.Max(0.001, 1 - ModUpdate.Progress), GridUnitType.Star)
                Dim Phase As String = ModUpdate.Phase
                If String.IsNullOrEmpty(Phase) Then Phase = "正在下载"
                LabUpdateProgress.Text = $"{Phase.TrimEnd("…"c)}  {ModUpdate.Progress * 100:F0}%"
            Else
                PanUpdateProgress.Visibility = Visibility.Collapsed
            End If

            Select Case CurStatus
                Case ModUpdate.UpdateStatus.Idle
                    LabUpdateInfo.Text = VersionText & vbCrLf & $"更新源：{ModUpdate.SourceDescription}"
                    If SelectedSource() = ModUpdate.UpdateSource.Official Then
                        LabUpdateInfo.Text &= vbCrLf & "授权状态：" & ModUpdateServer.LicenseSummary()
                    End If
                    BtnUpdateInstall.Visibility = Visibility.Collapsed
                    HideUpdateNotes()
                Case ModUpdate.UpdateStatus.Checking
                    LabUpdateInfo.Text = VersionText & vbCrLf & $"正在从{SourceShortName()}检查更新……"
                    BtnUpdateInstall.Visibility = Visibility.Collapsed
                    HideUpdateNotes()
                Case ModUpdate.UpdateStatus.UpToDate
                    LabUpdateInfo.Text = VersionText & "（已是最新版本）" & vbCrLf & LatestText
                    BtnUpdateInstall.Visibility = Visibility.Collapsed
                    HideUpdateNotes()
                Case ModUpdate.UpdateStatus.Available
                    LabUpdateInfo.Text = VersionText & vbCrLf & LatestText &
                        If(Info IsNot Nothing AndAlso Info.HasAsset, "", vbCrLf & "该版本没有提供 exe 资源，请到发布页手动下载。")
                    BtnUpdateInstall.Text = "下载并安装"
                    BtnUpdateInstall.Visibility = If(Info IsNot Nothing AndAlso Info.HasAsset, Visibility.Visible, Visibility.Collapsed)
                    ShowUpdateNotes(Info)
                Case ModUpdate.UpdateStatus.Downloading
                    LabUpdateInfo.Text = VersionText & vbCrLf & LatestText
                    BtnUpdateInstall.Text = "下载中……"
                    BtnUpdateInstall.Visibility = Visibility.Visible
                    ShowUpdateNotes(Info)
                Case ModUpdate.UpdateStatus.Downloaded
                    LabUpdateInfo.Text = VersionText & vbCrLf & $"已下载 {If(Info?.Version, "")}，退出启动器后自动安装。"
                    BtnUpdateInstall.Text = "立即重启安装"
                    BtnUpdateInstall.Visibility = Visibility.Visible
                    ShowUpdateNotes(Info)
                Case ModUpdate.UpdateStatus.Failed
                    LabUpdateInfo.Text = $"更新失败：{ModUpdate.Message}{vbCrLf}{VersionText}"
                    BtnUpdateInstall.Text = "重新下载"
                    BtnUpdateInstall.Visibility = If(Info IsNot Nothing AndAlso Info.HasAsset, Visibility.Visible, Visibility.Collapsed)
                    If Info IsNot Nothing Then ShowUpdateNotes(Info) Else HideUpdateNotes()
            End Select
        Catch ex As Exception
            Logger.Warn(ex, "刷新更新界面失败")
        End Try
    End Sub

    Private Sub ShowUpdateNotes(Info As ModUpdate.UpdateInfo)
        '没有公告就整块收起来，别在设置页留一个空面板
        If Info Is Nothing OrElse String.IsNullOrEmpty(Info.Notes) Then
            HideUpdateNotes()
            Return
        End If
        TextUpdateNotes.Text = Info.Notes
        LabUpdateNotesTitle.Visibility = Visibility.Visible
        PanUpdateNotes.Visibility = Visibility.Visible
    End Sub

    Private Sub HideUpdateNotes()
        LabUpdateNotesTitle.Visibility = Visibility.Collapsed
        PanUpdateNotes.Visibility = Visibility.Collapsed
    End Sub

    Private Sub BtnUpdateCheck_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUpdateCheck.Click
        '手动检查：结果用 Hint 明确告知（有新版本 / 已是最新 / 失败）
        RunInNewThread(Sub() ModUpdate.Check(Manual:=True), "检查更新")
    End Sub

    Private Sub BtnUpdateInstall_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUpdateInstall.Click
        Dim Info As ModUpdate.UpdateInfo = ModUpdate.Latest
        If Info Is Nothing OrElse Not Info.HasAsset Then Return

        '已经下载好了：这里只负责再问一次是否立刻重启
        If ModUpdate.Status = ModUpdate.UpdateStatus.Downloaded Then
            Dim Staged As String = ModUpdate.StagedPath
            If String.IsNullOrEmpty(Staged) Then Return
            ModUpdate.PromptInstallAfterDownload(Info, Staged)
            Return
        End If
        If ModUpdate.Status = ModUpdate.UpdateStatus.Downloading Then Return

        '官方源：下载前再确认一次授权。过期时直接引导重新激活，免得下到一半被服务器拒绝
        If Info.IsOfficial AndAlso Not ModUpdateServer.HasValidLicense Then
            If Not ActivateLicenseInteractive() Then Return
        End If

        Dim SizeText As String = If(Info.AssetSize > 0, FormatSize(Info.AssetSize), "大小未知")
        If MyMsgBox($"即将下载 RMCL {Info.Version}（{SizeText}）。" & vbCrLf &
                    "下载完成后可以立即重启安装，也可以选择在退出启动器时自动安装。",
                    "安装更新", "下载", "取消") <> 1 Then Return
        ModUpdate.DownloadAndPrompt(Info)
    End Sub

#End Region

End Class
