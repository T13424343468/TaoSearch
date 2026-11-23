using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.Core;
using TaoSearch.App.Services;

namespace TaoSearch.App;

/// <summary>应用入口：首启引导 / 主窗 / 快速搜索窗（Alt+Q 全局热键）/ 托盘常驻 / 主题 / 跳转列表。</summary>
public partial class App : Application
{
    public static App? Instance { get; private set; }

    private MainWindow? _mainWindow;
    private QuickSearchWindow? _quickWindow;
    private OnboardingWindow? _onboardingWindow;
    private GlobalHotkeyService? _hotkey;
    private TrayIconService? _tray;

    /// <summary>托盘「退出」触发的窗口关闭放行一次（其余关闭一律缩托盘）。</summary>
    public bool SuppressCloseToTrayOnce { get; set; }

    public App()
    {
        Instance = this;

        // 全局异常取证（0.1.0.9）：XAML 原生崩溃走 WER 事件日志，托管异常走 crash.log
        UnhandledException += (s, e) =>
        {
            LogCrash("Application.UnhandledException", e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>全局异常落盘（0.1.0.9）：未处理异常写 crash.log，云测与真机排障取证用。</summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoSearch");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n---\n";
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch { /* 取证失败不二次抛出 */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        JumpListService.SetProcessAppId();

        var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // 单实例：跳转列表点击拉起新进程时转发给已运行实例后退出
        if (!SingleInstanceLaunch.TryAcquire())
        {
            SingleInstanceLaunch.ForwardToRunningInstance(launchArgs);
            Environment.Exit(0);
            return;
        }

        var settings = AppSettings.Instance;

        if (!settings.OnboardingCompleted)
        {
            // 首次使用：先走引导，完成后再进主窗
            _onboardingWindow = new OnboardingWindow();
            _onboardingWindow.Activate();
        }
        else
        {
            _mainWindow = new MainWindow();
            _mainWindow.Activate();
            if (settings.ShowQuickOnStartup)
                ShowQuickWindow();
        }

        RegisterHotkey();
        ApplyTheme(settings.Theme);

        // 托盘常驻（0.1.0.9）：图标 + 菜单；失败不阻断启动
        try
        {
            var trayQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _tray = new TrayIconService(trayQueue);
            _tray.Show();
        }
        catch { /* 托盘缺失不阻断 */ }

        // 跳转列表参数：监听后续实例转发（回调切回 UI 线程）+ 处理本进程冷启动参数
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        SingleInstanceLaunch.StartListening(forwarded => queue.TryEnqueue(() => ApplyLaunchArgs(forwarded)));
        ApplyLaunchArgs(launchArgs);

        // 构建任务栏跳转列表（失败降级，不阻断启动）
        _ = Task.Run(() => JumpListService.Build(settings.RecentSearches));
    }

    /// <summary>应用跳转列表/命令行参数：--quick / --open-index / --search &lt;关键词&gt;。</summary>
    private void ApplyLaunchArgs(string[] args)
    {
        try
        {
            if (LaunchArgs.HasQuick(args)) ShowQuickWindow();
            if (LaunchArgs.TryGetSearch(args, out var keyword))
            {
                ShowMainWindow();
                // 0.1.0.12：落搜索框（与手输同链路），不再只改 ViewModel 导致框内空白
                if (_mainWindow is not null) _mainWindow.SetSearchText(keyword);
            }
            if (LaunchArgs.HasOpenIndex(args)) OpenIndexLocation();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Launch] 参数应用失败: {ex.Message}");
        }
    }

    private void OpenIndexLocation()
    {
        var dir = AppSettings.Instance.IndexLocation;
        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void RegisterHotkey()
    {
        if (!AppSettings.Instance.QuickHotkeyEnabled) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _hotkey = new GlobalHotkeyService(queue, ToggleQuickWindow);
        var registered = _hotkey.Start(); // 注册失败显式反馈（0.1.0.9：不再静默）
        if (!registered) _ = ShowHotkeyNoticeAsync();
    }

    /// <summary>热键注册失败提示：冲突换键指引，不再静默。</summary>
    private async Task ShowHotkeyNoticeAsync()
    {
        try
        {
            var window = (Window?)_mainWindow ?? (Window?)_quickWindow ?? (Window?)_onboardingWindow;
            if (window?.Content is not FrameworkElement root || root.XamlRoot is null) return;
            await new ContentDialog
            {
                Title = "快捷键未生效",
                Content = "全局快捷键 Alt+Q 注册失败，可能被其他程序占用。" +
                          "关闭占用程序后重启 TaoSearch 即可恢复；也可先从任务栏图标打开主窗使用。",
                CloseButtonText = "知道了",
                XamlRoot = root.XamlRoot,
            }.ShowAsync();
        }
        catch { /* 无可承载窗口时静默：托盘菜单仍可用 */ }
    }

    /// <summary>设置页开关热键后重注册（0.1.0.9）。</summary>
    public void ReRegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        RegisterHotkey();
    }

    /// <summary>托盘「退出」：放行一次窗口关闭并结束进程。</summary>
    public void ExitApplication()
    {
        SuppressCloseToTrayOnce = true;
        _tray?.Dispose();
        _hotkey?.Dispose();
        Environment.Exit(0);
    }

    /// <summary>热键唤起：显示并前置主窗（托盘隐藏态恢复）。</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
        }
        _mainWindow.Activate(); // WinUI3 Activate 自带显示与前置；托盘隐藏态经此恢复
    }

    public void ShowQuickWindow()
    {
        _quickWindow ??= new QuickSearchWindow();
        _quickWindow.Activate();
    }

    public void ToggleQuickWindow()
    {
        if (_quickWindow is { IsVisible: true })
        {
            _quickWindow.HideQuick();
        }
        else
        {
            ShowQuickWindow();
        }
    }

    /// <summary>主题切换：应用到当前窗口内容根（水墨=浅色 / 墨白=深色）。</summary>
    public void ApplyTheme(string theme)
    {
        var elementTheme = string.Equals(theme, "mobai", StringComparison.Ordinal)
            ? ElementTheme.Dark : ElementTheme.Light;
        if (_mainWindow?.Content is FrameworkElement root)
            root.RequestedTheme = elementTheme;
        if (_quickWindow?.Content is FrameworkElement quickRoot)
            quickRoot.RequestedTheme = elementTheme;
        if (_onboardingWindow?.Content is FrameworkElement onboardingRoot)
            onboardingRoot.RequestedTheme = elementTheme;
    }

    /// <summary>取当前活动窗口句柄（FolderPicker 等需绑定窗口的 API 用）。</summary>
    public IntPtr GetActiveWindowHandle()
    {
        var window = _quickWindow is { Visible: true } ? (Window)_quickWindow : (Window?)_mainWindow;
        return window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
}
