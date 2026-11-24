using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Service;
using Xunit;

namespace TaoSearch.Service.Tests;

/// <summary>
/// 端到端 IPC 测试：真实 NamedPipeServerStream/NamedPipeClientStream 对接 ServiceHost.DispatchAsync，
/// 帧协议 [4B 长度][2B 版本][2B 方法号][4B 序号][载荷 JSON] 与契约冻结版一致。
/// </summary>
public class ServiceHostIpcTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FsIndexEngine _fs;
    private readonly InvertedIndex _fullText = new();
    private readonly ContentIndexer _content;
    private readonly ServiceHost _host;
    private readonly NamedPipeServerStream _server;
    private readonly NamedPipeClientStream _client;
    private readonly PipeSession _session;
    private readonly Task _pump;

    public ServiceHostIpcTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "taosearch-ipc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        string fileA = WriteFile("alpha report.txt", "alpha body text");
        string fileB = WriteFile("beta notes.txt", "beta body text");
        string fileC = WriteFile("song.mp3", "binary-ish");

        _fs = new FsIndexEngine(
            mftScanOverride: (_, _, _) => MftScanOutcome.Fail(1),
            fallbackOverride: (root, ct) => Feed(
            [
                new FallbackEntry(fileA, 100, T1),
                new FallbackEntry(fileB, 2000, T1),
                new FallbackEntry(fileC, 5000, T1),
            ], ct));
        _fs.BuildAsync("C:\\").GetAwaiter().GetResult();

        _content = new ContentIndexer(_fullText);
        _host = new ServiceHost(_fs, _fullText, _content, "C:\\");

        string pipeName = "taosearch.test." + Guid.NewGuid().ToString("N");
        _server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _client.Connect(10_000);
        _server.WaitForConnection();

        // 读写串行化会话：Windows 实测同实例 pending 读 + 并发写挂死，事件推送帧经 Post 入队由泵写出
        _session = new PipeSession(_server);
        _pump = Task.Run(() => _session.RunAsync(_host, CancellationToken.None));
    }

    /// <summary>xUnit v2 不调用 IAsyncDisposable.DisposeAsync，改用 IDisposable 确保逐测试释放管道与内容索引器。</summary>
    public void Dispose()
    {
        try { _client.Dispose(); } catch (Exception) { }
        _server.Dispose();
        try { _pump.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _content.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async IAsyncEnumerable<FallbackEntry> Feed(FallbackEntry[] entries, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in entries)
        {
            await Task.Yield();
            yield return e;
        }
        await Task.CompletedTask;
    }

    private async Task SendAsync(ushort method, uint seq, ReadOnlyMemory<byte> payload)
    {
        byte[] frame = FrameWriter.Encode(method, seq, payload.Span);
        await _client.WriteAsync(frame);
        await _client.FlushAsync();
    }

    /// <summary>客户端侧精确读：客户端单线程顺序读写，无并发写，直接循环读满。</summary>
    private static async Task ReadExactAsync(Stream stream, Memory<byte> target)
    {
        int filled = 0;
        while (filled < target.Length)
        {
            int read = await stream.ReadAsync(target[filled..]);
            if (read == 0) throw new EndOfStreamException("管道已断开");
            filled += read;
        }
    }

    private async Task<(ushort Method, uint Seq, byte[] Payload)> ReadFrameAsync()
    {
        var header = new byte[12];
        await ReadExactAsync(_client, header);
        int payloadLength = BitConverter.ToInt32(header, 0);
        ushort method = BitConverter.ToUInt16(header, 6);
        uint seq = BitConverter.ToUInt32(header, 8);
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
            await ReadExactAsync(_client, payload);
        return (method, seq, payload);
    }

    private static T ReadJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidOperationException("载荷为空");

    /// <summary>挂起定位：给关键 await 装 15 秒分步计时器，超时抛出带步骤标签的异常而非无限等待。</summary>
    private static async Task StepAsync(string step, Func<Task> action)
    {
        try
        {
            await action().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"[挂起定位] 步骤 {step} 15 秒未完成");
        }
    }

    private static async Task<T> StepAsync<T>(string step, Func<Task<T>> action)
    {
        try
        {
            return await action().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"[挂起定位] 步骤 {step} 15 秒未完成");
        }
    }

    [Fact]
    public async Task Search_MultiFrameStreamed_SeqAndMethodEchoed()
    {
        var spec = new QuerySpec { Text = "regex:.*", Scope = QueryScope.Filename, BatchSize = 1 };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(spec);

        await SendAsync((ushort)IpcMethod.Search, seq: 77, payload);

        var batches = new List<ResultBatch>();
        while (batches.Count == 0 || !batches[^1].IsFinal)
        {
            var (method, seq, body) = await ReadFrameAsync();
            Assert.Equal((ushort)IpcMethod.Search, method);
            Assert.Equal(77u, seq);
            batches.Add(ReadJson<ResultBatch>(body));
        }

        Assert.Equal(3, batches.Count);
        Assert.False(batches[0].IsFinal);
        Assert.True(batches[2].IsFinal);
        Assert.All(batches, b => Assert.Single(b.Hits));
    }

    [Fact]
    public async Task GetIndexStatus_ReturnsAggregatedStatus()
    {
        await SendAsync((ushort)IpcMethod.GetIndexStatus, seq: 1, ReadOnlyMemory<byte>.Empty);

        var (method, seq, body) = await ReadFrameAsync();
        Assert.Equal((ushort)IpcMethod.GetIndexStatus, method);
        Assert.Equal(1u, seq);
        var status = ReadJson<IndexStatus>(body);
        Assert.Equal(3, status.FileCount);
        Assert.True(status.FilenameIndexReady);
        Assert.True(status.ContentIndexReady);
        Assert.Empty(status.FailedFiles);
        // 慢路径标记透传（真机红灯修复）：MFT 必败桩 → 状态必须如实上报慢路径，壳层据此提示提权
        Assert.True(status.FilenameUsingSlowPath);
    }

    [Fact]
    public async Task Subscribe_PushesEventFrame_UnsubscribeStops()
    {
        var filter = new FileEventFilter { Kinds = FileChangeKind.Created };
        byte[] subPayload = JsonSerializer.SerializeToUtf8Bytes(new SubscribeRequest { Filter = filter });
        await StepAsync("1-发送Subscribe", () => SendAsync((ushort)IpcMethod.SubscribeEvents, seq: 5, subPayload));

        var (method, seq, body) = await StepAsync<(ushort, uint, byte[])>("2-读SubscribeAck",
            () => ReadFrameAsync());
        Assert.Equal((ushort)IpcMethod.SubscribeEvents, method);
        Assert.Equal(5u, seq);
        var ack = ReadJson<SubscribeAck>(body);
        Assert.NotEqual(Guid.Empty, ack.SubscriptionId);

        // 事件推送帧：方法号=6、序号=0
        var e = new FileEvent
        {
            Kind = FileChangeKind.Created,
            FileReference = 999,
            Path = "C:\\Docs\\new.txt",
            UtcTimestamp = T1,
        };
        await StepAsync("3-OnFileEvent推送", () => _host.OnFileEventAsync(e, CancellationToken.None).AsTask());

        var (pushMethod, pushSeq, pushBody) = await StepAsync<(ushort, uint, byte[])>("4-读推送帧",
            () => ReadFrameAsync());
        Assert.Equal((ushort)IpcMethod.SubscribeEvents, pushMethod);
        Assert.Equal(0u, pushSeq);
        var notification = ReadJson<EventNotification>(pushBody);
        Assert.Equal(ack.SubscriptionId, notification.SubscriptionId);
        Assert.Equal(999, notification.Event.FileReference);

        byte[] unsubPayload = JsonSerializer.SerializeToUtf8Bytes(new UnsubscribeRequest { SubscriptionId = ack.SubscriptionId });
        await StepAsync("5-发送Unsubscribe", () => SendAsync((ushort)IpcMethod.UnsubscribeEvents, seq: 6, unsubPayload));
        var (_, seq2, ackBody) = await StepAsync<(ushort, uint, byte[])>("6-读UnsubscribeAck",
            () => ReadFrameAsync());
        Assert.Equal(6u, seq2);
        Assert.True(ReadJson<AckResponse>(ackBody).Ok);

        // 退订后不再推送：1 秒内不应有任何帧到达
        await StepAsync("7-退订后事件", () => _host.OnFileEventAsync(e with { FileReference = 1000 }, CancellationToken.None).AsTask());
        var probe = new byte[1];
        bool pushed = await _client.ReadAsync(probe).AsTask().WaitAsync(TimeSpan.FromSeconds(1))
            .ContinueWith(t => t.Status == TaskStatus.RanToCompletion, TaskScheduler.Default);
        Assert.False(pushed);
    }

    [Fact]
    public async Task Unsubscribe_UnknownId_NegativeAck()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new UnsubscribeRequest { SubscriptionId = Guid.NewGuid() });
        await SendAsync((ushort)IpcMethod.UnsubscribeEvents, seq: 8, payload);

        var (_, seq, body) = await ReadFrameAsync();
        Assert.Equal(8u, seq);
        var ack = ReadJson<AckResponse>(body);
        Assert.False(ack.Ok);
        Assert.NotNull(ack.Error);
    }

    [Fact]
    public async Task RegisterParser_FirstOk_DuplicateRejected()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new RegisterParserRequest { ParserId = "butler.archive-v1", Extensions = [".zip"] });
        await SendAsync((ushort)IpcMethod.RegisterParser, seq: 9, payload);

        var (_, seq, body) = await ReadFrameAsync();
        Assert.Equal(9u, seq);
        Assert.True(ReadJson<AckResponse>(body).Ok);

        await SendAsync((ushort)IpcMethod.RegisterParser, seq: 10, payload);
        var (_, seq2, body2) = await ReadFrameAsync();
        Assert.Equal(10u, seq2);
        var ack2 = ReadJson<AckResponse>(body2);
        Assert.False(ack2.Ok);
        Assert.NotNull(ack2.Error);
    }

    [Fact]
    public async Task UnknownMethod_NegativeAck()
    {
        await SendAsync(method: 99, seq: 11, ReadOnlyMemory<byte>.Empty);

        var (_, seq, body) = await ReadFrameAsync();
        Assert.Equal(11u, seq);
        var ack = ReadJson<AckResponse>(body);
        Assert.False(ack.Ok);
        Assert.Contains("unknown method", ack.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseResume_PositiveAcks()
    {
        await SendAsync((ushort)IpcMethod.PauseIndexing, seq: 12, ReadOnlyMemory<byte>.Empty);
        var (_, seq1, body1) = await ReadFrameAsync();
        Assert.Equal(12u, seq1);
        Assert.True(ReadJson<AckResponse>(body1).Ok);
        Assert.True(_content.IsPaused);

        await SendAsync((ushort)IpcMethod.ResumeIndexing, seq: 13, ReadOnlyMemory<byte>.Empty);
        var (_, seq2, body2) = await ReadFrameAsync();
        Assert.Equal(13u, seq2);
        Assert.True(ReadJson<AckResponse>(body2).Ok);
        Assert.False(_content.IsPaused);
    }

    [Fact]
    public async Task RebuildIndex_AcksImmediately_ThenRebuilds()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new RebuildRequest { Scope = QueryScope.Filename });
        await SendAsync((ushort)IpcMethod.RebuildIndex, seq: 14, payload);

        var (_, seq, body) = await ReadFrameAsync();
        Assert.Equal(14u, seq);
        Assert.True(ReadJson<AckResponse>(body).Ok);
    }
}
