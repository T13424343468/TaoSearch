using Microsoft.UI.Xaml;
using TaoSearch.App.Services;

namespace TaoSearch.App;

/// <summary>应用入口：首启引导 / 主窗 / 快速搜索窗（Alt+Q 全局热键）/ 主题。</summary>
public partial class App : Application
{
    public static App? Instance { get; private set; }

    private MainWindow? _mainWindow;
    private QuickSearchWindow? _quickWindow;
    private GlobalHotkeyService? _hotkey;

    public App() => Instance = this;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = AppSettings.Instance;

        if (!settings.OnboardingCompleted)
        {
            // 首次使用：先走引导，完成后再进主窗
            var onboarding = new OnboardingWindow();
            onboarding.Activate();
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
    }

    private void RegisterHotkey()
    {
        if (!AppSettings.Instance.QuickHotkeyEnabled) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _hotkey = new GlobalHotkeyService(queue, ToggleQuickWindow);
        _hotkey.Start(); // 注册失败不阻断启动，真机验收补提示
    }

    /// <summary>热键唤起：显示并前置主窗（托盘隐藏态恢复）。</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
        }
        _mainWindow.Activate(); // WinUI3 Activate 自带显示与前置；托盘/热键真机验收补强
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
    }

    /// <summary>取当前活动窗口句柄（FolderPicker 等需绑定窗口的 API 用）。</summary>
    public IntPtr GetActiveWindowHandle()
    {
        var window = _quickWindow is { Visible: true } ? (Window)_quickWindow : (Window?)_mainWindow;
        return window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
}
