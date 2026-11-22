using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;

// 关5「暴力」实弹装置（2026-11-22）：monkey/畸形数据/越权探试。
//   run --report <txt> [--pipe <name>] [--timeout-sec 30]
// 断言口径：畸形输入下服务进程绝不崩、单连接可断但服务必须继续应答新连接；
// 轰炸结束后正常查询命中与基线一致（韧性终验）。
// 帧协议：帧头 4B 载荷长度（LE）+ 2B 版本 + 2B 方法号 + 4B 序号 + JSON 载荷（契约 §3.4）。

const string PipeName = "taosearch.svc.1";
const ushort ProtocolVersion = 1;

if (args.Length == 0 || args[0] != "run") { Console.Error.WriteLine("用法: run --report <txt>"); return 2; }

string? reportPath = null;
for (int i = 1; i + 1 < args.Length; i += 2)
    if (args[i] == "--report") reportPath = args[i + 1];
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

bool ServiceAlive()
{
    var procs = System.Diagnostics.Process.GetProcessesByName("TaoSearch.Service");
    return procs.Length > 0;
}

/// <summary>开一条连接，发原始字节，可选读一帧响应（带超时毫秒）。</summary>
async Task<(NamedPipeClientStream pipe, byte[]? resp, string note)> RawRoundtripAsync(byte[] wire, bool readReply, int timeoutMs = 15000)
{
    var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(10000);
    await pipe.WriteAsync(wire);
    await pipe.FlushAsync();
    byte[]? resp = null;
    string note = "sent";
    if (readReply)
    {
        try
        {
            var header = new byte[12];
            await ReadExactlyAsync(pipe, header, timeoutMs);
            int payloadLen = BitConverter.ToInt32(header, 0);
            var payload = new byte[payloadLen];
            await ReadExactlyAsync(pipe, payload, timeoutMs);
            resp = payload;
            note = $"reply {payloadLen}B";
        }
        catch (Exception ex) { note = $"no-reply ({ex.GetType().Name})"; }
    }
    return (pipe, resp, note);
}

static async Task ReadExactlyAsync(Stream s, byte[] buffer, int timeoutMs)
{
    using var cts = new CancellationTokenSource(timeoutMs);
    int total = 0;
    while (total < buffer.Length)
    {
        int n = await s.ReadAsync(buffer.AsMemory(total), cts.Token);
        if (n == 0) throw new IOException("对端断开");
        total += n;
    }
}

static byte[] FrameBytes(ushort method, uint seq, ReadOnlySpan<byte> payload, ushort version = ProtocolVersion, int? declaredLen = null)
{
    var frame = new byte[12 + payload.Length];
    BitConverter.GetBytes(declaredLen ?? payload.Length).CopyTo(frame, 0);
    BitConverter.GetBytes(version).CopyTo(frame, 4);
    BitConverter.GetBytes(method).CopyTo(frame, 6);
    BitConverter.GetBytes(seq).CopyTo(frame, 8);
    payload.ToArray().CopyTo(frame, 12); // ReadOnlySpan 无双参 CopyTo，转数组
    return frame;
}

// ---- 基线：正常查询命中（韧性终验对照） ----
uint seq = 0;
async Task<ResultBatch?> NormalSearchAsync(string text, int timeoutMs = 30000)
{
    var spec = new QuerySpec { Text = text, Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 200 };
    byte[] wire = FrameBytes(1, seq++, JsonSerializer.SerializeToUtf8Bytes(spec), ProtocolVersion, null);
    var (_, resp, _) = await RawRoundtripAsync(wire, readReply: true, timeoutMs);
    return resp is null ? null : JsonSerializer.Deserialize<ResultBatch>(resp);
}

var baseline = await NormalSearchAsync("发票");
Check("G5-00", $"基线查询「发票」可通（命中 {baseline?.Hits.Count ?? -1} 条）", baseline is not null && baseline.Hits.Count >= 1);

