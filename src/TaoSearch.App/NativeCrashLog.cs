using System.Runtime.InteropServices;

namespace TaoSearch.App;

/// <summary>
/// 原生崩溃轻量落盘（v3.22 五章「崩溃日志随包落盘令」，2026-11-23 老板令）：
/// 托管异常走 App.LogCrash（crash.log）；XAML/WinRT 原生崩溃此前只走 WER 事件日志，
/// 本类经 SetUnhandledExceptionFilter 兜底把原生异常码/地址/发生时间/版本号追加到同一 crash.log，
/// 供用户主动反馈问题时取用。尽力而为口径：过滤器链中后装的过滤器或硬崩溃（堆栈溢出等）可能绕过，
/// 不承诺捕获全部原生崩溃；不搞自动上报、不收集任何个人信息，日志只留在用户自己机器上。
/// </summary>
internal static class NativeCrashLog
{
    private static NativeFilterDelegate? _filterKeepAlive; // 防 GC 回收委托

    private delegate int NativeFilterDelegate(IntPtr exceptionPointers);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord64
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public ulong ExceptionRecord;
        public ulong ExceptionAddress;
        public ulong NumberParameters;
        // ulParameters[15] 省略——只读前 8 字节字段
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr SetUnhandledExceptionFilter(NativeFilterDelegate filter);

    /// <summary>安装原生崩溃过滤器（进程生命周期内一次；失败静默，不影响启动）。</summary>
    internal static void Install()
    {
        try
        {
            _filterKeepAlive = Filter;
            SetUnhandledExceptionFilter(_filterKeepAlive);
        }
        catch { /* 取证失败不阻断启动 */ }
    }

    private static int Filter(IntPtr exceptionPointers)
    {
        try
        {
            uint code = 0;
            nuint address = 0;
            if (exceptionPointers != IntPtr.Zero)
            {
                var record = Marshal.ReadIntPtr(exceptionPointers);
                if (record != IntPtr.Zero)
                {
                    code = (uint)Marshal.ReadInt32(record);
                    address = (nuint)Marshal.ReadInt64(record, 16); // ExceptionAddress @ offset 16
                }
            }
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoSearch");
            Directory.CreateDirectory(dir);
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [v{version}] [Native.UnhandledFilter] " +
                       $"code=0x{code:X8} address=0x{address:X}\n---\n";
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch { /* 取证失败不二次抛出 */ }
        return 0; // EXCEPTION_CONTINUE_SEARCH：交回系统默认处理（WER 照旧）
    }
}
