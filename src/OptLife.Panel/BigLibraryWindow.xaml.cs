using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OptLife.Panel.ViewModels;

namespace OptLife.Panel;

/// <summary>
/// The library full screen, for the sofa: controller (XInput), keyboard or mouse. It reads the controller only while it is in front,
/// hides when a game starts and comes back when the game is over.
/// </summary>
public partial class BigLibraryWindow : Window
{
    private readonly BigLibraryViewModel _vm;
    private readonly Func<bool> _gameRunning;
    private readonly GamepadMapper _pad = new();
    private readonly SonyPad _sony = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DateTime _hiddenAt;
    private bool _sawGame, _busy;

    public static string ExitText => BigLibraryViewModel.ExitQuestion;

    /// <summary>Reads the gallery again from the history (set by the application).</summary>
    public Action? Refresh { get; set; }

    public BigLibraryWindow(BigLibraryViewModel vm, Func<bool> gameRunning)
    {
        InitializeComponent();
        _vm = vm;
        _gameRunning = gameRunning;
        DataContext = vm;
        vm.Tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewTab.ShownItems)) vm.Reload();
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BigLibraryViewModel.Selected)) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ShowSelected);
            if (e.PropertyName == nameof(BigLibraryViewModel.FilterIndex)) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ShowFilter);
        };
        _poll.Tick += (_, _) => ReadPad();
        _watch.Tick += (_, _) => WatchGame();
        Activated += (_, _) => _poll.Start();
        Deactivated += (_, _) => _poll.Stop();   // The game, or any other window, has the controller to itself.
        Closed += (_, _) => _sony.Dispose();
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnText;
        Loaded += (_, _) => { UpdateClock(); ShowSelected(); _vm.Pad = DetectPad(); FitColumns(); };
        RowsScroll.SizeChanged += (_, _) => FitColumns();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        // The clock, and every half minute the library read again (new covers, times played), while the view is in front.
        var ticks = 0;
        tick.Tick += (_, _) =>
        {
            UpdateClock();
            if (++ticks % 2 == 0 && IsActive && WindowState != WindowState.Minimized) Refresh?.Invoke();
        };
        tick.Start();
    }

    /// <summary>As many cards per row as the grid's width holds (card 196 + margins 16), at least three.</summary>
    private void FitColumns()
    {
        var columns = Math.Max(3, (int)((RowsScroll.ActualWidth - 24) / 212));
        if (columns == _vm.PerRow || RowsScroll.ActualWidth <= 0) return;
        _vm.PerRow = columns;
        _vm.Reload();
    }

    private void UpdateClock() => Clock.Text = DateTime.Now.ToString("HH:mm", L.Culture);

    // ── Which controller: its own buttons in the legend ──

    private long _padCheckedAt = -10_000;
    private PadKind _connected = PadKind.Xbox;

    /// <summary>
    /// A PlayStation (Sony, 054C) or Nintendo (057E) controller among the connected input devices; otherwise an Xbox one (XInput).
    /// Read from Windows' list of input devices, at most every 3 seconds.
    /// </summary>
    private PadKind DetectPad()
    {
        if (_clock.ElapsedMilliseconds - _padCheckedAt < 3000) return _connected;
        _padCheckedAt = _clock.ElapsedMilliseconds;
        var kind = PadKind.Xbox;
        try
        {
            uint count = 0;
            var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(null, ref count, size) == 0 && count > 0)
            {
                var list = new RAWINPUTDEVICELIST[count];
                if (GetRawInputDeviceList(list, ref count, size) != uint.MaxValue)
                    foreach (var d in list.Where(d => d.dwType == RIM_TYPEHID))
                    {
                        uint chars = 0;
                        GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, null, ref chars);
                        if (chars == 0) continue;
                        var name = new System.Text.StringBuilder((int)chars);
                        GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, name, ref chars);
                        var text = name.ToString();
                        if (text.Contains("VID_054C", StringComparison.OrdinalIgnoreCase)) { kind = PadKind.PlayStation; break; }
                        if (text.Contains("VID_057E", StringComparison.OrdinalIgnoreCase)) kind = PadKind.Nintendo;
                    }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return _connected = kind;
    }

    private const uint RIM_TYPEHID = 2, RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, System.Text.StringBuilder? data, ref uint size);

    // ── Controller ───────────────────────────────────────────────────────────

    private void ReadPad()
    {
        if (_busy || !IsActive || WindowState == WindowState.Minimized) return;
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) != 0) continue;
            var inputs = _pad.Step(state.Gamepad.wButtons, state.Gamepad.sThumbLX, state.Gamepad.sThumbLY, _clock.ElapsedMilliseconds);
            // Used now: the legend shows this controller's buttons.
            if (inputs.Count > 0 && _vm.Pad is PadKind.Keyboard or PadKind.Xbox) _vm.Pad = DetectPad();
            foreach (var input in inputs) _ = Do(input);
            return;   // The first controller connected drives the view.
        }
        // No Xbox-style controller: a PlayStation one read directly (with Steam Input on, it is already an Xbox one above, and
        // reading both would move twice).
        if (_sony.Read() is { } sony)
        {
            var inputs = _pad.Step(sony.Buttons, sony.X, sony.Y, _clock.ElapsedMilliseconds);
            if (inputs.Count > 0) _vm.Pad = PadKind.PlayStation;
            foreach (var input in inputs) _ = Do(input);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // With the on-screen keyboard open, a real keyboard types too.
        if (_vm.KeyboardOpen && e.Key == Key.Back) { _vm.Type(BigLibraryViewModel.Delete); e.Handled = true; return; }
        if (_vm.KeyboardOpen && e.Key == Key.Space) { _vm.Type(BigLibraryViewModel.Space); e.Handled = true; return; }
        BigInput? input = e.Key switch
        {
            Key.Up => BigInput.Up,
            Key.Down => BigInput.Down,
            Key.Left => BigInput.Left,
            Key.Right => BigInput.Right,
            Key.Enter or Key.Space => BigInput.Accept,
            Key.Apps or Key.F10 => BigInput.Menu,
            Key.F2 or Key.Tab => BigInput.Filters,
            Key.Back => BigInput.Back,
            // Esc goes back from a layer; on the grid it asks to leave.
            Key.Escape => _vm.MenuOpen || _vm.FiltersOpen || _vm.KeyboardOpen || _vm.ExitOpen || _vm.HelpOpen ? BigInput.Back : BigInput.Exit,
            Key.PageUp => BigInput.PreviousRow,
            Key.PageDown => BigInput.NextRow,
            Key.F1 => BigInput.Help,
            Key.F when Keyboard.Modifiers == ModifierKeys.Control => BigInput.Search,
            _ => null,
        };
        if (input is null) return;
        e.Handled = true;
        _vm.Pad = PadKind.Keyboard;
        _ = Do(input.Value);
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (!_vm.KeyboardOpen || e.Text.Length == 0 || char.IsControl(e.Text[0])) return;
        _vm.Type(e.Text);
        e.Handled = true;
    }

    private async Task Do(BigInput input)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            switch (await _vm.Handle(input))
            {
                case BigOutcome.Close:
                    Close();
                    break;
                case BigOutcome.Launched when !_vm.Tab.NoticeIsError:
                    HideForGame();
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    // ── While a game runs ────────────────────────────────────────────────────

    private void HideForGame()
    {
        _hiddenAt = DateTime.UtcNow;
        _sawGame = false;
        WindowState = WindowState.Minimized;
        _watch.Start();
    }

    /// <summary>
    /// Back when the game is over: OptLife sees a game while it runs (values game.* on the live board), and once it is gone the view
    /// returns. A game OptLife never sees within two minutes (not among the recognised games) leaves the view minimised.
    /// </summary>
    private void WatchGame()
    {
        var running = _gameRunning();
        if (running) { _sawGame = true; return; }
        if (_sawGame || DateTime.UtcNow - _hiddenAt > TimeSpan.FromMinutes(2))
        {
            _watch.Stop();
            if (!_sawGame) return;
            WindowState = WindowState.Maximized;
            Activate();
        }
    }

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BigCard card }) return;
        if (_vm.Selected == card) _ = Do(BigInput.Accept);
        else _vm.Choose(card);
    }

    /// <summary>An entry of the game's menu or of the filters, clicked: moved to, then applied.</summary>
    private async void Choice_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BigChoice item } || item.IsHeading) return;
        var list = _vm.MenuOpen ? _vm.Menu : _vm.Filters;
        int Index() => _vm.MenuOpen ? _vm.MenuIndex : _vm.FilterIndex;
        for (var guard = 0; guard < 100 && list[Index()] != item; guard++)
            await _vm.Handle(list.ToList().IndexOf(item) > Index() ? BigInput.Down : BigInput.Up);
        await Do(BigInput.Accept);
    }

    private void Key_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BigChoice key }) _vm.Type(key.Text);
    }

    /// <summary>Keeps the chosen card in sight, in its row and in the page.</summary>
    private void ShowSelected()
    {
        if (_vm.Selected is not { } card) return;
        foreach (var element in Descendants(this).OfType<FrameworkElement>())
            if (element.Tag == card && element.Name == "Frame")
            {
                element.BringIntoView(new Rect(-40, -60, element.ActualWidth + 80, element.ActualHeight + 120));
                return;
            }
    }

    private void ShowFilter()
    {
        if (!_vm.FiltersOpen || _vm.FilterIndex >= _vm.Filters.Count) return;
        var chosen = _vm.Filters[_vm.FilterIndex];
        foreach (var element in Descendants(FiltersScroll).OfType<FrameworkElement>())
            if (element.Tag == chosen) { element.BringIntoView(); return; }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    public void SnapshotTo(string path, BigInput[]? first = null)
    {
        WindowState = WindowState.Normal;
        Width = 1920;
        Height = 1080;
        Show();
        foreach (var input in first ?? []) _vm.Handle(input).GetAwaiter().GetResult();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    // ── XInput (part of Windows: no driver, no administrator) ───────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XInputState state);
}
