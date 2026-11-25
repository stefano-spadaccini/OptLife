using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OptLife.Panel.ViewModels;

namespace OptLife.Panel;

public partial class MainWindow : Window
{
    private readonly PanelViewModel _viewModel;
    private readonly DispatcherTimer _refresh;

    public MainWindow(PanelViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Icon = DrawIcon();
        ApplyScale();

        // The agent writes to the history in the background; while the window is in view, follow it.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick += (_, _) =>
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            try
            {
                _viewModel.Refresh();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // The history is busy for a moment: the next tick will read it.
            }
        };
        _refresh.Start();

        PreviewKeyDown += OnShortcut;
        SizeChanged += (_, _) => { FollowWidth(); Spotlight(); };
        TourLayer.SizeChanged += (_, _) => Spotlight();
        PageArea.SizeChanged += (_, _) => Spotlight();
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            _viewModel.ZoomByCommand.Execute(e.Delta > 0 ? "0.1" : "-0.1");
            e.Handled = true;
        };
        viewModel.PageTopNeeded += () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ButtonsPage.ScrollToTop());
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PanelViewModel.TourHighlight) or nameof(PanelViewModel.ShowWelcome) or nameof(PanelViewModel.TourIndex):
                    // After the page has changed under the tour: then the part to light up is where it will stay.
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        Spotlight();
                        if (_viewModel.ShowWelcome) TourNextButton.Focus();
                    });
                    break;
                case nameof(PanelViewModel.Page) or nameof(PanelViewModel.SelectedView):
                    TopButton.Visibility = Visibility.Collapsed;
                    break;
                case nameof(PanelViewModel.Zoom):
                    ApplyScale();
                    break;
                case nameof(PanelViewModel.FocusedModule) when viewModel.FocusedModule is { } id:
                    // A search result in the settings: bring that module's card into view.
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        if (id.Length == 0) { SettingsScroll.ScrollToTop(); return; }
                        var item = viewModel.Modules.FirstOrDefault(m => m.Id == id);
                        if (item is not null && ModuleList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement card)
                            card.BringIntoView(new Rect(0, 0, card.ActualWidth, Math.Min(card.ActualHeight, 400)));
                    });
                    break;
            }
        };
        // Windows' text size changed (Settings → Accessibility → Text size): follow it.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(ApplyScale);
    }

    /// <summary>The panel is drawn at Windows' text size times the panel's own zoom.</summary>
    private void ApplyScale()
    {
        var scale = ThemeManager.TextScale() * _viewModel.Zoom;
        var transform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        Root.LayoutTransform = transform;
        Application.Current.Resources["UiScale"] = transform;
        FollowWidth();
    }

    /// <summary>A narrow window (or a big zoom): the side bar keeps only its icons, so the page keeps its room.</summary>
    private void FollowWidth()
    {
        var scale = ThemeManager.TextScale() * _viewModel.Zoom;
        _viewModel.NavNarrow = ActualWidth > 0 && ActualWidth / scale < NarrowBelow;
    }

    private const double NarrowBelow = 980;

    /// <summary>The keyboard: Ctrl+F search, Ctrl+1…9 the side bar, F5 check now, F1 help, Ctrl+plus/minus/0 zoom, Esc closes.</summary>
    private void OnShortcut(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl && key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (ctrl && key is >= Key.D1 and <= Key.D9)
            _viewModel.GoToEntryCommand.Execute($"{key - Key.D0}");
        else if (ctrl && key is >= Key.NumPad1 and <= Key.NumPad9)
            _viewModel.GoToEntryCommand.Execute($"{key - Key.NumPad0}");
        else if (ctrl && key is Key.OemPlus or Key.Add)
            _viewModel.ZoomByCommand.Execute("0.1");
        else if (ctrl && key is Key.OemMinus or Key.Subtract)
            _viewModel.ZoomByCommand.Execute("-0.1");
        else if (ctrl && key is Key.D0 or Key.NumPad0)
            _viewModel.ZoomByCommand.Execute("0");
        else if (key == Key.Home && (ctrl || Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase) && TopButton.IsVisible)
            ScrollToTop();
        else if (key == Key.F1 && _viewModel.HasHelp)
            _viewModel.ToggleHelpCommand.Execute(null);
        else if (key == Key.F5)
            _viewModel.RefreshPageCommand.Execute(null);
        else if (key == Key.Escape && (_viewModel.HelpOpen || _viewModel.BellOpen))
            (_viewModel.HelpOpen, _viewModel.BellOpen) = (false, false);
        else
            return;
        e.Handled = true;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.GlobalQuery = "";
                _viewModel.SearchOpen = false;
                e.Handled = true;
                break;
            case Key.Enter when _viewModel.SearchResults.Count > 0:
                _viewModel.OpenResultCommand.Execute(_viewModel.SearchResults[0]);
                e.Handled = true;
                break;
            case Key.Down when _viewModel.SearchResults.Count > 0:
                // Into the list: arrows and Enter move through the results.
                SearchList.Focus();
                (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The guided tour dims the window and leaves a hole, framed in the accent, around the part the step talks about
    /// (the side bar, the problems, the page, the bell). Nothing to point at: all dimmed, the card in the middle.
    /// </summary>
    private void Spotlight()
    {
        if (!_viewModel.ShowWelcome || TourLayer.ActualWidth <= 0) return;
        FrameworkElement? target = _viewModel.TourHighlight switch
        {
            "SideBar" => SideBar,
            "ProblemsEntry" => ProblemsEntry,
            "PageArea" => PageArea,
            "Bell" => Bell,
            _ => null,
        };
        var all = new RectangleGeometry(new Rect(0, 0, TourLayer.ActualWidth, TourLayer.ActualHeight));
        if (target is null || !target.IsVisible)
        {
            TourDim.Data = all;
            TourRing.Visibility = Visibility.Collapsed;
            return;
        }
        var hole = target.TransformToVisual(TourLayer).TransformBounds(new Rect(target.RenderSize));
        hole.Inflate(4, 4);
        TourDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(hole, 8, 8));
        System.Windows.Controls.Canvas.SetLeft(TourRing, hole.X);
        System.Windows.Controls.Canvas.SetTop(TourRing, hole.Y);
        (TourRing.Width, TourRing.Height) = (hole.Width, hole.Height);
        TourRing.Visibility = Visibility.Visible;
    }

    /// <summary>The page being scrolled: "Back to top" shows once it is more than a screen down.</summary>
    private System.Windows.Controls.ScrollViewer? _scrolled;

    private void PageArea_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Only the pages' own vertical scrolling counts (not a list box inside a setting, nor a pop-up's list).
        if (e.OriginalSource is not System.Windows.Controls.ScrollViewer sv || sv.ViewportHeight < 200) return;
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;
        _scrolled = sv;
        TopButton.Visibility = sv.VerticalOffset > sv.ViewportHeight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TopButton_Click(object sender, RoutedEventArgs e) => ScrollToTop();

    private void ScrollToTop()
    {
        if (_scrolled is null || !_scrolled.IsVisible) return;
        _scrolled.ScrollToTop();
        TopButton.Visibility = Visibility.Collapsed;
        // The keyboard goes back to the top too: to the page title's row.
        HelpButton.Focus();
    }

    // ── A module's live picture (the overlay editor): dragged with the mouse, or moved with the arrows ──

    private Point? _grab;

    private void PreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image image || image.DataContext is not ViewModels.ViewTab { CanDrag: true }) return;
        _grab = e.GetPosition(image);
        image.CaptureMouse();
        PreviewScreen.Focus();
        e.Handled = true;
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_grab is not { } grab || sender is not System.Windows.Controls.Image { DataContext: ViewModels.ViewTab tab }) return;
        var at = e.GetPosition(PreviewCanvas);
        tab.MovePreview((at.X - grab.X) / tab.ScreenWidth, (at.Y - grab.Y) / tab.ScreenHeight);
    }

    private void PreviewImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _grab = null;
        (sender as UIElement)?.ReleaseMouseCapture();
    }

    private void PreviewScreen_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.ViewTab { CanDrag: true } tab) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.05 : 0.01;
        var (dx, dy) = e.Key switch { Key.Left => (-step, 0d), Key.Right => (step, 0d), Key.Up => (0d, -step), Key.Down => (0d, step), _ => (0d, 0d) };
        if (dx == 0 && dy == 0) return;
        tab.MovePreview(tab.PreviewLeft / tab.ScreenWidth + dx, tab.PreviewTop / tab.ScreenHeight + dy);
        e.Handled = true;
    }

    // ── The order of the chosen values in a simple view: dragged, or moved with the arrows ──

    private void OrderChip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ViewModels.ListRow row } chip) return;
        if (e.OriginalSource is DependencyObject source && FindParent<System.Windows.Controls.Button>(source) is not null) return; // The arrows are buttons.
        DragDrop.DoDragDrop(chip, row, DragDropEffects.Move);
    }

    private void OrderChip_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ViewModels.ListRow)) is not ViewModels.ListRow dragged || sender is not FrameworkElement { DataContext: ViewModels.ListRow target }) return;
        dragged.Owner.MoveRowTo(dragged, dragged.Owner.Rows.IndexOf(target));
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? at) where T : DependencyObject
    {
        for (; at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is T found) return found;
        return null;
    }

    /// <summary>"Browse…" beside a folder setting: Windows' own folder picker; the folder joins the list and is chosen.</summary>
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.IPickTarget target) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Scegli la cartella", "Choose the folder") };
        if (target.PickedValue.Length > 2 && Directory.Exists(target.PickedValue)) dialog.InitialDirectory = target.PickedValue;
        if (dialog.ShowDialog(this) != true) return;
        ViewModels.Picks.Add(target.Picks, dialog.FolderName);
        target.PickedValue = dialog.FolderName;
    }

    /// <summary>"More" on a problem: the card's own menu (the one a right click opens), under the button.</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? at = (DependencyObject)sender; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is not FrameworkElement { ContextMenu: { } menu } card) continue;
            menu.PlacementTarget = card;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.HorizontalOffset = card.ActualWidth - 200;
            menu.IsOpen = true;
            return;
        }
    }

    /// <summary>
    /// "⋯" at the top of a gallery page: its module's buttons (bring back removed games, look again…) and the module's settings
    /// (folders, games added by hand, emulators chosen by hand), as a menu.
    /// </summary>
    private void GalleryPageMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || DataContext is not ViewModels.PanelViewModel { SelectedView: { } tab } vm) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in tab.ButtonGroups.SelectMany(g => g.Buttons))
        {
            var entry = new System.Windows.Controls.MenuItem { Header = item.Label, ToolTip = item.Description };
            entry.Click += (_, _) => vm.PressCommand.Execute(item);
            menu.Items.Add(entry);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        var settings = new System.Windows.Controls.MenuItem { Header = L.T("Cartelle, giochi aggiunti a mano, emulatori… (impostazioni)", "Folders, games added by hand, emulators… (settings)") };
        settings.Click += (_, _) => vm.OpenSettingsOfCommand.Execute(tab.ModuleId);
        menu.Items.Add(settings);
        menu.IsOpen = true;
    }

    /// <summary>"⋯" on a gallery card: its other buttons as a menu under it (keyboard and screen readers reach it like any menu).</summary>
    private void GalleryMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.GalleryCard card } button || DataContext is not ViewModels.PanelViewModel vm) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in card.More)
        {
            var entry = new System.Windows.Controls.MenuItem { Header = item.ButtonText };
            System.Windows.Automation.AutomationProperties.SetName(entry, $"{item.ButtonText} {card.Title}");
            entry.Click += (_, _) => vm.PressCommand.Execute(item);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private WindowLook _look = ThemeManager.Current;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.Applied += look =>
        {
            _look = look;
            PaintFrame();
        };
        PaintFrame();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(CaptionHook);
        StateChanged += (_, _) => FollowState();
        FollowState();
    }

    // ── The window's own title bar buttons ──────────────────────────────────

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Maximised, a window reaches a few pixels past the screen on every side: the page is moved in by as much, so nothing
    /// is cut and the title bar keeps its height. The maximise button turns into restore.
    /// </summary>
    private void FollowState()
    {
        var max = WindowState == WindowState.Maximized;
        var b = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = max ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4) : new Thickness(0);
        MaxGlyph.Text = max ? "\uE923" : "\uE922";
        var tip = max ? L.T("Ripristina", "Restore") : L.T("Ingrandisci", "Maximise");
        MaxButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, tip);
    }

    /// <summary>
    /// Windows 11 shows its snap layouts when the pointer rests on the maximise button: the window says that button is
    /// there (HTMAXBUTTON), and handles its click itself.
    /// </summary>
    private IntPtr CaptionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCMOUSELEAVE = 0x2A2, HTMAXBUTTON = 9;
        switch (msg)
        {
            case WM_NCHITTEST when OverMaxButton(lParam):
                handled = true;
                return HTMAXBUTTON;
            case WM_NCMOUSEMOVE:
                MaxButton.Tag = wParam.ToInt32() == HTMAXBUTTON;
                break;
            case WM_NCMOUSELEAVE:
                MaxButton.Tag = false;
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                MaxButton.Tag = false;
                MaxButton_Click(MaxButton, new RoutedEventArgs());
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        try
        {
            var local = MaxButton.PointFromScreen(screen);
            return local.X >= 0 && local.Y >= 0 && local.X < MaxButton.ActualWidth && local.Y < MaxButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The window frame in the theme: light or dark title bar, rounded corners, and either Windows' Mica material
    /// under a see-through page or a title bar painted in the theme's colour (those attributes need Windows 11; ignored elsewhere).
    /// </summary>
    private void PaintFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        static int Bgr(Color c) => c.R | c.G << 8 | c.B << 16;
        int dark = _look.Light ? 0 : 1, round = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));     // Immersive dark mode
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));    // Rounded corners
        var source = HwndSource.FromHwnd(handle);
        if (_look.Mica)
        {
            // The page is see-through and Windows draws Mica behind it and behind the title bar.
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var margins = new Margins(-1);
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
            int none = unchecked((int)0xFFFFFFFF), mica = 2;
            _ = DwmSetWindowAttribute(handle, 35, ref none, sizeof(int)); // Caption colour: the default, so Mica shows
            _ = DwmSetWindowAttribute(handle, 34, ref none, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 38, ref mica, sizeof(int)); // System backdrop: Mica
        }
        else
        {
            if (source?.CompositionTarget is { } target) target.BackgroundColor = _look.Caption;
            var margins = new Margins(0);
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
            int none = 1, caption = Bgr(_look.Caption), border = Bgr(_look.Border);
            _ = DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
    }

    /// <summary>Draws the window content into a PNG and closes the application (see App.OnStartup).</summary>
    public void SnapshotTo(string path)
    {
        _refresh.Stop();
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000;
        Top = 0;
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var root = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            // A see-through page (Mica) is drawn on the theme's solid colour, as Windows would show it.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(ThemeManager.Current.Caption), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            Application.Current.Shutdown();
        });
        Show();
    }

    /// <summary>The same white ring the notification-area icon uses.</summary>
    private static BitmapSource DrawIcon()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawEllipse(null, new Pen(Brushes.White, 10), new Point(32, 32), 21, 21);
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int all)
    {
        public int Left = all, Right = all, Top = all, Bottom = all;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
