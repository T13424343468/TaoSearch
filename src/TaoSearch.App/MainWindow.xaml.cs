using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.Services;
using TaoSearch.App.ViewModels;

namespace TaoSearch.App;

/// <summary>主窗：搜索框防抖查询、结果列表、命中高亮预览、底部导航。</summary>
public sealed partial class MainWindow : Window
{
    private readonly SearchGateway _gateway = new();
    public MainViewModel ViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(_gateway);
        SettingsViewModel = new SettingsViewModel(_gateway);
        Title = "TaoSearch";

        // x:Bind 在 Window 根不可用（生成代码要求 FrameworkElement），改 code-behind 接线
        StatusText.Text = ViewModel.StatusText;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.StatusText))
                StatusText.Text = ViewModel.StatusText;
        };
        ResultList.ItemsSource = ViewModel.Hits;
    }

    private void OnSearchTextChanged(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text != ViewModel.Query)
            ViewModel.Query = SearchBox.Text;
    }

    private void OnScopeToggled(object sender, RoutedEventArgs e)
    {
        if (ContentToggle.IsOn != ViewModel.IsContentScope)
            ViewModel.IsContentScope = ContentToggle.IsOn;
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        // 打开文件/所在目录：真机验收项（ShellExecute 走资源管理器定位）
        if (ResultList.SelectedItem is HitViewModel hit)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{hit.Path}\"")
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { /* 打开失败静默，真机验收补 Toast */ }
        }
        // 选中后立即取消，允许重复点击同一行
        ResultList.SelectedItem = null;
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item)
        {
            if (item.Tag is "settings")
            {
                OverlayHost.Content = new SettingsPage { ViewModel = SettingsViewModel };
                OverlayHost.Visibility = Visibility.Visible;
            }
            else
            {
                OverlayHost.Visibility = Visibility.Collapsed;
            }
        }
    }
}
