using Microsoft.Win32;

namespace OptLife.Core.Appearance;

/// <summary>
/// What Windows says about its own look, read from the person's registry: the accent colour, light or dark apps,
/// transparency, the text size. The panel and the agent's pop-up both follow it.
/// </summary>
public static class WindowsLook
{
    /// <summary>The accent colour chosen in Windows Settings → Personalisation → Colours; null when it cannot be read.</summary>
    public static string? Accent() =>
        Read(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor") is int abgr ? ThemeCatalog.FromAbgr(abgr) : null;

    /// <summary>Windows Settings → Personalisation → Colours → "Choose your app mode": light.</summary>
    public static bool Light() =>
        Read(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") is int v && v != 0;

    /// <summary>Settings → Personalisation → Colours → Transparency effects.</summary>
    public static bool Transparency() =>
        Read(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency") is not int v || v != 0;

    /// <summary>Settings → Accessibility → Text size, from 1 (100%) to 2.25.</summary>
    public static double TextScale() =>
        Read(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor") is int v && v is >= 100 and <= 225 ? v / 100.0 : 1;

    /// <summary>The theme the person chose in the panel (saved in the configuration's Ui section), resolved to its colours.</summary>
    public static Dictionary<string, string> Chosen(IReadOnlyDictionary<string, string> ui) =>
        ThemeCatalog.Resolve(
            ui.TryGetValue("theme.base", out var b) ? b : ThemeCatalog.DefaultBase,
            ui.TryGetValue("theme.accent", out var a) ? a : ui.ContainsKey("theme.base") ? ThemeCatalog.Neutral : ThemeCatalog.DefaultAccent,
            ui.GetValueOrDefault("theme.custom"), Accent(), Light());

    /// <summary>The panel's own zoom on top of Windows' text size (Ui "zoom", 0.8 to 2).</summary>
    public static double Zoom(IReadOnlyDictionary<string, string> ui) =>
        double.TryParse(ui.GetValueOrDefault("zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z) ? Math.Clamp(z, 0.8, 2) : 1;

    private static object? Read(string key, string name)
    {
        try { return Registry.GetValue(key, name, null); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
