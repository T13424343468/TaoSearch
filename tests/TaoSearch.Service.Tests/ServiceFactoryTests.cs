using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Kernel.Store;
using TaoSearch.Service;
using Xunit;

namespace TaoSearch.Service.Tests;

/// <summary>
/// 服务工厂装配与宿主重建分支补测（测试规范九.11）：恢复路径/损坏降级/全文快照生命周期/
/// RebuildIndex 双作用域检查点/坏载荷负回执/PipeSession 异常帧收尾。
/// Linux 沙箱可跑恢复与降级路径；首建路径（MFT）以 Windows 守卫在云端 CI 实测。
/// </summary>
public class ServiceFactoryTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taosearch-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string WriteFile(string dir, string name, string content)
    {
        string path = Path.Combine(dir, name);
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

    /// <summary>慢路径引擎桩：MFT 必败，回退枚举注入固定语料（与 ServiceHostIpcTests 同口径）。</summary>
    private static FsIndexEngine BuildStubEngine(FallbackEntry[] entries)
    {
        var fs = new FsIndexEngine(
            mftScanOverride: (_, _, _) => MftScanOutcome.Fail(1),
            fallbackOverride: (root, ct) => Feed(entries, ct));
        fs.BuildAsync("X:\\").GetAwaiter().GetResult();
        return fs;
    }

    /// <summary>动态回退桩：MFT 必败，回退枚举真实目录现状（重建测试用，反映停机期删除）。</summary>
    private static FsIndexEngine BuildDynamicEngine(string volumeDir)
    {
        var fs = new FsIndexEngine(
            mftScanOverride: (_, _, _) => MftScanOutcome.Fail(1),
            fallbackOverride: (root, ct) => Feed(Directory.EnumerateFiles(root)
                .Select(p => new FallbackEntry(p, new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)))
                .ToArray(), ct));
        fs.BuildAsync(volumeDir).GetAwaiter().GetResult();
        return fs;
    }

    /// <summary>落文件名快照检查点（快照 + 空日志），返回日志路径供断言。</summary>
    private static string SaveFsCheckpoint(FsIndexEngine fs, string dataDir)
    {
        string snap = Path.Combine(dataDir, "fs.snap");
        string journal = Path.Combine(dataDir, "fs.journal");
        var sink = new JournalSink(journal);
        IndexPersistence.SaveCheckpoint(fs, snap, sink);
        sink.Dispose();
        return journal;
    }

    /// <summary>预置全文快照：经 ContentIndexer 真实索引一个文档后落检查点。</summary>
    private static void SaveFullTextCheckpoint(string dataDir, long fileRef, string path, string text)
    {
        string snap = Path.Combine(dataDir, "fulltext.snap");
        string journalPath = Path.Combine(dataDir, "fulltext.journal");
        var index = new InvertedIndex();
        var journal = new FileStream(journalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        using var indexer = new ContentIndexer(index, journal: journal);
        indexer.Enqueue(new FileEvent
        {
            Kind = FileChangeKind.Created,
            FileReference = fileRef,
            Path = path,
            UtcTimestamp = T1,
        });
        WaitUntil(() => indexer.PendingCount == 0 && index.Lookup(text.Split(' ')[0]).Count > 0);
        indexer.SaveCheckpoint(snap);
        journal.Dispose();
    }

    private static async Task<IndexStatus> GetStatusAsync(ServiceHost host)
    {
        byte[]? payload = null;
        await host.DispatchAsync((ushort)IpcMethod.GetIndexStatus, IpcProtocol.Version, seq: 1,
            ReadOnlyMemory<byte>.Empty, (frame, _) => { payload = frame[12..]; return ValueTask.CompletedTask; },
            CancellationToken.None);
        return JsonSerializer.Deserialize<IndexStatus>(payload!) ?? throw new InvalidOperationException("状态载荷为空");
    }

    private static async Task<AckResponse> GetAckAsync(ServiceHost host, ushort method, uint seq, ReadOnlyMemory<byte> payload)
    {
        byte[]? body = null;
        await host.DispatchAsync(method, IpcProtocol.Version, seq, payload,
            (frame, _) => { body = frame[12..]; return ValueTask.CompletedTask; }, CancellationToken.None);
        return JsonSerializer.Deserialize<AckResponse>(body!) ?? throw new InvalidOperationException("回执载荷为空");
    }

    private static void WaitUntil(Func<bool> predicate, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("条件在超时前未满足");
            Thread.Sleep(25);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("条件在超时前未满足");
            await Task.Delay(25);
        }
    }

    // ---------- 工厂：恢复路径 ----------

    [Fact]
    public async Task CreateAsync_WithSnapshot_RestoresJournalFrame_AndRotates()
    {
        string volume = NewTempDir();
        string dataDir = NewTempDir();
        string fileA = WriteFile(volume, "alpha report.txt", "alpha body text");
        string fileB = WriteFile(volume, "beta notes.txt", "beta body text");
        string fileC = WriteFile(volume, "gamma memo.txt", "gamma body text");

        var fs = BuildStubEngine(
        [
            new FallbackEntry(fileA, 100, T1),
            new FallbackEntry(fileB, 2000, T1),
        ]);
        string fsJournal = SaveFsCheckpoint(fs, dataDir);

        // 检查点之后追加一条事件 → journal 里留下快照之外的一帧，恢复时须重放
        fs.JournalSink = new JournalSink(fsJournal);
        fs.ApplyEvent(new FileEvent
        {
            Kind = FileChangeKind.Created,
            FileReference = 300,
            ParentReference = 0,
            Path = fileC,
            UtcTimestamp = T1,
        });
        Assert.True(File.Exists(fsJournal));

        var host = await ServiceHostFactory.CreateAsync(volume, dataDir, CancellationToken.None);
        var status = await GetStatusAsync(host);
        Assert.Equal(3, status.FileCount);
        Assert.True(status.FilenameIndexReady);

        // 恢复后落检查点轮转 journal：旧帧清空，重启只做增量重放
        Assert.False(File.Exists(fsJournal));
    }

    [Fact]
    public async Task CreateAsync_WithCorruptFsJournal_StillRestoresSnapshotState()
    {
        string volume = NewTempDir();
        string dataDir = NewTempDir();
        string fileA = WriteFile(volume, "alpha report.txt", "alpha body text");
        string fileB = WriteFile(volume, "beta notes.txt", "beta body text");

        var fs = BuildStubEngine(
        [
            new FallbackEntry(fileA, 100, T1),
            new FallbackEntry(fileB, 2000, T1),
        ]);
        SaveFsCheckpoint(fs, dataDir);

        // 损坏 journal 不得致命：降级截断，快照状态照常恢复
        File.WriteAllText(Path.Combine(dataDir, "fs.journal"), "corrupted journal bytes!!");

        var host = await ServiceHostFactory.CreateAsync(volume, dataDir, CancellationToken.None);
        var status = await GetStatusAsync(host);
        Assert.Equal(2, status.FileCount);
        Assert.True(status.FilenameIndexReady);
    }

    [Fact]
    public async Task CreateAsync_WithCorruptFullTextSnapshot_DegradesAndRebuildsContent()
    {
        string volume = NewTempDir();
        string dataDir = NewTempDir();
        string fileA = WriteFile(volume, "alpha report.txt", "alpha body text");
        string fileB = WriteFile(volume, "beta notes.txt", "beta body text");

        var fs = BuildStubEngine(
        [
            new FallbackEntry(fileA, 100, T1),
            new FallbackEntry(fileB, 2000, T1),
        ]);
        SaveFsCheckpoint(fs, dataDir);

        string ftSnap = Path.Combine(dataDir, "fulltext.snap");
        string garbage = "not a fulltext snapshot at all";
        File.WriteAllText(ftSnap, garbage);
        File.WriteAllText(Path.Combine(dataDir, "fulltext.journal"), "garbage journal");

        var host = await ServiceHostFactory.CreateAsync(volume, dataDir, CancellationToken.None);

        // 全文快照损坏 → 降级重建：存量补齐完成后重落快照（长度不再是垃圾串）
        await WaitUntilAsync(() => File.Exists(ftSnap) && new FileInfo(ftSnap).Length != garbage.Length);
        var status = await GetStatusAsync(host);
        Assert.Equal(2, status.FileCount);
    }

    [Fact]
    public async Task CreateAsync_WithFullTextSnapshot_ContentSearchHitsAfterRestore()
    {
        string volume = NewTempDir();
        string dataDir = NewTempDir();
        string fileA = WriteFile(volume, "alpha report.txt", "alpha body text");

        var fs = BuildStubEngine([new FallbackEntry(fileA, 100, T1)]);
        long fileRef = fs.EnumerateLivePaths().Single().FileReference;
        SaveFsCheckpoint(fs, dataDir);
        SaveFullTextCheckpoint(dataDir, fileRef, fileA, "alpha body text");

        var host = await ServiceHostFactory.CreateAsync(volume, dataDir, CancellationToken.None);

        // 全文快照有效 → 恢复而非重建，内容检索直接命中存量文档
        var spec = new QuerySpec { Text = "alpha", Scope = QueryScope.Content };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(spec);
        var batches = new List<ResultBatch>();
        await host.DispatchAsync((ushort)IpcMethod.Search, IpcProtocol.Version, seq: 7, payload,
            (frame, _) =>
            {
                var batch = JsonSerializer.Deserialize<ResultBatch>(frame[12..]);
                if (batch is not null) batches.Add(batch);
                return ValueTask.CompletedTask;
            }, CancellationToken.None);

        Assert.NotEmpty(batches);
        Assert.Contains(batches.SelectMany(b => b.Hits), h => h.FileReference == fileRef);
    }

    // ---------- 宿主：RebuildIndex 双作用域与检查点 ----------

    [Fact]
    public async Task RebuildIndex_BothScopes_DropsDeletedFiles_AndWritesCheckpoints()
    {
        string volume = NewTempDir();
        string dataDir = NewTempDir();
        string fileA = WriteFile(volume, "alpha report.txt", "alpha body text");
        string fileB = WriteFile(volume, "beta notes.txt", "beta body text");
        string fileC = WriteFile(volume, "gamma memo.txt", "gamma body text");

        var fullText = new InvertedIndex();
        using var ftJournal = new FileStream(Path.Combine(dataDir, "fulltext.journal"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        using var content = new ContentIndexer(fullText, journal: ftJournal);
        var fs = BuildDynamicEngine(volume);
        var host = new ServiceHost(fs, fullText, content, volume, dataDir);

        // 停机期删除的文件：重建必须先清后建，不得残留
        File.Delete(fileC);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new RebuildRequest { Scope = QueryScope.Filename | QueryScope.Content });
        var ack = await GetAckAsync(host, (ushort)IpcMethod.RebuildIndex, seq: 3, payload);
        Assert.True(ack.Ok);

        // 文件名重建后 FileCount 下降；双检查点（fs.snap/fulltext.snap）落盘
        await WaitUntilAsync(() =>
        {
            var status = GetStatusAsync(host).GetAwaiter().GetResult();
            return status.FileCount == 2
                && File.Exists(Path.Combine(dataDir, "fs.snap"))
                && File.Exists(Path.Combine(dataDir, "fulltext.snap"));
        });
        Assert.Empty(content.FailedFiles);
    }

    // ---------- 分发：坏载荷负回执 ----------

    [Fact]
    public async Task Dispatch_BadJsonPayload_NegativeAckWithoutThrow()
    {
        string volume = NewTempDir();
        var fullText = new InvertedIndex();
        using var content = new ContentIndexer(fullText);
        var fs = BuildStubEngine([]);
        var host = new ServiceHost(fs, fullText, content, volume);

        byte[] garbage = "[not valid json"u8.ToArray();
        var ack = await GetAckAsync(host, (ushort)IpcMethod.SubscribeEvents, seq: 9, garbage);
        Assert.False(ack.Ok);
        Assert.StartsWith("bad payload", ack.Error, StringComparison.Ordinal);
    }

    // ---------- 管道会话：异常帧收尾 ----------

    [Fact]
    public async Task PipeSession_EmptyStream_ReturnsCleanly()
    {
        var fullText = new InvertedIndex();
        using var content = new ContentIndexer(fullText);
        var host = new ServiceHost(BuildStubEngine([]), fullText, content, "X:\\");

        using var pipe = new MemoryStream();
        var session = new PipeSession(pipe);
        await session.RunAsync(host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PipeSession_OversizedFrame_ClosesWithoutThrow()
    {
        var fullText = new InvertedIndex();
        using var content = new ContentIndexer(fullText);
        var host = new ServiceHost(BuildStubEngine([]), fullText, content, "X:\\");

        using var pipe = new MemoryStream();
        var header = new byte[12];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), (int)(PipeServerLimits.MaxPayloadBytes + 1));
        pipe.Write(header);
        pipe.Position = 0;

        var session = new PipeSession(pipe);
        await session.RunAsync(host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---------- 内容索引器：检查点静默窗口 ----------

    [Fact]
    public async Task ContentIndexer_SaveCheckpoint_WritesRestorableSnapshot()
    {
        string dir = NewTempDir();
        string path = WriteFile(dir, "doc.txt", "checkpoint term body");
        string snap = Path.Combine(dir, "ft.snap");
        var index = new InvertedIndex();
        var journal = new FileStream(Path.Combine(dir, "ft.journal"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.Read);
        using var indexer = new ContentIndexer(index, journal: journal);

        indexer.Enqueue(new FileEvent
        {
            Kind = FileChangeKind.Created,
            FileReference = 42,
            Path = path,
            UtcTimestamp = T1,
        });
        await WaitUntilAsync(() => indexer.PendingCount == 0 && index.Lookup("checkpoint").Count > 0);

        indexer.SaveCheckpoint(snap);
        Assert.True(File.Exists(snap));

        journal.Dispose();
        using var emptyJournal = new MemoryStream();
        var (restored, _, _) = FullTextPersistence.Restore(snap, emptyJournal);
        Assert.Equal(1, restored.DocCount);
    }

    [Fact]
    public async Task ContentIndexer_SaveCheckpoint_WithoutJournal_IsNoop()
    {
        string dir = NewTempDir();
        string snap = Path.Combine(dir, "ft.snap");
        using var indexer = new ContentIndexer(new InvertedIndex());

        indexer.SaveCheckpoint(snap);
        Assert.False(File.Exists(snap));
    }

    // ---------- 管道服务端：监听循环端到端 ----------

    [Fact]
    public async Task PipeServer_ServesClients_AndStopsOnCancel()
    {
        string volume = NewTempDir();
        string fileA = WriteFile(volume, "alpha report.txt", "alpha body text");
        string fileB = WriteFile(volume, "beta notes.txt", "beta body text");
        var fullText = new InvertedIndex();
        using var content = new ContentIndexer(fullText);
        var host = new ServiceHost(BuildStubEngine(
        [
            new FallbackEntry(fileA, 100, T1),
            new FallbackEntry(fileB, 2000, T1),
        ]), fullText, content, volume);

        string pipeName = "taosearch.test.srv." + Guid.NewGuid().ToString("N");
        var server = new PipeServer(pipeName, host);
        using var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);

        byte[] request = FrameWriter.Encode((ushort)IpcMethod.GetIndexStatus, seq: 42, ReadOnlySpan<byte>.Empty);
        await client.WriteAsync(request);

        var header = new byte[12];
        for (int filled = 0; filled < 12;)
            filled += await client.ReadAsync(header.AsMemory(filled));
        uint seq = BitConverter.ToUInt32(header, 8);
        int payloadLength = BitConverter.ToInt32(header, 0);
        var payload = new byte[payloadLength];
        for (int filled = 0; filled < payloadLength;)
            filled += await client.ReadAsync(payload.AsMemory(filled));

        Assert.Equal(42u, seq);
        var status = JsonSerializer.Deserialize<IndexStatus>(payload) ?? throw new InvalidOperationException("状态载荷为空");
        Assert.Equal(2, status.FileCount);

        // 取消令牌收班：监听循环退出不悬挂
        cts.Cancel();
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    // ---------- 工厂：首建路径（MFT 仅 Windows 可跑，云端 CI 实测） ----------

    [Fact]
    public async Task CreateAsync_FirstBuild_ProducesSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return; // MftScanner P/Invoke 仅 Windows；沙箱跳过，云端 CI 执行

        string volume = NewTempDir();
        string dataDir = NewTempDir();
        WriteFile(volume, "alpha report.txt", "alpha body text");
        WriteFile(volume, "beta notes.txt", "beta body text");

        var host = await ServiceHostFactory.CreateAsync(volume, dataDir, CancellationToken.None);

        // 先听后建：CreateAsync 立即返回，首建后台跑完落快照
        await WaitUntilAsync(() => File.Exists(Path.Combine(dataDir, "fs.snap")), timeoutMs: 30_000);
        await WaitUntilAsync(() => GetStatusAsync(host).GetAwaiter().GetResult().FileCount == 2);
    }

    [Fact]
    public async Task CreateAsync_CorruptFsSnapshot_DegradesToFirstBuild()
    {
        if (!OperatingSystem.IsWindows()) return; // MftScanner P/Invoke 仅 Windows；沙箱跳过，云端 CI 执行

        string volume = NewTempDir();
        string dataDir = NewTempDir();
        WriteFile(volume, "alpha report.txt", "alpha body text");
        WriteFile(volume, "beta notes.txt", "beta body text");

        string fsSnap = Path.Combine(dataDir, "fs.snap");
        File.WriteAllText(fsSnap, "corrupted snapshot bytes");

        var host = await ServiceHostFactory.CreateAsync(volume, dataDir, CancellationToken.None);

        // 快照损坏 → 降级全量首建 → 首建完成重落有效快照
        await WaitUntilAsync(() =>
        {
            try
            {
                using var stream = new FileStream(fsSnap, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return NameIndex.LoadSnapshot(stream).Count == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }, timeoutMs: 30_000);
    }
}
