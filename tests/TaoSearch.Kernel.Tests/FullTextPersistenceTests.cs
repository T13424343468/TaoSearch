using Xunit;
using System.Buffers.Binary;
using TaoSearch.Kernel.FullText;

namespace TaoSearch.Kernel.Tests;

public class FullTextPersistenceTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-ft-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Snapshot_RoundTrip_PreservesIndex()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "hello world 你好世界", DateTimeOffset.UnixEpoch);
        idx.AddDocument(2, "hello tao", DateTimeOffset.UnixEpoch);
        idx.RemoveDocument(2);

        using var ms = new MemoryStream();
        idx.SaveSnapshot(ms);
        ms.Position = 0;
        var loaded = InvertedIndex.LoadSnapshot(ms);

        Assert.Equal(1, loaded.DocCount);
        var hits = loaded.Lookup("hello");
        Assert.Single(hits);
        Assert.Equal(1, hits[0].DocId);
        Assert.Equal(1, hits[0].TermFrequency);
        Assert.Empty(loaded.Lookup("tao"));
        Assert.NotEmpty(loaded.Lookup("你好"));
    }

    [Fact]
    public void Snapshot_CorruptHeader_Rejected()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "abc", DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        idx.SaveSnapshot(ms);
        byte[] bytes = ms.ToArray();
        bytes[2] ^= 0xFF;   // 破坏魔数

        using var bad = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(bad));
    }

    [Fact]
    public void Snapshot_CorruptBody_Rejected()
    {
        var idx = new InvertedIndex();
        idx.AddDocument(1, "abc def", DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        idx.SaveSnapshot(ms);
        byte[] bytes = ms.ToArray();
        bytes[^5] ^= 0xFF;   // 破坏正文（尾 CRC 前）

        using var bad = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => InvertedIndex.LoadSnapshot(bad));
    }

    [Fact]
    public void Journal_RoundTrip_ReplaysUpsertAndRemove()
    {
        using var ms = new MemoryStream();
        FullTextJournal.Append(ms, new FullTextEvent(1, "hello", DateTimeOffset.UnixEpoch));
        FullTextJournal.Append(ms, new FullTextEvent(2, "world", DateTimeOffset.UnixEpoch));
        FullTextJournal.Append(ms, new FullTextEvent(2, null, default));
        ms.Position = 0;

        var idx = new InvertedIndex();
        var r = FullTextJournal.Replay(ms, e =>
        {
            if (e.Text is null) idx.RemoveDocument(e.DocId);
            else idx.AddDocument(e.DocId, e.Text, e.ModifiedUtc);
        });

        Assert.True(r.CleanEnd);
        Assert.Equal(3, r.FramesApplied);
        Assert.Equal(0, r.TruncatedTailBytes);
        Assert.Equal(1, idx.DocCount);
        Assert.NotEmpty(idx.Lookup("hello"));
        Assert.Empty(idx.Lookup("world"));
    }

    [Fact]
    public void Journal_TornTail_StopsAtLastGoodFrame()
    {
        using var ms = new MemoryStream();
        FullTextJournal.Append(ms, new FullTextEvent(1, "aaa", DateTimeOffset.UnixEpoch));
        FullTextJournal.Append(ms, new FullTextEvent(2, "bbb", DateTimeOffset.UnixEpoch));
        byte[] bytes = ms.ToArray();

        // 撕裂：截掉最后一帧的部分字节（帧头 5B + payload 24+3 + crc 4 = 36B，截 10B 使尾帧不完整）
        using var torn = new MemoryStream(bytes, 0, bytes.Length - 10, writable: false);
        var idx = new InvertedIndex();
        int applied = 0;
        var r = FullTextJournal.Replay(torn, e => { applied++; });

        Assert.Equal(1, r.FramesApplied);
        Assert.Equal(1, applied);
        Assert.False(r.CleanEnd);
        // 残留按坏帧起点计：第二帧起点 = 8 + (5+27+4) = 44；截后总长 70，残留 = 70-44 = 26
        Assert.Equal(26, r.TruncatedTailBytes);
    }

    [Fact]
    public void CheckpointRestore_FullCycle_MatchesLiveState()
    {
        string dir = TempDir();
        try
        {
            string snap = Path.Combine(dir, "fulltext.snap");
            using var journal = new FileStream(Path.Combine(dir, "fulltext.journal"), FileMode.Create, FileAccess.ReadWrite);

            var live = new InvertedIndex();
            live.AddDocument(1, "alpha beta", DateTimeOffset.UnixEpoch);
            live.AddDocument(2, "beta gamma", DateTimeOffset.UnixEpoch);
            FullTextPersistence.SaveCheckpoint(live, snap, journal);

            // 断点后增量：新增 + 删除
            FullTextJournal.Append(journal, new FullTextEvent(3, "gamma delta", DateTimeOffset.UnixEpoch));
            FullTextJournal.Append(journal, new FullTextEvent(1, null, default));
            journal.Position = 0;

            var (restored, frames, torn) = FullTextPersistence.Restore(snap, journal);
            Assert.Equal(2, frames);
            Assert.Equal(0, torn);
            Assert.Equal(2, restored.DocCount);
            Assert.Empty(restored.Lookup("alpha"));
            Assert.NotEmpty(restored.Lookup("beta"));
            Assert.NotEmpty(restored.Lookup("gamma"));
            Assert.NotEmpty(restored.Lookup("delta"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void HotTermCache_EvictsBeyondBudget()
    {
        // 预算压小到 1KB 验证淘汰逻辑（预算常量本身断言 6MB）
        var cache = new HotTermCache(1024);
        Assert.Equal(FullTextPersistence.CacheBudgetBytes, 6L * 1024 * 1024);

        for (int i = 0; i < 100; i++)
            cache.Put($"term_{i:D4}", new List<(long, int)> { (i, 1) });

        Assert.True(cache.UsedBytes <= 1024, $"缓存占用 {cache.UsedBytes} 超预算 1024");
        // 最早写入的应被淘汰
        Assert.False(cache.TryGet("term_0000", out _));
        // 最近写入的应保留
        Assert.True(cache.TryGet("term_0099", out _));
    }

    [Fact]
    public void HotTermCache_OversizeEntry_NotCached()
    {
        var cache = new HotTermCache(64);
        var huge = new List<(long, int)>(Enumerable.Range(0, 100).Select(i => ((long)i, i)));
        cache.Put("huge", huge);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.UsedBytes);
    }
}
