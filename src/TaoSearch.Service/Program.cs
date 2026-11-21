using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoSearch.Service;

/// <summary>
/// AOT 源生成序列化上下文（M1 NativeAOT 实测修复）：反射式 JsonSerializer 与裁剪/AOT
/// 不兼容（IL2026/IL3050，云端 run 44859414687 实测暴露），改源生成零第三方依赖。
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AckFrame))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>管道 ACK 帧（原匿名对象载荷，字段不变）。</summary>
internal sealed record AckFrame(bool Ok, ushort Method, ushort Version, uint Seq);

/// <summary>
/// 服务宿主骨架（架构 §3/§7）：命名管道 IPC 服务端 + 单实例互斥体。
/// 内核接线在 M2/M3 里程碑补齐，本骨架先立进程模型与协议帧。
/// </summary>
public static class Program
{
    private const string PipeName = "taosearch.svc.1";
    private static readonly Mutex SingleInstance = new(false, @"Global\TaoSearch.Service.Mutex");

    public static async Task Main(string[] args)
    {
        if (!SingleInstance.WaitOne(0)) return; // 单实例：重复启动直接退出

        Console.WriteLine("TaoSearch.Service starting (kernel wiring pending M2/M3)...");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var server = new PipeServer(PipeName);
        await server.RunAsync(cts.Token);
    }
}

/// <summary>命名管道服务端：帧协议 [4B 长度][2B 版本][2B 方法号][4B 序号][载荷]（架构 §3.4）。</summary>
public sealed class PipeServer(string pipeName)
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

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
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

                // 载荷按方法号分发（M4 接 Contracts 方法族；当前回 ACK）
                var ack = JsonSerializer.SerializeToUtf8Bytes(
                    new AckFrame(true, method, version, seq), ServiceJsonContext.Default.AckFrame);
                await pipe.WriteAsync(ack, ct);
                await pipe.FlushAsync(ct);
            }
        }
        catch (IOException) { /* 客户端断开属正常 */ }
        finally { pipe.Dispose(); }
    }

    private static async Task<int> ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
