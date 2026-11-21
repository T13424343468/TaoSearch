using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;

namespace TaoSearch.Service;

/// <summary>
/// AOT 源生成序列化上下文（M1 实测修复沿用）：契约载荷全部走源生成，零反射零第三方。
/// ContentPreview.MatchRanges 的 ValueTuple 由 STJ 按 {"Item1","Item2"} 字段序列化，两端同契约一致。
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QuerySpec))]
[JsonSerializable(typeof(ResultBatch))]
[JsonSerializable(typeof(IndexStatus))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(SubscribeAck))]
[JsonSerializable(typeof(EventNotification))]
[JsonSerializable(typeof(UnsubscribeRequest))]
[JsonSerializable(typeof(RegisterParserRequest))]
[JsonSerializable(typeof(RebuildRequest))]
[JsonSerializable(typeof(AckResponse))]
[JsonSerializable(typeof(FileEvent))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>
/// M4 服务分发（契约冻结版）：8 方法真实接线——Search 走 <see cref="SearchEngine"/>，
/// GetIndexStatus 聚合文件名/内容两路状态，RebuildIndex 异步重建（先 ACK 后台跑），
/// Pause/Resume 控内容管线，Subscribe/Unsubscribe 管订阅表并推送 EventNotification 帧，
/// RegisterParser 登记解析器声明。帧协议：[4B 载荷长度 LE][2B 版本][2B 方法号][4B 序号][载荷 JSON]；
/// 响应帧回显方法号与序号；事件推送帧方法号=SubscribeEvents(6)、序号=0。
/// </summary>
public sealed class ServiceHost
{
    private const int EventPushMethod = (int)IpcMethod.SubscribeEvents;

    private readonly FsIndexEngine _fs;
    private readonly InvertedIndex _fullText;
    private readonly ContentIndexer _content;
    private readonly SearchEngine _engine;
    private readonly ParserRegistry _parsers = new();
    private readonly string _volumeRoot;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    private sealed record Subscription(Guid Id, FileEventFilter Filter, Func<byte[], ValueTask> PushAsync);

    public ServiceHost(FsIndexEngine fs, InvertedIndex fullText, ContentIndexer content, string volumeRoot)
    {
        _fs = fs;
        _fullText = fullText;
        _content = content;
        _volumeRoot = volumeRoot;
        _engine = new SearchEngine(fs, fullText);
    }

    public ParserRegistry Parsers => _parsers;

    /// <summary>单方法分发：读到的载荷 → 业务 → 响应帧（Search 多帧流式；事件推送走订阅表）。</summary>
    public async Task DispatchAsync(
        ushort method, ushort version, uint seq, ReadOnlyMemory<byte> payload,
        Func<byte[], CancellationToken, ValueTask> writeFrame, CancellationToken ct)
    {
        var methodId = (IpcMethod)method;
        try
        {
            switch (methodId)
            {
                case IpcMethod.Search:
                    await HandleSearchAsync(seq, payload, writeFrame, ct).ConfigureAwait(false);
                    return;

                case IpcMethod.GetIndexStatus:
                    await WriteJsonAsync(writeFrame, method, seq, StatusCore(), ct).ConfigureAwait(false);
                    return;

                case IpcMethod.RebuildIndex:
                    var rebuild = Deserialize<RebuildRequest>(payload);
                    _ = Task.Run(() => RebuildAsync(rebuild, ct), ct);
                    await WriteAckAsync(writeFrame, method, seq, ok: true, error: null, ct).ConfigureAwait(false);
                    return;

                case IpcMethod.PauseIndexing:
                    _content.Pause();
                    await WriteAckAsync(writeFrame, method, seq, ok: true, error: null, ct).ConfigureAwait(false);
                    return;

                case IpcMethod.ResumeIndexing:
                    _content.Resume();
                    await WriteAckAsync(writeFrame, method, seq, ok: true, error: null, ct).ConfigureAwait(false);
                    return;

                case IpcMethod.SubscribeEvents:
                    var request = Deserialize<SubscribeRequest>(payload);
                    var id = Guid.NewGuid();
                    var sub = new Subscription(id, request.Filter, async frame =>
                    {
                        await writeFrame(frame, ct).ConfigureAwait(false);
                    });
                    _subscriptions[id] = sub;
                    await WriteJsonAsync(writeFrame, method, seq, new SubscribeAck { SubscriptionId = id }, ct).ConfigureAwait(false);
                    return;

                case IpcMethod.UnsubscribeEvents:
                    var unsub = Deserialize<UnsubscribeRequest>(payload);
                    bool removed = _subscriptions.TryRemove(unsub.SubscriptionId, out _);
                    await WriteAckAsync(writeFrame, method, seq, ok: removed,
                        error: removed ? null : "unknown subscription", ct).ConfigureAwait(false);
                    return;

                case IpcMethod.RegisterParser:
                    var parser = Deserialize<RegisterParserRequest>(payload);
                    bool registered = _parsers.Register(parser.ParserId, parser.Extensions);
                    await WriteAckAsync(writeFrame, method, seq, ok: registered,
                        error: registered ? null : "parser id already registered", ct).ConfigureAwait(false);
                    return;

                default:
                    await WriteAckAsync(writeFrame, method, seq, ok: false, error: "unknown method", ct).ConfigureAwait(false);
                    return;
            }
        }
        catch (JsonException ex)
        {
            await WriteAckAsync(writeFrame, method, seq, ok: false, error: $"bad payload: {ex.Message}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>文件事件泵（宿主装配）：内核应用 + 内容入队 + 订阅分发，单源三路。</summary>
    public async ValueTask OnFileEventAsync(FileEvent e, CancellationToken ct)
    {
        _fs.ApplyEvent(e);
        _content.Enqueue(e);
        foreach (var (_, sub) in _subscriptions)
        {
            if (!sub.Filter.Matches(e)) continue;
            var notification = new EventNotification { SubscriptionId = sub.Id, Event = e };
            byte[] frame = FrameWriter.Encode(EventPushMethod, 0,
                JsonSerializer.SerializeToUtf8Bytes(notification, ServiceJsonContext.Default.EventNotification));
            await sub.PushAsync(frame).ConfigureAwait(false);
        }
    }

    private async Task HandleSearchAsync(uint seq, ReadOnlyMemory<byte> payload,
        Func<byte[], CancellationToken, ValueTask> writeFrame, CancellationToken ct)
    {
        QuerySpec spec;
        try
        {
            spec = Deserialize<QuerySpec>(payload) ?? throw new JsonException("payload 为空");
        }
        catch (JsonException ex)
        {
            await WriteAckAsync(writeFrame, (ushort)IpcMethod.Search, seq, ok: false, error: $"bad payload: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        await foreach (var batch in _engine.SearchAsync(spec, ct).ConfigureAwait(false))
        {
            await WriteJsonAsync(writeFrame, (ushort)IpcMethod.Search, seq, batch, ct).ConfigureAwait(false);
        }
    }

    private IndexStatus StatusCore()
    {
        var fsStatus = _fs.Snapshot();
        return new IndexStatus
        {
            FileCount = fsStatus.FileCount,
            FilenameIndexReady = fsStatus.Ready,
            ContentIndexReady = _content.IsReady,
            PendingContentQueue = _content.PendingCount,
            FailedFiles = _content.FailedFiles,
        };
    }

    private async Task RebuildAsync(RebuildRequest request, CancellationToken ct)
    {
        if (request.Scope.HasFlag(QueryScope.Filename))
        {
            await _fs.BuildAsync(_volumeRoot, ct).ConfigureAwait(false);
        }
        if (request.Scope.HasFlag(QueryScope.Content))
        {
            await Task.Run(() => _content.Rebuild(_fs.EnumerateLivePaths(), ct), ct).ConfigureAwait(false);
        }
    }

    private static T Deserialize<T>(ReadOnlyMemory<byte> payload) where T : class =>
        JsonSerializer.Deserialize(payload.Span, ServiceJsonContext.Default<T>)
        ?? throw new JsonException("payload 为空");

    private static ValueTask WriteAckAsync(Func<byte[], CancellationToken, ValueTask> writeFrame,
        ushort method, uint seq, bool ok, string? error, CancellationToken ct) =>
        WriteJsonAsync(writeFrame, method, seq, new AckResponse { Ok = ok, Error = error }, ct);

    private static async ValueTask WriteJsonAsync<T>(Func<byte[], CancellationToken, ValueTask> writeFrame,
        ushort method, uint seq, T value, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, ServiceJsonContext.Default<T>);
        await writeFrame(FrameWriter.Encode(method, seq, json), ct).ConfigureAwait(false);
    }
}

/// <summary>帧编码（协议 §3.4）：[4B 载荷长度 LE][2B 版本][2B 方法号][4B 序号][载荷]。</summary>
public static class FrameWriter
{
    public static byte[] Encode(ushort method, uint seq, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[12 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), payload.Length);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), IpcProtocol.Version);
        BitConverter.TryWriteBytes(frame.AsSpan(6, 2), method);
        BitConverter.TryWriteBytes(frame.AsSpan(8, 4), seq);
        payload.CopyTo(frame.AsSpan(12));
        return frame;
    }
}
