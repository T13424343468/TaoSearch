using Microsoft.UI.Xaml;

namespace TaoSearch.App;

/// <summary>应用入口：主窗启动 + 托盘常驻 + 全局热键 Alt+Q。</summary>
public partial class App : Application
{
    public static App? Instance { get; private set; }

    private MainWindow? _mainWindow;

    public App() => Instance = this;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }

    /// <summary>热键唤起：显示并前置主窗（托盘隐藏态恢复）。</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Activate();
        Microsoft.UI.Xaml.WindowInteropHelper? _ = null; // WinUI3 前置由 AppWindow 处理
        _mainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
            Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOW);
            SetForegroundWindowSafe(hwnd);
        });
    }

    private static void SetForegroundWindowSafe(IntPtr hwnd)
    {
        // WinUI3 无直接前台 API，走 AppWindow.IsAlwaysOnTop 短暂置顶再取消
        // 简化实现：依赖 Activate() 的系统前置行为；真机验收时按需补强
    }
}
