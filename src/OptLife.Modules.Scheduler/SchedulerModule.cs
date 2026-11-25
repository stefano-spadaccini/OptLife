using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Scheduler.Windows;

namespace OptLife.Modules.Scheduler;

/// <summary>A program or script the scheduler started, followed until it ends.</summary>
public interface IJobProcess
{
    int Pid { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
}

/// <summary>What the module needs from the machine, so the rules can be tested without starting anything.</summary>
public interface ISchedulerEnvironment
{
    /// <summary>When Windows last started.</summary>
    DateTimeOffset BootTime { get; }

    bool Exists(string file);

    /// <summary>
    /// For a job that runs as administrator in an installed copy: why its file is not safe to run that way (someone who is not an
    /// administrator could change it, and it would then run with full rights); null when it is safe, or the copy is portable.
    /// </summary>
    string? Unsafe(Job job) => null;

    /// <summary>Starts the job as the person (not as administrator, unless the job asks for it); null process with the reason when it cannot.</summary>
    (IJobProcess? Process, string? Error) Start(Job job);

    /// <summary>The processes that belong to the job (started by it, or living in the program's folder) and the data each has moved so far.</summary>
    IReadOnlyDictionary<int, long> Probe(Job job, int? rootPid);

    /// <summary>Closes those processes: first as the X button would, then for good.</summary>
    void Close(IReadOnlyCollection<int> pids);

    /// <summary>
    /// The process a run started, found again after OptLife restarted, to wait for its end and read its exit code; null when it
    /// is gone, or when that number now belongs to a process started after <paramref name="startedBy"/> (Windows reuses numbers).
    /// </summary>
    IJobProcess? Attach(int pid, DateTimeOffset startedBy) => null;
}

/// <summary>
/// The jobs OptLife runs by itself, in place of Windows scheduled tasks: a script at a set time (the weekly backup),
/// or a program opened for a while and closed once it has gone quiet (the NAS sync, which then does not sit in memory
/// all day). A moment missed while the PC was off is made up at the next chance; while a game is running nothing starts.
/// </summary>
public sealed class SchedulerModule : IModule
{
    public const string RunNow = "run-now", BoardKind = "scheduler_board", RunKind = "job_run";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private readonly ISchedulerEnvironment _env;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _waiting = new(StringComparer.OrdinalIgnoreCase);

    public SchedulerModule() : this(new WindowsSchedulerEnvironment()) { }

