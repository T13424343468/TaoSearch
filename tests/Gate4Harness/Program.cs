using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;

// 关4「复杂场景」实弹装置（2026-11-22）：
//   run --report <txt> --baseline <json> [--timeout-sec 240]
//                并发多客户端查询 / 查询与暂停恢复重建并发 / 订阅退订往返 / 重复查询一致性 → 落重启基线
//   verify-restart --baseline <json> --report <txt>
//                服务重启后按基线核对：FileCount 一致 + 基线查询命中一致（快照恢复闭环）
// 严格项失败退出码 1；报告落盘供流水线取证。
// 协议：帧头 12B（LE32 载荷长度 / LE16 版本 / LE16 方法号 / LE32 序号）+ JSON 载荷（契约 v1）。

const string PipeName = "taosearch.svc.1";

if (args.Length == 0) { Console.Error.WriteLine("用法: run --report <txt> --baseline <json> | verify-restart --baseline <json> --report <txt>"); return 2; }

if (args[0] == "run")
{
    string? reportPath = null, baselinePath = null; int timeoutSec = 240;
    for (int i = 1; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--report": reportPath = args[i + 1]; break;
            case "--baseline": baselinePath = args[i + 1]; break;
            case "--timeout-sec" when int.TryParse(args[i + 1], out var t): timeoutSec = t; break;
        }
    }
    if (reportPath is null || baselinePath is null) { Console.Error.WriteLine("run 需要 --report 与 --baseline"); return 2; }
    return await RunAsync(reportPath, baselinePath, timeoutSec);
}

if (args[0] == "verify-restart")
{
    string? reportPath = null, baselinePath = null;
    for (int i = 1; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--report": reportPath = args[i + 1]; break;
            case "--baseline": baselinePath = args[i + 1]; break;
        }
    }
    if (reportPath is null || baselinePath is null) { Console.Error.WriteLine("verify-restart 需要 --report 与 --baseline"); return 2; }
    return await VerifyRestartAsync(baselinePath, reportPath);
}

Console.Error.WriteLine($"未知命令: {args[0]}");
return 2;

