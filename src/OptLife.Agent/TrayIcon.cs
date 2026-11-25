using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using OptLife.Core;
using OptLife.Core.Storage;

namespace OptLife.Agent;

/// <summary>
/// The icon beside the clock. A dot on it means there are open problems (its colour is the worst
/// severity); no Windows notifications are ever shown — activities get OptLife's own pop-up (ActivityWatcher).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Store _store;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private (int Count, Severity Worst)? _shown;

    public TrayIcon(Store store, Action checkNow, Action onExit)
    {
        _store = store;

        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Apri OptLife", "Open OptLife"), null, (_, _) => OpenPanel());
        menu.Items.Add(L.T("Controlla ora", "Check now"), null, (_, _) => checkNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Chiudi OptLife", "Close OptLife"), null, (_, _) => onExit());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenPanel();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += (_, _) => Refresh();
        Refresh();
        _timer.Start();
    }

    private void Refresh()
    {
        (int Count, Severity Worst) state;
        try
        {
            var open = _store.GetFindings();
            state = (open.Count, open.Count == 0 ? Severity.Info : open.Max(f => f.Severity));
        }
        catch (Exception)
        {
            return; // The database is busy for a moment: keep the icon as it is and retry at the next tick.
        }
        if (state == _shown) return;
        _shown = state;

        var old = _icon.Icon;
        _icon.Icon = Draw(state.Count > 0 ? BadgeColor(state.Worst) : null);
        _icon.Text = state.Count switch
        {
            0 => L.T("OptLife — tutto a posto", "OptLife — all good"),
            1 => L.T("OptLife — 1 problema aperto", "OptLife — 1 open problem"),
            _ => L.T($"OptLife — {state.Count} problemi aperti", $"OptLife — {state.Count} open problems"),
        };
        if (old is not null)
        {
            DestroyIcon(old.Handle);
            old.Dispose();
        }
    }

    private static Color BadgeColor(Severity worst) => worst switch
    {
        Severity.High => Color.FromArgb(0xE5, 0x2B, 0x3F),
        Severity.Medium => Color.FromArgb(0xE0, 0xA8, 0x2E),
        _ => Color.White,
    };

    /// <summary>A white ring (readable on dark and light taskbars thanks to its dark edge), plus an optional dot.</summary>
    private static Icon Draw(Color? badge)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var edge = new Pen(Color.FromArgb(200, 0, 0, 0), 8f)) g.DrawEllipse(edge, 6, 6, 20, 20);
            using (var ring = new Pen(Color.White, 5f)) g.DrawEllipse(ring, 6, 6, 20, 20);
            if (badge is { } color)
            {
                using var back = new SolidBrush(Color.Black);
                using var dot = new SolidBrush(color);
                g.FillEllipse(back, 15, 15, 17, 17);
                g.FillEllipse(dot, 17, 17, 13, 13);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void OpenPanel()
    {
        var panel = Path.Combine(AppContext.BaseDirectory, "optlife-panel.exe");
        if (File.Exists(panel)) Process.Start(new ProcessStartInfo(panel) { UseShellExecute = false });
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        if (_icon.Icon is { } icon) DestroyIcon(icon.Handle);
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
