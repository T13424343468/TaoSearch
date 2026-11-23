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

if (args[0] == "perf")
{
    // perf --publish <dir> --root <corpus> --files <n> --report <txt> [--timeout-sec 600]
    // 模拟令（2026-11-23 老板问「不能模拟？」）：大文件量首扫性能云端实测——
    // 生成 N 文件语料 → 起服务（计时起点=进程启动）→ 等文件名索引就绪记耗时 → 文件名查询延迟抽样。
    // 云端虚拟盘无 MFT（两轮实测 Win32Error=1），本命令量的是枚举回退路径，MFT 直读性能仍须真机。
    string? publishDir = null, root = null, reportPath = null;
    int files = 100000, timeoutSec = 600;
    for (int i = 1; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--publish": publishDir = args[i + 1]; break;
            case "--root": root = args[i + 1]; break;
            case "--files" when int.TryParse(args[i + 1], out var f): files = f; break;
            case "--report": reportPath = args[i + 1]; break;
            case "--timeout-sec" when int.TryParse(args[i + 1], out var t): timeoutSec = t; break;
        }
    }
    if (publishDir is null || root is null || reportPath is null)
    { Console.Error.WriteLine("perf 需要 --publish --root --report"); return 2; }
    return await PerfAsync(publishDir, root, files, reportPath, timeoutSec);
}

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
        try { batch = await CallSearchAsync(spec, CancellationToken.None); }
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

// ---- 大文件量性能模拟（模拟令：真实文件量首扫与查询延迟，云端枚举路径实测） ----
async Task<int> PerfAsync(string publishDir, string root, int files, string reportPath, int timeoutSec)
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

    // 1. 大语料生成（bucket 分桶避免单目录 10 万项拖垮枚举与文件系统）
    GenerateLargeCorpus(root, files);
    lines.Add($"[INFO] 语料: {files} 文件 @ {root}");

    // 2. 起服务，计时起点 = 进程启动前一刻
    var svc = Process.Start(new ProcessStartInfo(
        Path.Combine(publishDir, "TaoSearch.Service.exe"), $"\"{root}\"")
    {
        WorkingDirectory = publishDir,
        UseShellExecute = false,
    }) ?? throw new InvalidOperationException("服务进程启动失败");
    var sw = Stopwatch.StartNew();

    // 3. 连管道（90s 窗口重试，同 RunAsync 口径）
    pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    var connectSw = Stopwatch.StartNew();
    while (true)
    {
        try { pipe.Connect(2000); break; }
        catch (TimeoutException) when (connectSw.Elapsed.TotalSeconds < 90) { await Task.Delay(500); }
    }
    lines.Add($"[INFO] 管道已连接（{connectSw.Elapsed.TotalSeconds:F1}s）");

    // 4. 等文件名索引就绪（内容索引后台继续，不阻塞本口径）
    IndexStatus? ready = null;
    while (sw.Elapsed.TotalSeconds < timeoutSec)
    {
        IndexStatus st;
        try { st = await CallAsync<IndexStatus>(2, null, CancellationToken.None); }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 状态轮询异常: {ex.Message}");
            break;
        }
        if (st.FilenameIndexReady && st.FileCount >= files) { ready = st; break; }
        await Task.Delay(1000);
    }
    sw.Stop();
    if (ready is null)
    {
        Check("G9-00", $"大语料首扫就绪（{timeoutSec}s 内）", false);
        try { svc.Kill(); } catch { }
        File.WriteAllLines(reportPath, lines);
        return 1;
    }
    lines.Add($"[INFO] 首扫: {ready.FileCount} 文件 耗时={sw.Elapsed.TotalSeconds:F1}s（云端虚拟盘无 MFT，枚举回退路径）");
    Check("G9-00", $"大语料首扫就绪：{ready.FileCount}/{files} 文件 {sw.Elapsed.TotalSeconds:F1}s", ready.FileCount >= files);

    // 5. 文件名查询延迟抽样（FR-01 毫秒级判据）
    // 口径（0.1.0.14 修正）：毫秒级判据只卡低命中探针（典型文件名搜索，≤个位数命中）；
    // 万级/全量命中探针（perf-00 命中 10 万）属流式吞吐场景，只记 INFO 不卡 200ms 门禁。
    // CallSearchAsync 单请求读至 IsFinal，杜绝「只读首批楔死」与「每批重发滚雪球」两类用例缺陷。
    var lat = new List<double>();
    // 预热查询（一次性 JIT/路径编译成本由 G9-00 首扫口径承担，探针量稳态延迟）
    try { await CallSearchAsync(new QuerySpec { Text = "perf-0000000", Scope = QueryScope.Filename, BatchSize = 200 }, CancellationToken.None); }
    catch { }
    // 低命中探针：性能语料只含 perf-* 文件（GenerateLargeCorpus），探针词取真实存在的单命中文件名
    string[] probesLow = ["perf-0000042", "perf-0000001", "perf-0000999", "perf-0001234", "perf-0000555"];
    foreach (var text in probesLow)
    {
        var spec = new QuerySpec { Text = text, Scope = QueryScope.Filename, BatchSize = 200 };
        var qsw = Stopwatch.StartNew();
        ResultBatch batch;
        try { batch = await CallSearchAsync(spec, CancellationToken.None); }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 查询 {text} 管道异常: {ex.Message}");
            Check($"G9-q{text}", $"{text} → 管道异常", false);
            continue;
        }
        qsw.Stop();
        lat.Add(qsw.Elapsed.TotalMilliseconds);
        lines.Add($"[INFO] 查询 {text}: 命中 {batch.Hits.Count()} 条 {qsw.Elapsed.TotalMilliseconds:F0}ms");
    }
    // 大命中流式吞吐（INFO）：验证多批流式回传完整、不错位、不楔死
    string[] probesMass = ["perf-00", "perf-09"];
    foreach (var text in probesMass)
    {
        var spec = new QuerySpec { Text = text, Scope = QueryScope.Filename, BatchSize = 20000 };
        var qsw = Stopwatch.StartNew();
        ResultBatch batch;
        try { batch = await CallSearchAsync(spec, CancellationToken.None); }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 查询 {text} 管道异常: {ex.Message}");
            Check($"G9-q{text}", $"{text} → 管道异常", false);
            continue;
        }
        qsw.Stop();
        lines.Add($"[INFO] 大命中 {text}: 命中 {batch.Hits.Count()} 条 整流 {qsw.Elapsed.TotalMilliseconds:F0}ms（流式吞吐，不卡毫秒级门禁）");
    }
    lat.Sort();
    double P(double p) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)Math.Ceiling(p * lat.Count) - 1)];
    lines.Add($"[INFO] 低命中探针延迟 ms: P50={P(0.5):F0} P95={P(0.95):F0} N={lat.Count}");
    Check("G9-95", $"低命中文件名查询 P95={P(0.95):F0}ms ≤ 200ms（FR-01 毫秒级判据，典型搜索口径）", P(0.95) <= 200);

    try { svc.Kill(); } catch { }
    File.WriteAllLines(reportPath, lines);
    Console.WriteLine($"== 关9 结果: 严格失败 {strictFails} 项，报告 {reportPath} ==");
    return strictFails == 0 ? 0 : 1;
}

