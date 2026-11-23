using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TaoSearch.App.Core;
using TaoSearch.Kernel.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TaoSearch.App.Core.Tests;

/// <summary>
/// 管道回环服务端：监听真实管道名，按帧协议应答，覆盖 PipeClient/SearchGateway 真实链路。
/// </summary>
internal sealed class LoopbackPipeServer : IDisposable
{
    public byte[] ResponsePayload { get; set; } = [];
    public bool CloseAfterHeader { get; set; }
    public bool RefuseConnection { get; set; }
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public static LoopbackPipeServer Start(byte[] responsePayload, bool closeAfterHeader = false, bool refuse = false)
    {
        var srv = new LoopbackPipeServer { ResponsePayload = responsePayload, CloseAfterHeader = closeAfterHeader, RefuseConnection = refuse };
        srv._loop = Task.Run(() => srv.RunAsync(srv._cts.Token));
        return srv;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream("taosearch.svc.1", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct);
                if (RefuseConnection) { server.Dispose(); continue; }

                var header = new byte[12];
                await ReadExactly(server, header, ct);
                int len = BitConverter.ToInt32(header, 0);
                var payload = new byte[Math.Max(len, 0)];
                if (len > 0) await ReadExactly(server, payload, ct);

                if (CloseAfterHeader) { server.Dispose(); continue; }

                var resp = new byte[12 + ResponsePayload.Length];
                BitConverter.GetBytes(ResponsePayload.Length).CopyTo(resp, 0);
                ResponsePayload.CopyTo(resp, 12);
                await server.WriteAsync(resp, ct);
                await server.FlushAsync(ct);
                // Linux UDS：写完立即关会重置连接，客户端可能未读完——短暂保持
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally { server.Dispose(); }
        }
    }

    private static async Task ReadExactly(Stream s, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) throw new IOException("server: pipe closed");
            total += n;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
    }
}

/// <summary>PipeClient 全分支：成功往返、空载荷拒绝、服务端断连、连接失败。</summary>
public class PipeClientTests
{
    private static byte[] Json(AckResponse ack) =>
        JsonSerializer.SerializeToUtf8Bytes(ack, AppJsonContext.Default.AckResponse);

    [Fact]
    public async Task CallAsync_Success_RoundTrips()
    {
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var client = new PipeClient();
        var ack = await client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null);
        Assert.True(ack.Ok);
    }

    [Fact]
    public async Task CallAsync_EmptyResponse_ThrowsInvalidData()
    {
        using var server = LoopbackPipeServer.Start([]);
        using var client = new PipeClient();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.CallAsync<object, AckResponse>(IpcMethod.GetIndexStatus, null));
    }

    [Fact]
    public async Task CallAsync_ServerClosesAfterHeader_ThrowsIO()
    {
        using var server = LoopbackPipeServer.Start([], closeAfterHeader: true);
        using var client = new PipeClient();
        await Assert.ThrowsAnyAsync<IOException>(
            () => client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null));
    }

    [Fact]
    public async Task CallAsync_NoServer_ThrowsWithinTimeout()
    {
        using var client = new PipeClient();
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null));
    }

    [Fact]
    public async Task CallAsync_NullJsonResponse_ThrowsInvalidData()
    {
        // 响应载荷为合法 JSON "null"：反序列化得 null，应走 ?? 兜底抛干净异常
        using var server = LoopbackPipeServer.Start("null"u8.ToArray());
        using var client = new PipeClient();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null));
    }

    [Fact]
    public async Task CallAsync_SecondCall_ReusesConnectedPipe()
    {
        // 同一客户端连续两次调用：第二次应复用已连接管道（EnsureConnectedAsync 真侧）
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var client = new PipeClient();
        var first = await client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null);
        var second = await client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
    }

    [Fact]
    public async Task CallAsync_WithRequestPayload_SerializesAndSucceeds()
    {
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var client = new PipeClient();
        var ack = await client.CallAsync<RebuildRequest, AckResponse>(
            IpcMethod.RebuildIndex, new RebuildRequest { Scope = QueryScope.Filename });
        Assert.True(ack.Ok);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var client = new PipeClient();
        client.Dispose();
        client.Dispose();
    }
}

/// <summary>SearchGateway 真实链路：服务监督放行（回环服务端在听）→ 管道往返。</summary>
public class SearchGatewayTests
{
    [Fact]
    public async Task SearchAsync_RoundTrips_ThroughRealPipe()
    {
        var batch = new ResultBatch
        {
            Hits = [new SearchHit
            {
                FileReference = 7, Path = @"D:\x.txt", FileName = "x.txt",
                Size = 12, ModifiedTime = DateTimeOffset.Now, Score = 2.5, Preview = null,
            }],
            TotalEstimate = 1,
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(batch, AppJsonContext.Default.ResultBatch);
        using var server = LoopbackPipeServer.Start(payload);
        using var gw = new SearchGateway();
        var result = await gw.SearchAsync("x", QueryScope.Filename | QueryScope.Content, 200);
        Assert.Single(result.Hits);
        Assert.Equal(1, result.TotalEstimate);
    }
}

/// <summary>ServiceSupervisor：管道在听即放行；管道不在且无服务可拉起则快速返回；进程级只试一次。</summary>
public class ServiceSupervisorTests
{
    private static void ResetAttempted()
    {
        var field = typeof(ServiceSupervisor).GetField("_attempted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, 0);
    }

    [Fact]
    public async Task EnsureRunning_ReturnsFast_WhenPipeAlive()
    {
        ResetAttempted();
        using var server = LoopbackPipeServer.Start([]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ServiceSupervisor.EnsureRunningAsync();
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task EnsureRunning_SecondCall_GuardedToOnce()
    {
        ResetAttempted();
        using var server = LoopbackPipeServer.Start([]);
        await ServiceSupervisor.EnsureRunningAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ServiceSupervisor.EnsureRunningAsync();
        Assert.True(sw.ElapsedMilliseconds < 500, $"guard should return immediately, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task EnsureRunning_PipeDead_NoExe_ReturnsQuickly()
    {
        ResetAttempted();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ServiceSupervisor.EnsureRunningAsync();
        // 管道不可达 + 本目录无服务可执行文件 → 不进入 15 秒等待
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task EnsureRunning_Cancelled_ReturnsQuietly()
    {
        ResetAttempted();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await ServiceSupervisor.EnsureRunningAsync(cts.Token);
    }
}