    /// <param name="delay">How to wait between two looks at a running job (the tests move a fake clock instead).</param>
    internal SchedulerModule(ISchedulerEnvironment env, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _env = env;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Waits for the jobs running now (for the tests).</summary>
    internal Task WhenDone() => Task.WhenAll(_running.Values);

    public ModuleInfo Info { get; } = new(
        "scheduler", L.T("Pianificatore", "Scheduler"),
        L.T("Fa girare da solo i lavori che gli scrivi: uno script a un'ora fissa, o un programma aperto il tempo che serve e richiuso quando ha finito. Recupera i giri saltati a PC spento e non parte mentre giochi.",
            "Runs by itself the jobs you give it: a script at a set time, or a program opened as long as needed and closed when it is done. It makes up runs missed while the PC was off and does not start while you play."),
        FindingArea.Software);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Table("jobs",
            [L.T("Nome", "Name"), L.T("Quando", "When"), "File", L.T("Argomenti", "Arguments"), L.T("Chiudi se fermo (min)", "Close when idle (min)"), L.T("Al massimo (min)", "At most (min)"), L.T("Admin (sì/no)", "Admin (yes/no)")],
            L.T("Lavori", "Jobs"),
            L.T("Scritti come: nome | quando | file | argomenti | chiudi se fermo (minuti) | al massimo (minuti) | amministratore (sì/no). " +
                "Quando: \"14:00\" ogni giorno, \"dom 19:00\" o \"lun, gio 08:30\" in quei giorni, \"ogni 4h\", \"dopo <nome di un altro lavoro>\", \"avvio\" una volta a ogni accensione; più di uno uniti da \"+\" (\"14:00 + dopo Backup\"). " +
                "Uno script (.ps1, .cmd, .bat, .vbs) gira senza finestra e si aspetta che finisca. Un programma con \"chiudi se fermo\" viene richiuso quando per quei minuti non muove quasi più dati (un programma di sincronizzazione che ha finito); senza, viene solo aperto.",
                "Written as: name | when | file | arguments | close when idle (minutes) | at most (minutes) | administrator (yes/no). " +
                "When: \"14:00\" every day, \"sun 19:00\" or \"mon, thu 08:30\" on those days, \"every 4h\", \"after <name of another job>\", \"startup\" once each time the PC starts; several joined by \"+\" (\"14:00 + after Backup\"). " +
                "A script (.ps1, .cmd, .bat, .vbs) runs without a window and is waited for. A program with \"close when idle\" is closed again once it has moved almost no data for those minutes (a sync program that is done); without it, it is only opened.")),
        SettingDefinition.Int("idleKb", L.T("Fermo sotto (KB/s)", "Idle below (KB/s)"),
            L.T("Un programma che muove meno di così, in media, nei minuti di \"chiudi se fermo\" ha finito.", "A program moving less than this on average during the \"close when idle\" minutes is done."), 30, 1, 100_000),
        SettingDefinition.Bool("waitForGames", L.T("Aspetta la fine delle partite", "Wait for games to end"),
            L.T("Mentre giochi i lavori non partono: aspettano che chiudi il gioco. Quelli avviati da te con AVVIA ORA partono comunque.", "While you play, jobs do not start: they wait for you to close the game. Those you start with RUN NOW start anyway."), true),

        // ── Ready-made jobs: one switch each (and the file or folder they need), all off for anyone at first ──
        SettingDefinition.Bool("tplDefender", L.T("Pronto: scansione completa di Defender ogni mese", "Ready-made: Defender full scan every month"),
            L.T("Una volta al mese Microsoft Defender controlla tutti i file del PC (la scansione veloce di ogni giorno non lo fa). Senza finestre; da amministratore.",
                "Once a month Microsoft Defender checks every file on the PC (the daily quick scan does not). No window; as administrator."), false),
        SettingDefinition.Bool("tplBackup", L.T("Pronto: copia di sicurezza di una cartella", "Ready-made: backup copy of a folder"),
            L.T("Copia la cartella nella destinazione (un disco esterno, il NAS): solo i file nuovi o cambiati, e non cancella mai niente nella destinazione.",
                "Copies the folder to the destination (an external drive, the NAS): only new or changed files, and it never deletes anything at the destination."), false),
        SettingDefinition.Text("tplBackupFrom", L.T("Copia: cartella da copiare", "Backup: folder to copy"), L.T("Per esempio la cartella Documenti.", "For example the Documents folder."), ""),
        SettingDefinition.Text("tplBackupTo", L.T("Copia: dove copiarla", "Backup: where to copy it"), L.T("Una cartella su un altro disco o sul NAS; dentro viene creata una cartella con lo stesso nome.", "A folder on another drive or the NAS; a folder with the same name is made inside it."), ""),
        SettingDefinition.Text("tplBackupWhen", L.T("Copia: quando", "Backup: when"), L.T("Come nei lavori: \"dom 20:00\", \"ogni 24h\"…", "As in jobs: \"sun 20:00\", \"every 24h\"…"), "sun 20:00"),
        SettingDefinition.Bool("tplSync", L.T("Pronto: sincronizzazione che si chiude quando ha finito", "Ready-made: sync that closes when it is done"),
            L.T("Apre il programma di sincronizzazione (NAS, cloud) all'ora scelta e lo richiude quando per qualche minuto non muove più dati.",
                "Opens the sync program (NAS, cloud) at the chosen time and closes it again once it has moved no data for a few minutes."), false),
        SettingDefinition.Text("tplSyncProgram", L.T("Sincronizzazione: programma", "Sync: program"), L.T("Il file .exe del programma.", "The program's .exe file."), ""),
        SettingDefinition.Text("tplSyncArguments", L.T("Sincronizzazione: argomenti", "Sync: arguments"), L.T("Facoltativi, se il programma ne vuole.", "Optional, if the program wants some."), ""),
        SettingDefinition.Text("tplSyncWhen", L.T("Sincronizzazione: quando", "Sync: when"), L.T("Come nei lavori: \"14:00\", \"ogni 6h\"…", "As in jobs: \"14:00\", \"every 6h\"…"), "14:00"),
        SettingDefinition.Int("tplSyncIdle", L.T("Sincronizzazione: chiudi dopo minuti fermo", "Sync: close after idle minutes"), L.T("Minuti senza dati mossi prima di richiuderlo.", "Minutes without data moved before closing it."), 15, 1, 240),
        SettingDefinition.Bool("tplScript", L.T("Pronto: script da riapplicare", "Ready-made: script to reapply"),
            L.T("Fa girare di nuovo uno script a intervalli (per esempio per rimettere un'impostazione che Windows cambia dopo la sospensione).",
                "Runs a script again at intervals (for example to put back a setting Windows changes after sleep)."), false),
        SettingDefinition.Text("tplScriptFile", L.T("Script: file", "Script: file"), L.T("Un file .ps1, .cmd o .bat.", "A .ps1, .cmd or .bat file."), ""),
        SettingDefinition.Text("tplScriptWhen", L.T("Script: quando", "Script: when"), L.T("Come nei lavori: \"ogni 30m\", \"avvio\"…", "As in jobs: \"every 30m\", \"startup\"…"), "every 30m"),
        SettingDefinition.Bool("tplScriptAdmin", L.T("Script: da amministratore", "Script: as administrator"), L.T("Solo se lo script lo richiede.", "Only if the script needs it."), false),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("run", L.T("Lavori pianificati", "Scheduled jobs"), Poll, TickAsync, Continuous: true),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(RunNow, L.T("Avvia ora un lavoro", "Run a job now"),
            L.T("Fa partire subito un lavoro del Pianificatore, anche durante una partita. Parte entro mezzo minuto.", "Starts a Scheduler job right away, even during a game. It starts within half a minute."),
            Permission.Ask, (ctx, argument, _) => Task.FromResult(Request(ctx, argument))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("jobs", L.T("Pianificatore", "Scheduler"), BoardKind, [], Menu: ViewMenu.Tools, Order: 2, Layout: ViewLayout.Checklist,
            EmptyText: L.T("Nessun lavoro: aggiungili in Impostazioni → Pianificatore.", "No jobs: add them in Settings → Scheduler."),
            Description: L.T("I lavori che OptLife fa girare da solo: quando toccano, com'è andata l'ultima volta e quando sarà la prossima. AVVIA ORA lo fa partire subito.",
                "The jobs OptLife runs by itself: when they are due, how the last run went and when the next one is. RUN NOW starts it right away.")),
        new ModuleView("runs", L.T("Esecuzioni", "Runs"), RunKind,
        [
            new ViewColumn(L.T("Quando", "When"), "@time", ColumnKind.When, 130),
            new ViewColumn(L.T("Lavoro", "Job"), "job", Width: 150),
            new ViewColumn(L.T("Esito", "Result"), "outcome", Width: 90),
            new ViewColumn(L.T("Durata", "Duration"), "seconds", ColumnKind.Duration, 90),
            new ViewColumn(L.T("Dettaglio", "Detail"), "detail", Width: 0),
        ], Menu: ViewMenu.Tools, Order: 3,
            EmptyText: L.T("Ancora nessuna esecuzione.", "No runs yet."),
            Description: L.T("Ogni volta che un lavoro del Pianificatore ha girato: perché è partito, quanto è durato e com'è finito.", "Every time a Scheduler job ran: why it started, how long it took and how it ended.")),
    ];

    // ── The round, every half minute ─────────────────────────────────────────

    private Task TickAsync(IModuleContext ctx, CancellationToken ct)
    {
        var jobs = SchedulerRules.All(ctx.Settings);
        var now = ctx.Now;
        var gaming = ctx.Settings.GetBool("waitForGames") && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));
        _waiting.Clear();

