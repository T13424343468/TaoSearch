using System.Buffers.Binary;
using System.Text;
using System.Collections.Concurrent;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Kernel.FullText;

/// <summary>
/// 倒排索引快照序列化（M3 全文持久化，对齐 NameIndexSnapshot 格式风格）。
/// 二进制格式（小端）：
///   头 28B：魔数 TSFT | 版本 u32 | 词条数 i32 | 文档数 i32 | 词条区字节 i64 | 头 CRC u32
///   词条区：词条 UTF-8 字节按词条顺序拼接（词条定长记录内存偏移）
///   词条记录区：每词条定长 24B（词条偏移 i32 | 词条字节长 i32 | 文档数 i32 | 文档 ID 列表偏移 i64 | 文档 ID 列表字节长 i32）
///   文档区：每文档定长 16B（docId i64 | ModifiedUtc.UtcTicks i64）
///   词频区：每词条每文档词频 i32，按词条记录顺序、词条内按文档 ID 升序拼接（词频=位置数）
///   文档 ID 列表区：各词条命中文档 ID（i64）顺序拼接
///   尾 CRC u32（正文流式校验）。
/// 加载校验魔数/版本/双 CRC，损坏抛 InvalidDataException 由上层走全量重建。
/// </summary>
public sealed partial class InvertedIndex
{
    private static ReadOnlySpan<byte> SnapshotMagic => "TSFT"u8;
    private const int SnapshotVersion = 1;
    private const int SnapshotHeaderSize = 28;
    private const int TermRecSize = 24;   // 4+4+4+8+4
    private const int DocRecSize = 16;    // 8+8
    private const int DocIdSize = 8;

