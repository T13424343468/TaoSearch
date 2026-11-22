using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace TaoSearch.Kernel.FsIndex;

/// <summary>
/// 文件名索引（架构 §4.2 非托管内存版）：
/// 文件名聚合区（UTF-8 大块非托管内存）+ 定长记录数组（约 40B/文件）+ 开放寻址哈希（引用号→下标），
/// 全部走 NativeMemory，服务进程 .NET 堆保持小而稳（GC 隔离）；父引用号链路径重建，不存全路径。
/// 子串扫描用 SearchValues 命中候选位（SIMD 加速），UTF-8 字节级 ASCII 大小写折叠比较
/// （非 ASCII 字节精确匹配，语义与 OrdinalIgnoreCase 对 UTF-8 一致）。
/// </summary>
public sealed partial class NameIndex : IDisposable
{
    private const double LoadFactor = 0.7;
    private const int InitialCapacity = 1024;
    private const byte RecFlagDeleted = 0b1;

    // ---- 非托管存储 ----
    private unsafe byte* _names;              // 文件名聚合区（UTF-8 拼接）
    private int _namesCapacity;        // 聚合区容量（字节）
    private int _namesUsed;            // 聚合区已用（字节）

    private unsafe Rec* _records;             // 定长记录数组
    private int _recordsCapacity;
    private int _recordCount;          // 含已删除记录（删除不回收下标与名字节，压缩留 M3）

    private unsafe long* _keys;               // 哈希：文件引用号
    private unsafe int* _values;              // 哈希：记录下标（-1 空槽 / -2 墓碑）
    private int _hashMask;             // 容量-1（容量为 2 的幂）
    private int _count;                // 活跃条目数

    private bool _disposed;

    public unsafe NameIndex()
    {
        _namesCapacity = 1 << 16;                       // 64KB 起步
        _names = (byte*)NativeMemory.Alloc((nuint)_namesCapacity);
        _recordsCapacity = InitialCapacity;
        _records = (Rec*)NativeMemory.Alloc((nuint)InitialCapacity, (nuint)sizeof(Rec));
        AllocHash(InitialCapacity);
    }

    ~NameIndex() => Dispose(false);

    /// <summary>定长记录（顺序布局，8+8+8+8+4+2+1 = 39B，对齐后 40B）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Rec
    {
        public long FileReference;
        public long ParentReference;
        public long Size;
        public long ModifiedUtcTicks; // .NET ticks（0001 纪元），非 FILETIME——读写两侧必须同纪元
        public int NameOffset;
        public short NameLength;
        public byte Flags;          // bit0: Deleted
    }

    /// <summary>写入或更新一条文件记录（同引用号覆盖，重命名/更新场景）。</summary>
    public unsafe void Upsert(long fileReference, long parentReference, string fileName, long size, DateTimeOffset modifiedUtc)
    {
        ThrowIfDisposed();
        int nameLen = Encoding.UTF8.GetByteCount(fileName);
        int nameOffset = AppendName(fileName, nameLen);

        EnsureRecordsCapacity(_recordCount + 1);
        int idx = _recordCount++;
        _records[idx] = new Rec
        {
            FileReference = fileReference,
            ParentReference = parentReference,
            Size = size,
            ModifiedUtcTicks = modifiedUtc.UtcTicks,
            NameOffset = nameOffset,
            NameLength = (short)nameLen,
            Flags = 0,
        };

        // 引用号已存在则覆盖记录（重命名/更新场景）：新记录顶掉旧记录，回退计数
        if (TryFind(fileReference, out int existing))
        {
            _records[existing] = _records[idx];
            _recordCount--;
            return;
        }

        if ((_count + 1) > _hashMask * LoadFactor) GrowHash();
        InsertHash(fileReference, idx);
    }

    public unsafe bool TryGet(long fileReference, out NameRecord record)
    {
        ThrowIfDisposed();
        record = default;
        if (!TryFind(fileReference, out int idx)) return false;
        record = ToNameRecord(in _records[idx]);
        return true;
    }

    public unsafe bool Remove(long fileReference)
    {
        ThrowIfDisposed();
        if (!TryFind(fileReference, out int idx)) return false;
        _records[idx].Flags |= RecFlagDeleted;
        RemoveHash(fileReference);
        return true;
    }

    /// <summary>
    /// 清空索引（重建口径）：哈希槽全部置空、记录与聚合区游标归零，已分配内存保留复用。
    /// 关7实弹口径：停机期删除的文件重建后不得残留——重建必须先清后建，反映磁盘现状。
    /// </summary>
    public unsafe void Clear()
    {
        ThrowIfDisposed();
        int capacity = _hashMask + 1;
        for (int i = 0; i < capacity; i++)
        {
            _keys[i] = 0;
            _values[i] = -1;
        }
        _namesUsed = 0;
        _recordCount = 0;
        _count = 0;
    }