        foreach (var job in jobs.Where(j => j.Problem is null))
        {
            var state = Load(ctx, job.Name) ?? Save(ctx, job.Name, new JobState(now));
            if (_running.ContainsKey(job.Name)) continue;

            if (state.Running)
            {
                // OptLife stopped in the middle of a run (an update, a restart). A program it opened is still followed and closed.
                // A script (a backup, a full scan) still running is followed to its end too.
                if (job.ClosesWhenIdle && _env.Probe(job, state.Pid).Count > 0
                    || job.IsScript && state.Pid is { } pid && _env.Attach(pid, (state.LastStart ?? now).AddMinutes(1)) is not null)
                    Launch(ctx, job, L.T("ripreso dopo il riavvio di OptLife", "resumed after OptLife restarted"), adopt: true, ct);
                else
                    Finish(ctx, job, state, state.LastStart ?? now, Outcome.Warn, L.T("interrotto: OptLife si è chiuso mentre girava", "interrupted: OptLife closed while it was running"), "");
                continue;
            }

            var asked = ctx.GetState(RequestKey(job.Name)) is not null;
            var reason = asked
                ? L.T("avviato da te", "started by you")
                : SchedulerRules.Reason(job, state, now, _env.BootTime, name => Load(ctx, name) is { Running: false, LastEnd: { } end } ? end : null);
            if (reason is null) continue;
            if (gaming && !asked)
            {
                _waiting[job.Name] = true;
                continue;
            }
            ctx.SetState(RequestKey(job.Name), null);
            Launch(ctx, job, reason, adopt: false, ct);
        }

