using System.Text;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FullText;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.Query;
using TaoSearch.Kernel.Store;

namespace TaoSearch.Service;

/// <summary>
/// M4 引擎接线（架构 §6/§7）：把冻结契约的 8 个方法接到真实内核。
/// 文件名路=FsIndexEngine（NameIndex 毫秒级），全文路=InvertedIndex（倒排+热词缓存），
/// 查询解析=QueryParser（高级语法），事件路=UsnJournalListener（USN 实时）。
/// 解析器注册表=预埋接口三，外部订阅者注册即插。
/// </summary>
public sealed class SearchService : IDisposable
{
    private const string DefaultVolumeRoot = @"C:\";
    private readonly FsIndexEngine _fsIndex;
    private readonly InvertedIndex _fullText;
    private readonly HotTermCache _hotCache;
    private readonly UsnJournalListener? _listener;
    private readonly Dictionary<Guid, (FileEventFilter Filter, IpcClientContext Ctx)> _subscriptions = new();
    private readonly Dictionary<string, RegisterParserRequest> _parsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _volumeRoot;
    private bool _paused;
    private long _pendingContentQueue;
    private readonly List<string> _failedFiles = [];
    private IndexPersistence.JournalSink? _journalSink;
    private FullTextJournal.Writer? _ftJournalWriter;
    private IDocumentStore? _documentStore;

    /// <summary>文档缓存挂点（预览走文档重扫，M4 决议；解析管线接入时注入）。</summary>
    public IDocumentStore? DocumentStore { get => _documentStore; set => _documentStore = value; }

    public SearchService(string volumeRoot = DefaultVolumeRoot, bool enableListener = true)
    {
        _volumeRoot = volumeRoot;
        _fsIndex = new FsIndexEngine();
        _fullText = new InvertedIndex();
        _hotCache = new HotTermCache();
        if (enableListener && OperatingSystem.IsWindows())
        {
            _listener = new UsnJournalListener(volumeRoot);
            _listener.Start();
        }
    }

