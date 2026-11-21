using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.ViewModels;

namespace TaoSearch.App;

/// <summary>设置页：索引状态/重建/暂停恢复/开机自启/存储位置。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; set; } = default!;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnRefresh(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        _ = ViewModel.RefreshAsync();

    private async void OnRebuild(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var message = await ViewModel.RebuildAsync();
        await new ContentDialog
        {
            Title = "索引重建",
            Content = message,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private async void OnTogglePause(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var message = await ViewModel.TogglePauseAsync();
        await new ContentDialog
        {
            Title = "全文索引",
            Content = message,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private void OnStartupToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // MSIX StartupTask：清单声明 + RequestEnabledAsync，真机验收项
    }
}
