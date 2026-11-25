using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using OptLife.Core;

namespace OptLife.Modules.FirstSetup.Windows;

/// <summary>
/// The real machine. Hardware from the registry (it works on a brand-new PC whose graphics card still has
/// Windows' basic driver: the PCI vendor id is there from the first boot); installed programs from the
/// uninstall lists; installation through winget, which ships with Windows 11.
/// </summary>
internal sealed class WindowsFirstSetupEnvironment : IFirstSetupEnvironment
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

    public Hardware Hardware()
    {
        var cpu = Vendor.Unknown;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("VendorIdentifier") as string) switch
            {
                "AuthenticAMD" => Vendor.Amd,
                "GenuineIntel" => Vendor.Intel,
                _ => Vendor.Unknown,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        var gpus = new List<Vendor>();
        try
        {
            using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            foreach (var device in pci?.GetSubKeyNames() ?? [])
            {
                var vendor = device.ToUpperInvariant() switch
                {
                    var d when d.StartsWith("VEN_10DE") => Vendor.Nvidia,
                    var d when d.StartsWith("VEN_1002") => Vendor.Amd,
                    var d when d.StartsWith("VEN_8086") => Vendor.Intel,
                    _ => Vendor.Unknown,
                };
                if (vendor == Vendor.Unknown || gpus.Contains(vendor)) continue;
                using var dev = pci!.OpenSubKey(device);
                foreach (var instance in dev?.GetSubKeyNames() ?? [])
                {
                    using var inst = dev!.OpenSubKey(instance);
                    if (string.Equals(inst?.GetValue("ClassGUID") as string, DisplayClass, StringComparison.OrdinalIgnoreCase))
                    {
                        gpus.Add(vendor);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        // A dedicated card first: it is the one the graphics driver step is about.
        return new Hardware(cpu, [.. gpus.OrderBy(v => v == Vendor.Intel ? 1 : 0)]);
    }

    public IReadOnlyList<string> InstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(uninstall);
                foreach (var sub in list?.GetSubKeyNames() ?? [])
                {
                    using var app = list!.OpenSubKey(sub);
                    if (app?.GetValue("DisplayName") is string { Length: > 0 } name) names.Add(name.Trim());
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return [.. names];
    }

    public async Task<ActionResult> Install(string wingetId, CancellationToken ct)
    {
        var start = new ProcessStartInfo("winget",
            $"install --id {wingetId} --exact --silent --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new ActionResult(false, L.T("troppo tempo, interrotta", "took too long, stopped"));
            }
            await output;
            return process.ExitCode switch
            {
                0 => new ActionResult(true, ""),
                // Already there (installed by hand meanwhile, or under a name the list did not recognise).
                unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) => new ActionResult(true, ""),
                // The installer wants a restart to finish: it is installed.
                unchecked((int)0x8A150109) or 3010 => new ActionResult(true, ""),
                // The maker replaced the file at the same address (LatencyMon does at every version): winget refuses it, rightly.
                unchecked((int)0x8A150011) => new ActionResult(false, L.T(
                    "il produttore ha cambiato il file e winget non lo riconosce più, per sicurezza non l'ha installato",
                    "the maker changed the file and winget no longer recognises it, so for safety it did not install it")),
                unchecked((int)0x80072EE7) => new ActionResult(false, L.T("sito di download non raggiungibile, riprova più tardi", "download site not reachable, try again later")),
                var code => new ActionResult(false, L.T($"codice {code:X8}", $"code {code:X8}")),
            };
        }
        catch (Win32Exception)
        {
            return new ActionResult(false, L.T("winget non c'è: aggiorna \"Programma di installazione app\" dal Microsoft Store",
                "winget is missing: update \"App Installer\" from the Microsoft Store"));
        }
    }

    public ActionResult Open(string url)
    {
        try
        {
            // Through Explorer, so the browser opens as the person even when OptLife runs as administrator.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T($"Aperto: {url}", $"Opened: {url}"));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }
}
