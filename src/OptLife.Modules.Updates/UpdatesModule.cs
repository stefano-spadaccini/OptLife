using System.Globalization;
using System.Text.Json;
using OptLife.Modules.Updates.Windows;

namespace OptLife.Modules.Updates;

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface IUpdatesEnvironment
{
    /// <summary>What winget can update; null when winget is missing or did not answer in time.</summary>
    Task<IReadOnlyList<AppUpdate>?> WingetUpgrades(CancellationToken ct);

    /// <summary>Updates one program with winget: as administrator when installed for the whole PC, as the person when installed for them; stopped after <paramref name="limit"/>.</summary>
    Task<UpgradeAttempt> WingetUpgrade(string id, AppScope scope, TimeSpan limit, CancellationToken ct);

    AppScope ScopeOf(AppUpdate app) => AppScope.Unknown;

    /// <summary>The folder the program is installed in (InstallLocation of its uninstall entry); null when it does not say.</summary>
    string? InstallLocationOf(AppUpdate app) => null;

    /// <summary>The same update, told to install into <paramref name="location"/> (winget --location) when it is not null.</summary>
    Task<UpgradeAttempt> WingetUpgradeAt(string id, AppScope scope, string? location, TimeSpan limit, CancellationToken ct) => WingetUpgrade(id, scope, limit, ct);

    /// <summary>Where installers put programs by default on this PC (Program Files, AppData, ProgramData).</summary>
    IReadOnlyList<string> UsualFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ];

    /// <summary>Minutes since the last key or mouse input.</summary>
    int IdleMinutes() => 0;

    /// <summary>The package ids winget would install along with this one; null when it could not be asked.</summary>
    Task<IReadOnlyList<string>?> PackageDependencies(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);

    /// <summary>Names of the installed programs (uninstall lists), to see what an update added.</summary>
    IReadOnlyList<string> InstalledNames() => [];

    /// <summary>What Windows Update has ready and not installed (read only); null when it did not answer.</summary>
    Task<IReadOnlyList<WindowsUpdateItem>?> WindowsUpdates(CancellationToken ct);

    /// <summary>"Get the latest updates as soon as they're available" is on.</summary>
    bool PreviewUpdatesOn();

    GraphicsDriver? Graphics();

    /// <summary>The first program in the uninstall lists whose name holds this text.</summary>
    InstalledProgram? Program(string namePart);

    BiosInfo? Bios();

    ActionResult Open(string target);

    /// <summary>The names in a folder (files only); null when it is not there.</summary>
    IReadOnlyList<string>? FilesIn(string folder) => null;

    /// <summary>Creates the folder if needed and writes the BIOS note in it.</summary>
    ActionResult WriteNote(string folder, string text) => new(false, "");

    /// <summary>Sends these files of the folder to the Recycle Bin, one at a time; never a real delete.</summary>
    ActionResult RecycleFiles(string folder, IReadOnlyList<string> names) => new(false, "");

    /// <summary>When Windows last started.</summary>
    DateTimeOffset? BootedAt() => null;
}

/// <summary>
/// Updates, all on one page: programs winget can update (with a button each), emulators with a new version
/// (read from the history the Emulation module leaves, when there is one), graphics and chipset drivers, BIOS, and what
/// Windows Update has waiting. It updates programs only through winget, as the person; drivers, BIOS and
/// Windows itself are only read, with the link to the official page.
/// </summary>
public sealed class UpdatesModule : IModule
{
    public const string BoardKind = "updates_board";
    private readonly IUpdatesEnvironment _env;

    public UpdatesModule() : this(new WindowsUpdatesEnvironment()) { }

