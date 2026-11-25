using OptLife.Core.Appearance;
using System.Windows;
using System.Windows.Media;
using OptLife.Panel.ViewModels;

namespace OptLife.Panel;

/// <summary>How the window itself must look for the theme on screen: light or dark frame, Mica or a solid colour.</summary>
public readonly record struct WindowLook(bool Light, Color Caption, Color Border, bool Mica);

/// <summary>
/// Puts a theme on screen: every token becomes a brush in the application's resources, which override the
/// ones of Themes/Mono.xaml. Styles read them with DynamicResource, so the whole window follows at once.
/// With Windows' high contrast on, Windows' own colours win over any theme.
/// </summary>
public static class ThemeManager
{
    /// <summary>Fired after a theme is applied: the window recolours its frame and turns Mica on or off.</summary>
    public static event Action<WindowLook>? Applied;

    /// <summary>The last look put on screen, for a window created after it.</summary>
    public static WindowLook Current { get; private set; } = new(false, Colors.Black, Color.FromRgb(0x23, 0x23, 0x23), false);

    private static (string? Base, string? Accent, string? Custom) _last;

    public static void Apply(string? baseId, string? accent, string? customHex)
    {
        _last = (baseId, accent, customHex);
        var light = WindowsLight();
        var theme = ThemeCatalog.BaseOf(baseId, light);
        var highContrast = SystemParameters.HighContrast;
        var colors = highContrast
            ? ThemeCatalog.HighContrast(Hex(SystemColors.WindowColor), Hex(SystemColors.WindowTextColor), Hex(SystemColors.HighlightColor),
                Hex(SystemColors.HighlightTextColor), Hex(SystemColors.GrayTextColor), Hex(SystemColors.HotTrackColor))
            : ThemeCatalog.Resolve(baseId, accent, customHex, WindowsAccent(), light);
        // Mica only where Windows draws it (Windows 11 22H2 and later) and the person did not turn transparency off.
        var mica = theme.Mica && !highContrast && MicaAvailable() && TransparencyOn();
        if (mica) colors["Surface.Base"] = "#00000000";

        var resources = Application.Current.Resources;
        foreach (var (key, hex) in colors)
        {
            var brush = new SolidColorBrush(ToColor(hex));
            brush.Freeze();
            resources[key] = brush;
        }
        var isLight = highContrast ? Luma(colors["Surface.Solid"]) > 0.5 : theme.IsLight;
        Current = new(isLight, ToColor(colors["Surface.Solid"]), ToColor(ThemeCatalog.Flatten(colors["Outline.Subtle"], colors["Surface.Solid"])), mica);
        Applied?.Invoke(Current);
    }

    /// <summary>Applies the last theme again: Windows changed its colours, light/dark mode, transparency or high contrast.</summary>
    public static void Reapply() => Apply(_last.Base, _last.Accent, _last.Custom);

    public static string? WindowsAccent() => WindowsLook.Accent();
    public static bool WindowsLight() => WindowsLook.Light();
    private static bool TransparencyOn() => WindowsLook.Transparency();
    public static double TextScale() => WindowsLook.TextScale();

    /// <summary>The system backdrop (Mica) exists from Windows 11 22H2, build 22621.</summary>
    public static bool MicaAvailable() => Environment.OSVersion.Version.Build >= 22621;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Luma(string hex)
    {
        var (r, g, b) = ThemeCatalog.Rgb(hex);
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
    }

    public static Color ToColor(string hex)
    {
        var (r, g, b) = ThemeCatalog.Rgb(hex);
        return Color.FromArgb((byte)Math.Round(ThemeCatalog.Alpha(hex) * 255), r, g, b);
    }
}
