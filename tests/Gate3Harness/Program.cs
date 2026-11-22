using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;

// 关3「好不好用」实效实弹装置（2026-11-22）：
//   generate <dir>                 生成确定性语料（中英文文件名、嵌套目录、内容埋独有暗号词）
//   run --queries <json> --report <txt> [--timeout-sec 180]
//                                  连服务管道 → 等索引就绪 → 实弹查询集 → 断言命中/预览 → 延迟 P50/P95
// 严格项失败退出码 1；报告落盘供流水线取证。
// 协议：帧头 12B（LE32 长度 / LE16 版本 / LE16 方法号 / LE32 序号）+ JSON 载荷（契约 v1，IpcContract.cs）。

const string PipeName = "taosearch.svc.1";
const ushort ProtocolVersion = 1;

if (args.Length == 0) { Console.Error.WriteLine("用法: generate <dir> | run --queries <json> --report <txt>"); return 2; }

uint _seq = 0;
NamedPipeClientStream pipe = null!;

if (args[0] == "generate" && args.Length >= 2) { GenerateCorpus(args[1]); return 0; }

if (args[0] == "run")
{
    string? queriesPath = null, reportPath = null; int timeoutSec = 180;
    for (int i = 1; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--queries": queriesPath = args[i + 1]; break;
            case "--report": reportPath = args[i + 1]; break;
            case "--timeout-sec" when int.TryParse(args[i + 1], out var t): timeoutSec = t; break;
        }
    }
    if (queriesPath is null || reportPath is null) { Console.Error.WriteLine("run 需要 --queries 与 --report"); return 2; }
    return await RunAsync(queriesPath, reportPath, timeoutSec);
}

Console.Error.WriteLine($"未知命令: {args[0]}");
return 2;

// ---- 语料生成：确定性、可复现，暗号词与文件一一对应 ----
static void GenerateCorpus(string root)
{
    Directory.CreateDirectory(root);
    string[] dirs = [root, Path.Combine(root, "docs"), Path.Combine(root, "docs", "2024"),
        Path.Combine(root, "代码"), Path.Combine(root, "notes"), Path.Combine(root, "notes", "深目录", "更深层")];
    foreach (var d in dirs) Directory.CreateDirectory(d);

    // 文件名类：中英文子串各若干（文件名即断言目标）
    var nameTargets = new (string Name, string Content)[]
    {
        ("发票明细-2024Q1.txt", "报销用发票明细表，含税额与开票日期。"),
        ("project-report-final.md", "# Project Report\nQ3 review of the report deliverables."),
        ("会议纪要-搜索工具评审.txt", "评审结论：文件名索引毫秒级达标。"),
        ("readme.txt", "TaoSearch gate3 corpus readme."),
        ("报价单-客户A.xlsx.txt", "客户A报价单正文副本。"),
        ("代码" + Path.DirectorySeparatorChar + "SearchEngine.cs.txt", "// 搜索引擎骨架：倒排索引与打分。"),
        ("notes" + Path.DirectorySeparatorChar + "深目录" + Path.DirectorySeparatorChar + "更深层" + Path.DirectorySeparatorChar + "埋点文件.txt", "深层目录可达性验证文件。"),
    };
    foreach (var (name, content) in nameTargets)
        File.WriteAllText(Path.Combine(root, name), content);

    // 内容类：每文件唯一暗号词（文件名不含，只有内容含），验证全文链路与预览
    for (int i = 0; i < 200; i++)
    {
        string marker = $"暗号词{i:D4}";
        string dir = dirs[i % dirs.Length];
        string path = Path.Combine(dir, $"content-{i:D3}.txt");
        File.WriteAllText(path,
            $"常规行 {i}\n本文包含唯一暗号 {marker} 供全文检索验证。\n尾部行 {i}");
    }

    // 干扰集：与暗号词前缀重叠但不相等（防「前缀误命中也算过」）
    for (int i = 0; i < 40; i++)
        File.WriteAllText(Path.Combine(dirs[1], $"decoy-{i:D3}.txt"), $"干扰文件，暗号词{i:D4}X 不应命中精确暗号。");

    Console.WriteLine($"corpus generated at {root}");
}

