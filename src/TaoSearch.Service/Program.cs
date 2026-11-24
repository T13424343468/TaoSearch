using System.IO.Pipes;
#if WINDOWS
using System.ServiceProcess;
#endif
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
/// 运行模式（0.1.0.9 装完即起令）：Windows 服务模式（SCM 拉起，安装器注册 TaoSearchService）
/// 与控制台模式（云端测试/调试 Start-Process 直拉）共用同一运行核心。
/// </summary>
public static class Program
{
    private const string PipeName = "taosearch.svc.1";
    private static readonly Mutex SingleInstance = new(false, @"Global\TaoSearch.Service.Mutex");

    public static async Task Main(string[] args)
    {
        CrashLog.Install(); // 崩溃日志随包落盘（v3.22 五章令）：托管异常写 service-crash.log，最早处安装

#if WINDOWS
        // Windows 服务模式：SCM 拉起时 UserInteractive=false，或显式 --service；
        // 控制台模式：Start-Process 直拉（UserInteractive=true），--console 可强制。
        bool serviceMode = args.Contains("--service", StringComparer.OrdinalIgnoreCase)
            || (!Environment.UserInteractive && !args.Contains("--console", StringComparer.OrdinalIgnoreCase));
        if (serviceMode)
        {
            ServiceBase.Run(new TaoSearchWindowsService());
            return;
        }
#endif

        string volumeRoot = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("TAOSEARCH_VOLUME") ?? @"C:\";
        await RunConsoleAsync(volumeRoot);
    }

    /// <summary>控制台模式（云端测试/调试）：互斥体 + 宿主 + 管道，Ctrl+C 退出。</summary>
    internal static async Task RunConsoleAsync(string volumeRoot)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8; // 重定向取证时中文不乱码（须先于占用文案，否则 OEM 代码页下中文变 ?）
        if (!SingleInstance.WaitOne(0)) { Console.WriteLine("[svc] 单实例互斥体被占用，退出"); return; } // 单实例：重复启动直接退出

        string dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);

        Console.WriteLine($"TaoSearch.Service starting (volume={volumeRoot}, data={dataDir})...");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var host = await ServiceHostFactory.CreateAsync(volumeRoot, dataDir, cts.Token);

        var server = new PipeServer(PipeName, host);
        await server.RunAsync(cts.Token);
    }

    /// <summary>Windows 服务模式：互斥体 + 宿主 + 管道；SCM 停止时经取消令牌收尾。</summary>
    internal static async Task RunServiceCoreAsync(string volumeRoot, CancellationToken ct)
    {
        if (!SingleInstance.WaitOne(0)) return; // 单实例：已有实例（如调试控制台）在跑则让位

        string dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);

        var host = await ServiceHostFactory.CreateAsync(volumeRoot, dataDir, ct);
        var server = new PipeServer(PipeName, host);
        await server.RunAsync(ct);
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

#if WINDOWS
/// <summary>Windows 服务壳（0.1.0.9 装完即起令）：安装器注册为自动启动服务，开机即常驻，免日常提权。</summary>
internal sealed class TaoSearchWindowsService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public TaoSearchWindowsService()
    {
        ServiceName = "TaoSearchService";
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        // OnStart 须在 30s 内返回：运行核心转后台任务，管道先监听后建索引（先听后建）
        var volume = Environment.GetEnvironmentVariable("TAOSEARCH_VOLUME") ?? @"C:\";
        _runTask = Program.RunServiceCoreAsync(volume, _cts.Token);
    }

    protected override void OnStop()
    {
        try
        {
            _cts.Cancel();
            _runTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException) { /* 取消引发的聚合异常属正常收尾 */ }
        base.OnStop();
    }

    protected override void OnShutdown()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        base.OnShutdown();
    }
}
#endif

