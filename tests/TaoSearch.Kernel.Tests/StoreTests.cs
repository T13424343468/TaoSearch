using System.Buffers.Binary;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.Store;
using Xunit;
using Xunit.Abstractions;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// M3 存储层：快照序列化/恢复、journal 重放、撕裂尾容忍（T4）、断点-恢复闭环、百万文件 T3 计时。
/// </summary>
public sealed class StoreTests
{
    private readonly ITestOutputHelper _output;

    public StoreTests(ITestOutputHelper output) => _output = output;

    private static FileEvent Ev(FileChangeKind kind, long fileRef, long parentRef, string name) => new()
    {
        Kind = kind,
        FileReference = fileRef,
        Path = name,
        UtcTimestamp = DateTimeOffset.UnixEpoch,
        ParentReference = parentRef,
    };

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- 快照序列化 ----

    [Fact]
    public void Snapshot_RoundTrip_PreservesCountSearchAndPaths()
    {
        using var index = new NameIndex();
        index.Upsert(5, 0, "C:", 0, DateTimeOffset.UnixEpoch);
        index.Upsert(100, 5, "docs", 0, DateTimeOffset.UnixEpoch);
        index.Upsert(200, 100, "报告.docx", 1234, DateTimeOffset.UnixEpoch);
        index.Upsert(201, 100, "notes.txt", 10, DateTimeOffset.UnixEpoch);

        using var ms = new MemoryStream();
        index.SaveSnapshot(ms);

        ms.Position = 0;
        using var loaded = NameIndex.LoadSnapshot(ms);

        Assert.Equal(4, loaded.Count);
        Assert.Equal(index.BuildPath(200), loaded.BuildPath(200));
        Assert.Equal("C:\\docs\\报告.docx", loaded.BuildPath(200));
        Assert.Contains(200, loaded.Search("报告"));
        Assert.Contains(201, loaded.Search("NOTES"));
        Assert.DoesNotContain(200, loaded.Search("notes"));
        Assert.True(loaded.TryGet(200, out var rec));
        Assert.Equal(1234, rec.Size);
    }

    [Fact]
    public void Snapshot_CompactsDeletedRecordsAndOrphanNames()
    {
        using var index = new NameIndex();
        for (int i = 0; i < 1000; i++)
            index.Upsert(i + 10, 0, $"file_{i:D5}.dat", i, DateTimeOffset.UnixEpoch);
        for (int i = 0; i < 500; i++)
            index.Remove(i + 10);

        using var ms = new MemoryStream();
        index.SaveSnapshot(ms);
        ms.Position = 0;
        using var loaded = NameIndex.LoadSnapshot(ms);

        Assert.Equal(500, loaded.Count);
        Assert.DoesNotContain(11, loaded.Search("file_00001"));
        Assert.Contains(510, loaded.Search("file_00500"));
        Assert.True(loaded.AllocatedBytes < index.AllocatedBytes, "压缩后内存应小于含删除记录的原索引");
    }

