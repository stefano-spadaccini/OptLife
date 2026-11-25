using System.Runtime.InteropServices;
using System.Text;
using OptLife.Core;

namespace OptLife.Modules.Setup.Windows;

/// <summary>
/// The screens in use: name, current mode, the highest refresh rate available at that resolution,
/// and whether HDR is on. Windows describes them in fixed-layout blocks, read here byte by byte.
/// </summary>
internal static class Displays
{
    private const uint OnlyActivePaths = 2;
    private const int PathSize = 72, ModeSize = 64, HeaderSize = 20;
    private const uint GetSourceName = 1, GetTargetName = 2, GetAdvancedColorInfo = 9;

    public static IReadOnlyList<DisplayInfo> Read() => [.. ReadWithNames().Select(d => d.Info)];

    /// <summary>
    /// Switches a screen to the highest refresh rate it offers at the resolution in use. Only modes the
    /// graphics driver itself lists are used, and the choice is saved like one made from Windows' settings.
    /// </summary>
    public static ActionResult SetMaxRefresh(string id)
    {
        var found = ReadWithNames().FirstOrDefault(d => d.Info.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (found.Info is not { } display) return new ActionResult(false, L.T($"Lo schermo \"{id}\" non è collegato o è spento.", $"Screen \"{id}\" is not connected or is off."));
        if (display.Hz + 1 >= display.MaxHz) return ActionResult.Nothing(L.T($"Lo schermo {display.Name} è già a {display.Hz} Hz, il suo massimo.", $"Screen {display.Name} is already at {display.Hz} Hz, its maximum."));

        var best = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (var m = 0; EnumDisplaySettings(found.Gdi, m, ref mode); m++)
            if (mode.dmPelsWidth == display.Width && mode.dmPelsHeight == display.Height && mode.dmDisplayFrequency > best.dmDisplayFrequency)
                best = mode;
        if (best.dmDisplayFrequency <= display.Hz) return new ActionResult(false, L.T("Nessuna frequenza più alta disponibile a questa risoluzione.", "No higher refresh rate available at this resolution."));

        const int updateRegistry = 1;
        var outcome = ChangeDisplaySettingsEx(found.Gdi, ref best, IntPtr.Zero, updateRegistry, IntPtr.Zero);
        return outcome == 0
            ? new ActionResult(true, L.T($"Schermo {display.Name} portato da {display.Hz} a {best.dmDisplayFrequency} Hz.", $"Screen {display.Name} moved from {display.Hz} to {best.dmDisplayFrequency} Hz."),
                Undo: L.T("Impostazioni di Windows → Sistema → Schermo → Schermo avanzato → ", "Windows Settings → System → Display → Advanced display → ") + $"{display.Hz} Hz")
            : new ActionResult(false, L.T($"Windows ha rifiutato il cambio di frequenza (codice {outcome}).", $"Windows refused the refresh rate change (code {outcome})."));
    }

    private static List<(DisplayInfo Info, string Gdi)> ReadWithNames()
    {
        var displays = new List<(DisplayInfo Info, string Gdi)>();
        if (GetDisplayConfigBufferSizes(OnlyActivePaths, out var pathCount, out var modeCount) != 0) return displays;
        var paths = new byte[pathCount * PathSize];
        var modes = new byte[modeCount * ModeSize];
        if (QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return displays;

        for (var i = 0; i < pathCount; i++)
        {
            var at = i * PathSize;
            // A path joins a source (a desktop area, "\\.\DISPLAY1") to a target (the physical screen).
            var source = Ask(GetSourceName, 84, paths, at, at + 8);
            var target = Ask(GetTargetName, 420, paths, at + 20, at + 28);
            if (source is null || target is null) continue;

            var gdiName = Text(source, HeaderSize, 64);
            var name = Text(target, 36, 128);
            var devicePath = Text(target, 164, 256); // \\?\DISPLAY#GSM5B7F#5&1520d583&0&UID33025#{...}
            var model = devicePath.Split('#') is { Length: > 1 } parts ? parts[1] : gdiName.Trim('\\', '.');
            var id = model;
            for (var n = 2; displays.Any(d => d.Info.Id == id); n++) id = $"{model}-{n}"; // Two screens of the same model.

            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(gdiName, -1, ref mode)) continue;
            int width = mode.dmPelsWidth, height = mode.dmPelsHeight, hz = mode.dmDisplayFrequency, max = hz;

            // The exact rate (164.96) rounds to what people call it (165).
            uint numerator = BitConverter.ToUInt32(paths, at + 48), denominator = BitConverter.ToUInt32(paths, at + 52);
            if (denominator > 0 && numerator > 0) hz = (int)Math.Round((double)numerator / denominator);
            max = Math.Max(max, hz);

            var other = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            for (var m = 0; EnumDisplaySettings(gdiName, m, ref other); m++)
                if (other.dmPelsWidth == width && other.dmPelsHeight == height)
                    max = Math.Max(max, other.dmDisplayFrequency);

            bool? hdr = null;
            if (Ask(GetAdvancedColorInfo, 32, paths, at + 20, at + 28) is { } color)
            {
                var flags = BitConverter.ToUInt32(color, HeaderSize);
                if ((flags & 1) != 0) hdr = (flags & 2) != 0; // Supported; then: enabled.
            }

            displays.Add((new DisplayInfo(id, name.Length > 0 ? name : model, width, height, hz, max, hdr), gdiName));
        }
        return displays;
    }

    /// <summary>Names of the graphics cards that have a screen attached.</summary>
    public static IReadOnlyList<string> Adapters()
    {
        var names = new List<string>();
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            const int attachedToDesktop = 1;
            if ((device.StateFlags & attachedToDesktop) != 0 && !names.Contains(device.DeviceString)) names.Add(device.DeviceString);
        }
        return names;
    }

    /// <summary>One question to Windows about a source or target; the header says which and how big the answer is.</summary>
    private static byte[]? Ask(uint type, int size, byte[] paths, int adapterAt, int idAt)
    {
        var packet = new byte[size];
        BitConverter.TryWriteBytes(packet.AsSpan(0), type);
        BitConverter.TryWriteBytes(packet.AsSpan(4), (uint)size);
        Array.Copy(paths, adapterAt, packet, 8, 8);
        Array.Copy(paths, idAt, packet, 16, 4);
        return DisplayConfigGetDeviceInfo(packet) == 0 ? packet : null;
    }

    private static string Text(byte[] data, int offset, int bytes)
    {
        var text = Encoding.Unicode.GetString(data, offset, bytes);
        var end = text.IndexOf('\0');
        return (end < 0 ? text : text[..end]).Trim();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [In, Out] byte[] paths, ref uint modeCount, [In, Out] byte[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE mode, IntPtr window, int flags, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE result, uint flags);
}