    /// <summary>快照落盘：全量词条与文档元数据。</summary>
    public void SaveSnapshot(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // 统计词条区与文档 ID 列表区大小
        var terms = _postings.Keys.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        long termBytes = 0;
        long docIdBytes = 0;
        foreach (var t in terms)
        {
            termBytes += Encoding.UTF8.GetByteCount(t);
            docIdBytes += (long)_postings[t].Count * DocIdSize;
        }

        uint bodyCrc = 0xFFFF_FFFFu;
        Stream sink = output;

        Span<byte> header = stackalloc byte[SnapshotHeaderSize];
        SnapshotMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], SnapshotVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], terms.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], _docs.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], termBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Crc32.Compute(header[..24]));
        sink.Write(header);
        bodyCrc = Crc32.Update(bodyCrc, header[..24]);

        // 词条区
        byte[] termBuf = new byte[termBytes];
        int termOffset = 0;
        foreach (var t in terms)
        {
            int len = Encoding.UTF8.GetBytes(t, termBuf.AsSpan(termOffset));
            bodyCrc = Crc32.Update(bodyCrc, termBuf.AsSpan(termOffset, len));
            termOffset += len;
        }
        sink.Write(termBuf);

        // 词条记录区（记录词条区偏移与文档 ID 列表偏移）
        byte[] recBuf = new byte[TermRecSize];
        long docIdOffset = 0;
        int termStart = 0;
        foreach (var t in terms)
        {
            var bucket = _postings[t];
            int tBytes = Encoding.UTF8.GetByteCount(t);
            BinaryPrimitives.WriteInt32LittleEndian(recBuf.AsSpan(0), termStart);
            BinaryPrimitives.WriteInt32LittleEndian(recBuf.AsSpan(4), tBytes);
            BinaryPrimitives.WriteInt32LittleEndian(recBuf.AsSpan(8), bucket.Count);
            BinaryPrimitives.WriteInt64LittleEndian(recBuf.AsSpan(12), docIdOffset);
            BinaryPrimitives.WriteInt32LittleEndian(recBuf.AsSpan(20), (int)(bucket.Count * DocIdSize));
            sink.Write(recBuf);
            bodyCrc = Crc32.Update(bodyCrc, recBuf);
            termStart += tBytes;
            docIdOffset += bucket.Count * DocIdSize;
        }

        // 文档区
        byte[] docBuf = new byte[DocRecSize];
        foreach (var (docId, meta) in _docs.OrderBy(kv => kv.Key))
        {
            BinaryPrimitives.WriteInt64LittleEndian(docBuf.AsSpan(0), docId);
            BinaryPrimitives.WriteInt64LittleEndian(docBuf.AsSpan(8), meta.ModifiedUtc.UtcTicks);
            sink.Write(docBuf);
            bodyCrc = Crc32.Update(bodyCrc, docBuf);
        }

        // 文档 ID 列表区
        byte[] idBuf = new byte[docIdBytes];
        long idOffset = 0;
        foreach (var t in terms)
        {
            foreach (var docId in _postings[t].Keys.OrderBy(d => d))
            {
                BinaryPrimitives.WriteInt64LittleEndian(idBuf.AsSpan((int)idOffset), docId);
                idOffset += DocIdSize;
            }
        }
        sink.Write(idBuf);
        bodyCrc = Crc32.Update(bodyCrc, idBuf);

        // 词频区（与文档 ID 列表同序）
        byte[] tfBuf = new byte[docIdBytes / DocIdSize * 4];
        int tfOffset = 0;
        foreach (var t in terms)
        {
            foreach (var docId in _postings[t].Keys.OrderBy(d => d))
            {
                BinaryPrimitives.WriteInt32LittleEndian(tfBuf.AsSpan(tfOffset), _postings[t][docId].Positions.Count);
                tfOffset += 4;
            }
        }
        sink.Write(tfBuf);
        bodyCrc = Crc32.Update(bodyCrc, tfBuf);

        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32.Final(bodyCrc));
        sink.Write(trailer);
        sink.Flush();
    }

    /// <summary>从快照恢复（损坏抛 InvalidDataException）。</summary>
    public static InvertedIndex LoadSnapshot(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        Span<byte> header = stackalloc byte[SnapshotHeaderSize];
        if (ReadFull(input, header) < SnapshotHeaderSize)
            throw new InvalidDataException("快照文件头不完整");
        if (!header[..4].SequenceEqual(SnapshotMagic))
            throw new InvalidDataException("快照魔数不符");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != SnapshotVersion)
            throw new InvalidDataException("快照版本不支持");
        if (Crc32.Compute(header[..24]) != BinaryPrimitives.ReadUInt32LittleEndian(header[24..]))
            throw new InvalidDataException("快照文件头 CRC 校验失败");

        int termCount = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int docCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        long termBytes = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        if (termCount < 0 || docCount < 0 || termBytes < 0 || termBytes > int.MaxValue)
            throw new InvalidDataException("快照头部数值非法");

        uint bodyCrc = 0xFFFF_FFFFu;
        bodyCrc = Crc32.Update(bodyCrc, header[..24]);   // 与写侧同口径：正文 CRC 从头开始

        // 词条区
        byte[] termBuf = new byte[termBytes];
        ReadFull(input, termBuf);
        bodyCrc = Crc32.Update(bodyCrc, termBuf);

        // 词条记录区
        byte[] recBuf = new byte[TermRecSize];
        var termRecords = new (int Offset, int Length, int DocIdCount, long DocIdOffset, int DocIdBytes)[termCount];
        for (int i = 0; i < termCount; i++)
        {
            if (ReadFull(input, recBuf) < TermRecSize) throw new InvalidDataException("词条记录区不完整");
            bodyCrc = Crc32.Update(bodyCrc, recBuf);
            termRecords[i] = (
                BinaryPrimitives.ReadInt32LittleEndian(recBuf.AsSpan(0)),
                BinaryPrimitives.ReadInt32LittleEndian(recBuf.AsSpan(4)),
                BinaryPrimitives.ReadInt32LittleEndian(recBuf.AsSpan(8)),
                BinaryPrimitives.ReadInt64LittleEndian(recBuf.AsSpan(12)),
                BinaryPrimitives.ReadInt32LittleEndian(recBuf.AsSpan(20)));
        }

        // 文档区
        byte[] docBuf = new byte[DocRecSize];
        var index = new InvertedIndex();
        for (int i = 0; i < docCount; i++)
        {
            if (ReadFull(input, docBuf) < DocRecSize) throw new InvalidDataException("文档区不完整");
            bodyCrc = Crc32.Update(bodyCrc, docBuf);
            long docId = BinaryPrimitives.ReadInt64LittleEndian(docBuf.AsSpan(0));
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(docBuf.AsSpan(8));
            index._docs[docId] = new DocMeta(docId, new DateTimeOffset(ticks, TimeSpan.Zero));
        }

        // 文档 ID 列表区
        long docIdBytes = 0;
        foreach (var (_, _, _, _, idBytes) in termRecords) docIdBytes += idBytes;
        byte[] idBuf = new byte[docIdBytes];
        if (ReadFull(input, idBuf) < docIdBytes) throw new InvalidDataException("文档 ID 列表区不完整");
        bodyCrc = Crc32.Update(bodyCrc, idBuf);

        // 词频区（每词条每文档一个 i32，顺序与文档 ID 列表一致）
        long tfBytes = docIdBytes / DocIdSize * 4;
        byte[] tfBuf = new byte[tfBytes];
        if (ReadFull(input, tfBuf) < tfBytes) throw new InvalidDataException("词频区不完整");
        bodyCrc = Crc32.Update(bodyCrc, tfBuf);

        Span<byte> trailer = stackalloc byte[4];
        if (ReadFull(input, trailer) < 4) throw new InvalidDataException("快照尾 CRC 缺失");
        if (Crc32.Final(bodyCrc) != BinaryPrimitives.ReadUInt32LittleEndian(trailer))
            throw new InvalidDataException("快照正文 CRC 校验失败");

        // 重建词条 → 文档映射（词频按序回填）
        long tfCursor = 0;
        for (int i = 0; i < termCount; i++)
        {
            var (offset, length, idCount, idOffset, idBytes) = termRecords[i];
            if (offset < 0 || offset + length > termBytes || idCount < 0 || idOffset + idBytes > docIdBytes)
                throw new InvalidDataException("词条记录越界");
            string term = Encoding.UTF8.GetString(termBuf.AsSpan(offset, length));
            var bucket = new ConcurrentDictionary<long, Posting>();
            for (int d = 0; d < idCount; d++)
            {
                long docId = BinaryPrimitives.ReadInt64LittleEndian(idBuf.AsSpan((int)idOffset + d * DocIdSize, DocIdSize));
                if (!index._docs.ContainsKey(docId)) throw new InvalidDataException("词条引用未定义文档");
                int tf = BinaryPrimitives.ReadInt32LittleEndian(tfBuf.AsSpan((int)tfCursor, 4));
                tfCursor += 4;
                var posting = new Posting(docId);
                for (int p = 0; p < tf; p++) posting.Positions.Add(0);   // 位置不落盘（快照口径只保词频），占位保证词频正确
                bucket[docId] = posting;
            }
            index._postings[term] = bucket;
        }
        return index;
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