// ---- 实弹运行 ----
async Task<int> RunAsync(string queriesPath, string reportPath, int timeoutSec)
{
    var lines = new List<string>();
    int strictFails = 0;
    void Check(string id, string name, bool ok, bool strict = true)
    {
        string verdict = ok ? "PASS" : strict ? "FAIL" : "INFO";
        if (!ok && strict) strictFails++;
        lines.Add($"[{verdict}] {id} {name}");
        Console.WriteLine($"[{verdict}] {id} {name}");
    }

    var queries = JsonSerializer.Deserialize<List<Gate3Query>>(File.ReadAllBytes(queriesPath))
        ?? throw new InvalidDataException("queries 文件解析失败");

    pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    // 轮询重试连接：服务启动+索引首建期间管道可能尚未监听，单次 Connect 15s 不够（上轮实弹步超时实证）
    int attempts = 0;
    var connectSw = Stopwatch.StartNew();
    Console.WriteLine($"[INFO] 开始连接管道 {PipeName}（窗口 90s）");
    while (true)
    {
        attempts++;
        try { pipe.Connect(2000); break; }
        catch (TimeoutException) when (connectSw.Elapsed.TotalSeconds < 90)
        {
            Console.WriteLine($"[INFO] 管道连接第 {attempts} 次未就绪，重试…");
            await Task.Delay(500);
        }
        catch (TimeoutException)
        {
            // 90s 仍连不上：自取证服务进程状态，避免盲猜
            var procs = Process.GetProcessesByName("TaoSearch.Service");
            Console.Error.WriteLine($"[DIAG] 90s 未连上管道；服务进程数={procs.Length}");
            foreach (var p in procs) Console.Error.WriteLine($"[DIAG]   pid={p.Id} 启动于={p.StartTime:HH:mm:ss}");
            throw;
        }
    }
    lines.Add($"[INFO] 管道已连接 {PipeName}（尝试 {attempts} 次，耗时 {connectSw.Elapsed.TotalSeconds:F1}s）");

    // 等索引就绪：文件名索引必须就绪；内容索引就绪或超时（超时后内容类查询按实际结果判）
    bool contentReady = false;
    var sw = Stopwatch.StartNew();
    int polls = 0;
    while (sw.Elapsed.TotalSeconds < timeoutSec)
    {
        polls++;
        IndexStatus status;
        try { status = await CallAsync<IndexStatus>(2, null, CancellationToken.None); }
        catch (TimeoutException tex)
        {
            lines.Add($"[FAIL] 索引状态轮询第 {polls} 次: {tex.Message}");
            Console.WriteLine($"[FAIL] 索引状态轮询第 {polls} 次: {tex.Message}");
            break;
        }
        if (polls % 10 == 1)
            Console.WriteLine($"[INFO] 轮询 {polls}: FileCount={status.FileCount} FilenameReady={status.FilenameIndexReady} ContentReady={status.ContentIndexReady} Pending={status.PendingContentQueue}");
        if (status.FilenameIndexReady && status.FileCount > 0)
        {
            contentReady = status.ContentIndexReady && status.PendingContentQueue == 0;
            if (contentReady) break;
        }
        await Task.Delay(1000);
    }
    var readyStatus = await CallAsync<IndexStatus>(2, null, CancellationToken.None);
    lines.Add($"[INFO] 索引状态: FileCount={readyStatus.FileCount} FilenameReady={readyStatus.FilenameIndexReady} " +
              $"ContentReady={readyStatus.ContentIndexReady} Pending={readyStatus.PendingContentQueue} " +
              $"等待耗时={sw.Elapsed.TotalSeconds:F1}s 失败清单={readyStatus.FailedFiles.Count}");
    Check("G3-00", "索引就绪（文件名就绪且有量）", readyStatus.FilenameIndexReady && readyStatus.FileCount > 0);
    Check("G3-01", $"内容索引就绪（实弹前）", contentReady, strict: false);

    // 实弹查询
    var latencies = new List<double>();
    int qn = 0;
    foreach (var q in queries)
    {
        qn++;
        var spec = new QuerySpec { Text = q.Text, Scope = q.Scope, BatchSize = 200 };
        var qsw = Stopwatch.StartNew();
        ResultBatch batch;
        try { batch = await CallAsync<ResultBatch>(1, spec, CancellationToken.None); }
        catch (Exception ex)
        {
            Check($"G3-q{qn}", $"{q.Text} → 管道异常 {ex.Message}", false);
            continue;
        }
        qsw.Stop();
        latencies.Add(qsw.Elapsed.TotalMilliseconds);

        var hits = batch.Hits.ToList();
        bool expectHit = q.Expect != ExpectKind.None;
        bool hitOk = q.Expect switch
        {
            ExpectKind.FileNameContains => hits.Any(h => h.FileName.Contains(q.ExpectValue, StringComparison.OrdinalIgnoreCase)),
            ExpectKind.PathContains => hits.Any(h => h.Path.Contains(q.ExpectValue, StringComparison.OrdinalIgnoreCase)),
            ExpectKind.PreviewNonNull => hits.Any(h => h.Preview is not null && h.Preview.Snippet.Contains(q.ExpectValue, StringComparison.OrdinalIgnoreCase)),
            _ => true,
        };
        bool countOk = !q.MinHits.HasValue || hits.Count >= q.MinHits.Value;
        bool absentOk = !q.MustNotHit || hits.Count == 0;
        Check($"G3-q{qn}", $"{q.Text} → 命中 {hits.Count} 条 预期={q.Expect}:{q.ExpectValue} 耗时={qsw.Elapsed.TotalMilliseconds:F0}ms",
            hitOk && countOk && absentOk);

        if (q.MustNotHit)
            foreach (var h in hits.Take(3)) lines.Add($"[INFO]   不应命中却返回: {h.Path}");
    }

    latencies.Sort();
    double P(double p) => latencies.Count == 0 ? 0 : latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(p * latencies.Count) - 1)];
    lines.Add($"[INFO] 延迟 ms: P50={P(0.5):F0} P95={P(0.95):F0} N={latencies.Count}");
    Check("G3-95", $"首批结果 P95={P(0.95):F0}ms ≤ 200ms（FR-01 判据）", P(0.95) <= 200);

    File.WriteAllLines(reportPath, lines);
    Console.WriteLine($"== 关3 结果: 严格失败 {strictFails} 项，报告 {reportPath} ==");
    return strictFails == 0 ? 0 : 1;
}

