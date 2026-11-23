using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.App.Core;
using TaoSearch.App.Services;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.App;

/// <summary>
/// 首次使用引导：欢迎 → 索引位置与范围 → 建索引进度（真实口径、可暂停）→ 完成。
/// 文案对照原型 onboarding 页逐字落实；0.1.0.9：居中、置顶、真实进度、暂停失败显式提示。
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
        WindowPlacement.CenterOnScreen(this, 760, 560, topMost: true);
        UpdateMethodText();
    }

    /// <summary>方式文案按真实权限口径（0.1.0.9）：无管理员权限不再宣称 MFT 直读。</summary>
    private void UpdateMethodText()
    {
        bool isAdmin;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            isAdmin = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { isAdmin = false; }
        MethodText.Text = isAdmin
            ? "MFT 直读（NTFS 主文件表）"
            : "目录扫描（未以管理员身份运行，速度较慢）";
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
                DoneNote.Text = "索引服务未响应，文件名搜索将在服务可用后自动开始。";
            }
        }
        catch (Exception)
        {
            // 文案中文化（0.1.0.9）：不透传英文异常；轮询会接续，就绪后自动出「完成」
            DoneNote.Text = "索引服务已启动，正在首次建立索引（仅一次，视文件数量需几分钟），完成后自动开始。";
        }
        StartPolling();
    }

    /// <summary>轮询索引状态：就绪前不确定进度（不虚构百分比），就绪即出「完成」。</summary>
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
                ProgressCount.Text = status.FilenameIndexReady
                    ? $"索引建立完成，共 {status.FileCount:N0} 个文件"
                    : $"已收录 {status.FileCount:N0} 个文件，仍在继续…";
                if (status.FilenameIndexReady)
                {
                    IndexProgress.IsIndeterminate = false;
                    IndexProgress.Value = 100;
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
            else
            {
                // 失败显式提示（0.1.0.9：不再静默）
                DoneNote.Text = "暂停未成功：索引服务暂时不可用，稍后再试。";
            }
        }
        catch
        {
            DoneNote.Text = "暂停未成功：索引服务未连接，稍后再试。";
        }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        CloseAndShowMain();
    }

    private void CloseAndShowMain()
    {
        // 先显主窗再关引导窗（0.1.0.9 修：先关后开会触发 WinUI3「末窗关闭即退出」，
        // 引导完成/跳过后进程静默退出、主窗不出现；云端 gate2 G2-15/G2-19 实证）
        App.Instance?.ShowMainWindow();
        Close();
    }
}
