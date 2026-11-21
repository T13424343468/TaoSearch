using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Kernel.Store;

/// <summary>journal 重放出的单条事件（重放语义：非删除一律按 Upsert 处理，引擎侧幂等）。</summary>
public readonly record struct JournalEvent(
    long FileReference,
    long ParentReference,
    string Name,
    long Size,
    DateTimeOffset ModifiedUtc,
    bool IsDelete);

/// <summary>重放结果：应用帧数、撕裂尾字节（强杀/断电残留）、是否干净收尾。</summary>
public sealed record JournalReplayResult
{
    public required int FramesApplied { get; init; }
    public required long TruncatedTailBytes { get; init; }
    public required bool CleanEnd { get; init; }
}

/// <summary>
/// 追加式事件日志（M3，架构 §4.3）：帧格式 op(1B) | payloadLen(4B) | payload | crc32(4B)。
/// 每帧独立校验，重放遇撕裂/坏帧即停（T4 强杀恢复：只回放完整前缀，绝不带崩索引）。
/// 每帧写后 Flush（进程被杀不丢已写帧；OS 崩溃级持久化由 M4 按需升 Flush(true)）。
/// </summary>
public sealed class JournalWriter : IDisposable
{
    private const int MaxPayloadBytes = 1 << 20;   // 名字 ≤32KB，1MB 上限防坏长度打爆内存
    private const byte OpUpsert = 1;
    private const byte OpRemove = 2;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;

    /// <summary>基于流构造（流位置即写入点；空流会在首帧前写文件头）。</summary>
    public JournalWriter(Stream stream, bool leaveOpen = true)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        if (stream.Length == 0)
        {
            Span<byte> header = stackalloc byte[8];
            "TSSJ"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
            stream.Write(header);
            stream.Flush();
        }
    }

    /// <summary>按路径构造：追加模式打开，空文件补文件头。</summary>
    public static JournalWriter OpenAppend(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), leaveOpen: false);

    public void AppendUpsert(long fileReference, long parentReference, string name, long size, DateTimeOffset modifiedUtc)
    {
        int nameBytes = Encoding.UTF8.GetByteCount(name);
        byte[] payload = ArrayPool<byte>.Shared.Rent(36 + nameBytes);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), fileReference);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), parentReference);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), size);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24), modifiedUtc.UtcTicks);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(32), nameBytes);
            Encoding.UTF8.GetBytes(name, payload.AsSpan(36));
            AppendFrame(OpUpsert, payload.AsSpan(0, 36 + nameBytes));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    public void AppendRemove(long fileReference)
    {
        byte[] payload = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), fileReference);
            AppendFrame(OpRemove, payload.AsSpan(0, 8));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private void AppendFrame(byte op, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        if (payload.Length > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));

        Span<byte> frame = stackalloc byte[5];   // op(1) + payloadLen(4)，多写即破坏帧布局
        frame[0] = op;
        BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(1), payload.Length);
        _stream.Write(frame);
        _stream.Write(payload);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(payload));
        _stream.Write(crc);
        _stream.Flush();
    }

    /// <summary>重放：从流读帧逐条回调；遇撕裂尾/坏帧停止并统计残留字节，绝不抛格式异常中断重放。</summary>
    public static JournalReplayResult Replay(Stream stream, Action<JournalEvent> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        Span<byte> header = stackalloc byte[8];
        int headerRead = ReadFull(stream, header);
        if (headerRead == 0)
            return new JournalReplayResult { FramesApplied = 0, TruncatedTailBytes = 0, CleanEnd = true };
        if (headerRead < 8 || !header[..4].SequenceEqual("TSSJ"u8))
            throw new InvalidDataException("journal 文件头损坏");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 1)
            throw new InvalidDataException("journal 版本不支持");

        int frames = 0;
        byte[] lenBuf = new byte[4];
        byte[] crcBuf = new byte[4];

        while (true)
        {
            long frameStart = stream.Position;
            int op = stream.ReadByte();
            if (op < 0) return Result(clean: true, frameStart);

            if (op is not (OpUpsert or OpRemove)) return Result(clean: false, frameStart);
            if (ReadFull(stream, lenBuf) < 4) return Result(clean: false, frameStart);
            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len < 0 || len > MaxPayloadBytes) return Result(clean: false, frameStart);

            byte[] payload = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                if (ReadFull(stream, payload.AsSpan(0, len)) < len) return Result(clean: false, frameStart);
                if (ReadFull(stream, crcBuf) < 4) return Result(clean: false, frameStart);
                if (Crc32.Compute(payload.AsSpan(0, len)) != BinaryPrimitives.ReadUInt32LittleEndian(crcBuf))
                    return Result(clean: false, frameStart);

                if (op == OpRemove)
                {
                    apply(new JournalEvent(
                        FileReference: BinaryPrimitives.ReadInt64LittleEndian(payload),
                        ParentReference: 0, Name: string.Empty, Size: 0,
                        ModifiedUtc: DateTimeOffset.MinValue, IsDelete: true));
                }
                else
                {
                    long fileRef = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0));
                    long parentRef = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8));
                    long size = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(16));
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(24));
                    int nameLen = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(32));
                    if (nameLen < 0 || 36 + nameLen > len) return Result(clean: false, frameStart);
                    string name = Encoding.UTF8.GetString(payload.AsSpan(36, nameLen));
                    apply(new JournalEvent(fileRef, parentRef, name, size,
                        new DateTimeOffset(ticks, TimeSpan.Zero), IsDelete: false));
                }
                frames++;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        JournalReplayResult Result(bool clean, long frameStart) => new()
        {
            FramesApplied = frames,
            // 残留字节按坏帧起点计：撕裂/坏帧时读失败已把位置推进到文件尾，Length-Position 恒为 0
            TruncatedTailBytes = stream.CanSeek ? Math.Max(0, stream.Length - frameStart) : 0,
            CleanEnd = clean,
        };
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    public void Flush() { ThrowIfDisposed(); _stream.Flush(); }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_leaveOpen) _stream.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JournalWriter));
    }
}
