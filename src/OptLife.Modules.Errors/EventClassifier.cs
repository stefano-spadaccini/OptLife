using OptLife.Core;

namespace OptLife.Modules.Errors;

public static class ErrorKinds
{
    public const string AppCrash = "app_crash";
    public const string AppHang = "app_hang";
    public const string BlueScreen = "bsod";
    public const string UnexpectedShutdown = "unexpected_shutdown";
    public const string Hardware = "hardware_error";
    public const string Disk = "disk_error";
    public const string Gpu = "gpu_error";
    public const string Service = "service_error";
    public const string Other = "system_error";
}

/// <summary>Turns a raw Windows event into an OptLife event, or drops it. Pure: no I/O, no clock.</summary>
public static class EventClassifier
{
    private static readonly HashSet<string> DiskProviders = new(StringComparer.OrdinalIgnoreCase)
        { "disk", "stornvme", "storahci", "Ntfs", "Microsoft-Windows-Ntfs" };

    private static readonly HashSet<string> GpuProviders = new(StringComparer.OrdinalIgnoreCase)
        { "Display", "nvlddmkm", "amdkmdag", "amdwddmg", "igfx" };

    /// <param name="ignored">Entries "Provider" or "Provider:EventId" the user does not want to see.</param>
    public static NewEvent? Classify(RawEvent e, IReadOnlyCollection<string> ignored)
    {
        if (ignored.Contains(e.Provider, StringComparer.OrdinalIgnoreCase) ||
            ignored.Contains($"{e.Provider}:{e.Id}", StringComparer.OrdinalIgnoreCase))
            return null;

        var data = new Dictionary<string, string>
        {
            ["log"] = e.Log,
            ["provider"] = e.Provider,
            ["eventId"] = e.Id.ToString(),
        };
        if (e.Message is not null) data["message"] = e.Message;

        NewEvent Make(string kind, Severity severity, string title, string group) =>
            new(e.Time, kind, severity, title, group, $"{e.Log}:{e.RecordId}:{e.Time.ToUnixTimeMilliseconds()}", data);

        string P(int index) => index < e.Properties.Count ? e.Properties[index].Trim() : "";

        switch (e.Provider, e.Id)
        {
            case ("Application Error", 1000):
            {
                var exe = P(0);
                if (exe.Length == 0) break;
                data["exe"] = exe;
                data["version"] = P(1);
                data["faultModule"] = P(3);
                data["code"] = NormalizeCode(P(6));
                data["path"] = P(10);
                return Make(ErrorKinds.AppCrash, Severity.Medium, L.T($"{exe} si è chiuso per un errore", $"{exe} closed because of an error"), $"crash|{exe.ToLowerInvariant()}");
            }

            case ("Application Hang", 1002):
            {
                var exe = P(0);
                if (exe.Length == 0) break;
                data["exe"] = exe;
                data["version"] = P(1);
                data["path"] = P(5);
                return Make(ErrorKinds.AppHang, Severity.Low, L.T($"{exe} ha smesso di rispondere", $"{exe} stopped responding"), $"hang|{exe.ToLowerInvariant()}");
            }

            case ("Windows Error Reporting", _):
            {
                // Program crashes are already covered by "Application Error"; here only the kernel-level
                // reports matter (a device or driver that hung without bringing Windows down).
                var at = e.Properties.ToList().FindIndex(p => p.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase));
                if (at < 0) return null;
                var code = P(at + 3).ToLowerInvariant();
                data["code"] = code;
                var isGpu = code is "141" or "117";
                return Make(
                    isGpu ? ErrorKinds.Gpu : ErrorKinds.Hardware, Severity.Medium,
                    isGpu ? L.T("La scheda video ha smesso di rispondere per un momento", "The graphics card stopped responding for a moment") : L.T($"Un dispositivo o un driver si è bloccato (codice {code})", $"A device or driver hung (code {code})"),
                    $"livekernel|{code}");
            }

            case ("Microsoft-Windows-WER-SystemErrorReporting", 1001):
            {
                var code = P(0).Split(' ', 2)[0];
                data["code"] = code;
                return Make(ErrorKinds.BlueScreen, Severity.High, L.T($"Schermata blu (codice {code})", $"Blue screen (code {code})"), $"bsod|{code.ToLowerInvariant()}");
            }

            case ("Microsoft-Windows-Kernel-Power", 41):
                return Make(ErrorKinds.UnexpectedShutdown, Severity.Medium,
                    L.T("Il PC si è spento o riavviato senza chiudersi correttamente", "The PC turned off or restarted without shutting down properly"), "shutdown");

            case ("EventLog", 6008):
                return null; // Same fact as Kernel-Power 41, reported a second time.

            case ("Microsoft-Windows-WHEA-Logger", _):
                return Make(ErrorKinds.Hardware, e.Level == 3 ? Severity.Medium : Severity.High,
                    e.Level == 3 ? L.T("Errore hardware corretto in automatico", "Hardware error corrected automatically") : L.T("Errore hardware segnalato dal processore", "Hardware error reported by the processor"),
                    $"whea|{e.Id}");

            case ("Service Control Manager", 7000 or 7001 or 7009 or 7011 or 7023 or 7024 or 7031 or 7034):
            {
                // In the two "timeout" events the service name is the second value, not the first.
                var service = e.Id is 7009 or 7011 ? P(1) : P(0);
                if (service.Length == 0) break;
                data["service"] = service;
                var what = e.Id switch
                {
                    7000 or 7001 => L.T("non è partito", "did not start"),
                    7009 or 7011 => L.T("non ha risposto in tempo", "did not respond in time"),
                    7031 or 7034 => L.T("si è chiuso in modo imprevisto", "closed unexpectedly"),
                    _ => L.T("si è fermato con un errore", "stopped with an error"),
                };
                return Make(ErrorKinds.Service, Severity.Low, L.T($"Il servizio \"{service}\" {what}", $"The \"{service}\" service {what}"), $"service|{service.ToLowerInvariant()}");
            }
        }

