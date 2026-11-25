using System.Globalization;

namespace OptLife.Core.Appearance;

/// <summary>One base look: the colours of everything except the accent.</summary>
/// <param name="Mica">Drawn on the Windows material: the window background is left to Windows (when it can).</param>
public sealed record ThemeBase(string Id, string Name, bool IsLight, IReadOnlyDictionary<string, string> Colors, bool Mica = false);

/// <summary>The accent: tabs, main buttons, switches, the ring. "neutral" is the base's own text colour.</summary>
public sealed record ThemeAccent(string Id, string Name, string? Hex);

/// <summary>
/// The panel's themes, pure: a base and an accent. <see cref="Resolve"/> turns the choice into the token colours
/// the styles read with DynamicResource, so applying a theme is replacing those brushes.
/// Every text colour comes out readable (contrast 4.5 with every surface it can sit on), whatever the accent.
/// Colours are "#RRGGBB", or "#AARRGGBB" for the see-through layers of the Windows base.
/// </summary>
public static class ThemeCatalog
{
    public const string Neutral = "neutral", Windows = "windows", Custom = "custom";

    /// <summary>The base that follows Windows: light or dark as Windows is, on its Mica material.</summary>
    public const string System = "system";

    /// <summary>What a new install starts with: the look of the Windows system apps.</summary>
    public const string DefaultBase = System, DefaultAccent = Windows;

    /// <summary>Text tokens and the surfaces they are written on: each pair must read at 4.5:1 at least.</summary>
    public static readonly string[] TextTokens = ["Text.Primary", "Text.Secondary", "Text.Muted", "Warning", "Danger", "Good"];
    public static readonly string[] Surfaces = ["Surface.Solid", "Surface.Raised", "Surface.Overlay", "Surface.Flyout", "Surface.Sunken"];

    private static Dictionary<string, string> Fluent(bool light) => light
        ? new()
        {
            ["Surface.Solid"] = "#F3F3F3", ["Surface.Base"] = "#F3F3F3", ["Surface.Raised"] = "#B3FFFFFF", ["Surface.Overlay"] = "#0F000000",
            ["Surface.Sunken"] = "#B3FFFFFF", ["Surface.Flyout"] = "#F9F9F9",
            ["Outline.Subtle"] = "#14000000", ["Outline.Strong"] = "#40000000",
            ["Text.Primary"] = "#1B1B1B", ["Text.Secondary"] = "#4B4B4B", ["Text.Muted"] = "#616161",
            ["Warning"] = "#8A5800", ["Danger"] = "#C42B1C", ["Info"] = "#6E6E6E", ["Good"] = "#0F7B0F",
        }
        : new()
        {
            ["Surface.Solid"] = "#202020", ["Surface.Base"] = "#202020", ["Surface.Raised"] = "#0DFFFFFF", ["Surface.Overlay"] = "#14FFFFFF",
            ["Surface.Sunken"] = "#4D000000", ["Surface.Flyout"] = "#2C2C2C",
            ["Outline.Subtle"] = "#17FFFFFF", ["Outline.Strong"] = "#40FFFFFF",
            ["Text.Primary"] = "#FFFFFF", ["Text.Secondary"] = "#D0D0D0", ["Text.Muted"] = "#A8A8A8",
            ["Warning"] = "#FCE100", ["Danger"] = "#FF99A4", ["Info"] = "#9A9A9A", ["Good"] = "#6CCB5F",
        };

