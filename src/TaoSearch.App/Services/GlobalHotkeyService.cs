using System.Runtime.InteropServices;

namespace TaoSearch.App.Services;

/// <summary>
/// 全局热键服务：后台线程建 message-only 窗口注册 Alt+Q，
/// WM_HOTKEY 经 DispatcherQueue 回到 UI 线程，避免占用前台消息循环。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HOTKEY_ID = 0x5154; // "QT"

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly Action _callback;
    private Thread? _thread;
    private IntPtr _hwnd;
    private bool _registered;

    public GlobalHotkeyService(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action callback)
    {
        _dispatcher = dispatcher;
        _callback = callback;
    }

    public bool Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                _hwnd = CreateMessageWindow();
                if (_hwnd == IntPtr.Zero) { ready.Set(); return; }
                _registered = RegisterHotKey(_hwnd, HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, 'Q');
                ready.Set();
                int ret;
                while ((ret = GetMessage(out var msg, _hwnd, 0, 0)) > 0)
                {
                    if (msg.message == WM_HOTKEY && msg.wParam == HOTKEY_ID)
                        _dispatcher.TryEnqueue(() => _callback());
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                // ret == 0：WM_QUIT 正常退出；ret < 0：GetMessage 出错（如窗口已被销毁），同样退出
            }
            finally
            {
                // 清理必须由创建窗口的本线程执行：跨线程 Unregister/Destroy 会失败或行为未定义
                if (_hwnd != IntPtr.Zero)
                {
                    if (_registered) UnregisterHotKey(_hwnd, HOTKEY_ID);
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
        })
        { IsBackground = true, Name = "TaoSearch-Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return _registered;
    }

    /// <summary>通知热键线程退出；Unregister/Destroy 由线程自身在 finally 中完成（0.1.0.9 修复跨线程销毁）。</summary>
    public void Dispose()
    {
        if (_thread is { ManagedThreadId: var tid })
            PostThreadMessage(tid, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
    }

    // —— Win32 ——
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern short RegisterClassW(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    private static IntPtr CreateMessageWindow()
    {
        var proc = new WndProcDelegate(DefWindowProcW);
        var pProc = Marshal.GetFunctionPointerForDelegate(proc);
        var cls = new WNDCLASS
        {
            lpfnWndProc = pProc,
            lpszClassName = "TaoSearchHotkeyWnd",
            hInstance = Marshal.GetHINSTANCE(typeof(GlobalHotkeyService).Module),
        };
        if (RegisterClassW(ref cls) == 0 && Marshal.GetLastWin32Error() != 0 && Marshal.GetLastWin32Error() != 1410)
            return IntPtr.Zero; // 1410=CLASS_ALREADY_EXISTS 可复用
        // HWND_MESSAGE=新 message-only 窗口：parent 传 -3
        GC.KeepAlive(proc);
        return CreateWindowExW(0, "TaoSearchHotkeyWnd", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, Marshal.GetHINSTANCE(typeof(GlobalHotkeyService).Module), IntPtr.Zero);
    }
}
