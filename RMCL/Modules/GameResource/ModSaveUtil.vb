Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.VisualBasic.FileIO

''' <summary>
''' 存档的扫描、备份、还原、复制与删除。
'''
''' 备份落在 exe 同级的 backup\saves\&lt;实例&gt;\&lt;存档名&gt;\时间戳.zip，
''' 直接打包整个存档目录，跳过游戏运行时会锁定的 session.lock。
''' </summary>
Friend Module ModSaveUtil

    ''' <summary>每个存档保留的备份份数。</summary>
    Friend Const KeepCount As Integer = 5

    Friend Class SaveEntry
        Public Folder As String = ""
        Public Name As String = ""
        Public LastPlayed As Date = Date.MinValue
        ''' <summary>0 生存 / 1 创造 / 2 冒险 / 3 旁观，未知为 -1。</summary>
        Public GameType As Integer = -1
        Public VersionName As String = ""
        Public SizeBytes As Long = 0
        Public BackupCount As Integer = 0
        ''' <summary>存档自带的预览图（icon.png），没有则为空。</summary>
        Public Logo As String = ""
    End Class

#Region "路径"

    Friend Function GetRoot(Instance As McInstance) As String
        If Instance Is Nothing Then Return ""
        Return Instance.PathIndie & "saves\"
    End Function

    ''' <summary>备份根目录：exe 同级的 backup\saves\。</summary>
    Friend Function BackupRoot() As String
        Return Paths.Base & "backup\saves\"
    End Function

    ''' <summary>某个实例在备份根目录下的子目录名：版本名 + 路径哈希，避免不同实例重名。</summary>
    Private Function InstanceKey(Instance As McInstance) As String
        Dim SafeName As String = If(String.IsNullOrEmpty(Instance.Name), "unknown", Instance.Name)
        For Each Ch As Char In New Char() {"\"c, "/"c, ":"c, "*"c, "?"c, """"c, "<"c, ">"c, "|"c}
            SafeName = SafeName.Replace(Ch, "_"c)
        Next
        If SafeName.Length > 40 Then SafeName = SafeName.Substring(0, 40)
        Using Sha As SHA256 = SHA256.Create()
            Dim Hash As Byte() = Sha.ComputeHash(Encoding.UTF8.GetBytes(Instance.PathIndie.ToLowerInvariant()))
            Return SafeName & "_" & BitConverter.ToString(Hash).Replace("-", "").Substring(0, 8).ToLowerInvariant()
        End Using
    End Function

    Friend Function BackupSaveFolder(Instance As McInstance, SaveName As String) As String
        Dim Safe As String = If(String.IsNullOrWhiteSpace(SaveName), "unknown", SaveName)
        For Each Ch As Char In New Char() {"\"c, "/"c, ":"c, "*"c, "?"c, """"c, "<"c, ">"c, "|"c}
            Safe = Safe.Replace(Ch, "_"c)
        Next
        Return BackupRoot() & InstanceKey(Instance) & "\" & Safe.Trim() & "\"
    End Function

#End Region

#Region "扫描"

    Friend Function LoadSaves(Instance As McInstance) As List(Of SaveEntry)
        Dim Result As New List(Of SaveEntry)
        Try
            Dim Root As String = GetRoot(Instance)
            If Not Directory.Exists(Root) Then Return Result

            For Each Folder As String In Directory.GetDirectories(Root)
                Dim Info As ModNbtReader.SaveInfo = ModNbtReader.Read(Folder.TrimEnd("\"c) & "\level.dat")
                Dim Name As String = Info.Name
                If String.IsNullOrEmpty(Name) Then Name = Path.GetFileName(Folder)

                Dim Icon As String = Folder.TrimEnd("\"c) & "\icon.png"
                Result.Add(New SaveEntry With {
                    .Folder = Folder,
                    .Name = Name,
                    .LastPlayed = Info.LastPlayed,
                    .GameType = Info.GameType,
                    .VersionName = Info.VersionName,
                    .SizeBytes = GetFolderSize(Folder),
                    .BackupCount = CountBackups(Instance, Name),
                    .Logo = If(IO.File.Exists(Icon), Icon, "")
                })
            Next
        Catch ex As Exception
            Logger.Warn(ex, "扫描存档列表失败")
        End Try
        Return Result
    End Function

    Friend Function GetFolderSize(Folder As String) As Long
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

    Friend Function GameTypeName(GameType As Integer) As String
        Select Case GameType
            Case 0
                Return "生存"
            Case 1
                Return "创造"
            Case 2
                Return "冒险"
            Case 3
                Return "旁观"
            Case Else
                Return "未知模式"
        End Select
    End Function

    ''' <summary>列表里显示的一行信息：上次游玩 · 模式 · 版本 · 大小。</summary>
    Friend Function BuildInfo(Entry As SaveEntry) As String
        Dim Played As String = If(Entry.LastPlayed = Date.MinValue, "从未进入", Entry.LastPlayed.ToString("yyyy-MM-dd HH:mm"))
        Dim Parts As New List(Of String) From {Played, GameTypeName(Entry.GameType)}
        If Not String.IsNullOrEmpty(Entry.VersionName) Then Parts.Add(Entry.VersionName)
        Parts.Add(FormatSize(Entry.SizeBytes))
        Return String.Join(" · ", Parts)
    End Function

