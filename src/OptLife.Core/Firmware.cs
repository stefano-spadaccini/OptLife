using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OptLife.Core;

/// <summary>
/// The PC's firmware as Windows reports it, and the restart straight into its setup screen (the BIOS), the way Windows'
/// own "Advanced start-up → UEFI firmware settings" does. A service of the core any module may offer: it names no module.
/// </summary>
public static class Firmware
{
    /// <summary>True for UEFI, false for the old BIOS (or UEFI in Legacy/CSM mode), null when Windows does not say.</summary>
    public static bool? IsUefi()
    {
        try
        {
            return GetFirmwareType(out var type) ? type switch { 2 => true, 1 => false, _ => null } : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>What a button that restarts into the BIOS is called.</summary>
    public static string RestartLabel => L.T("Riavvia nel BIOS", "Restart into the BIOS");

    public static string RestartDescription => L.T(
        "Riavvia il PC ed entra direttamente nel BIOS (le impostazioni del firmware UEFI), senza dover premere Canc o F2 all'accensione. " +
        "Chiude subito tutti i programmi aperti: salva prima il tuo lavoro.",
        "Restarts the PC straight into the BIOS (the UEFI firmware settings), without pressing Del or F2 at power-on. " +
        "It closes every open program at once: save your work first.");

    /// <summary>The one box to tick before the restart: the confirmation that everything open will be closed.</summary>
    public static IReadOnlyList<ActionChoice> RestartChoices() => IsUefi() == true
        ? [new ActionChoice("restart", L.T("Sì, riavvia adesso nel BIOS", "Yes, restart into the BIOS now"),
            L.T("Tutti i programmi aperti vengono chiusi senza chiedere di salvare.", "All open programs are closed without asking to save."), false)]
        : [];

    /// <summary>
    /// Restarts into the firmware setup now ("shutdown /r /fw /t 0"). Only with the tick of <see cref="RestartChoices"/>,
    /// only on UEFI, and it needs administrator rights (the agent and the panel it opens have them).
    /// </summary>
    public static ActionResult RestartIntoSetup(string? argument)
    {
        if (!(argument ?? "").Split(';').Contains("restart")) return new ActionResult(false, L.T("Riavvio non confermato: non ho fatto niente.", "Restart not confirmed: I did nothing."));
        if (IsUefi() != true)
            return new ActionResult(false, L.T("Questo PC non usa un firmware UEFI (o è in modalità Legacy/CSM): da Windows non si può entrare nel BIOS. Premi Canc o F2 all'accensione.",
                "This PC does not use UEFI firmware (or it is in Legacy/CSM mode): Windows cannot open the BIOS. Press Del or F2 at power-on."));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 0")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            });
            if (process is null) return new ActionResult(false, L.T("Non sono riuscito ad avviare shutdown.exe.", "Could not start shutdown.exe."));
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(15_000);
            return process.ExitCode == 0
                ? new ActionResult(true, L.T("Riavvio nel BIOS in corso.", "Restarting into the BIOS."))
                : new ActionResult(false, process.ExitCode == 5
                    ? L.T("Serve l'amministratore: apri il pannello dall'icona di OptLife (che gira da amministratore) e riprova.", "Administrator rights are needed: open the panel from OptLife's tray icon (which runs as administrator) and try again.")
                    : L.T($"Windows non ha accettato il riavvio (codice {process.ExitCode}). {error}", $"Windows refused the restart (code {process.ExitCode}). {error}"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int type);
}