    /// <param name="windowsLight">Whether Windows is in light mode: the Windows base follows it.</param>
    public static IReadOnlyList<ThemeBase> BasesFor(bool windowsLight) =>
    [
        new(System, L.T("Windows", "Windows"), windowsLight, Fluent(windowsLight), Mica: true),
        new("black", L.T("Nero", "Black"), false, new Dictionary<string, string>
        {
            ["Surface.Solid"] = "#000000", ["Surface.Base"] = "#000000", ["Surface.Raised"] = "#0B0B0B", ["Surface.Overlay"] = "#171717",
            ["Surface.Sunken"] = "#050505", ["Surface.Flyout"] = "#121212",
            ["Outline.Subtle"] = "#262626", ["Outline.Strong"] = "#4A4A4A",
            ["Text.Primary"] = "#F4F4F4", ["Text.Secondary"] = "#B4B4B4", ["Text.Muted"] = "#8E8E8E",
            ["Warning"] = "#E0A82E", ["Danger"] = "#F0475A", ["Info"] = "#8C8C8C", ["Good"] = "#4FC27A",
        }),
        new("graphite", L.T("Grafite", "Graphite"), false, new Dictionary<string, string>
        {
            ["Surface.Solid"] = "#1C1D21", ["Surface.Base"] = "#1C1D21", ["Surface.Raised"] = "#24262B", ["Surface.Overlay"] = "#2E3036",
            ["Surface.Sunken"] = "#17181B", ["Surface.Flyout"] = "#2A2C31",
            ["Outline.Subtle"] = "#363840", ["Outline.Strong"] = "#565A64",
            ["Text.Primary"] = "#ECEDEF", ["Text.Secondary"] = "#B4B7BE", ["Text.Muted"] = "#989CA5",
            ["Warning"] = "#E6AE3A", ["Danger"] = "#FF6B79", ["Info"] = "#8F939C", ["Good"] = "#5BCB86",
        }),
        new("light", L.T("Chiaro", "Light"), true, new Dictionary<string, string>
        {
            ["Surface.Solid"] = "#F5F5F4", ["Surface.Base"] = "#F5F5F4", ["Surface.Raised"] = "#FFFFFF", ["Surface.Overlay"] = "#EAEAE8",
            ["Surface.Sunken"] = "#EFEFED", ["Surface.Flyout"] = "#FFFFFF",
            ["Outline.Subtle"] = "#DDDDDA", ["Outline.Strong"] = "#A9A9A5",
            ["Text.Primary"] = "#141414", ["Text.Secondary"] = "#474747", ["Text.Muted"] = "#626262",
            ["Warning"] = "#8F5A00", ["Danger"] = "#B3102A", ["Info"] = "#6F6F6F", ["Good"] = "#17703C",
        }),
    ];

    public static IReadOnlyList<ThemeBase> Bases => BasesFor(false);

    public static IReadOnlyList<ThemeAccent> Accents =>
    [
        new(Windows, L.T("Colore di Windows", "Windows colour"), null),
        new(Neutral, L.T("Neutro", "Neutral"), null),
        new("blue", L.T("Blu", "Blue"), "#3B82F6"),
        new("violet", L.T("Viola", "Violet"), "#8B5CF6"),
        new("green", L.T("Verde", "Green"), "#22C55E"),
        new("orange", L.T("Arancio", "Orange"), "#F97316"),
        new("pink", L.T("Rosa", "Pink"), "#EC4899"),
        new(Custom, L.T("Personalizzato", "Custom"), null),
    ];

    /// <summary>The base with this id; an unknown one falls back to black, the look OptLife always had.</summary>
    public static ThemeBase BaseOf(string? id, bool windowsLight = false) =>
        BasesFor(windowsLight).FirstOrDefault(b => b.Id == id) ?? BasesFor(windowsLight)[1];

    /// <summary>The colour an accent stands for right now; null for neutral (or an unreadable custom one).</summary>
    public static string? AccentHex(string? accent, string? customHex, string? windowsHex) => accent switch
    {
        Windows => windowsHex,
        Custom => Normalize(customHex),
        _ => Accents.FirstOrDefault(a => a.Id == accent)?.Hex,
    };

    /// <summary>Every token and its colour for this choice.</summary>
    public static Dictionary<string, string> Resolve(string? baseId, string? accent, string? customHex, string? windowsHex, bool windowsLight = false)
    {
        var b = BaseOf(baseId, windowsLight);
        var colors = new Dictionary<string, string>(b.Colors);
        var solid = colors["Surface.Solid"];
        var accentHex = Readable(AccentHex(accent, customHex, windowsHex) ?? colors["Text.Primary"], solid, colors["Text.Primary"]);
        // "Accent" draws lines and edges (3:1 is enough). Buttons and switches are filled with "Accent.Fill", the same colour
        // moved just enough away from the text written on it (black or white, whichever reads better) to reach 4.5:1;
        // hover and pressed go further the same way, so they read at least as well.
        colors["Accent"] = accentHex;
        var onAccent = Contrast("#000000", accentHex) >= Contrast("#FFFFFF", accentHex) ? "#000000" : "#FFFFFF";
        var away = onAccent == "#000000" ? "#FFFFFF" : "#000000";
        var fill = accentHex;
        for (var step = 1; step <= 20 && Contrast(onAccent, fill) < 4.6; step++)
            fill = Mix(accentHex, away, step / 20.0);
        colors["Text.OnAccent"] = onAccent;
        colors["Accent.Fill"] = fill;
        colors["Accent.Hover"] = Mix(fill, away, 0.10);
        colors["Accent.Pressed"] = Mix(fill, away, 0.20);
        colors["Accent.Subtle"] = Mix(accentHex, solid, b.IsLight ? 0.86 : 0.84);
        // Black and white keep "all is well" in white or black; with a colour it is the accent that says it.
        colors["Success"] = accentHex;
        colors["Material.Glass"] = solid;
        MakeTextReadable(colors);
        return colors;
    }