#End Region

#Region "删除与复制"

    ''' <summary>
    ''' 判断存档是不是真的被占用（通常是游戏正在运行）。
    '''
    ''' 不能只看 session.lock 存不存在：游戏正常退出会删掉它，但崩溃、强杀进程、
    ''' 或者异常断电之后它会残留，导致明明没开游戏也弹「可能正在运行」。
    ''' 这里直接尝试以独占方式打开这个文件：能打开就说明没人占着。
    ''' </summary>
    Friend Function IsSaveInUse(Entry As SaveEntry) As Boolean
        If Entry Is Nothing OrElse String.IsNullOrEmpty(Entry.Folder) Then Return False
        Dim LockPath As String = Path.Combine(Entry.Folder, "session.lock")
        If Not IO.File.Exists(LockPath) Then Return False

        Try
            Using Stream As New FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                '能独占打开，说明只是残留文件
            End Using
            Return False
        Catch ex As IOException
            '被别的进程持有句柄
            Return True
        Catch ex As UnauthorizedAccessException
            '读不到也不冒进，按占用处理
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ''' <summary>删除存档目录，走回收站。</summary>
    Friend Function DeleteSave(Entry As SaveEntry) As Boolean
        If Entry Is Nothing Then Return False
        Try
            If Directory.Exists(Entry.Folder) Then
                FileSystem.DeleteDirectory(Entry.Folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)
            End If
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"删除存档失败：{Entry.Name}")
            Return False
        End Try
    End Function

    ''' <summary>把存档目录放进剪贴板，可以直接粘到别处。</summary>
    Friend Function CopySaveToClipboard(Entry As SaveEntry) As Boolean
        If Entry Is Nothing Then Return False
        Try
            Dim Files As New Collections.Specialized.StringCollection()
            Files.Add(Entry.Folder)
            Clipboard.SetFileDropList(Files)
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"复制存档失败：{Entry.Name}")
            Return False
        End Try
    End Function

    ''' <summary>把剪贴板里的文件夹复制进存档目录，返回成功个数。</summary>
    Friend Function PasteSaves(Instance As McInstance, Paths As Collections.Specialized.StringCollection) As Integer
        Dim Count As Integer = 0
        If Paths Is Nothing Then Return 0
        Try
            Dim Root As String = GetRoot(Instance)
            Directory.CreateDirectory(Root)
            For Each Source As String In Paths
                Try
                    If Not Directory.Exists(Source) Then Continue For
                    Dim Target As String = Root & Path.GetFileName(Source.TrimEnd("\"c))
                    If Directory.Exists(Target) Then
                        '重名时加序号，避免直接覆盖别人的存档
                        Dim Index As Integer = 2
                        While Directory.Exists(Target & "_" & Index)
                            Index += 1
                        End While
                        Target = Target & "_" & Index
                    End If
                    CopyFolder(Source, Target)
                    Count += 1
                Catch ex As Exception
                    Logger.Warn(ex, $"粘贴存档失败：{Source}")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, "粘贴存档失败")
        End Try
        Return Count
    End Function

    Private Sub CopyFolder(Source As String, Target As String)
        Directory.CreateDirectory(Target)
        For Each FilePath As String In Directory.GetFiles(Source)
            IO.File.Copy(FilePath, Path.Combine(Target, Path.GetFileName(FilePath)), True)
        Next
        For Each Folder As String In Directory.GetDirectories(Source)
            CopyFolder(Folder, Path.Combine(Target, Path.GetFileName(Folder)))
        Next
    End Sub

#End Region

#Region "备份与还原"

    Friend Function CountBackups(Instance As McInstance, SaveName As String) As Integer
        Try
            Dim Folder As String = BackupSaveFolder(Instance, SaveName)
            If Not Directory.Exists(Folder) Then Return 0
            Return Directory.GetFiles(Folder, "*.zip").Length
        Catch ex As Exception
            Return 0
        End Try
    End Function

    Friend Function ListBackups(Instance As McInstance, SaveName As String) As List(Of FileInfo)
        Dim Result As New List(Of FileInfo)
        Try
            Dim Folder As String = BackupSaveFolder(Instance, SaveName)
            If Not Directory.Exists(Folder) Then Return Result
            For Each FilePath As String In Directory.GetFiles(Folder, "*.zip")
                Result.Add(New FileInfo(FilePath))
            Next
            Result.Sort(Function(A, B) B.LastWriteTime.CompareTo(A.LastWriteTime))
        Catch ex As Exception
            Logger.Warn(ex, $"读取备份列表失败：{SaveName}")
        End Try
        Return Result
    End Function

    ''' <summary>备份一个存档，返回生成的 zip 路径。失败时抛出异常。</summary>
    Friend Function BackupSave(Instance As McInstance, Entry As SaveEntry) As String
        If Entry Is Nothing Then Throw New ArgumentNullException(NameOf(Entry))
        If Not Directory.Exists(Entry.Folder) Then Throw New Exception("存档目录不存在，可能已被移动或删除")

        Dim TargetFolder As String = BackupSaveFolder(Instance, Entry.Name)
        Directory.CreateDirectory(TargetFolder)

        Dim Stamp As String = DateTime.Now.ToString("yyyyMMdd_HHmmss")
        Dim Target As String = TargetFolder & Stamp & ".zip"
        Dim Counter As Integer = 1
        While IO.File.Exists(Target)
            Target = TargetFolder & Stamp & "_" & Counter & ".zip"
            Counter += 1
        End While

        Dim Base As String = Entry.Folder.TrimEnd("\"c) & "\"
        Using Output As New FileStream(Target, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            Using Zip As New ZipArchive(Output, ZipArchiveMode.Create)
                For Each FilePath As String In Directory.GetFiles(Base, "*", IO.SearchOption.AllDirectories)
                    Dim Relative As String = FilePath.Substring(Base.Length)
                    '游戏运行时会锁住这个文件，本身也没有备份价值
                    If Relative.Equals("session.lock", StringComparison.OrdinalIgnoreCase) Then Continue For
                    Dim Item As ZipArchiveEntry = Zip.CreateEntry(Relative.Replace("\"c, "/"c), CompressionLevel.Fastest)
                    Using ItemStream As Stream = Item.Open()
                        Using Source As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                            Source.CopyTo(ItemStream)
                        End Using
                    End Using
                Next
            End Using
        End Using

        Logger.Info($"已备份存档「{Entry.Name}」：{Target}")
        CleanupOldBackups(Instance, Entry.Name)
        Return Target
    End Function

    Private Sub CleanupOldBackups(Instance As McInstance, SaveName As String)
        Try
            Dim All As List(Of FileInfo) = ListBackups(Instance, SaveName)
            If All.Count <= KeepCount Then Return
            For i = KeepCount To All.Count - 1
                Try
                    IO.File.Delete(All(i).FullName)
                    Logger.Info($"已清理旧备份：{All(i).Name}")
                Catch ex As Exception
                    Logger.Warn(ex, $"清理旧备份失败：{All(i).FullName}")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, "清理旧备份失败")
        End Try
    End Sub

    ''' <summary>用备份覆盖当前存档。覆盖前会先把现状也备份一份。</summary>
    Friend Sub RestoreSave(Instance As McInstance, Entry As SaveEntry, ZipPath As String)
        If Entry Is Nothing Then Throw New ArgumentNullException(NameOf(Entry))
        If Not IO.File.Exists(ZipPath) Then Throw New Exception("备份文件不存在")
        If Not Directory.Exists(Entry.Folder) Then Throw New Exception("存档目录不存在")

        Dim Base As String = Entry.Folder.TrimEnd("\"c)

        '先把当前状态存一份，点错了还能找回来
        BackupSave(Instance, Entry)

        For Each FilePath As String In Directory.GetFiles(Base, "*", IO.SearchOption.AllDirectories)
            If Path.GetFileName(FilePath).Equals("session.lock", StringComparison.OrdinalIgnoreCase) Then Continue For
            Try
                IO.File.Delete(FilePath)
            Catch ex As Exception
                Logger.Warn(ex, $"还原前清理文件失败：{FilePath}")
            End Try
        Next

        Using Stream As New FileStream(ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Using Zip As New ZipArchive(Stream, ZipArchiveMode.Read)
                For Each Item As ZipArchiveEntry In Zip.Entries
                    If String.IsNullOrEmpty(Item.Name) Then Continue For '目录项
                    Dim TargetPath As String = Path.Combine(Base, Item.FullName.Replace("/"c, "\"c))
                    Directory.CreateDirectory(Path.GetDirectoryName(TargetPath))
                    Using ItemStream As Stream = Item.Open()
                        Using Output As New FileStream(TargetPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            ItemStream.CopyTo(Output)
                        End Using
                    End Using
                Next
            End Using
        End Using

        Logger.Info($"已从备份还原存档「{Entry.Name}」：{ZipPath}")
    End Sub

#End Region

End Module