    [Fact]
    public void Snapshot_RejectsCorruptHeader()
    {
        using var index = new NameIndex();
        index.Upsert(1, 0, "a.txt", 0, DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        index.SaveSnapshot(ms);
        byte[] bytes = ms.ToArray();
        bytes[0] = (byte)'X';   // 破坏魔数

        Assert.Throws<InvalidDataException>(() =>
        {
            using var bad = new MemoryStream(bytes);
            NameIndex.LoadSnapshot(bad);
        });
    }

    [Fact]
    public void Snapshot_RejectsCorruptBody()
    {
        using var index = new NameIndex();
        for (int i = 0; i < 100; i++)
            index.Upsert(i + 10, 0, $"name_{i}.bin", 0, DateTimeOffset.UnixEpoch);
        using var ms = new MemoryStream();
        index.SaveSnapshot(ms);
        byte[] bytes = ms.ToArray();
        bytes[^3] ^= 0xFF;   // 破坏正文/尾部校验

        Assert.Throws<InvalidDataException>(() =>
        {
            using var bad = new MemoryStream(bytes);
            NameIndex.LoadSnapshot(bad);
        });
    }

    [Fact]
    public void Snapshot_EmptyIndex_RoundTrips()
    {
        using var original = new NameIndex();
        using var ms = new MemoryStream();
        original.SaveSnapshot(ms);
        ms.Position = 0;
        using var restored = NameIndex.LoadSnapshot(ms);
        Assert.Equal(0, restored.Count);
    }

    // ---- 断点-恢复闭环（T3/T4 语义） ----

    [Fact]
    public void Checkpoint_Restore_FullCycle_MatchesLiveState()
    {
        string dir = NewTempDir();
        try
        {
            string snap = Path.Combine(dir, "index.snap");
            string jrnl = Path.Combine(dir, "index.journal");
            using var sink = new JournalSink(jrnl);
            using var engine = new FsIndexEngine(journalSink: sink);

            engine.ApplyEvent(Ev(FileChangeKind.Created, 5, 0, "C:"));
            engine.ApplyEvent(Ev(FileChangeKind.Created, 100, 5, "docs"));
            engine.ApplyEvent(Ev(FileChangeKind.Created, 200, 100, "报告.docx"));
            engine.ApplyEvent(Ev(FileChangeKind.Created, 201, 100, "notes.txt"));

            var checkpoint = IndexPersistence.SaveCheckpoint(engine, snap, sink);
            Assert.Equal(4, checkpoint.FileCount);
            Assert.True(checkpoint.SnapshotBytes > 0);

            // 断点后增量：新建 + 删除各一
            engine.ApplyEvent(Ev(FileChangeKind.Created, 300, 100, "late.txt"));
            engine.ApplyEvent(Ev(FileChangeKind.Deleted, 201, 100, "notes.txt"));

            var restore = IndexPersistence.Restore(snap, jrnl, "C:");
            Assert.True(restore.SnapshotUsed);
            Assert.Equal(2, restore.JournalFramesApplied);
            Assert.Equal(0, restore.JournalTruncatedTailBytes);
            Assert.Equal(4, restore.FileCount);

            // 活引擎终态对账：快照导出后引用号集合与恢复结果一致
            using var ms = new MemoryStream();
            engine.SaveSnapshot(ms);
            ms.Position = 0;
            using var liveSnap = NameIndex.LoadSnapshot(ms);
            Assert.Equal(engine.Snapshot().FileCount, liveSnap.Count);
            Assert.Contains(300L, liveSnap.Search("late"));
            Assert.DoesNotContain(201, liveSnap.Search("notes"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Restore_WithoutSnapshot_ReplaysJournalOnly()
    {
        string dir = NewTempDir();
        try
        {
            string jrnl = Path.Combine(dir, "index.journal");
            using (var sink = new JournalSink(jrnl))
            using (var engine = new FsIndexEngine(journalSink: sink))
            {
                engine.ApplyEvent(Ev(FileChangeKind.Created, 1, 0, "alpha.txt"));
                engine.ApplyEvent(Ev(FileChangeKind.Created, 2, 0, "beta.txt"));
                engine.ApplyEvent(Ev(FileChangeKind.Deleted, 2, 0, "beta.txt"));
            }

            string snap = Path.Combine(dir, "missing.snap");
            var restore = IndexPersistence.Restore(snap, jrnl, "C:");
            Assert.False(restore.SnapshotUsed);
            Assert.Equal(3, restore.JournalFramesApplied);
            Assert.Equal(1, restore.FileCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- T4：强杀/撕裂尾容忍 ----

    [Fact]
    public void Journal_TornTail_ReplaysValidPrefixOnly()
    {
        using var ms = new MemoryStream();
        using (var writer = new JournalWriter(ms))
        {
            writer.AppendUpsert(1, 0, "a.txt", 0, DateTimeOffset.UnixEpoch);
            writer.AppendUpsert(2, 0, "b.txt", 0, DateTimeOffset.UnixEpoch);
        }
        byte[] bytes = ms.ToArray();

        // 模拟强杀：最后一帧被截断（CRC 与部分 payload 丢失；截 7B 使尾帧 payload 缺 2 字节）
        using var torn = new MemoryStream(bytes, 0, bytes.Length - 7, writable: false);
        int applied = 0;
        var result = JournalWriter.Replay(torn, _ => applied++);

        Assert.Equal(1, applied);
        Assert.False(result.CleanEnd);
        Assert.True(result.TruncatedTailBytes > 0);
    }

    [Fact]
    public void Journal_CorruptCrc_StopsBeforeBadFrame()
    {
        using var ms = new MemoryStream();
        using (var writer = new JournalWriter(ms))
        {
            writer.AppendUpsert(1, 0, "a.txt", 0, DateTimeOffset.UnixEpoch);
            writer.AppendUpsert(2, 0, "b.txt", 0, DateTimeOffset.UnixEpoch);
            writer.AppendRemove(1);
        }
        byte[] bytes = ms.ToArray();

        // 破坏第二帧 payload 首字节：帧头 5B，帧1 = 5 + (36+5) + 4 = 50B，帧2 payload 起点 = 8 + 50 + 5 = 63
        bytes[8 + 50 + 5] ^= 0xFF;

        using var bad = new MemoryStream(bytes, writable: false);
        var applied = new List<JournalEvent>();
        var result = JournalWriter.Replay(bad, applied.Add);

        Assert.Single(applied);
        Assert.False(result.CleanEnd);
        Assert.True(result.TruncatedTailBytes > 0);
    }

    [Fact]
    public void Journal_EmptyAndHeaderOnly_ReplayClean()
    {
        using var empty = new MemoryStream();
        var r1 = JournalWriter.Replay(empty, _ => Assert.Fail("空文件不应有帧"));
        Assert.True(r1.CleanEnd);
        Assert.Equal(0, r1.FramesApplied);

        using var headerOnly = new MemoryStream();
        headerOnly.Write("TSSJ"u8);
        Span<byte> ver = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(ver, 1);
        headerOnly.Write(ver);
        headerOnly.Position = 0;
        var r2 = JournalWriter.Replay(headerOnly, _ => Assert.Fail("仅文件头不应有帧"));
        Assert.True(r2.CleanEnd);
        Assert.Equal(0, r2.FramesApplied);
    }

    // ---- T3：百万文件快照恢复计时 ----

    [Fact]
    public void Snapshot_MillionFiles_RestoreTimingAndCorrectness()
    {
        const int count = 1_000_000;
        string dir = NewTempDir();
        try
        {
            string snapPath = Path.Combine(dir, "million.snap");
            var index = new NameIndex();
            for (int i = 0; i < count; i++)
                index.Upsert(i + 10, 0, $"file_{i:D7}.dat", i, DateTimeOffset.UnixEpoch);

            var saveSw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(snapPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                index.SaveSnapshot(fs);
            }
            saveSw.Stop();

            var restoreSw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(snapPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var loaded = NameIndex.LoadSnapshot(fs))
            {
                restoreSw.Stop();

                _output.WriteLine($"[T3] 百万文件快照：保存 {saveSw.ElapsedMilliseconds}ms，恢复 {restoreSw.ElapsedMilliseconds}ms，" +
                                  $"体积 {new FileInfo(snapPath).Length / 1024}KB，恢复后内存 {loaded.AllocatedBytes / 1024 / 1024}MB");
                Assert.Equal(count, loaded.Count);
                Assert.Contains(500_010, loaded.Search("file_0500000"));
            }
            Assert.True(restoreSw.ElapsedMilliseconds < 30_000, $"恢复耗时 {restoreSw.ElapsedMilliseconds}ms 超出合理上限");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
