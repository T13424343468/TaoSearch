using System.Diagnostics;
using System.Text;
using TaoSearch.Kernel.FsIndex;
using Xunit;
using Xunit.Abstractions;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// M2 真实卷基准（基准与量化口径 §1/§4）：真实扫 C: 卷 MFT 全量枚举 + 20 条查询集 P50/P95。
/// 仅 Windows 环境执行（GitHub windows-latest runner 为管理员，走 MFT 快路径）；
/// 非 Windows 环境如实跳过并留痕，不伪造数据。扫描降级慢路径时在证据中如实标注。
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class MftBenchmarkTests
{
    private const int QueryRuns = 30;   // 口径 §1.2：预热 1 次后连测 30 次
    private readonly ITestOutputHelper _output;

    public MftBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RealVolume_ColdScan_And_QueryPercentiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("SKIPPED: 非 Windows 环境不跑真实卷基准（沙箱 Linux 无 NTFS/MFT）。");
            return;
        }

        var evidence = new StringBuilder();
        void Log(string line)
        {
            _output.WriteLine(line);
            evidence.AppendLine(line);
        }

        Log($"# TaoSearch M2 真实卷基准（云端 runner 实测）");
        Log($"- 时间：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        Log($"- 机器：{Environment.MachineName} / {Environment.OSVersion.VersionString}");
        Log($"- 处理器：{Environment.ProcessorCount} vCPU / .NET {Environment.Version}");

        // ---- T0→T2：冷启动首扫（口径 §4.1；T1 边建边查为后续优化项，当前单段构建 T1=T2） ----
        using var engine = new FsIndexEngine();
        var sw = Stopwatch.StartNew();          // T0
        var build = await engine.BuildAsync(@"C:\");
        sw.Stop();                              // T2

        var status = engine.Snapshot();
        Log($"- 首扫模式：{(build.UsedFallback ? "慢路径（目录递归降级，非 MFT）" : "MFT 全量枚举")}");
        if (build.UsedFallback) Log($"- 降级原因：{status.LastError}");
        Log($"- 文件总数：{build.FileCount:N0}");
        Log($"- T0→T2 首扫耗时：{sw.ElapsedMilliseconds:N0} ms（目标：100 万文件 ≤60000 ms）");
        Log($"- 索引非托管内存账：{status.AllocatedBytes / 1024.0 / 1024.0:F1} MB");

        var proc = Process.GetCurrentProcess();
        Log($"- 进程私有内存：{proc.PrivateMemorySize64 / 1024.0 / 1024.0:F1} MB / 工作集：{proc.WorkingSet64 / 1024.0 / 1024.0:F1} MB");

        // ---- 20 条查询集 P50/P95（口径 §1.2：预热 1 次 + 连测 30 次） ----
        string[] queries = LoadQueries();
        Log($"- 查询集：{queries.Length} 条 × 预热 1 次 + {QueryRuns} 次");
        Log("");
        Log("| # | 查询 | 命中数 | P50 (ms) | P95 (ms) |");
        Log($"|---|---|---|---|---|");

        int i = 0;
        foreach (var query in queries)
        {
            i++;
            engine.Search(query);   // 预热 1 次

            var times = new double[QueryRuns];
            long hits = 0;
            for (int run = 0; run < QueryRuns; run++)
            {
                var qsw = Stopwatch.StartNew();
                hits = engine.Search(query).Count();
                qsw.Stop();
                times[run] = qsw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            double p50 = times[QueryRuns / 2];
            double p95 = times[(int)Math.Ceiling(QueryRuns * 0.95) - 1];
            Log($"| {i} | `{query}` | {hits:N0} | {p50:F3} | {p95:F3} |");
        }

        // ---- 证据落盘（随 artifact 上传；目录取环境变量，缺省当前目录） ----
        string outDir = Environment.GetEnvironmentVariable("TAOSEARCH_BENCH_OUT") is { Length: > 0 } d
            ? d
            : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, "benchmark-evidence.md");
        await File.WriteAllTextAsync(path, evidence.ToString(), Encoding.UTF8);
        _output.WriteLine($"证据已写入：{path}");
    }

    private static string[] LoadQueries()
    {
        // 从仓库根 bench/queries.txt 读取（沿测试基目录向上找）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "bench", "queries.txt")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("未找到 bench/queries.txt（从测试基目录向上未达仓库根）");

        return File.ReadAllLines(Path.Combine(dir.FullName, "bench", "queries.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();
    }
}
