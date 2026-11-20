using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace TaoSearch.Kernel.FsIndex;

/// <summary>
/// 文件名索引（架构 §4 的最小可用版，M2 起点基线）：
/// 记录数组 + 开放寻址哈希（引用号→下标）+ 父引用号链路径重建。
/// 当前为托管数组原型，正确性优先；非托管内存版（NativeMemory）按 M2 基准测试结果替换，接口不变。
/// </summary>
public sealed class NameIndex
{
    private const double LoadFactor = 0.7;

    private long[] _keys = new long[1024];          // 文件引用号
    private int[] _values = NewEmpty(1024);         // 记录下标（-1 空槽 / -2 墓碑）
    private int _hashMask = 1023;
    private int _count;

    private NameRecord[] _records = new NameRecord[1024];
    private int _recordCount;

    /// <summary>分配全 -1（空槽）的值数组。</summary>
    private static int[] NewEmpty(int capacity)
    {
        var v = new int[capacity];
        Array.Fill(v, -1);
        return v;
    }

    /// <summary>写入或更新一条文件记录。</summary>
    public void Upsert(long fileReference, long parentReference, string fileName, long size, DateTimeOffset modifiedUtc)
    {
        if (_recordCount == _records.Length) Array.Resize(ref _records, _records.Length * 2);
        int idx = _recordCount++;

        _records[idx] = new NameRecord(parentReference, fileName, size, modifiedUtc);

        // 引用号已存在则覆盖记录（重命名/更新场景）
        if (TryFind(fileReference, out int existing))
        {
            _records[existing] = _records[idx];
            _recordCount--;
            return;
        }

        if ((_count + 1) > _hashMask * LoadFactor) GrowHash();
        InsertHash(fileReference, idx);
    }

    public bool TryGet(long fileReference, out NameRecord record)
    {
        record = default;
        if (!TryFind(fileReference, out int idx)) return false;
        record = _records[idx];
        return true;
    }

    public bool Remove(long fileReference)
    {
        if (!TryFind(fileReference, out int idx)) return false;
        _records[idx] = _records[idx] with { Deleted = true };
        RemoveHash(fileReference);
        return true;
    }

    /// <summary>按父引用号链向上重建完整路径（架构 §4.2：不存全路径）。</summary>
    public string BuildPath(long fileReference)
    {
        if (!TryGet(fileReference, out var record)) return string.Empty;
        var parts = new Stack<string>();
        parts.Push(record.FileName);
        long parent = record.ParentReference;
        int depth = 0;
        while (parent != 0 && depth++ < 256) // 深度上限防环
        {
            if (!TryGet(parent, out var parentRecord)) break;
            parts.Push(parentRecord.FileName);
            parent = parentRecord.ParentReference;
        }
        return string.Join('\\', parts);
    }

    /// <summary>子串匹配扫描（生产版换 SIMD SearchValues + 非托管内存，接口不变）。</summary>
    public IEnumerable<long> Search(string term, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        for (int i = 0; i < _recordCount; i++)
        {
            var r = _records[i];
            if (r.Deleted) continue;
            if (r.FileName.Contains(term, comparison)) yield return RefAt(i);
        }
    }

    public int Count => _count;

    // ---- 哈希（开放寻址，删除用墓碑位） ----
    private readonly HashSet<long> _deletedKeys = [];

    private bool TryFind(long key, out int recordIdx)
    {
        recordIdx = default;
        int mask = _hashMask;
        int i = (int)(Mix(key) & (uint)mask);
        while (true)
        {
            var k = _keys[i];
            if (k == 0 && _values[i] == -1) return false;      // 空槽，终止
            if (k == key && _values[i] >= 0) { recordIdx = _values[i]; return true; }
            i = (i + 1) & mask;
        }
    }

    private void InsertHash(long key, int recordIdx)
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

    private void RemoveHash(long key)
    {
        if (!TryFind(key, out int idx)) return;
        int mask = _hashMask;
        int i = (int)(Mix(key) & (uint)mask);
        while (_keys[i] != key || _values[i] != idx) i = (i + 1) & mask;
        _values[i] = -2; // 墓碑：-1 空槽 / -2 已删
        _count--;
    }

    private void GrowHash()
    {
        var oldKeys = _keys; var oldValues = _values; var oldMask = _hashMask;
        int newCap = (_hashMask + 1) * 2;
        _keys = new long[newCap];
        _values = NewEmpty(newCap);
        _hashMask = newCap - 1;
        _count = 0;
        for (int i = 0; i <= oldMask; i++)
            if (oldValues[i] >= 0) InsertHash(oldKeys[i], oldValues[i]);
    }

    private long RefAt(int recordIdx)
    {
        // 反查引用号：遍历哈希（原型版可接受；生产版记录数组冗余存引用号）
        for (int i = 0; i <= _hashMask; i++)
            if (_values[i] == recordIdx) return _keys[i];
        return 0;
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

/// <summary>单条文件记录（架构 §4.2：定长紧凑，生产版落非托管内存）。</summary>
public readonly record struct NameRecord(
    long ParentReference,
    string FileName,
    long Size,
    DateTimeOffset ModifiedUtc)
{
    public bool Deleted { get; init; }
}
