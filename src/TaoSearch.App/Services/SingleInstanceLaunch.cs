using System.Diagnostics;
using System.Text.Json;

namespace TaoSearch.App.Services;

/// <summary>
/// 单实例与跳转列表参数转发：跳转列表点击会拉起新进程，
/// 已有实例运行时把启动参数写入临时文件并唤醒首实例，自身退出。
/// </summary>
internal static class SingleInstanceLaunch
{
    private const string MutexName = @"Local\Tao.TaoSearch.SingleInstance";
    private const string EventName = @"Local\Tao.TaoSearch.Launch";

    private static Mutex? _mutex;
    private static EventWaitHandle? _launchEvent;

    private static string ArgsFilePath =>
        Path.Combine(Path.GetTempPath(), "TaoSearch", "launch-args.json");

    /// <summary>尝试成为唯一实例；已有实例在跑时返回 false。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>把启动参数转交给已运行实例（写文件 + 事件唤醒），随后调用方退出进程。</summary>
    public static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArgsFilePath)!);
            File.WriteAllText(ArgsFilePath, JsonSerializer.Serialize(args));
            var evt = EventWaitHandle.OpenExisting(EventName);
            evt.Set();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Launch] 参数转发失败: {ex.Message}");
        }
    }

    /// <summary>后台监听后续实例的参数转发；收到即回调（UI 线程由 DispatcherQueue 保证，见 App 接线）。</summary>
    public static void StartListening(Action<string[]> onArgs)
    {
        try
        {
            _launchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Launch] 监听事件创建失败: {ex.Message}");
            return;
        }

        var thread = new Thread(() =>
        {
            while (_launchEvent!.WaitOne())
            {
                try
                {
                    if (!File.Exists(ArgsFilePath)) continue;
                    var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ArgsFilePath)) ?? [];
                    File.Delete(ArgsFilePath);
                    if (args.Length > 0) onArgs(args);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Launch] 参数处理失败: {ex.Message}");
                }
            }
        })
        { IsBackground = true, Name = "TaoSearch.LaunchListener" };
        thread.Start();
    }
}

/// <summary>启动参数解析：--quick / --open-index / --search &lt;关键词&gt;。</summary>
internal static class LaunchArgs
{
    public static bool HasQuick(string[] args) => args.Contains("--quick");
    public static bool HasOpenIndex(string[] args) => args.Contains("--open-index");

    public static bool TryGetSearch(string[] args, out string keyword)
    {
        keyword = string.Empty;
        var index = Array.IndexOf(args, "--search");
        if (index < 0 || index == args.Length - 1) return false;
        // 关键词可能含空格被拆成多段，重新拼接
        keyword = string.Join(' ', args[(index + 1)..]).Trim();
        return keyword.Length > 0;
    }
}
