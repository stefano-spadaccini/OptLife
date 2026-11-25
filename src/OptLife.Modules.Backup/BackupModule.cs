using System.Globalization;
using OptLife.Core;
using OptLife.Modules.Backup.Windows;

namespace OptLife.Modules.Backup;

/// <summary>The disk, git, the network and the running programs, so the judgement can be tested without them.</summary>
public interface IBackupEnvironment
{
    /// <summary>Looks through a folder; null when it does not exist. <paramref name="verify"/> files are read back at random.</summary>
    FolderState? Scan(string path, int verify);

    /// <summary>The same, with every file listed by path, size and time, to compare an original with its copy file by file.</summary>
    FolderState? ScanIndexed(string path, int verify) => Scan(path, verify);

    /// <summary>Whether the disk or network share a path lives on is there at all.</summary>
    bool DriveExists(string path);

    RepoState? Repository(string path);
    bool IsRunning(string process);
    bool Reaches(string address);
    ReportState? ReadReport(string file);
}

/// <summary>
/// Checks that what matters has a recent copy somewhere, that repositories hold no forgotten work,
/// and that whatever carries the copies off this PC is alive. It makes no backups itself: it watches
/// the ones that exist, so that a backup that silently stopped is noticed before it is needed.
/// Everything it watches comes from the settings; with empty lists it does nothing.
/// </summary>
public sealed class BackupModule : IModule
{
    private static readonly TimeSpan FullCheckEvery = TimeSpan.FromHours(6);
    private readonly IBackupEnvironment _env;

    public BackupModule() : this(new WindowsBackupEnvironment()) { }

    internal BackupModule(IBackupEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(
        "backup", L.T("Protezione dati", "Data protection"),
        L.T("Controlla che le copie di sicurezza esistano e siano recenti, che nei repository non resti lavoro dimenticato e che la sincronizzazione sia viva.", "Checks that backups exist and are recent, that no forgotten work is left in repositories and that syncing is alive."), FindingArea.Data);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Table("copies", [L.T("Nome", "Name"), L.T("Cartella della copia", "Backup folder"), L.T("Giorni", "Days"), L.T("Cartella originale", "Original folder")], L.T("Copie da tenere d'occhio", "Backups to watch"),
            L.T("Scritte come: nome | cartella della copia | giorni | cartella originale. Senza l'originale conta da quanti giorni la copia non viene aggiornata; " +
            "con l'originale conta di quanti giorni la copia è rimasta indietro rispetto a lui.",
            "Written as: name | backup folder | days | original folder. Without the original it counts how many days the backup has not been updated; with the original it counts how many days the backup is behind it.")),
        SettingDefinition.List("repositories", L.T("Repository git", "Git repositories"),
            L.T("Cartelle dei progetti sotto git. Segnala le modifiche mai salvate in un commit e i commit mai inviati al server.", "Project folders under git. Reports changes never committed and commits never pushed to the server.")),
        SettingDefinition.Int("repoDays", L.T("Giorni prima di segnalare un repository", "Days before reporting a repository"),
            L.T("Il lavoro in corso non è un problema: lo diventa quando resta fermo così a lungo.", "Work in progress is not a problem: it becomes one when it sits still this long."), 7, 1, 90),
        SettingDefinition.Table("programs", [L.T("Nome", "Name"), L.T("Processo", "Process")], L.T("Programmi che devono restare aperti", "Programs that must stay open"),
            L.T("Scritti come: nome | processo (senza .exe). Per esempio il programma che sincronizza con il NAS.", "Written as: name | process (without .exe). For example the program that syncs with the NAS.")),
        SettingDefinition.Table("hosts", [L.T("Nome", "Name"), L.T("Indirizzo", "Address"), L.T("Giorni", "Days")], L.T("Dispositivi che devono farsi vedere", "Devices that must show up"),
            L.T("Scritti come: nome | indirizzo | giorni. Segnala solo quando non risponde da più giorni di così: un NAS in un'altra casa può mancare per un po'.", "Written as: name | address | days. Reports only when it has not answered for longer than that: a NAS in another house can be away for a while.")),
        SettingDefinition.Table("reports", [L.T("Nome", "Name"), "File", L.T("Giorni", "Days"), L.T("Parole", "Words")], L.T("Resoconti da leggere", "Reports to read"),
            L.T("Scritti come: nome | file | giorni | parole separate da virgola. Segnala se il file è più vecchio di così o contiene una di quelle parole (per esempio ERRORE, AVVISO).", "Written as: name | file | days | comma-separated words. Reports if the file is older than that or contains one of those words (for example ERROR, WARNING).")),
        SettingDefinition.Int("verifyFiles", L.T("File da rileggere a ogni controllo", "Files to re-read at each check"),
            L.T("Per ogni copia, quanti file scelti a caso vengono riletti per intero, per accorgersi di una copia rovinata. 0 = nessuno.", "For each backup, how many random files are read back in full, to notice a damaged backup. 0 = none."), 20, 0, 200),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo delle copie", "Checking the backups"), TimeSpan.FromMinutes(30), CheckAsync),
    ];

