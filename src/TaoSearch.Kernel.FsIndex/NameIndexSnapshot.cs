using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.Kernel.FsIndex;

/// <summary>
/// NameIndex 快照序列化（M3 Store，架构 §4.3）。
/// 二进制格式（小端）：头 24B（魔数 TSSN | 版本 u32 | 活跃记录数 i32 | 名字区字节 i64 | 头 CRC u32）
/// + 名字区（活跃记录名字按记录顺序拼接，偏移为快照内新偏移）+ 记录区（活跃记录定长 39B）
/// + 尾 CRC u32（正文流式校验）。
/// 删除记录与孤儿名字在快照中一并压缩；加载时校验魔数/版本/双 CRC，损坏抛 InvalidDataException 由上层走全量重建。
/// </summary>
public sealed partial class NameIndex
{
    private static ReadOnlySpan<byte> SnapshotMagic => "TSSN"u8;
    private const int SnapshotVersion = 1;
    private const int SnapshotHeaderSize = 24;
    private const int RecSize = 39;   // 8+8+8+8+4+2+1，Pack=1 无对齐填充

    /// <summary>恢复专用构造：按已知规模预分配，避免恢复过程反复扩容。</summary>
    private unsafe NameIndex(int recordCapacity, int namesCapacity)
    {
        _namesCapacity = Math.Max(1 << 16, RoundUpPow2(Math.Max(1, namesCapacity)));
        _names = (byte*)NativeMemory.Alloc((nuint)_namesCapacity);
        _recordsCapacity = Math.Max(InitialCapacity, recordCapacity);
        _records = (Rec*)NativeMemory.Alloc((nuint)_recordsCapacity, (nuint)sizeof(Rec));

        int hashCap = InitialCapacity;
        while (hashCap * LoadFactor < recordCapacity + 1) hashCap *= 2;
        AllocHash(hashCap);
    }

    private static int RoundUpPow2(int value)
    {
        int cap = 1;
        while (cap < value) cap *= 2;
        return cap;
    }

    /// <summary>快照落盘：只写活跃记录（删除记录压缩掉）。</summary>
    public unsafe void SaveSnapshot(Stream output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);

        int active = 0;
        long nameBytes = 0;
        for (int i = 0; i < _recordCount; i++)
        {
            ref Rec r = ref _records[i];
            if ((r.Flags & RecFlagDeleted) != 0) continue;
            active++;
            nameBytes += r.NameLength;
        }

