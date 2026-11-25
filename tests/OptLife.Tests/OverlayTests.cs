using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Modules.Overlay;
using OptLife.Modules.Overlay.Windows;

namespace OptLife.Tests;

/// <summary>A screen that only remembers what it was told, and a key pressed when the test says so.</summary>
internal sealed class FakeOverlayEnvironment : IOverlayEnvironment
{
    public OverlayFrame? Shown { get; private set; }
    public bool PressKey { get; set; }
    public int WatchedKey { get; private set; }

    public bool TogglePressed(int functionKey)
    {
        WatchedKey = functionKey;
        var pressed = PressKey;
        PressKey = false;
        return pressed;
    }

    public void Show(OverlayFrame frame) => Shown = frame;

    public void Hide() => Shown = null;
}

public class OverlayModuleTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly LiveBoard _board = new($@"Local\OptLife.Test.{Guid.NewGuid():N}");
    private readonly ModuleRunner _runner;
    private readonly FakeOverlayEnvironment _env = new();
    private readonly OverlayModule _module;

    public OverlayModuleTests()
    {
        _runner = new ModuleRunner(_box.Store, _box.Config, _box.Clock, _board);
        _module = new OverlayModule(_env);
    }

    public void Dispose()
    {
        _board.Dispose();
        _box.Dispose();
    }

    private async Task Tick() =>
        Assert.True(await _runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None),
            _runner.ReadStatus(_module, _module.Tasks[0]).LastError);

    private void Sensors() => _board.Publish("monitor",
    [
        new LiveValue("net.internet.ms", "Internet · latenza", "Rete", 8.4, "ms"),
        new LiveValue("net.internet.loss", "Internet · pacchetti persi", "Rete", 0, "%"),
        new LiveValue("gpu.temp", "Temperatura", "Scheda video", 53, "°C"),
        new LiveValue("ram.used_pct", "RAM occupata", "Memoria", 79.2, "%"),
    ], _box.Clock.Now);

    private void Game(bool running) => _board.Publish("games", running
        ?
        [
            new LiveValue("game.fps", "Frame al secondo", "Gioco", 141.6, "fps"),
            new LiveValue("game.fps_low", "FPS più bassi (ultimi 5 s)", "Gioco", 96, "fps"),
            new LiveValue("game.fps_drop", "Calo dei frame (ultimi 5 s)", "Gioco", 32.4, "%"),
            new LiveValue("game.ping_max", "Ping peggiore (ultimi 5 s)", "Gioco", 120, "ms"),
            new LiveValue("game.lost", "Pacchetti persi (ultimi 5 s)", "Gioco", 0, ""),
            new LiveValue("game.paging", "Memoria riletta dal disco (ultimi 5 s)", "Gioco", 35.2, "MB/s"),
            new LiveValue("game.minutes", "In gioco da", "Gioco", 3, "min"),
        ]
        : [], _box.Clock.Now);

    private void Set(string key, object value) =>
        _box.Config.UpdateModule("overlay", c => c.Settings[key] = JsonSerializer.SerializeToElement(value));

    [Fact]
    public async Task Outside_a_game_it_stays_out_of_the_way()
    {
        Sensors();

        await Tick();

        Assert.Null(_env.Shown);
    }

    [Fact]
    public async Task During_a_game_it_shows_the_chosen_values_in_the_chosen_order()
    {
        Sensors();
        Game(running: true);

        await Tick();

        // cpu.temp is in the default list but nobody publishes it here: it is left out, not shown empty.
        // The frame drop, the ping and the paging are beyond their limits, so they are flagged; the rest is within.
        Assert.Equal(
            [
                new("FPS", "142"), new("CALO", "32%", Alert: true), new("PING", "120 ms", Alert: true), new("PERSI", "0"),
                new("SWAP", "35 MB/s", Alert: true), new("GPU", "53°C"), new("RAM", "79%"),
            ],
            _env.Shown!.Rows);
        Assert.Equal(new OverlayStyle(OverlayCorner.TopRight, Arrangement: OverlayArrangement.Column, Margin: 16, ScalePercent: 100, BackgroundPercent: 60), _env.Shown.Style);
    }

    [Fact]
    public async Task It_goes_away_when_the_game_closes()
    {
        Sensors();
        Game(running: true);
        await Tick();
        Game(running: false);

        await Tick();

        Assert.Null(_env.Shown);
    }

    [Fact]
    public async Task The_key_hides_it_for_this_game_only()
    {
        Sensors();
        Game(running: true);
        await Tick();

        _env.PressKey = true;
        await Tick();
        Assert.Null(_env.Shown);
        await Tick();
        Assert.Null(_env.Shown); // Stays hidden without the key being held.

        Game(running: false);
        await Tick();
        Game(running: true);
        await Tick();
        Assert.NotNull(_env.Shown); // A new game starts from the automatic behaviour again.
    }

    [Fact]
    public async Task The_key_shows_it_on_the_desktop_too_and_a_second_press_hides_it()
    {
        Sensors();
        await Tick();

        _env.PressKey = true;
        await Tick();
        Assert.Equal("GPU", _env.Shown!.Rows[0].Label); // No game: only what the sensors publish.
        Assert.Equal(8, _env.WatchedKey);

        _env.PressKey = true;
        await Tick();
        Assert.Null(_env.Shown);
    }

    [Fact]
    public async Task It_can_be_left_always_on()
    {
        Sensors();
        Set("onlyInGame", false);

        await Tick();

        Assert.NotNull(_env.Shown);
    }

    [Fact]
    public async Task With_no_key_chosen_the_keyboard_is_not_watched()
    {
        Sensors();
        Set("toggleKey", 0);
        _env.PressKey = true;

        await Tick();

        Assert.Equal(0, _env.WatchedKey);
        Assert.Null(_env.Shown);
    }

    [Fact]
    public async Task Position_layout_size_and_background_come_from_the_settings()
    {
        Sensors();
        Game(running: true);
        Set("position", "basso a sinistra");
        Set("layout", "riga");
        Set("scale", 150);
        Set("background", 0);
        Set("margin", 40);
        Set("items", new[] { "net.internet.loss=persi", "gpu.temp" });

        await Tick();

        Assert.Equal(new OverlayStyle(OverlayCorner.BottomLeft, Arrangement: OverlayArrangement.Row, Margin: 40, ScalePercent: 150, BackgroundPercent: 0), _env.Shown!.Style);
        // Without a label of its own a value keeps the one it was published with.
        Assert.Equal([new("PERSI", "0%"), new("TEMPERATURA", "53°C")], _env.Shown.Rows);
    }

    [Fact]
    public async Task A_limit_can_warn_above_or_below_and_with_or_without_a_label()
    {
        Sensors();
        Game(running: true);
        Set("items", new[] { "game.fps=FPS<100", "game.fps_low=MIN<100", "gpu.temp>50", "ram.used_pct=RAM > 79,5", "net.internet.ms=PING>abc" });

        await Tick();

        Assert.Equal(
            [
                new("FPS", "142"), new("MIN", "96 fps", Alert: true), new("TEMPERATURA", "53°C", Alert: true),
                new("RAM", "79%"), new("PING>ABC", "8 ms"), // Not a number after the sign: it is just part of the label.
            ],
            _env.Shown!.Rows);
    }

    [Fact]
    public async Task With_nothing_to_show_there_is_no_empty_box()
    {
        Game(running: true);
        Set("items", new[] { "valore.che.non.esiste=X" });

        await Tick();

        Assert.Null(_env.Shown);
    }

    [Fact]
    public async Task Run_by_hand_outside_the_agent_it_finds_no_live_values_and_does_nothing()
    {
        Set("onlyInGame", false);

        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None));

        Assert.Null(_env.Shown);
    }
}

