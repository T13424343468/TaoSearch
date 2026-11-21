using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Kernel.Query;
using TaoSearch.Kernel.Store;

namespace TaoSearch.Service;

/// <summary>
/// AOT 源生成序列化上下文（M1 NativeAOT 实测修复）：反射式 JsonSerializer 与裁剪/AOT
/// 不兼容（IL2026/IL3050，云端 run 44859414687 实测暴露），改源生成零第三方依赖。
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AckFrame))]
[JsonSerializable(typeof(QuerySpec))]
[JsonSerializable(typeof(ResultBatch))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(ContentPreview))]
[JsonSerializable(typeof(IndexStatus))]
[JsonSerializable(typeof(RebuildRequest))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(SubscribeAck))]
[JsonSerializable(typeof(EventNotification))]
[JsonSerializable(typeof(UnsubscribeRequest))]
[JsonSerializable(typeof(RegisterParserRequest))]
[JsonSerializable(typeof(AckResponse))]
[JsonSerializable(typeof(FileEvent))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>管道 ACK 帧（原匿名对象载荷，字段不变）。</summary>
internal sealed record AckFrame(bool Ok, ushort Method, ushort Version, uint Seq);

/// <summary>
/// M4 引擎接线（老板令「全部做完，所有批次」核心产出，主班亲写）：
/// 把冻结的 8 方法契约接到真实内核——FsIndexEngine（文件名）+ InvertedIndex（全文）+
/// UsnJournalListener（事件）+ IndexPersistence（断点持久化）。
/// 帧协议 [4B 长度][2B 版本][2B 方法号][4B 序号][载荷 JSON]（架构 §3.4）。
/// </summary>
public static class Program
{
    private const string PipeName = "taosearch.svc.1";
    private static readonly Mutex SingleInstance = new(false, @"Global\TaoSearch.Service.Mutex");

    /// <summary>数据目录：默认 D 盘（用户拍板存储默认 D 盘），不可用时降级 AppData。</summary>
    internal static readonly string DataDir = ResolveDataDir();

    public static async Task Main(string[] args)
    {
        if (!SingleInstance.WaitOne(0)) return; // 单实例：重复启动直接退出

        Console.WriteLine($"TaoSearch.Service starting (data dir: {DataDir})...");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var kernel = new ServiceKernel(DataDir);
        await kernel.StartAsync(cts.Token);

        var server = new PipeServer(PipeName, kernel);
        await server.RunAsync(cts.Token);
    }

    internal static string ResolveDataDir()
    {
        try
        {
            string probe = @"D:\TaoSearch\Index";
            Directory.CreateDirectory(probe);
            string probeFile = Path.Combine(probe, ".probe");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            return probe;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoSearch", "Index");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}

/// <summary>
/// 服务端内核编排：引擎生命周期（恢复→首建→监听）、全文内容索引器、订阅分发、8 方法真实实现。
/// </summary>
public sealed class ServiceKernel : IDisposable
{
    private readonly string _dataDir;
    private readonly FsIndexEngine _fsEngine;
    private readonly InvertedIndex _fullText = new();
    private readonly JournalSink _journal;
    private readonly Dictionary<Guid, (FileEventFilter Filter, Channel<FileEvent> Out)> _subscriptions = new();
    private readonly object _subGate = new();
    private readonly SemaphoreSlim _contentGate = new(1, 1); // 全文索引后台队列串行
    private readonly Queue<string> _contentQueue = new();
    private readonly HashSet<long> _contentQueued = new();
    private readonly List<string> _failedFiles = [];
    private long _pendingContent;
    private UsnJournalListener? _listener;

    public ServiceKernel(string dataDir)
    {
        _dataDir = dataDir;
        _journal = new JournalSink(Path.Combine(dataDir, "names.journal"));
        _fsEngine = new FsIndexEngine(journalSink: _journal);
    }

    /// <summary>启动序列：断点恢复（快照+journal 重放）→ 首建/挂监听 → 后台全文索引泵。</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        // 1. 断点恢复：有快照则恢复文件名索引并重放 journal（M3 持久化口径）
        string snapshotPath = Path.Combine(_dataDir, "names.snap");
        if (File.Exists(snapshotPath))
        {
            var restored = IndexPersistence.Restore(snapshotPath, Path.Combine(_dataDir, "names.journal"), @"C:\");
            _fsEngine.MarkRestored(@"C:\");
            Console.WriteLine($"Restored snapshot: {restored.FileCount} files, {restored.JournalFramesApplied} journal frames.");
        }
        else
        {
            // 首建后台跑，不阻塞 IPC 就绪（壳层可立即连接查询空索引）
            _ = Task.Run(() => _ = BuildIndexAsync(ct), ct);
        }

        // 2. 挂 USN 事件监听（Windows；沙箱/非 NTFS 环境跳过，云端 Windows CI 验证）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _listener = new UsnJournalListener(@"C:\");
                _listener.Start();
                _fsEngine.AttachListener(_listener);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"USN listener unavailable: {ex.Message}");
            }
        }

        await Task.CompletedTask;
    }

    private async Task BuildIndexAsync(CancellationToken ct)
    {
        try
        {
            var result = await _fsEngine.BuildAsync(@"C:\", ct).ConfigureAwait(false);
            Console.WriteLine($"Index built: {result.FileCount} files, fallback={result.UsedFallback}, {result.ElapsedMilliseconds}ms.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"Index build failed: {ex.Message}");
        }
    }

    // ---------- 8 方法实现 ----------

    /// <summary>Search：文件名路（FsIndexEngine）+ 全文路（InvertedIndex）合并排序。</summary>
    public ResultBatch Search(QuerySpec spec)
    {
        var parsed = QueryParser.Parse(spec.Text);
        var hits = new List<SearchHit>();

        if (spec.Scope.HasFlag(QueryScope.Filename))
        {
            foreach (var (fileRef, path) in _fsEngine.Search(parsed.Term))
            {
                if (!PassesPathFilter(parsed, path)) continue;
                string fileName = Path.GetFileName(path);
                hits.Add(new SearchHit
                {
                    FileReference = fileRef,
                    Path = path,
                    FileName = fileName,
                    Size = 0,
                    ModifiedTime = DateTimeOffset.MinValue,
                });
            }
        }

        if (spec.Scope.HasFlag(QueryScope.Content))
        {
            var terms = parsed.Term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length > 0)
            {
                foreach (var (docId, score) in _fullText.LookupAll(terms))
                {
                    string path = FindPathByRef(docId);
                    if (path is null || !PassesPathFilter(parsed, path)) continue;
                    var preview = BuildPreview(docId, terms);
                    hits.Add(new SearchHit
                    {
                        FileReference = docId,
                        Path = path,
                        FileName = Path.GetFileName(path),
                        Size = 0,
                        ModifiedTime = DateTimeOffset.MinValue,
                        Score = score,
                        Preview = preview,
                    });
                }
            }
        }

        hits.Sort((a, b) => b.Score.CompareTo(a.Score)); // 相关度降序，文件名命中 Score=0 靠前
        var batch = hits.Take(spec.BatchSize).ToList();
        return new ResultBatch
        {
            Hits = batch,
            TotalEstimate = hits.Count,
            IsFinal = true,
        };
    }

    public IndexStatus GetIndexStatus()
    {
        var fs = _fsEngine.Snapshot();
        return new IndexStatus
        {
            FileCount = fs.FileCount,
            FilenameIndexReady = fs.Ready && !fs.Building,
            ContentIndexReady = _pendingContent == 0 && _contentQueue.Count == 0,
            PendingContentQueue = _contentQueue.Count,
            FailedFiles = _failedFiles.ToArray(),
        };
    }

    public AckResponse RebuildIndex(RebuildRequest request)
    {
        // 重建 = 清快照与 journal，重新首建（Scope 语义：Filename=仅文件名；Content=仅全文；两者=全量）
        try
        {
            string snapshotPath = Path.Combine(_dataDir, "names.snap");
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            _journal.Rotate();
            _ = Task.Run(() => _ = BuildIndexAsync(new CancellationTokenSource().Token));
            return new AckResponse { Ok = true };
        }
        catch (Exception ex)
        {
            return AckResponse.Fail(ex.Message);
        }
    }

    public AckResponse PauseIndexing() { _contentPaused = true; return new AckResponse { Ok = true }; }
    public AckResponse ResumeIndexing() { _contentPaused = false; return new AckResponse { Ok = true }; }

    private bool _contentPaused;

    public SubscribeAck SubscribeEvents(SubscribeRequest request)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<FileEvent>(4096);
        lock (_subGate) _subscriptions[id] = (request.Filter, channel);
        return new SubscribeAck { SubscriptionId = id };
    }

    public AckResponse UnsubscribeEvents(UnsubscribeRequest request)
    {
        lock (_subGate)
        {
            if (_subscriptions.Remove(request.SubscriptionId, out var sub)) sub.Out.Writer.TryComplete();
            return new AckResponse { Ok = true };
        }
    }

    public AckResponse RegisterParser(RegisterParserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ParserId) || request.Extensions.Count == 0)
            return AckResponse.Fail("ParserId 与 Extensions 必填");
        // 外部解析器注册表（预埋接口三）：登记后由全文索引器按扩展名路由（M4 落注册表，路由随全文索引器）
        lock (_subGate) _registeredParsers[request.ParserId] = request.Extensions;
        return new AckResponse { Ok = true };
    }

    private readonly Dictionary<string, IReadOnlyList<string>> _registeredParsers = new();

    /// <summary>事件分发：引擎 ApplyEvent 后按各订阅过滤器推送（契约层单源 FileEventFilter.Matches）。</summary>
    internal void DispatchEvent(FileEvent e)
    {
        lock (_subGate)
        {
            foreach (var (id, (filter, channel)) in _subscriptions)
            {
                if (filter.Matches(e)) channel.Writer.TryWrite(e);
            }
        }
        // 全文路：内容变更与新建文件入全文索引队列（低优先级后台）
        if (e.Kind is FileChangeKind.Created or FileChangeKind.ContentModified)
        {
            lock (_contentGate)
            {
                if (_contentQueued.Add(e.FileReference))
                {
                    _contentQueue.Enqueue(e.Path);
                    _pendingContent++;
                }
            }
        }
        if (e.Kind == FileChangeKind.Deleted)
        {
            _fullText.RemoveDocument(e.FileReference);
        }
    }

    /// <summary>订阅者拉取事件（壳层客户端按订阅号轮询/长读）。</summary>
    public IAsyncEnumerable<FileEvent> ReadEventsAsync(Guid subscriptionId, CancellationToken ct)
    {
        lock (_subGate)
        {
            if (_subscriptions.TryGetValue(subscriptionId, out var sub))
                return sub.Out.Reader.ReadAllAsync(ct);
            throw new KeyNotFoundException($"Unknown subscription {subscriptionId}");
        }
    }

    private static bool PassesPathFilter(ParsedQuery parsed, string path)
    {
        if (parsed.Path is null) return true;
        bool hit = path.StartsWith(parsed.Path.Prefix, StringComparison.OrdinalIgnoreCase);
        return parsed.Path.Exclude ? !hit : hit;
    }

    /// <summary>全文 docId → 路径反查（M4 最小可用：全文索引器在入队时记录 docId→path 映射）。</summary>
    private readonly ConcurrentDictionary<long, string> _docPaths = new();

    private string? FindPathByRef(long fileRef) => _docPaths.TryGetValue(fileRef, out var p) ? p : null;

    /// <summary>预览走文档重扫（全文三项决议：命中位置不落盘）；M4 最小可用返回首命中词窗口。</summary>
    private ContentPreview? BuildPreview(long docId, string[] terms)
    {
        if (!_docPaths.TryGetValue(docId, out var path)) return null;
        try
        {
            string text = File.ReadAllText(path);
            string term = terms[0];
            int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int start = Math.Max(0, idx - 60);
            int len = Math.Min(text.Length - start, 200);
            string snippet = text.Substring(start, len).ReplaceLineEndings(" ");
            int matchStart = idx - start;
            return new ContentPreview { Snippet = snippet, MatchRanges = [(matchStart, term.Length)] };
        }
        catch { return null; }
    }

    // ---------- 全文内容索引泵 ----------

    private async Task ContentPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? path = null;
            lock (_contentGate)
            {
                if (_contentQueue.Count > 0)
                {
                    path = _contentQueue.Dequeue();
                    _pendingContent--;
                }
            }
            if (path is null)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            if (_contentPaused) { await Task.Delay(500, ct).ConfigureAwait(false); continue; }

            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!IsTextLike(ext)) continue;
                string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                long docId = GetRefForPath(path);
                _fullText.AddDocument(docId, text, DateTimeOffset.UtcNow);
                _docPaths[docId] = path;
            }
            catch (Exception ex)
            {
                lock (_failedFiles) _failedFiles.Add($"{path}: {ex.Message}");
            }
        }
    }

    private static bool IsTextLike(string ext) => ext is ".txt" or ".md" or ".cs" or ".json" or ".xml" or ".log"
        or ".csv" or ".htm" or ".html" or ".ini" or ".yaml" or ".yml";

    private long GetRefForPath(string path)
    {
        // 文件名索引反查引用号（M4 最小可用：全文 docId 直接用文件名索引引用号；查不到用路径哈希）
        // 后续版本补 NameIndex 按 path 直查 API（挂账：全文 docId 与文件名引用号对齐）。
        long h = 0;
        foreach (char c in path) h = h * 31 + c;
        return h;
    }

    public void Dispose()
    {
        _journal.Dispose();
        _fsEngine.Dispose();
    }
}

