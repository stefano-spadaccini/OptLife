using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Drivers;

/// <summary>
/// Watches how the graphics driver gets installed. The NVIDIA App keeps only the log of its last run, so
/// a failed install is overwritten by the next attempt: after every run this module keeps a copy and reads
/// it. A driver install that failed, or that waits for a restart, becomes a problem pointing to the clean way
/// (a restart, then DDU in safe mode). It never installs or removes a driver itself.
/// </summary>
public sealed class DriversModule : IModule
{
    public const string InstallKind = "driver_install";
    private static CultureInfo Lang => L.Culture;

    // A log still being written is an install still running: it is read once it has been quiet this long.
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(3);
    private readonly Func<DateTimeOffset> _bootTime;

    public DriversModule() : this(() => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64)) { }

    internal DriversModule(Func<DateTimeOffset> bootTime) => _bootTime = bootTime;

    public ModuleInfo Info { get; } = new(
        "drivers", L.T("Driver video", "Graphics driver"),
        L.T("Conserva il registro di ogni installazione NVIDIA (l'app tiene solo l'ultimo) e avvisa quando il driver non si è installato bene o aspetta un riavvio.", "Keeps the log of every NVIDIA installation (the app keeps only the last one) and warns when the driver did not install properly or is waiting for a restart."),
        FindingArea.Setup);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.List("installerLogs", L.T("Registri dell'installazione", "Installation logs"),
            L.T("I file in cui l'installatore del driver scrive cosa fa. Quando uno cambia, OptLife ne tiene una copia e lo legge.", "The files where the driver installer writes what it does. When one changes, OptLife keeps a copy and reads it."),
            @"C:\ProgramData\NVIDIA Corporation\NVIDIA app\Installer\Logs\LOG.setup.exe.log",
            @"C:\ProgramData\NVIDIA Corporation\NVIDIA app\Logs\LOG.setup.exe.log"),
        SettingDefinition.Int("keep", L.T("Copie da conservare", "Copies to keep"), L.T("Le più vecchie vengono cancellate (sono copie di OptLife, non i registri originali).", "The oldest are deleted (they are OptLife's copies, not the original logs)."), 15, 1, 200),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo delle installazioni del driver", "Checking driver installations"), TimeSpan.FromMinutes(5), CheckAsync),
    ];

    private Task CheckAsync(IModuleContext ctx, CancellationToken ct)
    {
        var now = ctx.Now;
        foreach (var path in ctx.Settings.GetList("installerLogs"))
        {
            var file = new FileInfo(Environment.ExpandEnvironmentVariables(path));
            if (!file.Exists) continue;
            var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var key = $"seen:{path.ToLowerInvariant()}";
            // A log met for the first time is read too: it is the only trace of the last install. The judgement
            // below only looks at the last 30 days, so an old story does not become a problem.
            if (written.UtcTicks.ToString(CultureInfo.InvariantCulture) == ctx.GetState(key) || now - written < Settled) continue;

            string text;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue; // Still held by the installer: next time.
            }
            ctx.SetState(key, written.UtcTicks.ToString(CultureInfo.InvariantCulture));

            var copy = Keep(ctx, file, text, written);
            var run = InstallRules.Read(text);
            var outcome = !run.Succeeded ? L.T("non riuscita", "failed") : run.RebootRequired ? L.T("riuscita, serve un riavvio", "succeeded, needs a restart") : L.T("riuscita", "succeeded");
            var data = new Dictionary<string, string>
            {
                ["what"] = run.What,
                ["driver"] = run.TouchedDriver ? "1" : "0",
                ["ok"] = run.Succeeded ? "1" : "0",
                ["reboot"] = run.RebootRequired ? "1" : "0",
                ["clean"] = run.Clean ? "1" : "0",
                ["copy"] = copy,
            };
            for (var i = 0; i < run.Errors.Count; i++) data[$"error.{i}"] = run.Errors[i];
            ctx.RecordEvents([new NewEvent(written, InstallKind, run.Succeeded ? Severity.Info : Severity.Medium,
                L.T($"Installazione NVIDIA ({run.What}): {outcome}", $"NVIDIA installation ({run.What}): {outcome}") + (run.Clean ? L.T(" · installazione pulita", " · clean install") : ""),
                "driver_install", Uid: $"{file.Name}|{written.UtcTicks}", Data: data)]);
        }

        ctx.SyncFindings("install", Judge(ctx, now));
        return Task.CompletedTask;
    }

    /// <summary>The latest driver install that did not end well and is still worth hearing about.</summary>
    private IReadOnlyCollection<NewFinding> Judge(IModuleContext ctx, DateTimeOffset now)
    {
        var latest = ctx.History.Events(now.AddDays(-30), now, ctx.ModuleId, InstallKind)
            .Where(e => e.Data.GetValueOrDefault("driver") == "1")
            .MaxBy(e => e.Time);
        if (latest is null) return [];
        var when = latest.Time.ToLocalTime().ToString(L.T("d MMMM 'alle' HH:mm", "d MMMM 'at' HH:mm"), Lang);
        var errors = string.Join(" · ", latest.Data.Where(p => p.Key.StartsWith("error.", StringComparison.Ordinal)).Select(p => p.Value));
        var copy = latest.Data.GetValueOrDefault("copy", "");

        if (latest.Data.GetValueOrDefault("ok") != "1")
            return [new NewFinding($"failed|{latest.Time.UtcTicks}", Severity.Medium,
                L.T("L'ultima installazione del driver video non è riuscita", "The last graphics driver installation failed"),
                L.T($"Il {when} l'installatore NVIDIA ({latest.Data.GetValueOrDefault("what")}) non è arrivato in fondo.", $"On {when} the NVIDIA installer ({latest.Data.GetValueOrDefault("what")}) did not finish.") +
                (errors.Length > 0 ? L.T(" Errori: ", " Errors: ") + errors + "." : "") + (copy.Length > 0 ? L.T(" Registro conservato in ", " Log kept in ") + copy + "." : ""),
                L.T("Riavvia il PC e riprova. Se non basta, fai la pulizia completa: Strumenti → Pulizia driver (DDU), che riavvia in modalità provvisoria; " +
                "poi installa il driver scaricato da nvidia.com con Personalizzata → solo Driver grafico e PhysX, a internet staccato.",
                "Restart the PC and try again. If that is not enough, do a full cleanup: Tools → Driver cleanup (DDU), which restarts in safe mode; then install the driver downloaded from nvidia.com with Custom → Graphics Driver and PhysX only, with the internet unplugged."))];

        // Waiting for a restart until the PC has actually been restarted after the install.
        if (latest.Data.GetValueOrDefault("reboot") == "1" && _bootTime() < latest.Time)
            return [new NewFinding($"reboot|{latest.Time.UtcTicks}", Severity.Medium,
                L.T("Il driver video aspetta un riavvio", "The graphics driver is waiting for a restart"),
                L.T($"Il {when} è stato installato {latest.Data.GetValueOrDefault("what")}, ma finché non riavvii il PC una parte resta a metà: " +
                "è il momento in cui overlay, audio o il pannello NVIDIA possono smettere di funzionare.",
                $"On {when} {latest.Data.GetValueOrDefault("what")} was installed, but until you restart the PC part of it stays half done: that is when the overlay, audio or the NVIDIA panel can stop working."),
                L.T("Riavvia il PC prima di giocare.", "Restart the PC before playing."))];
        return [];
    }

    /// <summary>A copy of the log in the module's own folder, the oldest beyond the limit removed; returns its path.</summary>
    private static string Keep(IModuleContext ctx, FileInfo file, string text, DateTimeOffset written)
    {
        try
        {
            var folder = Path.Combine(ctx.DataDirectory, "registri");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, $"{written.ToLocalTime():yyyyMMdd-HHmmss}-{file.Directory?.Name}-{file.Name}");
            File.WriteAllText(copy, text);
            foreach (var old in new DirectoryInfo(folder).GetFiles().OrderByDescending(f => f.Name).Skip(ctx.Settings.GetInt("keep")))
                old.Delete();
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