    /// <summary>按父引用号链向上重建完整路径（架构 §4.2：不存全路径）。深度上限防环。</summary>
    public unsafe string BuildPath(long fileReference)
    {
        ThrowIfDisposed();
        if (!TryFind(fileReference, out int idx)) return string.Empty;

        // 收集父链各段（路径重建非查询热路径，正确性优先）
        var parts = new List<(int Offset, int Len)>(16);
        long currentRef = fileReference;
        int depth = 0;
        while (currentRef != 0 && depth++ < 256)
        {
            if (!TryFind(currentRef, out int i)) break;
            ref Rec r = ref _records[i];
            parts.Add((r.NameOffset, r.NameLength));
            currentRef = r.ParentReference;
        }

        int totalBytes = 0;
        for (int p = parts.Count - 1; p >= 0; p--)
        {
            totalBytes += parts[p].Len;
            if (p > 0) totalBytes++;                        // 分隔符
        }

        var buffer = totalBytes > 0 ? new byte[totalBytes] : [];
        int write = 0;
        for (int p = parts.Count - 1; p >= 0; p--)
        {
            int len = parts[p].Len;
            if (len > 0)
            {
                fixed (byte* dst = buffer)
                {
                    Buffer.MemoryCopy(_names + parts[p].Offset, dst + write, len, len);
                }
            }
            write += len;
            if (p > 0) buffer[write++] = (byte)'\\';
        }
        return Encoding.UTF8.GetString(buffer, 0, write);
    }

    /// <summary>
    /// 子串匹配扫描：UTF-8 字节级比较，ASCII 大小写折叠（中文等非 ASCII 字节精确匹配，语义与 OrdinalIgnoreCase 一致）。
    /// 候选位用 SearchValues（首字节双态）命中，SIMD 加速；comparison 仅区分 Ordinal（区分大小写）与忽略大小写。
    /// </summary>
    public unsafe IEnumerable<long> Search(string term, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(term)) return [];
        bool caseSensitive = comparison == StringComparison.Ordinal;

        byte[] termBytes = Encoding.UTF8.GetBytes(term);
        if (!caseSensitive) LowerAscii(termBytes);

        byte first = termBytes[0];
        var firstCandidates = SearchValues.Create(
            !caseSensitive && IsAsciiLetter(first) ? [LowerAscii(first), UpperAscii(first)] : [first]);

