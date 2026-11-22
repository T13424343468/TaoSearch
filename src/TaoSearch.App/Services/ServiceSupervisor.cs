using System.Diagnostics;
using System.IO.Pipes;

namespace TaoSearch.App.Services;

/// <summary>
/// 索引服务守护（真机红灯修复）：安装器只落文件不注册自启，云端测试均手动拉起服务，
/// 真机首次使用时服务从未运行 → 壳层管道连接超时（老板真机实锤）。
/// 壳层在首次 IPC 前探测管道；不可达则从安装目录拉起 TaoSearch.Service.exe 并等待就绪。
/// 进程级只尝试一次（无论成败），避免每次按键都白等；服务首建索引期间管道未监听（先建后听），
/// 由壳层轮询自然接续，就绪后进度自动恢复。
/// </summary>
public static class ServiceSupervisor
{
    private const string PipeName = "taosearch.svc.1";
    private static int _attempted;

    /// <summary>确保索引服务在运行：探测管道 → 未监听则拉起服务进程 → 轮询等待（至多 15s）。</summary>
    public static async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _attempted, 1, 0) == 1) return;

        try
        {
            if (await PipeAliveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) return;

            var exe = Path.Combine(AppContext.BaseDirectory, "TaoSearch.Service.exe");
            if (!File.Exists(exe)) return;

            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (process is null) return;

            // 服务恢复快照路径秒级就绪；首建路径管道在索引建完后才监听，这里只等短窗口，
            // 未就绪交由调用方轮询接续（OnboardingWindow.PollLoopAsync / MainWindow 轮询）
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (await PipeAliveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) return;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 拉起失败不阻断壳层：后续 IPC 调用按原错误路径提示
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
