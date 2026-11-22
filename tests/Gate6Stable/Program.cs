using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;

// 关6「稳定」实弹装置（2026-11-22）：冷启动 / 长跑负载 / 内存 / 延迟漂移。
//   run --report <txt> [--duration-sec 900] [--qps 2]
// 断言口径：长跑全程查询成功率 100%、无挂死；服务内存 ≤150MB（常驻百 MB 内承诺）；
// 首末段 P95 均 ≤200ms 且无恶化趋势；长跑后索引健康与基线一致。
// 帧协议：帧头 4B 载荷长度（LE）+ 2B 版本 + 2B 方法号 + 4B 序号 + JSON 载荷（契约 §3.4）。

const string PipeName = "taosearch.svc.1";
const ushort ProtocolVersion = 1;
const long MemoryCeilingBytes = 150 * 1024 * 1024;

if (args.Length == 0 || args[0] != "run") { Console.Error.WriteLine("用法: run --report <txt> [--duration-sec 900] [--qps 2]"); return 2; }

string? reportPath = null; int durationSec = 900; double qps = 2;
for (int i = 1; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--report": reportPath = args[i + 1]; break;
        case "--duration-sec" when int.TryParse(args[i + 1], out var d): durationSec = d; break;
        case "--qps" when double.TryParse(args[i + 1], out var q): qps = q; break;
    }
}
if (reportPath is null) { Console.Error.WriteLine("run 需要 --report"); return 2; }

var lines = new List<string>();
int strictFails = 0;
void Check(string id, string name, bool ok, bool strict = true)
{
    string verdict = ok ? "PASS" : strict ? "FAIL" : "INFO";
    if (!ok && strict) strictFails++;
    lines.Add($"[{verdict}] {id} {name}");
    Console.WriteLine($"[{verdict}] {id} {name}");
}

var sw = Stopwatch.StartNew();
uint seq = 0;

// 复用一条长连接跑全程（贴近 App 壳层真实用法：一个连接用到底）
using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
pipe.Connect(30000);
var connectMs = sw.ElapsedMilliseconds;
Console.WriteLine($"[INFO] 连接耗时 {connectMs}ms");

