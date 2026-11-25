using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OptLife.Core.Config;

namespace OptLife.Core.Layout;

/// <summary>A move OptLife made, kept so it can be undone.</summary>
/// <param name="Old">After a move between drives: the original kept, renamed "…-old-optlife", until the person deletes it.</param>
public sealed record LayoutMove(string Id, string From, string To, DateTimeOffset At, int Shortcuts, int Settings, string? Old = null, string? Launcher = null);

/// <summary>
/// "Organizzazione del PC": where every kind of thing goes on this PC (a layout with the person's own drives and names), which
/// folders are missing, which module settings keep it in order, and what is outside it, each with whether moving it is safe.
/// It lives in the core, not in a module, because applying the layout writes other modules' settings — something only the core
/// may do; it shows itself as a normal page. It creates folders and adds rules when asked; it moves only safe folders the person
/// ticks, updating shortcuts and OptLife's own settings, and every move can be undone. It never deletes anything.
/// </summary>
public sealed partial class LayoutModule : IModule
{
    public const string Id = "layout", BoardKind = "layout_board", Apply = "apply", Move = "move", MoveBack = "move-back", Keep = "keep", DeleteOld = "delete-old", HubApply = "hub-apply", HubUndo = "hub-undo";
    private readonly ConfigStore _config;
    private readonly Func<IReadOnlyList<IModule>> _modules;
    private readonly ILayoutEnvironment _env;
    private readonly string _configFile;

