using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OptLife.Panel;

/// <summary>
/// True when the bound value equals the parameter (compared as text, so enums work from XAML).
/// Bound to a Visibility it yields Visible/Collapsed instead.
/// </summary>
public sealed class IsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        return targetType == typeof(Visibility) ? (equal ? Visibility.Visible : Visibility.Collapsed) : equal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The icon Windows shows for a program file (a path to an .exe or .ico), as an image; null when there is no file
/// or no icon, and the template shows the first letter instead. Read once per file.
/// </summary>
public sealed class FileIconConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        if (Cache.TryGetValue(path, out var known)) return known;
        System.Windows.Media.ImageSource? image = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                var info = new ShFileInfo();
                if (SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShFileInfo>(), 0x100 /* ICON */) != 0 && info.hIcon != 0)
                {
                    try
                    {
                        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        image = source;
                    }
                    finally
                    {
                        DestroyIcon(info.hIcon);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.IO.IOException)
        {
            // No picture: the first letter does the job.
        }
        Cache[path] = image;
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}

/// <summary>Visible for true, a non-empty text or a positive number; parameter "not" inverts.</summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => value is not null,
        };
        if (parameter as string == "not") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Text written in capitals by a module or an older page ("SCEGLI E AGGIORNA") shown in sentence case ("Scegli e aggiorna"),
/// as Windows writes buttons and headings: capitals are harder to read, and some screen readers spell them out.
/// Acronyms (BIOS, GPU, DNS…) and text that already has small letters stay as they are.
/// </summary>
public sealed class SentenceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? TextCase.Sentence(s) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class TextCase
{
    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "PC", "BIOS", "UEFI", "GPU", "CPU", "RAM", "SSD", "HDD", "NAS", "DNS", "VPN", "USB", "HDR", "FPS", "DDU", "OK", "ID", "IP", "IPV4", "IPV6",
        "EQ", "APO", "NVIDIA", "AMD", "TCP", "UDP", "SMB", "SMB1", "UAC", "OS", "MB", "GB", "KB", "TB", "MHZ", "GHZ", "HZ", "PS1", "PS2", "PS3", "PS4", "PS5",
        "TV", "LED", "RGB", "CSV", "JSON", "URL", "ISO", "XML", "HTML", "AI", "IA", "WI-FI", "OPTLIFE",
    };

    public static string Sentence(string text)
    {
        if (text.Length < 2 || text.Any(char.IsLower) || !text.Any(char.IsLetter)) return text;
        var words = text.Split(' ');
        var first = true;
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            var core = w.Trim('(', ')', '.', ',', ':', ';', '·', '…', '!', '?', '"', '\'');
            if (Acronyms.Contains(core) || core.Any(char.IsDigit))
            {
                if (core == "OPTLIFE") words[i] = w.Replace("OPTLIFE", "OptLife");
                else if (core == "WI-FI") words[i] = w.Replace("WI-FI", "Wi-Fi");
            }
            else
            {
                var lower = w.ToLower(L.Culture);
                words[i] = first && char.IsLetter(lower.FirstOrDefault(char.IsLetter))
                    ? Capitalize(lower)
                    : lower;
            }
            if (core.Any(char.IsLetter)) first = false;
        }
        return string.Join(' ', words);
    }

    private static string Capitalize(string w)
    {
        var at = w.IndexOf(w.First(char.IsLetter));
        return w[..at] + char.ToUpper(w[at], L.Culture) + w[(at + 1)..];
    }
}

/// <summary>
/// A level (ok, good, info, low, medium, high) as a Segoe Fluent icon, so a state is never told by colour alone:
/// a tick, an "i", a warning triangle, an error badge.
/// </summary>
public sealed class LevelIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "high" => "\uEA39",
        "medium" => "\uE7BA",
        "ok" or "good" => "\uE73E",
        _ => "\uE946",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A heading of the side bar: only when there is one and the bar shows names (values: HasHeading, NavCompact).</summary>
