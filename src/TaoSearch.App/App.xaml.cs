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
        _mainWindow.Activate(); // WinUI3 Activate 自带显示与前置；托盘/热键真机验收补强
    }
}