    /// <summary>Where the layout keeps an imported template: the same folder the module runner gives it.</summary>
    public string DataDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configFile))!, "moduli", Id);

    /// <param name="modules">Every module (read when needed: the layout is created after them).</param>
    public LayoutModule(ConfigStore config, Func<IReadOnlyList<IModule>> modules, ILayoutEnvironment? env = null)
    {
        _config = config;
        _modules = modules;
        _configFile = config.Path;
        _env = env ?? new WindowsLayoutEnvironment();
    }

    private IReadOnlyList<IModule> Others => [.. _modules().Where(m => m.Info.Id != Id)];

    public ModuleInfo Info { get; } = new(Id, L.T("Organizzazione del PC", "PC organisation"),
        L.T("Dove va ogni cosa su questo PC (giochi, programmi portatili, emulatori, copie, Desktop), le cartelle che mancano, e cosa è fuori posto con se spostarlo è sicuro.",
            "Where everything goes on this PC (games, portable programs, emulators, backups, Desktop), the missing folders, and what is out of place with whether moving it is safe."),
        FindingArea.Tidy);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Choice(PresetKey, L.T("Come tenere il Desktop", "How to keep the Desktop"),
            L.T("Solo segnalazioni: non sposta niente, avvisa di collegamenti rotti, Download pieni e Desktop troppo pieno. " +
                "Desktop pulito: sul Desktop restano solo i collegamenti, il resto va in Documenti\\Dal Desktop. " +
                "Hub per categorie: sul Desktop una cartella sola, con i collegamenti divisi per sezione. " +
                "Personalizzato: le regole le scrivi tu (Pulizia e ordine), lo schema non ne aggiunge per il Desktop. " +
                "Dopo un cambio la pagina Organizzazione mostra cosa cambierebbe; succede solo con APPLICA, e ogni spostamento si annulla.",
                "Warnings only: moves nothing, warns about broken shortcuts, full Downloads and a crowded Desktop. " +
                "Clean Desktop: only shortcuts stay on the Desktop, the rest goes to Documents\\From the Desktop. " +
                "Hub by category: one folder on the Desktop, with shortcuts by section. " +
                "Custom: you write the rules (Cleanup and order), the layout adds none for the Desktop. " +
                "After a change the Organisation page shows what would change; it happens only with APPLY, and every move can be undone."),
            DesktopPreset.Names)
            .Guided(L.T("Come vuoi tenere il Desktop? Si cambia quando vuoi in Organizzazione.", "How do you want to keep the Desktop? You can change it any time in Organisation.")),
        SettingDefinition.Text("games", L.T("Cartella dei giochi", "Games folder"),
            L.T("Per i giochi che non passano da un launcher (repack, portatili) e quelli degli emulatori. Vuoto: su un secondo disco se c'è, altrimenti C:\\Giochi.",
                "For games that do not come from a launcher (repacks, portable) and emulator games. Empty: on a second drive if there is one, otherwise C:\\Giochi."), "")
            .PickFolder().Guided(L.T("La cartella dei giochi (proposta: sul disco con più spazio):", "The games folder (suggested: on the drive with most room):")),
        SettingDefinition.Text("software", L.T("Cartella dei programmi portatili", "Portable programs folder"),
            L.T("Per i programmi che non si installano: uno per cartella.", "For programs that do not install: one per folder."), @"C:\Software").PickFolder(),
        SettingDefinition.Text("emulators", L.T("Cartella degli emulatori", "Emulators folder"),
            L.T("Vuoto: Consoles dentro la cartella dei programmi portatili.", "Empty: Consoles inside the portable programs folder."), "").PickFolder(),
        SettingDefinition.Text("backups", L.T("Cartella delle copie di sicurezza", "Backup folder"),
            L.T("Su un altro disco o sul NAS. Vuoto: nessuna.", "On another drive or the NAS. Empty: none."), "").PickFolder(),
        SettingDefinition.Text("hub", L.T("Cartella sul Desktop", "Desktop folder"),
            L.T("L'unica cartella sul Desktop, con dentro i collegamenti divisi per sezione.", "The only folder on the Desktop, with shortcuts inside by section."), "Hub"),
        SettingDefinition.List("hubSections", L.T("Sezioni della cartella sul Desktop", "Sections of the Desktop folder"),
            L.T("La prima è dove finisce ciò che va ancora sistemato.", "The first is where things still to be sorted end up."),
            L.T("0 Da sistemare", "0 To sort"), L.T("1 Giochi", "1 Games"), L.T("2 Programmi", "2 Programs"), L.T("3 Lavoro e studio", "3 Work and study"), L.T("4 Sistema", "4 System")),
        SettingDefinition.List("keep", L.T("Cartelle che restano dove sono", "Folders that stay where they are"),
            L.T("Cartelle in cima ai dischi che hai deciso di tenere fuori dallo schema: non vengono più proposte.", "Folders at the top of the drives you decided to keep outside the layout: no longer proposed.")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo dell'organizzazione", "Checking the organisation"), TimeSpan.FromHours(6), (ctx, ct) => Check(ctx, ct)),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(Apply, L.T("Applica lo schema", "Apply the layout"),
            L.T("Crea le cartelle che mancano e aggiunge le regole che tengono il PC in ordine. Non sposta e non cancella niente; prima salva una copia delle impostazioni.",
                "Creates the missing folders and adds the rules that keep the PC tidy. It moves and deletes nothing; it first saves a copy of the settings."),
            Permission.Ask, (ctx, _, ct) => DoApply(ctx, ct)),
        new ModuleAction(Move, L.T("Sposta nella cartella giusta", "Move to the right folder"),
            L.T("Sposta le cartelle sicure che spunti al loro posto nello schema, aggiorna i collegamenti e le impostazioni di OptLife che le nominano. Ogni spostamento si annulla.",
                "Moves the safe folders you tick to their place in the layout, updating shortcuts and the OptLife settings that name them. Every move can be undone."),
            Permission.Ask, DoMove,
            // Before anything moves: where to, how big, how long, how full the destination drive is left.
            Choices: ctx => [.. Outside(ctx).Where(o => o.Risk is Risk.Safe or Risk.Assisted && o.Destination is not null)
                .Select(o => new ActionChoice(o.Path, o.Path, $"→ {o.Destination}\n{PreviewOf(o).Text}", Checked: false, o.What))]),
        new ModuleAction(HubApply, L.T("Riorganizza la Hub", "Reorganise the Hub"),
            L.T("Rinomina e raggruppa le sezioni della cartella dei collegamenti come nello schema; aggiorna le regole di pulizia e smistamento e i collegamenti che puntano lì. Mostra prima l'elenco di ogni passo; si annulla.",
                "Renames and regroups the sections of the shortcuts folder as in the layout; updates the tidy and sorting rules and the shortcuts pointing there. It first lists every step; it can be undone."),
            Permission.Ask, DoHubApply,
            Choices: ctx => HubChoices(ctx)),
        new ModuleAction(HubUndo, L.T("Annulla la riorganizzazione della Hub", "Undo the Hub reorganisation"),
            L.T("Rimette sezioni, collegamenti e impostazioni com'erano.", "Puts sections, shortcuts and settings back as they were."),
            Permission.Ask, DoHubUndo),
        new ModuleAction(DeleteOld, L.T("Elimina la copia vecchia", "Delete the old copy"),
            L.T("Dopo uno spostamento tra due dischi l'originale resta, rinominato \"…-old-optlife\", finché non controlli che tutto funzioni. Questo lo manda al Cestino; se è troppo grande per il Cestino, Windows chiede prima di eliminarlo per sempre.",
                "After a move between two drives the original stays, renamed \"…-old-optlife\", until you check that everything works. This sends it to the Recycle Bin; if it is too big for the bin, Windows asks before deleting it for good."),
            Permission.Ask, DoDeleteOld, NeverAuto: true),
        new ModuleAction(MoveBack, L.T("Annulla uno spostamento", "Undo a move"),
            L.T("Rimette la cartella dov'era, con i collegamenti e le impostazioni.", "Puts the folder back where it was, with its shortcuts and settings."),
            Permission.Ask, DoMoveBack),
        new ModuleAction(Keep, L.T("Tieni qui", "Keep here"),
            L.T("Toglie la cartella dalle proposte: resta dov'è e non viene più segnalata.", "Removes the folder from the proposals: it stays where it is and is no longer listed."),
            Permission.Ask, (ctx, path, ct) => DoKeep(ctx, path, ct)),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("layout", L.T("Organizzazione", "Organisation"), BoardKind, [], Days: 365, Menu: ViewMenu.Order, Order: -1,
            Guide: L.T("Dove va ogni cosa: giochi, programmi portatili, copie di sicurezza, la cartella sul Desktop. Quando vuoi, APPLICA crea le cartelle che mancano: non sposta e non cancella niente.",
                "Where everything goes: games, portable programs, backups, the Desktop folder. When you want, APPLY creates the missing folders: it moves and deletes nothing."),
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("Lo schema dice dove va ogni cosa su questo PC; i dischi e i nomi si scelgono in IMPOSTAZIONI → Organizzazione del PC. " +
                             "APPLICA crea le cartelle che mancano e le regole che tengono il PC in ordine. In fondo, le cartelle fuori dallo schema: SICURO si può spostare con SPOSTA (si annulla), RISCHIOSO va spostato come spiegato, perché a mano si romperebbe.",
                             "The layout says where everything goes on this PC; drives and names are chosen in SETTINGS → PC organisation. " +
                             "APPLY creates the missing folders and the rules that keep the PC tidy. At the bottom, the folders outside the layout: SAFE can be moved with MOVE (and undone), RISKY must be moved as explained, because by hand it would break."),
            Layout: ViewLayout.Checklist,
            Buttons: _ =>
            [
                new ViewButton(L.T("Schema", "Layout"), L.T("Applica lo schema", "Apply the layout"),
                    L.T("Crea le cartelle che mancano e aggiunge le regole. Non sposta niente.", "Creates the missing folders and adds the rules. Moves nothing."), Apply, ButtonText: L.T("APPLICA", "APPLY")),
                new ViewButton(L.T("Schema", "Layout"), L.T("Sposta ciò che è fuori posto", "Move what is out of place"),
                    L.T("Mostra le cartelle sicure da spostare: spunta quelle da mettere al loro posto.", "Shows the safe folders to move: tick those to put in their place."), Move, ButtonText: L.T("SCEGLI E SPOSTA", "PICK AND MOVE")),
            ]),
    ];

    // ── The person's choices ─────────────────────────────────────────────────

    private const string PresetKey = "desktop", AppliedKey = "preset.applied";

    /// <summary>
    /// The way the Desktop is kept. Never chosen yet (a PC set up before the choice existed): worked out once from the rules already
    /// there — the Desktop rule that keeps only the Hub means "Hub by category", another Desktop rule means "custom" — and written
    /// down, so nothing changes on that PC.
    /// </summary>
    private string Preset(IModuleContext ctx)
    {
        // Read from the file, not from the round's settings: they may have been taken before the line below wrote it.
        var stored = _config.GetModule(Id).Settings;
        if (stored.ContainsKey(PresetKey)) return DesktopPreset.IdOf(new ModuleSettings(Settings, stored).GetChoice(PresetKey));
        var hub = Or(ctx.Settings.GetText("hub"), "Hub");
        var cleanup = Others.FirstOrDefault(m => m.Info.Id == "cleanup");
        var tidy = cleanup is null ? [] : new ModuleSettings(cleanup.Settings, _config.GetModule("cleanup").Settings).GetList("tidy");
        var desktop = tidy.Select(l => l.Split('|', StringSplitOptions.TrimEntries)).FirstOrDefault(p => p.Length >= 2 && LayoutPaths.Same(p[0], @"%USERPROFILE%\Desktop"));
        var preset = desktop is null ? DesktopPreset.Report
            : desktop[1].Split(',', StringSplitOptions.TrimEntries).Contains(hub, StringComparer.OrdinalIgnoreCase) ? DesktopPreset.Hub
            : DesktopPreset.Custom;
        _config.UpdateModule(Id, m => m.Settings[PresetKey] = JsonSerializer.SerializeToElement(DesktopPreset.NameOf(preset)));
        if (desktop is not null) ctx.SetState(AppliedKey, preset); // What is there already is that way, applied.
        return preset;
    }

    private LayoutChoices Choices(IModuleContext ctx) => Choices(settings: ctx.Settings) with { Preset = Preset(ctx) };

    /// <summary>The way of keeping the Desktop changed since the layout last applied one: APPLY replaces its rules.</summary>
    private bool Replacing(IModuleContext ctx, string preset) =>
        preset != DesktopPreset.Custom && ctx.GetState(AppliedKey) is { } applied && applied != preset;

    /// <summary>The rules the way of keeping the Desktop applied before brought, still as written, that the new way does not want.</summary>
    private IReadOnlyList<ResolvedRule> Leftovers(IModuleContext ctx, LayoutChoices choices, LayoutTemplate template) =>
        Replacing(ctx, choices.Preset)
            ? LayoutConfig.Leftovers(choices.With(ctx.GetState(AppliedKey)!).Rules(template), choices.Rules(template), Others, _config)
            : [];

    public LayoutChoices Choices(ISettings settings)
    {
        var s = settings;
        var software = Or(s.GetText("software"), @"C:\Software");
        var games = s.GetText("games").Trim();
        if (games.Length == 0)
        {
            var system = LayoutChoices.SystemDrive;
            var second = _env.Drives().Select(d => d.TrimEnd('\\')).Where(d => !d.Equals(system, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(_env.FreeBytes).FirstOrDefault();
            games = $@"{second ?? system}\Giochi";
        }
        return new LayoutChoices(games.TrimEnd('\\'), software.TrimEnd('\\'), Or(s.GetText("emulators"), $@"{software.TrimEnd('\\')}\Consoles").TrimEnd('\\'),
            s.GetText("backups").Trim().TrimEnd('\\'), Or(s.GetText("hub"), "Hub"), [.. s.GetList("hubSections").Where(x => x.Trim().Length > 0).Select(x => x.Trim())],
            DesktopPreset.IdOf(s.GetChoice(PresetKey)));
    }

    /// <summary>
    /// What the folder settings offer in a list: on every drive the usual place, with the drive's name and free room
    /// ("E:\Giochi — New Volume, 191 GB free"), and the automatic choice where there is one; any other folder with "Browse".
    /// </summary>
    private void SaveFolderOptions(IModuleContext ctx, LayoutChoices choices)
    {
        var drives = _env.Drives().Select(d => d.TrimEnd('\\')).ToList();
        string Where(string drive)
        {
            var gb = _env.FreeBytes(drive + @"\") / 1_000_000_000;
            var label = _env.VolumeLabel(drive + @"\") ?? L.T($"disco {drive}", $"drive {drive}");
            return label + L.T($", {gb} GB liberi", $", {gb} GB free");
        }
        IEnumerable<SettingOption> On(string folder) => drives.Select(d => new SettingOption($@"{d}\{folder}", $@"{d}\{folder} — {Where(d)}"));
        SettingOption.Save(ctx, "games", [new SettingOption("", L.T($"Automatica: {choices.Games}", $"Automatic: {choices.Games}")), .. On("Giochi")]);
        SettingOption.Save(ctx, "software", On("Software"));
        SettingOption.Save(ctx, "emulators", [new SettingOption("", L.T($@"Automatica: {choices.Software}\Consoles", $@"Automatic: {choices.Software}\Consoles")), .. On("Consoles")]);
        SettingOption.Save(ctx, "backups", [new SettingOption("", L.T("Nessuna", "None")), .. On("Backup")]);
    }

    private static string Or(string value, string fallback) => value.Trim().Length > 0 ? value.Trim() : fallback;

    private static string TemplateFile(string dataDirectory) => Path.Combine(dataDirectory, "template.json");

    /// <summary>The layout in use: one imported from someone else, or the one that ships with OptLife.</summary>
    public static LayoutTemplate Template(IModuleContext ctx) => Template(ctx.DataDirectory);

    public static LayoutTemplate Template(string dataDirectory)
    {
        try
        {
            if (File.Exists(TemplateFile(dataDirectory))) return LayoutTemplate.Parse(File.ReadAllText(TemplateFile(dataDirectory)));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
        }
        return LayoutTemplate.Default;
    }

    // ── What is outside the layout ───────────────────────────────────────────

    public IReadOnlyList<OutsideItem> Outside(IModuleContext ctx)
    {
        var choices = Choices(ctx);
        var places = choices.Places(Template(ctx)).Select(p => p.Path).ToList();
        var keep = ctx.Settings.GetList("keep");
        // Folders OptLife itself is told about (a backup copy, a project folder) are known: listed as data, never moved by surprise.
        var refs = _env.References();
        var items = new List<OutsideItem>();
        foreach (var drive in _env.Drives())
            foreach (var folder in _env.TopFolders(drive))
            {
                var name = Path.GetFileName(folder.TrimEnd('\\'));
                if (LayoutScan.SystemNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (keep.Any(k => LayoutPaths.Same(k, folder))) continue;
                // A place of the layout, inside one, or holding one (E:\Giochi when games are in E:\Giochi\Repack): in its place.
                if (places.Any(p => LayoutPaths.Within(folder, p) || LayoutPaths.Within(p, folder))) continue;
                if (LayoutPaths.Within(AppContext.BaseDirectory, folder)) continue; // OptLife itself.
                var item = LayoutScan.Judge(_env.Inspect(folder, measure: false), refs, choices);
                if (item.Risk == Risk.Safe) item = LayoutScan.Judge(_env.Inspect(folder, measure: true), refs, choices);
                items.Add(item);
            }
        return items;
    }

    // ── The page ─────────────────────────────────────────────────────────────

    private Task Check(IModuleContext ctx, CancellationToken ct)
    {
        if (!ctx.OnDemand && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;
        var choices = Choices(ctx);
        var template = Template(ctx);
        ctx.SetState("suggest.games", choices.Games); // For the guided tour: where the games folder would go.
        SaveFolderOptions(ctx, choices);
        var board = new List<CheckItem>();

        var placesGroup = L.T("Cartelle dello schema", "Layout folders");
        var hubWaiting = HubPlan(ctx).Count > 0;
        foreach (var place in choices.Places(template))
        {
            var exists = _env.Exists(place.Path);
            var byHub = !exists && hubWaiting && LeftToHub(ctx, place.Path);
            board.Add(new CheckItem(placesGroup, LayoutPaths.Expand(place.Path),
                (exists ? L.T("C'è", "Present") : byHub ? L.T("Manca: la fa la riorganizzazione della Hub (in cima)", "Missing: the Hub reorganisation makes it (at the top)") : L.T("Manca: la crea APPLICA", "Missing: APPLY creates it")) + "\n" + place.What,
                exists ? CheckStatus.Ok : byHub ? CheckStatus.Info : CheckStatus.Wrong));
        }

        var rulesGroup = L.T("Regole che tengono il PC in ordine", "Rules that keep the PC tidy");
        // How the Desktop is kept, first: what the other lines follow from.
        var replacing = Replacing(ctx, choices.Preset);
        board.Add(new CheckItem(rulesGroup, L.T("Come tenere il Desktop", "How to keep the Desktop"),
            DesktopPreset.NameOf(choices.Preset) + (replacing
                ? L.T($"\nCambiato da \"{DesktopPreset.NameOf(ctx.GetState(AppliedKey)!)}\": qui sotto cosa cambia, e succede solo con APPLICA.", $"\nChanged from \"{DesktopPreset.NameOf(ctx.GetState(AppliedKey)!)}\": below is what changes, and it happens only with APPLY.")
                : L.T("\nSi cambia in IMPOSTAZIONI → Organizzazione del PC.", "\nChanged in SETTINGS → PC organisation.")),
            replacing ? CheckStatus.Watch : CheckStatus.Info, StatusText: ""));
        foreach (var rule in choices.Rules(template))
        {
            var state = LayoutConfig.State(rule, Others, _config, out var current, replacing);
            if (state == LayoutConfig.RuleState.NoModule) continue;
            board.Add(new CheckItem(rulesGroup, rule.What, state switch
            {
                LayoutConfig.RuleState.Applied => L.T("Attiva", "Active"),
                LayoutConfig.RuleState.Different => L.T($"Hai scelto {current}: lasciata com'è", $"You chose {current}: left as it is"),
                LayoutConfig.RuleState.Replace => L.T($"Da cambiare: APPLICA mette questa al posto di \"{current}\"", $"To change: APPLY puts this in place of \"{current}\""),
                _ => L.T("Manca: la aggiunge APPLICA", "Missing: APPLY adds it"),
            }, state switch { LayoutConfig.RuleState.Applied => CheckStatus.Ok, LayoutConfig.RuleState.Different => CheckStatus.Info, _ => CheckStatus.Wrong }));
        }
        foreach (var old in Leftovers(ctx, choices, template))
            board.Add(new CheckItem(rulesGroup, old.What, L.T("Del modo di prima: APPLICA la toglie (non sposta niente indietro)", "From the previous way: APPLY removes it (nothing is moved back)"), CheckStatus.Wrong));

        var outsideGroup = L.T("Fuori dallo schema", "Outside the layout");
        foreach (var o in Outside(ctx).OrderBy(o => o.Risk).ThenBy(o => o.Path, StringComparer.OrdinalIgnoreCase))
        {
            var keep = new CheckButton(Keep, o.Path, L.T("TIENI QUI", "KEEP HERE"));
            var (status, word, buttons) = o.Risk switch
            {
                Risk.Safe => (CheckStatus.Watch, L.T("SICURO", "SAFE"), new List<CheckButton> { new(Move, o.Path, L.T("SPOSTA", "MOVE")), keep }),
                Risk.Assisted => (CheckStatus.Watch, L.T("CON AIUTO", "ASSISTED"), new List<CheckButton> { new(Move, o.Path, L.T("SPOSTA", "MOVE")), keep }),
                Risk.Risky => (CheckStatus.Watch, L.T("RISCHIOSO", "RISKY"), new List<CheckButton> { keep }),
                _ => (CheckStatus.Info, "", new List<CheckButton> { keep }),
            };
            var waiting = (ctx.GetState("confirm") is { } c ? JsonSerializer.Deserialize<List<string>>(c) : null) ?? [];
            if (waiting.Any(p => LayoutPaths.Same(p, o.Path)))
                buttons.Insert(0, new CheckButton(Move, "!" + o.Path, L.T("SPOSTA COMUNQUE", "MOVE ANYWAY")));
            var preview = o.Destination is not null && o.Risk is Risk.Safe or Risk.Assisted ? "\n" + PreviewOf(o).Text : "";
            board.Add(new CheckItem(outsideGroup, o.Path,
                o.What + (o.Destination is { } d ? L.T($" → va in {d}", $" → belongs in {d}") : "") + preview + "\n" + o.Why,
                status, Kind: o.What, Buttons: buttons, StatusText: word));
        }

        var hubGroup = "Hub";
        var plan = HubPlan(ctx);
        // A reorganisation waiting is the first thing on the page, with its own title: APPLY below does not do it.
        if (plan.Count > 0)
            board.Insert(0, new CheckItem(HubPendingGroup, L.T("Riorganizzazione della Hub", "Hub reorganisation"),
                L.T($"{plan.Count} passi pronti (rinomine, sezioni nuove, collegamenti spostati). Non la fa APPLICA: premi VEDI E APPLICA qui, spunti i passi, confermi. Si può annullare.",
                    $"{plan.Count} steps ready (renames, new sections, shortcuts moved). APPLY does not do it: press SEE AND APPLY here, tick the steps, confirm. It can be undone."),
                CheckStatus.Watch, ValueStatus: CheckStatus.Watch, StatusText: L.T("DA FARE", "TO DO"),
                Buttons: [new CheckButton(HubApply, null, L.T("VEDI E APPLICA", "SEE AND APPLY"))]));
        foreach (var change in HubChanges(ctx).OrderByDescending(c => c.At))
            board.Add(new CheckItem(hubGroup, L.T("Hub riorganizzata", "Hub reorganised"),
                L.T($"Il {change.At.ToLocalTime().ToString("d MMM HH:mm", L.Culture)}: {change.Done.Count} passi.", $"On {change.At.ToLocalTime().ToString("d MMM HH:mm", L.Culture)}: {change.Done.Count} steps."),
                CheckStatus.Info, StatusText: "", Buttons: [new CheckButton(HubUndo, change.Id, L.T("ANNULLA", "UNDO"))]));

        var movesGroup = L.T("Spostamenti fatti", "Moves made");
        foreach (var m in Moves(ctx).OrderByDescending(m => m.At))
            board.Add(new CheckItem(movesGroup, Path.GetFileName(m.To), L.T($"{m.From} → {m.To}\nIl {m.At.ToLocalTime().ToString("d MMM HH:mm", L.Culture)}; {m.Shortcuts} collegamenti e {m.Settings} impostazioni aggiornati.",
                $"{m.From} → {m.To}\nOn {m.At.ToLocalTime().ToString("d MMM HH:mm", L.Culture)}; {m.Shortcuts} shortcuts and {m.Settings} settings updated."),
                CheckStatus.Info, StatusText: "", Buttons: m.Old is { } old && Directory.Exists(old)
                    ? [new CheckButton(MoveBack, m.Id, L.T("ANNULLA", "UNDO")), new CheckButton(DeleteOld, m.Id, L.T("ELIMINA LA COPIA VECCHIA", "DELETE THE OLD COPY"))]
                    : [new CheckButton(MoveBack, m.Id, L.T("ANNULLA", "UNDO"))]));

        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature != ctx.GetState("board") || ctx.OnDemand)
        {
            ctx.SetState("board", signature);
            var missing = board.Count(i => i.Status == CheckStatus.Wrong);
            var outside = board.Count(i => i.Group == outsideGroup && i.Status == CheckStatus.Watch);
            ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
                L.T($"Organizzazione: {missing} cose da applicare, {outside} cartelle fuori posto", $"Organisation: {missing} things to apply, {outside} folders out of place"), BoardKind, Data: data)]);
        }
        return Task.CompletedTask;
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>A folder inside the Hub (a section), not the Hub itself.</summary>
    private bool LeftToHub(IModuleContext ctx, string path)
    {
        var hub = HubOf(ctx);
        var full = LayoutPaths.Expand(path);
        return LayoutPaths.Within(full, hub) && !LayoutPaths.Same(full, hub);
    }

    private static string HubPendingGroup => L.T("Da fare: riorganizzazione della Hub", "To do: Hub reorganisation");

    /// <summary>Said after APPLY when the Hub reorganisation still waits: it is a separate button.</summary>
    private string HubStillPending(IModuleContext ctx) => HubPlan(ctx) is { Count: > 0 } plan
        ? " " + L.T($"La riorganizzazione della Hub ({plan.Count} passi) è a parte e non è ancora fatta: premi VEDI E APPLICA in cima a questa pagina.",
                    $"The Hub reorganisation ({plan.Count} steps) is separate and not done yet: press SEE AND APPLY at the top of this page.")
        : "";

    private async Task<ActionResult> DoApply(IModuleContext ctx, CancellationToken ct)
    {
        var choices = Choices(ctx);
        var template = Template(ctx);
        var made = new List<string>();
        var failed = new List<string>();
        // While a Hub reorganisation waits, its new sections are left to it: made empty here, they would stop it renaming the old ones.
        var hubWaiting = HubPlan(ctx).Count > 0;
        foreach (var place in choices.Places(template).Where(p => !_env.Exists(p.Path) && !(hubWaiting && LeftToHub(ctx, p.Path))))
        {
            var r = _env.CreateFolder(place.Path);
            (r.Ok ? made : failed).Add(r.Ok ? LayoutPaths.Expand(place.Path) : r.Detail);
        }
        var rules = choices.Rules(template);
        IReadOnlyList<string> added = [];
        var replacing = Replacing(ctx, choices.Preset);
        var leftovers = Leftovers(ctx, choices, template);
        if (leftovers.Count > 0 || rules.Any(r => LayoutConfig.State(r, Others, _config, out _, replacing) is LayoutConfig.RuleState.Missing or LayoutConfig.RuleState.Replace))
        {
            // A copy of the settings as they were, before the layout writes into them.
            try
            {
                if (File.Exists(_configFile)) File.Copy(_configFile, $"{_configFile}.bak-layout-{ctx.Now.ToLocalTime():yyyyMMdd-HHmm}", overwrite: true);
            }
            catch (IOException)
            {
            }
            added = LayoutConfig.Apply(rules, Others, _config, replacing, leftovers);
        }
        ctx.SetState(AppliedKey, choices.Preset);
        await Check(new Forced(ctx), ct);
        if (made.Count == 0 && added.Count == 0 && failed.Count == 0) return new ActionResult(true, L.T("Cartelle e regole erano già tutte a posto.", "Folders and rules were all in place already.") + HubStillPending(ctx));
        var parts = new List<string>();
        if (made.Count > 0) parts.Add(L.T("Cartelle create: ", "Folders created: ") + string.Join(", ", made) + ".");
        if (added.Count > 0) parts.Add(L.T("Regole aggiunte: ", "Rules added: ") + string.Join("; ", added) + ".");
        if (failed.Count > 0) parts.Add(L.T("Non riuscite: ", "Failed: ") + string.Join("; ", failed) + ".");
        return new ActionResult(failed.Count == 0, string.Join(' ', parts) + HubStillPending(ctx),
            added.Count > 0 ? L.T("Le impostazioni di prima sono in config.json.bak-layout-…", "The previous settings are in config.json.bak-layout-…") : null);
    }

    private (string Text, bool LowRoom) PreviewOf(OutsideItem o)
    {
        var to = o.Destination!;
        var drive = Path.GetPathRoot(LayoutPaths.Expand(to)) ?? to;
        return LayoutScan.Preview(_env.Inspect(o.Path, measure: true).Bytes, _env.SameVolume(o.Path, to), drive.TrimEnd('\\'), _env.FreeBytes(to), _env.TotalBytes(to));
    }

    /// <summary>The process each launcher runs as: it must be closed while its game moves.</summary>
    private static string? ProcessOf(string what) =>
        what.Contains("Epic", StringComparison.Ordinal) ? "EpicGamesLauncher"
        : what.Contains("Battle.net", StringComparison.Ordinal) ? "Battle.net"
        : what.Contains("Ubisoft", StringComparison.Ordinal) ? "upc" : null;

    private async Task<ActionResult> DoMove(IModuleContext ctx, string? argument, CancellationToken ct)
    {
        // "!path" = moved even if the destination drive is left almost full: the person confirmed it a second time.
        var raw = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var forced = raw.Where(p => p.StartsWith('!')).Select(p => p[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = raw.Select(p => p.TrimStart('!')).ToArray();
        var confirm = (ctx.GetState("confirm") is { } c ? JsonSerializer.Deserialize<List<string>>(c) : null) ?? [];
        if (wanted.Length == 0) return new ActionResult(true, L.T("Nessuna cartella scelta.", "No folder chosen."));
        // Judged again now: only what is still safe moves, whatever was ticked or typed.
        var outside = Outside(ctx);
        var done = new List<string>();
        var failed = new List<string>();
        var moves = Moves(ctx).ToList();
        var todo = new List<string>();
        foreach (var path in wanted)
        {
            if (ct.IsCancellationRequested) break;
            ctx.Progress(L.T($"Sposto {path}", $"Moving {path}"), Array.IndexOf(wanted, path) + 1, wanted.Length);
            var item = outside.FirstOrDefault(o => LayoutPaths.Same(o.Path, path));
            if (item is not { Risk: Risk.Safe or Risk.Assisted, Destination: { } to })
            {
                failed.Add($"{path} ({L.T("non è tra quelle sicure da spostare", "not among the safe ones to move")})");
                continue;
            }
            if (Blocked(item.Path, to) is { } why)
            {
                failed.Add($"{path} ({why})");
                continue;
            }
            if (ProcessOf(item.What) is { } launcherProcess && _env.ProcessRunning(launcherProcess))
            {
                failed.Add($"{path} ({L.T($"chiudi prima {launcherProcess} e riprova", $"close {launcherProcess} first and try again")})");
                continue;
            }
            var preview = PreviewOf(item);
            if (preview.LowRoom && !forced.Contains(path))
            {
                if (!confirm.Contains(path, StringComparer.OrdinalIgnoreCase)) confirm.Add(path);
                failed.Add($"{path} ({L.T("il disco di arrivo resterebbe quasi pieno: Windows e i programmi rallentano e gli aggiornamenti possono fallire. Se vuoi farlo comunque, premi SPOSTA COMUNQUE sulla sua riga",
                    "the destination drive would be left almost full: Windows and programs slow down and updates may fail. To do it anyway, press MOVE ANYWAY on its line")})");
                continue;
            }
            confirm.RemoveAll(p => LayoutPaths.Same(p, path));
            var result = _env.Move(item.Path, to);
            if (!result.Ok)
            {
                failed.Add($"{path} ({result.Detail})");
                continue;
            }
            var shortcuts = _env.FixShortcuts(item.Path, to);
            var settings = LayoutConfig.Rebase(_config, item.Path, to);
            var id = Guid.NewGuid().ToString("N")[..8];
            var launcher = item.Risk == Risk.Assisted ? item.What : null;
            // Epic learns the new folder from its own records (copied first); Battle.net and Ubisoft need one click of the person.
            if (item.What.Contains("Epic", StringComparison.Ordinal))
                _env.FixEpic(item.Path, to, Path.Combine(ctx.DataDirectory, "epic-backup-" + id));
            else if (item.Risk == Risk.Assisted)
                todo.Add($"{Path.GetFileName(to)}: {L.T("spostato. Ora", "moved. Now")} {item.Why}");
            var move = new LayoutMove(id, item.Path, to, ctx.Now, shortcuts, settings, result.Detail.Length > 0 && Directory.Exists(result.Detail) ? result.Detail : null, launcher);
            moves.Add(move);
            ctx.RecordEvents([new NewEvent(ctx.Now, "layout_moved", Severity.Info, L.T($"Spostata {item.Path} → {to}", $"Moved {item.Path} → {to}"), "layout",
                Data: new Dictionary<string, string> { ["from"] = item.Path, ["to"] = to, ["id"] = move.Id })]);
            done.Add($"{Path.GetFileName(to)} → {Path.GetDirectoryName(to)}");
        }
        ctx.SetState("moves", JsonSerializer.Serialize(moves));
        ctx.SetState("confirm", JsonSerializer.Serialize(confirm));
        await Check(new Forced(ctx), ct);
        var parts = new List<string>();
        if (done.Count > 0) parts.Add(L.T("Spostate: ", "Moved: ") + string.Join(", ", done) + ".");
        if (todo.Count > 0) parts.Add(L.T("Da completare: ", "To finish: ") + string.Join(" ", todo));
        if (failed.Count > 0) parts.Add(L.T("Non spostate: ", "Not moved: ") + string.Join("; ", failed) + ".");
        return new ActionResult(failed.Count == 0, string.Join(' ', parts),
            done.Count > 0 ? L.T("ANNULLA nella pagina Organizzazione, sotto \"Spostamenti fatti\".", "UNDO on the Organisation page, under \"Moves made\".") : null);
    }

    /// <summary>The checks before a move: the destination free, nothing running from inside, room on the destination drive.</summary>
    private string? Blocked(string from, string to)
    {
        if (_env.Exists(to)) return L.T($"esiste già {to}", $"{to} already exists");
        if (_env.RunningUnder(from) is { Count: > 0 } running) return L.T($"è in uso da {string.Join(", ", running)}: chiudilo e riprova", $"in use by {string.Join(", ", running)}: close it and try again");
        if (!_env.SameVolume(from, to) && _env.Inspect(from, measure: true).Bytes * 1.05 > _env.FreeBytes(to))
            return L.T("non c'è abbastanza spazio sul disco di destinazione", "not enough space on the destination drive");
        return null;
    }

    private async Task<ActionResult> DoMoveBack(IModuleContext ctx, string? id, CancellationToken ct)
    {
        var moves = Moves(ctx).ToList();
        if (moves.FirstOrDefault(m => m.Id == id) is not { } move) return new ActionResult(false, L.T("Spostamento non trovato.", "Move not found."));
        ActionResult result;
        if (move.Old is { } old && Directory.Exists(old))
        {
            // Between drives the original was kept: the copy goes to the Recycle Bin and the original gets its name back.
            if (_env.RunningUnder(move.To) is { Count: > 0 } running)
                return new ActionResult(false, L.T($"in uso da {string.Join(", ", running)}: chiudilo e riprova", $"in use by {string.Join(", ", running)}: close it and try again"));
            result = _env.RemoveOld(move.To);
            if (!result.Ok) return result;
            result = _env.Move(old, move.From);
        }
        else
        {
            if (Blocked(move.To, move.From) is { } why) return new ActionResult(false, why);
            result = _env.Move(move.To, move.From);
        }
        if (!result.Ok) return result;
        if (move.Launcher?.Contains("Epic", StringComparison.Ordinal) == true)
            _env.FixEpic(move.To, move.From, Path.Combine(ctx.DataDirectory, "epic-backup-" + move.Id + "-undo"));
        _env.FixShortcuts(move.To, move.From);
        LayoutConfig.Rebase(_config, move.To, move.From);
        moves.Remove(move);
        ctx.SetState("moves", JsonSerializer.Serialize(moves));
        ctx.RecordEvents([new NewEvent(ctx.Now, "layout_moved_back", Severity.Info, L.T($"Rimessa al suo posto: {move.From}", $"Put back: {move.From}"), "layout")]);
        await Check(new Forced(ctx), ct);
        return new ActionResult(true, L.T($"{move.From} è tornata dov'era, con i suoi collegamenti.", $"{move.From} is back where it was, with its shortcuts."));
    }

    // ── The Hub: sections renamed or regrouped as the layout says ──

    private string HubOf(IModuleContext ctx) => LayoutPaths.Expand(Choices(ctx).HubPath).TrimEnd('\\');

    /// <summary>The prepared plan (a file in the module's folder), or the renames by section number.</summary>
    private IReadOnlyList<HubOp> HubPlan(IModuleContext ctx)
    {
        // The Hub belongs to "Hub by category" only: with another way of keeping the Desktop there is nothing to reorganise.
        if (Preset(ctx) != DesktopPreset.Hub) return [];
        var hub = HubOf(ctx);
        if (!_env.Exists(hub)) return [];
        var plan = LayoutHub.Load(ctx.DataDirectory)
                   ?? LayoutHub.ByNumber(hub, _env.Subfolders(hub), Choices(ctx).HubSections);
        // Only what is still to do: a step whose source is gone (done already) is left out.
        return [.. plan.Where(op => op.Kind switch
        {
            "create" => !_env.Exists(op.From),
            "remove" => _env.Exists(op.From),
            "file" => File.Exists(op.From),
            _ => _env.Exists(op.From) && !_env.Exists(op.To),
        })];
    }

    /// <summary>Other texts naming the old folders: the Hub, the Desktop, and the programs' folders sharing a word with a section.</summary>
    private IReadOnlyList<(string File, string Mentions)> HubMentions(IModuleContext ctx, IReadOnlyList<HubOp> plan)
    {
        var hub = HubOf(ctx);
        var software = LayoutPaths.Expand(Choices(ctx).Software);
        var words = plan.Where(o => o.Kind == "folder").SelectMany(o => Path.GetFileName(o.From).Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length >= 5).ToList();
        var places = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        places.AddRange(_env.Subfolders(software).Where(n => words.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(n => Path.Combine(software, n)));
        var old = plan.Where(o => o.Kind == "folder").Select(o => o.From).ToList();
        // Texts inside the folders being moved travel with them: only those elsewhere need fixing.
        return [.. LayoutHub.MentionsOutside(places, old).Where(m => !LayoutPaths.Within(m.File, hub))];
    }

    private IReadOnlyList<ActionChoice> HubChoices(IModuleContext ctx)
    {
        var hub = HubOf(ctx);
        var plan = HubPlan(ctx);
        var list = plan.Select((op, i) => new ActionChoice($"op:{i}", LayoutHub.Describe(op, hub), "", true, L.T("Passi", "Steps"))).ToList();
        foreach (var (file, mentions) in HubMentions(ctx, plan))
            list.Add(new ActionChoice($"note:{file}", file, L.T($"Cita {mentions}: va aggiornato a mano (OptLife non riscrive file di altri).", $"Mentions {mentions}: to update by hand (OptLife does not rewrite other people's files)."),
                false, L.T("Da aggiornare a mano", "To update by hand")));
        return list;
    }

    private async Task<ActionResult> DoHubApply(IModuleContext ctx, string? argument, CancellationToken ct)
    {
        var hub = HubOf(ctx);
        var plan = HubPlan(ctx);
        var wanted = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Where(a => a.StartsWith("op:", StringComparison.Ordinal))
            .Select(a => int.TryParse(a[3..], out var i) ? i : -1).Where(i => i >= 0 && i < plan.Count).ToHashSet();
        if (wanted.Count == 0) return new ActionResult(true, L.T("Nessun passo scelto.", "No step chosen."));
        var oldSections = Choices(ctx).HubSections.ToList();
        try { if (File.Exists(_configFile)) File.Copy(_configFile, $"{_configFile}.bak-hub-{ctx.Now.ToLocalTime():yyyyMMdd-HHmm}", overwrite: true); }
        catch (IOException) { }
        var done = new List<HubOp>();
        var failed = new List<string>();
        for (var i = 0; i < plan.Count; i++)
        {
            if (!wanted.Contains(i) || ct.IsCancellationRequested) continue;
            var op = plan[i];
            var result = Run(op);
            if (result.Ok) done.Add(op);
            else failed.Add($"{LayoutHub.Describe(op, hub)} ({result.Detail})");
        }
        // OptLife's own settings and the shortcuts outside follow the folders.
        foreach (var op in done.Where(o => o.Kind == "folder"))
        {
            LayoutConfig.Rebase(_config, op.From, op.To);
            _env.FixShortcuts(op.From, op.To);
        }
        var sections = _env.Subfolders(hub).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        _config.UpdateModule(Id, m => m.Settings["hubSections"] = JsonSerializer.SerializeToElement(sections));
        // The sorting of shortcuts learns the sub-sections the plan made ("1 Games\Emulation").
        // A whole section that became a sub-section (a top-level folder moved inside another): found again by name in the final Hub.
        var leaves = done.Where(o => o.Kind == "folder" && Path.GetDirectoryName(o.From) is { } parent && LayoutPaths.Same(parent, hub)
                                     && Path.GetDirectoryName(o.To) is { } to && !LayoutPaths.Same(to, hub))
            .Select(o => Path.GetFileName(o.To)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subsections = sections.SelectMany(section => _env.Subfolders(Path.Combine(hub, section)).Where(leaves.Contains).Select(sub => $"{section}\\{sub}")).ToList();
        if (subsections.Count > 0 && Others.Any(m => m.Info.Id == "cleanup"))
            _config.UpdateModule("cleanup", m => m.Settings["hubSubsections"] = JsonSerializer.SerializeToElement(subsections));
        var changes = HubChanges(ctx).ToList();
        changes.Add(new HubChange(Guid.NewGuid().ToString("N")[..8], ctx.Now, done, oldSections));
        ctx.SetState("hub_changes", JsonSerializer.Serialize(changes));
        ctx.RecordEvents([new NewEvent(ctx.Now, "hub_reorganised", Severity.Info, L.T($"Hub riorganizzata: {done.Count} passi", $"Hub reorganised: {done.Count} steps"), "layout")]);
        await Check(new Forced(ctx), ct);
        var parts = new List<string> { L.T($"{done.Count} passi fatti.", $"{done.Count} steps done.") };
        if (failed.Count > 0) parts.Add(L.T("Non fatti: ", "Not done: ") + string.Join("; ", failed) + ".");
        return new ActionResult(failed.Count == 0, string.Join(' ', parts), L.T("ANNULLA nella pagina Organizzazione, sotto Hub", "UNDO on the Organisation page, under Hub"));
    }

    /// <summary>One step on disk.</summary>
    private ActionResult Run(HubOp op) => op.Kind switch
    {
        "folder" => _env.Move(op.From, op.To),
        "file" => _env.MoveFile(op.From, op.To),
        "create" => _env.CreateFolder(op.From),
        "remove" => _env.IsEmpty(op.From) ? _env.RemoveOld(op.From) : new ActionResult(false, L.T("non è vuota: resta", "not empty: it stays")),
        _ => new ActionResult(false, op.Kind),
    };

    private async Task<ActionResult> DoHubUndo(IModuleContext ctx, string? id, CancellationToken ct)
    {
        var changes = HubChanges(ctx).ToList();
        if (changes.FirstOrDefault(c => c.Id == id) is not { } change) return new ActionResult(false, L.T("Riorganizzazione non trovata.", "Reorganisation not found."));
        var failed = new List<string>();
        foreach (var op in change.Done.Reverse().Select(LayoutHub.Reverse))
        {
            var result = Run(op);
            if (!result.Ok) failed.Add($"{op.From} ({result.Detail})");
            else if (op.Kind == "folder")
            {
                LayoutConfig.Rebase(_config, op.From, op.To);
                _env.FixShortcuts(op.From, op.To);
            }
        }
        _config.UpdateModule(Id, m => m.Settings["hubSections"] = JsonSerializer.SerializeToElement(change.OldSections));
        if (Others.Any(m => m.Info.Id == "cleanup")) _config.UpdateModule("cleanup", m => m.Settings.Remove("hubSubsections"));
        changes.Remove(change);
        ctx.SetState("hub_changes", JsonSerializer.Serialize(changes));
        await Check(new Forced(ctx), ct);
        return new ActionResult(failed.Count == 0, failed.Count == 0 ? L.T("La Hub è tornata com'era.", "The Hub is back as it was.")
            : L.T("Rimessa in parte: ", "Partly put back: ") + string.Join("; ", failed));
    }

    private static IReadOnlyList<HubChange> HubChanges(IModuleContext ctx)
    {
        try { return ctx.GetState("hub_changes") is { } json ? JsonSerializer.Deserialize<List<HubChange>>(json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private async Task<ActionResult> DoDeleteOld(IModuleContext ctx, string? id, CancellationToken ct)
    {
        var moves = Moves(ctx).ToList();
        if (moves.FirstOrDefault(m => m.Id == id) is not { Old: { } old } move) return new ActionResult(false, L.T("Non c'è una copia vecchia da eliminare.", "There is no old copy to delete."));
        var result = _env.RemoveOld(old);
        if (!result.Ok) return result;
        moves[moves.IndexOf(move)] = move with { Old = null };
        ctx.SetState("moves", JsonSerializer.Serialize(moves));
        await Check(new Forced(ctx), ct);
        return result;
    }

    private async Task<ActionResult> DoKeep(IModuleContext ctx, string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ActionResult(false, L.T("Manca la cartella.", "The folder is missing."));
        var keep = ctx.Settings.GetList("keep").ToList();
        if (!keep.Any(k => LayoutPaths.Same(k, path))) keep.Add(path);
        _config.UpdateModule(Id, m => m.Settings["keep"] = JsonSerializer.SerializeToElement(keep));
        await Check(new Forced(ctx), ct);
        return new ActionResult(true, L.T($"{path} resta dov'è e non viene più proposta.", $"{path} stays where it is and is no longer proposed."),
            L.T("Si toglie da Impostazioni → Organizzazione del PC → Cartelle che restano dove sono.", "Remove it in SETTINGS → PC organisation → Folders that stay where they are."));
    }

    private static IReadOnlyList<LayoutMove> Moves(IModuleContext ctx)
    {
        try
        {
            return ctx.GetState("moves") is { } json ? JsonSerializer.Deserialize<List<LayoutMove>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ── Export / import ──────────────────────────────────────────────────────

    /// <summary>
    /// This PC's layout, made general: the person's choices and the template, plus their own extra rules for the same settings
    /// when they can be written with the layout's folders (a line naming a folder of this PC only is left out).
    /// </summary>
    public string Export(ISettings settings, string dataDirectory)
    {
        var choices = Choices(settings);
        var template = Template(dataDirectory);
        var vars = new (string Name, string Value)[] { ("games", choices.Games), ("emulators", choices.Emulators), ("software", choices.Software), ("backups", choices.Backups) }
            .Where(v => v.Value.Length > 0).OrderByDescending(v => v.Value.Length).ToList();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string General(string text)
        {
            foreach (var (name, value) in vars) text = LayoutPaths.RebaseText(text, value, "{" + name + "}");
            return LayoutPaths.RebaseText(text, profile, "%USERPROFILE%");
        }
        var rules = template.Rules.ToList();
        foreach (var key in template.Rules.Where(r => r.Line is not null).Select(r => (r.Module, r.Key)).Distinct())
        {
            if (Others.FirstOrDefault(m => m.Info.Id == key.Module) is not { } module || module.Settings.FirstOrDefault(d => d.Key == key.Key) is not { } def) continue;
            var resolved = choices.Rules(template).Where(r => r.Module == key.Module && r.Key == key.Key && r.Line is not null).Select(r => r.Line!.Split(def.Separator)[0].Trim()).ToList();
            foreach (var line in new ModuleSettings(module.Settings, _config.GetModule(module.Info.Id).Settings).GetList(key.Key))
            {
                if (resolved.Any(h => LayoutPaths.Same(h, line.Split(def.Separator)[0].Trim()))) continue; // Already a rule of the template.
                var general = General(line);
                if (OtherDrive().IsMatch(general.Replace(@"C:\Users\Public", "", StringComparison.OrdinalIgnoreCase))) continue;
                rules.Add(new LayoutRule(key.Module, key.Key, Line: general, It: general, En: general));
            }
        }
        var exported = template with
        {
            Rules = rules,
            Variables = new Dictionary<string, string>
            {
                ["games"] = choices.Games, ["software"] = choices.Software, ["emulators"] = choices.Emulators, ["backups"] = choices.Backups, ["hub"] = choices.Hub,
            },
            HubSections = choices.HubSections,
        };
        return exported.ToJson();
    }

    [GeneratedRegex(@"[A-Za-z]:\\")]
    private static partial Regex OtherDrive();

    /// <summary>
    /// Takes someone else's layout: its template replaces the one in use, and its choices fill the settings, moved to a drive of this
    /// PC when theirs does not exist here. Nothing is created or moved: the person looks at the page and presses APPLY.
    /// </summary>
    public IReadOnlyList<string> Import(string json, string dataDirectory)
    {
        var template = LayoutTemplate.Parse(json);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(TemplateFile(dataDirectory), (template with { Variables = null, HubSections = null }).ToJson());
        var drives = _env.Drives().Select(d => d.TrimEnd('\\').ToUpperInvariant()).ToList();
        var notes = new List<string>();
        _config.UpdateModule(Id, m =>
        {
            foreach (var (key, value) in template.Variables ?? new Dictionary<string, string>())
            {
                if (key is not ("games" or "software" or "emulators" or "backups" or "hub")) continue;
                var v = value;
                if (LayoutPaths.Drive(v) is { } drive && !drives.Contains(drive))
                {
                    // Their drive is not on this PC: the automatic choice (or C:) instead, said in the result.
                    notes.Add(L.T($"{key}: il disco {drive} qui non c'è, scegli tu dove", $"{key}: drive {drive} is not on this PC, choose where"));
                    v = key == "games" ? "" : $"{LayoutChoices.SystemDrive}{v[2..]}";
                }
                m.Settings[key] = JsonSerializer.SerializeToElement(v);
            }
            if (template.HubSections is { Count: > 0 } sections) m.Settings["hubSections"] = JsonSerializer.SerializeToElement(sections);
        });
        return notes;
    }

    /// <summary>The same context, as asked by the person: right after an action the page is rewritten even during a game.</summary>
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
