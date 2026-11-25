using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using OptLife.Core;

namespace OptLife.Modules.Setup.Windows;

/// <summary>
/// The real machine. Every source is optional and read-only: whatever this PC does not have or will
/// not tell is simply missing from the list, and nothing here needs administrator rights.
/// </summary>
public sealed class WindowsPcProbe : IPcProbe
{
    public IReadOnlyList<Reading> ReadSettings()
    {
        var readings = new List<Reading>();
        Try(() => readings.AddRange(Displays.Read().SelectMany(SetupRules.Display)));
        Try(() =>
        {
            if (Smbios.ReadRaw() is { } raw) readings.AddRange(SetupRules.Memory(Smbios.Memory(Smbios.Parse(raw))));
        });
        Try(() =>
        {
            if (ActivePowerPlan() is { } plan) readings.Add(SetupRules.PowerPlan(plan.Guid, plan.Name));
        });
        Try(() =>
        {
            readings.Add(SetupRules.GameMode(Number(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled")));
            readings.Add(SetupRules.HardwareScheduling(Number(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")));
            readings.Add(SetupRules.MemoryIntegrity(Number(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled")));
            readings.AddRange(SetupRules.DirectX(Text(Registry.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings")));
            readings.Add(SetupRules.MouseAcceleration(Text(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed")));
        });
        Try(() =>
        {
            if (Nvidia.PowerLimitWatts() is { } watts) readings.Add(SetupRules.GpuPowerLimit(watts));
        });
        Try(() => readings.AddRange(SetupRules.Firmware(FirmwareFacts())));
        Try(() =>
        {
            // The connections that reach the internet (up, with a gateway): their IPv4 DNS servers, as written by hand (none = automatic).
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback))
            {
                var ip = nic.GetIPProperties();
                if (!ip.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) continue;
                var id = ip.GetIPv4Properties()?.Index;
                var manual = Text(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}", "NameServer");
                IReadOnlyList<string> servers = string.IsNullOrWhiteSpace(manual) ? [] : [.. manual.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)];
                readings.Add(SetupRules.Dns(nic.Name, servers));
                _ = id;
            }
        });
        Try(() =>
        {
            var output = Audio.Default(recording: false);
            var input = Audio.Default(recording: true);
            bool? equalizer = output is not null && Audio.EqualizerFolder() is not null ? output.HasEqualizer : null;
            readings.AddRange(SetupRules.Audio(output?.Name, input?.Name, equalizer,
                equalizerOnTwin: equalizer == false && Audio.TwinHasEqualizer(output!)));
        });
        // A laptop: the advice meant for a desktop (High performance, graphics card limits) is taken out.
        byte? chassis = null;
        Try(() => chassis = Smbios.ReadRaw() is { } raw ? Smbios.Chassis(Smbios.Parse(raw)) : null);
        return SetupRules.ForDevice(readings, chassis, HasBattery());
    }

    /// <summary>A system battery, as Windows reports it (128 = no battery, 255 = unknown).</summary>
    private static bool HasBattery()
    {
        try { return GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>What the BIOS has set, read without drivers or administrator rights.</summary>
    private static FirmwareFacts FirmwareFacts()
    {
        string? bios = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
        {
            var version = (key?.GetValue("BIOSVersion") as string)?.Trim();
            var date = (key?.GetValue("BIOSReleaseDate") as string)?.Trim();
            if (version is { Length: > 0 })
                bios = date is { Length: > 0 } && DateTime.TryParseExact(date, "MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                    ? $"{version} · {d.ToString("d MMM yyyy", L.Culture)}" : version;
        }
        var secure = Number(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        int? tpm = null;
        try
        {
            var info = new TpmDeviceInfo { StructVersion = 1 };
            var result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), ref info);
            tpm = result == 0 ? (int)info.TpmVersion : unchecked((uint)result) == 0x8028400F ? 0 : null; // TBS_E_TPM_NOT_FOUND
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        bool? virtualization = null;
        try
        {
            // Enabled in the firmware; or a hypervisor already running (memory integrity, WSL), which needs it on.
            var hypervisor = System.Runtime.Intrinsics.X86.X86Base.IsSupported && (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & (1 << 31)) != 0;
            virtualization = IsProcessorFeaturePresent(21) || hypervisor; // PF_VIRT_FIRMWARE_ENABLED
        }
        catch (EntryPointNotFoundException)
        {
        }
        var gpu = Nvidia.Link();
        return new FirmwareFacts(bios, Firmware.IsUefi(), secure is null ? null : secure == 1, tpm, virtualization,
            gpu?.Name, gpu?.Bar1Mb, gpu?.VramMb, gpu?.Gen, gpu?.GenCard, gpu?.Width, gpu?.WidthMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion, TpmVersion, TpmInterfaceType, TpmImpRevision;
    }

    [DllImport("tbs.dll")] private static extern int Tbsi_GetDeviceInfo(uint size, ref TpmDeviceInfo info);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessorFeaturePresent(uint feature);

    public ActionResult Apply(string action, string? argument)
    {
        try
        {
            switch (action)
            {
                case SetupModule.SetDns:
                {
                    if ((argument ?? "").Split('|') is not [var adapter, var list] || adapter.Length == 0) return new ActionResult(false, L.T("Manca la connessione.", "The connection is missing."));
                    var wanted = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (wanted.Count == 0) return new ActionResult(false, L.T("Nessun DNS scelto nelle impostazioni.", "No DNS chosen in the settings."));
                    for (var i = 0; i < wanted.Count; i++)
                    {
                        var args = i == 0
                            ? $"interface ipv4 set dnsservers name=\"{adapter}\" static {wanted[0]} primary validate=no"
                            : $"interface ipv4 add dnsservers name=\"{adapter}\" {wanted[i]} index={i + 1} validate=no";
                        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false })!;
                        p.WaitForExit(15_000);
                        if (p.ExitCode != 0) return new ActionResult(false, L.T("Windows ha rifiutato il cambio di DNS (serve l'amministratore).", "Windows refused the DNS change (administrator rights needed)."));
                    }
                    return new ActionResult(true, L.T($"DNS di {adapter}: {string.Join(", ", wanted)}.", $"DNS of {adapter}: {string.Join(", ", wanted)}."),
                        Undo: $"netsh interface ipv4 set dnsservers name=\"{adapter}\" dhcp");
                }
                case SetupModule.SetRefresh:
                    return argument is { Length: > 0 } ? Displays.SetMaxRefresh(argument) : new ActionResult(false, L.T("Manca il nome dello schermo.", "The screen name is missing."));

                case SetupModule.SetPowerPlan:
                {
                    if (!Guid.TryParse(argument ?? SetupRules.Balanced, out var wanted)) return new ActionResult(false, L.T("Piano energetico non riconosciuto.", "Power plan not recognised."));
                    var before = ActivePowerPlan();
                    if (PowerSetActiveScheme(IntPtr.Zero, ref wanted) != 0) return new ActionResult(false, L.T("Windows ha rifiutato il cambio di piano energetico.", "Windows refused the power plan change."));
                    return new ActionResult(true, L.T($"Piano energetico: da \"{before?.Name}\" a \"{ActivePowerPlan()?.Name}\".", $"Power plan: from \"{before?.Name}\" to \"{ActivePowerPlan()?.Name}\"."),
                        Undo: before is null ? null : $"powercfg /setactive {before.Value.Guid}");
                }

                case SetupModule.EnableGameMode:
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar"))
                    {
                        key.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                        key.SetValue("AllowAutoGameMode", 1, RegistryValueKind.DWord);
                    }
                    return new ActionResult(true, L.T("Modalità gioco accesa.", "Game Mode on."), Undo: L.T("Impostazioni di Windows → Giochi → Modalità gioco", "Windows Settings → Gaming → Game Mode"));

                case SetupModule.OpenEqualizer:
                {
                    if (Audio.EqualizerFolder() is not { } folder) return new ActionResult(false, L.T("Equalizer APO non è installato.", "Equalizer APO is not installed."));
                    var tool = new[] { "DeviceSelector.exe", "Configurator.exe" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
                    if (tool is null) return new ActionResult(false, L.T("Non trovo il selettore dei dispositivi nella cartella di Equalizer APO.", "Cannot find the Device Selector in the Equalizer APO folder."));
                    // Started "in" its own folder, like its Start-menu shortcut does: it looks for its parts
                    // (the Qt plugins under qt\) relative to where it is started from, and fails without them.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
                    return new ActionResult(true, L.T("Selettore avviato: spunta l'uscita in uso, conferma e riavvia il PC.", "Selector started: tick the output in use, confirm and restart the PC."));
                }

                default:
                    return new ActionResult(false, L.T($"Azione sconosciuta: {action}.", $"Unknown action: {action}."));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Includes the person answering "no" to Windows' administrator prompt.
            return new ActionResult(false, L.T("Non riuscita: ", "Failed: ") + ex.Message);
        }
    }

    public IReadOnlyDictionary<string, string> ReadHardware()
    {
        var parts = new Dictionary<string, string>();
        Try(() =>
        {
            if (Text(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") is { Length: > 0 } cpu)
                parts[L.T("Processore", "Processor")] = cpu.Trim();
        });
        Try(() =>
        {
            if (Displays.Adapters() is { Count: > 0 } cards) parts[L.T("Scheda video", "Graphics card")] = string.Join(" + ", cards);
        });
        Try(() =>
        {
            if (Smbios.ReadRaw() is not { } raw) return;
            var table = Smbios.Parse(raw);
            if (SetupRules.DescribeMemory(Smbios.Memory(table)) is { } memory) parts[L.T("Memoria", "Memory")] = memory;
            if (Smbios.Board(table) is { } board) parts[L.T("Scheda madre", "Motherboard")] = board;
        });
        return parts;
    }

    public IReadOnlyDictionary<string, string> ReadVersions()
    {
        var versions = new Dictionary<string, string>();
        Try(() =>
        {
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var build = Text(Registry.LocalMachine, key, "CurrentBuild");
            if (build is null) return;
            versions["Windows"] = $"{Text(Registry.LocalMachine, key, "DisplayVersion")} build {build}.{Number(Registry.LocalMachine, key, "UBR")}".Trim();
        });
        Try(() =>
        {
            if (Nvidia.DriverVersion() is { } nvidia) versions[L.T("Driver della scheda video", "Graphics driver")] = nvidia;
            else if (DisplayDriverVersion() is { } other) versions[L.T("Driver della scheda video", "Graphics driver")] = other;
        });
        Try(() =>
        {
            if (Smbios.ReadRaw() is { } raw && Smbios.BiosVersion(Smbios.Parse(raw)) is { } bios) versions["BIOS"] = bios;
        });
        return versions;
    }

    /// <summary>One source failing (a missing library, a locked key) must not take the other readings with it.</summary>
    private static void Try(Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or System.Security.SecurityException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception
            or COMException or InvalidCastException)
        {
        }
    }

    private static int? Number(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static string? Text(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name)?.ToString();
    }

    /// <summary>Driver of the first graphics card in use, for cards that are not NVIDIA.</summary>
    private static string? DisplayDriverVersion()
    {
        var cards = Displays.Adapters();
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return null;
        foreach (var name in cls.GetSubKeyNames())
        {
            try
            {
                using var card = cls.OpenSubKey(name);
                if (card?.GetValue("DriverDesc") is string description && cards.Contains(description) && card.GetValue("DriverVersion") is string version)
                    return version;
            }
            catch (System.Security.SecurityException)
            {
                // The "Properties" subkey is not readable without administrator rights: not a card anyway.
            }
        }
        return null;
    }

    private static (string Guid, string Name)? ActivePowerPlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0) return null;
        try
        {
            var guid = Marshal.PtrToStructure<Guid>(pointer);
            uint size = 0;
            _ = PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, null, ref size);
            var buffer = new byte[size];
            var name = size > 0 && PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
                : guid.ToString();
            return (guid.ToString(), name);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, [Out] byte[]? buffer, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>NVIDIA card through the driver's own library: the power limit in force and the driver version.</summary>
    private static class Nvidia
    {
        public static double? PowerLimitWatts() =>
            Device() is { } device && nvmlDeviceGetPowerManagementLimit(device, out var milliwatts) == 0 ? milliwatts / 1000d : null;

        /// <summary>The card's name, the BAR window against its memory (Resizable BAR), and its PCIe link.</summary>
        public static (string? Name, long? Bar1Mb, long? VramMb, uint? Gen, uint? GenCard, uint? Width, uint? WidthMax)? Link()
        {
            if (Device() is not { } device) return null;
            var name = new StringBuilder(96);
            string? n = nvmlDeviceGetName(device, name, (uint)name.Capacity) == 0 ? name.ToString() : null;
            long? bar1 = nvmlDeviceGetBAR1MemoryInfo(device, out var b) == 0 ? (long)(b.Total / (1024 * 1024)) : null;
            long? vram = nvmlDeviceGetMemoryInfo(device, out var m) == 0 ? (long)(m.Total / (1024 * 1024)) : null;
            uint? Get(Func<IntPtr, (int, uint)> read)
            {
                try { var (code, value) = read(device); return code == 0 ? value : null; }
                catch (EntryPointNotFoundException) { return null; } // An older driver without that call.
            }
            return (n, bar1, vram,
                Get(d => (nvmlDeviceGetMaxPcieLinkGeneration(d, out var v), v)),
                Get(d => (nvmlDeviceGetGpuMaxPcieLinkGeneration(d, out var v), v)),
                Get(d => (nvmlDeviceGetCurrPcieLinkWidth(d, out var v), v)),
                Get(d => (nvmlDeviceGetMaxPcieLinkWidth(d, out var v), v)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Memory
        {
            public ulong Total, Free, Used;
        }

        public static string? DriverVersion()
        {
            if (Device() is null) return null;
            var text = new StringBuilder(80);
            return nvmlSystemGetDriverVersion(text, (uint)text.Capacity) == 0 && text.Length > 0 ? text.ToString() : null;
        }

        private static IntPtr? Device()
        {
            // While the NVIDIA installer swaps the driver, calling into it can kill the process: not then.
            if (InstallerRunning()) return null;
            try
            {
                return nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out var device) == 0 ? device : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return null; // No NVIDIA driver on this PC.
            }
        }

#pragma warning disable IDE1006 // Names mirror the native library.
        private static bool InstallerRunning()
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("setup"))
            {
                using (p)
                {
                    try
                    {
                        if ((p.MainModule?.FileName ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                    }
                }
            }
            return false;
        }

        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetBAR1MemoryInfo(IntPtr device, out Memory bar1);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint generation);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetGpuMaxPcieLinkGeneration(IntPtr device, out uint generation);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);
#pragma warning restore IDE1006
    }
}