/// <summary>内核装配（可测试）：恢复优先，快照缺失走首建；事件泵单源三路（内核/内容/订阅）。</summary>
public static class ServiceHostFactory
{
    public static async Task<ServiceHost> CreateAsync(string volumeRoot, string dataDir, CancellationToken ct)
    {
        string fsSnapshot = Path.Combine(dataDir, "fs.snap");
        string fsJournal = Path.Combine(dataDir, "fs.journal");
        string ftSnapshot = Path.Combine(dataDir, "fulltext.snap");
        string ftJournal = Path.Combine(dataDir, "fulltext.journal");

        // 文件名索引：快照+journal 恢复优先，缺失或损坏走首建（关7口径：磁盘损坏/升级残留不得致命）
        FsIndexEngine fs;
        NameIndex? restoredIndex = null;
        if (File.Exists(fsSnapshot))
        {
            try
            {
                using (var snapStream = new FileStream(fsSnapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    restoredIndex = NameIndex.LoadSnapshot(snapStream);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                restoredIndex = null;
                Console.WriteLine($"文件名快照不可用（{ex.Message}），降级全量首建");
            }
        }

        // 先听后建（0.1.0.9 真机红灯修复）：首建转后台，管道先监听——
        // 壳层连接不再超时，建索引期间 GetIndexStatus 照常应答（FileCount 实时增长，Ready=false）。
        Task<FsIndexBuildResult>? fsFirstBuild = null;
        if (restoredIndex is not null)
        {
            fs = new FsIndexEngine(restoredIndex);
            var restore = IndexPersistence.Restore(fsSnapshot, fsJournal, volumeRoot, fs);
            Console.WriteLine($"文件名索引已恢复：{restore.FileCount} 文件（journal {restore.JournalFramesApplied} 帧）");

            // 关7实弹实证：恢复路径从不落快照 → journal 永不轮转，每次重启重放全部历史帧。
            // 重放已完成，此刻挂 sink 不会双写；落 checkpoint 顺带轮转 journal，重启增量重放。
            // 只读盘/满盘（关7阶段4）落盘失败只降级不致命：内存索引照常服务。
            var restoreSink = new JournalSink(fsJournal);
            fs.JournalSink = restoreSink;
            try
            {
                var restoreCp = IndexPersistence.SaveCheckpoint(fs, fsSnapshot, restoreSink);
                Console.WriteLine($"恢复后快照已落盘：{restoreCp.FileCount} 文件（{restoreCp.SnapshotBytes} B，轮转 {restoreCp.JournalDiscardedBytes} B）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"恢复后快照落盘失败（只读/满盘？），降级为内存服务: {ex.Message}");
            }
        }
        else
        {
            var sink = new JournalSink(fsJournal);
            fs = new FsIndexEngine(journalSink: sink);
            fsFirstBuild = fs.BuildAsync(volumeRoot, ct);
            _ = fsFirstBuild.ContinueWith(
                async t =>
                {
                    if (t.IsCanceled) return;
                    if (t.IsFaulted)
                    {
                        Console.WriteLine($"文件名索引首建失败: {t.Exception?.GetBaseException().Message}");
                        return;
                    }
                    var result = t.Result;
                    Console.WriteLine($"文件名索引首建完成：{result.FileCount} 文件（降级慢路径={result.UsedFallback}）");

                    // 关7实弹实证：首建后从不落快照 → 真机每次重启全量重建（真实盘几十万文件=分钟级冷启动）。
                    // 满盘/只读（关7口径）落盘失败只降级不致命。
                    try
                    {
                        var cp = IndexPersistence.SaveCheckpoint(fs, fsSnapshot, sink);
                        Console.WriteLine($"首建快照已落盘：{cp.FileCount} 文件（{cp.SnapshotBytes} B，{cp.ElapsedMilliseconds} ms）");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine($"首建快照落盘失败（只读/满盘？），降级为内存服务: {ex.Message}");
                    }
                }, CancellationToken.None);
        }

        // 全文索引：快照+journal 恢复优先，缺失或损坏从空索引起（内容由事件管线增量补齐）
        var fullText = new InvertedIndex();
        bool fullTextFresh = !File.Exists(ftSnapshot);
        if (!fullTextFresh)
        {
            try
            {
                using var journalStream = new FileStream(ftJournal, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                var (index, frames, _) = FullTextPersistence.Restore(ftSnapshot, journalStream);
                fullText = index;
                Console.WriteLine($"全文索引已恢复：{index.DocCount} 文档（journal {frames} 帧）");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                fullText = new InvertedIndex();
                fullTextFresh = true;
                Console.WriteLine($"全文快照不可用（{ex.Message}），降级重建");
            }
        }

        // 全文 journal 生命周期流（关7口径）：事件先写日志后进索引，强杀后重放不丢；
        // 恢复失败降级重建时日志作废清零，避免旧帧污染新索引。
        if (fullTextFresh && File.Exists(ftJournal)) File.Delete(ftJournal);
        var ftLifetimeJournal = new FileStream(ftJournal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var content = new ContentIndexer(fullText, journal: ftLifetimeJournal);

        var host = new ServiceHost(fs, fullText, content, volumeRoot, dataDir);

        // 存量内容补齐（关3实弹实证缺陷）：内容管线只吃事件流，USN 不重放历史，
        // 全文快照缺失（首建）时存量文件内容永远不进倒排 → 全文搜索对存量恒空。
        // 快照缺失时把文件名索引存量全集入队内容管线；有快照则增量由事件泵承担，不重清。
        // 先听后建口径：首建路径须等文件名首建完成再补齐，存量全集才完整。
        if (fullTextFresh)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (fsFirstBuild is not null) await fsFirstBuild;
                    content.Rebuild(fs.EnumerateLivePaths(), CancellationToken.None);
                    // 补齐完成即落检查点（关7口径）：强杀后重启不再全量重扫全文
                    content.SaveCheckpoint(ftSnapshot);
                    Console.WriteLine("内容索引存量补齐完成，全文快照已落盘");
                }
                catch (Exception ex) { Console.WriteLine($"内容索引存量补齐失败: {ex.Message}"); }
            }, CancellationToken.None);
        }

        // 事件泵：Windows 且卷根语义才挂 USN 监听（真实源）；非 Windows（沙箱/测试）由注入源驱动。
        // USN 日志是整卷语义且事件只带引用号不带路径，非卷根（如索引子目录）挂整卷监听会把根外
        // 文件灌进索引（2026-11-23 云端 runner 实测 FileCount 2→14 实锤）——与 MFT 快路径同口径，
        // 非卷根不挂监听，走引擎慢路径。
        if (OperatingSystem.IsWindows() && MftScanner.IsVolumeRoot(volumeRoot))
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
    /// <summary>
    /// 管道 ACL（真机红灯修复）：服务以管理员/LocalSystem 运行时，Windows 命名管道默认 DACL
    /// 只给创建者与 Administrators 完全访问，普通权限壳层连上后写请求帧会被拒。
    /// 显式放行 WorldSid 读写，壳层无论何种权限均可连通；沙箱 Linux 无 ACL 概念走默认构造。
    /// </summary>
    internal static NamedPipeServerStream CreateServerStream(string name)
    {
#if WINDOWS
        // .NET 10 口径：PipeSecurity/PipeAccessRule 命名空间是 System.IO.Pipes（程序集文件名才带 AccessControl）；
        // 带 ACL 的管道须走 NamedPipeServerStreamAcl.Create 工厂（.NET Core 无 PipeSecurity 构造重载）
        var security = new System.IO.Pipes.PipeSecurity();
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.IO.Pipes.PipeAccessRights.ReadWrite,
            System.Security.AccessControl.AccessControlType.Allow));
        return System.IO.Pipes.NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
        return new NamedPipeServerStream(name, PipeDirection.InOut, 4,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
#endif
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream client;
            try
            {
                client = CreateServerStream(pipeName);
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                // 实例暂满（会话尚未释放）：等待重试，绝不因 All pipe instances busy 崩进程（关5实弹实证）
                await Task.Delay(200, ct);
                continue;
            }
            Console.WriteLine($"[pipe] 监听中: {pipeName}");
            await client.WaitForConnectionAsync(ct);
            Console.WriteLine("[pipe] 客户端已连接");
            var session = new PipeSession(client);
            // 每连接一任务，服务不因单客户端阻塞；会话终了必须释放实例，否则 4 实例耗尽后服务永久不可用
            _ = Task.Run(async () =>
            {
                try { await session.RunAsync(host, ct); }
                catch (Exception) { /* 会话级异常不外泄：单连接故障不崩服务 */ }
                finally { client.Dispose(); }
            }, ct);
        }
    }
}

/// <summary>帧上限（防异常/恶意载荷）：64MB。</summary>
public static class PipeServerLimits
{
    public const uint MaxPayloadBytes = 64 * 1024 * 1024;
}
