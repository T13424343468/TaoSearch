namespace TaoSearch.Kernel.FullText;

/// <summary>
/// 后台落盘限速流（全文三项决议之三，M4 落地）：包装写流，按预算速率节流，
/// 避免快照/journal 写盘挤占前台 IO。令牌按时间连续补充，短突发允许、长均值不超预算。
/// </summary>
public sealed class ThrottledWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly long _bytesPerSecond;
    private readonly bool _leaveOpen;
    private long _budget;
    private long _lastRefill;

    /// <summary>默认限速 10MB/s（全文落盘 IO 治理项，架构决议值）。</summary>
    public const long DefaultBytesPerSecond = 10L * 1024 * 1024;

    public ThrottledWriteStream(Stream inner, long bytesPerSecond = DefaultBytesPerSecond, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite) throw new ArgumentException("内层流必须可写", nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _inner = inner;
        _bytesPerSecond = bytesPerSecond;
        _leaveOpen = leaveOpen;
        _budget = bytesPerSecond;   // 初始一个整窗预算，允许首窗突发
        _lastRefill = Environment.TickCount64;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count) throw new ArgumentException("缓冲区越界");

        int written = 0;
        while (written < count)
        {
            Refill();
            int chunk = (int)Math.Min(count - written, _budget);
            if (chunk <= 0)
            {
                // 预算耗尽：睡到下一窗补充（1ms 粒度，均值不超预算）
                Thread.Sleep(1);
                continue;
            }
            _inner.Write(buffer, offset + written, chunk);
            _budget -= chunk;
            written += chunk;
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count) throw new ArgumentException("缓冲区越界");

        int written = 0;
        while (written < count)
        {
            Refill();
            int chunk = (int)Math.Min(count - written, _budget);
            if (chunk <= 0)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
                continue;
            }
            await _inner.WriteAsync(buffer.AsMemory(offset + written, chunk), ct).ConfigureAwait(false);
            _budget -= chunk;
            written += chunk;
        }
    }

    public override void Flush() => _inner.Flush();

    public override async Task FlushAsync(CancellationToken ct) => await _inner.FlushAsync(ct).ConfigureAwait(false);

    private void Refill()
    {
        long now = Environment.TickCount64;
        long elapsed = now - _lastRefill;
        if (elapsed <= 0) return;
        // 上限一个整窗，防长睡后一次性放行超大突发
        _budget = Math.Min(_bytesPerSecond, _budget + (long)(_bytesPerSecond * (elapsed / 1000.0)));
        _lastRefill = now;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}
