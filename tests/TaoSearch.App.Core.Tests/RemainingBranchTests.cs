using TaoSearch.App.Core;
using TaoSearch.Kernel.Contracts;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>补剩余分支：搜索取消静默、位置属性通知、服务监督异常吞并。</summary>
public class RemainingBranchTests
{
    [Fact]
    public async Task Search_CancelledByGateway_IsSilent()
    {
        var gw = new CancelledGateway();
        var vm = new MainViewModel(gw) { Query = "abc" };
        await Task.Delay(600);
        Assert.DoesNotContain("搜索出错", vm.StatusText);
    }

    [Fact]
    public void IndexLocation_Setter_RaisesNotification()
    {
        var vm = new SettingsViewModel(new FakeGateway());
        var raised = "";
        vm.PropertyChanged += (_, e) => raised = e.PropertyName;
        vm.IndexLocation = @"E:\New";
        Assert.Equal("IndexLocation", raised);
        Assert.Equal(@"E:\New", vm.IndexLocation);
    }

    [Fact]
    public async Task EnsureRunning_NonExecutableService_FailureSwallowed()
    {
        ResetAttempted();
        // 有文件但不可执行 → Process.Start 抛异常 → catch(Exception) 吞掉不阻断
        var exe = Path.Combine(AppContext.BaseDirectory, "TaoSearch.Service.exe");
        await File.WriteAllTextAsync(exe, "not an executable");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ServiceSupervisor.EnsureRunningAsync();
            Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
        }
        finally { File.Delete(exe); }
    }

    private static void ResetAttempted()
    {
        var field = typeof(ServiceSupervisor).GetField("_attempted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field!.SetValue(null, 0);
    }

    private sealed class CancelledGateway : ISearchGateway
    {
        public Task<ResultBatch> SearchAsync(string text, QueryScope scope, int batchSize, CancellationToken ct = default)
            => throw new OperationCanceledException();

        public Task<IndexStatus> GetIndexStatusAsync(CancellationToken ct = default) => throw new OperationCanceledException();
        public Task<TaoSearch.Kernel.Contracts.AckResponse> RebuildAsync(TaoSearch.Kernel.Contracts.QueryScope scope, CancellationToken ct = default) => throw new OperationCanceledException();
        public Task<TaoSearch.Kernel.Contracts.AckResponse> PauseAsync(CancellationToken ct = default) => throw new OperationCanceledException();
        public Task<TaoSearch.Kernel.Contracts.AckResponse> ResumeAsync(CancellationToken ct = default) => throw new OperationCanceledException();
        public void Dispose() { }
    }
}