        if (DiskProviders.Contains(e.Provider))
        {
            // These providers are read at every level; their informational records are not problems.
            if (e.Level is not (1 or 2 or 3)) return null;
            return Make(ErrorKinds.Disk, Severity.High, L.T($"Errore su un disco ({e.Provider}, evento {e.Id})", $"Disk error ({e.Provider}, event {e.Id})"), $"disk|{e.Provider.ToLowerInvariant()}|{e.Id}");
        }

        if (GpuProviders.Contains(e.Provider))
        {
            if (e.Level is not (1 or 2 or 3)) return null;
            return Make(ErrorKinds.Gpu, Severity.Medium,
                e.Id == 4101 ? L.T("Il driver video ha smesso di rispondere ed è stato ripristinato", "The graphics driver stopped responding and was recovered") : L.T($"Errore del driver video ({e.Provider}, evento {e.Id})", $"Graphics driver error ({e.Provider}, event {e.Id})"),
                $"gpu|{e.Provider.ToLowerInvariant()}|{e.Id}");
        }

        if (e.Level is not (1 or 2)) return null;
        if (HappenedDuringShutdown(e.Message)) return null;

        var title = e.Message is { Length: > 0 } m ? (m.Length > 140 ? m[..140] + "…" : m) : L.T($"{e.Provider} (evento {e.Id})", $"{e.Provider} (event {e.Id})");
        return Make(ErrorKinds.Other, e.Level == 1 ? Severity.Medium : Severity.Low, title, $"evt|{e.Provider.ToLowerInvariant()}|{e.Id}");
    }

    /// <summary>
    /// 0x8007045B is ERROR_SHUTDOWN_IN_PROGRESS: something (typically a restore point requested by Windows Update)
    /// asked a component to start while Windows was already shutting down. It says nothing about the PC's health.
    /// The code is checked, not the text, because the text is in the language of Windows.
    /// </summary>
    public static bool HappenedDuringShutdown(string? message) =>
        message is not null && message.Contains("0x8007045b", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCode(string code)
    {
        code = code.Trim().ToLowerInvariant();
        return code.StartsWith("0x") ? code[2..] : code;
    }
}
