Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.VisualBasic.FileIO
Imports Newtonsoft.Json.Linq

''' <summary>
''' 实例目录下资源包 / 光影包的本地文件模型。
'''
''' 一个条目 = 磁盘上的一个 zip 或一个文件夹。禁用的条目磁盘名会多一个 .disabled，
''' RawPath 始终是不带该后缀的路径，作为条目的唯一键（切换启用状态时不变）。
''' </summary>
Friend Module ModLocalComp

    Friend Enum CompType
        ResourcePack = 0
        Shader = 1
    End Enum

    ''' <summary>条目状态。Unavailable 指路径存在但读不出来（权限 / 占用）。</summary>
    Friend Enum FileState
        Fine = 0
        Disabled = 1
        Unavailable = 2
    End Enum

    Friend Class LocalCompFile
        ''' <summary>磁盘上的真实路径，禁用的带 .disabled。</summary>
        Public Path As String = ""
        ''' <summary>去掉 .disabled 的路径，唯一键。</summary>
        Public RawPath As String = ""
        Public Name As String = ""
        ''' <summary>展示用名称：资源包优先取 pack.mcmeta 里的名字，否则用文件名。</summary>
        Public Title As String = ""
        Public Description As String = ""
        Public State As FileState = FileState.Fine
        Public IsFolder As Boolean = False
        Public SizeBytes As Long = 0
        Public IconPath As String = ""
        Public AddedTime As Date = Date.MinValue
        ''' <summary>是否为文件夹（排序时置顶）。</summary>
        Public ReadOnly Property Enabled As Boolean
            Get
                Return State = FileState.Fine
            End Get
        End Property
    End Class

#Region "路径"

    Friend Function GetPathName(Type As CompType) As String
        Select Case Type
            Case CompType.ResourcePack
                Return "resourcepacks"
            Case CompType.Shader
                Return "shaderpacks"
            Case Else
                Return ""
        End Select
    End Function

    Friend Function GetTypeName(Type As CompType) As String
        Select Case Type
            Case CompType.ResourcePack
                Return "资源包"
            Case CompType.Shader
                Return "光影包"
            Case Else
                Return "资源"
        End Select
    End Function

    ''' <summary>资源目录，带结尾反斜杠。</summary>
    Friend Function GetRoot(Instance As McInstance, Type As CompType) As String
        If Instance Is Nothing Then Return ""
        Return Instance.PathIndie & GetPathName(Type) & "\"
    End Function

#End Region

#Region "扫描"

    Friend Function LoadFiles(Instance As McInstance, Type As CompType) As List(Of LocalCompFile)
        Dim Result As New List(Of LocalCompFile)
        Try
            Dim Root As String = GetRoot(Instance, Type)
            If Not Directory.Exists(Root) Then Return Result

            For Each FolderPath As String In Directory.GetDirectories(Root)
                Dim Name As String = Path.GetFileName(FolderPath)
                If Name.StartsWith(".") Then Continue For

                Dim Disabled As Boolean = Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                Dim Raw As String = If(Disabled, FolderPath.Substring(0, FolderPath.Length - ".disabled".Length), FolderPath)
                Dim Entry As New LocalCompFile With {
                    .Path = FolderPath,
                    .RawPath = Raw,
                    .Name = Path.GetFileName(Raw),
                    .Title = Path.GetFileName(Raw),
                    .State = If(Disabled, FileState.Disabled, FileState.Fine),
                    .IsFolder = True,
                    .SizeBytes = GetFolderSize(FolderPath),
                    .AddedTime = GetCreatedTime(FolderPath)
                }
                If Type = CompType.ResourcePack Then ReadPackMeta(Entry)
                Result.Add(Entry)
            Next

            For Each FilePath As String In Directory.GetFiles(Root)
                Dim Full As String = FilePath
                Dim Ext As String = Path.GetExtension(Full).ToLowerInvariant()
                Dim Disabled As Boolean = False
                Dim Raw As String = Full

                If Ext = ".disabled" Then
                    Raw = Full.Substring(0, Full.Length - ".disabled".Length)
                    If Path.GetExtension(Raw).ToLowerInvariant() <> ".zip" Then Continue For
                    Disabled = True
                ElseIf Ext <> ".zip" Then
                    Continue For
                End If

                Dim Entry As New LocalCompFile With {
                    .Path = Full,
                    .RawPath = Raw,
                    .Name = Path.GetFileName(Raw),
                    .Title = Path.GetFileNameWithoutExtension(Raw),
                    .State = If(Disabled, FileState.Disabled, FileState.Fine),
                    .IsFolder = False,
                    .AddedTime = GetCreatedTime(Full)
                }
                Try
                    Entry.SizeBytes = New FileInfo(Full).Length
                Catch ex As Exception
                    Entry.State = FileState.Unavailable
                End Try
                If Type = CompType.ResourcePack Then ReadPackMeta(Entry)
                Result.Add(Entry)
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"扫描{GetTypeName(Type)}目录失败")
        End Try
        Return Result
    End Function

    Private Function GetFolderSize(Folder As String) As Long
        Try
            Dim Total As Long = 0
            For Each FilePath As String In Directory.GetFiles(Folder, "*", IO.SearchOption.AllDirectories)
                Try
                    Total += New FileInfo(FilePath).Length
                Catch
                    '单个文件读不到就跳过
                End Try
            Next
            Return Total
        Catch ex As Exception
            Return 0
        End Try
    End Function

    Private Function GetCreatedTime(Target As String) As Date
        Try
            If Directory.Exists(Target) Then Return Directory.GetCreationTime(Target)
            Return IO.File.GetCreationTime(Target)
        Catch ex As Exception
            Return Date.MinValue
        End Try
    End Function

