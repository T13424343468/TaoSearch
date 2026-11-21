using System.Collections.Concurrent;

namespace TaoSearch.Kernel.FullText;

/// <summary>
/// 内存倒排索引（架构 §5.2 的最小可用版）：
/// 词 → 文档集合；文档 → 词频与位置（供相关度打分与命中段落回读）。
/// 生产版按段（segment）落盘压缩，此版为正确性基线，接口保持不变。
/// </summary>
public sealed partial class InvertedIndex
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Posting>> _postings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, DocMeta> _docs = new();

    public void AddDocument(long docId, string text, DateTimeOffset modifiedUtc)
    {
        RemoveDocument(docId);
        var meta = new DocMeta(docId, modifiedUtc);
        foreach (var token in Tokenizer.Tokenize(text))
        {
            var bucket = _postings.GetOrAdd(token.Value, _ => new ConcurrentDictionary<long, Posting>());
            var posting = bucket.GetOrAdd(docId, _ => new Posting(docId));
            posting.Positions.Add(token.Offset);
            meta.TermCount++;
        }
        _docs[docId] = meta;
    }

    public void RemoveDocument(long docId)
    {
        if (!_docs.TryRemove(docId, out _)) return;
        foreach (var (_, bucket) in _postings)
            bucket.TryRemove(docId, out _);
    }

    /// <summary>清空全部文档（M4 重建索引用；词表容量随字典释放，重建后重新填充）。</summary>
    public void Clear()
    {
        _postings.Clear();
        _docs.Clear();
    }

    /// <summary>查词返回命中文档与词频（词频作相关度初值）。</summary>
    public IReadOnlyList<(long DocId, int TermFrequency)> Lookup(string term)
    {
        if (!_postings.TryGetValue(term, out var bucket)) return [];
        return bucket.Select(kv => (kv.Key, kv.Value.Positions.Count)).ToArray();
    }

    /// <summary>多词 AND 查询：取交集，词频求和作相关度。</summary>
    public IReadOnlyList<(long DocId, double Score)> LookupAll(IEnumerable<string> terms)
    {
        Dictionary<long, double>? acc = null;
        foreach (var term in terms)
        {
            var hits = Lookup(term);
            if (acc is null)
            {
                acc = hits.ToDictionary(h => h.DocId, h => (double)h.TermFrequency);
                continue;
            }
            var next = new Dictionary<long, double>();
            foreach (var (docId, tf) in hits)
                if (acc.TryGetValue(docId, out var score)) next[docId] = score + tf;
            acc = next;
            if (acc.Count == 0) break;
        }
        return acc?.Select(kv => (kv.Key, kv.Value)).ToArray() ?? [];
    }

    /// <summary>回读命中位置（供命中段落预览取上下文）。</summary>
    public IReadOnlyList<int> GetPositions(long docId, string term) =>
        _postings.TryGetValue(term, out var bucket) && bucket.TryGetValue(docId, out var posting)
            ? posting.Positions.ToArray()
            : [];

    public long DocCount => _docs.Count;

    private sealed class Posting(long docId)
    {
        public long DocId { get; } = docId;
        public List<int> Positions { get; } = [];
    }

    private sealed record DocMeta(long DocId, DateTimeOffset ModifiedUtc)
    {
        public int TermCount { get; set; }
    }
}