    private Task CheckAsync(IModuleContext ctx, CancellationToken ct)
    {
        // Reading thousands of files is the last thing a game needs.
        if (ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;

        var s = ctx.Settings;
        var now = ctx.Now;
        var ci = CultureInfo.InvariantCulture;

        // ── Light, every time: is the program open, does the device answer ──
        var programs = new List<NewFinding>();
        foreach (var p in s.GetList("programs").Select(BackupRules.Split).Where(p => p.Length >= 2 && p[1].Length > 0))
        {
            var stateKey = $"missing:{p[1].ToLowerInvariant()}";
            // Right after logon a program may simply not have started yet: it must be missing twice in a row.
            var missing = _env.IsRunning(p[1]) ? 0 : (int.TryParse(ctx.GetState(stateKey), out var n) ? n : 0) + 1;
            ctx.SetState(stateKey, missing == 0 ? null : missing.ToString(ci));
            if (BackupRules.Program(p[0], p[1], missing) is { } f) programs.Add(f);
        }
        ctx.SyncFindings("programs", programs);

        var hosts = new List<NewFinding>();
        foreach (var h in s.GetList("hosts").Select(BackupRules.Split).Where(h => h.Length >= 3 && int.TryParse(h[2], out _)))
        {
            var id = h[1].ToLowerInvariant();
            if (ctx.GetState($"since:{id}") is null) ctx.SetState($"since:{id}", now.ToString("o", ci));
            if (_env.Reaches(h[1])) ctx.SetState($"seen:{id}", now.ToString("o", ci));
            var seen = ctx.GetState($"seen:{id}") is { } t ? DateTimeOffset.Parse(t, ci) : (DateTimeOffset?)null;
            var since = DateTimeOffset.Parse(ctx.GetState($"since:{id}")!, ci);
            if (BackupRules.Host(h[0], h[1], int.Parse(h[2], ci), seen, since, now) is { } f) hosts.Add(f);
        }
        ctx.SyncFindings("hosts", hosts);

        // ── Heavy, a few times a day, when the lists were edited, or when the person asks: folders, repositories, reports ──
        var lists = string.Join('\n', new[] { "copies", "repositories", "reports" }.SelectMany(k => s.GetList(k)))
            + $"\n{s.GetInt("repoDays")}|{s.GetInt("verifyFiles")}";
        var last = ctx.GetState("full_at") is { } at ? DateTimeOffset.Parse(at, ci) : (DateTimeOffset?)null;
        if (!ctx.OnDemand && last is not null && now - last < FullCheckEvery && ctx.GetState("full_for") == lists) return Task.CompletedTask;

        var picture = new Dictionary<string, string>();
        var copies = new List<NewFinding>();
        var verify = s.GetInt("verifyFiles");
        foreach (var entry in s.GetList("copies").Select(CopyEntry.Parse).OfType<CopyEntry>())
        {
            ct.ThrowIfCancellationRequested();
            var there = _env.DriveExists(entry.Copy);
            // With an original: both listed file by file, so "in step" means every file is there, not that two dates are close.
            var paired = entry.Origin is not null;
            var copy = there ? (paired ? _env.ScanIndexed(entry.Copy, verify) : _env.Scan(entry.Copy, verify)) : null;
            var origin = paired && _env.DriveExists(entry.Origin!) ? _env.ScanIndexed(entry.Origin!, 0) : null;
            var finding = BackupRules.Copy(entry, copy, there, origin, now);
            if (finding is not null) copies.Add(finding);
            picture[L.T("copia · ", "backup · ") + entry.Name] = !there ? L.T("disco non raggiungibile", "disk not reachable")
                : copy is null ? L.T("non esiste", "does not exist")
                : L.T($"{copy.Files} file, ultimo del {copy.Newest.ToLocalTime():dd/MM/yyyy}", $"{copy.Files} files, newest from {copy.Newest.ToLocalTime():dd/MM/yyyy}") + (finding is null ? "" : L.T(" (da sistemare)", " (to fix)"));
        }
        ctx.SyncFindings("copies", copies);

        var repos = new List<NewFinding>();
        foreach (var path in s.GetList("repositories").Where(p => p.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            var repo = _env.Repository(path);
            var found = BackupRules.Repository(path, repo, s.GetInt("repoDays"), now).ToList();
            repos.AddRange(found);
            picture[$"repository · {Path.GetFileName(path.TrimEnd('\\', '/'))}"] = repo is null ? L.T("non leggibile", "not readable")
                : L.T($"{repo.Changed} modifiche non salvate, {repo.Ahead} commit non inviati", $"{repo.Changed} uncommitted changes, {repo.Ahead} commits not pushed") + (found.Any(f => f.Severity > Severity.Info) ? L.T(" (da sistemare)", " (to fix)") : "");
        }
        ctx.SyncFindings("repos", repos);

        var reports = new List<NewFinding>();
        foreach (var r in s.GetList("reports").Select(BackupRules.Split).Where(r => r.Length >= 3 && int.TryParse(r[2], out _)))
        {
            var words = r.Length > 3 ? r[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            var report = _env.ReadReport(r[1]);
            var finding = BackupRules.Report(r[0], r[1], int.Parse(r[2], ci), words, report, now);
            if (finding is not null) reports.Add(finding);
            picture[L.T("resoconto · ", "report · ") + r[0]] = report is null ? L.T("non esiste", "does not exist")
                : L.T("del ", "from ") + $"{report.Modified.ToLocalTime():dd/MM/yyyy}" + (finding is null ? "" : L.T(" (da sistemare)", " (to fix)"));
        }
        ctx.SyncFindings("reports", reports);

        ctx.SetState("full_at", now.ToString("o", ci));
        ctx.SetState("full_for", lists);

        // One line in the history whenever the overall picture changes.
        var signature = string.Join('\n', picture.Select(p => $"{p.Key}={p.Value}"));
        if (picture.Count > 0 && signature != ctx.GetState("picture"))
        {
            ctx.SetState("picture", signature);
            var wrong = copies.Count + reports.Count + repos.Count(f => f.Severity > Severity.Info);
            ctx.RecordEvents([new NewEvent(now, "backup_check", Severity.Info,
                (picture.Count == 1 ? L.T("Protezione dati: 1 voce controllata, ", "Data protection: 1 item checked, ") : L.T($"Protezione dati: {picture.Count} voci controllate, ", $"Data protection: {picture.Count} items checked, "))
                    + (wrong == 0 ? (picture.Count == 1 ? L.T("a posto", "fine") : L.T("tutte a posto", "all fine")) : wrong == 1 ? L.T("1 da sistemare", "1 to fix") : L.T($"{wrong} da sistemare", $"{wrong} to fix")),
                "backup_check", Data: picture)]);
        }
        return Task.CompletedTask;
    }
}
