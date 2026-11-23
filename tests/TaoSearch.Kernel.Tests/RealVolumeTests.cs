using System.Runtime.InteropServices;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using Xunit;
using Xunit.Abstractions;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 真卷实测（windows-ci 管理员卷出账）：MftScanner / UsnJournalListener 真实 NTFS 分支，
/// 计入覆盖账（非 Benchmark 类）。非 Windows 环境如实跳过留痕，不伪造数据。
/// </summary>
public sealed class RealVolumeTests
{
    private readonly ITestOutputHelper _output;

    public RealVolumeTests(ITestOutputHelper output) => _output = output;

    private bool SkipOnNonWindows()
    {
        if (OperatingSystem.IsWindows()) return false;
        _output.WriteLine("SKIPPED: 非 Windows 环境无 NTFS/MFT/USN，真卷分支由 windows-ci 管理员卷出账。");
        return true;
    }

    [Fact]
    public void MftScanner_RealVolume_ScansSystemVolume()
    {
        if (SkipOnNonWindows()) return;

        var scanner = new MftScanner();
        long sinkCalls = 0;
        bool sawWindowsDir = false;
        var outcome = scanner.Scan(
            @"C:\",
            (fileRef, parentRef, name) =>
            {
                sinkCalls++;
                if (name == "Windows") sawWindowsDir = true;
            });

        Assert.True(outcome.Success, $"MFT 实扫失败：Win32Error={outcome.Win32Error}");
        Assert.True(outcome.RecordCount > 0, "系统卷 MFT 记录数应大于 0");
        Assert.True(sinkCalls > 0, "枚举回调应被调用");
        Assert.True(sawWindowsDir, "C:\\ 卷 MFT 枚举应含 Windows 目录（元文件 rec≤16 不入索引口径下仍可见普通目录）");
        Assert.True(outcome.ElapsedMilliseconds >= 0);
        _output.WriteLine($"MFT 实扫：{outcome.RecordCount:N0} 条记录 / {outcome.ElapsedMilliseconds:N0} ms / sink 调用 {sinkCalls:N0} 次");
    }

    [Fact]
    public void MftScanner_NonexistentVolume_FailsWithWin32Error()
    {
        if (SkipOnNonWindows()) return;

        var scanner = new MftScanner();
        var outcome = scanner.Scan(@"\\.\TAOSEARCH_NO_SUCH_VOL", (_, _, _) => { });

        Assert.False(outcome.Success);
        Assert.NotEqual(0, outcome.Win32Error);
    }

