using System.Buffers.Binary;
using System.Text;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using Xunit;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 分支覆盖补测第二批（测试规范九.11）：NameIndex 哈希桶/路径重建/搜索匹配边界、
/// 文件名快照与全文快照损坏字节流全路径（坏魔数/坏版本/坏头 CRC/数值非法/截断/坏体 CRC/
/// 已删记录/名字偏移非法/慢速流分片读）、InvertedIndex 查询组合、Tokenizer 词元边界。
/// </summary>
public sealed class SnapshotHashBranchTests
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------- NameIndex：路径重建与哈希边界 ----------

    [Fact]
    public void BuildPath_MissingReference_ReturnsEmpty()
    {
        using var idx = new NameIndex();
        Assert.Equal(string.Empty, idx.BuildPath(9999));
    }

    [Fact]
    public void BuildPath_BrokenParentChain_StopsAtMissingParent()
    {
        using var idx = new NameIndex();
        idx.Upsert(2, 9, "child.txt", 1, T1);   // 父引用号 9 不存在
        Assert.Equal("child.txt", idx.BuildPath(2));
    }

    [Fact]
    public void BuildPath_MultiSegment_JoinsInOrderWithBackslash()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "docs", 0, T1);
        idx.Upsert(2, 1, "sub", 0, T1);
        idx.Upsert(3, 2, "a.txt", 10, T1);
        Assert.Equal("docs\\sub\\a.txt", idx.BuildPath(3));
        Assert.Equal("docs\\sub", idx.BuildPath(2));
    }

    [Fact]
    public void Remove_NonexistentReference_ReturnsFalse()
    {
        using var idx = new NameIndex();
        Assert.False(idx.Remove(42));
    }

    [Fact]
    public void Dispose_SecondCallNoThrow_AndUseThrows()
    {
        var idx = new NameIndex();
        idx.Upsert(1, 0, "a.txt", 1, T1);
        idx.Dispose();
        idx.Dispose();  // 二次释放不抛
        Assert.Throws<ObjectDisposedException>(() => idx.Upsert(2, 0, "b.txt", 1, T1));
        Assert.Throws<ObjectDisposedException>(() => idx.BuildPath(1));
    }

    [Fact]
    public void AppendName_GrowsNamesRegion_AllRecordsIntact()
    {
        using var idx = new NameIndex();
        string longName = new('n', 300);
        for (long i = 1; i <= 300; i++)
            idx.Upsert(i, 0, $"{longName}{i}.txt", i, T1);
        Assert.Equal(300, idx.Count);
        Assert.True(idx.TryGet(300, out var rec));
        Assert.EndsWith("300.txt", rec.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyName_RecordKept_SearchEmptyTermReturnsNone()
    {
        using var idx = new NameIndex();
        idx.Upsert(7, 0, string.Empty, 5, T1);
        var rows = idx.EnumerateLive().ToList();
        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0].FileName);
        Assert.Empty(idx.Search(string.Empty));
    }

    [Fact]
    public void Search_CaseAndCharset_BranchMatrix()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "file0001.txt", 1, T1);    // 字母开头候选（大小写双态）
        idx.Upsert(2, 0, "9021.log", 1, T1);        // 数字开头（单候选）
        idx.Upsert(3, 0, "中文文档.txt", 1, T1);     // 非 ASCII 精确匹配
        idx.Upsert(4, 0, "xya", 1, T1);             // 首字节命中但尾部越界

        Assert.Single(idx.Search("FILE0001"));                      // 忽略大小写命中
        Assert.Single(idx.Search("file0001", StringComparison.Ordinal));
        Assert.Empty(idx.Search("FILE0001", StringComparison.Ordinal)); // 区分大小写不命中
        Assert.Single(idx.Search("9021"));                          // 数字首字节
        Assert.Single(idx.Search("中文"));                           // CJK 命中
        Assert.Empty(idx.Search("文中文"));                          // CJK 不命中
        Assert.Empty(idx.Search("ab"));                             // 首字节命中但越界 → 不命中
        Assert.Empty(idx.Search("zz"));                             // 全无候选
    }

    [Fact]
    public void Remove_SkipsDeletedInSearchAndEnumerate()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "gone.txt", 1, T1);
        idx.Upsert(2, 0, "kept.txt", 1, T1);
        Assert.True(idx.Remove(1));
        Assert.Empty(idx.Search("gone"));
        Assert.Single(idx.Search("kept"));
        Assert.Single(idx.EnumerateLive());
        Assert.Equal(1, idx.Count);
    }

    // ---------- NameIndex 快照：损坏字节流全路径 ----------

    private static byte[] BuildNameSnapshot(int recordCount)
    {
        using var idx = new NameIndex();
        for (long i = 1; i <= recordCount; i++)
            idx.Upsert(i, 0, $"file{i:D4}.txt", i * 10, T1);
        using var ms = new MemoryStream();
        idx.SaveSnapshot(ms);
        return ms.ToArray();
    }

    private static (byte[] Header, byte[] Names, byte[] Records, byte[] Trailer) ParseNameSnapshot(byte[] bytes)
    {
        int active = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        long nameBytes = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12));
        return (bytes[..24], bytes[24..(int)(24 + nameBytes)], bytes[(int)(24 + nameBytes)..^4], bytes[^4..]);
    }

    private static byte[] FixNameHeaderCrc(byte[] header)
    {
        var fixedHeader = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader.AsSpan(20), Crc32.Compute(fixedHeader.AsSpan(..20)));
        return fixedHeader;
    }

    private static byte[] RebuildNameSnapshot(byte[] header, byte[] names, byte[] records)
    {
        uint crc = Crc32.Update(0xFFFF_FFFFu, names);
        crc = Crc32.Update(crc, records);
        var trailer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32.Final(crc));
        return header.Concat(names).Concat(records).Concat(trailer).ToArray();
    }

    private static byte[] CorruptNameSnapshot(Func<byte[], byte[]> mutate)
    {
        byte[] bytes = BuildNameSnapshot(recordCount: 3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        return RebuildNameSnapshot(mutate((byte[])header.Clone()), (byte[])names.Clone(), (byte[])records.Clone());
    }

    private static void AssertInvalidSnapshot(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => NameIndex.LoadSnapshot(stream));
    }

    private static NameIndex LoadOk(byte[] bytes)
    {
        var stream = new SlowStream(bytes, bytesPerRead: 1);   // 分片读覆盖 ReadExactlyOrThrow 循环
        return NameIndex.LoadSnapshot(stream);
    }

    [Fact]
    public void NameSnapshot_Valid_MultiRecord_PartialReads_Loads()
    {
        using var idx = LoadOk(BuildNameSnapshot(recordCount: 300));   // 300 条触发装载期 GrowHash
        Assert.Equal(300, idx.Count);
        Assert.True(idx.TryGet(299, out var rec));
        Assert.Equal("file0299.txt", rec.FileName);
    }

    [Fact]
    public void NameSnapshot_SaveIntoBufferedStream_RoundTrips()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "buffered.txt", 1, T1);
        using var raw = new MemoryStream();
        var buffered = new BufferedStream(raw, 1 << 20);
        idx.SaveSnapshot(buffered);   // BufferedStream 直传分支：不二次包裹；SaveSnapshot 只 Flush 不 Dispose
        raw.Position = 0;
        using var loaded = NameIndex.LoadSnapshot(raw);
        Assert.Equal(1, loaded.Count);
    }

    [Fact]
    public void NameSnapshot_BadMagic_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        bytes[0] = (byte)'X';
        AssertInvalidSnapshot(bytes);
    }

    [Fact]
    public void NameSnapshot_BadVersion_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        header[4] = 99;
        AssertInvalidSnapshot(RebuildNameSnapshot(FixNameHeaderCrc(header), names, records));
    }

    [Fact]
    public void NameSnapshot_BadHeaderCrc_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        bytes[8] ^= 0xFF;   // 改 active 不修头 CRC
        AssertInvalidSnapshot(bytes);
    }

    [Fact]
    public void NameSnapshot_NegativeActive_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), -1);
        AssertInvalidSnapshot(RebuildNameSnapshot(FixNameHeaderCrc(header), names, records));
    }

    [Fact]
    public void NameSnapshot_OversizeNameBytes_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), (long)int.MaxValue + 1);
        AssertInvalidSnapshot(RebuildNameSnapshot(FixNameHeaderCrc(header), names, records));
    }

    [Fact]
    public void NameSnapshot_TruncatedHeader_Throws()
    {
        using var stream = new MemoryStream(new byte[10]);
        Assert.Throws<InvalidDataException>(() => NameIndex.LoadSnapshot(stream));
    }

    [Fact]
    public void NameSnapshot_TruncatedNames_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        AssertInvalidSnapshot(bytes[..30]);   // 头之后即断
    }

    [Fact]
    public void NameSnapshot_TruncatedRecords_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, _, _) = ParseNameSnapshot(bytes);
        AssertInvalidSnapshot(header.Concat(names).Concat(new byte[] { 0x01, 0x02 }).ToArray());
    }

    [Fact]
    public void NameSnapshot_BadBodyCrc_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, trailer) = ParseNameSnapshot(bytes);
        names[0] ^= 0xFF;   // 改名字区且保留原 trailer → 体 CRC 校验必炸
        AssertInvalidSnapshot(header.Concat(names).Concat(records).Concat(trailer).ToArray());
    }

    [Fact]
    public void NameSnapshot_DeletedRecordFlag_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        records[38] = 1;   // 首记录 flags 置已删位（快照应为压缩后活跃集）
        AssertInvalidSnapshot(RebuildNameSnapshot(header, names, records));
    }

    [Fact]
    public void NameSnapshot_BadNameOffset_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(32), 9999);
        AssertInvalidSnapshot(RebuildNameSnapshot(header, names, records));
    }

    [Fact]
    public void NameSnapshot_NegativeNameLength_Throws()
    {
        byte[] bytes = BuildNameSnapshot(3);
        var (header, names, records, _) = ParseNameSnapshot(bytes);
        BinaryPrimitives.WriteInt16LittleEndian(records.AsSpan(36), -1);
        AssertInvalidSnapshot(RebuildNameSnapshot(header, names, records));
    }

    /// <summary>每次 Read 最多返回 bytesPerRead 字节：覆盖精确读循环与慢速流分支。</summary>
    private sealed class SlowStream(byte[] bytes, int bytesPerRead) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, bytesPerRead);
            n = Math.Min(n, bytes.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(bytes, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------- InvertedIndex：查询组合 ----------

    private static InvertedIndex BuildIndex()
    {
        var index = new InvertedIndex();
        index.AddDocument(1, "alpha beta alpha", T1);
        index.AddDocument(2, "beta gamma", T1);
        return index;
    }

    [Fact]
    public void Lookup_MissingTerm_ReturnsEmpty()
    {
        var index = BuildIndex();
        Assert.Empty(index.Lookup("missing"));
        Assert.Single(index.Lookup("alpha"));
    }

    [Fact]
    public void LookupAll_SingleMultiAndAbsent_Combinations()
    {
        var index = BuildIndex();
        Assert.Equal(2, index.LookupAll(["beta"]).Count);                       // 单词
        Assert.Single(index.LookupAll(["beta", "gamma"]));                      // 双词 AND
        var doubled = index.LookupAll(["alpha", "alpha"]).Single();             // 同词重复：词频求和
        Assert.Equal(1, doubled.DocId);
        Assert.Equal(4.0, doubled.Score, 1);
        Assert.Empty(index.LookupAll(["beta", "missing"]));                     // AND 上无命中词
        Assert.Empty(index.LookupAll([]));                                      // 空词表
    }

    [Fact]
    public void GetPositions_PresentAbsent_Combinations()
    {
        var index = BuildIndex();
        Assert.Equal(2, index.GetPositions(1, "alpha").Count);  // 命中：词频即占位长度
        Assert.Empty(index.GetPositions(1, "missing"));          // 词不存在
        Assert.Empty(index.GetPositions(99, "alpha"));           // 词存在文档不存在
    }

    // ---------- InvertedIndex 快照：损坏字节流全路径 ----------

    private static byte[] BuildFullTextSnapshot()
    {
        var index = BuildIndex();
        using var ms = new MemoryStream();
        index.SaveSnapshot(ms);
        return ms.ToArray();
    }

    private static (byte[] Header, byte[] Terms, byte[] TermRecs, byte[] Docs, byte[] Ids, byte[] Tfs) ParseFullTextSnapshot(byte[] b)
    {
        int termCount = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        int docCount = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12));
        long termBytes = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16));
        int offset = 28 + (int)termBytes;
        var termRecs = b[offset..(offset + termCount * 24)];
        offset += termCount * 24;
        var docs = b[offset..(offset + docCount * 16)];
        offset += docCount * 16;
        int idBytes = 0;
        for (int i = 0; i < termCount; i++)
            idBytes += BinaryPrimitives.ReadInt32LittleEndian(termRecs.AsSpan(i * 24 + 20));
        var ids = b[offset..(offset + idBytes)];
        offset += idBytes;
        var tfs = b[offset..^4];
        return (b[..28], b[28..(28 + (int)termBytes)], termRecs, docs, ids, tfs);
    }

    private static byte[] FixFullTextHeaderCrc(byte[] header)
    {
        var fixedHeader = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader.AsSpan(24), Crc32.Compute(fixedHeader.AsSpan(..24)));
        return fixedHeader;
    }

    private static byte[] RebuildFullTextSnapshot(byte[] header, byte[] terms, byte[] termRecs, byte[] docs, byte[] ids, byte[] tfs)
    {
        uint crc = Crc32.Update(0xFFFF_FFFFu, header.AsSpan(..24));
        crc = Crc32.Update(crc, terms);
        crc = Crc32.Update(crc, termRecs);
        crc = Crc32.Update(crc, docs);
        crc = Crc32.Update(crc, ids);
        crc = Crc32.Update(crc, tfs);
        var trailer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32.Final(crc));
        return header.Concat(terms).Concat(termRecs).Concat(docs).Concat(ids).Concat(tfs).Concat(trailer).ToArray();
    }

    [Fact]
    public void FullTextSnapshot_Valid_PartialReads_Loads()
    {
        var stream = new SlowStream(BuildFullTextSnapshot(), bytesPerRead: 1);
        var index = InvertedIndex.LoadSnapshot(stream);
        Assert.Equal(2, index.DocCount);
        Assert.Single(index.Lookup("gamma"));
    }

    [Fact]
    public void FullTextSnapshot_BadMagic_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        bytes[0] = (byte)'X';
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(stream));
    }

    [Fact]
    public void FullTextSnapshot_BadVersion_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, recs, docs, ids, tfs) = ParseFullTextSnapshot(bytes);
        header[4] = 99;
        AssertInvalidFullText(RebuildFullTextSnapshot(FixFullTextHeaderCrc(header), terms, recs, docs, ids, tfs));
    }

    [Fact]
    public void FullTextSnapshot_BadHeaderCrc_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        bytes[8] ^= 0xFF;
        AssertInvalidFullText(bytes);
    }

    [Fact]
    public void FullTextSnapshot_NegativeTermCount_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, recs, docs, ids, tfs) = ParseFullTextSnapshot(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), -1);
        AssertInvalidFullText(RebuildFullTextSnapshot(FixFullTextHeaderCrc(header), terms, recs, docs, ids, tfs));
    }

    [Fact]
    public void FullTextSnapshot_OversizeTermBytes_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, recs, docs, ids, tfs) = ParseFullTextSnapshot(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), (long)int.MaxValue + 1);
        AssertInvalidFullText(RebuildFullTextSnapshot(FixFullTextHeaderCrc(header), terms, recs, docs, ids, tfs));
    }

    [Fact]
    public void FullTextSnapshot_TruncatedHeader_Throws()
    {
        using var stream = new MemoryStream(new byte[10]);
        Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(stream));
    }

    [Fact]
    public void FullTextSnapshot_TruncatedTermRegion_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        AssertInvalidFullText(bytes[..34]);   // 头之后即断
    }

    [Fact]
    public void FullTextSnapshot_TruncatedTermRecords_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, _, docs, ids, tfs) = ParseFullTextSnapshot(bytes);
        AssertInvalidFullText(RebuildFullTextSnapshot(header, terms, [], docs, ids, tfs));
    }

    [Fact]
    public void FullTextSnapshot_TruncatedDocRegion_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, recs, _, ids, tfs) = ParseFullTextSnapshot(bytes);
        AssertInvalidFullText(RebuildFullTextSnapshot(header, terms, recs, [], ids, tfs));
    }

    [Fact]
    public void FullTextSnapshot_TruncatedIdList_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, recs, docs, _, tfs) = ParseFullTextSnapshot(bytes);
        AssertInvalidFullText(RebuildFullTextSnapshot(header, terms, recs, docs, [], tfs));
    }

    [Fact]
    public void FullTextSnapshot_BadBodyCrc_Throws()
    {
        byte[] bytes = BuildFullTextSnapshot();
        var (header, terms, recs, docs, ids, tfs) = ParseFullTextSnapshot(bytes);
        terms[0] ^= 0xFF;   // 改词条区且保留原 trailer → 体 CRC 校验必炸
        AssertInvalidFullText(header.Concat(terms).Concat(recs).Concat(docs).Concat(ids).Concat(tfs).Concat(bytes[^4..]).ToArray());
    }

    private static void AssertInvalidFullText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(stream));
    }

    // ---------- Tokenizer：词元边界 ----------

    [Fact]
    public void Tokenize_WordChars_CjkBigramAndSingle_Combinations()
    {
        var tokens = Tokenizer.Tokenize("go2_x-y.z#w9 中文ab-9尾").Select(t => t.Value).ToArray();
        Assert.Contains("go2_x-y.z#w9", tokens);   // 字母/数字/下划线/连字符/点/# 全词元字符
        Assert.Contains("中文", tokens);            // CJK 二元组
        Assert.Contains("ab-9", tokens);            // CJK 后接词元
        Assert.Contains("尾", tokens);              // 末字 CJK 且前邻非 CJK → 补单字
    }

    [Fact]
    public void Tokenize_SingleCjkTail_EmitsSingle()
    {
        var tokens = Tokenizer.Tokenize("x单").Select(t => t.Value).ToArray();
        Assert.Contains("x", tokens);
        Assert.Contains("单", tokens);   // 末字 CJK 且前邻非 CJK → 补单字
    }

    [Fact]
    public void Tokenize_ConsecutiveCjk_EmitsBigramsOnly()
    {
        var tokens = Tokenizer.Tokenize("中文").Select(t => t.Value).ToArray();
        Assert.Equal(new[] { "中文" }, tokens);
    }

    [Fact]
    public void Tokenize_EmptyAndPunctuationOnly_YieldsNothing()
    {
        Assert.Empty(Tokenizer.Tokenize(string.Empty));
        Assert.Empty(Tokenizer.Tokenize("!!! ??? ,,, ;;"));
    }
}
