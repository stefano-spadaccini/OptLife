using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OptLife.Modules.Audio.Windows;

/// <summary>
/// The real machine. Devices and their state are read where Windows keeps them (MMDevices in the registry, readable without
/// administrator rights); the defaults are asked to Windows' audio service; switching a device on or off and setting the default
/// go through the same interface Sound settings uses (IPolicyConfig). Code of its own: the "PC settings" module reads audio too,
/// but modules never share code.
/// </summary>
internal sealed class WindowsAudioEnvironment : IAudioEnvironment
{
    private const string Devices_ = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string DescriptionProperty = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";   // "Speakers"
    private const string InterfaceProperty = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";     // "USB Audio DAC"

    public IReadOnlyList<AudioDevice> Devices()
    {
        var list = new List<AudioDevice>();
        foreach (var (flow, folder, prefix) in new[] { (Flow.Output, "Render", "{0.0.0.00000000}."), (Flow.Input, "Capture", "{0.0.1.00000000}.") })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey($@"{Devices_}\{folder}");
                foreach (var guid in key?.GetSubKeyNames() ?? [])
                {
                    using var device = key!.OpenSubKey(guid);
                    if (device?.GetValue("DeviceState") is not int state || AudioRules.FromState(state) is not { } s) continue;
                    using var properties = device.OpenSubKey("Properties");
                    var description = properties?.GetValue(DescriptionProperty) as string ?? "";
                    var iface = properties?.GetValue(InterfaceProperty) as string ?? "";
                    var name = (description, iface) switch
                    {
                        ({ Length: > 0 }, { Length: > 0 }) => $"{description} ({iface})",
                        ({ Length: > 0 }, _) => description,
                        _ => iface.Length > 0 ? iface : guid,
                    };
                    list.Add(new AudioDevice(prefix + guid, flow, name, s.On, s.Unplugged));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return list;
    }

    public AudioDefaults Defaults()
    {
        var output = new string?[3];
        var input = new string?[3];
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            for (var role = 0; role < 3; role++)
            {
                output[role] = DefaultId(enumerator, 0, role);
                input[role] = DefaultId(enumerator, 1, role);
            }
        }
        catch (COMException)
        {
            // The audio service is not running: no defaults to judge.
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
        return new AudioDefaults(output, input);
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow, int role)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null) return null;
        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    public ActionResult SetVisible(string id, bool visible) =>
        Policy(p => p.SetEndpointVisibility(id, visible ? 1 : 0),
            visible ? L.T("riacceso.", "turned back on.") : L.T("spento. Il driver resta: si riaccende col pulsante.", "turned off. The driver stays: it turns back on with the button."));

    public ActionResult SetDefault(string id) =>
        Policy(p =>
        {
            for (var role = 0; role < 3; role++)
                if (p.SetDefaultEndpoint(id, role) is var hr and not 0) return hr;
            return 0;
        }, L.T("ora è il predefinito per tutti gli usi.", "now the default for every use."));

    private static ActionResult Policy(Func<IPolicyConfig, int> call, string done)
    {
        IPolicyConfig? policy = null;
        try
        {
            policy = (IPolicyConfig)new PolicyConfigClient();
            var hr = call(policy);
            return hr == 0 ? new ActionResult(true, done) : new ActionResult(false, L.T($"Windows ha rifiutato (codice {hr:X8}).", $"Windows refused (code {hr:X8})."));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return new ActionResult(false, L.T("Non riuscita: ", "Failed: ") + ex.Message);
        }
        finally
        {
            if (policy is not null) Marshal.ReleaseComObject(policy);
        }
    }

    // ── COM ──────────────────────────────────────────────────────────────────

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

    /// <summary>What Sound settings itself uses (not in the public SDK, the same since Windows 7). Only the order of the methods matters.</summary>
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient;

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, out long defaultInterval, out long minInterval);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