public class ChoiceSettingTests
{
    private static readonly SettingDefinition Corner = SettingDefinition.Choice("corner", "Angolo", "", "alto", "basso");

    private static ModuleSettings With(object? stored) => new([Corner],
        stored is null ? [] : new Dictionary<string, JsonElement> { ["corner"] = JsonSerializer.SerializeToElement(stored) });

    [Fact]
    public void The_first_option_is_the_default()
    {
        Assert.Equal("alto", With(null).GetChoice("corner"));
        Assert.Equal("basso", With("basso").GetChoice("corner"));
    }

    [Fact]
    public void A_stored_value_that_is_no_longer_an_option_falls_back_to_the_default()
    {
        Assert.Equal("alto", With("di lato").GetChoice("corner"));
        Assert.Equal("alto", With(3).GetChoice("corner"));
    }

    [Fact]
    public void Typed_text_must_be_one_of_the_options()
    {
        Assert.Equal("basso", ModuleSettings.Parse(Corner, " BASSO ").GetString());
        var error = Assert.Throws<FormatException>(() => ModuleSettings.Parse(Corner, "di lato"));
        Assert.Contains("alto, basso", error.Message);
    }
}

public class OverlayRendererTests
{
    private static readonly OverlayRow[] Rows =
        [new("FPS", "142"), new("PING", "8 ms"), new("PERSI", "0%"), new("GPU", "53°C"), new("CPU", "58°C"), new("RAM", "79%")];

