using TaoSearch.App.Core;
using TaoSearch.Kernel.Contracts;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>SettingsViewModel 全分支：刷新成功/失败、重建三路、暂停恢复四路、错误转译。</summary>
public class SettingsViewModelTests
{
    [Fact]
    public async Task Refresh_Success_PopulatesFields()
    {
        var gw = new FakeGateway
        {
            Status = new IndexStatus
            {
                FileCount = 253, FilenameIndexReady = true, ContentIndexReady = true,
                PendingContentQueue = 7, FailedFiles = [],
            },
        };
        var vm = new SettingsViewModel(gw);
        vm.PropertyChanged += (_, _) => { }; // 订阅者在场 → 覆盖事件非空侧
        await vm.RefreshAsync();
        Assert.Equal("253", vm.FileCount.Replace(",", "").Replace(" ", "").Replace("\u00a0", ""));
        Assert.Equal("就绪", vm.IndexState);
        Assert.Equal("7", vm.PendingQueue.Replace(",", "").Replace(" ", "").Replace("\u00a0", ""));
    }

    [Fact]
    public async Task Refresh_IndexNotReady_ShowsBuilding()
    {
        var gw = new FakeGateway
        {
            Status = new IndexStatus
            {
                FileCount = 1, FilenameIndexReady = false, ContentIndexReady = false,
                PendingContentQueue = 0, FailedFiles = [],
            },
        };
        var vm = new SettingsViewModel(gw);
        await vm.RefreshAsync();
        Assert.Equal("建索引中", vm.IndexState);
    }

    [Fact]
    public async Task Refresh_Exception_ShowsChineseNotConnected()
    {
        var gw = new FakeGateway { StatusError = new IOException("timed out") };
        var vm = new SettingsViewModel(gw);
        await vm.RefreshAsync();
        Assert.Equal("服务未连接", vm.IndexState);
        Assert.Equal("—", vm.FileCount);
        Assert.Equal("—", vm.PendingQueue);
    }

    [Fact]
    public async Task Rebuild_Ok_ShowsStarted()
    {
        var gw = new FakeGateway { RebuildAck = new AckResponse { Ok = true } };
        var vm = new SettingsViewModel(gw);
        Assert.Equal("已开始重建索引", await vm.RebuildAsync());
    }

    [Fact]
    public async Task Rebuild_AckFail_ShowsChineseError()
    {
        var gw = new FakeGateway { RebuildAck = AckResponse.Fail("E_BUSY") };
        var vm = new SettingsViewModel(gw);
        var msg = await vm.RebuildAsync();
        Assert.StartsWith("重建失败：", msg);
        Assert.DoesNotContain("E_BUSY", msg);
    }

    [Fact]
    public async Task Rebuild_Exception_ShowsNotConnected()
    {
        var gw = new FakeGateway { RebuildError = new IOException("no pipe") };
        var vm = new SettingsViewModel(gw);
        Assert.Equal("重建失败：索引服务未连接，请确认服务已启动后重试。", await vm.RebuildAsync());
    }

    [Fact]
    public async Task TogglePause_FromRunning_Pauses()
    {
        var gw = new FakeGateway { PauseAck = new AckResponse { Ok = true } };
        var vm = new SettingsViewModel(gw);
        var msg = await vm.TogglePauseAsync();
        Assert.True(vm.Paused);
        Assert.Equal("已暂停索引", msg);
    }

    [Fact]
    public async Task TogglePause_FromPaused_Resumes()
    {
        var gw = new FakeGateway { PauseAck = new AckResponse { Ok = true }, ResumeAck = new AckResponse { Ok = true } };
        var vm = new SettingsViewModel(gw);
        await vm.TogglePauseAsync();
        var msg = await vm.TogglePauseAsync();
        Assert.False(vm.Paused);
        Assert.Equal("已恢复索引", msg);
    }

    [Fact]
    public async Task TogglePause_AckFail_ShowsChineseError_KeepsState()
    {
        var gw = new FakeGateway { PauseAck = AckResponse.Fail("E_DENIED") };
        var vm = new SettingsViewModel(gw);
        var msg = await vm.TogglePauseAsync();
        Assert.False(vm.Paused);
        Assert.StartsWith("操作失败：", msg);
        Assert.DoesNotContain("E_DENIED", msg);
    }

    [Fact]
    public async Task TogglePause_Exception_ShowsNotConnected()
    {
        var gw = new FakeGateway { ToggleError = new IOException("down") };
        var vm = new SettingsViewModel(gw);
        Assert.Equal("操作失败：索引服务未连接，请确认服务已启动后重试。", await vm.TogglePauseAsync());
    }

    [Fact]
    public async Task TogglePause_ResumeFail_ShowsChineseError()
    {
        var gw = new FakeGateway { PauseAck = new AckResponse { Ok = true }, ResumeAck = AckResponse.Fail("E_X") };
        var vm = new SettingsViewModel(gw);
        await vm.TogglePauseAsync();
        var msg = await vm.TogglePauseAsync();
        Assert.True(vm.Paused); // 失败不翻转
        Assert.StartsWith("操作失败：", msg);
    }

    [Fact]
    public async Task TogglePause_WithSubscriber_CoversEventNonNullSide()
    {
        // 订阅者在场时翻转 Paused → 覆盖 PropertyChanged?.Invoke 非空侧
        var gw = new FakeGateway { PauseAck = new AckResponse { Ok = true } };
        var vm = new SettingsViewModel(gw);
        vm.PropertyChanged += (_, _) => { };
        await vm.TogglePauseAsync();
        Assert.True(vm.Paused);
    }

    [Fact]
    public void IndexLocation_Set_BothEventSides()
    {
        // 无订阅者赋值 → 空侧；有订阅者赋值 → 非空侧
        var gw = new FakeGateway();
        var vm = new SettingsViewModel(gw);
        vm.IndexLocation = @"D:\TaoSearch\Index";
        vm.PropertyChanged += (_, _) => { };
        vm.IndexLocation = @"D:\TaoSearch\Index2";
        Assert.Equal(@"D:\TaoSearch\Index2", vm.IndexLocation);
    }

    [Fact]
    public async Task Rebuild_AckFail_NullError_ShowsUnknownError()
    {
        // 应答失败但 Error 为空 → ErrorText 空侧转译为"未知错误"
        var gw = new FakeGateway { RebuildAck = new AckResponse { Ok = false } };
        var vm = new SettingsViewModel(gw);
        Assert.Equal("重建失败：未知错误", await vm.RebuildAsync());
    }
}
