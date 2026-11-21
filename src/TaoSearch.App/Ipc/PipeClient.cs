using TaoSearch.Kernel.Contracts;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoSearch.App.Ipc;

/// <summary>AOT 源生成序列化上下文（壳层侧，零第三方依赖）。</summary>
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
            var pipe = await EnsureConnectedAsync(ct);
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);

            var header = new byte[12];
            await ReadExactlyAsync(pipe, header, ct);
            int respLen = BitConverter.ToInt32(header, 0) - 12;
            var respPayload = new byte[respLen];
            await ReadExactlyAsync(pipe, respPayload, ct);

            return (TResp)JsonSerializer.Deserialize(respPayload, AppJsonContext.Default.Options.GetTypeInfo(typeof(TResp)))
                ?? throw new InvalidDataException($"empty response for {method}");
        }
        finally { _gate.Release(); }
    }

    private static byte[] BuildFrame(IpcMethod method, byte[] payload)
    {
        var frame = new byte[12 + payload.Length];
        BitConverter.GetBytes(payload.Length + 12).CopyTo(frame, 0);
        BitConverter.GetBytes(IpcProtocol.Version).CopyTo(frame, 4);
        BitConverter.GetBytes((ushort)method).CopyTo(frame, 6);
        BitConverter.GetBytes(0u).CopyTo(frame, 8);
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