// ---- 管道调用（帧头 12B，与 App PipeClient 同构；单次调用 30s 超时防挂死盲等） ----
async Task<TResp> CallAsync<TResp>(ushort method, object? request, CancellationToken ct) where TResp : class
{
    using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    callCts.CancelAfter(TimeSpan.FromSeconds(30));
    var token = callCts.Token;
    try
    {
        byte[] payload = request is null
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(request, request.GetType());
        var frame = new byte[12 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0); // 契约 §3.4：帧头 4B 为载荷长度（此前误写总帧长，与服务端语义相反致挂死）
        BitConverter.GetBytes(ProtocolVersion).CopyTo(frame, 4);
        BitConverter.GetBytes(method).CopyTo(frame, 6);
        BitConverter.GetBytes(_seq++).CopyTo(frame, 8);
        payload.CopyTo(frame, 12);

        await pipe.WriteAsync(frame, token);
        await pipe.FlushAsync(token);

        var header = new byte[12];
        await ReadExactlyAsync(header, token);
        int respLen = BitConverter.ToInt32(header, 0); // 契约 §3.4：响应帧头即载荷长度
        var respPayload = new byte[respLen];
        await ReadExactlyAsync(respPayload, token);
        return JsonSerializer.Deserialize<TResp>(respPayload)
            ?? throw new InvalidDataException($"方法 {method} 空响应");
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        throw new TimeoutException($"方法 {method} 单次调用 30s 超时（写或读挂死，服务端无响应）");
    }
}

async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
{
    int total = 0;
    while (total < buffer.Length)
    {
        int n = await pipe.ReadAsync(buffer.AsMemory(total), ct);
        if (n == 0) throw new IOException("管道已关闭");
        total += n;
    }
}

// ---- 查询集模型 ----
enum ExpectKind { None = 0, FileNameContains = 1, PathContains = 2, PreviewNonNull = 3 }

class Gate3Query
{
    public string Text { get; set; } = "";
    public QueryScope Scope { get; set; } = QueryScope.Filename | QueryScope.Content;
    public ExpectKind Expect { get; set; } = ExpectKind.None;
    public string ExpectValue { get; set; } = "";
    public int? MinHits { get; set; }
    public bool MustNotHit { get; set; }
}
