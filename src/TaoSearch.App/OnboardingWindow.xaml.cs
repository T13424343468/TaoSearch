using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.Services;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.App;

/// <summary>
/// 首次使用引导：欢迎 → 索引位置与范围 → 建索引进度（MFT 直读、可暂停）→ 完成。
/// 文案对照原型 onboarding 页逐字落实。
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    private readonly SearchGateway _gateway = new();
    private readonly AppSettings _settings = AppSettings.Instance;
    private int _step = 1;
    private bool _paused;
    private CancellationTokenSource? _pollCts;

    public OnboardingWindow()
    {
        InitializeComponent();
        Title = "TaoSearch 首次使用引导";
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "taosearch.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { /* 图标缺失不阻断 */ }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 560));
    }

    private void SetStep(int step)
    {
        _step = step;
        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Dot1.Width = step == 1 ? 24 : 8;
        Dot2.Width = step == 2 ? 24 : 8;
        Dot3.Width = step == 3 ? 24 : 8;
        Dot2.Background = step >= 2 ? App.Current.Resources["AzureBrush"] as Microsoft.UI.Xaml.Media.Brush : App.Current.Resources["LineBrush"] as Microsoft.UI.Xaml.Media.Brush;
        Dot3.Background = step >= 3 ? App.Current.Resources["AzureBrush"] as Microsoft.UI.Xaml.Media.Brush : App.Current.Resources["LineBrush"] as Microsoft.UI.Xaml.Media.Brush;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        // 已跳过引导，稍后可在设置中建立索引
        _settings.OnboardingCompleted = true;
        _settings.Save();
        CloseAndShowMain();
    }

    private void OnNext(object sender, RoutedEventArgs e) => SetStep(2);

    private void OnBack(object sender, RoutedEventArgs e) => SetStep(1);

    private async void OnStartIndexing(object sender, RoutedEventArgs e)
    {
        _settings.IndexLocation = LocAppData.IsChecked == true
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoSearch", "Index")
            : @"D:\TaoSearch\Index";
        _settings.IndexScope = ScopeUser.IsChecked == true ? "user-folders"
            : ScopeCustom.IsChecked == true ? "custom" : "drive-d";
        _settings.OnboardingCompleted = true;
        _settings.Save();

        SetStep(3);
        try
        {
            var ack = await _gateway.RebuildAsync(QueryScope.Filename | QueryScope.Content);
            if (!ack.Ok)
            {
                DoneNote.Text = $"索引服务未响应：{ack.Error}。文件名搜索将在服务可用后自动开始。";
            }
        }
        catch (Exception ex)
        {
            DoneNote.Text = $"索引服务未连接：{ex.Message}。文件名搜索将在服务可用后自动开始。";
        }
        StartPolling();
    }

    /// <summary>轮询索引状态：进度条按文件数/百万估算，就绪即出「完成」。</summary>
    private void StartPolling()
    {
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _gateway.GetIndexStatusAsync(ct);
                double pct = Math.Min(95, status.FileCount / 1_048_576.0 * 100);
                if (status.FilenameIndexReady) pct = 100;
                IndexProgress.Value = pct;
                ProgressCount.Text = $"{status.FileCount:N0} 个文件";
                ProgressPct.Text = $"{pct:0}%";
                if (status.FilenameIndexReady)
                {
                    DoneNote.Text = "索引建立完成。文件名搜索已可用，内容索引转入后台低优先级构建。";
                    PauseButton.Visibility = Visibility.Collapsed;
                    FinishButton.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* 服务未连接继续轮询 */ }
            await Task.Delay(500, ct);
        }
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        try
        {
            var ack = _paused ? await _gateway.ResumeAsync() : await _gateway.PauseAsync();
            if (ack.Ok)
            {
                _paused = !_paused;
                PauseButton.Content = _paused ? "继续" : "暂停";
            }
        }
        catch { /* 服务未连接静默，真机验收补提示 */ }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        CloseAndShowMain();
    }

    private void CloseAndShowMain()
    {
        Close();
        App.Instance?.ShowMainWindow();
    }
}
