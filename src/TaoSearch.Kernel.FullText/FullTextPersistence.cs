using System.Buffers.Binary;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Kernel.FullText;

/// <summary>
/// 全文持久化编排（对齐 IndexPersistence）：断点 = 快照临时文件原子替换 + journal 轮转；
/// 恢复 = 快照加载（无快照则空索引）→ journal 重放（撕裂尾容忍）。
/// 缓存预算恒定 6MB（架构 E-02 决议）：热词条缓存超预算即淘汰，主体落盘。
/// </summary>
public static class FullTextPersistence
{
    /// <summary>热缓存预算：6MB（架构 E-02 决议，恒定不随语料增长）。</summary>
    public const long CacheBudgetBytes = 6L * 1024 * 1024;

    public static void SaveCheckpoint(InvertedIndex index, string snapshotPath, Stream journal)
    {
        ArgumentNullException.ThrowIfNull(index);
        string dir = Path.GetDirectoryName(Path.GetFullPath(snapshotPath)) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = snapshotPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            index.SaveSnapshot(fs);
        }
        if (File.Exists(snapshotPath)) File.Replace(tmp, snapshotPath, null);
        else File.Move(tmp, snapshotPath);
        journal.SetLength(0);   // 轮转：快照已含全量，journal 清空
        journal.Flush();
    }

    public static (InvertedIndex Index, int FramesApplied, long TruncatedTailBytes) Restore(string snapshotPath, Stream journal)
    {
        InvertedIndex index;
        if (File.Exists(snapshotPath))
        {
            // 快照读入内存后即释放句柄：Windows 上不释放会占住文件，导致目录清理失败（云 CI 实测暴露）
            using var snapStream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            index = InvertedIndex.LoadSnapshot(snapStream);
        }
        else
        {
            index = new InvertedIndex();
        }

        int frames = 0;
        long torn = 0;
        var replay = FullTextJournal.Replay(journal, e =>
        {
            if (e.Text is null) index.RemoveDocument(e.DocId);
            else index.AddDocument(e.DocId, e.Text, e.ModifiedUtc);
        });
        frames = replay.FramesApplied;
        torn = replay.TruncatedTailBytes;
        return (index, frames, torn);
    }
}

/// <summary>
/// 热词条缓存（6MB 预算，E-02）：缓存高频查询词条的倒排结果，超预算按 LRU 淘汰。
/// 主体倒排在磁盘，缓存只保热点，内存恒定不随语料增长。
/// </summary>
public sealed class HotTermCache
{
    private readonly long _budgetBytes;
    private long _usedBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();

    private sealed class Entry(string term, IReadOnlyList<(long DocId, int TermFrequency)> value)
    {
        public readonly string Term = term;
        public readonly IReadOnlyList<(long DocId, int TermFrequency)> Value = value;
        public long SizeBytes;
    }

    public HotTermCache(long budgetBytes = FullTextPersistence.CacheBudgetBytes) => _budgetBytes = budgetBytes;

    public bool TryGet(string term, out IReadOnlyList<(long DocId, int TermFrequency)> value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(term, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = [];
            return false;
        }
    }

    public void Put(string term, IReadOnlyList<(long DocId, int TermFrequency)> value)
    {
        long size = 8 + (long)term.Length * 2 + (long)value.Count * 16;
        lock (_gate)
        {
            if (_map.TryGetValue(term, out var existing))
            {
                _usedBytes -= existing.Value.SizeBytes;
                _lru.Remove(existing);
                _map.Remove(term);
            }
            if (size > _budgetBytes) return;   // 单条超预算不缓存
            while (_usedBytes + size > _budgetBytes && _lru.Count > 0)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _usedBytes -= last.Value.SizeBytes;
                _map.Remove(last.Value.Term);
            }
            var entry = new Entry(term, value) { SizeBytes = size };
            var node = new LinkedListNode<Entry>(entry);
            _lru.AddFirst(node);
            _map[term] = node;
            _usedBytes += size;
        }
    }

    public long UsedBytes { get { lock (_gate) return _usedBytes; } }
    public int Count { get { lock (_gate) return _map.Count; } }
}