// ---- A. 畸形帧层 ----
// G5-01 帧头声明长度超上限（0xFFFFFFFF）：服务端应断开该连接，进程不崩
{
    var bad = new byte[12];
    BitConverter.GetBytes(unchecked((int)0xFFFFFFFF)).CopyTo(bad, 0);
    BitConverter.GetBytes(ProtocolVersion).CopyTo(bad, 4);
    BitConverter.GetBytes((ushort)1).CopyTo(bad, 6);
    BitConverter.GetBytes(seq++).CopyTo(bad, 8);
    var (_, resp, note) = await RawRoundtripAsync(bad, readReply: true, timeoutMs: 8000);
    Check("G5-01", $"超上限长度帧 → {note}；服务存活={ServiceAlive()}", ServiceAlive());
}

// G5-02 帧头长度=0（空载荷 GetIndexStatus 语义）：服务应正常应答
{
    byte[] wire = FrameBytes(2, seq++, ReadOnlySpan<byte>.Empty);
    var (_, resp, note) = await RawRoundtripAsync(wire, readReply: true);
    Check("G5-02", $"零长度帧 GetIndexStatus → {(resp is null ? note : "应答 " + resp.Length + "B")}", resp is not null && ServiceAlive());
}

// G5-03 声明长度与实际不符（声明 1000B 只发 10B 后断开）：服务端会话应干净结束，进程不崩
{
    byte[] wire = FrameBytes(1, seq++, new byte[10], ProtocolVersion, declaredLen: 1000);
    var (pipe, _, note) = await RawRoundtripAsync(wire, readReply: false);
    pipe.Close();
    await Task.Delay(500);
    Check("G5-03", $"截断帧（声明1000发10后断开）→ {note}；服务存活={ServiceAlive()}", ServiceAlive());
}

// G5-04 未知方法号 0x00FF：应回 AckResponse ok=false，不崩
{
    byte[] wire = FrameBytes(0x00FF, seq++, ReadOnlySpan<byte>.Empty);
    var (_, resp, note) = await RawRoundtripAsync(wire, readReply: true);
    bool acked = resp is not null;
    Check("G5-04", $"未知方法号 0x00FF → {(acked ? "有应答" : note)}", (acked || note.Contains("断开", StringComparison.Ordinal)) && ServiceAlive());
}

// G5-05 版本号错（0x0063）：观察行为（契约未定义版本拒绝），只断言不崩
{
    byte[] wire = FrameBytes(2, seq++, ReadOnlySpan<byte>.Empty, version: 0x0063);
    var (_, resp, note) = await RawRoundtripAsync(wire, readReply: true);
    Check("G5-05", $"错误版本号 99 → {(resp is not null ? "有应答" : note)}；服务存活={ServiceAlive()}", ServiceAlive(), strict: false);
}

// ---- B. 恶意载荷层（正常帧、恶意 JSON） ----
async Task<(byte[]? resp, string note)> SearchRawAsync(string payloadJson, int timeoutMs = 30000)
{
    byte[] wire = FrameBytes(1, seq++, Encoding.UTF8.GetBytes(payloadJson));
    var (_, resp, note) = await RawRoundtripAsync(wire, readReply: true, timeoutMs);
    return (resp, note);
}

// G5-06 非 JSON 载荷：应回 bad payload ack，不崩
{
    var (resp, note) = await SearchRawAsync("这不是JSON{{!");
    Check("G5-06", $"非 JSON 载荷 → {(resp is not null ? "有应答" : note)}", resp is not null && ServiceAlive());
}

// G5-07 空 JSON（QuerySpec 全默认，Text=""）：不崩
{
    var (resp, note) = await SearchRawAsync("{}");
    Check("G5-07", $"空对象载荷 → {(resp is not null ? "有应答" : note)}", resp is not null && ServiceAlive());
}

// G5-08 超长查询文本（1MB）：不崩、限时内应答
{
    string mega = new string('A', 1024 * 1024);
    var (resp, note) = await SearchRawAsync(JsonSerializer.Serialize(new QuerySpec { Text = mega, Scope = QueryScope.Filename, BatchSize = 10 }), 30000);
    Check("G5-08", $"1MB 查询文本 → {(resp is not null ? "有应答" : note)}", resp is not null && ServiceAlive());
}

