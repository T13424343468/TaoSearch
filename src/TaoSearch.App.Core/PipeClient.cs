using TaoSearch.Kernel.Contracts;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace TaoSearch.App.Core;

/// <summary>AOT 源生成序列化上下文（壳层侧，零第三方依赖）。</summary>
[ExcludeFromCodeCoverage]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QuerySpec))]
[JsonSerializable(typeof(ResultBatch))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(ContentPreview))]
[JsonSerializable(typeof(IndexStatus))]
[JsonSerializable(typeof(RebuildRequest))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(SubscribeAck))]
[JsonSerializable(typeof(UnsubscribeRequest))]
[JsonSerializable(typeof(RegisterParserRequest))]
[JsonSerializable(typeof(AckResponse))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>
/// 管道客户端（契约冻结版）：帧协议 [4B 长度][2B 版本][2B 方法号][4B 序号][载荷 JSON]，
/// 管道名 taosearch.svc.1，8 方法对齐 IpcMethod。
/// </summary>
public sealed class PipeClient : IDisposable
{
    private const string PipeName = "taosearch.svc.1";
    private NamedPipeClientStream? _pipe;
    private uint _seq;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>确保连接（懒连接，服务未起时抛 IOException 由调用方提示启动服务）。</summary>
    private async Task<NamedPipeClientStream> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return _pipe;
        _pipe?.Dispose();
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, ct);
        _pipe = pipe;
        return pipe;
    }

    /// <summary>通用请求：发一帧收一帧，载荷按方法号对应的响应类型反序列化。</summary>
    public async Task<TResp> CallAsync<TReq, TResp>(IpcMethod method, TReq? request, CancellationToken ct = default)
        where TResp : class
    {
        byte[] payload = request is null ? [] : JsonSerializer.SerializeToUtf8Bytes(request, AppJsonContext.Default.Options);
        byte[] frame = BuildFrame(method, payload);

        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var pipe = await EnsureConnectedAsync(ct);
                    await pipe.WriteAsync(frame, ct);
                    await pipe.FlushAsync(ct);

                    var header = new byte[12];
                    await ReadExactlyAsync(pipe, header, ct);
                    int respLen = BitConverter.ToInt32(header, 0); // 契约 §3.4：响应帧头即载荷长度
                    if (respLen <= 0) throw new InvalidDataException($"empty response for {method}");
                    var respPayload = new byte[respLen];
                    await ReadExactlyAsync(pipe, respPayload, ct);

                    return JsonSerializer.Deserialize<TResp>(respPayload, AppJsonContext.Default.Options)
                        ?? throw new InvalidDataException($"empty response for {method}");
                }
                catch (IOException) when (attempt == 0)
                {
                    // 连接已被对端关闭（服务重启/单应答即关）：弃旧换新重试一次
                    _pipe?.Dispose();
                    _pipe = null;
                }
                catch (OperationCanceledException)
                {
                    // 取消可能发生在帧中途，残留半帧会毒化连接：弃管道，下次调用重连
                    _pipe?.Dispose();
                    _pipe = null;
                    throw;
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 搜索请求：服务端按 BatchSize 流式回多批直至 IsFinal 终帧，本方法连续读帧聚合。
    /// 真机红灯修复（0.1.0.15）：此前只读一帧即返回，命中超一批时残余帧滞留管道，
    /// 下一次请求读到上一单残帧，协议错位后所有搜索全部落空。
    /// </summary>
    public async Task<ResultBatch> CallSearchAsync(QuerySpec request, CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, AppJsonContext.Default.Options);
        byte[] frame = BuildFrame(IpcMethod.Search, payload);

        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var hits = new List<SearchHit>();
                long total = 0;
                try
                {
                    var pipe = await EnsureConnectedAsync(ct);
                    await pipe.WriteAsync(frame, ct);
                    await pipe.FlushAsync(ct);

                    while (true)
                    {
                        var header = new byte[12];
                        await ReadExactlyAsync(pipe, header, ct);
                        int respLen = BitConverter.ToInt32(header, 0);
                        if (respLen <= 0) throw new InvalidDataException($"empty response for {IpcMethod.Search}");
                        var respPayload = new byte[respLen];
                        await ReadExactlyAsync(pipe, respPayload, ct);
                        var batch = JsonSerializer.Deserialize<ResultBatch>(respPayload, AppJsonContext.Default.Options)
                            ?? throw new InvalidDataException($"empty response for {IpcMethod.Search}");
                        total = batch.TotalEstimate;
                        hits.AddRange(batch.Hits);
                        if (batch.IsFinal)
                        {
                            return new ResultBatch { Hits = hits, TotalEstimate = total, IsFinal = true };
                        }
                    }
                }
                catch (IOException) when (attempt == 0)
                {
                    _pipe?.Dispose();
                    _pipe = null;
                }
                catch (OperationCanceledException)
                {
                    // 半帧中断毒化连接：弃管道，下次调用重连
                    _pipe?.Dispose();
                    _pipe = null;
                    throw;
                }
            }
        }
        finally { _gate.Release(); }
    }

    private byte[] BuildFrame(IpcMethod method, byte[] payload)
    {
        var frame = new byte[12 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0); // 契约 §3.4：帧头 4B 为载荷长度（不含 12B 帧头）
        BitConverter.GetBytes(IpcProtocol.Version).CopyTo(frame, 4);
        BitConverter.GetBytes((ushort)method).CopyTo(frame, 6);
        BitConverter.GetBytes(_seq++).CopyTo(frame, 8);
        payload.CopyTo(frame, 12);
        return frame;
    }

    private static async Task ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) throw new IOException("pipe closed");
            total += n;
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _gate.Dispose();
    }
}
