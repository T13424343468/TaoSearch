using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.Store;
using Xunit;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 分支覆盖补测（测试规范九.11）：FsIndexEngine 快慢路径、EnumerateLivePaths、
/// TryGetInfo、默认慢路径对不存在目录的降级、Dispose；NameIndex 边界分支。
/// </summary>
public sealed class FsIndexBranchTests
{
    private static FileEvent Ev(FileChangeKind kind, long fileRef, long parentRef, string name) => new()
    {
        Kind = kind,
        FileReference = fileRef,
        Path = name,
        UtcTimestamp = DateTimeOffset.UnixEpoch,
        ParentReference = parentRef,
    };

    [Fact]
    public async Task Build_MftSuccess_PrefixesPathsAndEnumerates()
    {
        var engine = new FsIndexEngine((root, sink, ct) =>
        {
            sink(1, 0, "C:");
            sink(2, 1, "docs");
            sink(3, 2, "报告.docx");
            return MftScanOutcome.Ok(3, 1);
        });
        var result = await engine.BuildAsync("C:");
        Assert.False(result.UsedFallback);
        Assert.Equal(3, result.FileCount);
        Assert.True(engine.Snapshot().Ready);

        var hits = engine.Search("报告").ToList();
        Assert.Single(hits);
        Assert.StartsWith("C:\\", hits[0].Item2); // MFT 路径补卷前缀

        var live = engine.EnumerateLivePaths().ToList();
        Assert.Equal(3, live.Count);
        Assert.All(live, e => Assert.StartsWith("C:\\", e.Item2));

        Assert.True(engine.TryGetInfo(3, out var path, out _, out _));
        Assert.StartsWith("C:\\", path);
        engine.Dispose();
    }

    [Fact]
    public async Task Build_MftFails_FallbackEntries_KeptAsFullPath()
    {
        var engine = new FsIndexEngine(
            (_, _, _) => MftScanOutcome.Fail(5),
            (root, ct) => FallbackSeq(new[]
            {
                new FallbackEntry("/data/a.txt", 12, DateTimeOffset.UnixEpoch),
                new FallbackEntry("/data/sub", 0, DateTimeOffset.UnixEpoch),
            }));
        var result = await engine.BuildAsync("C:");
        Assert.True(result.UsedFallback);
        Assert.Equal(2, result.FileCount);
        Assert.Contains("MFT 枚举不可用", engine.Snapshot().LastError);

        // 慢路径：记录名即全路径，不再补前缀
        var hits = engine.Search("a.txt").ToList();
        Assert.Single(hits);
        Assert.Equal("/data/a.txt", hits[0].Item2);

        var live = engine.EnumerateLivePaths().ToList();
        Assert.Contains("/data/a.txt", live.Select(e => e.Item2));

        Assert.True(engine.TryGetInfo(live[0].FileReference, out var path, out var size, out _));
        Assert.Equal("/data/a.txt", path);
        Assert.Equal(12, size);
        engine.Dispose();
    }

    [Fact]
    public async Task Build_MftFails_NoFallbackRoot_Missing_YieldsEmpty()
    {
        var engine = new FsIndexEngine((_, _, _) => MftScanOutcome.Fail(5));
        var result = await engine.BuildAsync("/nonexistent-root-$$");
        Assert.True(result.UsedFallback);
        Assert.Equal(0, result.FileCount);
        Assert.False(engine.Snapshot().Building);
        engine.Dispose();
    }

    [Fact]
    public async Task Build_FallbackOverride_Empty_YieldsEmpty()
    {
        var engine = new FsIndexEngine(
            (_, _, _) => MftScanOutcome.Fail(5),
            (root, ct) => FallbackSeq(Array.Empty<FallbackEntry>()));
        var result = await engine.BuildAsync("C:");
        Assert.True(result.UsedFallback);
        Assert.Equal(0, result.FileCount);
        engine.Dispose();
    }

    [Fact]
    public void ApplyEvent_Delete_RemovesAndWritesJournal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string jrn = Path.Combine(dir, "j.log");
        using var engine = new FsIndexEngine(journalSink: new JournalSink(jrn));

        engine.ApplyEvent(Ev(FileChangeKind.Created, 1, 0, "a.txt"));
        engine.ApplyEvent(Ev(FileChangeKind.Deleted, 1, 0, "a.txt"));
        Assert.Equal(0, engine.Snapshot().FileCount);
        Assert.True(File.Exists(jrn));
    }

    [Fact]
    public void Clear_ResetsReady()
    {
        using var engine = new FsIndexEngine();
        engine.ApplyEvent(Ev(FileChangeKind.Created, 1, 0, "a.txt"));
        engine.Clear();
        var s = engine.Snapshot();
        Assert.Equal(0, s.FileCount);
        Assert.False(s.Ready);
    }

    [Fact]
    public void Dispose_SecondCall_ThrowsObjectDisposed()
    {
        var engine = new FsIndexEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(engine.Dispose);
    }

    [Fact]
    public void TryGetInfo_MissingReference_ReturnsFalse()
    {
        using var engine = new FsIndexEngine();
        Assert.False(engine.TryGetInfo(999, out _, out _, out _));
    }

    // ---- NameIndex 边界分支 ----

    [Fact]
    public void NameIndex_RemoveUnknownReference_NoThrow()
    {
        using var index = new NameIndex();
        index.Upsert(1, 0, "C:", 0, DateTimeOffset.UnixEpoch);
        index.Remove(42); // 不存在的引用号：静默
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void NameIndex_BuildPath_RootAndDeepChain()
    {
        using var index = new NameIndex();
        index.Upsert(1, 0, "C:", 0, DateTimeOffset.UnixEpoch);
        Assert.Equal("C:", index.BuildPath(1));
        index.Upsert(2, 1, "a", 0, DateTimeOffset.UnixEpoch);
        index.Upsert(3, 2, "b.txt", 0, DateTimeOffset.UnixEpoch);
        Assert.Equal("C:\\a\\b.txt", index.BuildPath(3));
    }

    [Fact]
    public void NameIndex_TryGet_Missing_ReturnsFalse()
    {
        using var index = new NameIndex();
        Assert.False(index.TryGet(7, out _));
    }

    [Fact]
    public void NameIndex_Clear_EmptiesEverything()
    {
        using var index = new NameIndex();
        index.Upsert(1, 0, "C:", 0, DateTimeOffset.UnixEpoch);
        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Empty(index.Search("C:"));
    }

    private static async IAsyncEnumerable<FallbackEntry> FallbackSeq(FallbackEntry[] entries)
    {
        await Task.Yield();
        foreach (var e in entries)
            yield return e;
    }
}