    internal UpdatesModule(IUpdatesEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("updates", L.T("Aggiornamenti", "Updates"),
        L.T("Programmi, emulatori, driver, BIOS e Windows Update: cosa c'è di nuovo da installare, e i programmi si aggiornano con un pulsante.",
            "Programs, emulators, drivers, BIOS and Windows Update: what is new to install, and programs update with one button."),
        FindingArea.Software);

    private static string Notify => L.T("avvisa", "notify");
    private static string AutoIdle => L.T("da solo quando non uso il PC", "automatically when I am away");
    private static string AutoAt => L.T("da solo all'orario scelto", "automatically at the chosen time");

    public IReadOnlyList<SettingDefinition> Settings { get; } = Grouped(
    [
        SettingDefinition.Choice("updates", L.T("Aggiornamento dei programmi", "Program updates"),
            L.T("Avvisa: ti dice cosa c'è da aggiornare e lo fai col pulsante. Da solo: OptLife aggiorna con winget i programmi (non quelli da non aggiornare né quelli che si aggiornano dal loro programma) quando non usi il PC da un po', o all'orario scelto, mai durante un gioco. Driver, BIOS e Windows non li tocca mai.",
                "Notify: tells you what can be updated and you do it with the button. Automatically: OptLife updates programs with winget (not those not to update nor those that update from their own program) when you have been away for a while, or at the chosen time, never during a game. Drivers, BIOS and Windows are never touched."),
            Notify, AutoIdle, AutoAt).Guided(L.T("Come vuoi gli aggiornamenti dei programmi? Consigliato: avvisa, e decidi tu.", "How do you want program updates? Suggested: notify, and you decide.")),
        SettingDefinition.Int("idleMinutes", L.T("\"Quando non uso il PC\": dopo quanti minuti", "\"When I am away\": after how many minutes"),
            L.T("Minuti senza tastiera né mouse prima di partire.", "Minutes without keyboard or mouse before starting."), 20, 5, 240),
        SettingDefinition.Text("autoAt", L.T("Orario per aggiornare da solo", "Time to update automatically"),
            L.T("Per esempio 03:00 (con il PC acceso). Una volta al giorno.", "For example 03:00 (with the PC on). Once a day."), "03:00"),
        SettingDefinition.Int("packageMinutes", L.T("Tempo massimo per programma (minuti)", "Maximum time per program (minutes)"),
            L.T("Un installatore che non finisce entro questo tempo (di solito perché ha aperto una finestra e aspetta) viene fermato e segnato da controllare, così gli altri vanno avanti.",
                "An installer that does not finish within this time (usually because it opened a window and is waiting) is stopped and marked to check, so the others go on."), 15, 2, 120),
        SettingDefinition.List("selfUpdating", L.T("Programmi che si aggiornano dal loro programma", "Programs that update from their own program"),
            L.T("Id di winget (anche solo l'inizio) o parte del nome: launcher e programmi col loro sistema di aggiornamento, o che installano la versione nuova accanto alla vecchia. Restano sulla pagina in un gruppo a parte, senza essere riproposti né aggiornati da soli.",
                "winget id (even just its start) or part of the name: launchers and programs with their own updater, or that install the new version beside the old one. They stay on the page in a group of their own, neither offered again nor updated automatically."),
            UpdatesRules.SelfUpdatingDefaults),
        SettingDefinition.List("exclude", L.T("Programmi da non aggiornare", "Programs not to update"),
            L.T("Id di winget o parte del nome: non vengono aggiornati da soli né segnalati, ma restano sulla pagina.",
                "winget id or part of the name: they are neither updated automatically nor reported, but stay on the page.")),
        SettingDefinition.Table("drivers", [L.T("Nome", "Name"), L.T("Nome nei programmi installati", "Name in installed programs"), L.T("Sito", "Site"), L.T("Giorni", "Days")],
            L.T("Driver da tenere d'occhio", "Drivers to keep an eye on"),
            L.T("Scritti come: nome | parte del nome tra i programmi installati | pagina ufficiale | giorni. Segnala quelli installati da più di quei giorni (guarda se c'è una versione nuova). Quelli che non hai non compaiono.",
                "Written as: name | part of the name among installed programs | official page | days. Reports those installed more than that many days ago (check for a newer version). Those you do not have do not show."),
            "AMD chipset | AMD Chipset Software | https://www.amd.com/en/support/download/drivers.html | 180"),
        SettingDefinition.Int("graphicsDays", L.T("Giorni dopo cui guardare il driver video", "Days after which to check the graphics driver"),
            L.T("Un driver video più vecchio di così viene segnalato.", "A graphics driver older than this is reported."), 120, 14, 1000),
        SettingDefinition.Int("biosDays", L.T("Giorni dopo cui guardare il BIOS", "Days after which to check the BIOS"),
            L.T("Un BIOS più vecchio di così è segnato da guardare sulla pagina (mai un problema: si aggiorna solo se serve).", "A BIOS older than this is marked to look at on the page (never a problem: update it only if needed)."), 365, 30, 3000),
        SettingDefinition.Text("biosFolder", L.T("Cartella per aggiornare il BIOS", "Folder to update the BIOS"),
            L.T("Dove mettere il file del BIOS da installare: una cartella del disco di Windows (il BIOS la legge) o la radice di una chiavetta USB FAT32. OptLife la prepara con una nota e la sorveglia.",
                "Where to put the BIOS file to install: a folder on the Windows drive (the BIOS reads it) or the root of a FAT32 USB stick. OptLife prepares it with a note and keeps an eye on it."), @"C:\BIOS").PickFolder(),
        SettingDefinition.Int("everyHours", L.T("Ogni quante ore controllare", "Check every how many hours"),
            L.T("winget e Windows Update si interrogano al massimo così spesso (sono lenti); \"Controlla ora\" lo fa subito.", "winget and Windows Update are asked at most this often (they are slow); \"Check now\" does it at once."), 12, 1, 168),
    ]);

    /// <summary>Headings for the settings, so the list reads as: when, what to leave alone, drivers, how often.</summary>
    private static IReadOnlyList<SettingDefinition> Grouped(IReadOnlyList<SettingDefinition> list) => [.. list.Select(d => d.In(d.Key switch
    {
        "updates" or "idleMinutes" or "autoAt" or "packageMinutes" => L.T("Come e quando aggiornare i programmi", "How and when to update programs"),
        "selfUpdating" or "exclude" => L.T("Programmi da lasciare stare", "Programs to leave alone"),
        "drivers" or "graphicsDays" or "biosDays" or "biosFolder" => L.T("Driver e BIOS (solo controllo, mai installati)", "Drivers and BIOS (only checked, never installed)"),
        _ => L.T("Controllo", "Checking"),
    }))];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo degli aggiornamenti", "Checking for updates"), TimeSpan.FromMinutes(30), (ctx, ct) => Check(ctx, ct, refresh: true)),
        // The updates themselves run in the agent, already administrator: no UAC prompt for programs installed for the whole PC,
        // and they go on with the panel closed.
        new ModuleTask("queue", L.T("Aggiornamenti in coda", "Queued updates"), TimeSpan.FromSeconds(20), RunQueue, Continuous: true),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(UpdatesRules.Upgrade, L.T("Aggiorna i programmi scelti", "Update the chosen programs"),
            L.T("Mette in coda i programmi che spunti: OptLife li aggiorna uno alla volta, anche a pannello chiuso, senza chiederti ogni volta il permesso di amministratore. Segui l'avanzamento in Attività. Qui non compaiono quelli che hai escluso, quelli che si aggiornano dal loro programma e quelli che installerebbero anche altri programmi: li trovi nella pagina Aggiornamenti.",
                "Queues the programs you tick: OptLife updates them one at a time, even with the panel closed, without asking for administrator permission each time. Follow the progress in Activity. Programs you excluded, those that update from their own program and those that would also install other programs are not listed here: you find them on the Updates page."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(Enqueue(ctx, arg, byPerson: true)),
            // Only what OptLife can really update: excluded programs, those that update from their own program
            // and those that would make winget install other programs stay on the page, never in this list.
            Choices: ctx => [.. UpdatesRules.Updatable(Cached<List<AppUpdate>>(ctx, "apps") ?? [], Exclude(ctx), SelfList(ctx), Stubborn(ctx), NeedsOthers(ctx), Managed(ctx))
                .Where(a => a.WholeId)
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new ActionChoice(a.Id, a.Name, $"{a.Installed} → {a.Available}", true, UpdatesRules.AppsGroup))]),
        new ModuleAction(UpdatesRules.UpgradeOne, L.T("Aggiorna un programma", "Update a program"),
            L.T("Mette in coda il programma della riga (o quelli dati, separati da ;).", "Queues the program of the line (or those given, separated by ;)."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(Enqueue(ctx, arg, byPerson: true))),
        new ModuleAction(UpdatesRules.RestartBios, Firmware.RestartLabel, Firmware.RestartDescription,
            // Never by itself, and always with the box ticked: it closes everything that is open.
            Permission.Ask, (_, argument, _) => Task.FromResult(Firmware.RestartIntoSetup(argument)), NeverAuto: true,
            Choices: _ => Firmware.RestartChoices()),
        new ModuleAction(BiosFolder.Prepare, L.T("Prepara la cartella per aggiornare il BIOS", "Prepare the folder to update the BIOS"),
            L.T("Crea la cartella scelta nelle impostazioni (C:\\BIOS) con una nota: scheda, versione, pagina ufficiale, come aggiornare e le impostazioni del BIOS da rimettere dopo, lette adesso da Windows. Non scarica niente.",
                "Creates the folder chosen in the settings (C:\\BIOS) with a note: board, version, official page, how to update and the BIOS settings to put back afterwards, read now from Windows. It downloads nothing."),
            Permission.Ask, (ctx, _, _) => Task.FromResult(DoPrepareBios(ctx))),
        new ModuleAction(BiosFolder.Clean, L.T("Manda nel Cestino i file del BIOS", "Send the BIOS files to the Recycle Bin"),
            L.T("Dopo un aggiornamento riuscito, manda nel Cestino i file della cartella del BIOS (la nota resta). Si riprendono dal Cestino.",
                "After a successful update, sends the BIOS folder's files to the Recycle Bin (the note stays). They can be restored from the Recycle Bin."),
            Permission.Ask, (ctx, _, _) => Task.FromResult(DoCleanBios(ctx)), NeverAuto: true),
        new ModuleAction(UpdatesRules.OpenSite, L.T("Apri la pagina ufficiale", "Open the official page"),
            L.T("Apre il sito del produttore o la pagina di Windows Update.", "Opens the maker's site or the Windows Update page."),
            // Ask, not Auto: a problem that carries this as its fix would otherwise open a window by itself when it appears.
            Permission.Ask, (_, target, _) => Task.FromResult(UpdatesRules.SafeTarget(target) ? _env.Open(target!)
                : new ActionResult(false, L.T("Indirizzo non valido.", "Invalid address.")))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("updates", L.T("Aggiornamenti", "Updates"), BoardKind, [], Days: 365, Menu: ViewMenu.Programs, Order: 1,
            Guide: L.T("Le versioni nuove dei programmi, di Windows e dei driver. OptLife aggiorna i programmi senza chiederti ogni volta il permesso di amministratore; driver e BIOS mai, ti dà solo il link ufficiale.", "New versions of programs, Windows and drivers. OptLife updates programs without asking for administrator permission each time; drivers and BIOS never, it only gives you the official link."),
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\" (winget e Windows Update ci mettono un minuto).", "Not checked yet: press \"Check now\" (winget and Windows Update take a minute)."),
            Description: L.T("Qui vedi i programmi che hanno una versione nuova. AGGIORNA mette il programma in coda e OptLife lo aggiorna da solo, anche a pannello chiuso e senza chiederti il permesso di amministratore; l'avanzamento è in Attività, l'esito app per app in \"Ultimo aggiornamento\". " +
                             "Launcher e programmi col loro aggiornamento stanno in un gruppo a parte. Driver e BIOS: solo la versione e il link al sito ufficiale, OptLife non li installa mai. Gli emulatori si aggiornano dalla pagina Emulazione.",
                             "Here you see the programs that have a new version. UPDATE queues the program and OptLife updates it by itself, even with the panel closed and without asking for administrator permission; progress is in Activity, the outcome app by app in \"Last update run\". " +
                             "Launchers and programs with their own updater are in a group of their own. Drivers and BIOS: only the version and the link to the official site, OptLife never installs them. Emulators are updated from the Emulation page."),
            Layout: ViewLayout.Checklist,
            Buttons: _ =>
            [
                new ViewButton(L.T("Più programmi insieme", "Several at once"), L.T("Aggiorna i programmi scelti", "Update the chosen programs"),
                    L.T("Ti mostra i programmi che winget può aggiornare (quelli da fare già spuntati) e li mette in coda.", "Shows the programs winget can update (those to do already ticked) and queues them."),
                    UpdatesRules.Upgrade, ButtonText: L.T("SCEGLI E AGGIORNA", "PICK AND UPDATE")),
            ]),
    ];

    private static IReadOnlyList<string> Exclude(IModuleContext ctx) => ctx.Settings.GetList("exclude");
    private static IReadOnlyList<string> SelfList(IModuleContext ctx) => ctx.Settings.GetList("selfUpdating");
    private static HashSet<string> Stubborn(IModuleContext ctx) => Cached<HashSet<string>>(ctx, "stubborn") ?? new(StringComparer.OrdinalIgnoreCase);

    private static bool Playing(IModuleContext ctx) => ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));

    private sealed record Queued(IReadOnlyList<string> Ids, bool ByPerson);

    /// <summary>Puts programs in the queue the agent works through (it runs them as administrator: no UAC each time).</summary>
    private static ActionResult Enqueue(IModuleContext ctx, string? argument, bool byPerson)
    {
        var ids = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return ActionResult.Nothing(L.T("Nessun programma scelto.", "No program chosen."));
        var queue = Cached<List<Queued>>(ctx, "queue") ?? [];
        queue.Add(new Queued(ids, byPerson));
        Save(ctx, "queue", queue);
        return new ActionResult(true, L.T($"{ids.Count} programmi in coda: OptLife li aggiorna entro mezzo minuto, uno alla volta, anche a pannello chiuso. Segui l'avanzamento in Attività.",
            $"{ids.Count} programs queued: OptLife updates them within half a minute, one at a time, even with the panel closed. Follow the progress in Activity."));
    }

    /// <summary>The agent's side: takes what is queued and updates it, as one activity with the outcome app by app.</summary>
    private async Task RunQueue(IModuleContext ctx, CancellationToken ct)
    {
        var queue = Cached<List<Queued>>(ctx, "queue") ?? [];
        if (queue.Count == 0) return;
        Save(ctx, "queue", new List<Queued>());
        var ids = queue.SelectMany(q => q.Ids).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byPerson = queue.Any(q => q.ByPerson);
        var activity = ctx.BeginActivity(L.T($"Aggiornamento di {ids.Count} programmi", $"Updating {ids.Count} programs"), byPerson);
        var results = await Run(ctx, ids, activity, ct);

        // Checked, not believed: winget may say "done" (or "nothing to do") and leave the old version. Read the list again: one still
        // listed with the same version did not update. Twice in a row, and it is put aside with those that update themselves.
        if (await _env.WingetUpgrades(ct) is { } after)
        {
            var stubborn = Stubborn(ctx);
            var known = Cached<List<AppUpdate>>(ctx, "apps") ?? [];
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r.Outcome != UpdatesRules.Outcome.Done) continue;
                var still = after.FirstOrDefault(a => a.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
                var before = known.FirstOrDefault(a => a.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
                var key = $"unchanged:{r.Id.ToLowerInvariant()}";
                if (still is null || before is not null && still.Installed != before.Installed)
                {
                    ctx.SetState(key, null);
                    continue;
                }
                results[i] = UpdatesRules.NotChanged(r, still);
                var times = (int.TryParse(ctx.GetState(key), out var n) ? n : 0) + 1;
                ctx.SetState(key, times.ToString(CultureInfo.InvariantCulture));
                if (times >= 2) stubborn.Add(r.Id);
            }
            Save(ctx, "stubborn", stubborn);
            Save(ctx, "apps", after);
            ctx.SetState("apps_at", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        foreach (var r in results.Where(r => r.Outcome is UpdatesRules.Outcome.Done or UpdatesRules.Outcome.Reboot))
            ctx.RecordEvents([new NewEvent(ctx.Now, "app_updated", Severity.Info, L.T($"Aggiornato: {r.Name}", $"Updated: {r.Name}"), "app_updated",
                Data: new Dictionary<string, string> { ["id"] = r.Id, ["detail"] = r.Technical ?? "" })]);
        Save(ctx, "last_run", results);
        ctx.SetState("last_run_at", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        activity.End(results.All(r => r.Outcome is UpdatesRules.Outcome.Done or UpdatesRules.Outcome.Reboot), UpdatesRules.Summary(results));
        await Check(new Forced(ctx), ct, refresh: false);
    }

    private async Task<List<UpdatesRules.RunResult>> Run(IModuleContext ctx, IReadOnlyList<string> ids, IActivity activity, CancellationToken ct)
    {
        var results = new List<UpdatesRules.RunResult>();
        var known = Cached<List<AppUpdate>>(ctx, "apps") ?? [];
        var minutes = ctx.Settings.GetInt("packageMinutes");
        for (var i = 0; i < ids.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var id = ids[i];
            var app = known.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var name = app?.Name ?? id;
            activity.Progress(L.T($"Aggiorno {name}", $"Updating {name}"), i + 1, ids.Count);
            // A dependency that is not on this PC (iTunes for iloader, 23 Nov 2026) would be installed without asking: not done, said.
            var installedBefore = _env.InstalledNames();
            if (await _env.PackageDependencies(id, ct) is { } deps && Dependencies.Missing(deps, installedBefore) is { Count: > 0 } missing)
            {
                var others = string.Join(", ", missing.Select(Dependencies.Name));
                results.Add(new UpdatesRules.RunResult(id, name, UpdatesRules.Outcome.Attention,
                    L.T($"Non l'ho aggiornato: winget installerebbe anche {others}, che non hai. Se ti va bene, aggiornalo a mano; se no, mettilo tra i programmi da non aggiornare.",
                        $"Not updated: winget would also install {others}, which you do not have. If that is fine, update it by hand; if not, put it among the programs not to update."),
                    $"winget show --id {id} · " + string.Join(", ", missing)));
                continue;
            }
            var scope = app is null ? AppScope.Unknown : _env.ScopeOf(app);
            // Installed in a folder of the person's choosing (C:\Software\…): told to winget, or the installer may put the new version
            // in its own default folder (RetroArch went to C:\RetroArch-Win64 on 23 Nov 2026). Checked again afterwards.
            var placedBefore = app is null ? null : _env.InstallLocationOf(app);
            var location = UpdatesRules.CustomLocation(placedBefore, _env.UsualFolders());
            var attempt = await _env.WingetUpgradeAt(id, scope, location, TimeSpan.FromMinutes(minutes), ct);
            var (outcome, why) = attempt.Error is { } error ? (UpdatesRules.Outcome.Failed, error) : UpdatesRules.Explain(attempt.Code, attempt.TimedOut, minutes);
            var technical = $"winget upgrade --id {id} · " + (attempt.TimedOut ? L.T("fermato dopo il tempo massimo", "stopped after the maximum time") : attempt.Code is { } c ? L.T($"codice {c:X8}", $"code {c:X8}") : attempt.Error ?? "")
                + " · " + scope switch { AppScope.Machine => L.T("installato per tutto il PC", "installed for the whole PC"), AppScope.User => L.T("installato per l'utente", "installed for the user"), _ => L.T("installazione non trovata", "installation not found") }
                + (app is null ? "" : $" · {app.Installed} → {app.Available}");
            // What else this update brought: programs that were not there before (winget installs dependencies quietly).
            var added = _env.InstalledNames().Except(installedBefore, StringComparer.OrdinalIgnoreCase)
                .Where(n => !n.StartsWith(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (added.Count > 0)
            {
                why += " " + L.T($"Ha installato anche: {string.Join(", ", added)}. Se non lo vuoi, disinstallalo da Impostazioni → App.", $"It also installed: {string.Join(", ", added)}. If you do not want it, uninstall it from Settings → Apps.");
                if (outcome == UpdatesRules.Outcome.Done) outcome = UpdatesRules.Outcome.Attention;
            }
            if (location is not null) technical += $" · --location \"{location}\"";
            var result = new UpdatesRules.RunResult(id, name, outcome, why, technical);
            // Somewhere else afterwards: said, and never again with winget (put with those that update from their program).
            if (placedBefore is not null && app is not null && _env.InstallLocationOf(app) is { } placedAfter
                && !string.Equals(placedAfter.Trim().TrimEnd('\\'), placedBefore.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                result = UpdatesRules.Moved(result, placedBefore, placedAfter);
                var stubborn = Stubborn(ctx);
                stubborn.Add(id);
                Save(ctx, "stubborn", stubborn);
            }
            results.Add(result);
        }
        return results;
    }

    private async Task Check(IModuleContext ctx, CancellationToken ct, bool refresh)
    {
        var playing = Playing(ctx);
        if (playing && !ctx.OnDemand) return;
        var s = ctx.Settings;
        var every = TimeSpan.FromHours(s.GetInt("everyHours"));

        if (refresh && Due(ctx, "apps_at", every))
        {
            if (await _env.WingetUpgrades(ct) is { } apps)
            {
                Save(ctx, "apps", apps);
                await LookAtDependencies(ctx, apps, ct);
            }
            ctx.SetState("apps_at", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        if (refresh && Due(ctx, "windows_at", every))
        {
            if (await _env.WindowsUpdates(ct) is { } windows) Save(ctx, "windows", windows);
            ctx.SetState("windows_at", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        }

        // Automatic: once a day, when the person has been away a while or at the chosen time, never during a game.
        if (refresh && !playing && AutoDue(ctx))
        {
            var todo = UpdatesRules.Updatable(Cached<List<AppUpdate>>(ctx, "apps") ?? [], Exclude(ctx), SelfList(ctx), Stubborn(ctx), NeedsOthers(ctx), Managed(ctx)).Select(a => a.Id).ToList();
            ctx.SetState("auto_day", ctx.Now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (todo.Count > 0) Enqueue(ctx, string.Join(';', todo), byPerson: false);
        }

        var drivers = new List<(DriverRule, InstalledProgram)>();
        foreach (var rule in s.GetList("drivers").Select(DriverRule.Parse).OfType<DriverRule>())
            if (_env.Program(rule.Installed) is { } program) drivers.Add((rule, program));

        var inputs = new UpdatesRules.Inputs(
            Cached<List<AppUpdate>>(ctx, "apps"), Exclude(ctx), Emulators(ctx),
            _env.Graphics(), s.GetInt("graphicsDays"), drivers, _env.Bios(), s.GetInt("biosDays"),
            Cached<List<WindowsUpdateItem>>(ctx, "windows"), _env.PreviewUpdatesOn(), ctx.Now,
            SelfList(ctx), Stubborn(ctx), Cached<List<UpdatesRules.RunResult>>(ctx, "last_run"),
            ctx.GetState("last_run_at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null,
            NeedsOthers(ctx), Managed(ctx));

        ctx.SyncFindings("updates", [.. UpdatesRules.Findings(inputs)]);

        var board = UpdatesRules.Board(inputs).ToList();
        // The BIOS folder, right under the BIOS line.
        if (inputs.Bios is { } bios)
        {
            var folder = BiosFolderPath(ctx);
            var (row, state) = BiosFolder.Board(bios, folder, _env.FilesIn(folder), Cached<BiosPrepared>(ctx, "bios_prep"), ctx.Now, _env.BootedAt());
            if (state is not null) Save(ctx, "bios_prep", state);
            var biosLine = board.FindIndex(i => i.Label.StartsWith("BIOS · ", StringComparison.Ordinal));
            board.Insert(biosLine < 0 ? board.Count : biosLine + 1, row);
        }
        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("board")) return;
        ctx.SetState("board", signature);
        var count = board.Count(i => i.Status is CheckStatus.Watch or CheckStatus.Wrong);
        ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
            L.T($"Aggiornamenti: {count} da guardare", $"Updates: {count} to look at"), BoardKind, Data: data)]);
    }

    /// <summary>Whether the automatic update should start now: not yet today, and away long enough or past the chosen time.</summary>
    private bool AutoDue(IModuleContext ctx)
    {
        var mode = ctx.Settings.GetChoice("updates");
        if (mode == Notify) return false;
        var local = ctx.Now.ToLocalTime();
        if (ctx.GetState("auto_day") == local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) return false;
        if (mode == AutoIdle) return _env.IdleMinutes() >= ctx.Settings.GetInt("idleMinutes");
        return TimeSpan.TryParse(ctx.Settings.GetText("autoAt"), CultureInfo.InvariantCulture, out var at) && local.TimeOfDay >= at;
    }

    /// <summary>
    /// Emulators with a new version, from the newest page the Emulation module wrote in the history (its amber lines).
    /// Nothing when that module is not there: the two never refer to each other.
    /// </summary>
    private static IReadOnlyList<CheckItem> Emulators(IModuleContext ctx)
    {
        var latest = ctx.History.Events(ctx.Now.AddDays(-30), ctx.Now.AddMinutes(1), "emulation", "emulation_board").MaxBy(e => e.Time);
        return latest is null ? [] : [.. CheckItem.FromData(latest.Data).Where(i => i.Status == CheckStatus.Watch)];
    }

    /// <summary>The chosen folder, expanded; a drive root ("E:") keeps its backslash ("E:\").</summary>
    private static string BiosFolderPath(IModuleContext ctx)
    {
        var chosen = ctx.Settings.GetText("biosFolder").Trim();
        var path = Environment.ExpandEnvironmentVariables(chosen.Length > 0 ? chosen : @"C:\BIOS").TrimEnd('\\');
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }

    /// <summary>The BIOS settings Windows reads now, from the last list the PC settings module wrote ("Secure Boot = on"); empty without it.</summary>
    private static IReadOnlyList<string> BiosSettings(IModuleContext ctx)
    {
        var latest = ctx.History.Events(ctx.Now.AddDays(-30), ctx.Now.AddMinutes(1), "setup", "settings_check").MaxBy(e => e.Time);
        return latest is null ? [] : [.. latest.Data.Where(p => p.Key.StartsWith("bios.", StringComparison.Ordinal) && p.Key != "bios.version" || p.Key.StartsWith("ram.", StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)];
    }

    private ActionResult DoPrepareBios(IModuleContext ctx)
    {
        if (_env.Bios() is not { } bios) return new ActionResult(false, L.T("Windows non dice che BIOS e che scheda ha questo PC.", "Windows does not say which BIOS and board this PC has."));
        var folder = BiosFolderPath(ctx);
        var note = BiosFolder.Readme(bios, UpdatesRules.BiosPage(bios.BoardMaker, bios.Board), BiosSettings(ctx), ctx.Now);
        var r = _env.WriteNote(folder, note);
        if (!r.Ok) return r;
        Save(ctx, "bios_prep", new BiosPrepared(bios.Version, ctx.Now));
        return new ActionResult(true, L.T($"Cartella pronta: {folder}, con la nota {BiosFolder.Note}. Scarica il BIOS dalla pagina ufficiale e mettilo lì.",
            $"Folder ready: {folder}, with the note {BiosFolder.Note}. Download the BIOS from the official page and put it there."));
    }

    private ActionResult DoCleanBios(IModuleContext ctx)
    {
        var folder = BiosFolderPath(ctx);
        var files = (_env.FilesIn(folder) ?? []).Where(f => !f.Equals(BiosFolder.Note, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return ActionResult.Nothing(L.T($"in {folder} non ci sono file da togliere.", $"{folder} has no files to remove."));
        var r = _env.RecycleFiles(folder, files);
        if (r.Ok) Save(ctx, "bios_prep", new BiosPrepared(_env.Bios()?.Version ?? "", ctx.Now));
        return r;
    }

    /// <summary>
    /// The emulators the Emulation page found installed (its green and amber lines), by name: those it updates itself, in their own
    /// folder. Nothing when that module is not there.
    /// </summary>
    private static IReadOnlyList<string> Managed(IModuleContext ctx)
    {
        var latest = ctx.History.Events(ctx.Now.AddDays(-30), ctx.Now.AddMinutes(1), "emulation", "emulation_board").MaxBy(e => e.Time);
        return latest is null ? [] : [.. CheckItem.FromData(latest.Data).Where(i => i.Status is CheckStatus.Ok or CheckStatus.Watch).Select(i => i.Label).Distinct()];
    }

    private sealed record DependencyNote(string Available, IReadOnlyList<string> Missing);

    /// <summary>
    /// For each program that could be updated: what winget would install with it that is not on this PC. Asked once per new
    /// version (winget show is slow), kept in the state, shown on the page before anyone presses UPDATE.
    /// </summary>
    private async Task LookAtDependencies(IModuleContext ctx, IReadOnlyList<AppUpdate> apps, CancellationToken ct)
    {
        var installed = _env.InstalledNames();
        foreach (var a in UpdatesRules.Updatable(apps, Exclude(ctx), SelfList(ctx), Stubborn(ctx), managedEmulators: Managed(ctx)))
        {
            if (ct.IsCancellationRequested) return;
            var key = $"deps:{a.Id.ToLowerInvariant()}";
            if (Cached<DependencyNote>(ctx, key) is { } note && note.Available == a.Available) continue;
            if (await _env.PackageDependencies(a.Id, ct) is not { } deps) continue;
            Save(ctx, key, new DependencyNote(a.Available, Dependencies.Missing(deps, installed)));
        }
    }

    /// <summary>Programs whose update would bring others not on this PC, with their names.</summary>
    private static IReadOnlyDictionary<string, string> NeedsOthers(IModuleContext ctx)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Cached<List<AppUpdate>>(ctx, "apps") ?? [])
            if (Cached<DependencyNote>(ctx, $"deps:{a.Id.ToLowerInvariant()}") is { Missing.Count: > 0 } note && note.Available == a.Available)
                map[a.Id] = string.Join(", ", note.Missing.Select(Dependencies.Name));
        return map;
    }

    private static bool Due(IModuleContext ctx, string key, TimeSpan every) =>
        ctx.OnDemand || ctx.GetState(key) is not { } at || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) || ctx.Now - t >= every;

    private static void Save<T>(IModuleContext ctx, string key, T value) => ctx.SetState(key, JsonSerializer.Serialize(value));

    private static T? Cached<T>(IModuleContext ctx, string key) where T : class
    {
        try
        {
            return ctx.GetState(key) is { } json ? JsonSerializer.Deserialize<T>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The same context, as asked by the person: right after an update the page is rewritten even during a game.</summary>
    private sealed class Forced(IModuleContext inner) : IModuleContext
    {
        public string ModuleId => inner.ModuleId;
        public DateTimeOffset Now => inner.Now;
        public bool OnDemand => true;
        public ISettings Settings => inner.Settings;
        public IHistory History => inner.History;
        public string DataDirectory => inner.DataDirectory;
        public void RecordSample(string metric, double value, DateTimeOffset? at = null) => inner.RecordSample(metric, value, at);
        public void RecordSamples(IEnumerable<KeyValuePair<string, double>> samples, DateTimeOffset? at = null) => inner.RecordSamples(samples, at);
        public void PublishLive(IReadOnlyCollection<LiveValue> values) => inner.PublishLive(values);
        public IReadOnlyList<LiveValue> ReadLive() => inner.ReadLive();
        public int RecordEvents(IEnumerable<NewEvent> events) => inner.RecordEvents(events);
        public void SyncFindings(string scope, IReadOnlyCollection<NewFinding> current) => inner.SyncFindings(scope, current);
        public string? GetState(string key) => inner.GetState(key);
        public void SetState(string key, string? value) => inner.SetState(key, value);
    }
}
