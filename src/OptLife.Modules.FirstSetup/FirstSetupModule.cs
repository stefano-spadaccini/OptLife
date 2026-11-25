using OptLife.Core;
using OptLife.Modules.FirstSetup.Windows;

namespace OptLife.Modules.FirstSetup;

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface IFirstSetupEnvironment
{
    Hardware Hardware();

    /// <summary>Display names of the installed programs (both registry views, machine and user).</summary>
    IReadOnlyList<string> InstalledNames();

    /// <summary>Installs one winget package silently; Ok false with the reason when it fails or winget is missing.</summary>
    Task<ActionResult> Install(string wingetId, CancellationToken ct);

    /// <summary>Opens a web page or a Windows settings page as the person (not as administrator).</summary>
    ActionResult Open(string url);
}

/// <summary>
/// First setup of a brand-new PC: the programs worth having from day one, in a sensible order, for this
/// PC's hardware and for what the person uses it for. Each one can be installed by OptLife (winget, from the
/// maker's own package, the ticked ones one after another) or downloaded by hand from its official site.
/// Only reads what is installed: no problems, nothing done without the person pressing a button.
/// </summary>
public sealed class FirstSetupModule : IModule
{
    public const string Install = "install", InstallOne = "install-one", OpenSite = "open-site", BoardKind = "firstsetup_board";
    private readonly IFirstSetupEnvironment _env;

    public FirstSetupModule() : this(new WindowsFirstSetupEnvironment()) { }

