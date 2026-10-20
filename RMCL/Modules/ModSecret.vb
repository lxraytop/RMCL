'由于包含加解密等安全信息，本文件中的部分代码已被删除

Friend Module ModSecret

    '标注 RMCL 的不同分支，仅用于替换标记
    Public Const VersionBranchMain As String = "OpenSource"
    '在开源版的注册表与常规版的注册表隔离，以防数据冲突
    Public Const RegFolder As String = "RMCLDebug"
    '用于微软登录的 ClientId
    Public OAuthClientId As String = If(Environment.GetEnvironmentVariable("RMCL_MS_CLIENT_ID"), "ab25b229-f826-46db-b646-01035c3a3e52")
    'CurseForge API Key
    Public CurseForgeAPIKey As String = If(Environment.GetEnvironmentVariable("RMCL_CURSEFORGE_API_KEY"), "")

#Region "网络鉴权"

    Friend Function SecretCdnSign(UrlWithMark As String) As String
        If Not UrlWithMark.EndsWithF("{CDN}") Then Return UrlWithMark
        Return UrlWithMark.Replace("{CDN}", "").Replace(" ", "%20")
    End Function
    ''' <summary>
    ''' 设置 Headers 的 UA、Referer。
    ''' </summary>
    Friend Sub SecretHeadersSign(Url As String, ByRef Req As HttpRequestMessage, Optional SimulateBrowserHeaders As Boolean = False)
        If Not Req.Headers.UserAgent.Any Then
            If Url.Contains("baidupcs.com") OrElse Url.Contains("baidu.com") Then
                Req.Headers.Add("User-Agent", "LogStatistic")  '#4951
            ElseIf SimulateBrowserHeaders Then
                Req.Headers.Add("User-Agent", $"RMCL/{VersionBaseName}.{CInt(BuildType)} Mozilla/5.0 AppleWebKit/537.36 Chrome/63.0.3239.132 Safari/537.36")
            Else
                Req.Headers.Add("User-Agent", $"RMCL/{VersionBaseName}.{CInt(BuildType)}")
            End If
        End If
        If Not SimulateBrowserHeaders Then Req.Headers.Add("Referer", $"http://{VersionCode}.open.rmcl.server/")
        If Url.Contains("api.curseforge.com") OrElse Url.Contains("forgecdn.net") Then Req.Headers.Add("x-api-key", CurseForgeAPIKey)
    End Sub

#End Region

#Region "主题"

    Public Color1 As New MyColor(52, 61, 74)
    Public Color2 As New MyColor(11, 91, 203)
    Public Color3 As New MyColor(19, 112, 243)
    Public Color4 As New MyColor(72, 144, 245)
    Public Color5 As New MyColor(150, 192, 249)
    Public Color6 As New MyColor(213, 230, 253)
    Public Color7 As New MyColor(222, 236, 253)
    Public Color8 As New MyColor(234, 242, 254)
    Public ColorBg0 As New MyColor(150, 192, 249)
    Public ColorBg1 As New MyColor(190, Color7)
    Public ColorGray1 As New MyColor(64, 64, 64)
    Public ColorGray2 As New MyColor(115, 115, 115)
    Public ColorGray3 As New MyColor(140, 140, 140)
    Public ColorGray4 As New MyColor(166, 166, 166)
    Public ColorGray5 As New MyColor(204, 204, 204)
    Public ColorGray6 As New MyColor(235, 235, 235)
    Public ColorGray7 As New MyColor(240, 240, 240)
    Public ColorGray8 As New MyColor(245, 245, 245)
    Public ColorSemiTransparent As New MyColor(1, Color8)

    Public ThemeNow As Integer = -1
    Public ColorHue As Integer = 210, ColorSat As Integer = 85, ColorLightAdjust As Integer = 0, ColorHueTopbarDelta As OneOf(Of Integer, Integer()) = 0
    Public ThemeDontClick As Integer = 0

    ''' <summary>
    ''' 可用主题：编号 → {色相, 饱和度, 亮度偏移, 顶栏渐变差}。
    ''' 仅保留蓝、青、绿、黄、红五种预设颜色，其余由“自定义”提供。
    ''' </summary>
    Public ReadOnly ThemePresets As New Dictionary(Of Integer, Integer()) From {
        {0, New Integer() {210, 85, 0, 0}},   ' 蓝
        {1, New Integer() {186, 76, 0, 0}},   ' 青
        {2, New Integer() {142, 62, 0, 0}},   ' 绿
        {3, New Integer() {45, 82, 0, 0}},    ' 黄
        {10, New Integer() {356, 74, 0, 0}}   ' 红
    }
    Public Const ThemeCustom As Integer = 14

    ''' <summary>
    ''' 将数值限制在指定区间内（.NET Framework 4.8 无 Math.Clamp）。
    ''' </summary>
    Private Function ClampInt(Value As Integer, Min As Integer, Max As Integer) As Integer
        If Value < Min Then Return Min
        If Value > Max Then Return Max
        Return Value
    End Function

    ''' <summary>
    ''' 根据当前主题编号刷新全局配色。
    ''' </summary>
    Public Sub ThemeRefresh(Optional NewTheme As Integer = -1)
        Try
            If NewTheme >= 0 Then ThemeNow = NewTheme
            If ThemeNow < 0 Then ThemeNow = Settings.Get(Of Integer)("UiLauncherTheme")

            '解析主题参数
            If ThemeNow = ThemeCustom Then
                '自定义：直接读取 HSL 滑块
                ColorHue = ClampInt(Settings.Get(Of Integer)("UiLauncherHue"), 0, 360)
                ColorSat = ClampInt(Settings.Get(Of Integer)("UiLauncherSat"), 0, 100)
                ColorLightAdjust = ClampInt(Settings.Get(Of Integer)("UiLauncherLight"), 0, 40) - 20
                ColorHueTopbarDelta = CInt(ClampInt(Settings.Get(Of Integer)("UiLauncherDelta"), 0, 180) * 0.3)
            Else
                Dim Preset As Integer() = Nothing
                If Not ThemePresets.TryGetValue(ThemeNow, Preset) Then Preset = ThemePresets(0) '未知主题回退为蓝色
                ColorHue = Preset(0)
                ColorSat = Preset(1)
                ColorLightAdjust = Preset(2)
                ColorHueTopbarDelta = Preset(3)
            End If

            Color1 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.2, 25 + ColorLightAdjust * 0.3)
            Color2 = New MyColor().FromHSL2(ColorHue, ColorSat, 45 + ColorLightAdjust)
            Color3 = New MyColor().FromHSL2(ColorHue, ColorSat, 55 + ColorLightAdjust)
            Color4 = New MyColor().FromHSL2(ColorHue, ColorSat, 65 + ColorLightAdjust)
            Color5 = New MyColor().FromHSL2(ColorHue, ColorSat, 80 + ColorLightAdjust * 0.4)
            Color6 = New MyColor(236, New MyColor().FromHSL2(ColorHue, ColorSat, 91 + ColorLightAdjust * 0.1))
            Color7 = New MyColor(216, New MyColor().FromHSL2(ColorHue, ColorSat, 95))
            Color8 = New MyColor(190, New MyColor().FromHSL2(ColorHue, ColorSat, 97))
            ColorBg0 = Color4 * 0.4 + Color5 * 0.4 + ColorGray4 * 0.2
            ColorBg1 = New MyColor(190, Color7)

            ColorSemiTransparent = New MyColor(1, Color8)
            Application.Current.Resources("ColorBrush1") = New SolidColorBrush(Color1)
            Application.Current.Resources("ColorBrush2") = New SolidColorBrush(Color2)
            Application.Current.Resources("ColorBrush3") = New SolidColorBrush(Color3)
            Application.Current.Resources("ColorBrush4") = New SolidColorBrush(Color4)
            Application.Current.Resources("ColorBrush5") = New SolidColorBrush(Color5)
            Application.Current.Resources("ColorBrush6") = New SolidColorBrush(Color6)
            Application.Current.Resources("ColorBrush7") = New SolidColorBrush(Color7)
            Application.Current.Resources("ColorBrush8") = New SolidColorBrush(Color8)
            Application.Current.Resources("ColorBrushBg0") = New SolidColorBrush(ColorBg0)
            Application.Current.Resources("ColorBrushBg1") = New SolidColorBrush(ColorBg1)
            Application.Current.Resources("ColorObject1") = CType(Color1, Color)
            Application.Current.Resources("ColorObject2") = CType(Color2, Color)
            Application.Current.Resources("ColorObject3") = CType(Color3, Color)
            Application.Current.Resources("ColorObject4") = CType(Color4, Color)
            Application.Current.Resources("ColorObject5") = CType(Color5, Color)
            Application.Current.Resources("ColorObject6") = CType(Color6, Color)
            Application.Current.Resources("ColorObject7") = CType(Color7, Color)
            Application.Current.Resources("ColorObject8") = CType(Color8, Color)
            Application.Current.Resources("ColorObjectBg0") = CType(ColorBg0, Color)
            Application.Current.Resources("ColorObjectBg1") = CType(ColorBg1, Color)
            ThemeRefreshMain()
        Catch ex As Exception
            Logger.Error(ex, "刷新主题颜色失败", LogBehavior.Toast)
        End Try
    End Sub
    Public Sub ThemeRefreshMain()
        RunInUi(
        Sub()
            If Not FrmMain.IsLoaded Then Return
            '顶部条：浅色半透明玻璃，只保留极淡的主题色调，避免高饱和色块压住内容
            Dim BarBrush As New LinearGradientBrush With {.EndPoint = New Point(0, 1), .StartPoint = New Point(0, 0)}
            BarBrush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(250, New MyColor().FromHSL2(ColorHue, ColorSat * 0.35, 98))})
            BarBrush.GradientStops.Add(New GradientStop With {.Offset = 0.55, .Color = New MyColor(242, New MyColor().FromHSL2(ColorHue, ColorSat * 0.5, 96))})
            BarBrush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(232, New MyColor().FromHSL2(ColorHue, ColorSat * 0.65, 93))})
            FrmMain.PanTitle.Background = BarBrush
            FrmMain.PanTitle.Background.Freeze()
            '主区域：底色 + 多层柔光光斑，给半透明玻璃提供可折射的层次
            FrmMain.PanForm.Background = BuildMeshBackground()
            '侧栏玻璃色
            Application.Current.Resources("ColorBrushGlassSidebar") = New SolidColorBrush(New MyColor(122, New MyColor().FromHSL2(ColorHue, ColorSat * 0.22, 99)))
        End Sub)
    End Sub

    ''' <summary>
    ''' 生成「极光」式背景：一层柔和的底色，叠加多个相对坐标的径向光斑。
    ''' 相比单条线性渐变，这种多层柔光能让上方的半透明玻璃卡片呈现出明显的折射与层次。
    ''' </summary>
    Private Function BuildMeshBackground() As DrawingBrush
        Dim Group As New DrawingGroup
        '底色
        Dim Base As New LinearGradientBrush With {.StartPoint = New Point(0, 0), .EndPoint = New Point(1, 1)}
        If Settings.Get(Of Boolean)("UiBackgroundColorful") Then
            Base.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor().FromHSL2(ColorHue - 8, ColorSat * 0.72, 95)})
            Base.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor().FromHSL2(ColorHue + 4, ColorSat * 0.5, 93)})
            Base.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor().FromHSL2(ColorHue + 22, ColorSat * 0.78, 95)})
        Else
            Base.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(247, 248, 250)})
            Base.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(236, 238, 242)})
        End If
        Group.Children.Add(New GeometryDrawing(Base, Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
        '柔光光斑
        If Settings.Get(Of Boolean)("UiBackgroundColorful") Then
            Group.Children.Add(New GeometryDrawing(BlobBrush(0.04, -0.02, 1.05, ColorHue - 18, ColorSat * 0.85, 84, 190), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
            Group.Children.Add(New GeometryDrawing(BlobBrush(0.92, 0.06, 0.95, ColorHue + 42, ColorSat * 0.7, 86, 148), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
            Group.Children.Add(New GeometryDrawing(BlobBrush(0.55, 1.08, 1.0, ColorHue + 12, ColorSat * 0.6, 88, 116), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
        Else
            Group.Children.Add(New GeometryDrawing(BlobBrush(0.08, -0.05, 1.0, ColorHue, 0, 92, 130), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
            Group.Children.Add(New GeometryDrawing(BlobBrush(0.95, 1.0, 0.95, ColorHue, 0, 88, 96), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
        End If
        Dim Brush As New DrawingBrush(Group) With {
            .Stretch = Stretch.Fill,
            .Viewbox = New Rect(0, 0, 1, 1),
            .Viewport = New Rect(0, 0, 1, 1),
            .ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            .TileMode = TileMode.None}
        Brush.Freeze()
        Return Brush
    End Function

    ''' <summary>
    ''' 单个柔光光斑。
    ''' </summary>
    Private Function BlobBrush(CenterX As Double, CenterY As Double, Radius As Double,
                               Hue As Integer, Sat As Double, Light As Integer, Alpha As Integer) As RadialGradientBrush
        Dim Color As MyColor = New MyColor().FromHSL2(Hue, Sat, Light)
        Dim Brush As New RadialGradientBrush With {
            .MappingMode = BrushMappingMode.RelativeToBoundingBox,
            .Center = New Point(CenterX, CenterY),
            .GradientOrigin = New Point(CenterX, CenterY),
            .RadiusX = Radius, .RadiusY = Radius}
        Brush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(Alpha, Color)})
        Brush.GradientStops.Add(New GradientStop With {.Offset = 0.55, .Color = New MyColor(CInt(Alpha * 0.42), Color)})
        Brush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(0, Color)})
        Return Brush
    End Function
#End Region

#Region "更新"

    ''' <summary>
    ''' 确保 PathTemp/Latest.exe 是最新正式版的 RMCL，它会被用于整合包打包。
    ''' 如果不是，则下载一个。
    ''' </summary>
    Friend Sub DownloadLatestRMCL(Optional LoaderToSyncProgress As LoaderBase = Nothing)
        '注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
    End Sub

#End Region

#Region "联网配置"

    ''' <summary>
    ''' 联网获取的配置信息。
    ''' 若获取失败或仍在获取中，可能为 Nothing。
    ''' </summary>
    Public ServerConfig As JObject

    Public ServerLoader As New LoaderTask(Of Integer, Integer)("RMCL 配置更新", Sub() Logger.Info("该版本中不包含更新通知功能……"), Priority:=ThreadPriority.BelowNormal) With
        {.ReloadTimeout = 1000 * 60 * 60} '超时 1 小时

#End Region

#Region "版本标识"

    Public ReadOnly Property CurrentRank As DonationRank
        Get
            Return DonationRank.None
        End Get
    End Property

    ''' <summary>
    ''' 获取设备识别码。
    ''' </summary>
    Friend Function GetIdentify() As String
        Return "0000-0000-0000-0000"
    End Function

#End Region

End Module
