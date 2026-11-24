using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using Xunit;

namespace TaoSearch.Kernel.Tests;

public class FsIndexEngineTests : IDisposable
{
    private readonly string _tempDir;

    public FsIndexEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "taosearch-fb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "子目录"));
        File.WriteAllText(Path.Combine(_tempDir, "report.txt"), "x");
        File.WriteAllText(Path.Combine(_tempDir, "子目录", "notes.txt"), "y");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task MftPermissionDenied_FallsBackToRecursiveScan()
    {
        // 反向用例①：标准权限下 MFT 枚举被拒（Win32Error=5），引擎自动降级目录递归慢路径
        var engine = new FsIndexEngine(
            mftScanOverride: (root, sink, ct) => MftScanOutcome.Fail(MftScanner.Win32ErrorAccessDenied));
        var result = await engine.BuildAsync(_tempDir);

        Assert.True(result.UsedFallback);
        var status = engine.Snapshot();
        Assert.True(status.Ready);
        Assert.True(status.UsedFallback);
        Assert.NotNull(status.LastError);
        Assert.Contains("5", status.LastError, StringComparison.Ordinal);
        Assert.Equal(3, result.FileCount); // 子目录 + 2 文件（根目录本身不入索引，与 MFT 路径跳过 ref 5 口径一致）

        var hit = Assert.Single(engine.Search("report"));
        Assert.Equal(Path.Combine(_tempDir, "report.txt"), hit.Path);

        var nested = Assert.Single(engine.Search("notes"));
        Assert.Equal(Path.Combine(_tempDir, "子目录", "notes.txt"), nested.Path);
    }

    [Fact]
    public async Task MftScanSuccess_BuildsIndexAndRebuildsPathWithPrefix()
    {
        var engine = new FsIndexEngine(
            mftScanOverride: (root, sink, ct) =>
            {
                sink(100, 5, "Users");
                sink(200, 100, "report.txt");
                return MftScanOutcome.Ok(2, 1);
            });
        var result = await engine.BuildAsync("C:\\");

        Assert.False(result.UsedFallback);
        Assert.Equal(2, result.FileCount);
        Assert.True(engine.Snapshot().Ready);

        var hit = Assert.Single(engine.Search("report"));
        Assert.Equal("C:\\Users\\report.txt", hit.Path);
    }

    [Fact]
    public async Task UsnEvents_ApplyCreateRenameDeleteClosedLoop()
    {
        var engine = new FsIndexEngine(
            mftScanOverride: (root, sink, ct) =>
            {
                sink(100, 5, "Users");
                sink(200, 100, "report.txt");
                return MftScanOutcome.Ok(2, 1);
            });
        await engine.BuildAsync("C:\\");
        var t = DateTimeOffset.UtcNow;

        // 建：父引用号入索引，路径可重建
        engine.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = 300, ParentReference = 100, Path = "notes.txt", UtcTimestamp = t });
        Assert.Single(engine.Search("notes"));

        // 改名：同引用号覆盖，旧名消失、新名路径正确
        engine.ApplyEvent(new FileEvent { Kind = FileChangeKind.Renamed, FileReference = 300, ParentReference = 100, Path = "notes-v2.txt", UtcTimestamp = t });
        Assert.Empty(engine.Search("notes.txt"));
        var renamed = Assert.Single(engine.Search("notes-v2"));
        Assert.Equal("C:\\Users\\notes-v2.txt", renamed.Path);

        // 删：出索引
        engine.ApplyEvent(new FileEvent { Kind = FileChangeKind.Deleted, FileReference = 300, Path = "notes-v2.txt", UtcTimestamp = t });
        Assert.Empty(engine.Search("notes-v2"));
        Assert.Equal(2, engine.Snapshot().FileCount);

        // 内容变更：记录仍在，重复 Upsert 不产生重复条目
        engine.ApplyEvent(new FileEvent { Kind = FileChangeKind.ContentModified, FileReference = 200, ParentReference = 100, Path = "report.txt", UtcTimestamp = t });
        var hit = Assert.Single(engine.Search("report"));
        Assert.Equal("C:\\Users\\report.txt", hit.Path);
    }

    [Fact]
    public async Task AttachListener_ConsumesEventsIntoIndex()
    {
        var source = new FakeEventSource();
        var engine = new FsIndexEngine(
            mftScanOverride: (root, sink, ct) =>
            {
                sink(100, 5, "Users");
                return MftScanOutcome.Ok(1, 1);
            });
        await engine.BuildAsync("C:\\");
        engine.AttachListener(source);

        source.Publish(new FileEvent
        {
            Kind = FileChangeKind.Created,
            FileReference = 400,
            ParentReference = 100,
            Path = "todo.txt",
            UtcTimestamp = DateTimeOffset.UtcNow,
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (engine.Snapshot().FileCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(2, engine.Snapshot().FileCount);
        var hit = Assert.Single(engine.Search("todo"));
        Assert.Equal("C:\\Users\\todo.txt", hit.Path);
    }

    private sealed class FakeEventSource : IFileEventSource
    {
        private readonly Channel<FileEvent> _channel = Channel.CreateUnbounded<FileEvent>();

        public void Publish(FileEvent e) => _channel.Writer.TryWrite(e);

        public async IAsyncEnumerable<FileEvent> SubscribeAsync(
            FileEventFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(ct)) yield return e;
        }
    }

    [Fact]
    public async Task Pause_StopsUpsert_ResumeContinues()
    {
        // 0.1.0.18 真机红灯修复：暂停必须真的停文件名首建（此前暂停只管内容管线，真机实锤没停）
        var engine = new FsIndexEngine(
            mftScanOverride: (root, sink, ct) =>
            {
                sink(100, 5, "a.txt");
                return MftScanOutcome.Ok(1, 1);
            });
        engine.Pause();
        Assert.True(engine.IsPaused);

        // 扫描在暂停点同步阻塞，必须放后台线程，否则测试线程被堵死、Resume 永远执行不到
        var buildTask = Task.Run(() => engine.BuildAsync("C:\\"));
        await Task.Delay(500); // 暂停期间扫描停在 PausePoint，索引不前进
        Assert.Equal(0, engine.Snapshot().FileCount);

        engine.Resume();
        var result = await buildTask;
        Assert.Equal(1, result.FileCount);
        Assert.False(engine.IsPaused);
        Assert.True(engine.Snapshot().Ready);
        Assert.True(engine.Snapshot().Paused == false);
    }
}
