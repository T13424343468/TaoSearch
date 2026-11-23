using System.Buffers.Binary;
using System.Text;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Kernel.Query;
using TaoSearch.Kernel.Store;
using Xunit;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 分支覆盖补测第三批（测试规范九.11）：零星 50% 分支收口——
/// FsIndexEngine 空卷前缀、NameIndex 墓碑重插/搜索边界矩阵补角、HotTermCache 同词覆盖、
/// 全文快照尾缺失/坏体 CRC/词条记录越界组合/未定义文档引用、QueryParser 前缀矩阵补角、
/// JournalSink 弃用后轮转、JournalWriter 负长度帧。
/// </summary>
public sealed class MiscBranchGapsTests
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------- FsIndexEngine：空卷前缀 ----------

    [Fact]
    public void MarkRestored_EmptyVolumeRoot_ReadyWithoutPrefix()
    {
        using var engine = new FsIndexEngine();
        engine.MarkRestored(string.Empty);   // NormalizePrefix 空串分支
        Assert.True(engine.Snapshot().Ready);
    }

    // ---------- NameIndex：墓碑重插与搜索边界 ----------

    [Fact]
    public void Reinsert_AfterRemove_TombstoneSlotProbedAndReused()
    {
        using var idx = new NameIndex();
        idx.Upsert(5, 0, "x.txt", 1, T1);
        Assert.True(idx.Remove(5));          // 槽位留墓碑（value=-2，键保留）
        idx.Upsert(5, 0, "y.txt", 2, T1);    // 同键重插：命中墓碑组合支路后继续探测
        Assert.True(idx.TryGet(5, out var rec));
        Assert.Equal("y.txt", rec.FileName);
        Assert.Equal(1, idx.Count);
    }

    [Fact]
    public void Search_TermLongerThanAnyName_NoMatch()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "ab", 1, T1);
        Assert.Empty(idx.Search("abcdef"));  // haystack 短于词条支路
    }

    [Fact]
    public void Search_FirstByteHitsButMismatch_NoMatch()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "axc.txt", 1, T1);
        Assert.Empty(idx.Search("ab"));      // MatchAt 首字节后失配支路
    }

    [Fact]
    public void Search_SymbolFirstByte_SingleCandidateMatch()
    {
        using var idx = new NameIndex();
        idx.Upsert(1, 0, "a{b}.txt", 1, T1);
        Assert.Single(idx.Search("{b}"));    // '{' 越过 z 上界的字母判定组合
        idx.Upsert(2, 0, "x[i].txt", 1, T1);
        Assert.Single(idx.Search("[i]"));    // '[' 落在 A-Z 区间后沿的字母判定组合
    }

    // ---------- HotTermCache：同词覆盖 ----------

    [Fact]
    public void Put_SameTermTwice_ReplacesValueAndBudget()
    {
        var cache = new HotTermCache(4096);
        cache.Put("a", [(1, 1)]);
        cache.Put("a", [(1, 1), (2, 1)]);    // 已存在分支：旧值出账、LRU 摘除后重挂
        Assert.Equal(1, cache.Count);
        Assert.Equal(8 + 1 * 2 + 2 * 16, cache.UsedBytes);
        Assert.True(cache.TryGet("a", out var v));
        Assert.Equal(2, v.Count);
    }

    [Fact]
    public void TryGet_EmptyNameRecord_ReturnsEmptyStringName()
    {
        using var idx = new NameIndex();
        idx.Upsert(7, 0, string.Empty, 5, T1);
        Assert.True(idx.TryGet(7, out var rec));
        Assert.Equal(string.Empty, rec.FileName);   // ToNameRecord 空名支路
    }

    // ---------- InvertedIndex 快照：尾缺失/坏体 CRC/词条记录越界/未定义文档 ----------

    private static byte[] BuildFullTextSnapshot()
    {
        var index = new InvertedIndex();
        index.AddDocument(1, "alpha beta alpha", T1);
        index.AddDocument(2, "beta gamma", T1);
        using var ms = new MemoryStream();
        index.SaveSnapshot(ms);
        return ms.ToArray();
    }

    private static byte[] FixTrailer(byte[] bytes)
    {
        // 头 CRC 与正文 CRC 均覆盖 header[..24] 起的全部正文；改字节后仅重算尾 CRC
        var patched = (byte[])bytes.Clone();
        uint crc = Crc32.Update(0xFFFF_FFFFu, patched.AsSpan(..24));
        crc = Crc32.Update(crc, patched.AsSpan(28..^4));
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(^4..), Crc32.Final(crc));
        return patched;
    }

    [Fact]
    public void FullTextSnapshot_MissingTrailer_Throws()
    {
        var bytes = BuildFullTextSnapshot();
        var ex = Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(new MemoryStream(bytes[..^4])));
        Assert.Contains("尾 CRC 缺失", ex.Message);
    }

    [Fact]
    public void FullTextSnapshot_BadBodyCrc_Throws()
    {
        var bytes = BuildFullTextSnapshot();
        bytes[28] ^= 0xFF;                   // 词条区首字节翻转，保留原 trailer
        var ex = Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(new MemoryStream(bytes)));
        Assert.Contains("正文 CRC 校验失败", ex.Message);
    }

    [Fact]
    public void FullTextSnapshot_TermRecordNegativeOffset_Throws()
    {
        var bytes = FixTrailer(PatchTermRecord(BuildFullTextSnapshot(), rec => BinaryPrimitives.WriteInt32LittleEndian(rec[..4], -1)));
        var ex = Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(new MemoryStream(bytes)));
        Assert.Contains("词条记录越界", ex.Message);
    }

    [Fact]
    public void FullTextSnapshot_TermRecordNegativeIdCount_Throws()
    {
        var bytes = FixTrailer(PatchTermRecord(BuildFullTextSnapshot(), rec => BinaryPrimitives.WriteInt32LittleEndian(rec[8..12], -1)));
        Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(new MemoryStream(bytes)));
    }

    [Fact]
    public void FullTextSnapshot_TermRecordIdListOverflow_Throws()
    {
        var bytes = FixTrailer(PatchTermRecordAt(BuildFullTextSnapshot(), termIndex: 2, patch: rec => BinaryPrimitives.WriteInt64LittleEndian(rec[12..20], 100L)));
        var ex = Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(new MemoryStream(bytes)));
        Assert.Contains("词条记录越界", ex.Message);
    }

    [Fact]
    public void FullTextSnapshot_TermReferencesUndefinedDoc_Throws()
    {
        var bytes = BuildFullTextSnapshot();
        int termBytes = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)) is long tb ? (int)tb : 0;
        int idsStart = 28 + termBytes + 3 * 24 + 2 * 16;   // 3 词条记录、2 文档
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(idsStart), 999);   // alpha 首个文档 ID 改为未定义
        bytes = FixTrailer(bytes);
        var ex = Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(new MemoryStream(bytes)));
        Assert.Contains("未定义文档", ex.Message);
    }

    private static byte[] PatchTermRecord(byte[] bytes, Action<Span<byte>> patch) => PatchTermRecordAt(bytes, 0, patch);

    private static byte[] PatchTermRecordAt(byte[] bytes, int termIndex, Action<Span<byte>> patch)
    {
        var patched = (byte[])bytes.Clone();
        int termBytes = (int)BinaryPrimitives.ReadInt64LittleEndian(patched.AsSpan(16));
        int recStart = 28 + termBytes + termIndex * 24;
        patch(patched.AsSpan(recStart..(recStart + 24)));
        return patched;
    }

    // ---------- QueryParser：前缀矩阵补角 ----------

    [Fact]
    public void Parse_PrefixMatrix_RegexCaseWholeWordAndUnknown()
    {
        Assert.True(QueryParser.Parse("regex:abc").IsRegex);
        Assert.True(QueryParser.Parse("case:abc").CaseSensitive);
        Assert.True(QueryParser.Parse("wholeword:abc").WholeWord);
        Assert.Equal("foo:bar", QueryParser.Parse("foo:bar").Term);   // 未知前缀按普通词
    }

    [Fact]
    public void Parse_SizeBareComparator_Throws()
    {
        Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse("size:>"));   // ParseSizeBytes 空串支路
    }

    [Fact]
    public void Parse_DateRangeFourParts_Combinations()
    {
        var q = QueryParser.Parse("dm:2026-11-01-x");   // 4 段、第二日期段不可解析
        Assert.NotNull(q.Modified!.Value.From);
        Assert.Equal(q.Modified!.Value.From!.Value + TimeSpan.FromDays(1), q.Modified!.Value.To);

        var q2 = QueryParser.Parse("dm:2026-xy-01-z");  // 4 段、首日期月不可解析
        Assert.Null(q2.Modified!.Value.From);
        Assert.Null(q2.Modified!.Value.To);
    }

    // ---------- JournalSink / JournalWriter ----------

    [Fact]
    public void JournalSink_RotateAfterDispose_ReturnsZero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-journal-{Guid.NewGuid():N}.log");
        var sink = new JournalSink(path);
        sink.Dispose();
        Assert.Equal(0, sink.Rotate());      // 弃用后轮转短路支路
    }

    [Fact]
    public void Replay_NegativeLengthFrame_DirtyStop()
    {
        using var ms = new MemoryStream();
        ms.Write("TSSJ"u8);
        var verBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(verBuf, 1);
        ms.Write(verBuf);
        ms.WriteByte(0x01);                  // OpUpsert
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, -1);
        ms.Write(lenBuf);
        ms.Position = 0;

        var result = JournalWriter.Replay(ms, _ => Assert.Fail("负长度帧不得应用"));
        Assert.False(result.CleanEnd);
        Assert.Equal(0, result.FramesApplied);
    }

    [Fact]
    public void Replay_OverLimitLengthFrame_DirtyStop()
    {
        using var ms = new MemoryStream();
        ms.Write("TSSJ"u8);
        var verBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(verBuf, 1);
        ms.Write(verBuf);
        ms.WriteByte(0x01);                  // OpUpsert
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, (1 << 20) + 1);   // 超过 1MB 上限
        ms.Write(lenBuf);
        ms.Position = 0;

        var result = JournalWriter.Replay(ms, _ => Assert.Fail("超限帧不得应用"));
        Assert.False(result.CleanEnd);
        Assert.Equal(0, result.FramesApplied);
    }
}
