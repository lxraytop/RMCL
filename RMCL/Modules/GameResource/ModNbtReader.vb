Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text

''' <summary>
''' 最小 NBT 读取器：只从 level.dat 里取存档名、最后游玩时间、游戏模式和版本。
'''
''' 存档动辄几十 MB，完整解析 NBT 纯属浪费内存和时间。
''' 这里只顺序走一遍顶层复合标签，需要的字段解析出来，其余按类型整体跳过，
''' 读取成本基本等于「解压前几 KB」。
''' </summary>
Friend Module ModNbtReader

    ''' <summary>从 level.dat 读出的存档信息。读取失败时字段保持默认值。</summary>
    Friend Class SaveInfo
        Public Name As String = ""
        Public LastPlayed As Date = Date.MinValue
        ''' <summary>0 生存 / 1 创造 / 2 冒险 / 3 旁观，未知为 -1。</summary>
        Public GameType As Integer = -1
        Public VersionName As String = ""
    End Class

    Public Function Read(LevelDatPath As String) As SaveInfo
        Dim Info As New SaveInfo
        Try
            If Not IO.File.Exists(LevelDatPath) Then Return Info
            Using Stream As New FileStream(LevelDatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Using Gzip As New GZipStream(Stream, CompressionMode.Decompress)
                    Using Reader As New BinaryReader(Gzip, Encoding.UTF8)
                        '根是一个 TAG_Compound，名字通常为空
                        If ReadTagType(Reader) <> 10 Then Return Info
                        ReadString(Reader)
                        ReadRootCompound(Reader, Info)
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"读取存档信息失败：{LevelDatPath}")
        End Try
        Return Info
    End Function

#Region "解析"

    Private Sub ReadRootCompound(Reader As BinaryReader, Info As SaveInfo)
        Do
            Dim TagType As Integer = ReadTagType(Reader)
            If TagType = 0 Then Return
            Dim TagName As String = ReadString(Reader)
            Select Case TagName
                Case "Data"
                    '现代存档的字段全在 Data 这层复合标签里，根上只有它一个
                    If TagType = 10 Then ReadRootCompound(Reader, Info) Else Skip(Reader, TagType)
                Case "LevelName"
                    If TagType = 8 Then Info.Name = ReadString(Reader) Else Skip(Reader, TagType)
                Case "LastPlayed"
                    If TagType = 4 Then
                        Info.LastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64Big(Reader)).ToLocalTime().DateTime
                    Else
                        Skip(Reader, TagType)
                    End If
                Case "GameType"
                    If TagType = 3 Then Info.GameType = ReadInt32Big(Reader) Else Skip(Reader, TagType)
                Case "Version"
                    If TagType = 10 Then ReadVersionCompound(Reader, Info) Else Skip(Reader, TagType)
                Case Else
                    Skip(Reader, TagType)
            End Select
        Loop
    End Sub

    ''' <summary>Version 复合标签里只需要 Name 字段。</summary>
    Private Sub ReadVersionCompound(Reader As BinaryReader, Info As SaveInfo)
        Do
            Dim TagType As Integer = ReadTagType(Reader)
            If TagType = 0 Then Return
            Dim TagName As String = ReadString(Reader)
            If TagName = "Name" AndAlso TagType = 8 Then
                Info.VersionName = ReadString(Reader)
            Else
                Skip(Reader, TagType)
            End If
        Loop
    End Sub

    ''' <summary>按标签类型整体跳过，不关心内容。</summary>
    Private Sub Skip(Reader As BinaryReader, TagType As Integer)
        Select Case TagType
            Case 0
                'TAG_End，没有负载
            Case 1
                Reader.ReadByte()
            Case 2
                Reader.ReadBytes(2)
            Case 3, 5
                Reader.ReadBytes(4)
            Case 4, 6
                Reader.ReadBytes(8)
            Case 7
                Dim Length As Integer = ReadInt32Big(Reader)
                If Length > 0 Then Reader.ReadBytes(Length)
            Case 8
                Dim Length As Integer = ReadInt16Big(Reader)
                If Length > 0 Then Reader.ReadBytes(Length)
            Case 9
                Dim ItemType As Integer = Reader.ReadByte()
                Dim Count As Integer = ReadInt32Big(Reader)
                For i = 1 To Count
                    Skip(Reader, ItemType)
                Next
            Case 10
                Do
                    Dim Inner As Integer = ReadTagType(Reader)
                    If Inner = 0 Then Exit Do
                    ReadString(Reader)
                    Skip(Reader, Inner)
                Loop
            Case 11
                Dim Length As Integer = ReadInt32Big(Reader)
                If Length > 0 Then Reader.ReadBytes(Length * 4)
            Case 12
                Dim Length As Integer = ReadInt32Big(Reader)
                If Length > 0 Then Reader.ReadBytes(Length * 8)
        End Select
    End Sub

#End Region

#Region "大端读取（NBT 全是大端，BinaryReader 默认小端）"

    Private Function ReadTagType(Reader As BinaryReader) As Integer
        Return CInt(Reader.ReadByte())
    End Function

    Private Function ReadInt16Big(Reader As BinaryReader) As Integer
        Dim Buffer As Byte() = Reader.ReadBytes(2)
        If Buffer.Length < 2 Then Return 0
        Return (CInt(Buffer(0)) << 8) Or CInt(Buffer(1))
    End Function

    Private Function ReadInt32Big(Reader As BinaryReader) As Integer
        Dim Buffer As Byte() = Reader.ReadBytes(4)
        If Buffer.Length < 4 Then Return 0
        Return (CInt(Buffer(0)) << 24) Or (CInt(Buffer(1)) << 16) Or (CInt(Buffer(2)) << 8) Or CInt(Buffer(3))
    End Function

    Private Function ReadInt64Big(Reader As BinaryReader) As Long
        Dim Buffer As Byte() = Reader.ReadBytes(8)
        If Buffer.Length < 8 Then Return 0
        Dim Value As Long = 0
        For i = 0 To 7
            Value = (Value << 8) Or Buffer(i)
        Next
        Return Value
    End Function

    Private Function ReadString(Reader As BinaryReader) As String
        Dim Length As Integer = ReadInt16Big(Reader)
        If Length <= 0 Then Return ""
        Dim Buffer As Byte() = Reader.ReadBytes(Length)
        Return Encoding.UTF8.GetString(Buffer)
    End Function

#End Region

End Module