    private static OverlayFrame Frame(bool inRow = false, int background = 60) =>
        new(Rows, new OverlayStyle(OverlayCorner.TopRight, inRow ? OverlayArrangement.Row : OverlayArrangement.Column, 16, 100, background));

    [Fact]
    public void A_column_is_tall_and_narrow_and_a_row_is_wide_and_short()
    {
        using var column = OverlayRenderer.Render(Frame(), 1);
        using var row = OverlayRenderer.Render(Frame(inRow: true), 1);

        Assert.InRange(column.Width, 80, 200);
        Assert.InRange(column.Height, 6 * 18, 6 * 26);
        Assert.True(row.Width > column.Width * 2);
        Assert.True(row.Height < column.Height / 3);
        Save(column, "colonna");
        Save(row, "riga");
    }

    [Fact]
    public void Size_follows_the_scale()
    {
        using var normal = OverlayRenderer.Render(Frame(), 1);
        using var doubled = OverlayRenderer.Render(Frame(), 2);

        Assert.InRange(doubled.Width, normal.Width * 2 - 6, normal.Width * 2 + 6);
        Assert.InRange(doubled.Height, normal.Height * 2 - 2, normal.Height * 2 + 2);
    }

    [Fact]
    public void The_background_is_as_see_through_as_asked_and_the_text_is_solid()
    {
        using var half = OverlayRenderer.Render(Frame(background: 60), 1);
        using var none = OverlayRenderer.Render(Frame(background: 0), 1);
        using var full = OverlayRenderer.Render(Frame(background: 100), 1);

        // A spot between the two columns of the first row, where there is no text.
        Assert.InRange(half.GetPixel(4, 3).A, 150, 156);
        Assert.Equal(0, none.GetPixel(4, 3).A);
        Assert.Equal(255, full.GetPixel(4, 3).A);
        Assert.True(HasWhite(none), "the numbers must be drawn in solid white even with no background");
        Save(none, "senza-sfondo");
    }

    [Fact]
    public void A_value_beyond_its_limit_is_drawn_in_the_warning_colour()
    {
        OverlayRow[] rows = [new("FPS", "142"), new("PING", "120 ms", Alert: true), new("SWAP", "35 MB/s", Alert: true), new("RAM", "79%")];
        var style = new OverlayStyle(OverlayCorner.TopRight, OverlayArrangement.Column, 16, 100, 60);
        using var calm = OverlayRenderer.Render(new OverlayFrame([.. rows.Select(r => r with { Alert = false })], style), 1);
        using var alarmed = OverlayRenderer.Render(new OverlayFrame(rows, style), 1);

        static bool HasAmber(Bitmap b)
        {
            for (var y = 0; y < b.Height; y++)
                for (var x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y) is { A: 255, R: > 240, G: > 150 and < 200, B: < 80 }) return true;
            return false;
        }
        Assert.False(HasAmber(calm));
        Assert.True(HasAmber(alarmed));
        Assert.Equal(calm.Size, alarmed.Size); // Colour only: nothing moves when a warning appears.
        Save(alarmed, "allarme");
    }

    private static bool HasWhite(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p is { A: 255, R: > 240, G: > 240, B: > 240 }) return true;
            }
        return false;
    }

    /// <summary>Set OPTLIFE_OVERLAY_PNG to a folder to keep the pictures and look at them.</summary>
    private static void Save(Bitmap bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("OPTLIFE_OVERLAY_PNG") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        // Over a mid-grey "scene", three times larger, so the transparency and the edges can be judged by eye.
        using var preview = new Bitmap(bitmap.Width * 3 + 60, bitmap.Height * 3 + 60, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(preview))
        {
            using var scene = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, preview.Width, preview.Height), Color.FromArgb(40, 70, 110), Color.FromArgb(235, 225, 190), 20f);
            g.FillRectangle(scene, 0, 0, preview.Width, preview.Height);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(bitmap, new Rectangle(30, 30, bitmap.Width * 3, bitmap.Height * 3));
        }
        preview.Save(Path.Combine(folder, $"overlay-{name}.png"), ImageFormat.Png);
    }
}

