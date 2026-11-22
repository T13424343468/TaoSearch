using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoSearch.Kernel.Contracts;

namespace TaoSearch.App.Services;

/// <summary>
/// 系统托盘（0.1.0.9 新增）：常驻图标 + 右键菜单（打开主窗 / 快速搜索 / 暂停恢复索引 / 退出）。
/// Shell_NotifyIcon 直调，零第三方依赖；菜单经 TrackPopupMenu 在隐藏消息窗上弹出。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WM_APP_TRAYICON = 0x0400 + 0x5154; // 自定义回调消息
    private const uint NIF_MESSAGE = 0x0001, NIF_ICON = 0x0002, NIF_TIP = 0x0004;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;

    private readonly SearchGateway _gateway = new();
    private readonly DispatcherQueue _dispatcher;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _paused;
    private bool _created;
    private WndProcDelegate? _proc;
    private readonly Dictionary<int, string> _menuCommands = new();

    public TrayIconService(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>创建托盘图标；失败静默（不阻断启动，真机验收补提示）。</summary>
    public void Show()
    {
        if (_created) return;
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "taosearch.ico");
            _icon = File.Exists(iconPath)
                ? LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE)
                : IntPtr.Zero;

            _proc = WndProc;
            _hwnd = CreateHiddenWindow(_proc);
            if (_hwnd == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[tray] CreateHiddenWindow failed gle={Marshal.GetLastWin32Error()}");
                return;
            }

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_APP_TRAYICON,
                hIcon = _icon,
                szTip = "TaoSearch",
                szInfo = "",
            };
            _created = Shell_NotifyIcon(NIM_ADD, ref nid);
            if (!_created)
                Console.Error.WriteLine($"[tray] NIM_ADD failed gle={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            // 托盘创建失败不阻断主流程
            Console.Error.WriteLine($"[tray] Show exception: {ex.Message}");
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAYICON && lParam.ToInt64() == 0x0205 /* WM_RBUTTONUP：右键弹菜单 */)
        {
            _dispatcher.TryEnqueue(ShowContextMenu);
        }
        else if (msg == WM_COMMAND && wParam.ToInt64() >> 16 == 0)
        {
            int id = (int)(wParam.ToInt64() & 0xFFFF);
            if (_menuCommands.TryGetValue(id, out var command))
                _dispatcher.TryEnqueue(() => Execute(command));
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        try
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            _menuCommands.Clear();
            int id = 1;
            foreach (var text in new[]
            {
                "打开主窗", "快速搜索",
                _paused ? "恢复索引" : "暂停索引", "退出",
            })
            {
                AppendMenu(menu, 0x0000 /*MF_STRING*/, id, text);
                _menuCommands[id] = text;
                if (id == 2) AppendMenu(menu, 0x0800 /*MF_SEPARATOR*/, 0, null);
                id++;
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // 菜单外点击可关闭的标准前置
            TrackPopupMenu(menu, 0x0000 /*TPM_LEFTALIGN*/, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            DestroyMenu(menu);
        }
        catch
        {
            // 菜单失败不阻断
        }
    }

    private async void Execute(string command)
    {
        switch (command)
        {
            case "打开主窗":
                App.Instance?.ShowMainWindow();
                break;
            case "快速搜索":
                App.Instance?.ShowQuickWindow();
                break;
            case "暂停索引":
            case "恢复索引":
                try
                {
                    var ack = _paused ? await _gateway.ResumeAsync() : await _gateway.PauseAsync();
                    if (ack.Ok) _paused = !_paused;
                }
                catch { /* 服务未连接：下次点击再试 */ }
                break;
            case "退出":
                App.Instance?.ExitApplication();
                break;
        }
    }

    public void Dispose()
    {
        if (_created && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    private const uint WM_COMMAND = 0x0111;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeout;
        public uint dwInfoFlags;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern short RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private IntPtr CreateHiddenWindow(WndProcDelegate proc)
    {
        var pProc = Marshal.GetFunctionPointerForDelegate(proc);
        var cls = new WNDCLASS
        {
            lpfnWndProc = pProc,
            lpszClassName = "TaoSearchTrayWnd",
            hInstance = Marshal.GetHINSTANCE(typeof(TrayIconService).Module),
        };
        if (RegisterClassW(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410)
            return IntPtr.Zero; // 1410=CLASS_ALREADY_EXISTS 可复用
        GC.KeepAlive(proc);
        // WS_OVERLAPPED 仅作占位：不显示、不进任务栏
        return CreateWindowExW(0, "TaoSearchTrayWnd", "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
            Marshal.GetHINSTANCE(typeof(TrayIconService).Module), IntPtr.Zero);
    }
}