    internal FirstSetupModule(IFirstSetupEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(
        "firstsetup", L.T("Prima configurazione", "First setup"),
        L.T("Per un PC nuovo: le app utili da subito, in ordine, scelte in base a processore, scheda video e a cosa usi il PC (musica, giochi, studio). " +
            "Le installa OptLife con winget, oppure apri il sito ufficiale e le scarichi tu.",
            "For a new PC: the programs worth having from day one, in order, chosen for its processor, graphics card and what you use it for (music, gaming, study). " +
            "OptLife installs them with winget, or you open the official site and download them yourself."));

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        // Interests: every section stays visible, but only the chosen ones recommend apps.
        SettingDefinition.Bool("games", "Gaming", L.T("Consiglia le app per giocare.", "Recommend the apps for gaming."), false),
        SettingDefinition.Bool("music", L.T("Produzione musicale", "Music production"), L.T("Consiglia le app per fare musica.", "Recommend the apps for making music."), false),
        SettingDefinition.Bool("study", L.T("Studio", "Study"), L.T("Consiglia le app per lo studio.", "Recommend the apps for studying."), false),
        SettingDefinition.Bool("entertainment", L.T("Intrattenimento", "Entertainment"), L.T("Consiglia le app per musica e video in streaming.", "Recommend the music and video streaming apps."), false),
        SettingDefinition.Bool("emulation", L.T("Emulazione", "Emulation"), L.T("Consiglia RetroArch e ricorda la pagina Emulazione.", "Recommend RetroArch and point to the Emulation page."), false),
        SettingDefinition.Bool("aesthetics", L.T("Estetica", "Aesthetics"), L.T("Consiglia sfondi animati, barra trasparente e simili.", "Recommend animated wallpapers, a transparent taskbar and the like."), false),
        SettingDefinition.Bool("creativity", L.T("Creatività", "Creativity"), L.T("Consiglia programmi per foto, disegno e 3D.", "Recommend photo, drawing and 3D programs."), false),
        SettingDefinition.List("skip", L.T("Non mostrare", "Do not show"),
            L.T("Nomi delle app da togliere dall'elenco (quelli scritti in piccolo accanto a ogni app, es. \"chrome\").",
                "Names of the apps to leave out of the list (the ones in small print next to each app, e.g. \"chrome\").")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controlla cosa è installato", "Check what is installed"), TimeSpan.FromMinutes(30), (ctx, _) => Check(ctx)),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(Install, L.T("Installa le app scelte", "Install the chosen apps"),
            L.T("Mostra le app che mancano (quelle consigliate già spuntate) e installa con winget quelle che lasci spuntate, una dopo l'altra.",
                "Shows the missing apps (the recommended ones already ticked) and installs with winget the ones left ticked, one after the other."),
            Permission.Ask, (ctx, argument, ct) => DoInstall(ctx, argument, ct),
            Choices: ctx => [.. Missing(ctx).Where(a => a.Winget is not null)
                .Select(a => new ActionChoice(a.Id, a.Name, $"{Catalog.KindOf(a)} · {Catalog.TierWord(a.Tier).ToLower(L.Culture)}",
                    a.Essential, Catalog.Steps[a.Step]))]),
        new ModuleAction(InstallOne, L.T("Installa un'app", "Install one app"),
            L.T("Installa con winget l'app della riga su cui premi.", "Installs with winget the app of the line you press."),
            Permission.Ask, (ctx, argument, ct) => DoInstall(ctx, argument, ct)),
        new ModuleAction(OpenSite, L.T("Apri il sito ufficiale", "Open the official site"),
            L.T("Apre la pagina di download ufficiale dell'app (o la pagina di Windows), per scaricarla a mano.",
                "Opens the app's official download page (or the Windows page), to download it by hand."),
            Permission.Auto, (ctx, argument, _) => Task.FromResult(DoOpen(argument))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("firstsetup", L.T("Prima configurazione", "First setup"), BoardKind, [], Days: 365, Menu: ViewMenu.Programs, Order: 2,
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("Le app da avere su questo PC. Verde: installata. Rosso: consigliata e mancante. Grigio: facoltativa. " +
                             "INSTALLA la installa OptLife con winget, dai pacchetti ufficiali; SITO apre la pagina di download ufficiale. Prima Windows e i driver, riavviando quando lo chiedono.",
                             "The apps to have on this PC. Green: installed. Red: recommended and missing. Grey: optional. " +
                             "INSTALL lets OptLife install it with winget, from the official packages; SITE opens the official download page. Windows and drivers first, restarting when asked."),
            Layout: ViewLayout.Checklist,
            Buttons: _ =>
            [
                new ViewButton(L.T("Tutte insieme", "All at once"), L.T("Installa le app scelte", "Install the chosen apps"),
                    L.T("Ti mostra le app che mancano, con le consigliate già spuntate, e installa quelle che lasci spuntate, una dopo l'altra.",
                        "Shows the missing apps, the recommended ones already ticked, and installs the ones left ticked, one after the other."), Install,
                    ButtonText: L.T("SCEGLI E INSTALLA", "PICK AND INSTALL")),
            ]),
    ];

    private static IReadOnlyList<App> ListFor(ISettings settings, Hardware hardware)
    {
        var uses = new HashSet<Use>();
        if (settings.GetBool("music")) uses.Add(Use.Music);
        if (settings.GetBool("games")) uses.Add(Use.Games);
        if (settings.GetBool("study")) uses.Add(Use.Study);
        if (settings.GetBool("entertainment")) uses.Add(Use.Entertainment);
        if (settings.GetBool("emulation")) uses.Add(Use.Emulation);
        if (settings.GetBool("aesthetics")) uses.Add(Use.Aesthetics);
        if (settings.GetBool("creativity")) uses.Add(Use.Creativity);
        return Catalog.For(hardware, uses, settings.GetList("skip"));
    }

    private IReadOnlyList<App> Missing(IModuleContext ctx)
    {
        var installed = _env.InstalledNames();
        return [.. ListFor(ctx.Settings, _env.Hardware()).Where(a => a.Match.Count > 0 && !Catalog.IsInstalled(a, installed))];
    }

    /// <summary>The checklist: one line per app, grouped by step.</summary>
    internal static List<CheckItem> Board(IReadOnlyList<App> apps, Hardware hardware, IReadOnlyCollection<string> installed)
    {
        var board = new List<CheckItem>
        {
            new(Catalog.Steps[0], L.T("Questo PC", "This PC"), Catalog.Describe(hardware), CheckStatus.Info,
                Advice: L.T("I driver dell'elenco sono scelti in base a questo.", "The drivers in the list are chosen from this."), Kind: L.T("Questo PC", "This PC")),
            // Said in words only: the organisation page belongs to the core, this module does not refer to it.
            new(Catalog.Steps[0], L.T("Dove va ogni cosa", "Where everything goes"),
                L.T("Ordine e spazio → Organizzazione\nScegli i dischi per giochi, programmi ed emulatori e come tenere il Desktop (solo segnalazioni, Desktop pulito, Hub per categorie o regole tue), premi APPLICA: crea le cartelle e le regole che tengono il PC in ordine. Vale anche per un PC già configurato: mostra cosa è fuori posto e se spostarlo è sicuro.",
                    "Order and space → Organisation\nChoose the drives for games, programs and emulators and how to keep the Desktop (warnings only, clean Desktop, Hub by category or your own rules), press APPLY: it creates the folders and the rules that keep the PC tidy. It also works on a PC already set up: it shows what is out of place and whether moving it is safe."),
                CheckStatus.Info, Kind: L.T("Questo PC", "This PC"), StatusText: ""),
        };
        foreach (var app in apps)
        {
            if (app.Match.Count == 0)
            {
                board.Add(new CheckItem(Catalog.Steps[app.Step], app.Name, L.T("Da fare a mano", "Done by hand"), CheckStatus.Info, Advice: app.Why,
                    Kind: Catalog.KindOf(app), Buttons: [new CheckButton(OpenSite, app.Id, L.T("APRI", "OPEN"))]));
                continue;
            }
            var ok = Catalog.IsInstalled(app, installed);
            var importance = Catalog.TierWord(app.Tier);
            List<CheckButton> buttons = [];
            if (!ok && app.Winget is not null) buttons.Add(new CheckButton(InstallOne, app.Id, L.T("INSTALLA", "INSTALL")));
            if (!ok) buttons.Add(new CheckButton(OpenSite, app.Id, L.T("SITO", "SITE")));
            board.Add(new CheckItem(Catalog.Steps[app.Step], app.Name,
                ok ? L.T("Installata", "Installed") : L.T("Non installata", "Not installed"),
                ok ? CheckStatus.Ok : app.Step == 0 && app.Tier == Tier.Essential ? CheckStatus.Watch : CheckStatus.Info,
                Advice: ok ? app.Why : $"{importance} · {Catalog.KindOf(app)}. {app.Why}",
                Kind: Catalog.KindOf(app), Buttons: buttons,
                StatusText: ok || app.Step == 0 ? null : app.Tier == Tier.Optional ? "" : importance.ToUpper(L.Culture)));
        }
        return board;
    }

    private Task Check(IModuleContext ctx, bool evenDuringAGame = false)
    {
        // A board refresh is cheap (registry only), but a game deserves the whole PC.
        if (!evenDuringAGame && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;
        var hardware = _env.Hardware();
        var installed = _env.InstalledNames();
        var apps = ListFor(ctx.Settings, hardware);
        var board = Board(apps, hardware, installed);
        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("board")) return Task.CompletedTask;
        ctx.SetState("board", signature);
        var tracked = apps.Where(a => a.Match.Count > 0).ToList();
        var done = tracked.Count(a => Catalog.IsInstalled(a, installed));
        var missing = tracked.Count(a => a.Essential && !Catalog.IsInstalled(a, installed));
        ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
            L.T($"Prima configurazione: {done} app installate, {missing} consigliate mancanti", $"First setup: {done} apps installed, {missing} recommended missing"),
            BoardKind, Data: data)]);
        return Task.CompletedTask;
    }

    private async Task<ActionResult> DoInstall(IModuleContext ctx, string? argument, CancellationToken ct)
    {
        var wanted = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Read again now and in catalog order: drivers before programs, whatever order the ticks came in.
        var apps = Missing(ctx).Where(a => a.Winget is not null && wanted.Contains(a.Id)).ToList();
        if (apps.Count == 0) return new ActionResult(true, L.T("Nessuna app da installare.", "No app to install."));

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var app in apps)
        {
            if (ct.IsCancellationRequested) break;
            ctx.Progress(L.T($"Installo {app.Name}", $"Installing {app.Name}"), apps.IndexOf(app) + 1, apps.Count);
            var result = await _env.Install(app.Winget!, ct);
            if (result.Ok) done.Add(app.Name);
            else failed.Add($"{app.Name} ({result.Detail})");
        }
        await Check(ctx, evenDuringAGame: true);
        var parts = new List<string>();
        if (done.Count > 0) parts.Add(L.T("Installate: ", "Installed: ") + string.Join(", ", done) + ".");
        if (failed.Count > 0) parts.Add(L.T("Non riuscite: ", "Failed: ") + string.Join("; ", failed) + L.T(". Per queste usa il pulsante del sito ufficiale.", ". For these, use the official site button."));
        return new ActionResult(failed.Count == 0, string.Join(' ', parts));
    }

    private ActionResult DoOpen(string? id)
    {
        var app = Catalog.All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return app is null
            ? new ActionResult(false, L.T($"L'app \"{id}\" non è nell'elenco.", $"The app \"{id}\" is not in the list."))
            : _env.Open(app.Url);
    }

}
