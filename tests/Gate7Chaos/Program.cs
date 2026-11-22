using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;

// 关7「健壮性」实弹装置（2026-11-22）：断盘/满盘/损坏注入/中断恢复。
//   verify --label <名> [--expect-files N]
//     连接服务（60s 窗口）→ 索引状态断言 → 基线查询断言 → 退出码 0/1。
//     损坏注入与进程启停由 workflow pwsh 承担（注入须在服务停止时做），本装置只做「启动后验证」。
// 断言口径：任何损坏注入后服务必须能启动应答（降级重建也算过），进程不崩、查询可用。
// 帧协议：帧头 4B 载荷长度（LE）+ 2B 版本 + 2B 方法号 + 4B 序号 + JSON 载荷（契约 §3.4）。

const string PipeName = "taosearch.svc.1";
const ushort ProtocolVersion = 1;

if (args.Length == 0 || args[0] != "verify") { Console.Error.WriteLine("用法: verify --label <名> [--expect-files N] [--rebuild]"); return 2; }

string label = "未命名"; int? expectFiles = null; bool rebuild = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--label" when i + 1 < args.Length: label = args[++i]; break;
        case "--expect-files" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): expectFiles = n; i++; break;
        case "--rebuild": rebuild = true; break;
    }
}

var lines = new List<string>();
int strictFails = 0;
void Check(string id, string name, bool ok)
{
    string verdict = ok ? "PASS" : "FAIL";
    if (!ok) strictFails++;
    lines.Add($"[{verdict}] {id} {name}");
    Console.WriteLine($"[{verdict}] {id} {name}");
}

var sw = Stopwatch.StartNew();
using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try { pipe.Connect(60000); }
catch (TimeoutException)
{
    Check($"G7[{label}]-conn", $"60s 未连上管道（服务可能崩在启动恢复段）", false);
    File.WriteAllLines($"gate7-{label}.txt", lines);
    return 1;
}
var connectMs = sw.ElapsedMilliseconds;

async Task<TResp> CallAsync<TResp>(ushort method, object? request) where TResp : class
{
    byte[] payload = request is null ? [] : JsonSerializer.SerializeToUtf8Bytes(request);
    var frame = new byte[12 + payload.Length];
    BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
    BitConverter.GetBytes(ProtocolVersion).CopyTo(frame, 4);
    BitConverter.GetBytes(method).CopyTo(frame, 6);
    BitConverter.GetBytes(0u).CopyTo(frame, 8);
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

// 索引就绪等待（重建可能耗时，上限 120s）
if (rebuild)
{
    // 语料消失口径：触发真实重建（服务端先清后建），状态等待循环会等到重建完成
    var ack = await CallAsync<AckResponse>(3, new RebuildRequest { Scope = QueryScope.Filename | QueryScope.Content });
    Check($"G7[{label}]-rebuild", $"重建指令 ACK={ack.Ok}", ack.Ok);
}

IndexStatus status;
var waitSw = Stopwatch.StartNew();
while (true)
{
    status = await CallAsync<IndexStatus>(2, null);
    if (status.FilenameIndexReady && status.PendingContentQueue == 0) break;
    if (waitSw.Elapsed.TotalSeconds > 120) break;
    await Task.Delay(1000);
}

bool filesOk = expectFiles is null ? status.FilenameIndexReady : status.FileCount == expectFiles.Value;
Check($"G7[{label}]-files", $"FileCount={status.FileCount}（期望 {expectFiles?.ToString() ?? "就绪即可"}）", filesOk);

var batch = await CallAsync<ResultBatch>(1, new QuerySpec { Text = "发票", Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 200 });
Check($"G7[{label}]-query", $"基线查询「发票」命中 {batch.Hits.Count} 条 ≥ 1", batch.Hits.Count >= 1);

Console.WriteLine($"== 关7[{label}] 严格失败 {strictFails} 项 ==");
return strictFails == 0 ? 0 : 1;