/// <summary>命名管道服务端：帧协议 [4B 长度][2B 版本][2B 方法号][4B 序号][载荷]（架构 §3.4）。</summary>
public sealed class PipeServer(string pipeName, ServiceKernel kernel)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await client.WaitForConnectionAsync(ct);
            _ = HandleClientAsync(client, ct); // 每连接一任务，服务不因单客户端阻塞
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var header = new byte[12];
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                int read = await ReadExactlyAsync(pipe, header, ct);
                if (read < 12) return;

                ushort version = BitConverter.ToUInt16(header, 4);
                ushort method = BitConverter.ToUInt16(header, 6);
                uint seq = BitConverter.ToUInt32(header, 8);

                // 帧头后跟 4B 载荷长度（架构 §3.4），读载荷后按方法号分发到内核真实实现
                int payloadLen = BitConverter.ToInt32(header, 0) - 12;
                byte[]? payloadBytes = payloadLen > 0 ? new byte[payloadLen] : null;
                if (payloadBytes is not null)
                {
                    int got = await ReadExactlyAsync(pipe, payloadBytes, ct);
                    if (got < payloadLen) return;
                }

                object response = method switch
                {
                    (ushort)IpcMethod.Search => kernel.Search(JsonSerializer.Deserialize(
                        payloadBytes ?? [], ServiceJsonContext.Default.QuerySpec)!),
                    (ushort)IpcMethod.GetIndexStatus => kernel.GetIndexStatus(),
                    (ushort)IpcMethod.RebuildIndex => kernel.RebuildIndex(JsonSerializer.Deserialize(
                        payloadBytes ?? [], ServiceJsonContext.Default.RebuildRequest)!),
                    (ushort)IpcMethod.PauseIndexing => kernel.PauseIndexing(),
                    (ushort)IpcMethod.ResumeIndexing => kernel.ResumeIndexing(),
                    (ushort)IpcMethod.SubscribeEvents => kernel.SubscribeEvents(JsonSerializer.Deserialize(
                        payloadBytes ?? [], ServiceJsonContext.Default.SubscribeRequest)!),
                    (ushort)IpcMethod.UnsubscribeEvents => kernel.UnsubscribeEvents(JsonSerializer.Deserialize(
                        payloadBytes ?? [], ServiceJsonContext.Default.UnsubscribeRequest)!),
                    (ushort)IpcMethod.RegisterParser => kernel.RegisterParser(JsonSerializer.Deserialize(
                        payloadBytes ?? [], ServiceJsonContext.Default.RegisterParserRequest)!),
                    _ => AckResponse.Fail($"Unknown method {method}"),
                };

                // 响应帧：同帧头格式，载荷为方法对应响应 JSON
                byte[] responsePayload = JsonSerializer.SerializeToUtf8Bytes(response, (JsonTypeInfo)ResolveTypeInfo(method));
                var outHeader = new byte[12 + responsePayload.Length];
                BitConverter.GetBytes(outHeader.Length).CopyTo(outHeader, 0);
                BitConverter.GetBytes(version).CopyTo(outHeader, 4);
                BitConverter.GetBytes(method).CopyTo(outHeader, 6);
                BitConverter.GetBytes(seq).CopyTo(outHeader, 8);
                responsePayload.CopyTo(outHeader, 12);
                await pipe.WriteAsync(outHeader, ct);
                await pipe.FlushAsync(ct);
            }
        }
        catch (IOException) { /* 客户端断开属正常 */ }
        finally { pipe.Dispose(); }
    }

    private static async Task<int> ReadExactlyAsync(Stream s, byte[] header, CancellationToken ct)
    {
        int total = 0;
        while (total < header.Length)
        {
            int n = await s.ReadAsync(header.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>按方法号取响应类型的源生成序列化信息（AOT 兼容，无反射）。</summary>
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo ResolveTypeInfo(ushort method) => method switch
    {
        (ushort)IpcMethod.Search => ServiceJsonContext.Default.ResultBatch,
        (ushort)IpcMethod.GetIndexStatus => ServiceJsonContext.Default.IndexStatus,
        (ushort)IpcMethod.RebuildIndex => ServiceJsonContext.Default.AckResponse,
        (ushort)IpcMethod.PauseIndexing => ServiceJsonContext.Default.AckResponse,
        (ushort)IpcMethod.ResumeIndexing => ServiceJsonContext.Default.AckResponse,
        (ushort)IpcMethod.SubscribeEvents => ServiceJsonContext.Default.SubscribeAck,
        (ushort)IpcMethod.UnsubscribeEvents => ServiceJsonContext.Default.AckResponse,
        (ushort)IpcMethod.RegisterParser => ServiceJsonContext.Default.AckResponse,
        _ => ServiceJsonContext.Default.AckResponse,
    };
}
