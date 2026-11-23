using TaoSearch.App.Core;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>SearchGateway 五方法全链路（真实管道回环）+ ServiceSupervisor 等待环与取消路径。</summary>
public class SearchGatewayMethodsTests
{
    private static byte[] Json(AckResponse ack) =>
        JsonSerializer.SerializeToUtf8Bytes(ack, AppJsonContext.Default.AckResponse);

    private static byte[] JsonStatus(IndexStatus st) =>
        JsonSerializer.SerializeToUtf8Bytes(st, AppJsonContext.Default.IndexStatus);

    [Fact]
    public async Task GetIndexStatusAsync_RoundTrips()
    {
        var st = new IndexStatus
        {
            FileCount = 253, FilenameIndexReady = true, ContentIndexReady = true,
            PendingContentQueue = 3, FailedFiles = [],
        };
        using var server = LoopbackPipeServer.Start(JsonStatus(st));
        using var gw = new SearchGateway();
        var result = await gw.GetIndexStatusAsync();
        Assert.Equal(253, result.FileCount);
    }

    [Fact]
    public async Task RebuildAsync_RoundTrips()
    {
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var gw = new SearchGateway();
        var ack = await gw.RebuildAsync(QueryScope.Filename | QueryScope.Content);
        Assert.True(ack.Ok);
    }

    [Fact]
    public async Task PauseAsync_RoundTrips()
    {
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var gw = new SearchGateway();
        var ack = await gw.PauseAsync();
        Assert.True(ack.Ok);
    }

    [Fact]
    public async Task ResumeAsync_RoundTrips()
    {
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var gw = new SearchGateway();
        var ack = await gw.ResumeAsync();
        Assert.True(ack.Ok);
    }

    [Fact]
    public async Task EnsureRunning_FakeServiceExe_WaitsThenHonoursCancel()
    {
        ResetAttempted();
        var exe = Path.Combine(AppContext.BaseDirectory, "TaoSearch.Service.exe");
        await File.WriteAllTextAsync(exe, "#!/bin/sh\nsleep 30\n");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.UserRead;
            File.SetUnixFileMode(exe, mode);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ServiceSupervisor.EnsureRunningAsync(cts.Token); // 等待环被取消 → OCE 内部吞掉
            Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds}ms");
        }
        finally { File.Delete(exe); }
    }

    private static void ResetAttempted()
    {
        var field = typeof(ServiceSupervisor).GetField("_attempted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, 0);
    }
}

/// <summary>
/// Windows 专属路径（命名内核对象 / explorer 集成）：Linux 沙箱跳过，windows-ci 真环境实跑出账。
/// </summary>
public class WindowsOnlyPathTests
{
    private static bool OnWindows() => OperatingSystem.IsWindows();

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TryAcquire_SecondAcquireReturnsFalse()
    {
        if (!OnWindows()) return; // 云端 Windows 实跑
        Assert.True(SingleInstanceLaunch.TryAcquire());
        Assert.False(SingleInstanceLaunch.TryAcquire());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ForwardToRunningInstance_NoListener_DoesNotThrow()
    {
        if (!OnWindows()) return;
        SingleInstanceLaunch.ForwardToRunningInstance(["--quick"]);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void LaunchArgs_IntegrationWithForward()
    {
        if (!OnWindows()) return;
        var args = new[] { "--search", "年度 报告" };
        Assert.True(LaunchArgs.TryGetSearch(args, out var kw));
        Assert.Equal("年度 报告", kw);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void FileLauncher_RevealInExplorer_NonExistentPath_DoesNotThrow()
    {
        if (!OnWindows()) return;
        // explorer /select 对不存在路径不抛异常、照常返回（windows-ci 真机实测），打开动作本身视为成功
        var ok = FileLauncher.RevealInExplorer(@"Z:\__taosearch_no_such__\none.txt");
        Assert.True(ok);
    }
}
