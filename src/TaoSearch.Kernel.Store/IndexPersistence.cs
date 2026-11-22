using System.Diagnostics;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;

namespace TaoSearch.Kernel.Store;

/// <summary>
/// 引擎事件日志挂点（IJournalSink 实现）：串行化写入 + 断点轮转。
/// 断点（checkpoint）= 快照原子落盘 + journal 清空重开；恢复 = 快照加载 + journal 重放（T3/T4 口径）。
/// </summary>
public sealed class JournalSink : IJournalSink, IDisposable
{
    private readonly object _gate = new();
    private readonly string _journalPath;
    private JournalWriter? _writer;
    private bool _disposed;

    public JournalSink(string journalPath) => _journalPath = journalPath;

    public void Write(FileEvent e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer ??= JournalWriter.OpenAppend(_journalPath);
            if (e.Kind == FileChangeKind.Deleted)
                _writer.AppendRemove(e.FileReference);
            else
                _writer.AppendUpsert(e.FileReference, e.ParentReference ?? 0, e.Path, 0, e.UtcTimestamp);
        }
    }

    /// <summary>轮转：丢弃当前 journal（返回丢弃字节数）并清空文件，下次写入重开。快照落盘成功后调用。</summary>
    public long Rotate()
    {
        lock (_gate)
        {
            if (_disposed) return 0;
            long discarded = 0;
            if (_writer != null)
            {
                if (File.Exists(_journalPath)) discarded = new FileInfo(_journalPath).Length;
                _writer.Dispose();
                _writer = null;
            }
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            return discarded;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer?.Dispose();
            _writer = null;
            _disposed = true;
        }
    }
}

/// <summary>断点结果（基准口径 §4：快照体积与耗时记录）。</summary>
public sealed record CheckpointResult
{
    public required long FileCount { get; init; }
    public required long SnapshotBytes { get; init; }
    public required long JournalDiscardedBytes { get; init; }
    public required long ElapsedMilliseconds { get; init; }
}

/// <summary>恢复结果（T3 快照恢复 / T4 强杀恢复口径回填用）。</summary>
public sealed record RestoreResult
{
    public required long FileCount { get; init; }
    public required bool SnapshotUsed { get; init; }
    public required int JournalFramesApplied { get; init; }
    public required long JournalTruncatedTailBytes { get; init; }
    public required long ElapsedMilliseconds { get; init; }
}

/// <summary>
/// 持久化编排（M3，架构 §4.3）：断点 = 快照临时文件写全 → 原子替换 → journal 轮转；
/// 恢复 = 快照加载（无快照则全量重建）→ journal 重放（撕裂尾容忍）。重放路径不挂 sink，不重复落日志。
/// </summary>
public static class IndexPersistence
{
    public static CheckpointResult SaveCheckpoint(FsIndexEngine engine, string snapshotPath, JournalSink journal)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(journal);
        var sw = Stopwatch.StartNew();

        string dir = Path.GetDirectoryName(Path.GetFullPath(snapshotPath)) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = snapshotPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            engine.SaveSnapshot(fs);
        }
        if (File.Exists(snapshotPath)) File.Replace(tmp, snapshotPath, null);
        else File.Move(tmp, snapshotPath);

        long discarded = journal.Rotate();
        sw.Stop();
        return new CheckpointResult
        {
            FileCount = engine.Snapshot().FileCount,
            SnapshotBytes = new FileInfo(snapshotPath).Length,
            JournalDiscardedBytes = discarded,
            ElapsedMilliseconds = sw.ElapsedMilliseconds,
        };
    }

    /// <param name="engine">可选目标引擎：调用方已装载快照时传入，journal 直接重放到该引擎（服务宿主装配路径）。</param>
    public static RestoreResult Restore(string snapshotPath, string journalPath, string volumeRoot, FsIndexEngine? engine = null)
    {
        var sw = Stopwatch.StartNew();
        bool usedSnapshot = false;
        FsIndexEngine target;

        if (engine is not null)
        {
            target = engine;
            usedSnapshot = true;
        }
        else if (File.Exists(snapshotPath))
        {
            // FileShare.ReadWrite：容忍活跃写者（Windows 下 FileShare.Read 会拒绝并发写句柄）
            using (var fs = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var index = NameIndex.LoadSnapshot(fs);
                target = new FsIndexEngine(index);
                usedSnapshot = true;
            }
        }
        else
        {
            target = new FsIndexEngine();
        }

        target.MarkRestored(volumeRoot);

        int frames = 0;
        long torn = 0;
        if (File.Exists(journalPath))
        {
            // FileShare.ReadWrite：sink 未轮转时写句柄仍持有 journal，恢复侧只读不排斥
            using (var fs = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                try
                {
                    var replay = JournalWriter.Replay(fs, e =>
                    {
                        target.ApplyEvent(new FileEvent
                        {
                            Kind = e.IsDelete ? FileChangeKind.Deleted : FileChangeKind.ContentModified,
                            FileReference = e.FileReference,
                            Path = e.Name,
                            UtcTimestamp = e.ModifiedUtc,
                            ParentReference = e.ParentReference,
                        });
                        frames++;
                    });
                    torn = replay.TruncatedTailBytes;
                }
                catch (InvalidDataException)
                {
                    // 关7口径：journal 整体损坏（文件头/版本非法）按撕裂尾同等对待——
                    // 保留快照与已重放前缀降级服务，绝不因日志损坏拒绝启动（帧级损坏本就不抛，此处只兜文件头）。
                }
            }
        }

        sw.Stop();
        return new RestoreResult
        {
            FileCount = target.Snapshot().FileCount,
            SnapshotUsed = usedSnapshot,
            JournalFramesApplied = frames,
            JournalTruncatedTailBytes = torn,
            ElapsedMilliseconds = sw.ElapsedMilliseconds,
        };
    }
}
