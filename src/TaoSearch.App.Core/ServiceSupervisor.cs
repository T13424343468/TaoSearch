using System.Diagnostics;
using System.IO.Pipes;

namespace TaoSearch.App.Core;

/// <summary>
/// 索引服务守护（真机红灯修复）：安装器只落文件不注册自启，云端测试均手动拉起服务，
/// 真机首次使用时服务从未运行 → 壳层管道连接超时（老板真机实锤）。
/// 壳层在首次 IPC 前探测管道；不可达则从安装目录拉起 TaoSearch.Service.exe 并等待就绪。
/// 进程级重试口径（0.1.0.18 真机实锤整改）：此前进程级只试一次，服务中途被杀后
/// 「服务未连接」永远不恢复。现在每次 IPC 前都探测管道（本地探测毫秒级）；
/// 管道不可达才拉起进程，拉起动作节流（至多每 30s 一次）避免 UAC 弹窗轰炸。
/// </summary>
public static class ServiceSupervisor
{
    private const string PipeName = "taosearch.svc.1";
    private const int StartThrottleMs = 30_000;
    private static int _startInFlight;
    private static long _lastStartTicks;

    /// <summary>确保索引服务在运行：探测管道 → 不可达且距上次拉起超节流窗则再拉 → 轮询等待（至多 15s）。</summary>
    public static async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        try
        {
            if (await PipeAliveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) return;

            // 节流：30s 内不重复拉起（UAC 弹窗不能连发）；并发调用只放一个进去
            var last = Interlocked.Read(ref _lastStartTicks);
            if (DateTime.UtcNow.Ticks - last < TimeSpan.FromMilliseconds(StartThrottleMs).Ticks) return;
            if (Interlocked.CompareExchange(ref _startInFlight, 1, 0) == 1) return;
            try
            {
                await StartServiceAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _lastStartTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _startInFlight, 0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 拉起失败不阻断壳层：后续 IPC 调用按原错误路径提示
        }
    }

    private static async Task StartServiceAsync(CancellationToken ct)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "TaoSearch.Service.exe");
        if (!File.Exists(exe)) return;

            // 管理员优先（真机红灯修复）：MFT 直读需管理员权限，普通权限启动必降级慢路径
            // （真机实锤：几十万文件慢速扫描十分钟不完成）。先以 runas 请求提权（UAC 弹窗），
            // 用户拒绝或非管理员账户再降级普通启动——可用性优先，慢但能用。
            bool started = false;
            try
            {
                using var elevated = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                started = elevated is not null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                started = false; // 用户拒绝 UAC 或无提权通道：降级普通启动
            }

            if (!started)
            {
                using var process = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                started = process is not null;
            }
            if (!started) return;

            // 服务恢复快照路径秒级就绪；首建路径「先听后建」管道先监听，这里只等短窗口，
            // 未就绪交由调用方轮询接续（OnboardingWindow.PollLoopAsync / MainWindow 轮询）
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (await PipeAliveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) return;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
    }

    private static async Task<bool> PipeAliveAsync(TimeSpan connectTimeout, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch (Exception)
        {
            return false; // 超时/管道不存在/进程退出，一律视为未就绪
        }
    }
}