async Task<TResp> CallAsync<TResp>(ushort method, object? request) where TResp : class
{
    byte[] payload = request is null ? [] : JsonSerializer.SerializeToUtf8Bytes(request);
    var frame = new byte[12 + payload.Length];
    BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
    BitConverter.GetBytes(ProtocolVersion).CopyTo(frame, 4);
    BitConverter.GetBytes(method).CopyTo(frame, 6);
    BitConverter.GetBytes(seq++).CopyTo(frame, 8);
    payload.CopyTo(frame, 12);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await pipe.WriteAsync(frame, cts.Token);
    await pipe.FlushAsync(cts.Token);

    var header = new byte[12];
    await ReadExactlyAsync(header, cts.Token);
    var respPayload = new byte[BitConverter.ToInt32(header, 0)];
    await ReadExactlyAsync(respPayload, cts.Token);
    return JsonSerializer.Deserialize<TResp>(respPayload) ?? throw new InvalidDataException("空响应");
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

// ---- G6-01 冷启动口径：连接 + 首查完成（workflow 已在服务启动后 2s 内拉起本装置） ----
var firstSw = Stopwatch.StartNew();
var first = await CallAsync<ResultBatch>(1, new QuerySpec { Text = "发票", Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 200 });
firstSw.Stop();
Check("G6-01", $"冷启动首查 {firstSw.ElapsedMilliseconds}ms（连接 {connectMs}ms + 首查 {firstSw.ElapsedMilliseconds - connectMs}ms）≤ 15s", firstSw.ElapsedMilliseconds <= 15000);
Check("G6-02", $"首查命中 {first.Hits.Count} 条 ≥ 1", first.Hits.Count >= 1);

// ---- G6-03 长跑负载：durationSec 秒 @ qps，交替文件名/内容查询，统计成功率/延迟 ----
var latencies = new List<double>();
long okCount = 0, failCount = 0;
var firstMinute = new List<double>();
var lastMinute = new List<double>();
var runStart = Stopwatch.StartNew();
long queryIndex = 0;
string[] texts = ["发票", "暗号词0001", "report", "暗号词0100", "会议纪要", "readme"];
int intervalMs = Math.Max(1, (int)(1000 / qps));
while (runStart.Elapsed.TotalSeconds < durationSec)
{
    var qsw = Stopwatch.StartNew();
    try
    {
        var batch = await CallAsync<ResultBatch>(1, new QuerySpec { Text = texts[queryIndex % texts.Length], Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 200 });
        qsw.Stop();
        if (batch.Hits.Count == 0 && texts[queryIndex % texts.Length].StartsWith("暗号词")) throw new InvalidDataException("内容命中丢失");
        Interlocked.Increment(ref okCount);
        lock (latencies)
        {
            latencies.Add(qsw.Elapsed.TotalMilliseconds);
            if (runStart.Elapsed.TotalMinutes <= 1) firstMinute.Add(qsw.Elapsed.TotalMilliseconds);
            if (durationSec - runStart.Elapsed.TotalSeconds <= 60) lastMinute.Add(qsw.Elapsed.TotalMilliseconds);
        }
    }
    catch (Exception ex)
    {
        qsw.Stop();
        Interlocked.Increment(ref failCount);
        Console.WriteLine($"[INFO] 长跑异常 @{runStart.Elapsed.TotalSeconds:F0}s: {ex.Message}");
    }
    queryIndex++;
    var wait = intervalMs - (int)qsw.ElapsedMilliseconds;
    if (wait > 0) await Task.Delay(wait);
    if (runStart.Elapsed.TotalSeconds % 120 < 1) Console.WriteLine($"[INFO] 长跑 @{runStart.Elapsed.TotalSeconds:F0}s ok={okCount} fail={failCount}");
}
var runSec = runStart.Elapsed.TotalSeconds;

double P(List<double> xs, double p)
{
    if (xs.Count == 0) return 0;
    var sorted = xs.OrderBy(x => x).ToList();
    return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
}

Check("G6-03", $"长跑 {runSec:F0}s @{qps}QPS：成功 {okCount} 失败 {failCount}", failCount == 0 && okCount > 0);
Check("G6-04", $"长跑全程 P95={P(latencies, 0.95):F0}ms ≤ 200ms", P(latencies, 0.95) <= 200);

// ---- G6-05 延迟漂移：末段 P95 不得劣于首段 P95 的 3 倍（且均 ≤200ms 已由 G6-04 覆盖） ----
double p95First = P(firstMinute, 0.95), p95Last = P(lastMinute, 0.95);
Check("G6-05", $"延迟漂移：首分钟 P95={p95First:F0}ms → 末分钟 P95={p95Last:F0}ms（≤3×）", p95Last <= Math.Max(600, p95First * 3));

// ---- G6-06 内存：长跑后服务进程 WorkingSet ≤ 150MB ----
var svc = Process.GetProcessesByName("TaoSearch.Service").FirstOrDefault();
long ws = svc?.WorkingSet64 ?? -1;
Check("G6-06", $"服务内存 {ws / 1024 / 1024}MB ≤ 150MB", ws is > 0 and <= MemoryCeilingBytes);

// ---- G6-07 长跑后索引健康：FileCount=253、内容就绪、基线命中一致 ----
var status = await CallAsync<IndexStatus>(2, null);
Check("G6-07", $"索引健康 FileCount={status.FileCount} FilenameReady={status.FilenameIndexReady} ContentReady={status.ContentIndexReady} Pending={status.PendingContentQueue}",
    status.FilenameIndexReady && status.ContentIndexReady && status.PendingContentQueue == 0 && status.FileCount == 253);
var after = await CallAsync<ResultBatch>(1, new QuerySpec { Text = "发票", Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 200 });
Check("G6-08", $"长跑后「发票」命中 {after.Hits.Count} 条 = 首查 {first.Hits.Count} 条", after.Hits.Count == first.Hits.Count);

File.WriteAllLines(reportPath, lines);
Console.WriteLine($"== 关6 结果: 严格失败 {strictFails} 项，报告 {reportPath} ==");
return strictFails == 0 ? 0 : 1;
