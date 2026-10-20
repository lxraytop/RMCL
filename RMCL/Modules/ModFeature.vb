Imports System.Runtime.InteropServices

''' <summary>
''' 内存优化与垃圾清理的通用实现，供「设置 → 功能」页面与启动流程共用。
''' </summary>
Friend Module ModFeature

#Region "内存优化"

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function SetProcessWorkingSetSize(hProcess As IntPtr, dwMinimumWorkingSetSize As IntPtr,
                                              dwMaximumWorkingSetSize As IntPtr) As Boolean
    End Function

    ''' <summary>
    ''' 当前进程占用的物理内存（MB）。
    ''' </summary>
    Public Function MemoryUsedMb() As Long
        Try
            Using Proc As Process = Process.GetCurrentProcess()
                Return CLng(Math.Round(Proc.WorkingSet64 / 1024 / 1024))
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "获取内存占用失败")
            Return 0
        End Try
    End Function

    ''' <summary>
    ''' 强制进行完整 GC，并释放当前进程的空闲工作集。
    ''' </summary>
    Public Sub MemoryOptimize()
        Try
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
            GC.WaitForPendingFinalizers()
            '将工作集交还给系统
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, New IntPtr(-1), New IntPtr(-1))
        Catch ex As Exception
            Logger.Warn(ex, "内存优化失败")
        End Try
    End Sub

#End Region

#Region "清理垃圾"

    ''' <summary>
    ''' 一个可清理的位置。
    ''' </summary>
    Public Class CleanItem
        Public ReadOnly Key As String
        Public ReadOnly Name As String
        Public ReadOnly Folder As String
        ''' <summary>仅删除匹配该通配符的文件；为空时表示删除目录下的所有内容。</summary>
        Public ReadOnly Pattern As String

        Public Sub New(Key As String, Name As String, Folder As String, Optional Pattern As String = "")
            Me.Key = Key
            Me.Name = Name
            Me.Folder = Folder
            Me.Pattern = Pattern
        End Sub
    End Class

    ''' <summary>
    ''' 全部可清理的位置。
    ''' </summary>
    Public Function GetCleanTargets() As List(Of CleanItem)
        Return New List(Of CleanItem) From {
            New CleanItem("Temp", "临时文件", PathTemp),
            New CleanItem("Cache", "下载缓存", PathTemp & "Cache"),
            New CleanItem("Log", "启动器日志", Paths.Base & "RMCL\", "Log*.txt")
        }
    End Function

    ''' <summary>
    ''' 计算某个清理位置占用的空间（字节）。
    ''' </summary>
    Public Function GetCleanTargetSize(Target As CleanItem) As Long
        Try
            If Not Directory.Exists(Target.Folder) Then Return 0
            Dim Total As Long = 0
            If Target.Pattern = "" Then
                '临时文件：不统计下载缓存，避免重复计算
                For Each FilePath In Directory.EnumerateFiles(Target.Folder, "*", SearchOption.AllDirectories)
                    If FilePath.StartsWithF(PathTemp & "Cache\") Then Continue For
                    Total += GetFileLength(FilePath)
                Next
            Else
                For Each FilePath In Directory.EnumerateFiles(Target.Folder, Target.Pattern, SearchOption.TopDirectoryOnly)
                    Total += GetFileLength(FilePath)
                Next
            End If
            Return Total
        Catch ex As Exception
            Logger.Warn(ex, $"计算 {Target.Name} 占用空间失败")
            Return 0
        End Try
    End Function

    Private Function GetFileLength(FilePath As String) As Long
        Try
            Return New FileInfo(FilePath).Length
        Catch
            Return 0
        End Try
    End Function

    ''' <summary>
    ''' 执行清理，返回是否成功（被占用的文件会被跳过）。
    ''' </summary>
    Public Function CleanTarget(Target As CleanItem) As Boolean
        Try
            If Not Directory.Exists(Target.Folder) Then Return True
            If Target.Pattern = "" Then
                For Each SubFolder In Directory.EnumerateDirectories(Target.Folder)
                    If SubFolder = Target.Folder.TrimEnd("\"c) & "\Cache" Then Continue For
                    Try
                        Directory.Delete(SubFolder, True)
                    Catch ex As Exception
                        Logger.Warn(ex, $"删除文件夹失败：{SubFolder}")
                    End Try
                Next
                For Each FilePath In Directory.EnumerateFiles(Target.Folder, "*", SearchOption.TopDirectoryOnly)
                    Try
                        File.Delete(FilePath)
                    Catch ex As Exception
                        Logger.Warn(ex, $"删除文件失败：{FilePath}")
                    End Try
                Next
            Else
                For Each FilePath In Directory.EnumerateFiles(Target.Folder, Target.Pattern, SearchOption.TopDirectoryOnly)
                    Try
                        File.Delete(FilePath)
                    Catch ex As Exception
                        Logger.Warn(ex, $"删除文件失败：{FilePath}")
                    End Try
                Next
            End If
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"清理 {Target.Name} 失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 按用户勾选执行清理。
    ''' </summary>
    Public Function CleanCheckedJunk() As Long
        Dim Freed As Long = 0
        For Each Target In GetCleanTargets()
            Dim Enabled As Boolean
            Select Case Target.Key
                Case "Temp" : Enabled = Settings.Get(Of Boolean)("FeatureCleanTemp")
                Case "Cache" : Enabled = Settings.Get(Of Boolean)("FeatureCleanCache")
                Case "Log" : Enabled = Settings.Get(Of Boolean)("FeatureCleanLog")
                Case Else : Enabled = False
            End Select
            If Not Enabled Then Continue For
            Freed += GetCleanTargetSize(Target)
            CleanTarget(Target)
        Next
        Return Freed
    End Function

#End Region

    ''' <summary>
    ''' 将字节数格式化为易读文本。
    ''' </summary>
    Public Function FormatSize(Bytes As Long) As String
        If Bytes < 1024 Then Return Bytes & " B"
        If Bytes < 1024 * 1024 Then Return Math.Round(Bytes / 1024, 1) & " KB"
        If Bytes < 1024L * 1024 * 1024 Then Return Math.Round(Bytes / 1024 / 1024, 1) & " MB"
        Return Math.Round(Bytes / 1024 / 1024 / 1024, 2) & " GB"
    End Function

End Module
