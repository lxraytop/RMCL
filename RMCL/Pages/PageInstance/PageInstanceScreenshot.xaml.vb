Imports System.IO
Imports System.Windows.Media.Imaging
Imports Microsoft.VisualBasic.FileIO

''' <summary>
''' 版本设置 · 截图管理。
'''
''' 卡片网格，每张图按 200px 解码后显示缩略图，滚动到底部再追加 20 张，
''' 所以不管目录里有几十张还是几百张，首屏都只处理 20 个元素。
''' </summary>
Public Class PageInstanceScreenshot
    Implements IRefreshable

    Private Const PageSize As Integer = 20
    Private ReadOnly AllowedSuffix As String() = {"*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tiff"}

    Private FileList As New List(Of String)
    Private Offset As Integer = 0
    Private AppendLock As Boolean = False
    Private Scanning As Boolean = False
    Private InstancePath As String = ""

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Reload()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Reload()
    End Sub

#Region "读取"

    Private Sub Reload()
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        If Scanning Then Return

        Scanning = True
        InstancePath = Instance.PathIndie
        Offset = 0
        AppendLock = False
        FileList.Clear()
        PanList.Children.Clear()

        Dim Root As String = Path.Combine(Instance.PathIndie, "screenshots")
        RunInNewThread(
        Sub()
            Dim Result As New List(Of String)
            Try
                If Not Directory.Exists(Root) Then Directory.CreateDirectory(Root)
                For Each Suffix As String In AllowedSuffix
                    Result.AddRange(Directory.EnumerateFiles(Root, Suffix, IO.SearchOption.TopDirectoryOnly))
                Next
                '新截的图排前面
                Result.Sort(Function(a, b) IO.File.GetCreationTime(b).CompareTo(IO.File.GetCreationTime(a)))
            Catch ex As Exception
                Logger.Warn(ex, "扫描截图目录失败")
            End Try

            RunInUi(Sub()
                        Scanning = False
                        If PageInstanceLeft.Instance Is Nothing OrElse PageInstanceLeft.Instance.PathIndie <> InstancePath Then Return
                        FileList = Result
                        If FileList.Count = 0 Then
                            PanContent.Visibility = Visibility.Collapsed
                            PanNoPic.Visibility = Visibility.Visible
                            Return
                        End If
                        PanContent.Visibility = Visibility.Visible
                        PanNoPic.Visibility = Visibility.Collapsed
                        AppendItems()
                    End Sub)
        End Sub, "读取截图列表")
    End Sub

    ''' <summary>滚动到底部时再追加一页。</summary>
    Private Sub RequireAppend(sender As Object, e As ScrollChangedEventArgs)
        If FileList.Count = 0 OrElse AppendLock Then Return
        If PanBack.VerticalOffset + PanBack.ViewportHeight >= PanBack.ExtentHeight - 40 Then
            AppendItems()
        End If
    End Sub

    Private Sub AppendItems()
        If AppendLock Then Return
        AppendLock = True
        Try
            Dim [End] As Integer = Math.Min(Offset + PageSize, FileList.Count)
            For Index As Integer = Offset To [End] - 1
                Dim Card As UIElement = BuildCard(FileList(Index))
                If Card IsNot Nothing Then PanList.Children.Add(Card)
            Next
            Offset = [End]
        Catch ex As Exception
            Logger.Warn(ex, "追加截图预览失败")
        Finally
            AppendLock = False
        End Try
    End Sub

    Private Function BuildCard(FilePath As String) As UIElement
        Try
            If Not IO.File.Exists(FilePath) Then Return Nothing
            Dim Info As New FileInfo(FilePath)
            If Info.Length = 0 Then Return Nothing
            If Info.Attributes.HasFlag(FileAttributes.Hidden) Then Return Nothing

            Dim Card As New MyCard With {
                .Width = 178,
                .Margin = New Thickness(7),
                .Tag = FilePath,
                .ToolTip = Path.GetFileName(FilePath)
            }

            Dim Grid As New Grid()
            Grid.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(8)})
            Grid.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(100)})
            Grid.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
            Card.Children.Add(Grid)

            Dim Image As New Image With {
                .Stretch = Stretch.Uniform,
                .Cursor = Cursors.Hand,
                .Source = Decode(FilePath)
            }
            AddHandler Image.MouseLeftButtonDown, Sub(s As Object, e As MouseButtonEventArgs) OpenShot(FilePath)
            Grid.SetRow(Image, 1)
            Grid.Children.Add(Image)

            Dim Panel As New StackPanel With {
                .Orientation = Orientation.Horizontal,
                .HorizontalAlignment = HorizontalAlignment.Center,
                .Margin = New Thickness(3, 4, 3, 4)
            }
            Grid.SetRow(Panel, 2)

            Dim BtnOpen As New MyIconButton With {.Logo = IconOpen, .Width = 22, .Height = 22, .Margin = New Thickness(3, 0, 3, 0), .ToolTip = "打开"}
            AddHandler BtnOpen.Click, Sub(s As Object, e As EventArgs) OpenShot(FilePath)
            Dim BtnCopy As New MyIconButton With {.Logo = IconCopy, .Width = 22, .Height = 22, .Margin = New Thickness(3, 0, 3, 0), .ToolTip = "复制到剪贴板"}
            AddHandler BtnCopy.Click, Sub(s As Object, e As EventArgs) CopyShot(FilePath)
            Dim BtnDelete As New MyIconButton With {.Logo = IconTrash, .Width = 22, .Height = 22, .Margin = New Thickness(3, 0, 3, 0), .ToolTip = "删除"}
            AddHandler BtnDelete.Click, Sub(s As Object, e As EventArgs) DeleteShot(FilePath)

            Panel.Children.Add(BtnOpen)
            Panel.Children.Add(BtnCopy)
            Panel.Children.Add(BtnDelete)
            Grid.Children.Add(Panel)
            Return Card
        Catch ex As Exception
            Logger.Warn(ex, $"生成截图预览失败：{FilePath}")
            Return Nothing
        End Try
    End Function

    ''' <summary>按 400×200 解码，避免把 4K 截图整张读进内存。</summary>
    Private Function Decode(FilePath As String) As BitmapImage
        Try
            Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim Bitmap As New BitmapImage()
                Bitmap.BeginInit()
                Bitmap.DecodePixelHeight = 200
                Bitmap.DecodePixelWidth = 400
                Bitmap.CacheOption = BitmapCacheOption.OnLoad
                Bitmap.StreamSource = Stream
                Bitmap.EndInit()
                Bitmap.Freeze()
                Return Bitmap
            End Using
        Catch ex As Exception
            '图片损坏就留空，不影响其它截图
            Logger.Trace($"截图解码失败：{FilePath}（{ex.Message}）")
            Return Nothing
        End Try
    End Function

