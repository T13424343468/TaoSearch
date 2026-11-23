using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TaoSearch.App.Core;
using TaoSearch.Kernel.Contracts;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>补齐 50% 分支：事件空侧、管道复用、等待环双出口。</summary>
public class BranchCompletionTests
{
    private static byte[] Json(AckResponse ack) =>
        JsonSerializer.SerializeToUtf8Bytes(ack, AppJsonContext.Default.AckResponse);

    private static byte[] JsonBatch() => JsonSerializer.SerializeToUtf8Bytes(new ResultBatch
    {
        Hits = [new SearchHit
        {
            FileReference = 1, Path = @"D:\a.txt", FileName = "a.txt",
            Size = 1, ModifiedTime = DateTimeOffset.Now,
        }],
        TotalEstimate = 1,
    }, AppJsonContext.Default.ResultBatch);

    [Fact]
    public async Task MainViewModel_NoSubscribers_SettersTakeNullEventSide()
    {
        // 无任何订阅者 → PropertyChanged 为 null → 走 ?. 的空侧
        var gw = new FakeGateway { SearchBatch = new ResultBatch { Hits = [], TotalEstimate = 0 } };
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Poll.UntilAsync(() => vm.StatusText.Contains("未找到"));
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task SettingsViewModel_NoSubscribers_RefreshTakesNullEventSide()
    {
        var gw = new FakeGateway();
        var vm = new SettingsViewModel(gw);
        await vm.RefreshAsync();
        Assert.Equal("就绪", vm.IndexState);
    }

    [Fact]
    public async Task PipeClient_ReusesConnection_OnSecondCall()
    {
        using var server = LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        using var client = new PipeClient();
        var first = await client.CallAsync<object, AckResponse>(IpcMethod.PauseIndexing, null);
        var second = await client.CallAsync<object, AckResponse>(IpcMethod.ResumeIndexing, null);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
    }

    [Fact]
    public async Task EnsureRunning_ServiceComesUpMidWait_ReturnsViaLoop()
    {
        ResetAttempted();
        var exe = Path.Combine(AppContext.BaseDirectory, "TaoSearch.Service.exe");
        if (OperatingSystem.IsWindows())
        {
            // Windows：占位服务用真实 PE（hostname 副本，秒退）——非 PE 文件 Process.Start 抛 Win32Exception 被吞，走不到等待环（windows-ci 真机实测）
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), exe, overwrite: true);
        }
        else
        {
            await File.WriteAllTextAsync(exe, "#!/bin/sh\nsleep 30\n");
            var mode = File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.UserRead;
            File.SetUnixFileMode(exe, mode);
        }
        // 1.5 秒后服务管道上线 → 等待环内 PipeAliveAsync 转真 → 循环内 return
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            await Task.Delay(1500, cts.Token);
            return LoopbackPipeServer.Start(Json(new AckResponse { Ok = true }));
        });
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ServiceSupervisor.EnsureRunningAsync(cts.Token);
            Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            File.Delete(exe);
            var srv = await serverTask;
            srv.Dispose();
        }
    }

    [Fact]
    public async Task EnsureRunning_ServiceNeverComesUp_ExitsViaDeadline()
    {
        ResetAttempted();
        var exe = Path.Combine(AppContext.BaseDirectory, "TaoSearch.Service.exe");
        if (OperatingSystem.IsWindows())
        {
            // Windows：占位服务用真实 PE（hostname 副本，秒退）——非 PE 文件 Process.Start 抛 Win32Exception 被吞，走不到等待环（windows-ci 真机实测）
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), exe, overwrite: true);
        }
        else
        {
            await File.WriteAllTextAsync(exe, "#!/bin/sh\nsleep 30\n");
            var mode = File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.UserRead;
            File.SetUnixFileMode(exe, mode);
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ServiceSupervisor.EnsureRunningAsync(); // 无取消 → 等 15 秒死线自然到期
            Assert.True(sw.ElapsedMilliseconds >= 14_000, $"deadline exit expected, took {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            // 兜底：清掉可能残留的占位进程再删文件（Windows 上运行中的 exe 文件被锁）
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("TaoSearch.Service"))
            {
                try { proc.Kill(); } catch { /* 已退出 */ }
            }
            File.Delete(exe);
        }
    }

    private static void ResetAttempted()
    {
        var field = typeof(ServiceSupervisor).GetField("_attempted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, 0);
    }
}