    /// <summary>
    /// The colours of Windows' high contrast theme, as Windows gives them: background, text, selection, selected text,
    /// disabled text and links. Status is said by words and icons there, never by a colour of OptLife's own.
    /// </summary>
    public static Dictionary<string, string> HighContrast(string window, string text, string highlight, string highlightText, string grayText, string hotlight)
    {
        var colors = new Dictionary<string, string>
        {
            ["Surface.Solid"] = window, ["Surface.Base"] = window, ["Surface.Raised"] = window, ["Surface.Overlay"] = window,
            ["Surface.Sunken"] = window, ["Surface.Flyout"] = window,
            ["Outline.Subtle"] = text, ["Outline.Strong"] = text,
            ["Text.Primary"] = text, ["Text.Secondary"] = text, ["Text.Muted"] = grayText,
            ["Warning"] = hotlight, ["Danger"] = hotlight, ["Info"] = text, ["Good"] = text,
            ["Accent"] = highlight, ["Accent.Fill"] = highlight, ["Accent.Hover"] = highlight, ["Accent.Pressed"] = highlight, ["Accent.Subtle"] = window,
            ["Text.OnAccent"] = highlightText, ["Success"] = text, ["Material.Glass"] = window,
        };
        return colors;
    }

    /// <summary>Moves every text colour toward the main text colour until it reads on every surface (4.5:1).</summary>
    private static void MakeTextReadable(Dictionary<string, string> colors)
    {
        var solid = colors["Surface.Solid"];
        var surfaces = Surfaces.Select(s => Flatten(colors[s], solid)).ToList();
        var primary = colors["Text.Primary"];
        foreach (var token in TextTokens)
        {
            var original = colors[token];
            var result = original;
            for (var step = 1; step <= 20 && surfaces.Min(s => Contrast(result, s)) < 4.5; step++)
                result = Mix(original, primary, step / 20.0);
            colors[token] = result;
        }
    }

    /// <summary>"#RGB", "RGB", "#RRGGBB" → "#RRGGBB"; null when it is not a colour.</summary>
    public static string? Normalize(string? hex)
    {
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        return h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + h.ToUpperInvariant() : null;
    }

    /// <summary>Windows keeps its accent as 0xAABBGGRR (DWM "AccentColor").</summary>
    public static string FromAbgr(int abgr) => $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

    /// <summary>The colour without its transparency.</summary>
    public static (byte R, byte G, byte B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        var v = int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>How opaque the colour is, 0 to 1 ("#RRGGBB" is fully opaque).</summary>
    public static double Alpha(string hex)
    {
        var h = hex.TrimStart('#');
        return h.Length == 8 ? int.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0 : 1;
    }

    /// <summary>A see-through colour as it looks laid on an opaque one.</summary>
    public static string Flatten(string hex, string under) => Mix(under, $"#{Rgb(hex).R:X2}{Rgb(hex).G:X2}{Rgb(hex).B:X2}", Alpha(hex));

    private static string Mix(string a, string b, double towardB)
    {
        var (r1, g1, b1) = Rgb(a);
        var (r2, g2, b2) = Rgb(b);
        byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * towardB);
        return $"#{M(r1, r2):X2}{M(g1, g2):X2}{M(b1, b2):X2}";
    }

    /// <summary>WCAG contrast between two colours: 1 is none, 21 is black on white.</summary>
    public static double Contrast(string a, string b)
    {
        static double Lin(byte c) { var s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double L(string hex) { var (r, g, b) = Rgb(hex); return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b); }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>
    /// An accent that would vanish on the background (a dark Windows colour on a dark theme) is moved toward the
    /// text colour until it stands out (contrast 3, what lines and buttons need), keeping its hue.
    /// </summary>
    public static string Readable(string accent, string background, string text)
    {
        var result = accent;
        for (var step = 1; step <= 10 && Contrast(result, background) < 3; step++)
            result = Mix(accent, text, step / 10.0);
        return result;
    }
}
