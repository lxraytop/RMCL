'由于包含加解密等安全信息，本文件中的部分代码已被删除

Friend Module ModSecret

    '标注 RMCL 的不同分支，仅用于替换标记
    Public Const VersionBranchMain As String = "OpenSource"
    '在开源版的注册表与常规版的注册表隔离，以防数据冲突
    Public Const RegFolder As String = "RMCLDebug"
    '用于微软登录的 ClientId
    Public OAuthClientId As String = If(Environment.GetEnvironmentVariable("RMCL_MS_CLIENT_ID"), "ab25b229-f826-46db-b646-01035c3a3e52")
    'CurseForge API Key（资源搜索与整合包安装均依赖它）
    '优先读取环境变量 RMCL_CURSEFORGE_API_KEY，未设置时使用内置密钥
    Public CurseForgeAPIKey As String = If(Environment.GetEnvironmentVariable("RMCL_CURSEFORGE_API_KEY"), "$2a$10$wuAJuNZuted3NORVmpgUC.m8sI.pv1tOPKZyBgLFGjxFp/br0lZCC")

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
        'CurseForge 官方 API 与镜像（mod.mcimirror.top/curseforge）都需要携带 x-api-key
        If Not String.IsNullOrEmpty(CurseForgeAPIKey) AndAlso
           (Url.Contains("api.curseforge.com") OrElse Url.Contains("forgecdn.net") OrElse Url.Contains("mcimirror.top/curseforge")) Then
            Req.Headers.Add("x-api-key", CurseForgeAPIKey)
        End If
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
    ''' <summary>
    ''' 当前是否处于深色外观（由 UiColorMode 与系统设置共同决定）。
    ''' </summary>
    Public IsDark As Boolean = False
    ''' <summary>外观模式：跟随系统。</summary>
    Public Const ColorModeSystem As Integer = 0
    ''' <summary>外观模式：浅色。</summary>
    Public Const ColorModeLight As Integer = 1
    ''' <summary>外观模式：深色。</summary>
    Public Const ColorModeDark As Integer = 2
    Public ColorHue As Integer = 210, ColorSat As Integer = 85, ColorLightAdjust As Integer = 0, ColorHueTopbarDelta As OneOf(Of Integer, Integer()) = 0
    Public ThemeDontClick As Integer = 0

    ''' <summary>
    ''' 可用主题：编号 → {色相, 饱和度, 亮度偏移, 顶栏渐变差}。
    ''' 仅保留蓝、青、绿、黄、红五种预设颜色，其余由“自定义”提供。
    ''' </summary>
    Public ReadOnly ThemePresets As New Dictionary(Of Integer, Integer()) From {
        {0, New Integer() {210, 85, 0, 0}},    ' 蓝
        {1, New Integer() {186, 76, 0, -26}}, ' 青
        {2, New Integer() {142, 62, 0, 22}},  ' 绿
        {3, New Integer() {45, 82, 0, 26}},   ' 黄
        {10, New Integer() {356, 74, 0, -20}} ' 红
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
            '先确定浅色 / 深色外观，后面的整套调色板都依赖它
            RefreshColorMode()
            '灰色阶要在计算 ColorBg0 之前就位（其中用到了 ColorGray4）
            AssignGrayScale()

            '解析主题参数
            If ThemeNow = ThemeCustom Then
                '自定义：直接读取 HSL 滑块
                ColorHue = ClampInt(Settings.Get(Of Integer)("UiLauncherHue"), 0, 360)
                ColorSat = ClampInt(Settings.Get(Of Integer)("UiLauncherSat"), 0, 100)
                ColorLightAdjust = ClampInt(Settings.Get(Of Integer)("UiLauncherLight"), 0, 40) - 20
                '色调渐变滑条以 90 为中点（90 表示无渐变），这里换算成以 0 为中心的色相偏移量
                ColorHueTopbarDelta = CInt((ClampInt(Settings.Get(Of Integer)("UiLauncherDelta"), 0, 180) - 90) * 0.7)
            Else
                Dim Preset As Integer() = Nothing
                If Not ThemePresets.TryGetValue(ThemeNow, Preset) Then Preset = ThemePresets(0) '未知主题回退为蓝色
                ColorHue = Preset(0)
                ColorSat = Preset(1)
                ColorLightAdjust = Preset(2)
                ColorHueTopbarDelta = Preset(3)
            End If

            If IsDark Then
                '深色：文字取高明度，强调色整体提亮以保证在深底上的对比度，面板色取低明度的同色相灰
                Color1 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.12, 96)
                Color2 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.9, 74)
                Color3 = New MyColor().FromHSL2(ColorHue, ColorSat, 66)
                Color4 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.95, 59)
                Color5 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.6, 46)
                Color6 = New MyColor(236, New MyColor().FromHSL2(ColorHue, ColorSat * 0.18, 18))
                Color7 = New MyColor(216, New MyColor().FromHSL2(ColorHue, ColorSat * 0.16, 14))
                Color8 = New MyColor(190, New MyColor().FromHSL2(ColorHue, ColorSat * 0.14, 11))
                'ColorBg0 是「软强调色」，用作滑动条轨道、输入框与密码框的描边：
                '深色下必须给到中等明度，否则这些描边和轨道会黑成一片看不见
                ColorBg0 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.55, 60)
                ColorBg1 = New MyColor(190, Color7)
            Else
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
            End If

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
            '阴影色：浅色跟随主文字色，深色用纯黑，避免在深底上出现发光的白雾
            Application.Current.Resources("ColorObjectShadow") = CType(If(IsDark, New MyColor(0, 0, 0), Color1), Color)
            '强调色药丸（顶部导航选中态）与灰色阶、玻璃令牌
            Application.Current.Resources("ColorBrushHighlightPill") = New SolidColorBrush(New MyColor(If(IsDark, 76, 56), Color3))
            AssignGlassResources()
            ThemeRefreshMain()
        Catch ex As Exception
            Logger.Error(ex, "刷新主题颜色失败", LogBehavior.Toast)
        End Try
    End Sub
    ''' <summary>
    ''' 根据外观模式设置与系统设置，决定当前是否为深色外观。
    ''' </summary>
    Public Sub RefreshColorMode()
        Select Case Settings.Get(Of Integer)("UiColorMode")
            Case ColorModeLight
                IsDark = False
            Case ColorModeDark
                IsDark = True
            Case Else
                IsDark = IsSystemDark()
        End Select
    End Sub

    ''' <summary>
    ''' 读取系统「应用模式」是否为深色（Windows 10 1809+）。
    ''' </summary>
    Public Function IsSystemDark() As Boolean
        Try
            Using Key As Microsoft.Win32.RegistryKey =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                If Key Is Nothing Then Return False
                Dim Value As Object = Key.GetValue("AppsUseLightTheme")
                If Value Is Nothing Then Return False
                Return CInt(Value) = 0
            End Using
        Catch ex As Exception
            '读取失败时按浅色处理
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 按当前外观分配灰色阶。深色下整体反转，保证次要文字与分隔线在深底上依然可读。
    ''' </summary>
    Private Sub AssignGrayScale()
        Dim Levels As Integer() = If(IsDark,
            New Integer() {242, 195, 166, 138, 90, 58, 46, 36},
            New Integer() {64, 115, 140, 166, 204, 235, 240, 245})
        ColorGray1 = New MyColor(Levels(0), Levels(0), Levels(0))
        ColorGray2 = New MyColor(Levels(1), Levels(1), Levels(1))
        ColorGray3 = New MyColor(Levels(2), Levels(2), Levels(2))
        ColorGray4 = New MyColor(Levels(3), Levels(3), Levels(3))
        ColorGray5 = New MyColor(Levels(4), Levels(4), Levels(4))
        ColorGray6 = New MyColor(Levels(5), Levels(5), Levels(5))
        ColorGray7 = New MyColor(Levels(6), Levels(6), Levels(6))
        ColorGray8 = New MyColor(Levels(7), Levels(7), Levels(7))
        Dim Dict As ResourceDictionary = Application.Current.Resources
        Dict("ColorBrushGray1") = New SolidColorBrush(ColorGray1)
        Dict("ColorObjectGray1") = CType(ColorGray1, Color)
        Dict("ColorBrushGray2") = New SolidColorBrush(ColorGray2)
        Dict("ColorObjectGray2") = CType(ColorGray2, Color)
        Dict("ColorBrushGray3") = New SolidColorBrush(ColorGray3)
        Dict("ColorObjectGray3") = CType(ColorGray3, Color)
        Dict("ColorBrushGray4") = New SolidColorBrush(ColorGray4)
        Dict("ColorObjectGray4") = CType(ColorGray4, Color)
        Dict("ColorBrushGray5") = New SolidColorBrush(ColorGray5)
        Dict("ColorObjectGray5") = CType(ColorGray5, Color)
        Dict("ColorBrushGray6") = New SolidColorBrush(ColorGray6)
        Dict("ColorObjectGray6") = CType(ColorGray6, Color)
        Dict("ColorBrushGray7") = New SolidColorBrush(ColorGray7)
        Dict("ColorObjectGray7") = CType(ColorGray7, Color)
        Dict("ColorBrushGray8") = New SolidColorBrush(ColorGray8)
        Dict("ColorObjectGray8") = CType(ColorGray8, Color)
    End Sub

    ''' <summary>
    ''' 侧栏玻璃背景的实际 Alpha（0-255），由「侧边栏透明度」设置统一换算。
    ''' 主界面左上导航、启动页的账号与启动区、实例 / 资源列表都盖在同一块侧栏玻璃上，
    ''' 因此这三处只需要这一个开关。
    ''' </summary>
    Private Function SidebarGlassAlpha() As Integer
        Dim Opacity As Integer = Math.Min(1000, Math.Max(0, Settings.Get(Of Integer)("UiSidebarOpacity")))
        '线性换算：浅色与深色下「80%」都是同样的 80% 不透明度，便于对照
        Return Math.Min(255, CInt(Math.Round(Opacity / 1000 * 255)))
    End Function

    ''' <summary>
    ''' 按当前外观重新分配液态玻璃令牌。浅色为乳白玻璃，深色为同色相低明度玻璃。
    ''' </summary>
    Private Sub AssignGlassResources()
        Dim Dict As ResourceDictionary = Application.Current.Resources
        Dim SidebarAlpha As Integer = SidebarGlassAlpha()
        '菜单胶囊（启动 / 下载 / 设置）取顶栏渐变中点的平色，使渐变只作用在它两侧的顶栏上
        Dim TopDelta As Integer = 0
        If ColorHueTopbarDelta.IsT1() Then TopDelta = ColorHueTopbarDelta.AsT1()
        Dim Vivid As Double = ClampInt(Settings.Get(Of Integer)("UiLauncherGradient"), 0, 100) / 100.0
        Dim DockHue As Integer = ColorHue - 6 + CInt(Math.Round(TopDelta * (0.6 + 0.8 * Vivid) * 0.5, MidpointRounding.AwayFromZero))
        '卡片材质
        Dim Card As New LinearGradientBrush With {.StartPoint = New Point(0, 0), .EndPoint = New Point(0.25, 1)}
        '面板材质（侧栏、浮层）
        '边缘高光：左上强、右下弱，模拟受光面
        Dim Rim As New LinearGradientBrush With {.StartPoint = New Point(0, 0), .EndPoint = New Point(0.75, 1)}
        '表面光泽：卡片内部自上而下的镜面反光
        Dim Sheen As New LinearGradientBrush With {.StartPoint = New Point(0, 0), .EndPoint = New Point(0.15, 1)}
        If IsDark Then
            Card.GradientStops.Add(New GradientStop(New MyColor(232, New MyColor().FromHSL2(ColorHue, ColorSat * 0.16, 21)), 0))
            Card.GradientStops.Add(New GradientStop(New MyColor(219, New MyColor().FromHSL2(ColorHue, ColorSat * 0.15, 18)), 0.32))
            Card.GradientStops.Add(New GradientStop(New MyColor(203, New MyColor().FromHSL2(ColorHue, ColorSat * 0.14, 15)), 0.72))
            Card.GradientStops.Add(New GradientStop(New MyColor(188, New MyColor().FromHSL2(ColorHue, ColorSat * 0.14, 13)), 1))
            '深色下卡片要干净：边缘只留一圈几乎看不见的弱白线，顶部那片镜面反光直接去掉，
            '否则卡片左上角会泛出一团白雾，跟深色底对不上
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(20, 255, 255, 255), 0))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(9, 255, 255, 255), 0.28))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(5, 255, 255, 255), 0.72))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(3, 255, 255, 255), 1))
            Sheen.GradientStops.Add(New GradientStop(Color.FromArgb(6, 255, 255, 255), 0))
            Sheen.GradientStops.Add(New GradientStop(Color.FromArgb(2, 255, 255, 255), 0.4))
            Sheen.GradientStops.Add(New GradientStop(Color.FromArgb(0, 255, 255, 255), 0.72))
            Dict("ColorBrushGlassInner") = New SolidColorBrush(New MyColor(20, 255, 255, 255))
            Dict("ColorBrushGlassSidebar") = New SolidColorBrush(New MyColor(SidebarAlpha, New MyColor().FromHSL2(ColorHue, ColorSat * 0.16, 12)))
            Dict("ColorBrushGlassHairline") = New SolidColorBrush(New MyColor(28, 255, 255, 255))
            Dict("ColorBrushGlassDock") = New SolidColorBrush(New MyColor(226, New MyColor().FromHSL2(DockHue, ColorSat * 0.5, 16.5 + ColorLightAdjust * 0.25)))
            '深色下白色叠加要弱很多，否则勾选框、输入框会亮成一片
            Dict("ColorBrushHalfWhite") = New SolidColorBrush(New MyColor(24, 255, 255, 255))
            Dict("ColorBrushSemiWhite") = New SolidColorBrush(New MyColor(40, 255, 255, 255))
        Else
            Card.GradientStops.Add(New GradientStop(Color.FromArgb(237, 255, 255, 255), 0))
            Card.GradientStops.Add(New GradientStop(Color.FromArgb(222, 255, 255, 255), 0.32))
            Card.GradientStops.Add(New GradientStop(Color.FromArgb(203, 255, 255, 255), 0.72))
            Card.GradientStops.Add(New GradientStop(Color.FromArgb(188, 255, 255, 255), 1))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(216, 255, 255, 255), 0))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(140, 255, 255, 255), 0.28))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(61, 255, 255, 255), 0.72))
            Rim.GradientStops.Add(New GradientStop(Color.FromArgb(41, 255, 255, 255), 1))
            Sheen.GradientStops.Add(New GradientStop(Color.FromArgb(88, 255, 255, 255), 0))
            Sheen.GradientStops.Add(New GradientStop(Color.FromArgb(26, 255, 255, 255), 0.4))
            Sheen.GradientStops.Add(New GradientStop(Color.FromArgb(0, 255, 255, 255), 0.72))
            Dict("ColorBrushGlassInner") = New SolidColorBrush(New MyColor(92, 255, 255, 255))
            Dict("ColorBrushGlassSidebar") = New SolidColorBrush(New MyColor(SidebarAlpha, New MyColor().FromHSL2(ColorHue, ColorSat * 0.22, 99)))
            Dict("ColorBrushGlassHairline") = New SolidColorBrush(New MyColor(92, 255, 255, 255))
            Dict("ColorBrushGlassDock") = New SolidColorBrush(New MyColor(244, New MyColor().FromHSL2(DockHue, ColorSat * 0.55, Math.Min(98.5, 95.5 + ColorLightAdjust * 0.15))))
            Dict("ColorBrushHalfWhite") = New SolidColorBrush(New MyColor(85, 255, 255, 255))
            Dict("ColorBrushSemiWhite") = New SolidColorBrush(New MyColor(187, 255, 255, 255))
        End If
        Dict("ColorBrushGlassCard") = Card
        Dict("ColorBrushGlassRim") = Rim
        Dict("ColorBrushGlassSheen") = Sheen
        '弹窗（错误提示、输入框、选择框、登录）的底色与正文：深色下必须换成深底浅字，否则会是一块刺眼的白板
        Dict("ColorBrushDialogBackground") = New SolidColorBrush(If(IsDark,
            New MyColor(250, New MyColor().FromHSL2(ColorHue, ColorSat * 0.16, 17)),
            New MyColor(251, 251, 251)))
        Dict("ColorBrushDialogText") = New SolidColorBrush(If(IsDark,
            New MyColor(210, 255, 255, 255),
            New MyColor(92, 92, 92)))
    End Sub

    Public Sub ThemeRefreshMain()
        RunInUi(
        Sub()
            If Not FrmMain.IsLoaded Then Return
            '顶部条：直接取用个性化主题色（同样的色相 / 饱和度 / 亮度），
            '「色调渐变」滑条控制左右两端的色相偏移，「炫目程度」再乘上一层强度，
            '同时放大色相跨度、饱和度对比与明度落差，做出从左到右鲜明的色带。
            Dim TopDelta As Integer = 0
            If ColorHueTopbarDelta.IsT1() Then TopDelta = ColorHueTopbarDelta.AsT1()
            Dim Vivid As Double = ClampInt(Settings.Get(Of Integer)("UiLauncherGradient"), 0, 100) / 100.0
            '色相跨度：0% 时收窄，100% 时放大 1.4 倍
            Dim SpanDelta As Integer = CInt(Math.Round(TopDelta * (0.6 + 0.8 * Vivid), MidpointRounding.AwayFromZero))
            '两端饱和度：起点几乎不带色，终点一路推到满饱和
            Dim SatStart As Double = ColorSat * (0.35 + 0.25 * Vivid)
            Dim SatMid As Double = Math.Min(100, ColorSat * (0.55 + 0.35 * Vivid))
            Dim SatEnd As Double = Math.Min(100, ColorSat * (0.70 + 0.30 * Vivid))
            Dim BarBrush As New LinearGradientBrush With {.EndPoint = New Point(1, 0.6), .StartPoint = New Point(0, 0)}
            Dim BarLight As Double = Math.Min(99, 95.5 + ColorLightAdjust * 0.15)
            If IsDark Then
                BarBrush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(246, New MyColor().FromHSL2(ColorHue - 6, SatStart * 0.9, 19 + ColorLightAdjust * 0.25))})
                BarBrush.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor(242, New MyColor().FromHSL2(ColorHue - 6 + SpanDelta * 0.5, SatMid, 15 + ColorLightAdjust * 0.25 - 3 * Vivid))})
                BarBrush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(236, New MyColor().FromHSL2(ColorHue - 6 + SpanDelta, SatEnd, 11 + ColorLightAdjust * 0.25 - 3 * Vivid))})
            Else
                BarBrush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(252, New MyColor().FromHSL2(ColorHue - 6, SatStart, BarLight))})
                BarBrush.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor(252, New MyColor().FromHSL2(ColorHue - 6 + SpanDelta * 0.5, SatMid, Math.Max(58, BarLight - 1 - 6 * Vivid)))})
                BarBrush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(252, New MyColor().FromHSL2(ColorHue - 6 + SpanDelta, SatEnd, Math.Max(58, BarLight - 2 - 12 * Vivid)))})
            End If
            FrmMain.PanTitle.Background = BarBrush
            FrmMain.PanTitle.Background.Freeze()
            '主区域：底色 + 多层柔光光斑，给半透明玻璃提供可折射的层次
            FrmMain.PanForm.Background = BuildMeshBackground()
            '侧栏玻璃色（与 AssignGlassResources 里的同一支画刷，保持两处一致）
            Application.Current.Resources("ColorBrushGlassSidebar") = New SolidColorBrush(New MyColor(SidebarGlassAlpha(), New MyColor().FromHSL2(ColorHue, ColorSat * If(IsDark, 0.16, 0.22), If(IsDark, 12, 99))))
            '单色图标只取决于浅色 / 深色：整棵可视化树遍历 + 每个按钮重启动画很贵，
            '拖动颜色滑条时没必要每帧都做一遍，所以只在明暗真正切换时刷新
            If _LastMonoIconsDark Is Nothing OrElse _LastMonoIconsDark <> IsDark Then
                _LastMonoIconsDark = IsDark
                RefreshMonoIcons(FrmMain)
            End If
            '外观切换按钮同理，只在模式或明暗变化时重设 Logo（每次重设都会重新解析一遍路径）
            Dim TitleButtonState As String = Settings.Get(Of Integer)("UiColorMode") & "|" & IsDark
            If _LastTitleButtonState <> TitleButtonState Then
                _LastTitleButtonState = TitleButtonState
                FrmMain.RefreshTitleThemeButton()
            End If
        End Sub)
    End Sub

    '上一次刷新单色图标 / 外观按钮时的状态，用于跳过重复的整树遍历
    Private _LastMonoIconsDark As Boolean? = Nothing
    Private _LastTitleButtonState As String = Nothing

    ''' <summary>
    ''' 递归刷新所有单色（Theme=Black）图标按钮的颜色，使其跟随当前外观。
    ''' 深色外观下这些图标必须变成浅色，否则会与深色顶栏、深色卡片糊成一团。
    ''' </summary>
    Private Sub RefreshMonoIcons(Root As DependencyObject)
        If Root Is Nothing Then Return
        Try
            Dim IconButton As MyIconButton = TryCast(Root, MyIconButton)
            If IconButton IsNot Nothing AndAlso IconButton.Theme = MyIconButton.Themes.Black Then IconButton.RefreshAnim()
            Dim Count As Integer = VisualTreeHelper.GetChildrenCount(Root)
            For i As Integer = 0 To Count - 1
                RefreshMonoIcons(VisualTreeHelper.GetChild(Root, i))
            Next
        Catch
            '遍历过程中元素可能已被卸载，忽略即可
        End Try
    End Sub

    ''' <summary>
    ''' 生成「极光」式背景：一层柔和的底色，叠加多个相对坐标的径向光斑。
    ''' 相比单条线性渐变，这种多层柔光能让上方的半透明玻璃卡片呈现出明显的折射与层次。
    ''' </summary>
    Private Function BuildMeshBackground() As DrawingBrush
        Dim Colorful As Boolean = Settings.Get(Of Boolean)("UiBackgroundColorful")
        '背景只跟外观、主题色相饱和度亮度、彩色开关有关：参数没变就直接复用上次的冻结画刷，
        '避免拖动颜色滑条时反复构建这一组渐变和几何图形
        Dim CacheKey As String = $"{IsDark}|{Colorful}|{ColorHue}|{ColorSat}|{ColorLightAdjust}"
        If _MeshCache IsNot Nothing AndAlso _MeshCacheKey = CacheKey Then Return _MeshCache
        Dim Group As New DrawingGroup
        '底色
        Dim Base As New LinearGradientBrush With {.StartPoint = New Point(0, 0), .EndPoint = New Point(1, 1)}
        If IsDark Then
            If Colorful Then
                Base.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor().FromHSL2(ColorHue - 8, ColorSat * 0.5, 14)})
                Base.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor().FromHSL2(ColorHue + 6, ColorSat * 0.38, 9)})
                Base.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor().FromHSL2(ColorHue + 24, ColorSat * 0.5, 12)})
            Else
                Base.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(24, 25, 28)})
                Base.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(13, 14, 16)})
            End If
        ElseIf Colorful Then
            Base.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor().FromHSL2(ColorHue - 8, ColorSat * 0.72, 95)})
            Base.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor().FromHSL2(ColorHue + 4, ColorSat * 0.5, 93)})
            Base.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor().FromHSL2(ColorHue + 22, ColorSat * 0.78, 95)})
        Else
            Base.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor(247, 248, 250)})
            Base.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor(236, 238, 242)})
        End If
        Group.Children.Add(New GeometryDrawing(Base, Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
        '柔光光斑
        If IsDark Then
            If Colorful Then
                '深色下光斑明度压低、饱和度提高，形成彩色辉光而不是糊成一片白
                Group.Children.Add(New GeometryDrawing(BlobBrush(0.04, -0.02, 1.05, ColorHue - 18, ColorSat * 0.75, 32, 150), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
                Group.Children.Add(New GeometryDrawing(BlobBrush(0.92, 0.06, 0.95, ColorHue + 42, ColorSat * 0.62, 28, 118), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
                Group.Children.Add(New GeometryDrawing(BlobBrush(0.55, 1.08, 1.0, ColorHue + 12, ColorSat * 0.52, 26, 92), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
            Else
                Group.Children.Add(New GeometryDrawing(BlobBrush(0.08, -0.05, 1.0, ColorHue, 0, 46, 46), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
                Group.Children.Add(New GeometryDrawing(BlobBrush(0.95, 1.0, 0.95, ColorHue, 0, 34, 34), Nothing, New RectangleGeometry(New Rect(0, 0, 1, 1))))
            End If
        ElseIf Colorful Then
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
        _MeshCache = Brush
        _MeshCacheKey = CacheKey
        Return Brush
    End Function

    '背景网格画刷的缓存，key 为影响它的全部参数
    Private _MeshCache As DrawingBrush = Nothing
    Private _MeshCacheKey As String = Nothing

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