        WriteBoard(ctx, jobs);
        ctx.SyncFindings("jobs", [.. jobs.Select(j => FindingOf(ctx, j)).OfType<NewFinding>()]);
        return Task.CompletedTask;
    }

    private void Launch(IModuleContext ctx, Job job, string reason, bool adopt, CancellationToken ct)
    {
        var start = adopt ? Load(ctx, job.Name)?.LastStart ?? ctx.Now : ctx.Now;
        var state = (Load(ctx, job.Name) ?? new JobState(start)) with { LastStart = start, Running = true };
        Save(ctx, job.Name, state);
        _running[job.Name] = Task.Run(async () =>
        {
            try
            {
                await RunAsync(ctx, job, start, reason, adopt, ct);
            }
            finally
            {
                _running.TryRemove(job.Name, out _);
            }
        }, CancellationToken.None);
    }

    private async Task RunAsync(IModuleContext ctx, Job job, DateTimeOffset start, string reason, bool adopt, CancellationToken ct)
    {
        Outcome outcome;
        string detail;
        // Shown on the Activity page and in the pop-up while it runs (by the person when started with RUN NOW).
        var byPerson = reason == L.T("avviato da te", "started by you");
        var activity = ctx.BeginActivity(job.Name, byPerson, timetable: !byPerson);
        activity.Progress(adopt ? L.T("Ripreso dopo il riavvio di OptLife", "Picked up after OptLife restarted") : reason);
        try
        {
            (outcome, detail) = await WorkAsync(ctx, job, start, adopt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activity.End(false, L.T("OptLife si è chiuso: lo riprende al prossimo avvio.", "OptLife closed: it picks it up at the next start."));
            return; // OptLife is closing: the run stays "running", and the next start picks it up again.
        }
        catch (Exception ex)
        {
            (outcome, detail) = (Outcome.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
        activity.End(outcome != Outcome.Fail, detail);
        Finish(ctx, job, Load(ctx, job.Name) ?? new JobState(start), start, outcome, detail, reason);
    }

    private async Task<(Outcome, string)> WorkAsync(IModuleContext ctx, Job job, DateTimeOffset start, bool adopt, CancellationToken ct)
    {
        int? pid = Load(ctx, job.Name)?.Pid;
        IJobProcess? process = null;
        if (!adopt)
        {
            if (!_env.Exists(job.File)) return (Outcome.Fail, L.T($"non trovo {job.File}", $"cannot find {job.File}"));
            if (!job.IsScript && _env.Probe(job, null).Count > 0)
                return (Outcome.Ok, L.T("era già aperto: lasciato com'è", "it was already open: left as it is"));
            if (job.Admin && _env.Unsafe(job) is { } why)
                return (Outcome.Fail, L.T($"non lo avvio da amministratore: {why}. Mettilo in una cartella che cambiano solo gli amministratori (per esempio dentro Programmi) o togli \"admin\".",
                    $"not started as administrator: {why}. Put it in a folder only administrators change (inside Program Files, for example) or remove \"admin\"."));
            var (started, error) = _env.Start(job);
            if (started is null) return (Outcome.Fail, error ?? L.T("non si avvia", "does not start"));
            process = started;
            pid = started.Pid;
            Save(ctx, job.Name, (Load(ctx, job.Name) ?? new JobState(start)) with { Pid = pid });
        }

        var limit = TimeSpan.FromMinutes(job.MaxMinutes);
        if (job.IsScript)
        {
            // Picked up after a restart: wait for the same process, found again by its number.
            if (adopt && pid is { } again) process = _env.Attach(again, start.AddMinutes(1));
            while (ctx.Now - start < limit)
            {
                if (process is null || process.HasExited)
                {
                    var code = process?.ExitCode;
                    return code is null || (code >= 0 && code < job.OkBelow)
                        ? (Outcome.Ok, L.T($"finito in {SchedulerRules.Duration(ctx.Now - start)}", $"done in {SchedulerRules.Duration(ctx.Now - start)}"))
                        : (Outcome.Fail, L.T($"finito con errore (codice {code})", $"ended with an error (code {code})"));
                }
                await _delay(Poll, ct);
            }
            return (Outcome.Fail, L.T($"dopo {job.MaxMinutes} min lavora ancora: l'ho lasciato andare", $"still working after {job.MaxMinutes} min: I left it running"));
        }

        if (!job.ClosesWhenIdle) return (Outcome.Ok, L.T("aperto", "opened"));

        // A sync program: follow the data it moves, close it once it has been quiet for the chosen minutes.
        var samples = new List<(DateTimeOffset, long)>();
        var seen = new Dictionary<int, long>();
        long moved = 0;
        var first = true;
        var kb = ctx.Settings.GetInt("idleKb");
        while (true)
        {
            var probe = _env.Probe(job, pid);
            var now = ctx.Now;
            foreach (var (id, bytes) in probe)
            {
                // The first look is the baseline; a process that appears later (a helper it starts) counts in full.
                if (seen.TryGetValue(id, out var before)) moved += Math.Max(0, bytes - before);
                else if (!first) moved += bytes;
                seen[id] = bytes;
            }
            first = false;
            samples.Add((now, moved));
            var took = SchedulerRules.Duration(now - start);
            if (probe.Count == 0)
                return (Outcome.Ok, L.T($"si è chiuso da solo dopo {took} ({SchedulerRules.Size(moved)} di dati)", $"closed by itself after {took} ({SchedulerRules.Size(moved)} of data)"));
            if (SchedulerRules.IsIdle(samples, now, job.IdleMinutes, kb))
            {
                _env.Close([.. probe.Keys]);
                return (Outcome.Ok, L.T($"finito: {SchedulerRules.Size(moved)} di dati in {took}, chiuso dopo {job.IdleMinutes} min fermo",
                    $"done: {SchedulerRules.Size(moved)} of data in {took}, closed after {job.IdleMinutes} min idle"));
            }
            if (now - start >= limit)
            {
                _env.Close([.. probe.Keys]);
                return (Outcome.Warn, L.T($"chiuso dopo {job.MaxMinutes} min mentre lavorava ancora ({SchedulerRules.Size(moved)} di dati)",
                    $"closed after {job.MaxMinutes} min while still working ({SchedulerRules.Size(moved)} of data)"));
            }
            await _delay(Poll, ct);
        }
    }

    private void Finish(IModuleContext ctx, Job job, JobState state, DateTimeOffset start, Outcome outcome, string detail, string reason)
    {
        var end = ctx.Now;
        Save(ctx, job.Name, state with { LastEnd = end, Last = outcome, Detail = detail, Running = false, Pid = null });
        var data = new Dictionary<string, string>
        {
            ["job"] = job.Name,
            ["outcome"] = OutcomeText(outcome),
            ["result"] = outcome.ToString().ToLowerInvariant(),
            ["seconds"] = ((long)(end - start).TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["detail"] = reason.Length > 0 ? $"{detail} · {reason}" : detail,
        };
        ctx.RecordEvents([new NewEvent(end, RunKind, outcome == Outcome.Fail ? Severity.Medium : Severity.Info, $"{job.Name}: {detail}", GroupKey: job.Name.ToLowerInvariant(), Data: data)]);
    }

    private static ActionResult Request(IModuleContext ctx, string? name)
    {
        var job = SchedulerRules.All(ctx.Settings).FirstOrDefault(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (job is null) return new ActionResult(false, L.T($"Il lavoro \"{name}\" non è più nell'elenco.", $"The job \"{name}\" is no longer in the list."));
        if (job.Problem is not null) return new ActionResult(false, $"{job.Name}: {job.Problem}.");
        ctx.SetState(RequestKey(job.Name), ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        return new ActionResult(true, L.T($"{job.Name}: parte entro mezzo minuto.", $"{job.Name}: starts within half a minute."));
    }

    // ── The page and the problems ────────────────────────────────────────────

    private void WriteBoard(IModuleContext ctx, IReadOnlyList<Job> jobs)
    {
        var now = ctx.Now;
        var items = jobs.Select(job =>
        {
            var state = Load(ctx, job.Name);
            var what = (job.File + " " + job.Arguments).Trim();
            if (job.Problem is not null)
                return new CheckItem(L.T("Lavori", "Jobs"), job.Name, job.Problem, CheckStatus.Wrong, null,
                    L.T("Correggilo in Impostazioni → Pianificatore.", "Fix it in Settings → Scheduler.") + " " + what);
            var when = string.Join(" + ", job.Triggers.Select(t => t.Describe()));
            var running = state?.Running == true || _running.ContainsKey(job.Name);
            var (value, status) = running
                ? (L.T($"in corso dalle {state?.LastStart:HH:mm}", $"running since {state?.LastStart:HH:mm}"), CheckStatus.Watch)
                : _waiting.ContainsKey(job.Name)
                    ? (L.T("aspetta la fine della partita", "waiting for the game to end"), CheckStatus.Watch)
                    : state?.Last is { } last
                        ? ($"{OutcomeText(last)} · {SchedulerRules.Moment(state.LastEnd!.Value, now)}", last switch { Outcome.Ok => CheckStatus.Ok, Outcome.Warn => CheckStatus.Watch, _ => CheckStatus.Wrong })
                        : (L.T("non ancora eseguito", "not run yet"), CheckStatus.Info);
            var advice = (state?.Detail is { Length: > 0 } d ? d + ". " : "") + what;
            return new CheckItem(L.T("Lavori", "Jobs"), job.Name, value, status,
                $"{when} · {L.T("prossima", "next")}: {SchedulerRules.Next(job, state ?? new JobState(now), now)}", advice,
                Buttons: running ? null : [new CheckButton(RunNow, job.Name, L.T("AVVIA ORA", "RUN NOW"))]);
        }).ToList();

        var data = CheckItem.ToData(items);
        var fingerprint = string.Join("\n", data.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value));
        if (ctx.GetState("board") == fingerprint) return;
        ctx.SetState("board", fingerprint);
        ctx.RecordEvents([new NewEvent(now, BoardKind, Severity.Info, L.T($"Pianificatore: {items.Count} lavori", $"Scheduler: {items.Count} jobs"), Data: data)]);
    }

    private static NewFinding? FindingOf(IModuleContext ctx, Job job)
    {
        if (job.Problem is not null)
            return new NewFinding($"bad:{job.Name.ToLowerInvariant()}", Severity.Low, L.T($"Pianificatore: «{job.Name}» non può partire", $"Scheduler: “{job.Name}” cannot start"),
                job.Problem + ".", L.T("Correggilo in Impostazioni → Pianificatore.", "Fix it in Settings → Scheduler."));
        if (Load(ctx, job.Name) is not { Last: Outcome.Fail, Running: false } state) return null;
        return new NewFinding($"failed:{job.Name.ToLowerInvariant()}", Severity.Medium,
            L.T($"Pianificatore: «{job.Name}» non è riuscito", $"Scheduler: “{job.Name}” did not succeed"),
            L.T($"Ultima esecuzione {SchedulerRules.Moment(state.LastEnd!.Value, ctx.Now)}: {state.Detail}.", $"Last run {SchedulerRules.Moment(state.LastEnd!.Value, ctx.Now)}: {state.Detail}."),
            L.T("Controlla il file e gli argomenti in Impostazioni → Pianificatore, poi riprova. Il problema si chiude da solo alla prima esecuzione riuscita.",
                "Check the file and the arguments in Settings → Scheduler, then try again. The problem closes by itself at the first successful run."),
            new FindingFix(RunNow, L.T("Riprova ora", "Try again now"), job.Name), LatestAt: state.LastEnd);
    }

    private static string OutcomeText(Outcome o) => o switch
    {
        Outcome.Ok => L.T("riuscito", "succeeded"),
        Outcome.Warn => L.T("da guardare", "worth a look"),
        _ => L.T("non riuscito", "failed"),
    };

    // ── Memory between rounds ────────────────────────────────────────────────

    private static string StateKey(string name) => "job:" + name.ToLowerInvariant();
    private static string RequestKey(string name) => "request:" + name.ToLowerInvariant();

    private static JobState? Load(IModuleContext ctx, string name)
    {
        try
        {
            return ctx.GetState(StateKey(name)) is { } json ? JsonSerializer.Deserialize<JobState>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JobState Save(IModuleContext ctx, string name, JobState state)
    {
        ctx.SetState(StateKey(name), JsonSerializer.Serialize(state));
        return state;
    }
}
