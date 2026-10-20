Imports System.Runtime.InteropServices

''' <summary>
''' 内存优化与垃圾清理。
''' 实现遵循上游 PCL 的做法：内存优化在提权后清理全部进程的工作集，垃圾清理则直接清空缓存、临时文件与日志。
''' </summary>
Friend Module ModFeature

#Region "内存优化"

    <DllImport("psapi.dll", SetLastError:=True)>
    Private Function EmptyWorkingSet(hProcess As IntPtr) As Boolean
    End Function

    ''' <summary>
    ''' 执行内存优化。没有管理员权限时会通过 --memory 参数以管理员权限重启自身完成优化。
    ''' </summary>
    ''' <param name="ShowHint">是否显示提示。</param>
    Public Sub MemoryOptimize(Optional ShowHint As Boolean = True)
        If ShowHint Then Hint("正在进行内存优化，请稍候……")
        Try
            If WindowsUtils.HasAdminRole() Then
                MemoryOptimizeInternal(ShowHint)
            Else
                '上游做法：以管理员权限重新启动自己，并执行 --memory
                Dim FreedKb As Integer = RunAsAdmin("--memory")
                If FreedKb < 0 Then Throw New Exception("提权执行内存优化失败")
                If ShowHint Then Hint($"内存优化完成，共释放 {FormatSize(FreedKb * 1024L)}", HintType.Green)
            End If
        Catch ex As Exception
            '用户拒绝了 UAC 等情况：至少把启动器自己的内存交还给系统
            Logger.Warn(ex, "内存优化失败，仅清理启动器自身的内存占用")
            MemoryOptimizeInternal(False)
            If ShowHint Then Hint("未能取得管理员权限，已仅优化启动器自身的内存占用", HintType.Red)
        End Try
    End Sub

    ''' <summary>
    ''' 实际执行内存优化：回收托管内存，并清空所有可访问进程的工作集。
    ''' 该操作会让系统在随后一小段时间内重新从磁盘读取数据，机械硬盘上可能出现卡顿。
    ''' </summary>
    Public Sub MemoryOptimizeInternal(Optional ShowHint As Boolean = True)
        '先回收自身托管内存
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        GC.WaitForPendingFinalizers()
        '再让所有能访问到的进程归还工作集
        Dim SuccessCount As Integer = 0
        For Each Proc As Process In Process.GetProcesses
            Try
                If EmptyWorkingSet(Proc.Handle) Then SuccessCount += 1
            Catch ex As Exception
                '系统进程等无法访问的进程直接跳过
            Finally
                Proc.Dispose()
            End Try
        Next
        Try
            Logger.Info($"内存优化完成，已清理 {SuccessCount} 个进程的工作集")
        Catch
            '启动早期（--memory 参数）日志尚未初始化
        End Try
    End Sub

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

#End Region

