using System.Diagnostics;
using System.Media;
using OptLife.Core;
using OptLife.Core.Appearance;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Core.Storage;

namespace OptLife.Agent;

/// <summary>
/// OptLife's own small pop-up above the clock, never a Windows notification: an activity started, or ended (with a short sound).
/// It does not take the keyboard, does not show in the taskbar, closes by itself; a click opens the panel. During a game nothing
/// shows: it is all said when the game is over.
/// </summary>
internal sealed class ActivityWatcher : IDisposable
{
    private readonly Store _store;
    private readonly ConfigStore _config;
    private readonly Action _openPanel;
    private readonly ActivityNotices _notices = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<PopupForm> _open = [];

    public ActivityWatcher(Store store, ConfigStore config, Action openPanel)
    {
        _store = store;
        _config = config;
        _openPanel = openPanel;
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Look();
        _timer.Start();
    }

    private void Look()
    {
        IReadOnlyList<ActivityNotice> notices;
        NotificationSettings settings;
        try
        {
            settings = _config.Load().Notifications;
            var playing = LiveBoard.Read(DateTimeOffset.Now).Any(e => e.Key.StartsWith("game.", StringComparison.Ordinal));
            notices = _notices.Next(_store.GetActivities(30), settings, DateTimeOffset.Now, playing);
        }
        catch (Exception)
        {
            return; // The history or the config is busy for a moment: the next look reads it.
        }
        // Many at once (after a game): the last few, the rest are on the Activity page.
        foreach (var notice in notices.TakeLast(3)) Show(notice, settings.Sound);
    }

    private void Show(ActivityNotice notice, bool sound)
    {
        _open.RemoveAll(f => f.IsDisposed);
        // Read at every pop-up: a theme, accent or text size changed in the panel shows at once, with no restart.
        Dictionary<string, string> colors;
        double scale;
        try
        {
            var ui = _config.Load().Ui;
            colors = WindowsLook.Chosen(ui);
            scale = WindowsLook.TextScale() * WindowsLook.Zoom(ui);
        }
        catch (Exception)
        {
            colors = WindowsLook.Chosen(new Dictionary<string, string>());
            scale = 1;
        }
        var popup = new PopupForm(notice, _openPanel, colors, scale);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        var below = _open.Count == 0 ? area.Bottom : _open.Min(f => f.Top);
        popup.Location = new Point(area.Right - popup.Width - 12, below - popup.Height - 12);
        popup.FormClosed += (_, _) => _open.Remove(popup);
        _open.Add(popup);
        popup.Show();
        if (sound && notice.Ended)
            (notice.State == ActivityState.Done ? SystemSounds.Asterisk : SystemSounds.Exclamation).Play();
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var f in _open.ToList()) f.Close();
    }

    /// <summary>A small card in the panel's theme: title, one line, a coloured edge (accent: started, green: done, amber: not done).</summary>
    private sealed class PopupForm : Form
    {
        private readonly System.Windows.Forms.Timer _life = new() { Interval = 8000 };

        public PopupForm(ActivityNotice notice, Action open, IReadOnlyDictionary<string, string> theme, double scale)
        {
            Color C(string token) { var (r, g, b) = ThemeCatalog.Rgb(theme[token]); return Color.FromArgb(r, g, b); }
            int S(double v) => (int)Math.Round(v * scale);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = C("Surface.Flyout");
            Size = new Size(S(380), S(92));
            Cursor = Cursors.Hand;
            _edge = C("Outline.Strong");
            var edge = new Panel
            {
                Dock = DockStyle.Left, Width = S(5),
                BackColor = notice.State switch
                {
                    ActivityState.Done when notice.Ended => C("Good"),
                    ActivityState.Running => C("Accent"),
                    _ => C("Warning"),
                },
            };
            var title = new Label
            {
                Text = notice.Title, ForeColor = C("Text.Primary"), Font = new Font("Segoe UI Semibold", (float)(10.5 * scale)), AutoEllipsis = true,
                Location = new Point(S(18), S(12)), Size = new Size(S(300), S(22)),
            };
            var text = new Label
            {
                Text = notice.Text, ForeColor = C("Text.Secondary"), Font = new Font("Segoe UI", (float)(9 * scale)), AutoEllipsis = true,
                Location = new Point(S(18), S(36)), Size = new Size(S(350), S(46)),
            };
            var by = new Label
            {
                Text = "OptLife", ForeColor = C("Text.Muted"), Font = new Font("Segoe UI", (float)(7.5 * scale)), AutoSize = true,
                Location = new Point(S(326), S(13)), BackColor = Color.Transparent,
            };
            Controls.AddRange([edge, by, title, text]);
            foreach (Control c in Controls) c.Click += (_, _) => { open(); Close(); };
            Click += (_, _) => { open(); Close(); };
            _life.Tick += (_, _) => Close();
            _life.Start();
        }

        private readonly Color _edge;

        /// <summary>A thin frame in the theme and, on Windows 11, rounded corners like the panel.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(_edge);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var round = 2;
            _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>Shown without taking the keyboard away from what the person is doing.</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return p;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _life.Dispose();
            base.Dispose(disposing);
        }
    }
}
