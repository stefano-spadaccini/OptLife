using System.Runtime.InteropServices;

namespace OptLife.Modules.Overlay.Windows;

/// <summary>
/// The real overlay: a borderless window that is always on top, lets every click through and never
/// takes the keyboard, so the game underneath does not know it is there. Nothing is injected into
/// the game: it works over anything shown in a window or borderless full screen, and cannot be
/// seen over a game in exclusive full screen.
/// It lives on a thread of its own, which also watches the show/hide key.
/// </summary>
internal sealed class OverlayWindow : IOverlayEnvironment
{
    private const int TickMs = 50;
    private const int PlaceEveryTicks = 10;       // Where it belongs is re-checked twice a second.
    private const long ForgottenAfterMs = 3000;   // Nobody refreshed it (module switched off): take it down.

    private readonly Lock _gate = new();
    private Thread? _thread;
    private volatile string? _failure;
    private volatile OverlayFrame? _wanted;
    private long _wantedAt;
    private volatile int _watchedKey;
    private int _presses;

    /// <summary>The window handle while it exists; for the checks done on the real machine.</summary>
    internal IntPtr Handle { get; private set; }

    public bool TogglePressed(int functionKey)
    {
        _watchedKey = functionKey;
        EnsureThread();
        return Interlocked.Exchange(ref _presses, 0) > 0;
    }

    public void Show(OverlayFrame frame)
    {
        Interlocked.Exchange(ref _wantedAt, Environment.TickCount64);
        _wanted = frame;
        EnsureThread();
    }

    public void Hide() => _wanted = null;

    /// <summary>The screens attached now, with Windows' own name for each ("\.DISPLAY2").</summary>
    public IReadOnlyList<OverlayScreen> Screens()
    {
        var list = new List<OverlayScreen>();
        MonitorEnumProc each = (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
                list.Add(new OverlayScreen(info.szDevice, info.szDevice, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top, (info.dwFlags & 1) != 0));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
        GC.KeepAlive(each);
        return [.. list.OrderByDescending(m => m.Primary).ThenBy(m => m.Id, StringComparer.Ordinal)];
    }

    /// <summary>The screen chosen by its name, or none when it is not attached now.</summary>
    private static IntPtr NamedMonitor(string name)
    {
        var found = IntPtr.Zero;
        MonitorEnumProc each = (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info) && info.szDevice.Equals(name, StringComparison.OrdinalIgnoreCase)) found = monitor;
            return found == IntPtr.Zero;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
        GC.KeepAlive(each);
        return found;
    }