#Region "清理垃圾"

    ''' <summary>
    ''' 清理游戏垃圾：下载缓存、临时文件与启动器日志。
    ''' </summary>
    ''' <param name="ShowHint">是否显示提示。</param>
    ''' <returns>清理掉的字节数。</returns>
    Public Function RubbishClear(Optional ShowHint As Boolean = True) As Long
        Dim Freed As Long = 0
        Dim Failed As Integer = 0
        '下载缓存
        Freed += ClearFolder(PathTemp & "Cache\", Failed)
        '临时文件（跳过缓存目录，避免重复统计）
        Freed += ClearFolder(PathTemp, Failed, {"cache"})
        '启动器日志
        Freed += ClearFiles(Paths.Base & "RMCL\", "Log*.txt", Failed)
        If ShowHint Then
            If Failed = 0 Then
                Hint($"垃圾清理完成，共清理 {FormatSize(Freed)}", HintType.Green)
            Else
                Hint($"垃圾清理完成，共清理 {FormatSize(Freed)}，{Failed} 个文件被占用已跳过", HintType.Red)
            End If
        End If
        Return Freed
    End Function

    ''' <summary>
    ''' 统计垃圾占用的空间，返回 (合计字节, 分行说明)。
    ''' </summary>
    Public Function GetRubbishSummary() As (Total As Long, Details As List(Of String))
        Dim CacheSize As Long = GetFolderSize(PathTemp & "Cache\")
        Dim TempSize As Long = GetFolderSize(PathTemp) - CacheSize
        Dim LogSize As Long = GetFilePatternSize(Paths.Base & "RMCL\", "Log*.txt")
        Dim Details As New List(Of String) From {
            $"下载缓存：{FormatSize(CacheSize)}",
            $"临时文件：{FormatSize(If(TempSize < 0, 0, TempSize))}",
            $"启动器日志：{FormatSize(LogSize)}"}
        Return (CacheSize + If(TempSize < 0, 0, TempSize) + LogSize, Details)
    End Function

    ''' <summary>
    ''' 清空文件夹内容（不删除文件夹本身），返回清理掉的字节数。
    ''' </summary>
    Private Function ClearFolder(Folder As String, ByRef Failed As Integer, Optional SkipNames As String() = Nothing) As Long
        Dim Freed As Long = 0
        If Not Directory.Exists(Folder) Then Return 0
        Try
            For Each SubFolder In Directory.EnumerateDirectories(Folder)
                Dim Name As String = Path.GetFileName(SubFolder).ToLower()
                If SkipNames IsNot Nothing AndAlso Array.IndexOf(SkipNames, Name) >= 0 Then Continue For
                Freed += GetFolderSize(SubFolder)
                Try
                    Directory.Delete(SubFolder, True)
                Catch ex As Exception
                    Failed += 1
                    Logger.Warn(ex, $"删除文件夹失败：{SubFolder}")
                End Try
            Next
            Freed += ClearFiles(Folder, "*", Failed)
        Catch ex As Exception
            Logger.Warn(ex, $"清理文件夹失败：{Folder}")
        End Try
        Return Freed
    End Function

    ''' <summary>
    ''' 删除文件夹中匹配的通配符文件，返回清理掉的字节数。
    ''' </summary>
    Private Function ClearFiles(Folder As String, Pattern As String, ByRef Failed As Integer) As Long
        Dim Freed As Long = 0
        If Not Directory.Exists(Folder) Then Return 0
        Try
            For Each FilePath In Directory.EnumerateFiles(Folder, Pattern, SearchOption.TopDirectoryOnly)
                Try
                    Dim Length As Long = New FileInfo(FilePath).Length
                    File.Delete(FilePath)
                    Freed += Length
                Catch ex As Exception
                    Failed += 1
                    Logger.Warn(ex, $"删除文件失败：{FilePath}")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"枚举文件失败：{Folder}")
        End Try
        Return Freed
    End Function

    ''' <summary>
    ''' 递归统计文件夹占用的空间（字节）。
    ''' </summary>
    Private Function GetFolderSize(Folder As String) As Long
        If Not Directory.Exists(Folder) Then Return 0
        Dim Total As Long = 0
        Try
            For Each FilePath In Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
                Try
                    Total += New FileInfo(FilePath).Length
                Catch
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"统计文件夹大小失败：{Folder}")
        End Try
        Return Total
    End Function

    ''' <summary>
    ''' 统计文件夹中匹配的通配符文件大小。
    ''' </summary>
    Private Function GetFilePatternSize(Folder As String, Pattern As String) As Long
        If Not Directory.Exists(Folder) Then Return 0
        Dim Total As Long = 0
        Try
            For Each FilePath In Directory.EnumerateFiles(Folder, Pattern, SearchOption.TopDirectoryOnly)
                Try
                    Total += New FileInfo(FilePath).Length
                Catch
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"统计文件大小失败：{Folder}")
        End Try
        Return Total
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
