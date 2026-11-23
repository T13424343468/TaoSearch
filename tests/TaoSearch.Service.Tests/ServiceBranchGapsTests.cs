using System.Runtime.CompilerServices;
using System.Text;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Service;
using Xunit;

namespace TaoSearch.Service.Tests;

/// <summary>
/// 服务层分支覆盖补测（测试规范九.11）：SearchEngine 正则非法/扫描上限/空词浏览/过滤器矩阵/
/// 预览窗口边界、ContentIndexer 无日志/坏日志/失败清单环形淘汰。
/// </summary>
public sealed class ServiceBranchGapsTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FsIndexEngine _fs;
    private readonly InvertedIndex _fullText = new();
    private readonly SearchEngine _engine;

    public ServiceBranchGapsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "taosearch-sbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        string sub = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(sub);

        // 落真实临时文件：预览走文档重扫；引用号按 fallback 顺序合成 -1/-2/-3...
        string alpha = WriteFile("alpha report.txt", new string('x', 76) + " alpha body");
        string beta = WriteFile("beta notes.txt", new string('y', 2000));
        string song = WriteFile("song.mp3", new string('z', 5000));
        string inner = WriteFile(Path.Combine("sub", "inner.txt"), "inner token here");

        var entries = new[]
        {
            new FallbackEntry(alpha, 100, T1),
            new FallbackEntry(beta, 2000, T1),
            new FallbackEntry(song, 5000, T2),
            new FallbackEntry(inner, 50, T1),
        };
        _fs = new FsIndexEngine(
            mftScanOverride: (_, _, _) => MftScanOutcome.Fail(1),
            fallbackOverride: (root, ct) => Feed(entries, ct));
        _engine = new SearchEngine(_fs, _fullText);
        _fs.BuildAsync("C:\\").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _fs.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async IAsyncEnumerable<FallbackEntry> Feed(FallbackEntry[] entries, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in entries)
        {
            await Task.Yield();
            yield return e;
        }
        await Task.CompletedTask;
    }

    private async Task<List<ResultBatch>> CollectAsync(QuerySpec spec)
    {
        var batches = new List<ResultBatch>();
        await foreach (var b in _engine.SearchAsync(spec))
            batches.Add(b);
        return batches;
    }

    private static IReadOnlyList<SearchHit> Hits(List<ResultBatch> batches) => batches.SelectMany(b => b.Hits).ToList();

    // ---------- SearchEngine：语法与扫描路 ----------

    [Fact]
    public async Task Search_InvalidRegex_EmptyFinalBatch()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "regex:[", Scope = QueryScope.Filename });
        var batch = Assert.Single(batches);
        Assert.True(batch.IsFinal);
        Assert.Empty(batch.Hits);
    }

    [Fact]
    public async Task Search_BatchSizeZero_ClampedToOneBatch()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "txt", Scope = QueryScope.Filename, BatchSize = 0 });
        Assert.Equal(3, batches.Count);      // Math.Max(1, 0)=1：每批 1 条
        Assert.All(batches, b => Assert.Single(b.Hits));
        Assert.True(batches[^1].IsFinal);
    }

    [Fact]
    public async Task Search_EmptyTermBrowse_AppliesExtFilter()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "ext:mp3", Scope = QueryScope.Filename });
        var hits = Hits(batches);
        var hit = Assert.Single(hits);
        Assert.EndsWith("song.mp3", hit.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_WholeWord_MatchesSegmentOnly()
    {
        var hit = Assert.Single(Hits(await CollectAsync(new QuerySpec { Text = "wholeword:report", Scope = QueryScope.Filename })));
        Assert.EndsWith("alpha report.txt", hit.Path, StringComparison.Ordinal);
        Assert.Empty(Hits(await CollectAsync(new QuerySpec { Text = "wholeword:rep", Scope = QueryScope.Filename })));
    }

    [Fact]
    public async Task Search_SizeFilter_BothBounds()
    {
        var hits = Hits(await CollectAsync(new QuerySpec { Text = "size:1000-6000", Scope = QueryScope.Filename }));
        Assert.Equal(2, hits.Count);   // beta 2000、song 5000；alpha 100 被下限挡掉
        Assert.DoesNotContain(hits, h => h.Path.EndsWith("alpha report.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_ModifiedFilter_DateRange()
    {
        var hits = Hits(await CollectAsync(new QuerySpec { Text = "dm:2026-11-01", Scope = QueryScope.Filename }));
        Assert.Equal(3, hits.Count);   // T1 三条；song 是 T2 被挡
        Assert.DoesNotContain(hits, h => h.Path.EndsWith("song.mp3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_PathPrefixFilter_SubdirectoryOnly()
    {
        string subPrefix = Path.Combine(_tempDir, "sub");
        var hits = Hits(await CollectAsync(new QuerySpec { Text = "path:" + subPrefix, Scope = QueryScope.Filename }));
        var hit = Assert.Single(hits);
        Assert.EndsWith("inner.txt", hit.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ScanCap_TruncatesRegexScanWithoutHang()
    {
        using var fs = new FsIndexEngine(
            mftScanOverride: (_, _, _) => MftScanOutcome.Fail(1),
            fallbackOverride: (root, ct) => FeedMany(ScanCapEntries, ct));
        var engine = new SearchEngine(fs, new InvertedIndex());
        await fs.BuildAsync("C:\\");

        var batches = new List<ResultBatch>();
        await foreach (var b in engine.SearchAsync(new QuerySpec { Text = "regex:QQQ", Scope = QueryScope.Filename }))
            batches.Add(b);
        var batch = Assert.Single(batches);
        Assert.True(batch.IsFinal);
        Assert.Empty(batch.Hits);
    }

    private static readonly FallbackEntry[] ScanCapEntries =
        [.. Enumerable.Range(0, SearchEngine.ScanCap + 1)
            .Select(i => new FallbackEntry($@"C:\cap\file{i:D6}.txt", 1, T1))];

    private static async IAsyncEnumerable<FallbackEntry> FeedMany(FallbackEntry[] entries, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in entries)
        {
            await Task.Yield();
            yield return e;
        }
        await Task.CompletedTask;
    }

    // ---------- SearchEngine：全文路与预览 ----------

    [Fact]
    public async Task ContentHit_MissingFileRecord_SkipsSilently()
    {
        _fullText.AddDocument(999, "ghost word", T1);   // 倒排有、文件名索引无
        var batches = await CollectAsync(new QuerySpec { Text = "word", Scope = QueryScope.Content });
        Assert.True(Assert.Single(batches).IsFinal);
        Assert.Empty(Hits(batches));
    }

    [Fact]
    public async Task ContentHit_BothPaths_MergesWithPreviewAndMaxScore()
    {
        // -1 = alpha report.txt：文件名与全文双命中
        _fullText.AddDocument(-1, "alpha body", T1);
        var hits = Hits(await CollectAsync(new QuerySpec { Text = "alpha", Scope = QueryScope.Filename | QueryScope.Content }));
        var hit = Assert.Single(hits);
        Assert.True(hit.Score > 0);
        Assert.NotNull(hit.Preview);
        Assert.Contains("alpha", hit.Preview!.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_EmptyFile_PreviewNullButHitReturns()
    {
        string empty = WriteFile("empty.txt", string.Empty);
        _fs.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = -100, Path = empty, UtcTimestamp = T1 });
        _fullText.AddDocument(-100, "vacuum", T1);
        var hits = Hits(await CollectAsync(new QuerySpec { Text = "vacuum", Scope = QueryScope.Content }));
        var hit = Assert.Single(hits);
        Assert.Null(hit.Preview);   // 0 字节文件：预览置空，命中照常
    }

    [Fact]
    public async Task Preview_ContentMismatch_PreviewNull()
    {
        string plain = WriteFile("plain.txt", "hello world");
        _fs.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = -101, Path = plain, UtcTimestamp = T1 });
        _fullText.AddDocument(-101, "zebra crossing", T1);   // 索引词与文件现文不一致
        var hits = Hits(await CollectAsync(new QuerySpec { Text = "zebra", Scope = QueryScope.Content }));
        var hit = Assert.Single(hits);
        Assert.Null(hit.Preview);
    }

    [Fact]
    public async Task Preview_MultiTerm_WindowAndRanges()
    {
        string multi = WriteFile("multi.txt", string.Concat(Enumerable.Repeat("hit ", 30)) + "alpha tail beta end");
        _fs.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = -102, Path = multi, UtcTimestamp = T1 });
        _fullText.AddDocument(-102, File.ReadAllText(multi), T1);

        var hits = Hits(await CollectAsync(new QuerySpec { Text = "alpha beta", Scope = QueryScope.Content }));
        var hit = Assert.Single(hits);
        Assert.NotNull(hit.Preview);
        Assert.True(hit.Preview!.MatchRanges.Count > 0);
        Assert.True(hit.Preview.MatchRanges.Count <= SearchEngine.SnippetMaxRanges);
    }

    [Fact]
    public async Task Preview_RepeatedTerm_RangesCappedAtMax()
    {
        string dense = WriteFile("dense.txt", string.Concat(Enumerable.Repeat("hit ", 40)));
        _fs.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = -103, Path = dense, UtcTimestamp = T1 });
        _fullText.AddDocument(-103, File.ReadAllText(dense), T1);

        var hits = Hits(await CollectAsync(new QuerySpec { Text = "hit", Scope = QueryScope.Content }));
        var hit = Assert.Single(hits);
        Assert.NotNull(hit.Preview);
        Assert.Equal(SearchEngine.SnippetMaxRanges, hit.Preview!.MatchRanges.Count);   // 环形上限 8
    }

    // ---------- ContentIndexer：日志与失败清单 ----------

    [Fact]
    public async Task Indexer_NoJournal_EventsProcessedWithoutLog()
    {
        var index = new InvertedIndex();
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-sbg-j" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "doc.txt");
            await File.WriteAllTextAsync(path, "journal less doc");
            using var indexer = new ContentIndexer(index);   // 不挂日志
            indexer.Enqueue(new FileEvent { Kind = FileChangeKind.Created, FileReference = 7, Path = path, UtcTimestamp = T1 });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!indexer.IsReady && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.True(indexer.IsReady);
            Assert.Single(index.Lookup("journal"));
            Assert.Empty(indexer.FailedFiles);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Indexer_ThrowingJournal_FailureRecordedNotThrown()
    {
        var index = new InvertedIndex();
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-sbg-k" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "doc.txt");
            await File.WriteAllTextAsync(path, "doomed journal doc");
            using var indexer = new ContentIndexer(index, journal: new ThrowingStream());
            indexer.Enqueue(new FileEvent { Kind = FileChangeKind.Created, FileReference = 8, Path = path, UtcTimestamp = T1 });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (indexer.FailedFiles.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Single(indexer.FailedFiles);              // 日志失败只记清单，不阻断索引
            Assert.Single(index.Lookup("doomed"));           // 先写后应用：日志炸了索引仍生效
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Indexer_FailedList_EvictsBeyondKeepCount()
    {
        var index = new InvertedIndex();
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-sbg-f" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var indexer = new ContentIndexer(index);
            for (int i = 0; i < ContentIndexer.FailedKeepCount + 1; i++)   // 101 个不存在文件 → 全部记失败
                indexer.Enqueue(new FileEvent { Kind = FileChangeKind.Created, FileReference = 1000 + i, Path = Path.Combine(dir, $"gone{i}.txt"), UtcTimestamp = T1 });
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (indexer.FailedFiles.Count < ContentIndexer.FailedKeepCount && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal(ContentIndexer.FailedKeepCount, indexer.FailedFiles.Count);   // 环形淘汰最旧
            Assert.DoesNotContain(indexer.FailedFiles, f => f.Contains("gone0.txt", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("journal broken");
    }
}