public class OverlayEndToEndTests
{
    /// <summary>
    /// The real window on the real screen for a few seconds: on top, click-through, in the chosen
    /// corner, and gone by itself when nobody refreshes it. It shows something on the monitor, so it
    /// runs only when asked: set OPTLIFE_E2E=1 (and OPTLIFE_OVERLAY_PNG to keep a picture of that corner).
    /// </summary>
    [Fact]
    public async Task The_real_window_appears_in_its_corner_above_everything_and_lets_clicks_through()
    {
        if (Environment.GetEnvironmentVariable("OPTLIFE_E2E") != "1") return;

        var window = new OverlayWindow();
        var frame = new OverlayFrame(
            [new("FPS", "142"), new("PING", "8 ms"), new("PERSI", "0%"), new("GPU", "53°C")],
            new OverlayStyle(OverlayCorner.TopRight, Arrangement: OverlayArrangement.Column, Margin: 16, ScalePercent: 100, BackgroundPercent: 60));

        for (var i = 0; i < 8; i++)
        {
            window.Show(frame);
            await Task.Delay(250);
        }

        var hwnd = window.Handle;
        Assert.NotEqual(IntPtr.Zero, hwnd);
        Assert.True(IsWindowVisible(hwnd));
        var ex = GetWindowLong(hwnd, -20);
        foreach (var flag in new[] { OverlayWindow.WS_EX_LAYERED, OverlayWindow.WS_EX_TRANSPARENT, OverlayWindow.WS_EX_TOPMOST, OverlayWindow.WS_EX_TOOLWINDOW, OverlayWindow.WS_EX_NOACTIVATE })
            Assert.True((ex & flag) != 0, $"missing window style 0x{flag:X}");
        Assert.NotEqual(hwnd, GetForegroundWindow()); // It never takes the keyboard.

        Assert.True(GetWindowRect(hwnd, out var rect));
        var screen = System.Windows.Forms.Screen.AllScreens.Single(s => s.Bounds.Contains(rect.Left, rect.Top));
        Assert.Equal(screen.Bounds.Right - 16, rect.Right);
        Assert.Equal(screen.Bounds.Top + 16, rect.Top);
        Assert.InRange(rect.Right - rect.Left, 60, 300);

        if (Environment.GetEnvironmentVariable("OPTLIFE_OVERLAY_PNG") is { Length: > 0 } folder)
        {
            // Only the corner the overlay is in, nothing else of the screen.
            Directory.CreateDirectory(folder);
            var area = Rectangle.FromLTRB(rect.Left - 30, rect.Top - 10, rect.Right + 10, rect.Bottom + 30);
            using var shot = new Bitmap(area.Width, area.Height);
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            shot.Save(Path.Combine(folder, "overlay-schermo.png"), ImageFormat.Png);
        }

        // Moving it: same window, other corner, on one line.
        var moved = frame with { Style = frame.Style with { Corner = OverlayCorner.BottomLeft, Arrangement = OverlayArrangement.Row } };
        for (var i = 0; i < 4; i++)
        {
            window.Show(moved);
            await Task.Delay(250);
        }
        Assert.True(GetWindowRect(hwnd, out var after));
        Assert.Equal(screen.Bounds.Left + 16, after.Left);
        Assert.Equal(screen.Bounds.Bottom - 16, after.Bottom);

        // Nobody refreshes it any more (the module was switched off): it must leave by itself.
        await Task.Delay(4000);
        Assert.False(IsWindowVisible(hwnd));

        window.Show(frame);
        await Task.Delay(400);
        Assert.True(IsWindowVisible(hwnd));
        window.Hide();
        await Task.Delay(400);
        Assert.False(IsWindowVisible(hwnd));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}

public sealed class OverlayEditorTests
{
    private static LiveValue V(string key, double value, string unit = "", string label = "") => new(key, label, "", value, unit);