// ---- 关4 主场景 ----
async Task<int> RunAsync(string reportPath, string baselinePath, int timeoutSec)
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

    var main = await ConnectAsync(lines);
    if (main is null) { File.WriteAllLines(reportPath, lines); return 1; }

    // 等索引就绪（文件名就绪即开跑；内容就绪与否按实际判，不阻塞）
    var sw = Stopwatch.StartNew();
    IndexStatus ready = await PollReadyAsync(main, timeoutSec, lines);
    Check("G4-00", $"索引就绪 FileCount={ready.FileCount} FilenameReady={ready.FilenameIndexReady}", ready.FilenameIndexReady && ready.FileCount > 0);

    // G4-01 并发多客户端：3 连接 × 6 查询并发，全部成功
    var clients = new List<PipeClient> { main };
    for (int i = 0; i < 2; i++) { var c = await ConnectOnceAsync(lines); if (c is null) break; clients.Add(c); }
    Check("G4-01a", $"并发客户端建立 {clients.Count}/3", clients.Count == 3, strict: false);

    var queries = new[] { "发票", "report", "content-0", "暗号词0042", "readme", "深目录" };
    var concurrentTasks = clients.Select(async (client, ci) =>
    {
        int okCount = 0;
        for (int i = 0; i < queries.Length; i++)
        {
            var spec = new QuerySpec { Text = queries[(i + ci) % queries.Length], BatchSize = 100 };
            var batch = await client.CallAsync<ResultBatch>((ushort)IpcMethod.Search, spec, CancellationToken.None);
            if (batch.Hits is not null) okCount++;
        }
        return okCount;
    }).ToList();
    var concurrentResults = await Task.WhenAll(concurrentTasks);
    Check("G4-01", $"并发 3 客户端 × 6 查询全部成功（{string.Join("/", concurrentResults)}）", concurrentResults.All(n => n == queries.Length));

    // G4-02 查询与暂停/恢复/重建并发：后台查询负载下走暂停→恢复→重建，服务全程应答
    var loadCts = new CancellationTokenSource();
    int loadOk = 0, loadFail = 0;
    var loadTask = Task.Run(async () =>
    {
        var loadClient = clients[1];
        while (!loadCts.IsCancellationRequested)
        {
            try
            {
                var batch = await loadClient.CallAsync<ResultBatch>((ushort)IpcMethod.Search,
                    new QuerySpec { Text = "readme", BatchSize = 50 }, loadCts.Token);
                if (batch.Hits is not null) loadOk++; else loadFail++;
            }
            catch (OperationCanceledException) when (loadCts.IsCancellationRequested) { break; }
            catch { loadFail++; }
        }
    });
    await Task.Delay(1500);

    var pauseAck = await clients[0].CallAsync<AckResponse>((ushort)IpcMethod.PauseIndexing, null, CancellationToken.None);
    Check("G4-02a", $"负载下暂停索引 Ack Ok={pauseAck.Ok}", pauseAck.Ok);
    var resumeAck = await clients[0].CallAsync<AckResponse>((ushort)IpcMethod.ResumeIndexing, null, CancellationToken.None);
    Check("G4-02b", $"负载下恢复索引 Ack Ok={resumeAck.Ok}", resumeAck.Ok);
    var rebuildAck = await clients[0].CallAsync<AckResponse>((ushort)IpcMethod.RebuildIndex,
        new RebuildRequest { Scope = QueryScope.Filename | QueryScope.Content }, CancellationToken.None);
    Check("G4-02c", $"负载下重建索引 Ack Ok={rebuildAck.Ok}", rebuildAck.Ok);
    loadCts.Cancel();
    try { await loadTask; } catch { }
    Check("G4-02d", $"负载查询成功 {loadOk} 次 / 失败 {loadFail} 次（失败=0）", loadFail == 0 && loadOk > 0);

    // G4-03 订阅/退订往返（事件推送依赖 USN，云端卷不可用；此处验证契约往返与订阅号）
    var subAck = await clients[0].CallAsync<SubscribeAck>((ushort)IpcMethod.SubscribeEvents,
        new SubscribeRequest { Filter = new FileEventFilter() }, CancellationToken.None);
    Check("G4-03a", $"订阅返回有效订阅号 {subAck.SubscriptionId}", subAck.SubscriptionId != Guid.Empty);
    var unsubAck = await clients[0].CallAsync<AckResponse>((ushort)IpcMethod.UnsubscribeEvents,
        new UnsubscribeRequest { SubscriptionId = subAck.SubscriptionId }, CancellationToken.None);
    Check("G4-03b", $"退订 Ack Ok={unsubAck.Ok}", unsubAck.Ok);

    // G4-04 重复查询一致性：同一查询 5 次命中数一致
    var counts = new List<int>();
    for (int i = 0; i < 5; i++)
    {
        var batch = await clients[0].CallAsync<ResultBatch>((ushort)IpcMethod.Search,
            new QuerySpec { Text = "暗号词0042", BatchSize = 100 }, CancellationToken.None);
        counts.Add(batch.Hits.Count);
    }
    Check("G4-04", $"重复查询 5 次命中一致（{string.Join(",", counts)}）", counts.All(c => c == counts[0]));

    // 落重启基线：FileCount + 基线查询命中数
    var status = await clients[0].CallAsync<IndexStatus>((ushort)IpcMethod.GetIndexStatus, null, CancellationToken.None);
    var baseBatch = await clients[0].CallAsync<ResultBatch>((ushort)IpcMethod.Search,
        new QuerySpec { Text = "暗号词0042", BatchSize = 100 }, CancellationToken.None);
    var baseline = new RestartBaseline
    {
        FileCount = status.FileCount,
        QueryText = "暗号词0042",
        HitCount = baseBatch.Hits.Count,
    };
    await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(baseline));
    lines.Add($"[INFO] 重启基线: FileCount={baseline.FileCount} Query={baseline.QueryText} Hits={baseline.HitCount}");
    Console.WriteLine($"[INFO] 重启基线: FileCount={baseline.FileCount} Hits={baseline.HitCount}");

    foreach (var c in clients) await c.DisposeAsync();
    File.WriteAllLines(reportPath, lines);
    Console.WriteLine($"== 关4 结果: 严格失败 {strictFails} 项，报告 {reportPath} ==");
    return strictFails == 0 ? 0 : 1;
}

