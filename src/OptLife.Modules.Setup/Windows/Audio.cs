using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OptLife.Modules.Setup.Windows;

/// <summary>
/// The default playback and recording devices, and whether Equalizer APO is attached to the one in
/// use. Which device is the default is asked to Windows' audio service; everything else about a
/// device is read from where Windows keeps it in the registry.
/// </summary>
internal static class Audio
{
    private const string Devices = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string DescriptionProperty = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";   // "Altoparlanti"
    private const string InterfaceProperty = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";     // "USB Audio DAC"

    // Equalizer APO attaches itself to a device by putting these two identifiers among its effects.
    private static readonly string[] EqualizerIds = ["EACD2258-FCAC-4FF4-B36D-419E924A6D79", "EC1CC9CE-FAED-4822-828A-82A81A6F018F"];

    public sealed record Endpoint(string Id, string Name, string Description, string Interface, bool HasEqualizer);

    public static Endpoint? Default(bool recording)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            const int multimedia = 1;
            if (enumerator.GetDefaultAudioEndpoint(recording ? 1 : 0, multimedia, out var device) != 0 || device is null) return null;
            try
            {
                // "{0.0.0.00000000}.{e545e194-ed90-4016-9e0a-80023cc811f5}": the second part names its registry key.
                if (device.GetId(out var id) != 0 || id is null) return null;
                var guid = id[(id.LastIndexOf('{'))..];
                return Read(recording ? "Capture" : "Render", guid);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Where Equalizer APO is installed; null when it is not.</summary>
    public static string? EqualizerFolder()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
        return key?.GetValue("InstallPath") as string is { Length: > 0 } path && Directory.Exists(path) ? path : null;
    }

    /// <summary>Another playback entry of the same physical device that has the equalizer attached.</summary>
    public static bool TwinHasEqualizer(Endpoint endpoint)
    {
        using var render = Registry.LocalMachine.OpenSubKey($@"{Devices}\Render");
        if (render is null) return false;
        foreach (var guid in render.GetSubKeyNames())
        {
            if (guid.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (Read("Render", guid) is { HasEqualizer: true } other
                && other.Interface == endpoint.Interface && other.Description == endpoint.Description)
                return true;
        }
        return false;
    }

    private static Endpoint? Read(string flow, string guid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{Devices}\{flow}\{guid}");
        if (key is null) return null;
        using var properties = key.OpenSubKey("Properties");
        var description = properties?.GetValue(DescriptionProperty) as string ?? "";
        var iface = properties?.GetValue(InterfaceProperty) as string ?? "";

        var equalizer = false;
        using (var effects = key.OpenSubKey("FxProperties"))
        {
            if (effects is not null)
                foreach (var name in effects.GetValueNames())
                    if (effects.GetValue(name)?.ToString() is { } value && EqualizerIds.Any(id => value.Contains(id, StringComparison.OrdinalIgnoreCase)))
                        equalizer = true;
        }
        var label = (description, iface) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{description} ({iface})",
            ({ Length: > 0 }, _) => description,
            _ => iface.Length > 0 ? iface : guid,
        };
        return new Endpoint(guid, label, description, iface, equalizer);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    }
}