    private void EnsureThread()
    {
        if (_failure is { } failure) throw new InvalidOperationException(L.T("La finestra dell'overlay si è fermata: ", "The overlay window stopped: ") + failure);
        lock (_gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "OptLife overlay" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private void Run()
    {
        try
        {
            var window = new NativeWindow();
            window.CreateHandle(new CreateParams
            {
                Caption = "OptLife Overlay",
                Style = unchecked((int)WS_POPUP),
                ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                Width = 1,
                Height = 1,
            });
            Handle = window.Handle;

            var state = new Drawn();
            var keyWasDown = false;
            var tick = 0;
            using var timer = new System.Windows.Forms.Timer { Interval = TickMs };
            timer.Tick += (_, _) =>
            {
                try
                {
                    var key = _watchedKey;
                    var keyDown = key > 0 && IsDown(VK_CONTROL) && IsDown(VK_F1 + key - 1);
                    if (keyDown && !keyWasDown) Interlocked.Increment(ref _presses);
                    keyWasDown = keyDown;

                    var frame = _wanted;
                    if (frame is not null && Environment.TickCount64 - Interlocked.Read(ref _wantedAt) > ForgottenAfterMs) frame = null;
                    // Only when the person asked: OptLife's own window in front hides the strip.
                    if (frame is not null && frame.Style.HideOverOptLife && OwnWindowInFront()) frame = null;
                    Apply(window.Handle, frame, state, placeNow: tick++ % PlaceEveryTicks == 0);

                    // With nothing on screen and no key to watch there is no reason to wake up 20 times a second.
                    var interval = frame is null && key == 0 ? TickMs * PlaceEveryTicks : TickMs;
                    if (timer.Interval != interval) timer.Interval = interval;
                }
                catch (Exception ex)
                {
                    // An exception escaping a timer would open a Windows Forms error dialog: stop instead, and let the module report it.
                    timer.Stop();
                    ShowWindow(window.Handle, SW_HIDE);
                    _failure = $"{ex.GetType().Name}: {ex.Message}";
                }
            };
            timer.Start();
            Application.Run(); // This thread's own message loop; ends when the agent closes.
        }
        catch (Exception ex)
        {
            _failure = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>What is on screen now, to redraw only when something changed.</summary>
    private sealed class Drawn
    {
        public string Signature = "";
        public IntPtr Monitor;
        public bool Visible;
    }

    private static void Apply(IntPtr hwnd, OverlayFrame? frame, Drawn state, bool placeNow)
    {
        if (frame is null)
        {
            if (state.Visible) ShowWindow(hwnd, SW_HIDE);
            state.Visible = false;
            state.Signature = "";
            return;
        }

        if (placeNow || state.Monitor == IntPtr.Zero)
            state.Monitor = frame.Style.Monitor.Length > 0 && NamedMonitor(frame.Style.Monitor) is var named && named != IntPtr.Zero ? named : ChooseMonitor(hwnd, state.Monitor);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(state.Monitor, ref info))
        {
            state.Monitor = IntPtr.Zero; // That screen is gone: choose again next tick.
            return;
        }
        var dpi = GetDpiForMonitor(state.Monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
        var area = info.rcMonitor;
        var style = frame.Style;

        var signature = string.Join('|', frame.Rows.Select(r => $"{r.Label}={r.Value}{(r.Alert ? "!" : "")}")) + $"|{style}|{area.Left},{area.Top},{area.Right},{area.Bottom}|{dpi}";
        if (signature != state.Signature)
        {
            using var bitmap = OverlayRenderer.Render(frame, style.ScalePercent / 100d * dpi / 96d);
            var (x, y) = OverlayLayout.Place(style, bitmap.Width, bitmap.Height, area.Right - area.Left, area.Bottom - area.Top, dpi / 96d);
            Push(hwnd, bitmap, area.Left + x, area.Top + y);
            state.Signature = signature;
        }

        if (!state.Visible) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        state.Visible = true;
        // A game coming to the front can slip above other "always on top" windows: claim the top again.
        if (placeNow) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static uint _lastPid;
    private static bool _lastOwn;

    /// <summary>The window in front belongs to OptLife's panel (its full-screen library); the answer is kept per process.</summary>
    private static bool OwnWindowInFront()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == _lastPid) return _lastOwn;
        _lastPid = pid;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            _lastOwn = process.ProcessName.StartsWith("optlife-panel", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _lastOwn = false;
        }
        return _lastOwn;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>
    /// The screen where something is running full screen in the foreground (the game), otherwise the
    /// one already in use, otherwise the main one. So the overlay follows the game, not the mouse.
    /// </summary>
    private static IntPtr ChooseMonitor(IntPtr self, IntPtr current)
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != self && GetWindowRect(foreground, out var rect))
        {
            var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONULL);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
                && rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top
                && rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom)
                return monitor;
        }
        return current != IntPtr.Zero ? current : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
    }

    /// <summary>Hands the picture, see-through pixels included, to the window and moves it in the same step.</summary>
    private static void Push(IntPtr hwnd, Bitmap bitmap, int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var handle = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memory, handle);
        try
        {
            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT();
            var target = new POINT { x = x, y = y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(hwnd, screen, ref target, ref size, memory, ref source, 0, ref blend, ULW_ALPHA))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(handle);
            DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Only the "currently down" bit: reading the key state does not take the key away from the game.
    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private const uint WS_POPUP = 0x80000000;
    internal const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const int ULW_ALPHA = 2;
    private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;
    private const int VK_CONTROL = 0x11, VK_F1 = 0x70;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
