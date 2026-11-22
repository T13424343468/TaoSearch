using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.Services;
using TaoSearch.App.ViewModels;

namespace TaoSearch.App;

/// <summary>设置页：索引状态/操作/索引库位置/排除目录/优先级/主题/快速搜索窗/自启/关于。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; set; } = default!;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Instance;
        ExcludeList.ItemsSource = settings.ExcludedDirectories;
        QuickStartupToggle.IsOn = settings.ShowQuickOnStartup;
        (settings.IsDarkTheme ? ThemeMobai : ThemeShuimo).IsChecked = true;
        var priority = settings.ContentPriority switch
        {
            "low" => 1,
            "normal" => 2,
            _ => 0,
        };
        PriorityBox.SelectedIndex = priority;
    }

    private void OnRefresh(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        _ = ViewModel.RefreshAsync();

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
        catch (Exception ex)
        {
            await ShowDialog("更改索引库位置", $"未能打开文件夹选择器：{ex.Message}");
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

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        // MSIX StartupTask：清单声明 + RequestEnabledAsync，真机验收项
    }

    // —— 关于 ——
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        // 更新服务尚未接入：如实告知版本与渠道，不伪造「已是最新」结论
        await ShowDialog("检查更新", $"当前版本 0.1.0.7。更新检查服务将随正式安装器一同提供。");
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