        var results = new List<long>();
        for (int i = 0; i < _recordCount; i++)
        {
            ref Rec r = ref _records[i];
            if ((r.Flags & RecFlagDeleted) != 0) continue;
            var name = new ReadOnlySpan<byte>(_names + r.NameOffset, r.NameLength);
            if (!ContainsUtf8(name, termBytes, firstCandidates, caseSensitive)) continue;
            results.Add(r.FileReference);
        }
        return results;
    }

    public unsafe int Count => _count;

    /// <summary>
    /// 枚举全部活跃记录（快照导出用，非查询热路径；删除记录不产出）。
    /// 迭代器体内不允许指针：先物化为列表再返回。
    /// </summary>
    public IEnumerable<(long FileReference, long ParentReference, string FileName, long Size, DateTimeOffset ModifiedUtc)> EnumerateLive()
    {
        ThrowIfDisposed();
        var rows = new List<(long FileReference, long ParentReference, string FileName, long Size, DateTimeOffset ModifiedUtc)>(_count);
        CollectLive(rows);
        return rows;
    }

    private unsafe void CollectLive(List<(long FileReference, long ParentReference, string FileName, long Size, DateTimeOffset ModifiedUtc)> rows)
    {
        for (int i = 0; i < _recordCount; i++)
        {
            ref Rec r = ref _records[i];
            if ((r.Flags & RecFlagDeleted) != 0) continue;
            string name = r.NameLength > 0
                ? Encoding.UTF8.GetString(_names + r.NameOffset, r.NameLength)
                : string.Empty;
            rows.Add((r.FileReference, r.ParentReference, name, r.Size, new DateTimeOffset(r.ModifiedUtcTicks, TimeSpan.Zero)));
        }
    }

    /// <summary>当前实际分配的非托管内存（字节）——内存预算表（架构 §4.4）逐项实测口径。</summary>
    public unsafe long AllocatedBytes =>
        _namesCapacity + (long)_recordsCapacity * sizeof(Rec) + (long)(_hashMask + 1) * (sizeof(long) + sizeof(int));

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private unsafe void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (_names != null) NativeMemory.Free(_names);
        if (_records != null) NativeMemory.Free(_records);
        if (_keys != null) NativeMemory.Free(_keys);
        if (_values != null) NativeMemory.Free(_values);
        _names = null; _records = null; _keys = null; _values = null;
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NameIndex));
    }

    // ---- 名称聚合区 ----
    private unsafe int AppendName(string fileName, int byteLen)
    {
        EnsureNamesCapacity(_namesUsed + byteLen);
        int offset = _namesUsed;
        fixed (char* src = fileName)
        {
            _namesUsed += Encoding.UTF8.GetBytes(src, fileName.Length, _names + offset, byteLen);
        }
        return offset;
    }

    private unsafe void EnsureNamesCapacity(int required)
    {
        if (required <= _namesCapacity) return;
        int newCap = _namesCapacity;
        while (newCap < required) newCap *= 2;
        byte* fresh = (byte*)NativeMemory.Alloc((nuint)newCap);
        Buffer.MemoryCopy(_names, fresh, newCap, _namesUsed);
        NativeMemory.Free(_names);
        _names = fresh;
        _namesCapacity = newCap;
    }

    private unsafe void EnsureRecordsCapacity(int required)
    {
        if (required <= _recordsCapacity) return;
        int newCap = _recordsCapacity;
        while (newCap < required) newCap *= 2;
        Rec* fresh = (Rec*)NativeMemory.Alloc((nuint)newCap, (nuint)sizeof(Rec));
        Buffer.MemoryCopy(_records, fresh, (long)newCap * sizeof(Rec), (long)_recordsCapacity * sizeof(Rec));
        NativeMemory.Free(_records);
        _records = fresh;
        _recordsCapacity = newCap;
    }

    // ---- 哈希（开放寻址，删除用墓碑位） ----
    private unsafe void AllocHash(int capacity)
    {
        _keys = (long*)NativeMemory.AllocZeroed((nuint)capacity, sizeof(long));   // 键必须清零：空槽哨兵判 k==0
        _values = (int*)NativeMemory.Alloc((nuint)capacity, sizeof(int));
        for (int i = 0; i < capacity; i++) _values[i] = -1;
        _hashMask = capacity - 1;
        _count = 0;
    }

    private unsafe bool TryFind(long key, out int recordIdx)
    {
        recordIdx = default;
        int mask = _hashMask;
        int i = (int)(Mix(key) & (uint)mask);
        while (true)
        {
            if (_keys[i] == 0 && _values[i] == -1) return false;    // 空槽，终止
            if (_keys[i] == key && _values[i] >= 0) { recordIdx = _values[i]; return true; }
            i = (i + 1) & mask;
        }
    }

    private unsafe void InsertHash(long key, int recordIdx)
    {
        int mask = _hashMask;
        int i = (int)(Mix(key) & (uint)mask);
        while (true)
        {
            if (_values[i] == -1 || (_keys[i] == key && _values[i] >= 0))
            {
                if (_values[i] == -1) _count++;
                _keys[i] = key;
                _values[i] = recordIdx;
                return;
            }
            i = (i + 1) & mask;
        }
    }

    private unsafe void RemoveHash(long key)
    {
        if (!TryFind(key, out int idx)) return;
        int mask = _hashMask;
        int i = (int)(Mix(key) & (uint)mask);
        while (_keys[i] != key || _values[i] != idx) i = (i + 1) & mask;
        _values[i] = -2;    // 墓碑：-1 空槽 / -2 已删
        _count--;
    }

    private unsafe void GrowHash()
    {
        long* oldKeys = _keys; int* oldValues = _values; int oldMask = _hashMask;
        int newCap = (_hashMask + 1) * 2;
        AllocHash(newCap);
        for (int i = 0; i <= oldMask; i++)
            if (oldValues[i] >= 0) InsertHash(oldKeys[i], oldValues[i]);
        NativeMemory.Free(oldKeys);
        NativeMemory.Free(oldValues);
    }

    // ---- 记录换形 ----
    private unsafe NameRecord ToNameRecord(in Rec r)
    {
        string name = r.NameLength > 0
            ? Encoding.UTF8.GetString(_names + r.NameOffset, r.NameLength)
            : string.Empty;
        return new NameRecord(r.ParentReference, name, r.Size, new DateTimeOffset(r.ModifiedUtcTicks, TimeSpan.Zero));
    }

    // ---- UTF-8 子串匹配 ----
    private static bool ContainsUtf8(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerTerm, SearchValues<byte> firstCandidates, bool caseSensitive)
    {
        if (lowerTerm.Length == 0 || haystack.Length < lowerTerm.Length) return false;

        int start = 0;
        while (true)
        {
            int hit = haystack[start..].IndexOfAny(firstCandidates);
            if (hit < 0) return false;
            int pos = start + hit;
            if (pos + lowerTerm.Length > haystack.Length) return false;
            if (MatchAt(haystack, pos, lowerTerm, caseSensitive)) return true;
            start = pos + 1;
        }
    }

    private static bool MatchAt(ReadOnlySpan<byte> haystack, int pos, ReadOnlySpan<byte> lowerTerm, bool caseSensitive)
    {
        for (int i = 0; i < lowerTerm.Length; i++)
        {
            byte h = haystack[pos + i], t = lowerTerm[i];
            if (!caseSensitive) h = LowerAscii(h);
            if (h != t) return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(byte b) => b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z');
    private static byte LowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
    private static byte UpperAscii(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;

    private static void LowerAscii(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++) bytes[i] = LowerAscii(bytes[i]);
    }

    internal static uint Mix(long key)
    {
        ulong h = (ulong)key;
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return (uint)(h ^ (h >> 32));
    }
}

/// <summary>单条文件记录（对外只读视图；FileName 访问时物化为字符串）。</summary>
public readonly record struct NameRecord(
    long ParentReference,
    string FileName,
    long Size,
    DateTimeOffset ModifiedUtc)
{
    public bool Deleted { get; init; }
}