static void GenerateLargeCorpus(string root, int files)
{
    Directory.CreateDirectory(root);
    int dirs = Math.Min(256, Math.Max(1, files / 400));
    for (int d = 0; d < dirs; d++) Directory.CreateDirectory(Path.Combine(root, $"bucket-{d:D3}"));
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < files; i++)
    {
        string dir = Path.Combine(root, $"bucket-{i % dirs:D3}");
        File.WriteAllText(Path.Combine(dir, $"perf-{i:D7}.txt"), $"性能语料 {i}\n内容行，用于文件量模拟。\n");
    }
    Console.WriteLine($"[INFO] 大语料生成 {files} 文件 耗时 {sw.Elapsed.TotalSeconds:F1}s");
}

// ---- 管道调用（帧头 12B，与 App PipeClient 同构；单次调用 30s 超时防挂死盲等） ----
// Search 流式调用（0.1.0.14 修复用例自身两处缺陷，与壳层 PipeClient 同口径）：
// ①请求只发一次，连续读帧直到 IsFinal——只读首批会让服务端剩余批次写满管道楔死泵（首轮 gate9 实证）；
// ②禁止每批重发请求——服务端按请求流式回全部批次，重发会让批队列滚雪球、批流永久错位
//   （次轮 gate9 实证：readme 显示命中 100000 条=错位读入 perf-00 的遗留批次）。
async Task<ResultBatch> CallSearchAsync(QuerySpec spec, CancellationToken ct)
{
    using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    callCts.CancelAfter(TimeSpan.FromSeconds(120)); // 整流超时：万级命中多批流式回传
    var token = callCts.Token;

    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(spec, (Type)typeof(QuerySpec));
    var frame = new byte[12 + payload.Length];
    BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
    BitConverter.GetBytes(ProtocolVersion).CopyTo(frame, 4);
    BitConverter.GetBytes((ushort)1).CopyTo(frame, 6); // IpcMethod.Search
    BitConverter.GetBytes(_seq++).CopyTo(frame, 8);
    payload.CopyTo(frame, 12);
    await pipe.WriteAsync(frame, token);
    await pipe.FlushAsync(token);

    var merged = new List<SearchHit>();
    long total = 0;
    var header = new byte[12];
    while (true)
    {
        await ReadExactlyAsync(header, token);
        int respLen = BitConverter.ToInt32(header, 0);
        var respPayload = new byte[respLen];
        await ReadExactlyAsync(respPayload, token);
        var batch = JsonSerializer.Deserialize<ResultBatch>(respPayload)
            ?? throw new InvalidDataException("空批响应");
        merged.AddRange(batch.Hits);
        total = batch.TotalEstimate;
        if (batch.IsFinal) break;
    }
    return new ResultBatch { Hits = merged, TotalEstimate = total, IsFinal = true };
}

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