#End Region

#Region "元数据与图标"

    ''' <summary>读 pack.mcmeta，取资源包自带的名字与描述。只解这一个条目，不解压整包。</summary>
    Private Sub ReadPackMeta(Entry As LocalCompFile)
        Try
            Dim Json As String = ""
            If Entry.IsFolder Then
                Dim MetaPath As String = Entry.Path.TrimEnd("\"c) & "\pack.mcmeta"
                If IO.File.Exists(MetaPath) Then Json = IO.File.ReadAllText(MetaPath, Encoding.UTF8)
            Else
                Using Stream As New FileStream(Entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Using Zip As New ZipArchive(Stream, ZipArchiveMode.Read)
                        Dim Item As ZipArchiveEntry = Zip.GetEntry("pack.mcmeta")
                        If Item IsNot Nothing AndAlso Item.Length <= 64 * 1024 Then
                            Using Reader As New StreamReader(Item.Open(), Encoding.UTF8)
                                Json = Reader.ReadToEnd()
                            End Using
                        End If
                    End Using
                End Using
            End If
            If String.IsNullOrWhiteSpace(Json) Then Return

            Dim Root As JObject = Json.DeserializeJson()
            Dim Pack As JObject = TryCast(Root("pack"), JObject)
            If Pack Is Nothing Then Return

            Dim Desc As JToken = Pack("description")
            Entry.Description = PlainText(Desc)

            '资源包本身没有名字字段，描述第一行常常就是名字，这里只做空的兜底
            If String.IsNullOrEmpty(Entry.Title) Then Entry.Title = Entry.Name
        Catch ex As Exception
            '单个包元数据坏了不影响整个列表
            Logger.Trace($"读取资源包元数据失败：{Entry.Path}（{ex.Message}）")
        End Try
    End Sub

    ''' <summary>
    ''' 把 description 转成能直接显示的纯文本：展开 JSON 文本组件的 text / extra / translate，
    ''' 并去掉 § 格式码（资源包里常见「§8xxx」「§#RRGGBB」）。
    ''' </summary>
    Friend Function PlainText(Token As JToken) As String
        If Token Is Nothing Then Return ""
        Dim Raw As String

        Select Case Token.Type
            Case JTokenType.String
                Raw = Token.ToString()
            Case JTokenType.Array
                Dim Parts As New List(Of String)
                For Each Item As JToken In CType(Token, JArray)
                    Parts.Add(PlainText(Item))
                Next
                Raw = String.Join("", Parts)
            Case JTokenType.Object
                Dim Obj As JObject = CType(Token, JObject)
                Dim Parts As New List(Of String)
                Dim Translate As String = If(Obj("translate")?.ToString, "")
                If Translate <> "" Then Parts.Add(If(Obj("fallback")?.ToString, Translate))
                Dim Text As String = If(Obj("text")?.ToString, "")
                If Text <> "" Then Parts.Add(Text)
                Dim Extra As JToken = Obj("extra")
                If Extra IsNot Nothing AndAlso Extra.Type = JTokenType.Array Then
                    For Each Item As JToken In CType(Extra, JArray)
                        Parts.Add(PlainText(Item))
                    Next
                End If
                Raw = String.Join("", Parts)
            Case Else
                Raw = Token.ToString()
        End Select

        Dim Builder As New StringBuilder(Raw.Length)
        Dim Index As Integer = 0
        While Index < Raw.Length
            If Raw(Index) = "§"c Then
                Index += 2 '§ 后面跟一个字符构成格式码
            Else
                Builder.Append(Raw(Index))
                Index += 1
            End If
        End While

        Dim Clean As String = Builder.ToString().Trim()
        If Clean.Length > 160 Then Clean = Clean.Substring(0, 160) & "…"
        Return Clean
    End Function

    ''' <summary>取包内的 pack.png 作为列表图标，缓存到临时目录，文件名带修改时间以便失效。</summary>
    Private Function ExtractIcon(Entry As LocalCompFile) As String
        Try
            Dim Stamp As Long
            If Entry.IsFolder Then
                If Not Directory.Exists(Entry.Path) Then Return ""
                Stamp = Directory.GetLastWriteTimeUtc(Entry.Path).Ticks
            Else
                If Not IO.File.Exists(Entry.Path) Then Return ""
                Stamp = IO.File.GetLastWriteTimeUtc(Entry.Path).Ticks
            End If

            Dim CacheFolder As String = Path.Combine(PathTemp, "PackIcon")
            Directory.CreateDirectory(CacheFolder)

            Dim Key As String
            Using Sha As SHA256 = SHA256.Create()
                Key = BitConverter.ToString(Sha.ComputeHash(Encoding.UTF8.GetBytes(Entry.RawPath.ToLowerInvariant()))).
                        Replace("-", "").Substring(0, 16).ToLowerInvariant()
            End Using
            Dim CachePath As String = Path.Combine(CacheFolder, Key & "_" & Stamp & ".png")
            If IO.File.Exists(CachePath) Then Return CachePath

            Dim Data As Byte() = Nothing
            If Entry.IsFolder Then
                Dim IconFile As String = Path.Combine(Entry.Path, "pack.png")
                If IO.File.Exists(IconFile) Then
                    Dim Info As New FileInfo(IconFile)
                    If Info.Length > 0 AndAlso Info.Length <= 1024 * 1024 Then Data = IO.File.ReadAllBytes(IconFile)
                End If
            Else
                Using Stream As New FileStream(Entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Using Zip As New ZipArchive(Stream, ZipArchiveMode.Read)
                        Dim Item As ZipArchiveEntry = Zip.GetEntry("pack.png")
                        If Item IsNot Nothing AndAlso Item.Length > 0 AndAlso Item.Length <= 1024 * 1024 Then
                            Using ItemStream As Stream = Item.Open()
                                Using Buffer As New MemoryStream()
                                    ItemStream.CopyTo(Buffer)
                                    Data = Buffer.ToArray()
                                End Using
                            End Using
                        End If
                    End Using
                End Using
            End If

            If Data Is Nothing Then Return ""
            IO.File.WriteAllBytes(CachePath, Data)
            Return CachePath
        Catch ex As Exception
            Logger.Trace($"读取资源包图标失败：{Entry.Path}（{ex.Message}）")
            Return ""
        End Try
    End Function

    ''' <summary>列表要用的图标：资源包取 pack.png，光影包没有图标，返回空串交给控件用默认图。</summary>
    Friend Function GetIcon(Entry As LocalCompFile, Type As CompType) As String
        If Entry Is Nothing Then Return ""
        If Type <> CompType.ResourcePack Then Return ""
        If Entry.IconPath = "" Then Entry.IconPath = ExtractIcon(Entry)
        Return Entry.IconPath
    End Function

#End Region

#Region "启用 / 删除"

    ''' <summary>切换启用状态：改文件名后缀，这是游戏本身认的方式。</summary>
    Friend Function SetFileEnabled(Entry As LocalCompFile, Enabled As Boolean) As Boolean
        If Entry Is Nothing Then Return False
        Try
            Dim Target As String = If(Enabled, Entry.RawPath, Entry.RawPath & ".disabled")
            If String.Equals(Entry.Path, Target, StringComparison.OrdinalIgnoreCase) Then
                Entry.State = If(Enabled, FileState.Fine, FileState.Disabled)
                Return True
            End If

            If Entry.IsFolder Then
                If Directory.Exists(Entry.Path) Then Directory.Move(Entry.Path, Target)
            Else
                If IO.File.Exists(Entry.Path) Then IO.File.Move(Entry.Path, Target)
            End If

            Entry.Path = Target
            Entry.State = If(Enabled, FileState.Fine, FileState.Disabled)
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"切换资源启用状态失败：{Entry.RawPath}")
            Return False
        End Try
    End Function

    ''' <summary>删除条目，走回收站。</summary>
    Friend Function DeleteEntry(Entry As LocalCompFile) As Boolean
        If Entry Is Nothing Then Return False
        Try
            If Entry.IsFolder Then
                If Directory.Exists(Entry.Path) Then
                    FileSystem.DeleteDirectory(Entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)
                End If
            Else
                If IO.File.Exists(Entry.Path) Then
                    FileSystem.DeleteFile(Entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)
                End If
            End If
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"删除资源失败：{Entry.Path}")
            Return False
        End Try
    End Function

#End Region

End Module
