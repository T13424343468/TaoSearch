using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TaoSearch.App.Core;
using TaoSearch.App.Services;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.App;

/// <summary>
/// 快速搜索窗（Alt+Q 唤起）：输入即搜、键盘上下选择、Enter 打开、
/// Ctrl+E 打开目录、Esc 隐藏、空态展示最近搜索。
/// </summary>
public sealed partial class QuickSearchWindow : Window
{
    private const int MaxResults = 8;
    private readonly SearchGateway _gateway = new();
    private readonly AppSettings _settings = AppSettings.Instance;
    private CancellationTokenSource? _debounceCts;
    private readonly List<HitViewModel> _items = [];

    /// <summary>WinUI3 Window 无 Visible/Hide，自行跟踪显隐。</summary>
    public bool IsVisible { get; private set; }

    public QuickSearchWindow()
    {
        InitializeComponent();
        Title = "TaoSearch 快速搜索";
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "taosearch.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { /* 图标缺失不阻断 */ }

        // 迷你窗形态：640 宽、屏幕居中、置顶显示（OverlappedPresenter 承载置顶）
        WindowPlacement.CenterOnScreen(this, 640, 480, topMost: true);

        QuickInput.KeyDown += OnInputKeyDown;
        QuickList.ItemsSource = _items;
        ShowIdleState();
    }

    /// <summary>唤起：清空输入、置顶聚焦。</summary>
    public new void Activate()
    {
        QuickInput.Text = "";
        base.Activate();
        IsVisible = true;
        QuickInput.Focus(FocusState.Programmatic);
    }

    /// <summary>隐藏窗口（WinUI3 Window 无 Hide，走 AppWindow）。</summary>
    public void HideQuick()
    {
        AppWindow.Hide();
        IsVisible = false;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Delay(100, token).ContinueWith(_ =>
        {
            queue.TryEnqueue(async () =>
            {
                try { await SearchAsync(token); } catch (OperationCanceledException) { }
            });
        });
    }

    private async Task SearchAsync(CancellationToken ct)
    {
        var query = QuickInput.Text.Trim();
        if (query.Length == 0)
        {
            ShowIdleState();
            return;
        }
        try
        {
            var batch = await _gateway.SearchAsync(query, QueryScope.Filename, MaxResults, ct);
            _items.Clear();
            foreach (var hit in batch.Hits.Take(MaxResults))
                _items.Add(MainViewModel.ToSharedViewModel(hit));
            QuickList.ItemsSource = null;
            QuickList.ItemsSource = _items;

            if (_items.Count == 0)
            {
                QuickEmpty.Visibility = Visibility.Visible;
                QuickList.Visibility = Visibility.Collapsed;
                EmptyTitle.Text = $"没有找到「{query}」";
                EmptyDesc.Text = "换个更短的关键词试试。";
                FootHint.Text = "Enter 在主窗口中打开完整搜索";
            }
            else
            {
                QuickEmpty.Visibility = Visibility.Collapsed;
                QuickList.Visibility = Visibility.Visible;
                QuickList.SelectedIndex = 0;
                FootHint.Text = "↑ ↓ 选择 · Enter 打开 · Ctrl+E 打开目录";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 文案中文化（0.1.0.9）：不把原始英文异常直接抛给用户
            QuickEmpty.Visibility = Visibility.Visible;
            QuickList.Visibility = Visibility.Collapsed;
            EmptyTitle.Text = "搜索出错";
            EmptyDesc.Text = "搜索服务暂时不可用，请稍候重试。";
            FootHint.Text = "Enter 在主窗口中打开完整搜索";
        }
    }

    private void ShowIdleState()
    {
        QuickEmpty.Visibility = Visibility.Visible;
        QuickList.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = "快速搜索";
        var recent = _settings.RecentSearches.Count > 0
            ? "输入即搜。最近：" + string.Join(" · ", _settings.RecentSearches)
            : "输入即搜。";
        EmptyDesc.Text = recent;
        FootHint.Text = "Alt+Q 唤起 / 隐藏 · Esc 隐藏窗口";
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrlDown = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down when _items.Count > 0:
                QuickList.SelectedIndex = Math.Min(_items.Count - 1, QuickList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up when _items.Count > 0:
                QuickList.SelectedIndex = Math.Max(0, QuickList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                OpenSelected(closeAfter: true);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.E when ctrlDown:
                RevealSelected();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                HideQuick();
                e.Handled = true;
                break;
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e) => OpenSelected(closeAfter: false);

    private void OpenSelected(bool closeAfter)
    {
        if (QuickList.SelectedItem is not HitViewModel hit) return;
        _settings.PushRecentSearch(QuickInput.Text.Trim());
        FileLauncher.OpenFile(hit.Path);
        if (closeAfter) HideQuick();
    }

    private void RevealSelected()
    {
        if (QuickList.SelectedItem is not HitViewModel hit) return;
        _settings.PushRecentSearch(QuickInput.Text.Trim());
        FileLauncher.RevealInExplorer(hit.Path);
        HideQuick();
    }
}
