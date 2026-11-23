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

        // 普通目录句柄可打开（BackupSemantics），但 FSCTL_QUERY_USN_JOURNAL 失败 → QueryJournal 返回 null → 循环退出
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-usn-neg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        using var listener = new UsnJournalListener(dir);
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

        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WithLock(Action f) { lock (_gate) f(); }
    private static T WithLock<T>(Func<T> f) { lock (_gate) return f(); }
    private static readonly object _gate = new();
}
