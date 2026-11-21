using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FullText;

namespace TaoSearch.Service;

/// <summary>
/// 内容索引管线（架构 §5，M4 引擎接线）：文件事件入队 → 后台低优先级消费 →
/// 文本尽力读取（UTF-8，限长）→ 分词 → 倒排索引。docId = NTFS 文件引用号，与文件名索引同键。
/// 决议口径：命中位置不落盘（预览走文档重扫，见 SearchEngine）；segment 合并不做；落盘限速由
/// FullTextPersistence（ThrottledWriteStream 10MB/s）承担，本类只管内存索引。
/// 内置文本扩展名白名单 + 外部解析器登记扩展名（ParserRegistry）；二进制大文件跳过并记失败清单。
/// Pause/Resume 支持设置页「暂停全文索引」；Deleted 事件直接移除倒排条目；Renamed 不动（docId 不变）。
/// </summary>
public sealed class ContentIndexer : IDisposable
{
    /// <summary>内置文本扩展名白名单（带点小写；代码与配置类文本，PDF/Office 自研解析走后续版本）。</summary>
    public static readonly IReadOnlyList<string> DefaultExtensions =
    [
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".html", ".htm", ".ini", ".config",
        ".yml", ".yaml", ".sql", ".cs", ".js", ".ts", ".py", ".java", ".c", ".cpp", ".h",
        ".go", ".rs", ".sh", ".bat", ".ps1",
    ];

    /// <summary>单文件索引上限（字节）：超过跳过，防大文件拖垮后台（轻量口径）。</summary>
    public const long MaxIndexBytes = 2 * 1024 * 1024;

    /// <summary>失败清单保留条数（环形丢弃最旧）。</summary>
    public const int FailedKeepCount = 100;

    private const int QueueCapacity = 4096;

    private readonly InvertedIndex _index;
    private readonly ParserRegistry _parsers;
    private readonly Channel<FileEvent> _queue = Channel.CreateBounded<FileEvent>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _failed = new();
    private readonly ManualResetEventSlim _resumeGate = new(true);
    private volatile bool _paused;
    private int _pending;

    public ContentIndexer(InvertedIndex index, ParserRegistry? parsers = null)
    {
        _index = index;
        _parsers = parsers ?? new ParserRegistry();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>事件入队（服务事件泵调用；Deleted 也入队，由消费循环统一处理）。</summary>
    public void Enqueue(FileEvent e)
    {
        Interlocked.Increment(ref _pending);
        // 队列满时同步等待：事件源是低频 USN 流，背压优于丢事件（丢事件=索引漏文件）
        _queue.Writer.TryWrite(e);
    }

    public void Pause()
    {
        _paused = true;
        _resumeGate.Reset();
    }

    public void Resume()
    {
        _paused = false;
        _resumeGate.Set();
    }

    public bool IsPaused => _paused;

    /// <summary>待处理事件数（壳层设置页与 IPC GetIndexStatus 用）。</summary>
    public int PendingCount => Volatile.Read(ref _pending);

    /// <summary>内容索引就绪口径：队列已清空且未暂停（重建完成后同样成立）。</summary>
    public bool IsReady => !_paused && PendingCount == 0;

    /// <summary>解析失败文件清单快照（最多 <see cref="FailedKeepCount"/> 条，设置页展示）。</summary>
    public IReadOnlyList<string> FailedFiles => [.. _failed];

    /// <summary>
    /// 重建内容索引：清空倒排后按文件名索引现存的「引用号+路径」全集重新入队
    /// （RebuildIndex Scope=Content 走此路；文件集由宿主从 FsIndexEngine.EnumerateLivePaths 取，
    /// 不做逐事件引用号回查，避免重建期 O(n²) 扫描）。
    /// </summary>
    public void Rebuild(IEnumerable<(long FileReference, string Path)> files, CancellationToken ct = default)
    {
        _index.Clear();
        foreach (var (fileRef, path) in files)
        {
            ct.ThrowIfCancellationRequested();
            Enqueue(new FileEvent
            {
                Kind = FileChangeKind.Created,
                FileReference = fileRef,
                Path = path,
                UtcTimestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                while (_paused && !ct.IsCancellationRequested) _resumeGate.Wait(ct);
                ct.ThrowIfCancellationRequested();
                try
                {
                    ProcessEvent(e);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    RecordFailed(e.Path, ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ProcessEvent(FileEvent e)
    {
        if (e.Kind == FileChangeKind.Deleted)
        {
            _index.RemoveDocument(e.FileReference);
            return;
        }
        if (e.Kind == FileChangeKind.Renamed) return; // docId 不变，内容索引仍有效

        IndexFile(e.FileReference, e.Path, e.UtcTimestamp);
    }

    private void IndexFile(long fileRef, string path, DateTimeOffset modifiedUtc)
    {
        string ext = Path.GetExtension(path);
        bool known = DefaultExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || _parsers.IsKnownExtension(ext);
        if (!known) return;

        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception ex) { RecordFailed(path, ex); return; }
        if (length > MaxIndexBytes) { RecordFailed(path, new IOException($"文件超过索引上限 {MaxIndexBytes} 字节")); return; }

        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) { RecordFailed(path, ex); return; }

        _index.AddDocument(fileRef, text, modifiedUtc);
    }

    private void RecordFailed(string path, Exception ex)
    {
        _failed.Enqueue($"{path} ({ex.Message})");
        while (_failed.Count > FailedKeepCount) _failed.TryDequeue(out _);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _resumeGate.Dispose();
    }
}