    [Fact]
    public void Older_lines_are_read_in_the_new_columns_and_keep_their_limit()
    {
        Assert.Equal("game.ping_max | PING | >80 | ", OverlayLayout.Upgrade("game.ping_max=PING>80"));
        Assert.Equal("game.fps | FPS |  | ", OverlayLayout.Upgrade("game.fps=FPS"));
        Assert.Equal("cpu.temp | CPU | >90 | #FF0000", OverlayLayout.Upgrade("cpu.temp | CPU | >90 | #FF0000"));
        var (key, label, limit, above, color) = OverlayLayout.Parse("fps | FPS | <100 | ff0000");
        Assert.Equal(("fps", "FPS", 100d, false, "#FF0000"), (key, label, limit, above, color));
    }

    [Fact]
    public void A_game_profile_shows_only_its_values_in_its_order_with_their_labels()
    {
        var settings = new ModuleSettings(new OverlayModule().Settings, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["items"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "game.fps | FPS | | ", "game.ping_max | PING | >80 | #FF0000", "gpu.temp | GPU | | " }),
            ["myPresets"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "Competitive | game.ping_max, game.fps | Overwatch®", "Manual only | gpu.temp | " }),
        });
        var live = new[] { V("game.minutes", 12, "min", "Overwatch · in gioco da"), V("game.fps", 144), V("game.ping_max", 95, "ms"), V("gpu.temp", 60, "°C") };
        Assert.Equal("Overwatch", OverlayLayout.GameName(live));
        var rows = OverlayLayout.Rows(OverlayLayout.ItemsFor(settings, OverlayLayout.GameName(live)), live);
        Assert.Equal(["PING", "FPS"], rows.Select(r => r.Label));
        Assert.True(rows[0].Alert);
        Assert.Equal("#FF0000", rows[0].Color);
        // Another game: the main list.
        Assert.Equal(3, OverlayLayout.ItemsFor(settings, "Rocket League").Count);
    }

    [Fact]
    public void The_text_edge_has_its_own_colour_and_thickness()
    {
        var settings = new ModuleSettings(new OverlayModule().Settings, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["outlineColor"] = System.Text.Json.JsonSerializer.SerializeToElement("#ff0000"),
            ["outlineWidth"] = System.Text.Json.JsonSerializer.SerializeToElement(3),
        });
        var style = OverlayModule.StyleOf(settings);
        Assert.Equal("#FF0000", style.OutlineColor);
        Assert.Equal(3, style.OutlineWidth);
    }

    [Fact]
    public void A_free_position_stays_on_the_screen_and_a_corner_keeps_its_margin()
    {
        var free = new OverlayStyle(OverlayCorner.Free, OverlayArrangement.Column, 16, 100, 60, X: 95, Y: 50);
        Assert.Equal((1720, 540), OverlayLayout.Place(free, 200, 100, 1920, 1080)); // Kept inside the right edge.
        var corner = new OverlayStyle(OverlayCorner.BottomLeft, OverlayArrangement.Column, 16, 100, 60);
        Assert.Equal((32, 1080 - 32 - 100), OverlayLayout.Place(corner, 200, 100, 1920, 1080, dpiScale: 2));
    }

    [Fact]
    public void The_editor_draws_a_preview_with_dashes_for_values_only_in_a_game()
    {
        var view = new OverlayModule().Views.Single();
        Assert.Equal(ViewLayout.Form, view.Layout);
        var settings = new ModuleSettings(new OverlayModule().Settings, new Dictionary<string, System.Text.Json.JsonElement>());
        var preview = view.Preview!(settings, [V("gpu.temp", 61, "°C")]);
        Assert.NotNull(preview);
        Assert.True(preview!.Png.Length > 100);
        Assert.Contains("FPS —", preview.Description);
        Assert.Contains("GPU 61°C", preview.Description);
        Assert.Equal(("x", "y"), (preview.DragX, preview.DragY));
        Assert.True(preview.X is > 0.5 and < 1); // Top right by default.
    }
}
