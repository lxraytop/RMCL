Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' 社区资源的前置（依赖项）解析与本地已安装检测。
''' </summary>
Public Module ResourceDependency

    ''' <summary>
    ''' 一个待处理的资源前置。
    ''' </summary>
    Public Class DependencyEntry

        ''' <summary>
        ''' 前置工程。
        ''' </summary>
        Public ReadOnly Project As ResourceProject
        ''' <summary>
        ''' 为当前游戏版本挑选出的文件。若为 Nothing 则没有可用的版本。
        ''' </summary>
        Public File As ResourceVersion = Nothing
        ''' <summary>
        ''' 距离目标资源的层级。直接前置为 1。
        ''' </summary>
        Public ReadOnly Depth As Integer
        ''' <summary>
        ''' 该前置是否已经安装在目标文件夹中。
        ''' </summary>
        Public IsInstalled As Boolean = False
        ''' <summary>
        ''' 是否默认勾选。
        ''' </summary>
        Public IsSelected As Boolean = True
        ''' <summary>
        ''' 无法下载的原因。若为 Nothing 则无异常。
        ''' </summary>
        Public FailReason As String = Nothing

        Public Sub New(Project As ResourceProject, Depth As Integer)
            Me.Project = Project
            Me.Depth = Depth
        End Sub

        ''' <summary>
        ''' 供列表项显示的描述文本。
        ''' </summary>
        Public ReadOnly Property Description As String
            Get
                Dim Parts As New List(Of String)
                If Depth > 1 Then Parts.Add($"第 {Depth} 层前置")
                If IsInstalled Then
                    Parts.Add("已安装")
                ElseIf File IsNot Nothing Then
                    Parts.Add(File.Display)
                    If File.ReleaseType <> ResourceVersion.ReleaseTypes.Release Then Parts.Add(File.ReleaseTypeDisplay)
                End If
                If File Is Nothing AndAlso FailReason IsNot Nothing Then Parts.Add(FailReason)
                Return Parts.Join("，")
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return $"{Project}: {Description}"
        End Function

    End Class

    ''' <summary>
    ''' 收集指定资源版本的全部前置（包含前置的前置），并尝试为每个前置挑选合适的文件。
    ''' </summary>
    ''' <param name="SourceProject">源工程。</param>
    ''' <param name="SourceFile">源文件。</param>
    ''' <param name="TargetMcVersion">目标 MC 版本名，用于筛选前置版本。可为空。</param>
    ''' <param name="TargetLoaders">目标 Mod 加载器，用于筛选前置版本。</param>
    ''' <param name="InstalledModKeys">目标 mods 文件夹中已安装的 Mod ID 集合，用于判断是否已安装。可为 Nothing。</param>
    Public Function Collect(SourceProject As ResourceProject, SourceFile As ResourceVersion,
                            TargetMcVersion As String, TargetLoaders As ModLoaders,
                            InstalledModKeys As HashSet(Of String)) As List(Of DependencyEntry)
        Dim Result As New List(Of DependencyEntry)
        If SourceProject Is Nothing OrElse SourceFile Is Nothing OrElse Not SourceFile.Dependencies.Any() Then Return Result
        Dim Visited As New HashSet(Of String) From {SourceProject.Id}
        Dim Queue As New Queue(Of KeyValuePair(Of String, Integer))
        For Each DependencyId As String In SourceFile.Dependencies
            If Visited.Add(DependencyId) Then Queue.Enqueue(New KeyValuePair(Of String, Integer)(DependencyId, 1))
        Next
        Do While Queue.Any()
            Dim Current = Queue.Dequeue()
            Dim DependencyProject As ResourceProject = Nothing
            Try
                DependencyProject = ResourceProject.FromProjectId(Current.Key, SourceProject.Platform)
            Catch ex As Exception
                Logger.Warn(ex, $"获取前置工程信息失败：{Current.Key}")
            End Try
            If DependencyProject Is Nothing Then Continue Do
            Dim Entry As New DependencyEntry(DependencyProject, Current.Value)
            Entry.IsInstalled = IsInstalled(DependencyProject, InstalledModKeys)
            Try
                Entry.File = GetBestFile(DependencyProject, TargetMcVersion, TargetLoaders)
            Catch ex As Exception
                Logger.Warn(ex, $"获取前置版本列表失败：{DependencyProject}")
                Entry.FailReason = "获取版本列表失败"
            End Try
            If Entry.File Is Nothing AndAlso Entry.FailReason Is Nothing Then
                Entry.FailReason = If(String.IsNullOrEmpty(TargetMcVersion), "没有可用的版本", $"没有适用于 {TargetMcVersion} 的版本")
            End If
            Entry.IsSelected = Not Entry.IsInstalled AndAlso Entry.File IsNot Nothing
            Result.Add(Entry)
            '递归解析前置的前置，最多两层，避免依赖链过长
            If Entry.File IsNot Nothing AndAlso Current.Value < 2 Then
                For Each SubDependencyId As String In Entry.File.Dependencies
                    If Visited.Add(SubDependencyId) Then Queue.Enqueue(New KeyValuePair(Of String, Integer)(SubDependencyId, Current.Value + 1))
                Next
            End If
        Loop
        Return Result
    End Function

    ''' <summary>
    ''' 为指定工程挑选最适合目标游戏版本与加载器的文件。
    ''' </summary>
    Public Function GetBestFile(Project As ResourceProject, TargetMcVersion As String, TargetLoaders As ModLoaders) As ResourceVersion
        Dim Files = ResourceVersion.FromProjectId(Project.Id, Project.Platform, LoadDependencies:=False).
            Where(Function(f) f.DownloadAvailable AndAlso Not f.ResourceType.HasFlagF(ResourceTypes.Plugin)).
            ToList()
        If Not Files.Any() Then Return Nothing
        '游戏版本筛选
        Dim Matched As List(Of ResourceVersion) = Nothing
        If Not String.IsNullOrEmpty(TargetMcVersion) Then
            Matched = Files.Where(Function(f) f.GameVersions.Contains(TargetMcVersion)).ToList()
            If Not Matched.Any() AndAlso McVersion.IsStandardFormat(TargetMcVersion) Then
                Dim TargetDrop = McVersion.VersionToDrop(TargetMcVersion)
                If TargetDrop <> 209 Then '209 表示无法识别的版本，此时按 Drop 匹配没有意义
                    Matched = Files.Where(Function(f) f.GameVersions.Any(Function(v) McVersion.VersionToDrop(v) = TargetDrop)).ToList()
                End If
            End If
        End If
        If Matched Is Nothing OrElse Not Matched.Any() Then Matched = Files
        '加载器筛选；前置自身可能是不限定加载器的库
        If TargetLoaders <> ModLoaders.None Then
            Dim LoaderMatched = Matched.Where(Function(f) f.ModLoaders = ModLoaders.None OrElse
                                                          (f.ModLoaders And TargetLoaders) <> ModLoaders.None).ToList()
            If LoaderMatched.Any() Then Matched = LoaderMatched
        End If
        '优先正式版，其次最新的文件
        Return Matched.OrderByDescending(Function(f) If(f.ReleaseType = ResourceVersion.ReleaseTypes.Release, 1, 0)).
            ThenByDescending(Function(f) f.ReleaseDate).FirstOrDefault()
    End Function

    ''' <summary>
    ''' 获取指定 mods 文件夹中所有已安装 Mod 的 ID 集合。结果已归一化。
    ''' </summary>
    Public Function GetInstalledModKeys(ModsFolder As String) As HashSet(Of String)
        Dim Result As New HashSet(Of String)
        If String.IsNullOrWhiteSpace(ModsFolder) OrElse Not DirectoryUtils.Exists(ModsFolder) Then Return Result
        Try
            For Each FilePath As String In DirectoryUtils.EnumerateFiles(ModsFolder)
                If Not IsModFile(FilePath) Then Continue For
                For Each ModId As String In GetModIds(FilePath)
                    Dim Key As String = NormalizeKey(ModId)
                    If Key.Length >= 3 Then Result.Add(Key)
                Next
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"读取本地 Mod 列表失败：{ModsFolder}")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 检查某个工程是否已安装在目标文件夹中。
    ''' </summary>
    Public Function IsInstalled(Project As ResourceProject, InstalledModKeys As HashSet(Of String)) As Boolean
        If Project Is Nothing OrElse InstalledModKeys Is Nothing OrElse Not InstalledModKeys.Any() Then Return False
        For Each Candidate In {Project.Slug, Project.RawName, Project.Id}
            Dim Key As String = NormalizeKey(Candidate)
            If Key.Length < 3 Then Continue For
            If InstalledModKeys.Contains(Key) Then Return True
            '允许长度接近的前缀匹配，例如 architecturyapi 与 architectury
            If Key.Length < 4 Then Continue For
            For Each InstalledKey As String In InstalledModKeys
                If InstalledKey.Length < 4 Then Continue For
                If InstalledKey.StartsWithF(Key) AndAlso InstalledKey.Length - Key.Length <= 3 Then Return True
                If Key.StartsWithF(InstalledKey) AndAlso Key.Length - InstalledKey.Length <= 3 Then Return True
            Next
        Next
        Return False
    End Function

    ''' <summary>
    ''' 查找 mods 文件夹所属的游戏实例。若不属于任何实例则为 Nothing。
    ''' </summary>
    Public Function GetInstanceOfModsFolder(ModsFolder As String) As McInstance
        If String.IsNullOrWhiteSpace(ModsFolder) Then Return Nothing
        Try
            Dim Target As String = PathUtils.RemoveLastPart(ModsFolder)
            For Each Instance As McInstance In McInstanceList.Values.SelectMany(Function(l) l)
                If PathUtils.IsSame(Target, PathUtils.RemoveLastPart(Instance.PathIndie & "mods\")) Then Return Instance
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"查找 Mod 文件夹所属实例失败：{ModsFolder}")
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 将文本归一化，仅保留小写字母与数字。
    ''' </summary>
    Public Function NormalizeKey(Text As String) As String
        If String.IsNullOrEmpty(Text) Then Return ""
        Dim Builder As New StringBuilder(Text.Length)
        For Each Character As Char In Text
            If Char.IsLetterOrDigit(Character) Then Builder.Append(Char.ToLowerInvariant(Character))
        Next
        Return Builder.ToString()
    End Function