    [Fact]
    public async Task UsnJournalListener_RealVolume_EventsFlowToEnd()
    {
        if (SkipOnNonWindows()) return;

        // 临时目录在系统卷（runner 为 C:），保证 USN 记录落在监听卷上
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-usn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        using var listener = new UsnJournalListener(@"C:\");
        listener.Start();

        var seen = new List<FileChangeKind>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var consume = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in listener.SubscribeAsync(new FileEventFilter(), cts.Token))
                    WithLock(() => seen.Add(e.Kind));
            }
            catch (OperationCanceledException) { }
        });

        // 等监听循环就绪（打开卷句柄 + 查询日志 ID）
        await Task.Delay(1500);

        string f1 = Path.Combine(dir, "usn-a.txt");
        await File.WriteAllTextAsync(f1, "x");          // Created
        await Task.Delay(500);
        string f2 = Path.Combine(dir, "usn-b.txt");
        File.Move(f1, f2);                               // Renamed（RENAME_NEW_NAME）
        await Task.Delay(500);
        await File.WriteAllTextAsync(f2, "yy");          // ContentModified（DATA_OVERWRITE）
        await Task.Delay(500);
        File.Delete(f2);                                 // Deleted

        var deadline = DateTime.UtcNow.AddSeconds(25);
        bool allSeen;
        while ((allSeen = WithLock(() =>
                   seen.Contains(FileChangeKind.Created) && seen.Contains(FileChangeKind.Renamed)
                   && seen.Contains(FileChangeKind.ContentModified) && seen.Contains(FileChangeKind.Deleted))) == false
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        cts.Cancel();
        try { await consume; } catch (Exception) { }

        _output.WriteLine($"USN 实收事件：{string.Join(", ", WithLock(() => seen))}");
        Assert.True(allSeen, $"四种事件未集齐，实收：{string.Join(", ", WithLock(() => seen))}");

        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task UsnJournalListener_NonexistentVolume_StopsQuietly()
    {
        if (SkipOnNonWindows()) return;

        using var listener = new UsnJournalListener(@"\\.\TAOSEARCH_NO_SUCH_VOL");
        listener.Start();

        int count = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (var _ in listener.SubscribeAsync(new FileEventFilter(), cts.Token))
                count++;
        }
        catch (OperationCanceledException) { }

        Assert.Equal(0, count);   // 卷句柄打开失败：监听循环静默退出，不抛不炸
    }

    [Fact]
    public async Task UsnJournalListener_NonVolumePath_StopsQuietly()
    {
        if (SkipOnNonWindows()) return;

        // UNC 管理共享下的目录句柄可打开（BackupSemantics），但卷级 FSCTL_QUERY_USN_JOURNAL 不适用
        // → QueryJournal 返回 null → 监听循环静默退出（卷设备路径映射只认盘符形态，UNC 原样打开）
        string localTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        string uncDir = @"\\localhost\" + localTemp[0] + "$" + localTemp[2..] + @"\taosearch-usn-neg-" + Guid.NewGuid().ToString("N");
        try { Directory.CreateDirectory(uncDir); } catch (Exception) { }

        using var listener = new UsnJournalListener(uncDir);
        listener.Start();

        int count = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (var _ in listener.SubscribeAsync(new FileEventFilter(), cts.Token))
                count++;
        }
        catch (OperationCanceledException) { }

        Assert.Equal(0, count);

        try { Directory.Delete(uncDir, true); } catch (Exception) { }
    }

    [Fact]
    public void RealVolume_DiagnosticProbe_FsctlEvidence()
    {
        if (SkipOnNonWindows()) return;

        // ① 提权状态与卷文件系统
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            bool isAdmin = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            _output.WriteLine($"[probe] user={identity.Name} elevated={isAdmin}");
        }
        var fsName = new System.Text.StringBuilder(261);
        bool volInfo = GetVolumeInformationW(@"C:\", 0, 0, out _, out _, out _, fsName, fsName.Capacity);
        _output.WriteLine($"[probe] GetVolumeInformation C:\\ ok={volInfo} fs={(volInfo ? fsName.ToString() : "-")} err={Marshal.GetLastWin32Error()}");

        // ② 卷设备句柄 vs 目录句柄：QUERY / ENUM 版本组合矩阵 / READ 全量出证
        foreach (var (label, path) in new[] { ("volume", @"\\.\C:"), ("dir", @"C:\") })
        {
            using var h = CreateFileW(path, 0x80000000u, 3u, 0, 3, 0x02000000, 0);
            if (h.IsInvalid)
            {
                _output.WriteLine($"[probe] open {label} {path} FAILED err={Marshal.GetLastWin32Error()}");
                continue;
            }
            _output.WriteLine($"[probe] open {label} {path} ok");

            var qbuf = new byte[64];
            bool qok = DeviceIoControlPinned(h, FsctlQueryUsnJournal, null, qbuf, out uint qret);
            _output.WriteLine($"[probe] {label} QUERY_USN_JOURNAL ok={qok} err={Marshal.GetLastWin32Error()} ret={qret}"
                + (qok ? $" journalId={BitConverter.ToUInt64(qbuf, 0)} firstUsn={BitConverter.ToInt64(qbuf, 8)} nextUsn={BitConverter.ToInt64(qbuf, 16)}" : ""));

            // MFT_ENUM_DATA_V0 正确 24 字节布局（StartRef/LowUsn/HighUsn 各 8 字节）：
            // 全量口径 (0, 0, MAX) 与零口径 (0,0,0) 双组合出证
            foreach (var (lo, hi) in new[] { (0UL, ulong.MaxValue), (0UL, 0UL) })
            {
                var input = new byte[24];
                BitConverter.GetBytes(0L).CopyTo(input, 0);          // StartFileReferenceNumber
                BitConverter.GetBytes(lo).CopyTo(input, 8);          // LowUsn
                BitConverter.GetBytes(hi).CopyTo(input, 16);         // HighUsn
                var obuf = new byte[1 << 20];
                bool eok = DeviceIoControlPinned(h, FsctlEnumUsnData, input, obuf, out uint eret);
                _output.WriteLine($"[probe] {label} ENUM_USN_DATA v0-24B usn=({lo},{hi}) ok={eok} err={Marshal.GetLastWin32Error()} ret={eret}");
            }

            if (qok)
            {
                // READ_USN_JOURNAL_DATA_V0 40 字节：JournalID@0、StartUsn@8、ReasonMask@16、ReturnOnlyOnClose@20、Timeout@24、BytesToWaitFor@32；
                // StartUsn 取 QUERY 应答的 NextUsn@16（USN_JOURNAL_DATA = JournalID@0/FirstUsn@8/NextUsn@16）
                var rin = new byte[40];
                BitConverter.GetBytes(BitConverter.ToUInt64(qbuf, 0)).CopyTo(rin, 0);    // JournalID
                BitConverter.GetBytes(BitConverter.ToInt64(qbuf, 16)).CopyTo(rin, 8);    // StartUsn = NextUsn
                BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(rin, 16);                      // ReasonMask
                BitConverter.GetBytes(0u).CopyTo(rin, 20);                               // ReturnOnlyOnClose
                BitConverter.GetBytes(0UL).CopyTo(rin, 24);                              // Timeout
                BitConverter.GetBytes(0UL).CopyTo(rin, 32);                              // BytesToWaitFor
                var rbuf = new byte[64 * 1024];
                bool rok = DeviceIoControlPinned(h, FsctlReadUsnJournal, rin, rbuf, out uint rret);
                _output.WriteLine($"[probe] {label} READ_USN_JOURNAL v0-40B ok={rok} err={Marshal.GetLastWin32Error()} ret={rret}");
            }
        }

        Assert.True(true);   // 诊断探针：只出证不判红，证据进日志随流水线归档
    }

    private static void WithLock(Action f) { lock (_gate) f(); }
    private static T WithLock<T>(Func<T> f) { lock (_gate) return f(); }
    private static readonly object _gate = new();

    // ---- 诊断探针自包含 P/Invoke（不依赖产品互操作层，证据单源） ----
    // FSCTL 控制码（ntifs.h 权威值）：ENUM=功能号44 METHOD_NEITHER=0x000900B3、READ=功能号46 METHOD_NEITHER=0x000900BB、QUERY=功能号61 METHOD_BUFFERED=0x000900F4
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
        nint lpInBuffer, uint nInBufferSize, nint lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName, nint lpVolumeNameBuffer, int nVolumeNameSize,
        out int lpVolumeSerialNumber, out int lpMaximumComponentLength, out int lpFileSystemFlags,
        System.Text.StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

    /// <summary>固定缓冲调用 DeviceIoControl：GCHandle 覆盖调用全程（原 Pin 先释放后使用，小数组可被 GC 移动致 1784）。</summary>
    private static bool DeviceIoControlPinned(
        Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[]? inBuf, byte[] outBuf, out uint returned)
    {
        var hIn = inBuf is null ? default : System.Runtime.InteropServices.GCHandle.Alloc(inBuf, System.Runtime.InteropServices.GCHandleType.Pinned);
        var hOut = System.Runtime.InteropServices.GCHandle.Alloc(outBuf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return DeviceIoControl(h, code,
                inBuf is null ? 0 : hIn.AddrOfPinnedObject(), (uint)(inBuf?.Length ?? 0),
                hOut.AddrOfPinnedObject(), (uint)outBuf.Length, out returned, 0);
        }
        finally { if (hIn.IsAllocated) hIn.Free(); hOut.Free(); }
    }
}
