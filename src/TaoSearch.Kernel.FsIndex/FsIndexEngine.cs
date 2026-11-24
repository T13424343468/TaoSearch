using System.Diagnostics;
using System.Runtime.CompilerServices;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Kernel.FsIndex;

public delegate MftScanOutcome MftScanDelegate(string volumeRoot, MftScanner.RecordSink sink, CancellationToken ct);

public delegate IAsyncEnumerable<FallbackEntry> FallbackEnumerateDelegate(string root, CancellationToken ct);

/// <summary>慢路径枚举条目（全路径 + 属性；慢路径无 NTFS 引用号，由引擎合成）。</summary>
public readonly record struct FallbackEntry(string FullPath, long Size, DateTimeOffset ModifiedUtc);

/// <summary>首建结果（模式与耗时，供基准口径 §5 记录）。</summary>
public sealed record FsIndexBuildResult
{
    public required long FileCount { get; init; }
    public required bool UsedFallback { get; init; }
    public required long ElapsedMilliseconds { get; init; }
}

/// <summary>索引运行状态（壳层设置页与 IPC GetIndexStatus 的数据源）。</summary>
public sealed record FsIndexStatus
{
    public required long FileCount { get; init; }
    public required bool Ready { get; init; }
    public required bool Building { get; init; }
    /// <summary>首建是否被用户暂停（0.1.0.18）。</summary>
    public bool Paused { get; init; }
    public required bool UsedFallback { get; init; }
    public required long AllocatedBytes { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// 文件名索引编排引擎（架构 §4）：首建 MFT 全量枚举 → NameIndex；USN 事件增量 Upsert/Remove 闭环。
/// MFT 枚举需管理员或卷读权限（knowledge.md 权限模型）：枚举失败（含权限不足）自动降级目录递归慢路径，
/// UsedFallback 状态标志供壳层提示与反向用例断言；慢路径合成负数引用号、名字存全路径。
/// 两个 delegate 为测试注入点（沙箱无 NTFS，云端 Windows 才能跑真扫描）。
/// </summary>
public sealed class FsIndexEngine : IDisposable
{
    private readonly NameIndex _index;
    private readonly MftScanDelegate? _mftScanOverride;
    private readonly FallbackEnumerateDelegate? _fallbackOverride;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<Task> _listenerTasks = [];

    private string _volumePrefix = string.Empty;   // MFT 父链重建缺盘符，查询路径补卷前缀，如 "C:\"
    private bool _ready;
    private bool _building;
    private bool _usedFallback;
    private string? _lastError;

    // 首建暂停（真机实锤 0.1.0.18：暂停按钮此前只停内容管线，文件名扫描照跑）。
    // PausePoint 在扫描循环内协作等待：暂停期间不再收录新文件，Resume 后原地继续。
    private readonly object _pauseGate = new();
    private volatile bool _fsPaused;

    /// <summary>事件日志写入点（M3 持久化）：先写后应用，强杀后重放不丢事件；恢复重放路径不挂 sink。</summary>
    public IJournalSink? JournalSink { get; set; }

    public FsIndexEngine(MftScanDelegate? mftScanOverride = null, FallbackEnumerateDelegate? fallbackOverride = null, IJournalSink? journalSink = null)
    {
        _index = new NameIndex();
        _mftScanOverride = mftScanOverride;
        _fallbackOverride = fallbackOverride;
        JournalSink = journalSink;
    }

    /// <summary>快照恢复专用：以已恢复的索引为底座（重放路径不重复落日志）。</summary>
    public FsIndexEngine(NameIndex restoredIndex)
    {
        _index = restoredIndex;
    }

    /// <summary>
    /// 清空索引（重建口径）：先清后建，保证重建反映磁盘现状（关7实弹：停机期删除的文件重建后不得残留）。
    /// 清空后 Ready 置否，由随后的 BuildAsync 重新就绪。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _ready = false;
        }
    }

    /// <summary>暂停首建扫描：扫描循环在下一个收录点停靠（0.1.0.18）。</summary>
    public void Pause() => _fsPaused = true;