// G5-09 正则元字符/控制字符/emoji/注入样式：不崩
{
    string[] nasties =
    [
        ".*[](){}$^+?|\\",
        "\u0000\u0001\u0002控制字符",
        "🔍😀🎉",
        "'; DROP TABLE files;--",
        new string('%', 5000),
        "暗号词0001\"\\/*",
    ];
    int okCount = 0;
    foreach (var n in nasties)
    {
        var (resp, note) = await SearchRawAsync(JsonSerializer.Serialize(new QuerySpec { Text = n, Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 50 }));
        if (resp is not null) okCount++;
        else Console.WriteLine($"[INFO]   恶意文本无响应: {note}");
    }
    Check("G5-09", $"恶意文本查询 {okCount}/{nasties.Length} 有应答", okCount == nasties.Length && ServiceAlive());
}

// G5-10 非法 scope 枚举位（0xFFFF）：不崩
{
    byte[] wire = FrameBytes(1, seq++, Encoding.UTF8.GetBytes("{\"Text\":\"发票\",\"Scope\":65535,\"BatchSize\":10}"));
    var (_, resp, note) = await RawRoundtripAsync(wire, readReply: true);
    Check("G5-10", $"非法 Scope=65535 → {(resp is not null ? "有应答" : note)}", resp is not null && ServiceAlive());
}

// G5-11 BatchSize 边界（0 / 负数 / 10亿）：不崩
{
    int okCount = 0;
    foreach (var bs in new[] { 0, -1, 1_000_000_000 })
    {
        byte[] wire = FrameBytes(1, seq++, Encoding.UTF8.GetBytes($"{{\"Text\":\"发票\",\"Scope\":3,\"BatchSize\":{bs}}}"));
        var (_, resp, note) = await RawRoundtripAsync(wire, readReply: true);
        if (resp is not null) okCount++;
        else Console.WriteLine($"[INFO]   BatchSize={bs} 无响应: {note}");
    }
    Check("G5-11", $"BatchSize 边界 {okCount}/3 有应答", okCount == 3 && ServiceAlive());
}

// ---- C. 并发 monkey：4 线程 × 25 次混合畸形/正常请求，全部限时应答，服务存活 ----
{
    int done = 0, hung = 0;
    var rand = new Random(20261122);
    var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        for (int i = 0; i < 25; i++)
        {
            try
            {
                if (rand.Next(2) == 0)
                {
                    var (resp, _) = await SearchRawAsync(JsonSerializer.Serialize(new QuerySpec { Text = "暗号词0001", Scope = QueryScope.Filename | QueryScope.Content, BatchSize = 20 }), 30000);
                    if (resp is null) Interlocked.Increment(ref hung);
                }
                else
                {
                    byte[] wire = FrameBytes(1, seq++, Encoding.UTF8.GetBytes("{\"Text\":\"\\u0000bad\",\"Scope\":3,\"BatchSize\":-5}"));
                    var (_, _, _) = await RawRoundtripAsync(wire, readReply: true, timeoutMs: 30000);
                }
                Interlocked.Increment(ref done);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INFO]   monkey 异常: {ex.GetType().Name} {ex.Message}");
                Interlocked.Increment(ref hung);
            }
        }
    })).ToArray();
    await Task.WhenAll(tasks);
    Check("G5-12", $"并发 monkey 100 请求：完成 {done}，挂死/异常 {hung}", hung == 0 && ServiceAlive());
}

// ---- D. 韧性终验：轰炸后正常查询与基线一致 ----
{
    var after = await NormalSearchAsync("发票");
    bool same = after is not null && baseline is not null && after.Hits.Count == baseline.Hits.Count;
    Check("G5-13", $"轰炸后「发票」命中 {after?.Hits.Count ?? -1} 条 = 基线 {baseline?.Hits.Count ?? -1} 条", same);
    Check("G5-14", "全程服务进程存活", ServiceAlive());
}

File.WriteAllLines(reportPath, lines);
Console.WriteLine($"== 关5 结果: 严格失败 {strictFails} 项，报告 {reportPath} ==");
return strictFails == 0 ? 0 : 1;
