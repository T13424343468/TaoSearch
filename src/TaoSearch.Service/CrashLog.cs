namespace TaoSearch.Service;

/// <summary>
/// 崩溃日志随包落盘（v3.22 五章「崩溃日志随包落盘令」，2026-11-23 老板令）：
/// 服务端进程崩溃时把异常类型、堆栈、发生时间、版本号写到本地日志文件，随包内建、无需配置；
/// 不搞自动上报、不收集任何个人信息，日志只留在用户自己机器上。
/// 落点优先级：TAOSEARCH_DATA → D:\TaoSearch → ProgramData\TaoSearch（服务以 LocalSystem 运行时
/// %LOCALAPPDATA% 指向系统 profile，不落用户可见处，故不走 LocalApplicationData）。
/// </summary>
internal static class CrashLog
{
    internal static readonly string Version =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    internal static void Write(string source, Exception? ex)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [v{Version}] [{source}] " +
                       $"{ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n---\n";
            File.AppendAllText(Path.Combine(ResolveDir(), "service-crash.log"), line);
        }
        catch { /* 取证失败不二次抛出 */ }
    }

    private static string ResolveDir()
    {
        // 与 Program.ResolveDataDir 同口径的降级链，崩溃时数据目录可能尚未建立
        string?[] candidates =
        {
            Environment.GetEnvironmentVariable("TAOSEARCH_DATA"),
            @"D:\TaoSearch",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TaoSearch"),
        };
        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { Directory.CreateDirectory(dir); return dir; }
            catch { /* 下一个候选 */ }
        }
        return Path.GetTempPath();
    }

    /// <summary>安装全局异常钩子（Main 最早处调用；控制台与服务模式共用）。</summary>
    internal static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }
}