#Region "本地 Mod 信息读取"

    ''' <summary>
    ''' 是否为可读取的 Mod 文件（包含已被禁用的文件）。
    ''' </summary>
    Private Function IsModFile(FilePath As String) As Boolean
        Dim Extension As String = PathUtils.GetExtension(FilePath).Lower
        If Extension = "jar" OrElse Extension = "litemod" Then Return True
        Return Extension = "disabled" AndAlso (FilePath.Lower.EndsWithF(".jar.disabled") OrElse FilePath.Lower.EndsWithF(".litemod.disabled"))
    End Function

    ''' <summary>
    ''' 单个 Mod 文件的元数据。包含 jar 内嵌（Jar-in-Jar）的内容。
    ''' </summary>
    Public Class ModMetadata
        ''' <summary>
        ''' 本 Mod 及内嵌 Mod 的 ID。
        ''' </summary>
        Public Ids As New List(Of String)
        ''' <summary>
        ''' Forge / NeoForge 声明的必需依赖的 Mod ID。
        ''' </summary>
        Public RequiresForge As New List(Of String)
        ''' <summary>
        ''' Fabric / Quilt 声明的必需依赖的 Mod ID。
        ''' </summary>
        Public RequiresFabric As New List(Of String)
    End Class

    '文件路径 -> Mod 元数据的缓存，避免重复解压
    Private ReadOnly ModMetadataCache As New Dictionary(Of String, ModMetadata)
    Private ReadOnly ModMetadataCacheLock As New Object

    ''' <summary>
    ''' 获取单个 Mod 文件的 Mod ID 列表。
    ''' </summary>
    Public Function GetModIds(FilePath As String) As List(Of String)
        Return GetModMetadata(FilePath).Ids
    End Function

    ''' <summary>
    ''' 获取单个 Mod 文件的元数据。
    ''' </summary>
    Public Function GetModMetadata(FilePath As String) As ModMetadata
        Dim Info As FileInfo = FileUtils.GetInfo(FilePath)
        If Not Info.Exists Then Return New ModMetadata
        Dim CacheKey As String = $"{Info.FullName}|{Info.Length}|{Info.LastWriteTime.Ticks}"
        SyncLock ModMetadataCacheLock
            If ModMetadataCache.ContainsKey(CacheKey) Then Return ModMetadataCache(CacheKey)
        End SyncLock
        Dim Result As ModMetadata = ReadModMetadata(FilePath)
        SyncLock ModMetadataCacheLock
            ModMetadataCache(CacheKey) = Result
        End SyncLock
        Return Result
    End Function

    ''' <summary>
    ''' 从 jar 内的元数据文件读取 Mod ID 与必需依赖。
    ''' 会同时读取 Jar-in-Jar 内嵌的 Mod（META-INF/jars、META-INF/jarjar），
    ''' 因为 NeoForge 的许多前置（如 EnderIO 的 endercore）是这样打包的。
    ''' </summary>
    Private Function ReadModMetadata(FilePath As String) As ModMetadata
        Dim Result As New ModMetadata
        Try
            Using Stream As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Using Archive As New ZipArchive(Stream, ZipArchiveMode.Read)
                    ReadArchiveMetadata(Archive, Result, 0)
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"读取 Mod 元数据失败：{FilePath}")
        End Try
        Result.Ids = Result.Ids.Distinct.ToList()
        Result.RequiresForge = Result.RequiresForge.Distinct.ToList()
        Result.RequiresFabric = Result.RequiresFabric.Distinct.ToList()
        Return Result
    End Function

    ''' <summary>
    ''' 读取单个 zip 的元数据，并递归读取其中内嵌的 jar。深度限制为 2 层。
    ''' </summary>
    Private Sub ReadArchiveMetadata(Archive As ZipArchive, Result As ModMetadata, Depth As Integer)
        'Fabric / Quilt
        For Each EntryName In {"fabric.mod.json", "quilt.mod.json"}
            Dim Entry As ZipArchiveEntry = Archive.GetEntry(EntryName)
            If Entry Is Nothing Then Continue For
            Try
                Dim Json = ReadEntryText(Entry).DeserializeJson()
                Dim Root As JObject = If(EntryName = "quilt.mod.json", TryCast(Json("quilt_loader"), JObject), Json)
                If Root Is Nothing Then Continue For
                If Root("id") IsNot Nothing Then Result.Ids.Add(Root("id").ToString)
                Dim Depends As JObject = TryCast(Root("depends"), JObject)
                If Depends IsNot Nothing AndAlso Depth = 0 Then '内嵌库的依赖由宿主负责，不计入
                    For Each Item As JProperty In Depends.Properties
                        Result.RequiresFabric.Add(Item.Name)
                    Next
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"解析 {EntryName} 失败")
            End Try
        Next
        'Forge / NeoForge
        '注意：mods.toml 中的 [[dependencies.xxx]] 段也会包含 modId，它们是被依赖的 Mod 而不是本 Mod，必须只读取 [[mods]] 段
        For Each EntryName In {"META-INF/mods.toml", "META-INF/neoforge.mods.toml"}
            Dim Entry As ZipArchiveEntry = Archive.GetEntry(EntryName)
            If Entry Is Nothing Then Continue For
            Dim InModsSection As Boolean = False
            Dim InDependencySection As Boolean = False
            Dim DependencyModId As String = Nothing
            Dim DependencyIsOptional As Boolean = False
            For Each Line As String In ReadEntryText(Entry).Split({vbCrLf, vbLf}, StringSplitOptions.None)
                Dim Trimmed As String = Line.Trim
                If Trimmed.StartsWith("[") Then
                    '提交上一个依赖段
                    If InDependencySection AndAlso DependencyModId IsNot Nothing AndAlso Not DependencyIsOptional AndAlso Depth = 0 Then Result.RequiresForge.Add(DependencyModId)
                    InModsSection = Trimmed.StartsWith("[[mods]]")
                    InDependencySection = Trimmed.StartsWith("[[dependencies.")
                    DependencyModId = Nothing
                    DependencyIsOptional = False
                    Continue For
                End If
                Dim Key As String = Nothing, Value As String = Nothing
                If Not ParseTomlLine(Trimmed, Key, Value) Then Continue For
                If InModsSection Then
                    If Key = "modId" Then Result.Ids.Add(Value)
                ElseIf InDependencySection Then
                    Select Case Key
                        Case "modId"
                            DependencyModId = Value
                        Case "mandatory"
                            If Value.Lower = "false" Then DependencyIsOptional = True
                        Case "type"
                            'NeoForge 1.21+ 使用 type 取代 mandatory
                            If Value.Lower <> "required" Then DependencyIsOptional = True
                    End Select
                End If
            Next
            '提交最后一个依赖段
            If InDependencySection AndAlso DependencyModId IsNot Nothing AndAlso Not DependencyIsOptional AndAlso Depth = 0 Then Result.RequiresForge.Add(DependencyModId)
        Next
        '内嵌 jar
        If Depth >= 2 Then Return
        For Each Entry As ZipArchiveEntry In Archive.Entries
            If Not Entry.FullName.EndsWithF(".jar", True) Then Continue For
            If Not (Entry.FullName.StartsWithF("META-INF/jars/") OrElse Entry.FullName.StartsWithF("META-INF/jarjar/")) Then Continue For
            Try
                Using SubStream = Entry.Open()
                    Using Buffer As New MemoryStream()
                        SubStream.CopyTo(Buffer)
                        Buffer.Position = 0
                        Using SubArchive As New ZipArchive(Buffer, ZipArchiveMode.Read)
                            ReadArchiveMetadata(SubArchive, Result, Depth + 1)
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Logger.Warn(ex, $"读取内嵌 jar 失败：{Entry.FullName}")
            End Try
        Next
    End Sub

    ''' <summary>
    ''' 解析 TOML 中的 key = value 行。
    ''' </summary>
    Private Function ParseTomlLine(Line As String, ByRef Key As String, ByRef Value As String) As Boolean
        Key = "" : Value = ""
        Dim Index As Integer = Line.IndexOf("="c)
        If Index <= 0 Then Return False
        Key = Line.Substring(0, Index).Trim
        If Key = "" OrElse Key.Contains(" ") OrElse Key.StartsWith("#") Then Return False
        Dim Raw As String = Line.Substring(Index + 1).Trim()
        If Raw.StartsWith("""") OrElse Raw.StartsWith("'") Then
            '带引号的值，取配对引号之间的内容，避免把行尾注释也读进去
            Dim Quote As Char = Raw(0)
            Dim EndIndex As Integer = Raw.IndexOf(Quote, 1)
            Value = If(EndIndex > 0, Raw.Substring(1, EndIndex - 1), Raw.Trim(Quote))
        Else
            '不带引号的值，直接去掉行尾注释
            Dim CommentIndex As Integer = Raw.IndexOf("#"c)
            If CommentIndex >= 0 Then Raw = Raw.Substring(0, CommentIndex)
            Value = Raw.Trim()
        End If
        Return True
    End Function

    Private Function ReadEntryText(Entry As ZipArchiveEntry) As String
        Using Reader As New StreamReader(Entry.Open(), Encoding.UTF8)
            Return Reader.ReadToEnd()
        End Using
    End Function

#End Region

#Region "前置完整性检查"

    ''' <summary>
    ''' 平台内置的 Mod ID，不能作为前置被检查。
    ''' </summary>
    Private ReadOnly PlatformModIds As New HashSet(Of String) From {
        "minecraft", "forge", "neoforge", "fabricloader", "fabric", "quiltloader", "java", "liteloader", "fml", "optifine"}

    ''' <summary>
    ''' 一个缺失的前置。
    ''' </summary>
    Public Class MissingDependency

        ''' <summary>
        ''' 缺失的 Mod ID。
        ''' </summary>
        Public ReadOnly ModId As String
        ''' <summary>
        ''' 声明该依赖的 Mod。
        ''' </summary>
        Public ReadOnly RequiredBy As String
        ''' <summary>
        ''' 在资源网站上找到的对应工程。若为 Nothing 则没有找到。
        ''' </summary>
        Public Project As ResourceProject = Nothing
        ''' <summary>
        ''' 为当前游戏版本挑选出的文件。
        ''' </summary>
        Public File As ResourceVersion = Nothing
        ''' <summary>
        ''' 无法自动下载的原因。
        ''' </summary>
        Public FailReason As String = Nothing

        Public Sub New(ModId As String, RequiredBy As String)
            Me.ModId = ModId
            Me.RequiredBy = RequiredBy
        End Sub

        ''' <summary>
        ''' 列表项显示的标题。
        ''' </summary>
        Public ReadOnly Property DisplayName As String
            Get
                Return If(Project?.TranslatedName, ModId)
            End Get
        End Property

        ''' <summary>
        ''' 列表项显示的描述。
        ''' </summary>
        Public ReadOnly Property Description As String
            Get
                Dim Parts As New List(Of String) From {$"由 {RequiredBy} 需要"}
                If File IsNot Nothing Then
                    Parts.Add(File.Display)
                    If File.ReleaseType <> ResourceVersion.ReleaseTypes.Release Then Parts.Add(File.ReleaseTypeDisplay)
                End If
                If FailReason IsNot Nothing Then Parts.Add(FailReason)
                Return Parts.Join("，")
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return $"{ModId}: {Description}"
        End Function

    End Class

    ''' <summary>
    ''' 检查指定 mods 文件夹中所有 Mod 的必需前置是否满足。
    ''' </summary>
    ''' <param name="ModsFolder">mods 文件夹。</param>
    ''' <param name="TargetMcVersion">目标 MC 版本，用于为缺失的前置挑选版本。</param>
    ''' <param name="TargetLoaders">目标 Mod 加载器，用于为缺失的前置挑选版本。</param>
    Public Function CheckDependencies(ModsFolder As String, TargetMcVersion As String, TargetLoaders As ModLoaders) As List(Of MissingDependency)
        Dim Result As New List(Of MissingDependency)
        If String.IsNullOrWhiteSpace(ModsFolder) OrElse Not DirectoryUtils.Exists(ModsFolder) Then Return Result
        '读取所有本地 Mod 的元数据
        Dim LocalMods As New List(Of KeyValuePair(Of String, ModMetadata))
        Dim Installed As New HashSet(Of String)
        For Each FilePath As String In DirectoryUtils.EnumerateFiles(ModsFolder)
            If Not IsModFile(FilePath) Then Continue For
            Dim Meta As ModMetadata = GetModMetadata(FilePath)
            LocalMods.Add(New KeyValuePair(Of String, ModMetadata)(FilePath, Meta))
            For Each ModId As String In Meta.Ids
                Dim Key As String = NormalizeKey(ModId)
                If Key.Length >= 3 Then Installed.Add(Key)
            Next
        Next
        If Not LocalMods.Any() Then Return Result
        '按加载器决定读取哪一类依赖声明：多平台 jar 在 Forge 下不会加载 fabric.mod.json 中的依赖
        Dim CheckForge As Boolean = (TargetLoaders And (ModLoaders.Forge Or ModLoaders.NeoForge)) <> ModLoaders.None
        Dim CheckFabric As Boolean = (TargetLoaders And (ModLoaders.Fabric Or ModLoaders.LiteLoader)) <> ModLoaders.None
        If Not CheckForge AndAlso Not CheckFabric Then
            CheckForge = True
            CheckFabric = True
        End If
        '找出缺失的必需前置
        Dim Missing As New Dictionary(Of String, MissingDependency)
        For Each LocalMod In LocalMods
            '被禁用的 Mod 不会被加载，无需检查它的前置
            If LocalMod.Key.Lower.EndsWithF(".disabled") Then Continue For
            Dim SelfName As String = If(LocalMod.Value.Ids.FirstOrDefault, PathUtils.GetFileNameWithoutExtension(LocalMod.Key))
            Dim Requires As New List(Of String)
            If CheckForge Then Requires.AddRange(LocalMod.Value.RequiresForge)
            If CheckFabric Then Requires.AddRange(LocalMod.Value.RequiresFabric)
            For Each RequiredId As String In Requires
                Dim Key As String = NormalizeKey(RequiredId)
                If Key.Length < 3 Then Continue For
                If PlatformModIds.Contains(Key) Then Continue For
                '纯 Forge 实例不会加载 Fabric 的依赖声明（多平台 jar 常见的移植残留）
                If Not CheckFabric AndAlso Key.StartsWithF("fabric") Then Continue For
                If ContainsModId(Installed, Key) Then Continue For
                If Missing.ContainsKey(Key) Then Continue For
                Missing(Key) = New MissingDependency(RequiredId, SelfName)
            Next
        Next
        If Not Missing.Any() Then Return Result
        Logger.Info($"检查到 {Missing.Count} 项可能缺失的前置：{String.Join("，", Missing.Values.Select(Function(m) m.ModId))}")
        '联网查找对应的工程
        For Each Entry As MissingDependency In Missing.Values
            Try
                Entry.Project = FindProjectByModId(Entry.ModId, TargetLoaders)
                If Entry.Project IsNot Nothing Then Entry.File = GetBestFile(Entry.Project, TargetMcVersion, TargetLoaders)
                If Entry.Project Is Nothing Then
                    Entry.FailReason = "未在 Modrinth 或 CurseForge 中找到对应工程"
                ElseIf Entry.File Is Nothing Then
                    Entry.FailReason = If(String.IsNullOrEmpty(TargetMcVersion), "没有可用的版本", $"没有适用于 {TargetMcVersion} 的版本")
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"查找前置 {Entry.ModId} 对应的工程失败")
                Entry.FailReason = "查找失败"
            End Try
            Result.Add(Entry)
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 检查已安装的 Mod ID 集合中是否包含指定项。
    ''' </summary>
    Private Function ContainsModId(Installed As HashSet(Of String), Key As String) As Boolean
        If Installed.Contains(Key) Then Return True
        'Fabric API 的子模块（fabric-registry-sync-v0 等）都随 fabric-api 一起安装
        If Key.StartsWithF("fabric") AndAlso Installed.Contains("fabricapi") Then Return True
        If Key.Length < 4 Then Return False
        For Each InstalledKey As String In Installed
            If InstalledKey.Length < 4 Then Continue For
            If InstalledKey.StartsWithF(Key) AndAlso InstalledKey.Length - Key.Length <= 3 Then Return True
            If Key.StartsWithF(InstalledKey) AndAlso Key.Length - InstalledKey.Length <= 3 Then Return True
        Next
        Return False
    End Function

    ''' <summary>
    ''' 通过 Mod ID 在 Modrinth、CurseForge 中查找对应的工程。
    ''' Mod ID 是连写的（如 ftblibrary、kotlinforforge），直接搜索往往没有结果，
    ''' 因此会依次用完整 ID、前 6 位、前 4 位、前 3 位作为搜索词重试，再按工程短名前缀匹配。
    ''' </summary>
    Public Function FindProjectByModId(ModId As String, Optional TargetLoaders As ModLoaders = ModLoaders.None) As ResourceProject
        Dim TargetKey As String = NormalizeKey(ModId)
        If TargetKey.Length < 3 Then Return Nothing
        '生成候选搜索词
        Dim Queries As New List(Of String) From {ModId}
        For Each Length As Integer In {6, 4, 3}
            If Length >= TargetKey.Length Then Continue For
            Dim Query As String = TargetKey.Substring(0, Length)
            If Not Queries.Contains(Query) Then Queries.Add(Query)
        Next
        For Each Query As String In Queries
            'Modrinth
            Try
                Dim Json = DlModRequest($"https://api.modrinth.com/v2/search?limit=50&index=relevance&query={StringUtils.UrlEscape(Query)}&facets=[[%22project_type:mod%22]]")
                Dim Best As JToken = PickBestHit(CType(Json("hits"), JArray), TargetKey, TargetLoaders)
                If Best IsNot Nothing Then
                    Logger.Info($"前置 {ModId} 在 Modrinth 上对应工程 {Best("slug")}（搜索词 {Query}）")
                    Return ResourceProject.FromProjectId(Best("project_id").ToString, ResourcePlatforms.Modrinth)
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"在 Modrinth 中查找 {ModId} 失败（搜索词 {Query}）")
            End Try
            'CurseForge
            Try
                Dim Json = DlModRequest($"https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&pageSize=50&sortField=2&sortOrder=desc&searchFilter={StringUtils.UrlEscape(Query)}")
                Dim Best As JToken = PickBestHit(CType(Json("data"), JArray), TargetKey, TargetLoaders)
                If Best IsNot Nothing Then
                    Logger.Info($"前置 {ModId} 在 CurseForge 上对应工程 {Best("slug")}（搜索词 {Query}）")
                    Return New ResourceProject(CType(Best, JObject))
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"在 CurseForge 中查找 {ModId} 失败（搜索词 {Query}）")
            End Try
        Next
        Logger.Warn($"未能为前置 {ModId} 找到对应的工程")
        Return Nothing
    End Function

    ''' <summary>
    ''' 从搜索结果中挑选与 Mod ID 最匹配的工程。
    ''' </summary>
    Private Function PickBestHit(Hits As JArray, TargetKey As String, TargetLoaders As ModLoaders) As JToken
        If Hits Is Nothing OrElse Not Hits.Any() Then Return Nothing
        Dim Candidates As List(Of JToken) = Hits.Where(Function(h) Not String.IsNullOrEmpty(h("slug")?.ToString)).ToList()
        If Not Candidates.Any() Then Return Nothing
        '工程短名与 Mod ID 完全一致
        Dim Exact As JToken = Candidates.FirstOrDefault(Function(h) NormalizeKey(h("slug").ToString) = TargetKey)
        If Exact IsNot Nothing Then Return Exact
        '工程短名以 Mod ID 开头，例如 ftblibrary ↔ ftb-library-forge
        Dim Prefixed As List(Of JToken) = Candidates.Where(Function(h) NormalizeKey(h("slug").ToString).StartsWithF(TargetKey)).ToList()
        If Not Prefixed.Any() Then Return Nothing
        '优先选择加载器匹配的版本，避免给 Forge 实例挑到 ftb-library-fabric
        If TargetLoaders <> ModLoaders.None Then
            Dim LoaderMatched As List(Of JToken) = Prefixed.Where(
            Function(h)
                Dim Slug As String = h("slug").ToString.Lower
                If TargetLoaders.HasFlagF(ModLoaders.NeoForge) AndAlso Slug.Contains("neoforge") Then Return True
                If TargetLoaders.HasFlagF(ModLoaders.Forge) AndAlso Slug.Contains("forge") AndAlso Not Slug.Contains("neoforge") Then Return True
                If TargetLoaders.HasFlagF(ModLoaders.Fabric) AndAlso Slug.Contains("fabric") Then Return True
                Return False
            End Function).ToList()
            If LoaderMatched.Any() Then Return LoaderMatched.First()
        End If
        Return Prefixed.First()
    End Function

#End Region

End Module
