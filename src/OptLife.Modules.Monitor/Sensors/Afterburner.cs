using System.Diagnostics;
using Microsoft.Win32;
using OptLife.Core;

namespace OptLife.Modules.Monitor.Sensors;

/// <summary>Where MSI Afterburner stands on this PC: it is what reads the processor temperature (also on Ryzen) without a driver of OptLife's own.</summary>
public enum AfterburnerState { NotInstalled, Closed, Running }

/// <summary>
/// The values the sensors could not read, as "not available" with why and what would bring them. Nothing is ever made up: a
/// missing value is never a 0. When Afterburner is opened OptLife reads it by itself, with nothing to set.
/// </summary>
public static class MissingValues
{
    /// <summary>MSI's own page for Afterburner: the only place the button opens.</summary>
    public const string AfterburnerPage = "https://www.msi.com/Landing/afterburner/graphics-cards";

    public const string OpenSite = "open-site", StartAfterburner = "start-afterburner";

    /// <param name="read">The keys the sensors gave this round.</param>
    public static IReadOnlyList<LiveValue> For(IReadOnlySet<string> read, AfterburnerState afterburner)
    {
        var list = new List<LiveValue>();
        var (why, fix) = afterburner switch
        {
            AfterburnerState.NotInstalled => (
                L.T("Per la temperatura del processore installa MSI Afterburner (gratuito, dal sito di MSI). Appena è aperto OptLife lo trova da solo: non c'è niente da impostare.",
                    "For the processor temperature install MSI Afterburner (free, from MSI's site). As soon as it is open OptLife finds it by itself: there is nothing to set."),
                new FindingFix(OpenSite, L.T("Sito di MSI Afterburner", "MSI Afterburner site"), AfterburnerPage)),
            AfterburnerState.Closed => (
                L.T("MSI Afterburner è installato ma non è aperto: avvialo (nelle sue impostazioni puoi farlo partire con Windows) e OptLife legge la temperatura da lì.",
                    "MSI Afterburner is installed but not open: start it (in its settings it can start with Windows) and OptLife reads the temperature from it."),
                new FindingFix(StartAfterburner, L.T("Avvia Afterburner", "Start Afterburner"))),
            _ => (
                L.T("MSI Afterburner è aperto ma non misura la temperatura del processore: in Afterburner → Impostazioni → Monitoraggio spunta \"CPU temperature\".",
                    "MSI Afterburner is open but does not measure the processor temperature: in Afterburner → Settings → Monitoring tick \"CPU temperature\"."),
                (FindingFix?)null),
        };
        if (!read.Contains("cpu.temp"))
            list.Add(LiveValue.Unavailable("cpu.temp", L.T("Temperatura", "Temperature"), Groups.Cpu, "°C", why, fix));

        // No graphics card values at all: no NVIDIA driver library. Afterburner reads AMD cards too.
        if (!read.Any(k => k.StartsWith("gpu.", StringComparison.Ordinal)))
            list.Add(LiveValue.Unavailable("gpu.temp", L.T("Temperatura", "Temperature"), Groups.Gpu, "°C",
                afterburner == AfterburnerState.Running
                    ? L.T("Né il driver della scheda video né MSI Afterburner danno valori della scheda: con una scheda NVIDIA arrivano dal driver, con una AMD da Afterburner (Impostazioni → Monitoraggio).",
                        "Neither the graphics driver nor MSI Afterburner give graphics card values: with an NVIDIA card they come from the driver, with an AMD one from Afterburner (Settings → Monitoring).")
                    : L.T("Con una scheda NVIDIA i valori arrivano dal suo driver; con una scheda AMD servono MSI Afterburner aperto.",
                        "With an NVIDIA card the values come from its driver; with an AMD card MSI Afterburner must be open."),
                afterburner == AfterburnerState.Running ? null : fix));
        return list;
    }
}

/// <summary>Afterburner on the real PC: installed (its uninstall entry or its usual folder) and running. Looked at once a minute.</summary>
internal static class AfterburnerOnPc
{
    private static DateTime _at;
    private static AfterburnerState _state;
    private static string? _exe;

    public static AfterburnerState State()
    {
        if (DateTime.UtcNow - _at < TimeSpan.FromMinutes(1)) return _state;
        _at = DateTime.UtcNow;
        _exe = Find();
        _state = Running() ? AfterburnerState.Running : _exe is null ? AfterburnerState.NotInstalled : AfterburnerState.Closed;
        return _state;
    }

    public static string? Exe() => _exe ??= Find();

    private static bool Running()
    {
        var found = Process.GetProcessesByName("MSIAfterburner");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    private static string? Find()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Afterburner");
                if (key?.GetValue("InstallLocation") is string folder && File.Exists(Path.Combine(folder, "MSIAfterburner.exe")))
                    return Path.Combine(folder, "MSIAfterburner.exe");
                if (key?.GetValue("UninstallString") is string uninstall && Path.GetDirectoryName(uninstall.Trim('"')) is { } dir
                    && File.Exists(Path.Combine(dir, "MSIAfterburner.exe")))
                    return Path.Combine(dir, "MSIAfterburner.exe");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
            }
        }
        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner", "MSIAfterburner.exe");
        return File.Exists(usual) ? usual : null;
    }
}