        Span<byte> header = stackalloc byte[SnapshotHeaderSize];
        SnapshotMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], SnapshotVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], active);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], nameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Crc32.Compute(header[..20]));
        output.Write(header);

        // 不用 using：BufferedStream 释放会关闭底层流，调用方的流必须保持打开；
        // 结尾只 Flush 把缓冲落到底层流，不 Dispose
        Stream sink = output is BufferedStream bs ? bs : new BufferedStream(output, 1 << 20);
        uint bodyCrc = 0xFFFF_FFFFu;

        // 两遍写：先全部名字区，再全部记录区（与 LoadSnapshot 的块布局一致）
        for (int i = 0; i < _recordCount; i++)
        {
            ref Rec r = ref _records[i];
            if ((r.Flags & RecFlagDeleted) != 0) continue;
            var name = new ReadOnlySpan<byte>(_names + r.NameOffset, r.NameLength);
            sink.Write(name);
            bodyCrc = Crc32.Update(bodyCrc, name);
        }

        byte[] recBuf = new byte[RecSize];
        int recOffset = 0;
        for (int i = 0; i < _recordCount; i++)
        {
            ref Rec r = ref _records[i];
            if ((r.Flags & RecFlagDeleted) != 0) continue;
            WriteRecord(recBuf, r.FileReference, r.ParentReference, r.Size, r.ModifiedUtcTicks, recOffset, r.NameLength);
            sink.Write(recBuf);
            bodyCrc = Crc32.Update(bodyCrc, recBuf);
            recOffset += r.NameLength;
        }

        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32.Final(bodyCrc));
        sink.Write(trailer);
        sink.Flush();
    }

    /// <summary>从快照恢复：校验并重建哈希；损坏抛 InvalidDataException。</summary>
    public static NameIndex LoadSnapshot(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        Span<byte> header = stackalloc byte[SnapshotHeaderSize];
        ReadExactlyOrThrow(input, header, "快照头不完整");
        if (!header[..4].SequenceEqual(SnapshotMagic))
            throw new InvalidDataException("快照魔数不符");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != SnapshotVersion)
            throw new InvalidDataException($"快照版本不支持：{version}");
        if (Crc32.Compute(header[..20]) != BinaryPrimitives.ReadUInt32LittleEndian(header[20..]))
            throw new InvalidDataException("快照头校验失败");

        int active = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        long nameBytes = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
        if (active < 0 || nameBytes < 0 || nameBytes > int.MaxValue)
            throw new InvalidDataException("快照头数值非法");

        byte[] names = new byte[nameBytes];
        ReadExactlyOrThrow(input, names, "快照名字区不完整");
        uint bodyCrc = Crc32.Update(0xFFFF_FFFFu, names);

        var index = new NameIndex(recordCapacity: active, namesCapacity: (int)nameBytes);
        byte[] recBuf = new byte[RecSize];
        try
        {
            int runningOffset = 0;
            for (int n = 0; n < active; n++)
            {
                ReadExactlyOrThrow(input, recBuf, "快照记录区不完整");
                bodyCrc = Crc32.Update(bodyCrc, recBuf);

                long fileRef = BinaryPrimitives.ReadInt64LittleEndian(recBuf.AsSpan(0));
                long parentRef = BinaryPrimitives.ReadInt64LittleEndian(recBuf.AsSpan(8));
                long size = BinaryPrimitives.ReadInt64LittleEndian(recBuf.AsSpan(16));
                long mtime = BinaryPrimitives.ReadInt64LittleEndian(recBuf.AsSpan(24));
                int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(recBuf.AsSpan(32));
                short nameLen = BinaryPrimitives.ReadInt16LittleEndian(recBuf.AsSpan(36));
                byte flags = recBuf[38];

                if ((flags & RecFlagDeleted) != 0)
                    throw new InvalidDataException("快照记录区含已删除记录（应为压缩后快照）");
                if (nameLen < 0 || runningOffset + nameLen > names.Length || nameOffset != runningOffset)
                    throw new InvalidDataException("快照记录名字偏移非法");

                index.LoadRecord(fileRef, parentRef, names.AsSpan(runningOffset, nameLen), size, mtime);
                runningOffset += nameLen;
            }

            Span<byte> trailer = stackalloc byte[4];
            ReadExactlyOrThrow(input, trailer, "快照尾校验不完整");
            if (Crc32.Final(bodyCrc) != BinaryPrimitives.ReadUInt32LittleEndian(trailer))
                throw new InvalidDataException("快照正文校验失败");
            return index;
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    private static void WriteRecord(Span<byte> dst, long fileReference, long parentReference, long size, long modifiedFileTimeUtcTicks, int nameOffset, short nameLength)
    {
        BinaryPrimitives.WriteInt64LittleEndian(dst, fileReference);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), parentReference);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(16), size);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(24), modifiedFileTimeUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(32), nameOffset);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(36), nameLength);
        dst[38] = 0;
    }

    private static void ReadExactlyOrThrow(Stream input, Span<byte> buffer, string message)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read <= 0) throw new InvalidDataException(message);
            total += read;
        }
    }

    /// <summary>恢复专用：名字字节直接入聚合区并插入哈希（不经字符串编码）。</summary>
    private unsafe void LoadRecord(long fileReference, long parentReference, ReadOnlySpan<byte> nameUtf8, long size, long modifiedFileTimeUtcTicks)
    {
        int nameOffset = AppendNameBytes(nameUtf8);
        EnsureRecordsCapacity(_recordCount + 1);
        _records[_recordCount] = new Rec
        {
            FileReference = fileReference,
            ParentReference = parentReference,
            Size = size,
            ModifiedUtcTicks = modifiedFileTimeUtcTicks,
            NameOffset = nameOffset,
            NameLength = (short)nameUtf8.Length,
            Flags = 0,
        };
        _recordCount++;

        if ((_count + 1) > _hashMask * LoadFactor) GrowHash();
        InsertHash(fileReference, _recordCount - 1);
    }

    private unsafe int AppendNameBytes(ReadOnlySpan<byte> nameUtf8)
    {
        EnsureNamesCapacity(_namesUsed + nameUtf8.Length);
        int offset = _namesUsed;
        nameUtf8.CopyTo(new Span<byte>(_names + offset, nameUtf8.Length));
        _namesUsed += nameUtf8.Length;
        return offset;
    }
}
