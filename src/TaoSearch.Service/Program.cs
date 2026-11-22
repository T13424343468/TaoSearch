using System.IO.Pipes;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.FullText;
using TaoSearch.Kernel.Store;

namespace TaoSearch.Service;

/// <summary>
/// 服务宿主（架构 §3/§7，M4 引擎接线版）：单实例互斥体 + 命名管道 IPC 服务端 +
/// 内核装配（文件名索引恢复/首建、全文快照恢复、内容管线、事件泵三路单源）。
/// 数据目录口径：存储默认 D 盘（TAOSEARCH_DATA 可覆盖，测试注入临时目录）；
/// D 盘不可用时降级 AppData（并行班设计，归并吸收）。
/// </summary>
public static class Program
{
    private const string PipeName = "taosearch.svc.1";
    private static readonly Mutex SingleInstance = new(false, @"Global\TaoSearch.Service.Mutex");

    public static async Task Main(string[] args)
    {
        if (!SingleInstance.WaitOne(0)) { Console.WriteLine("[svc] 单实例互斥体被占用，退出"); return; } // 单实例：重复启动直接退出

        Console.OutputEncoding = System.Text.Encoding.UTF8; // 重定向取证时中文不乱码

        string volumeRoot = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("TAOSEARCH_VOLUME") ?? @"C:\";
        string dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);

        Console.WriteLine($"TaoSearch.Service starting (volume={volumeRoot}, data={dataDir})...");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var host = await ServiceHostFactory.CreateAsync(volumeRoot, dataDir, cts.Token);

        var server = new PipeServer(PipeName, host);
        await server.RunAsync(cts.Token);
    }

    /// <summary>数据目录：存储默认 D 盘（用户拍板），不可用降级 AppData；TAOSEARCH_DATA 最高优先。</summary>
    internal static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("TAOSEARCH_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        string dPath = @"D:\TaoSearch\Index";
        try
        {
            string? dRoot = Path.GetPathRoot(dPath);
            if (dRoot != null && Directory.Exists(dRoot)) return dPath;
        }
        catch (IOException) { }
        // D 盘不可用：降级 AppData（用户拍板「存储默认 D 盘」的兜底顺序，不反向覆盖）
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoSearch", "Index");
    }
}

/// <summary>内核装配（可测试）：恢复优先，快照缺失走首建；事件泵单源三路（内核/内容/订阅）。</summary>
public static class ServiceHostFactory
{
    public static async Task<ServiceHost> CreateAsync(string volumeRoot, string dataDir, CancellationToken ct)
    {
        string fsSnapshot = Path.Combine(dataDir, "fs.snap");
        string fsJournal = Path.Combine(dataDir, "fs.journal");
        string ftSnapshot = Path.Combine(dataDir, "fulltext.snap");
        string ftJournal = Path.Combine(dataDir, "fulltext.journal");

        // 文件名索引：快照+journal 恢复优先，缺失走首建（MFT 失败自动降级慢路径）
        FsIndexEngine fs;
        if (File.Exists(fsSnapshot))
        {
            NameIndex restoredIndex;
            using (var snapStream = new FileStream(fsSnapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                restoredIndex = NameIndex.LoadSnapshot(snapStream);
            fs = new FsIndexEngine(restoredIndex);
            var restore = IndexPersistence.Restore(fsSnapshot, fsJournal, volumeRoot, fs);
            Console.WriteLine($"文件名索引已恢复：{restore.FileCount} 文件（journal {restore.JournalFramesApplied} 帧）");
        }
        else
        {
            fs = new FsIndexEngine(journalSink: new JournalSink(fsJournal));
            await fs.BuildAsync(volumeRoot, ct);
            Console.WriteLine($"文件名索引首建完成：{fs.Snapshot().FileCount} 文件（降级慢路径={fs.Snapshot().UsedFallback}）");
        }

        // 全文索引：快照+journal 恢复优先，缺失从空索引起（内容由事件管线增量补齐）
        var fullText = new InvertedIndex();
        if (File.Exists(ftSnapshot))
        {
            using var journalStream = new FileStream(ftJournal, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            var (index, frames, _) = FullTextPersistence.Restore(ftSnapshot, journalStream);
            fullText = index;
            Console.WriteLine($"全文索引已恢复：{index.DocCount} 文档（journal {frames} 帧）");
        }

        var content = new ContentIndexer(fullText);

        var host = new ServiceHost(fs, fullText, content, volumeRoot);

        // 事件泵：Windows 挂 USN 监听（真实源）；非 Windows（沙箱/测试）由注入源驱动
        if (OperatingSystem.IsWindows())
        {
            var listener = new UsnJournalListener(volumeRoot);
            listener.Start();
            _ = PumpEventsAsync(host, listener, CancellationToken.None);
        }

        return host;
    }

    private static async Task PumpEventsAsync(ServiceHost host, IFileEventSource source, CancellationToken ct)
    {
        try
        {
            await foreach (var e in source.SubscribeAsync(new FileEventFilter(), ct).ConfigureAwait(false))
            {
                await host.OnFileEventAsync(e, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>
/// 命名管道服务端：帧协议 [4B 载荷长度][2B 版本][2B 方法号][4B 序号][载荷]（架构 §3.4）。
/// 读写串行化由 <see cref="PipeSession"/> 承担：Windows 实测同实例 pending 读 + 并发写会挂死，
/// 事件推送帧经 Post 入队由连接泵统一写出，杜绝并发写。
/// </summary>
public sealed class PipeServer(string pipeName, ServiceHost host)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Console.WriteLine($"[pipe] 监听中: {pipeName}");
            await client.WaitForConnectionAsync(ct);
            Console.WriteLine("[pipe] 客户端已连接");
            var session = new PipeSession(client);
            _ = Task.Run(() => session.RunAsync(host, ct), ct); // 每连接一任务，服务不因单客户端阻塞
        }
    }
}

/// <summary>帧上限（防异常/恶意载荷）：64MB。</summary>
public static class PipeServerLimits
{
    public const uint MaxPayloadBytes = 64 * 1024 * 1024;
}
