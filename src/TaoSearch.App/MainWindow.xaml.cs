using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TaoSearch.App.Services;
using TaoSearch.App.ViewModels;

namespace TaoSearch.App;

/// <summary>主窗：文件名/内容双标签、筛选 chips、排序、空态/报错、索引状态条、底部导航。</summary>
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

        // 窗口标题栏与任务栏图标（非打包应用从安装目录取 ico）
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "taosearch.ico");
            if (System.IO.File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch { /* 图标缺失不阻断启动 */ }

        // x:Bind 在 Window 根不可用（生成代码要求 FrameworkElement），改 code-behind 接线
        StatusText.Text = ViewModel.StatusText;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.StatusText))
                StatusText.Text = ViewModel.StatusText;
            if (e.PropertyName is nameof(MainViewModel.Hits))
                UpdateEmptyState();
        };
        ResultList.ItemsSource = ViewModel.Hits;
        ViewModel.Hits.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();

        _ = RefreshIndexStatusAsync();
    }

    // —— 双标签：文件名 / 内容（单选互斥） ——
    private void OnTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton clicked) SelectTab(clicked);
    }

    private void SelectTab(ToggleButton clicked)
    {
        clicked.IsChecked = true;
        var other = ReferenceEquals(clicked, TabFilename) ? TabContent : TabFilename;
        other.IsChecked = false;

        var isContent = ReferenceEquals(clicked, TabContent);
        ChipsPanel.Visibility = isContent ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.PlaceholderText = isContent ? "输入关键词，在文档内容中检索" : "输入文件名，支持 * 通配与 regex: 正则";
        if (ViewModel.IsContentScope != isContent)
            ViewModel.IsContentScope = isContent;
    }

    // —— 筛选 chips：全部/Markdown/文档/表格/代码（单选互斥） ——
    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        clicked.IsChecked = true;
        foreach (var chip in new[] { ChipAll, ChipMarkdown, ChipDocument, ChipSheet, ChipCode })
            if (!ReferenceEquals(chip, clicked))
                chip.IsChecked = false;
        ViewModel.KindFilter = (string)clicked.Tag;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && Enum.TryParse<MainViewModel.SortKind>(tag, out var kind))
        {
            ViewModel.Sort = kind;
        }
    }

    private void OnSearchTextChanged(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text != ViewModel.Query)
            ViewModel.Query = SearchBox.Text;
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        // 打开文件/所在目录：真机验收项（ShellExecute 走资源管理器定位）
        if (ResultList.SelectedItem is HitViewModel hit)
            FileLauncher.RevealInExplorer(hit.Path);
        // 选中后立即取消，允许重复点击同一行
        ResultList.SelectedItem = null;
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "content":
                    // 内容搜索与文件名搜索共用主窗口，通过上方标签页切换（原型口径）
                    OverlayHost.Visibility = Visibility.Collapsed;
                    SelectTab(TabContent);
                    break;
                case "settings":
                    OverlayHost.Content = new SettingsPage { ViewModel = SettingsViewModel };
                    OverlayHost.Visibility = Visibility.Visible;
                    break;
                default:
                    OverlayHost.Visibility = Visibility.Collapsed;
                    break;
            }
        }
    }

    // —— 空态与报错（六.2 负阴抱阳） ——
    private void UpdateEmptyState()
    {
        ErrorBanner.Visibility = ViewModel.StatusText.StartsWith("搜索出错", StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
        if (ErrorBanner.Visibility == Visibility.Visible)
            ErrorCause.Text = ViewModel.StatusText;

        if (ViewModel.Hits.Count > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyPanel.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(ViewModel.Query))
        {
            EmptyIcon.Text = "🔍";
            EmptyTitle.Text = "准备好了";
            EmptyDesc.Text = "索引已就绪。输入即搜，无需等待；Alt+Q 随时唤起快速搜索窗。";
        }
        else
        {
            EmptyIcon.Text = "📥";
            EmptyTitle.Text = ViewModel.IsContentScope
                ? $"内容中未找到「{ViewModel.Query}」"
                : $"未找到与「{ViewModel.Query}」匹配的结果";
            EmptyDesc.Text = ViewModel.IsContentScope
                ? "内容索引在后台持续构建，覆盖范围随索引增长。试试更短的关键词。"
                : "试试更短的关键词；或检查通配符与 regex: 语法是否正确。";
        }
    }

    /// <summary>索引状态条：就绪/建索引中 + 文件数。</summary>
    private async Task RefreshIndexStatusAsync()
    {
        try
        {
            var status = await _gateway.GetIndexStatusAsync();
            IndexStatusText.Text = status.FilenameIndexReady
                ? $"索引就绪 · {status.FileCount:N0} 个文件"
                : $"建索引中 · 已收录 {status.FileCount:N0} 个文件";
        }
        catch
        {
            IndexStatusText.Text = "索引服务未连接";
        }
    }
}