    /// <summary>恢复首建扫描：唤醒停靠中的扫描循环，原地继续。</summary>
    public void Resume()
    {
        _fsPaused = false;
        lock (_pauseGate) Monitor.PulseAll(_pauseGate);
    }

    /// <summary>首建是否处于暂停态（壳层状态展示用）。</summary>
    public bool IsPaused => _fsPaused;

    /// <summary>扫描循环暂停点：暂停态下阻塞等待，恢复或取消后返回。</summary>
    private void PausePoint(CancellationToken ct)
    {
        if (!_fsPaused) return;
        lock (_pauseGate)
        {
            while (_fsPaused)
            {
                if (Monitor.Wait(_pauseGate, 300)) break;
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>首建：MFT 全量枚举入索引；失败（权限不足等）降级慢路径。完成后状态 Ready。</summary>
    public async Task<FsIndexBuildResult> BuildAsync(string volumeRoot, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        lock (_gate)
        {
            _building = true;
            _lastError = null;
        }

        try
        {
            _volumePrefix = NormalizePrefix(volumeRoot);
            var scan = _mftScanOverride ?? ((root, sink, token) => new MftScanner().Scan(root, sink, token));
            var outcome = scan(
                volumeRoot,
                (fileRef, parentRef, name) =>
                {
                    PausePoint(ct);
                    _index.Upsert(fileRef, parentRef, name, 0, DateTimeOffset.MinValue);
                },
                ct);

            if (outcome.Success)
            {
                lock (_gate) _usedFallback = false;
            }
            else
            {
                lock (_gate)
                {
                    _usedFallback = true;
                    _lastError = $"MFT 枚举不可用（Win32Error={outcome.Win32Error}），已降级目录递归慢路径";
                }
                await BuildFallbackAsync(volumeRoot, ct).ConfigureAwait(false);
            }

            lock (_gate) _ready = true;
            bool usedFallback;
            lock (_gate) usedFallback = _usedFallback;
            return new FsIndexBuildResult
            {
                FileCount = _index.Count,
                UsedFallback = usedFallback,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
            };
        }
        finally
        {
            lock (_gate) _building = false;
        }
    }

    /// <summary>挂接事件源（USN 监听或测试桩），后台消费并增量维护索引。</summary>
    public void AttachListener(IFileEventSource source, FileEventFilter? filter = null)
    {
        var token = _cts.Token;
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in source.SubscribeAsync(filter ?? new FileEventFilter(), token)
                                    .WithCancellation(token).ConfigureAwait(false))
                {
                    ApplyEvent(e);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
        lock (_gate) _listenerTasks.Add(task);
    }

    /// <summary>USN 事件入索引：删除 → Remove；建/改名/内容变更 → Upsert（重命名同引用号覆盖旧名）。
    /// 挂了日志点则先写 journal 再应用（先写后应用：崩溃后重放幂等，不丢事件）。</summary>
    public void ApplyEvent(FileEvent e)
    {
        JournalSink?.Write(e);
        if (e.Kind == FileChangeKind.Deleted)
        {
            _index.Remove(e.FileReference);
            return;
        }
        _index.Upsert(e.FileReference, e.ParentReference ?? 0, e.Path, 0, e.UtcTimestamp);
    }

    /// <summary>索引快照落盘（M3 持久化，格式见 NameIndexSnapshot）。</summary>
    public void SaveSnapshot(Stream output) => _index.SaveSnapshot(output);

    /// <summary>恢复收尾：补卷前缀并置 Ready（快照+journal 重放完成后调用）。</summary>
    public void MarkRestored(string volumeRoot)
    {
        _volumePrefix = NormalizePrefix(volumeRoot);
        lock (_gate) _ready = true;
    }

    /// <summary>文件名子串搜索，返回（引用号, 完整路径）。慢路径记录名即全路径，MFT 路径补卷前缀。</summary>
    public IEnumerable<(long FileReference, string Path)> Search(
        string term, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        bool usedFallback;
        string prefix;
        lock (_gate)
        {
            usedFallback = _usedFallback;
            prefix = _volumePrefix;
        }
        foreach (var fileRef in _index.Search(term, comparison))
        {
            string relative = _index.BuildPath(fileRef);
            yield return (fileRef, usedFallback || relative.Length == 0 ? relative : prefix + relative);
        }
    }

    /// <summary>
    /// 按引用号取文件元数据（路径/大小/修改时间），供查询过滤器与全文命中回填（M4 引擎接线）。
    /// 慢路径记录名即全路径，MFT 路径补卷前缀，与 <see cref="Search"/> 口径一致。
    /// </summary>
    public bool TryGetInfo(long fileReference, out string path, out long size, out DateTimeOffset modifiedUtc)
    {
        path = string.Empty;
        size = 0;
        modifiedUtc = DateTimeOffset.MinValue;
        bool usedFallback;
        string prefix;
        lock (_gate)
        {
            usedFallback = _usedFallback;
            prefix = _volumePrefix;
        }
        if (!_index.TryGet(fileReference, out var rec) || rec.Deleted) return false;
        string relative = _index.BuildPath(fileReference);
        path = usedFallback || relative.Length == 0 ? relative : prefix + relative;
        size = rec.Size;
        modifiedUtc = rec.ModifiedUtc;
        return true;
    }

    /// <summary>现存文件「引用号+完整路径」全集枚举（M4 内容索引用：重建时喂给 ContentIndexer，避免逐事件回查）。</summary>
    public IEnumerable<(long FileReference, string Path)> EnumerateLivePaths()
    {
        bool usedFallback;
        string prefix;
        lock (_gate)
        {
            usedFallback = _usedFallback;
            prefix = _volumePrefix;
        }
        foreach (var (fileRef, _, _, _, _) in _index.EnumerateLive())
        {
            string relative = _index.BuildPath(fileRef);
            yield return (fileRef, usedFallback || relative.Length == 0 ? relative : prefix + relative);
        }
    }

    public FsIndexStatus Snapshot()
    {
        lock (_gate)
        {
            return new FsIndexStatus
            {
                FileCount = _index.Count,
                Ready = _ready,
                Building = _building,
                Paused = _fsPaused,
                UsedFallback = _usedFallback,
                AllocatedBytes = _index.AllocatedBytes,
                LastError = _lastError,
            };
        }
    }

    private async Task BuildFallbackAsync(string volumeRoot, CancellationToken ct)
    {
        var enumerate = _fallbackOverride ?? DefaultFallbackEnumerate;
        long syntheticRef = -1;
        await foreach (var entry in enumerate(volumeRoot, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            PausePoint(ct);
            // 慢路径无 NTFS 引用号：合成负数引用号，parent=0，名字存全路径（BuildPath 直接返回）
            _index.Upsert(syntheticRef--, 0, entry.FullPath, entry.Size, entry.ModifiedUtc);
        }
    }

    private static async IAsyncEnumerable<FallbackEntry> DefaultFallbackEnumerate(
        string root, [EnumeratorCancellation] CancellationToken ct)
    {
        var top = new DirectoryInfo(root);
        if (!top.Exists) yield break;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(top);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();

            DirectoryInfo[] subDirs = [];
            try { subDirs = current.EnumerateDirectories().ToArray(); }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }

            foreach (var dir in subDirs)
            {
                pending.Push(dir);
                yield return new FallbackEntry(dir.FullName, 0, dir.LastWriteTimeUtc);
            }

            FileInfo[] files = [];
            try { files = current.EnumerateFiles().ToArray(); }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }

            foreach (var file in files)
                yield return new FallbackEntry(file.FullName, file.Length, file.LastWriteTimeUtc);

            await Task.Yield();
        }
    }

    private static string NormalizePrefix(string volumeRoot)
    {
        if (string.IsNullOrEmpty(volumeRoot)) return string.Empty;
        return volumeRoot.EndsWith(':') ? volumeRoot + "\\" : volumeRoot;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll([.. _listenerTasks], 2000); }
        catch (AggregateException) { }
        catch (ArgumentOutOfRangeException) { }
        _cts.Dispose();
        _index.Dispose();
    }
}