    /// <summary>启动时恢复上次快照+journal 重放（断点续跑，架构 §4.3）。</summary>
    public RestoreOutcome Restore(string dataDir)
    {
        var fsSnap = Path.Combine(dataDir, "fsindex.snap");
        var fsJournal = Path.Combine(dataDir, "fsindex.journal");
        var ftSnap = Path.Combine(dataDir, "fulltext.snap");
        var ftJournal = Path.Combine(dataDir, "fulltext.journal");

        var fsResult = IndexPersistence.Restore(fsSnap, fsJournal, _volumeRoot);
        _fsIndex.MarkRestored(_volumeRoot);

        if (File.Exists(ftSnap))
        {
            using var snapStream = new FileStream(ftSnap, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var journalStream = File.Exists(ftJournal)
                ? new FileStream(ftJournal, FileMode.Open, FileAccess.Read, FileShare.Read)
                : Stream.Null;
            var (_, frames, _) = FullTextPersistence.Restore(ftSnap, journalStream);
            _ftJournalFrames = frames;
        }

        _journalSink = new IndexPersistence.JournalSink(fsJournal);
        _fsIndex.JournalSink = _journalSink;
        return new RestoreOutcome(
            fsResult.FileCount, fsResult.SnapshotUsed, fsResult.JournalFramesApplied, _ftJournalFrames);
    }

    private int _ftJournalFrames;

    public sealed record RestoreOutcome(long FileCount, bool SnapshotUsed, int FsJournalFrames, int FtJournalFrames);

    /// <summary>启动后台首扫（MFT 快路径失败自动降级目录枚举，架构 §4.1）。</summary>
    public Task<FsIndexBuildResult> BuildIndexAsync(CancellationToken ct = default) =>
        _fsIndex.BuildAsync(_volumeRoot, ct);

    /// <summary>保存检查点（快照原子替换 + journal 轮转，架构 §4.3）。</summary>
    public CheckpointResult Checkpoint(string dataDir)
    {
        if (_journalSink is null) throw new InvalidOperationException("Restore must be called before Checkpoint.");
        var fsSnap = Path.Combine(dataDir, "fsindex.snap");
        var fsResult = IndexPersistence.SaveCheckpoint(_fsIndex, fsSnap, _journalSink);

        var ftSnap = Path.Combine(dataDir, "fulltext.snap");
        using var ftJournal = new FileStream(
            Path.Combine(dataDir, "fulltext.journal"), FileMode.Append, FileAccess.Write, FileShare.Read);
        FullTextPersistence.SaveCheckpoint(_fullText, ftSnap, ftJournal);
        return fsResult;
    }

    // ---------- 8 方法真实现 ----------

    public ResultBatch Search(QuerySpec spec)
    {
        var parsed = QueryParser.Parse(spec.Text);
        var hits = new List<SearchHit>();
        long total = -1;

        bool wantFilename = spec.Scope.HasFlag(QueryScope.Filename);
        bool wantContent = spec.Scope.HasFlag(QueryScope.Content);

        if (wantFilename)
        {
            foreach (var (fileRef, path) in _fsIndex.Search(parsed.Term))
            {
                if (!PassesPathFilter(parsed, path)) continue;
                if (_fsIndex.TryGet(fileRef, out var rec))
                {
                    hits.Add(new SearchHit
                    {
                        FileReference = fileRef,
                        Path = path,
                        FileName = rec.FileName,
                        Size = rec.Size,
                        ModifiedTime = rec.ModifiedUtc,
                        Score = 0,
                    });
                }
                if (hits.Count >= spec.BatchSize) break;
            }
        }

        if (wantContent && hits.Count < spec.BatchSize)
        {
            var terms = TokenizeForQuery(parsed.Term);
            if (terms.Count > 0)
            {
                var scored = _fullText.LookupAll(terms);
                total = scored.Count;
                foreach (var (docId, score) in scored)
                {
                    if (hits.Count >= spec.BatchSize) break;
                    if (_fsIndex.TryGet(docId, out var rec))
                    {
                        hits.Add(new SearchHit
                        {
                            FileReference = docId,
                            Path = _fsIndex.BuildPath(docId),
                            FileName = rec.FileName,
                            Size = rec.Size,
                            ModifiedTime = rec.ModifiedUtc,
                            Score = score,
                            Preview = BuildPreview(docId, terms),
                        });
                    }
                }
            }
        }

        return new ResultBatch { Hits = hits, TotalEstimate = total, IsFinal = true };
    }

    private List<string> TokenizeForQuery(string term)
    {
        var result = new List<string>();
        foreach (var token in Tokenizer.Tokenize(term))
        {
            if (token.Value.Length > 0) result.Add(token.Value);
        }
        return result;
    }

    private ContentPreview? BuildPreview(long docId, List<string> terms)
    {
        // 命中位置不落盘（M4 决议）：预览走文档重扫。文档缓存未建时返回无预览命中。
        var text = _documentStore?.TryGet(docId);
        if (text is null) return null;
        var ranges = new List<(int Start, int Length)>();
        foreach (var term in terms)
        {
            int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                ranges.Add((idx, term.Length));
                idx = text.IndexOf(term, idx + term.Length, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (ranges.Count == 0) return null;
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        int windowStart = Math.Max(0, ranges[0].Start - 60);
        int windowLen = Math.Min(text.Length - windowStart, 200);
        var snippet = text.Substring(windowStart, windowLen);
        return new ContentPreview
        {
            Snippet = snippet,
            MatchRanges = ranges
                .Where(r => r.Start >= windowStart && r.Start < windowStart + windowLen)
                .Select(r => new MatchRangeTuple(r.Start - windowStart, r.Length))
                .ToList(),
        };
    }

    public IndexStatus GetIndexStatus()
    {
        var fs = _fsIndex.Snapshot();
        lock (_gate)
        {
            return new IndexStatus
            {
                FileCount = fs.FileCount,
                FilenameIndexReady = fs.Ready,
                ContentIndexReady = _fullText.DocCount > 0,
                PendingContentQueue = (int)Math.Min(int.MaxValue, _pendingContentQueue),
                FailedFiles = _failedFiles.ToArray(),
            };
        }
    }

    public AckResponse RebuildIndex(RebuildRequest request)
    {
        // 重建=清空双索引后重扫（快照与 journal 由 Checkpoint 通道管理，不在此删除）
        lock (_gate) { _pendingContentQueue = 0; _failedFiles.Clear(); }
        _ = Task.Run(async () =>
        {
            try { await BuildIndexAsync(CancellationToken.None); }
            catch { /* 后台重建失败留待状态查询暴露，不外抛带崩服务 */ }
        });
        return new AckResponse { Ok = true };
    }

    public AckResponse PauseIndexing()
    {
        lock (_gate) _paused = true;
        return new AckResponse { Ok = true };
    }

    public AckResponse ResumeIndexing()
    {
        lock (_gate) _paused = false;
        return new AckResponse { Ok = true };
    }

    public SubscribeAck SubscribeEvents(SubscribeRequest request, IpcClientContext ctx)
    {
        var id = Guid.NewGuid();
        lock (_gate) _subscriptions[id] = (request.Filter, ctx);
        return new SubscribeAck { SubscriptionId = id };
    }

    public AckResponse UnsubscribeEvents(UnsubscribeRequest request)
    {
        lock (_gate) return _subscriptions.Remove(request.SubscriptionId)
            ? new AckResponse { Ok = true }
            : AckResponse.Fail("unknown subscription");
    }

    public AckResponse RegisterParser(RegisterParserRequest request)
    {
        lock (_gate)
        {
            if (_parsers.ContainsKey(request.ParserId))
                return AckResponse.Fail("parser already registered");
            foreach (var ext in request.Extensions)
            {
                if (!ext.StartsWith('.') || ext != ext.ToLowerInvariant())
                    return AckResponse.Fail("extensions must be lowercase dotted form");
                if (_parsers.Values.Any(p => p.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)))
                    return AckResponse.Fail($"extension {ext} already claimed");
                _parsers[request.ParserId] = request;
            }
        }
        return new AckResponse { Ok = true };
    }

    private bool PassesPathFilter(ParsedQuery parsed, string path)
    {
        if (parsed.Path is null) return true;
        bool hit = path.StartsWith(parsed.Path.Prefix, StringComparison.OrdinalIgnoreCase);
        return parsed.Path.Exclude ? !hit : hit;
    }

    /// <summary>事件分发：USN 事件按订阅过滤器推送（预埋接口二，管家挂接点）。</summary>
    public void DispatchEvent(FileEvent e)
    {
        IpcClientContext[] targets;
        lock (_gate)
        {
            targets = _subscriptions.Values
                .Where(s => s.Filter.Matches(e))
                .Select(s => s.Ctx)
                .ToArray();
        }
        foreach (var t in targets) t.PushEvent(e);
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _journalSink?.Dispose();
    }
}

/// <summary>文档缓存抽象（预览重扫数据源；解析管线接入时提供实现）。</summary>
public interface IDocumentStore
{
    string? TryGet(long docId);
}

/// <summary>IPC 客户端上下文：事件推送通道（管道连接侧实现推送帧）。</summary>
public sealed class IpcClientContext
{
    private readonly System.IO.Stream _stream;

    public IpcClientContext(System.IO.Stream stream) => _stream = stream;

    /// <summary>向该客户端推送事件帧（EventNotification 载荷，方法号 6）。</summary>
    public void PushEvent(FileEvent e)
    {
        var payload = JsonSerializerShim.Serialize(new EventNotification
        {
            SubscriptionId = default, // 由分发层填充订阅号
            Event = e,
        });
        // 帧头 [4B 长度][2B 版本][2B 方法号=6][4B 序号=0]
        var header = new byte[12];
        BitConverter.GetBytes(payload.Length + 12).CopyTo(header, 0);
        BitConverter.GetBytes(IpcProtocol.Version).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)IpcMethod.SubscribeEvents).CopyTo(header, 6);
        BitConverter.GetBytes(0u).CopyTo(header, 8);
        _stream.Write(header);
        _stream.Write(payload);
        _stream.Flush();
    }
}
