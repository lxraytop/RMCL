Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Media.Effects

''' <summary>
''' 更新下载进度弹窗。
''' 做成独立窗口而不是塞在设置页里：自动更新是从启动弹窗触发的，那时用户可能停留在任何页面，
''' 只有独立窗口才能保证「无论从哪触发都看得到进度」。
''' </summary>
Friend Class MyUpdateProgress
    Inherits Window

    Private ReadOnly LabDetail As TextBlock
    Private ReadOnly LabPercent As TextBlock
    Private ReadOnly ColFill As ColumnDefinition
    Private ReadOnly BtnCancel As MyButton
    Private ReadOnly StateListener As Action
    Private IsClosingNow As Boolean

    Public Sub New()
        '窗口本体
        WindowStyle = WindowStyle.None
        AllowsTransparency = True
        Background = Brushes.Transparent
        ResizeMode = ResizeMode.NoResize
        ShowInTaskbar = False
        WindowStartupLocation = WindowStartupLocation.CenterOwner
        SizeToContent = SizeToContent.Height
        Width = 430
        Title = "下载更新"
        Topmost = False

        '卡片
        Dim Card As New Border With {.CornerRadius = New CornerRadius(7), .Margin = New Thickness(25)}
        Card.SetResourceReference(Border.BackgroundProperty, "ColorBrushDialogBackground")
        Card.Effect = New DropShadowEffect With {
            .BlurRadius = 20, .ShadowDepth = 4, .RenderingBias = RenderingBias.Performance,
            .Opacity = 0.8, .Color = Color.FromArgb(90, 0, 0, 0)
        }

        Dim Panel As New StackPanel With {.Margin = New Thickness(22, 18, 22, 18)}

        '标题
        Dim LabTitle As New TextBlock With {.Text = "正在下载更新", .FontSize = 21, .Margin = New Thickness(2, 0, 0, 10)}
        LabTitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
        Panel.Children.Add(LabTitle)

        '说明
        LabDetail = New TextBlock With {.Text = "正在准备……", .FontSize = 14, .TextWrapping = TextWrapping.Wrap, .LineHeight = 20}
        LabDetail.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushDialogText")
        Panel.Children.Add(LabDetail)

        '进度条（自绘：双列 Star 比例，跟随窗口宽度自适应）
        Dim Track As New Grid With {.Height = 6.0, .Margin = New Thickness(0, 14, 0, 8)}
        ColFill = New ColumnDefinition With {.Width = New GridLength(0, GridUnitType.Star)}
        Track.ColumnDefinitions.Add(ColFill)
        Track.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Dim Fill As New Border With {.CornerRadius = New CornerRadius(3)}
        Fill.SetResourceReference(Border.BackgroundProperty, "ColorBrush2")
        Dim Rest As New Border With {.CornerRadius = New CornerRadius(3)}
        Rest.SetResourceReference(Border.BackgroundProperty, "ColorBrushGlassInner")
        Grid.SetColumn(Fill, 0)
        Grid.SetColumn(Rest, 1)
        Track.Children.Add(Fill)
        Track.Children.Add(Rest)
        Panel.Children.Add(Track)

        '百分比 + 取消
        Dim Bottom As New Grid
        Bottom.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Bottom.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
        LabPercent = New TextBlock With {.Text = "0%", .FontSize = 13, .VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(2, 0, 0, 0)}
        LabPercent.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushDialogText")
        BtnCancel = New MyButton With {
            .Text = "取消下载", .ColorType = MyButton.ColorState.Normal,
            .Padding = New Thickness(5, 0, 5, 0), .TextPadding = New Thickness(7),
            .MinWidth = 96, .HorizontalAlignment = HorizontalAlignment.Right
        }
        AddHandler BtnCancel.Click, Sub(s, e)
                                        BtnCancel.IsEnabled = False
                                        BtnCancel.Text = "正在取消……"
                                        ModUpdate.CancelDownload()
                                    End Sub
        Grid.SetColumn(LabPercent, 0)
        Grid.SetColumn(BtnCancel, 1)
        Bottom.Children.Add(LabPercent)
        Bottom.Children.Add(BtnCancel)
        Panel.Children.Add(Bottom)

        Card.Child = Panel
        Content = Card

        '跟随共享状态刷新
        StateListener = Sub() RunInUi(Sub() RefreshUi())
        ModUpdate.RegisterListener(StateListener)
        AddHandler Closed, Sub(s, e) ModUpdate.UnregisterListener(StateListener)
        RefreshUi()
    End Sub

    Private Sub RefreshUi()
        Try
            If IsClosingNow Then Return
            Dim Percent As Double = Math.Max(0, Math.Min(1, ModUpdate.Progress))
            ColFill.Width = New GridLength(Math.Max(0.001, Percent), GridUnitType.Star)
            LabPercent.Text = $"{Percent * 100:F0}%"

            Dim Info As ModUpdate.UpdateInfo = ModUpdate.Latest
            Dim Text As String = "正在准备下载……"
            If Info IsNot Nothing Then
                Dim SizeText As String = If(Info.AssetSize > 0, FormatSize(Info.AssetSize), "大小未知")
                Text = $"RMCL {Info.Version}（{SizeText}）"
            End If
            '连接建立、分段调度这些阶段没有字节进度，只看百分比会以为卡死了
            Dim Phase As String = ModUpdate.Phase
            If Not String.IsNullOrEmpty(Phase) Then Text &= vbCrLf & Phase
            LabDetail.Text = Text

            '下载结束（成功或失败）就自己关掉，后续提示由调用方负责
            If ModUpdate.Status = ModUpdate.UpdateStatus.Downloaded OrElse
               ModUpdate.Status = ModUpdate.UpdateStatus.Failed Then
                IsClosingNow = True
                Close()
            End If
        Catch ex As Exception
            Logger.Warn(ex, "刷新更新进度窗口失败")
        End Try
    End Sub
End Class
