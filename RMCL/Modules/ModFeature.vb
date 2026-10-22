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
    ''' PROCESS_MEMORY_COUNTERS_EX2（Windows 8.1+）。比 EX 多出 PrivateWorkingSetSize，即「专用工作集」。
    ''' </summary>
    <StructLayout(LayoutKind.Sequential)>
    Private Structure ProcessMemoryCountersEx2
        Public cb As Integer
        Public PageFaultCount As Integer
        Public PeakWorkingSetSize As IntPtr
        Public WorkingSetSize As IntPtr
        Public QuotaPeakPagedPoolUsage As IntPtr
        Public QuotaPagedPoolUsage As IntPtr
        Public QuotaPeakNonPagedPoolUsage As IntPtr
        Public QuotaNonPagedPoolUsage As IntPtr
        Public PagefileUsage As IntPtr
        Public PeakPagefileUsage As IntPtr
        Public PrivateUsage As IntPtr
        Public PrivateWorkingSetSize As IntPtr
        Public SharedCommitUsage As ULong
    End Structure

    <DllImport("psapi.dll", SetLastError:=True)>
    Private Function GetProcessMemoryInfo(hProcess As IntPtr, ByRef Counters As ProcessMemoryCountersEx2, cb As Integer) As Boolean
    End Function

    ''' <summary>
    ''' 读取进程当前的「专用工作集」字节数，专用值不可用时退回整个工作集；都读不到返回 -1。
    ''' 专用工作集不含共享页，所以跨进程求和不会重复计算 —— 这正是之前释放量虚高的根源。
    ''' </summary>
    Private Function PrivateWorkingSetOf(Proc As Process) As Long
        Try
            Dim Counters As New ProcessMemoryCountersEx2 With {.cb = Marshal.SizeOf(GetType(ProcessMemoryCountersEx2))}
            If GetProcessMemoryInfo(Proc.Handle, Counters, Counters.cb) Then
                Dim Value As Long = Counters.PrivateWorkingSetSize.ToInt64()
                '专用工作集被清到 0 是正常结果，此时退回整个工作集（基本只剩共享页），读数仍然很小
                If Value > 0 Then Return Value
            End If
        Catch ex As Exception
            '权限不足或进程已退出，走下面的兜底
        End Try
        Try
            Return Proc.WorkingSet64
        Catch ex As Exception
            Return -1
        End Try
    End Function

    ''' <summary>
    ''' 执行内存优化。清理其它进程的工作集必须要有管理员权限，没有权限时不会执行任何「优化」。
    ''' </summary>
    ''' <param name="ShowHint">是否显示提示。</param>
    ''' <param name="OnProgress">进度回调，参数为 (已处理进程数, 进程总数)；进程总数为 0 表示正在等待提权进程。</param>
    ''' <returns>本次优化释放的字节数；返回 -1 表示未执行（未取得管理员权限或用户取消了 UAC）。</returns>
    Public Function MemoryOptimize(Optional ShowHint As Boolean = True, Optional OnProgress As Action(Of Integer, Integer) = Nothing) As Long
        '已经是管理员：直接在本进程内完成，不需要再弹一次 UAC
        If WindowsUtils.HasAdminRole() Then
            If ShowHint Then Hint("正在进行内存优化，请稍候……")
            Dim SelfFreed As Long = MemoryOptimizeInternal(False, OnProgress)
            Logger.Info($"内存优化结束，共释放 {FormatSize(SelfFreed)}")
            If ShowHint Then
                If SelfFreed > 0 Then
                    Hint($"内存优化完成，共释放 {FormatSize(SelfFreed)}", HintType.Green)
                Else
                    Hint("内存优化完成，但未检测到明显可释放的内存", HintType.Green)
                End If
            End If
            Return SelfFreed
        End If
        '上游做法：以管理员权限重新启动自己并执行 --memory，退出码为释放的 KB（负数表示失败）
        If ShowHint Then Hint("正在进行内存优化，请在权限提示中允许管理员权限……")
        If OnProgress IsNot Nothing Then OnProgress(0, 0)
        Dim FreedKb As Integer
        Try
            FreedKb = RunAsAdmin("--memory")
        Catch ex As Exception
            '用户在 UAC 上点了「否」（Win32Exception 1223），或提权进程未能启动
            Logger.Warn(ex, "申请管理员权限失败，内存优化未执行")
            If ShowHint Then Hint("内存优化需要管理员权限，已取消（未做任何改动）", HintType.Red)
            Return -1
        End Try
        If FreedKb < 0 Then
            Logger.Warn($"提权进程未能完成内存优化，退出码 {FreedKb}")
            If ShowHint Then Hint("内存优化未能完成：提权进程没有取得管理员权限", HintType.Red)
            Return -1
        End If
        Dim Freed As Long = FreedKb * 1024L
        Logger.Info($"内存优化结束，共释放 {FormatSize(Freed)}")
        If ShowHint Then
            If Freed > 0 Then
                Hint($"内存优化完成，共释放 {FormatSize(Freed)}", HintType.Green)
            Else
                Hint("内存优化完成，但未检测到明显可释放的内存", HintType.Green)
            End If
        End If
        Return Freed
    End Function

    ''' <summary>
    ''' 实际执行内存优化：回收托管内存，并清空所有可访问进程的工作集。
    ''' 该操作会让系统在随后一小段时间内重新从磁盘读取数据，机械硬盘上可能出现卡顿。
    ''' </summary>
    ''' <returns>各进程「专用工作集」实际减少的字节数之和。</returns>
    Public Function MemoryOptimizeInternal(Optional ShowHint As Boolean = True, Optional OnProgress As Action(Of Integer, Integer) = Nothing) As Long
        '先回收自身托管内存
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        GC.WaitForPendingFinalizers()
        '统计口径：各进程「专用工作集」的减少量之和。
        '不能用整个工作集：DLL、运行时、共享内存这些共享页会在几十个进程里各算一次，
        '第一次清理时这类软页最多，累加出来能比机器实际占用还大（32 GB 的机器算出 14.53 GB 就是这么来的），
        '再清一次就没什么可清的了，所以表现为「只有第一次的数字离谱」。
        '也不能用「系统可用内存的增量」：清出来的私有页先进 modified / standby 列表等写回页面文件，
        '短期内并不计入可用内存（实测清掉 700 MB 工作集，可用内存只涨了 59 MB）。
        Dim Freed As Long = 0
        Dim TrimmedCount As Integer = 0
        Dim AllProcesses As Process() = Process.GetProcesses()
        Dim Total As Integer = AllProcesses.Length
        Dim Done As Integer = 0
        For Each Proc As Process In AllProcesses
            Try
                Dim Before As Long = PrivateWorkingSetOf(Proc) '-1 表示读不到
                Dim Trimmed As Boolean = False
                Try
                    Trimmed = EmptyWorkingSet(Proc.Handle)
                Catch ex As Exception
                    '读不到的进程同样清理不了，跳过
                End Try
                If Trimmed Then
                    TrimmedCount += 1
                    If Before >= 0 Then
                        Dim After As Long = PrivateWorkingSetOf(Proc)
                        If After >= 0 AndAlso After < Before Then Freed += Before - After
                    End If
                End If
            Catch ex As Exception
                '系统进程等无法访问的进程直接跳过
            Finally
                Try
                    Proc.Dispose()
                Catch
                End Try
                Done += 1
                '每处理一批上报一次进度，避免频繁跨线程调度拖慢整体速度
                If OnProgress IsNot Nothing AndAlso Done Mod 24 = 0 Then OnProgress(Done, Total)
            End Try
        Next
        If OnProgress IsNot Nothing Then OnProgress(Total, Total)
        '可用内存只写进日志做对照：清理出的页要写回页面文件后才算「可用」，与释放量不是一回事
        Dim AvailAfter As Long = AvailableBytes()
        Try
            Logger.Info($"内存优化完成，已清理 {TrimmedCount} 个进程的工作集，专用工作集减少 {FormatSize(Freed)}（当前系统可用内存 {FormatSize(AvailAfter)}）")
        Catch
            '启动早期（--memory 参数）日志尚未初始化
        End Try
        Return Freed
    End Function

    ''' <summary>
    ''' 系统当前可用的物理内存（字节）。读取失败时返回 0，避免异常打断优化流程。
    ''' </summary>
    Private Function AvailableBytes() As Long
        Try
            Return CLng(My.Computer.Info.AvailablePhysicalMemory)
        Catch
            Return 0
        End Try
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