#End Region

#Region "操作"

    Private Sub OpenShot(FilePath As String)
        Try
            Process.Start(New ProcessStartInfo(FilePath) With {.UseShellExecute = True})
        Catch ex As Exception
            Logger.Warn(ex, "打开截图失败")
            Hint("打开失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub CopyShot(FilePath As String)
        Try
            Dim Bitmap As BitmapImage = Decode(FilePath)
            If Bitmap Is Nothing Then
                Hint("这张图读不出来，可能已损坏", HintType.Red)
                Return
            End If
            Clipboard.SetImage(Bitmap)
            Hint("已复制到剪贴板", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, "复制截图失败")
            Hint("复制失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub DeleteShot(FilePath As String)
        If MyMsgBox($"确定删除这张截图吗？" & vbCrLf & vbCrLf & Path.GetFileName(FilePath) & vbCrLf & vbCrLf &
                    "文件会移到回收站，可以从那里还原。",
                    "删除截图", "删除", "取消", IsWarn:=True) <> 1 Then Return

        Try
            FileSystem.DeleteFile(FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)
            Hint("已把截图移到回收站", HintType.Green)
            Reload()
        Catch ex As Exception
            Logger.Warn(ex, "删除截图失败")
            Hint("删除失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnOpenFolder_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Instance As McInstance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            Dim Root As String = Path.Combine(Instance.PathIndie, "screenshots")
            Directory.CreateDirectory(Root)
            OpenExplorer(Root)
        Catch ex As Exception
            Logger.Warn(ex, "打开截图文件夹失败")
        End Try
    End Sub

#End Region

#Region "图标"

    Private Const IconOpen As String =
        "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"
    Private Const IconCopy As String =
        "M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"
    Private Const IconTrash As String =
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"

#End Region

End Class
