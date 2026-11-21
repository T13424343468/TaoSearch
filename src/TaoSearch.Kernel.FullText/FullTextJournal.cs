using System.Buffers.Binary;
using System.Text;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Kernel.FullText;

/// <summary>全文 journal 重放出的单条事件。</summary>
public readonly record struct FullTextEvent(
    long DocId,
    string? Text,          // null = 删除
    DateTimeOffset ModifiedUtc);

/// <summary>全文 journal 重放结果（口径对齐 JournalWriter.Replay）。</summary>
public sealed record FullTextReplayResult
{
    public required int FramesApplied { get; init; }
    public required long TruncatedTailBytes { get; init; }
    public required bool CleanEnd { get; init; }
}

/// <summary>
/// 全文索引增量 journal（对齐 Store.JournalWriter 帧格式与重放语义）：
/// 帧格式 op(1B) | payloadLen(4B) | payload | crc32(4B)；op: 1=Upsert(docId|ticks|textLen|text) 2=Remove(docId)。
/// 每帧写后 Flush；重放遇撕裂/坏帧即停，残留字节按坏帧起点计。
/// </summary>
public static class FullTextJournal
{
    private const int MaxPayloadBytes = 1 << 20;
    private const byte OpUpsert = 1;
    private const byte OpRemove = 2;

    public static void Append(Stream stream, FullTextEvent e)
    {
        byte[] payload;
        if (e.Text is null)
        {
            payload = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(payload, e.DocId);
            WriteFrame(stream, OpRemove, payload);
            return;
        }
        int textBytes = Encoding.UTF8.GetByteCount(e.Text);
        payload = new byte[24 + textBytes];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), e.DocId);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), e.ModifiedUtc.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), textBytes);
        Encoding.UTF8.GetBytes(e.Text, payload.AsSpan(24));
        WriteFrame(stream, OpUpsert, payload);
    }

    private static void WriteFrame(Stream stream, byte op, byte[] payload)
    {
        Span<byte> head = stackalloc byte[5];
        head[0] = op;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], payload.Length);
        stream.Write(head);
        stream.Write(payload);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(payload));
        stream.Write(crc);
        stream.Flush();
    }

    public static FullTextReplayResult Replay(Stream stream, Action<FullTextEvent> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        int frames = 0;
        long frameStart = 0;
        byte[] lenBuf = new byte[4];
        byte[] crcBuf = new byte[4];

        while (true)
        {
            frameStart = stream.Position;
            int op = stream.ReadByte();
            if (op < 0) return Result(clean: true);

            if (op is not (OpUpsert or OpRemove)) return Result(clean: false);
            if (ReadFull(stream, lenBuf) < 4) return Result(clean: false);
            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len < 0 || len > MaxPayloadBytes) return Result(clean: false);

            byte[] payload = new byte[len];
            if (ReadFull(stream, payload) < len) return Result(clean: false);
            if (ReadFull(stream, crcBuf) < 4) return Result(clean: false);
            if (Crc32.Compute(payload) != BinaryPrimitives.ReadUInt32LittleEndian(crcBuf))
                return Result(clean: false);

            if (op == OpRemove)
            {
                apply(new FullTextEvent(BinaryPrimitives.ReadInt64LittleEndian(payload), null, default));
            }
            else
            {
                if (len < 24) return Result(clean: false);
                long docId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0));
                long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8));
                int textBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(16));
                if (textBytes < 0 || 24 + textBytes > len) return Result(clean: false);
                string text = Encoding.UTF8.GetString(payload.AsSpan(24, textBytes));
                apply(new FullTextEvent(docId, text, new DateTimeOffset(ticks, TimeSpan.Zero)));
            }
            frames++;
        }

        FullTextReplayResult Result(bool clean) => new()
        {
            FramesApplied = frames,
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
}