// ---- 重启恢复核对 ----
async Task<int> VerifyRestartAsync(string baselinePath, string reportPath)
{
    var lines = new List<string>();
    int strictFails = 0;
    void Check(string id, string name, bool ok)
    {
        lines.Add($"[{(ok ? "PASS" : "FAIL")}] {id} {name}");
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {id} {name}");
        if (!ok) strictFails++;
    }

    var baseline = JsonSerializer.Deserialize<RestartBaseline>(File.ReadAllBytes(baselinePath))
        ?? throw new InvalidDataException("基线文件解析失败");

    var client = await ConnectAsync(lines);
    if (client is null) { File.WriteAllLines(reportPath, lines); return 1; }

    var ready = await PollReadyAsync(client, 240, lines);
    Check("G4-R1", $"重启后索引就绪 FileCount={ready.FileCount}", ready.FilenameIndexReady && ready.FileCount > 0);
    Check("G4-R2", $"重启后 FileCount={ready.FileCount} 与基线 {baseline.FileCount} 一致", ready.FileCount == baseline.FileCount);

    var batch = await client.CallAsync<ResultBatch>((ushort)IpcMethod.Search,
        new QuerySpec { Text = baseline.QueryText, BatchSize = 100 }, CancellationToken.None);
    Check("G4-R3", $"基线查询「{baseline.QueryText}」重启后命中 {batch.Hits.Count} 与基线 {baseline.HitCount} 一致",
        batch.Hits.Count == baseline.HitCount);

    await client.DisposeAsync();
    File.WriteAllLines(reportPath, lines);
    Console.WriteLine($"== 关4 重启核对: 严格失败 {strictFails} 项 ==");
    return strictFails == 0 ? 0 : 1;
}

// ---- 连接与就绪轮询 ----
async Task<PipeClient?> ConnectAsync(List<string> lines)
{
    var sw = Stopwatch.StartNew();
    int attempts = 0;
    while (sw.Elapsed.TotalSeconds < 90)
    {
        attempts++;
        var c = await ConnectOnceAsync(lines);
        if (c is not null)
        {
            lines.Add($"[INFO] 管道已连接（尝试 {attempts} 次，{sw.Elapsed.TotalSeconds:F1}s）");
            return c;
        }
        await Task.Delay(500);
    }
    lines.Add("[FAIL] 90s 内未能连接服务管道");
    return null;
}

async Task<PipeClient?> ConnectOnceAsync(List<string> lines)
{
    try
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        return new PipeClient(pipe);
    }
    catch (Exception ex)
    {
        lines.Add($"[INFO] 连接未就绪: {ex.GetType().Name}");
        return null;
    }
}

async Task<IndexStatus> PollReadyAsync(PipeClient client, int timeoutSec, List<string> lines)
{
    var sw = Stopwatch.StartNew();
    IndexStatus status = await client.CallAsync<IndexStatus>((ushort)IpcMethod.GetIndexStatus, null, CancellationToken.None);
    while (sw.Elapsed.TotalSeconds < timeoutSec)
    {
        status = await client.CallAsync<IndexStatus>((ushort)IpcMethod.GetIndexStatus, null, CancellationToken.None);
        if (status.FilenameIndexReady && status.FileCount > 0) break;
        await Task.Delay(1000);
    }
    return status;
}

// ---- 管道客户端（帧头 12B；单次调用 30s 超时防挂死盲等） ----
sealed class PipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private uint _seq;

    public PipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public async Task<TResp> CallAsync<TResp>(ushort method, object? request, CancellationToken ct) where TResp : class
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
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0); // 契约 §3.4：载荷长度
            BitConverter.GetBytes(IpcProtocol.Version).CopyTo(frame, 4);
            BitConverter.GetBytes(method).CopyTo(frame, 6);
            BitConverter.GetBytes(_seq++).CopyTo(frame, 8);
            payload.CopyTo(frame, 12);

            await _pipe.WriteAsync(frame, token);
            await _pipe.FlushAsync(token);

            var header = new byte[12];
            await ReadExactlyAsync(header, token);
            int respLen = BitConverter.ToInt32(header, 0);
            var respPayload = new byte[respLen];
            await ReadExactlyAsync(respPayload, token);
            return JsonSerializer.Deserialize<TResp>(respPayload)
                ?? throw new InvalidDataException($"方法 {method} 空响应");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"方法 {method} 单次调用 30s 超时");
        }
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await _pipe.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) throw new IOException("管道已关闭");
            total += n;
        }
    }

    public ValueTask DisposeAsync()
    {
        _pipe.Dispose();
        return ValueTask.CompletedTask;
    }
}

// ---- 重启基线模型 ----
class RestartBaseline
{
    public long FileCount { get; set; }
    public string QueryText { get; set; } = "";
    public int HitCount { get; set; }
}
