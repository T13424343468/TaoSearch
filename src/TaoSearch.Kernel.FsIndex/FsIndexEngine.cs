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
    private readonly NameIndex _index = new();
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

    public FsIndexEngine(MftScanDelegate? mftScanOverride = null, FallbackEnumerateDelegate? fallbackOverride = null)
    {
        _mftScanOverride = mftScanOverride;
        _fallbackOverride = fallbackOverride;
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
                (fileRef, parentRef, name) => _index.Upsert(fileRef, parentRef, name, 0, DateTimeOffset.MinValue),
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

    /// <summary>USN 事件入索引：删除 → Remove；建/改名/内容变更 → Upsert（重命名同引用号覆盖旧名）。</summary>
    public void ApplyEvent(FileEvent e)
    {
        if (e.Kind == FileChangeKind.Deleted)
        {
            _index.Remove(e.FileReference);
            return;
        }
        _index.Upsert(e.FileReference, e.ParentReference ?? 0, e.Path, 0, e.UtcTimestamp);
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

    public FsIndexStatus Snapshot()
    {
        lock (_gate)
        {
            return new FsIndexStatus
            {
                FileCount = _index.Count,
                Ready = _ready,
                Building = _building,
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
