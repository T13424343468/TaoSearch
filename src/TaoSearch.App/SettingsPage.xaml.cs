using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.Services;
using TaoSearch.App.ViewModels;

namespace TaoSearch.App;

/// <summary>设置页：索引状态/操作/索引库位置/排除目录/优先级/主题/快速搜索窗/自启/关于。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; set; } = default!;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;

    /// <summary>关于行：版本号取自程序集（0.1.0.9：不再写死，随包自动更新）。</summary>
    public string AboutLine { get; } = $"TaoSearch · 版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Instance;
        ExcludeList.ItemsSource = settings.ExcludedDirectories;
        QuickStartupToggle.IsOn = settings.ShowQuickOnStartup;
        HotkeyToggle.IsOn = settings.QuickHotkeyEnabled;
        UpdateHotkeyNote();
        (settings.IsDarkTheme ? ThemeMobai : ThemeShuimo).IsChecked = true;
        var priority = settings.ContentPriority switch
        {
            "low" => 1,
            "normal" => 2,
            _ => 0,
        };
        PriorityBox.SelectedIndex = priority;

        // 进页自动加载 + 定时刷新（0.1.0.9）：状态不再非要手动点刷新
        _ = ViewModel.RefreshAsync();
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += (_, _) => _ = ViewModel.RefreshAsync();
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _refreshTimer?.Stop();

    private async void OnRebuild(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var message = await ViewModel.RebuildAsync();
        await ShowDialog("索引重建", message);
    }

    private async void OnTogglePause(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var message = await ViewModel.TogglePauseAsync();
        await ShowDialog("全文索引", message);
    }

    // —— 索引库位置 ——
    private async void OnChangeIndexLocation(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            // 非打包应用需绑定窗口句柄
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Instance!.GetActiveWindowHandle());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ViewModel.IndexLocation = folder.Path;
            AppSettings.Instance.IndexLocation = folder.Path;
            AppSettings.Instance.Save();
        }
        catch (Exception)
        {
            // 文案中文化（0.1.0.9）：不透传英文异常
            await ShowDialog("更改索引库位置", "未能打开文件夹选择器，请重试。");
        }
    }

    private void OnResetIndexLocation(object sender, RoutedEventArgs e)
    {
        ViewModel.IndexLocation = @"D:\TaoSearch\Index";
        AppSettings.Instance.IndexLocation = ViewModel.IndexLocation;
        AppSettings.Instance.Save();
    }

    // —— 排除目录 ——
    private void OnAddExclude(object sender, RoutedEventArgs e)
    {
        var value = ExcludeInput.Text.Trim();
        if (value.Length == 0) return;
        if (!AppSettings.Instance.ExcludedDirectories.Contains(value))
        {
            AppSettings.Instance.ExcludedDirectories.Add(value);
            AppSettings.Instance.Save();
            ExcludeList.ItemsSource = null;
            ExcludeList.ItemsSource = AppSettings.Instance.ExcludedDirectories;
        }
        ExcludeInput.Text = "";
    }

    private void OnRemoveExclude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string dir) return;
        AppSettings.Instance.ExcludedDirectories.Remove(dir);
        AppSettings.Instance.Save();
        ExcludeList.ItemsSource = null;
        ExcludeList.ItemsSource = AppSettings.Instance.ExcludedDirectories;
    }

    // —— 内容索引优先级 ——
    private void OnPriorityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PriorityBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            AppSettings.Instance.ContentPriority = tag;
            AppSettings.Instance.Save();
        }
    }

    // —— 主题 ——
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        var theme = ThemeMobai.IsChecked == true ? "mobai" : "shuimo";
        AppSettings.Instance.Theme = theme;
        AppSettings.Instance.Save();
        App.Instance?.ApplyTheme(theme);
    }

    // —— 快速搜索窗 ——
    private void OnQuickStartupToggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.ShowQuickOnStartup = QuickStartupToggle.IsOn;
        AppSettings.Instance.Save();
    }

    // —— 全局热键开关（0.1.0.9：即时生效 + 结果反馈） ——
    private void OnHotkeyToggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.QuickHotkeyEnabled = HotkeyToggle.IsOn;
        AppSettings.Instance.Save();
        App.Instance?.ReRegisterHotkey();
        UpdateHotkeyNote();
    }

    private void UpdateHotkeyNote()
    {
        HotkeyNote.Text = AppSettings.Instance.QuickHotkeyEnabled
            ? "已开启。若注册失败（快捷键被占用），启动或开启时会弹窗提示。"
            : "已关闭。可从任务栏托盘或主窗打开快速搜索窗。";
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        // MSIX StartupTask：清单声明 + RequestEnabledAsync，真机验收项
    }

    // —— 关于 ——
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        // 更新服务尚未接入：如实告知版本与渠道，不伪造「已是最新」结论
        await ShowDialog("检查更新", $"当前版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}。更新检查服务将随正式安装器一同提供。");
    }

    private async Task ShowDialog(string title, string content)
    {
        await new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }
}
