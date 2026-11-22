using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace TaoSearch.App.Services;

/// <summary>窗口落位（0.1.0.9）：按内容尺寸屏幕居中；引导窗另可置顶。</summary>
public static class WindowPlacement
{
    /// <summary>窗口缩放到指定尺寸并移到主屏居中位置。</summary>
    public static void CenterOnScreen(Window window, int width, int height, bool topMost = false)
    {
        try
        {
            var presenter = window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter is not null && topMost)
                presenter.IsAlwaysOnTop = true;

            window.AppWindow.Resize(new SizeInt32(width, height));

            // 工作区（排除任务栏）：GetFromWindowId 第二参为回退策略（Nearest=找不到就近给），WorkArea 为 RectInt32
            var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
            window.AppWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // 落位失败不阻断窗口：保持系统默认位置
        }
    }
}
