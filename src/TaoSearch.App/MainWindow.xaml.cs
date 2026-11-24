using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TaoSearch.App.Core;
using TaoSearch.App.Services;

namespace TaoSearch.App;

/// <summary>主窗：文件名/内容双标签、筛选 chips、排序、空态/报错、索引状态条、底部导航、托盘常驻。</summary>
public sealed partial class MainWindow : Window
{
    private readonly SearchGateway _gateway = new();
    public MainViewModel ViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;

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

        WindowPlacement.CenterOnScreen(this, 960, 680);

        // 关窗缩托盘（常驻口径）：点 × 隐藏窗口，退出走托盘菜单
        AppWindow.Closing += (sender, args) =>
        {
            if (App.Instance is { } app && app.SuppressCloseToTrayOnce)
            {
                app.SuppressCloseToTrayOnce = false;
                return; // 托盘「退出」触发的真关闭放行
            }
            args.Cancel = true;
            AppWindow.Hide();
        };

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

        // 索引状态条自动刷新（0.1.0.9）：不再只在构造时取一次
        // Window 非 DependencyObject，无 DispatcherQueue 属性，取当前线程队列
        _statusTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(5);
        _statusTimer.Tick += (_, _) => _ = RefreshIndexStatusAsync();
        _statusTimer.Start();
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
        if (ViewModel is not null && ViewModel.IsContentScope != isContent)
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
        // XAML 解析期 ComboBoxItem 预置 IsSelected 会触发本回调，此时 ViewModel 尚未构造
        if (ViewModel is null) return;
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

    /// <summary>转发/跳转参数落搜索框（0.1.0.12）：走 TextBox 与手输一致的链路，框内可见关键词。</summary>
    public void SetSearchText(string text)
    {
        SearchBox.Text = text;
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
        // XAML 解析期首项 IsSelected 会触发本回调，此时 OverlayHost/ViewModel 尚未构造完成
        if (OverlayHost is null) return;
        if (sender.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "content":
                    // 内容搜索与文件名搜索共用主窗口，通过上方标签页切换（原型口径）
                    ShowResults();
                    SelectTab(TabContent);
                    break;
                case "settings":
                    OverlayHost.Content = new SettingsPage { ViewModel = SettingsViewModel };
                    OverlayHost.Visibility = Visibility.Visible;
                    ResultsHost.Visibility = Visibility.Collapsed;
                    break;
                default:
                    ShowResults();
                    break;
            }
        }
    }

    private void ShowResults()
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        ResultsHost.Visibility = Visibility.Visible;
    }

    // —— 空态与报错（六.2 负阴抱阳；错误与空态互斥） ——
    private void UpdateEmptyState()
    {
        bool hasError = ViewModel.StatusText.StartsWith("搜索出错", StringComparison.Ordinal);
        ErrorBanner.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        if (hasError)
            ErrorCause.Text = ViewModel.StatusText;

        if (ViewModel.Hits.Count > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }
        // 报错时不并排空态，避免「错误横幅 + 空态」同时出现
        EmptyPanel.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
        if (hasError) return;
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
            if (status.FilenameUsingSlowPath)
            {
                IndexStatusText.Text += "（未以管理员运行，正在慢速扫描；关闭后以管理员身份重新打开可秒级完成）";
            }
        }
        catch
        {
            IndexStatusText.Text = "索引服务未连接";
        }
    }
}