public sealed class HeadingVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [true, false] ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>The level of a notice line: "medium" when it reports an error, "info" otherwise.</summary>
public sealed class NoticeLevelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "medium" : "info";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible only for a worrying level (medium, high): the icon beside a number that needs a look.</summary>
public sealed class WorryVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value as string is "medium" or "high" ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A level in words, for screen readers: what the icon and the colour say.</summary>
public sealed class LevelWordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "high" => L.T("da sistemare", "to fix"),
        "medium" => L.T("da guardare", "worth a look"),
        "ok" or "good" => L.T("a posto", "fine"),
        _ => L.T("informazione", "information"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A PNG picture (a module's preview) as an image; read once, at its own pixels.</summary>
public sealed class PngImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } png) return null;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        using var stream = new System.IO.MemoryStream(png);
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// A picture file (a game's cover) shown small: decoded at the card's width so a large library costs little memory,
/// read once and then kept (the file stays free to change or move).
/// </summary>
public sealed class FileImageConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        if (Cache.TryGetValue(path, out var known)) return known;
        System.Windows.Media.ImageSource? image = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 320;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException or UriFormatException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Not a picture after all: the card shows the game's initial.
        }
        Cache[path] = image;
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// A cover decoded tiny (32 pixels wide) and stretched smoothly: it looks blurred at no cost, unlike a live blur effect that
/// the graphics card recomputes on every frame. Kept once per file.
/// </summary>
public sealed class SoftImageConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        if (Cache.TryGetValue(path, out var known)) return known;
        System.Windows.Media.ImageSource? image = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 32;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException or UriFormatException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or ArgumentException)
        {
        }
        Cache[path] = image;
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The cover's main colour, a little brighter and more saturated, for the background's gradient; a deep blue when there is no cover.
/// Read from an 8×8 copy of the picture, once per file.
/// </summary>
public sealed class CoverColorConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.Color> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Windows.Media.Color Fallback = System.Windows.Media.Color.FromRgb(0x1E, 0x2A, 0x4A);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = Fallback;
        if (value is string path && path.Length > 0)
        {
            if (!Cache.TryGetValue(path, out color))
            {
                color = Fallback;
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 8;
                        bitmap.DecodePixelHeight = 8;
                        bitmap.UriSource = new Uri(path);
                        bitmap.EndInit();
                        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                        var pixels = new byte[8 * 8 * 4];
                        bgra.CopyPixels(pixels, 8 * 4, 0);
                        // The most colourful pixels count most: a grey sky does not wash out a red cover.
                        double r = 0, g = 0, b = 0, weight = 0;
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            var (pb, pg, pr) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                            var w = 0.1 + (Math.Max(pr, Math.Max(pg, pb)) - Math.Min(pr, Math.Min(pg, pb))) / 255.0;
                            (r, g, b, weight) = (r + pr * w, g + pg * w, b + pb * w, weight + w);
                        }
                        color = Lift(System.Windows.Media.Color.FromRgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight)));
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException or UriFormatException
                                               or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
                {
                }
                Cache[path] = color;
            }
        }
        if (targetType == typeof(System.Windows.Media.Brush)) return new System.Windows.Media.SolidColorBrush(color);
        return color;
    }

    /// <summary>Kept dark enough for white text, but with some colour.</summary>
    private static System.Windows.Media.Color Lift(System.Windows.Media.Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max == 0) return Fallback;
        var scale = 150.0 / max;   // The brightest channel at 150: coloured, still dark behind white text.
        return System.Windows.Media.Color.FromRgb((byte)Math.Min(255, c.R * scale), (byte)Math.Min(255, c.G * scale), (byte)Math.Min(255, c.B * scale));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// A big picture (the game panel's art) decoded at up to 1600 pixels wide, so it is never a small copy stretched; shown with
/// "DownOnly" it is never enlarged beyond its own size either. Kept once per file.
/// </summary>
public sealed class LargeImageConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        if (Cache.TryGetValue(path, out var known)) return known;
        System.Windows.Media.ImageSource? image = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                var probe = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                if (probe.PixelWidth > 1600) bitmap.DecodePixelWidth = 1600;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException or UriFormatException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or ArgumentException or System.IO.FileFormatException)
        {
        }
        Cache[path] = image;
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
