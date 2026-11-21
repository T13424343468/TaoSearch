using System.Threading.Channels;

namespace TaoSearch.Service;

/// <summary>
/// 单连接管道会话（Windows 挂死修复）：同一 <see cref="Stream"/> 实例上「pending 读 + 并发写」在
/// Windows 云端实测挂死（诊断用例 PipeDiagnostic 复现，.NET 10 NamedPipeServerStream）。
/// 本类把全部管道写串行化到泵循环：响应帧与事件帧一律入队，泵只在无 pending 读的窗口统一写出；
/// 事件到达时取消 pending 读（唤醒）让泵尽快排空。读侧空闲等待可取消、请求体不可取消（保证帧完整）。
/// </summary>
public sealed class PipeSession(Stream pipe)
{
    private readonly Stream _pipe = pipe;
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly object _gate = new();
    private CancellationTokenSource? _readCts;
    private bool _reading;
    private bool _closed;

    /// <summary>帧入站（响应帧与事件推送帧统一走此入口）：入队并唤醒 pending 读，绝不直接写管道。</summary>
    public void Post(byte[] frame)
    {
        if (_closed) return;
        _outbound.Writer.TryWrite(frame);
        lock (_gate)
        {
            if (_reading) _readCts!.Cancel();
        }
    }

    /// <summary>泵循环：排空出站 → 可取消等待请求头 → 完整消费请求 → 分发（响应帧经 Post 回队）。</summary>
    public async Task RunAsync(ServiceHost host, CancellationToken ct)
    {
        var header = new byte[12];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. 排空出站队列：此刻无 pending 读，写安全
                while (_outbound.Reader.TryRead(out var frame))
                {
                    await _pipe.WriteAsync(frame, ct).ConfigureAwait(false);
                    await _pipe.FlushAsync(ct).ConfigureAwait(false);
                }

                // 2. 空闲等待下一请求头：单次可取消读；与 Post 的唤醒在 _gate 下原子交接，无丢失唤醒
                CancellationToken readToken;
                lock (_gate)
                {
                    if (_closed) return;
                    if (_outbound.Reader.TryPeek(out _)) continue; // 排空期间又有新帧，回去写
                    _reading = true;
                    _readCts = new CancellationTokenSource();
                    readToken = _readCts.Token;
                }

                int n;
                try
                {
                    n = await _pipe.ReadAsync(header, readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue; // 被事件唤醒，回去排空
                }
                finally
                {
                    lock (_gate)
                    {
                        _reading = false;
                        _readCts?.Dispose();
                        _readCts = null;
                    }
                }

                if (n == 0) return; // 对端断开

                // 3. 请求剩余部分不可取消读：请求必须完整消费，事件帧在队列里等下一轮
                int total = n;
                while (total < 12)
                {
                    int k = await _pipe.ReadAsync(header.AsMemory(total), CancellationToken.None).ConfigureAwait(false);
                    if (k == 0) return;
                    total += k;
                }

                uint payloadLength = BitConverter.ToUInt32(header, 0);
                ushort version = BitConverter.ToUInt16(header, 4);
                ushort method = BitConverter.ToUInt16(header, 6);
                uint seq = BitConverter.ToUInt32(header, 8);

                if (payloadLength > PipeServerLimits.MaxPayloadBytes) return; // 异常帧直接断开，防内存打爆

                var payload = new byte[payloadLength];
                total = 0;
                while (total < payloadLength)
                {
                    int k = await _pipe.ReadAsync(payload.AsMemory(total), CancellationToken.None).ConfigureAwait(false);
                    if (k == 0) return;
                    total += k;
                }

                // 4. 分发：响应帧经 Post 入队，下一轮排空写出（保持帧序）
                await host.DispatchAsync(method, version, seq, payload,
                    (frame, _) => { Post(frame); return ValueTask.CompletedTask; }, ct).ConfigureAwait(false);
            }
        }
        catch (IOException) { /* 客户端断开属正常 */ }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) _closed = true;
        }
    }
}
