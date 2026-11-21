using System.Runtime.CompilerServices;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Service;
using Xunit;

namespace TaoSearch.Service.Tests;

public class SearchEngineTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FsIndexEngine _fs;
    private readonly InvertedIndex _fullText;
    private readonly SearchEngine _engine;
    private FallbackEntry[] _entries = [];

    public SearchEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "taosearch-eng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // 落真实临时文件：全文命中预览走文档重扫，路径必须真实可读
        string alpha = WriteFile("alpha report.txt", new string('x', 76) + " alpha body");
        string beta = WriteFile("beta notes.txt", new string('y', 2000));
        string song = WriteFile("song.mp3", new string('z', 5000));
        _entries =
        [
            new FallbackEntry(alpha, 100, T1),
            new FallbackEntry(beta, 2000, T1),
            new FallbackEntry(song, 5000, T1),
        ];

        _fs = new FsIndexEngine(
            mftScanOverride: (_, _, _) => MftScanOutcome.Fail(1),
            fallbackOverride: (root, ct) => Feed(_entries, ct));
        _fullText = new InvertedIndex();
        _engine = new SearchEngine(_fs, _fullText);
        _fs.BuildAsync("C:\\").GetAwaiter().GetResult();
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
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

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<List<ResultBatch>> CollectAsync(QuerySpec spec)
    {
        var batches = new List<ResultBatch>();
        await foreach (var b in _engine.SearchAsync(spec))
            batches.Add(b);
        return batches;
    }

    [Fact]
    public async Task FilenameSearch_SubstringMatch_SingleFinalBatch()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "report", Scope = QueryScope.Filename });

        var batch = Assert.Single(batches);
        Assert.True(batch.IsFinal);
        var hit = Assert.Single(batch.Hits);
        Assert.EndsWith("alpha report.txt", hit.Path, StringComparison.Ordinal);
        Assert.Equal("alpha report.txt", hit.FileName);
        Assert.Equal(100, hit.Size);
        Assert.Equal(0, hit.Score);
        Assert.Null(hit.Preview);
    }

    [Fact]
    public async Task ContentSearch_ReturnsScoreAndPreviewFromRescan()
    {
        string path = Path.Combine(_tempDir, "alpha report.txt");
        await File.WriteAllTextAsync(path, "hello alpha world, alpha again");
        var (fileRef, _) = _fs.Search("alpha").First();
        _fullText.AddDocument(fileRef, await File.ReadAllTextAsync(path), T1);

        var batches = await CollectAsync(new QuerySpec { Text = "alpha", Scope = QueryScope.Content });

        var batch = Assert.Single(batches);
        var hit = Assert.Single(batch.Hits);
        Assert.True(hit.Score > 0);
        Assert.NotNull(hit.Preview);
        Assert.Contains("alpha", hit.Preview!.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(hit.Preview.MatchRanges);
    }

    [Fact]
    public async Task FilenameAndContentBothHit_MergedToSingleHitWithScoreAndPreview()
    {
        string path = Path.Combine(_tempDir, "alpha report.txt");
        var (fileRef, _) = _fs.Search("alpha").First();
        _fullText.AddDocument(fileRef, await File.ReadAllTextAsync(path), T1);

        var batches = await CollectAsync(new QuerySpec { Text = "alpha" });

        var batch = Assert.Single(batches);
        var hit = Assert.Single(batch.Hits);
        Assert.True(hit.Score > 0);
        Assert.NotNull(hit.Preview);
    }

    [Fact]
    public async Task ExtFilter_BrowseOnlyMatchingExtensions()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "ext:txt", Scope = QueryScope.Filename });

        var batch = Assert.Single(batches);
        Assert.Equal(2, batch.Hits.Count);
        Assert.All(batch.Hits, h => Assert.EndsWith(".txt", h.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SizeFilter_RangeAppliesToIndexedMetadata()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "size:1kb-3kb", Scope = QueryScope.Filename });

        var batch = Assert.Single(batches);
        var hit = Assert.Single(batch.Hits);
        Assert.EndsWith("beta notes.txt", hit.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathFilter_PrefixRestrictsResults()
    {
        string prefix = _tempDir[.._tempDir.LastIndexOf(Path.DirectorySeparatorChar)];
        var batches = await CollectAsync(new QuerySpec { Text = $"path:{prefix}", Scope = QueryScope.Filename });

        var batch = Assert.Single(batches);
        Assert.Equal(3, batch.Hits.Count);
    }

    [Fact]
    public async Task Batching_SplitsByBatchSize_LastBatchFinal()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "regex:.*", Scope = QueryScope.Filename, BatchSize = 1 });

        Assert.Equal(3, batches.Count);
        Assert.False(batches[0].IsFinal);
        Assert.False(batches[1].IsFinal);
        Assert.True(batches[2].IsFinal);
        Assert.All(batches, b => Assert.Equal(3, b.TotalEstimate));
        Assert.All(batches, b => Assert.Single(b.Hits));
    }

    [Fact]
    public async Task SyntaxError_EmptyText_YieldsEmptyFinalBatch()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "" });

        var batch = Assert.Single(batches);
        Assert.True(batch.IsFinal);
        Assert.Empty(batch.Hits);
        Assert.Equal(0, batch.TotalEstimate);
    }

    [Fact]
    public async Task RegexSearch_MatchesFullPath()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "regex:al.*a", Scope = QueryScope.Filename });

        var batch = Assert.Single(batches);
        var hit = Assert.Single(batch.Hits);
        Assert.EndsWith("alpha report.txt", hit.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WholeWordFilter_ExactSegmentOnly()
    {
        var batches = await CollectAsync(new QuerySpec { Text = "wholeword:alpha", Scope = QueryScope.Filename });

        var batch = Assert.Single(batches);
        var hit = Assert.Single(batch.Hits);
        Assert.EndsWith("alpha report.txt", hit.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletedFile_ContentHitSuppressed_EvenIfInvertedIndexStale()
    {
        string path = Path.Combine(_tempDir, "beta notes.txt");
        await File.WriteAllTextAsync(path, "phantom term");
        var (fileRef, _) = _fs.Search("beta").First();
        _fullText.AddDocument(fileRef, "phantom term", T1);

        _fs.ApplyEvent(new FileEvent
        {
            Kind = FileChangeKind.Deleted,
            FileReference = fileRef,
            Path = path,
            UtcTimestamp = T1,
        });

        var batches = await CollectAsync(new QuerySpec { Text = "phantom", Scope = QueryScope.Content });
        var batch = Assert.Single(batches);
        Assert.Empty(batch.Hits);
        Assert.True(batch.IsFinal);
    }
}
