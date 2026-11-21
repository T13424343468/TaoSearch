using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FullText;
using TaoSearch.Service;
using Xunit;

namespace TaoSearch.Service.Tests;

public class ContentIndexerTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly InvertedIndex _index = new();
    private readonly ContentIndexer _indexer;

    public ContentIndexerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "taosearch-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _indexer = new ContentIndexer(_index);
    }

    public void Dispose()
    {
        _indexer.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private FileEvent Created(long fileRef, string path) => new()
    {
        Kind = FileChangeKind.Created,
        FileReference = fileRef,
        Path = path,
        UtcTimestamp = T1,
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("条件在超时前未满足");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task CreatedEvent_TextFile_IndexedIntoInvertedIndex()
    {
        string path = WriteFile("doc.txt", "unique gremlin words here");

        _indexer.Enqueue(Created(42, path));

        await WaitUntilAsync(() => _index.Lookup("gremlin").Any(h => h.DocId == 42));
        Assert.True(_indexer.IsReady);
        Assert.Empty(_indexer.FailedFiles);
    }

    [Fact]
    public async Task DeletedEvent_RemovesDocument()
    {
        string path = WriteFile("doc.txt", "vanishing term");
        _indexer.Enqueue(Created(7, path));
        await WaitUntilAsync(() => _index.Lookup("vanishing").Count > 0);

        _indexer.Enqueue(new FileEvent
        {
            Kind = FileChangeKind.Deleted,
            FileReference = 7,
            Path = path,
            UtcTimestamp = T1,
        });

        await WaitUntilAsync(() => _index.Lookup("vanishing").Count == 0);
    }

    [Fact]
    public async Task RenamedEvent_NoContentChange()
    {
        string path = WriteFile("doc.txt", "stable term");
        _indexer.Enqueue(Created(9, path));
        await WaitUntilAsync(() => _index.Lookup("stable").Count > 0);

        _indexer.Enqueue(new FileEvent
        {
            Kind = FileChangeKind.Renamed,
            FileReference = 9,
            Path = Path.Combine(_tempDir, "renamed.txt"),
            UtcTimestamp = T1,
        });

        await WaitUntilAsync(() => _indexer.PendingCount == 0);
        Assert.NotEmpty(_index.Lookup("stable"));
    }

    [Fact]
    public async Task UnknownExtension_SkippedWithoutFailure()
    {
        string path = WriteFile("blob.xyz", "opaque content");

        _indexer.Enqueue(Created(11, path));

        await WaitUntilAsync(() => _indexer.PendingCount == 0);
        Assert.Empty(_index.Lookup("opaque"));
        Assert.Empty(_indexer.FailedFiles);
    }

    [Fact]
    public async Task RegisteredParserExtension_Indexed()
    {
        var parsers = new ParserRegistry();
        Assert.True(parsers.Register("ext.v1", [".xyz"]));
        var indexer = new ContentIndexer(_index, parsers);
        try
        {
            string path = WriteFile("data.xyz", "parser fed content");
            indexer.Enqueue(Created(13, path));

            await WaitUntilAsync(() => _index.Lookup("parser").Any(h => h.DocId == 13));
        }
        finally
        {
            indexer.Dispose();
        }
    }

    [Fact]
    public async Task OversizedFile_RecordedAsFailed()
    {
        string path = WriteFile("big.txt", new string('a', (int)ContentIndexer.MaxIndexBytes + 16));

        _indexer.Enqueue(Created(21, path));

        await WaitUntilAsync(() => _indexer.FailedFiles.Count > 0);
        Assert.Contains(_indexer.FailedFiles, f => f.Contains("big.txt", StringComparison.Ordinal));
        Assert.Empty(_index.Lookup("a"));
    }

    [Fact]
    public async Task Pause_SuspendsProcessing_ResumeDrains()
    {
        string path = WriteFile("paused.txt", "deferred term");
        _indexer.Pause();
        Assert.True(_indexer.IsPaused);

        _indexer.Enqueue(Created(31, path));
        await Task.Delay(300);
        Assert.Empty(_index.Lookup("deferred"));

        _indexer.Resume();
        await WaitUntilAsync(() => _index.Lookup("deferred").Any(h => h.DocId == 31));
        Assert.False(_indexer.IsPaused);
    }

    [Fact]
    public async Task Rebuild_ClearsAndRequeuesLiveFiles()
    {
        string keep = WriteFile("keep.txt", "kept term");
        string drop = WriteFile("drop.txt", "dropped term");
        _indexer.Enqueue(Created(51, drop));
        await WaitUntilAsync(() => _index.Lookup("dropped").Count > 0);

        _indexer.Rebuild([(51, keep)]);

        await WaitUntilAsync(() => _index.Lookup("kept").Any(h => h.DocId == 51));
        Assert.Empty(_index.Lookup("dropped"));
    }
}
