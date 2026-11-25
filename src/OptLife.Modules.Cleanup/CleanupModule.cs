using System.Globalization;
using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Cleanup.Windows;

namespace OptLife.Modules.Cleanup;

/// <summary>The disk, the registry and the Recycle Bin, so the rules can be tested without them.</summary>
public interface ICleanupEnvironment
{
    /// <summary>Names of what is directly inside a folder; null when the folder is not there.</summary>
    IReadOnlyList<string>? List(string folder);

    /// <summary>What is directly inside a folder, each with its full weight.</summary>
    IReadOnlyList<DiskItem> Weigh(string folder);

    /// <summary>Folders with one of these names under a root, weighed; only those next to the marker when one is given.</summary>
    IReadOnlyList<DiskItem> Find(string root, IReadOnlyList<string> names, string? marker);

    /// <summary>Shortcuts under a folder that point to something that no longer exists, by their path inside it.</summary>
    IReadOnlyList<string> BrokenLinks(string folder);

    /// <summary>Full paths of the shortcuts in a folder that open nothing any more (file gone, Store app removed).</summary>
    IReadOnlyList<string> BrokenShortcuts(string folder, bool recursive) => [];

    IReadOnlyList<StartupEntry> Startup();
    IReadOnlyList<string> InstalledPrograms();

    ActionResult Move(string folder, IReadOnlyList<string> names, string destination);
    ActionResult Recycle(IReadOnlyList<string> paths);
    ActionResult RemoveStartup(string id);
    ActionResult Open(string folder);

    /// <summary>The shortcuts (.lnk, .url) directly inside a folder, with what each opens.</summary>
    IReadOnlyList<Shortcut> Shortcuts(string folder) => [];

    /// <summary>Names of the folders directly inside a folder.</summary>
    IReadOnlyList<string> Subfolders(string folder) => [];

    /// <summary>Moves one file into a folder (same name); refuses when one with that name is already there.</summary>
    ActionResult MoveFile(string path, string toFolder, string? newName = null) => new(false, "");

    /// <summary>The same as <see cref="Move"/>, saying where each thing ended up (a namesake gets " (2)"), so it can be put back.</summary>
    (ActionResult Result, IReadOnlyList<MovedItem> Moved) MoveTracked(string folder, IReadOnlyList<string> names, string destination)
    {
        var r = Move(folder, names, destination);
        return (r, r.Ok ? [.. names.Select(n => new MovedItem(Path.Combine(folder, n), Path.Combine(destination, n)))] : []);
    }

    /// <summary>Puts moved things back, only where their old place is free and they are still where they were put.</summary>
    ActionResult MoveBack(IReadOnlyList<MovedItem> items) => new(false, "");
}

/// <summary>Something OptLife moved: from where, to where (full paths).</summary>
public sealed record MovedItem(string From, string To);

/// <summary>One press of "move what is out of place", kept for UNDO.</summary>
public sealed record LooseMove(string Id, string Folder, DateTimeOffset At, IReadOnlyList<MovedItem> Items);

/// <summary>
/// Keeps the PC tidy by the person's own rules, all of them editable from the menu: which folders
/// must hold only certain things, where new things should not appear, which shortcuts must work,
/// what is heavy and forgotten, what could be thrown away because it rebuilds itself, and what
/// newly starts with Windows. It never deletes: the most it does, and only when asked, is move
/// things aside or to the Recycle Bin.
/// </summary>
public sealed class CleanupModule : IModule
{
    public const string LooseUndo = "move-loose-undo";
    public const string MoveLoose = "move-loose", OpenFolder = "open-folder", Recycle = "recycle",
        RemoveStartup = "remove-startup", RemoveUnwanted = "remove-unwanted",
        SortHub = "sort-hub", SortOne = "sort-one", SortPick = "sort-pick", SortUndo = "sort-undo", CleanDesktop = "clean-desktop", DropDuplicate = "drop-duplicate", SortRename = "sort-rename", RecycleBroken = "recycle-broken", HubKind = "hub_board",
        TwinKeepHub = "twin-keep-hub", TwinUseNew = "twin-use-new", TwinDecide = "twin-decide", TwinForget = "twin-forget";

    private const int GrowthDays = 30;
    private static readonly TimeSpan HeavyEvery = TimeSpan.FromHours(12);
    private static readonly TimeSpan MapEvery = TimeSpan.FromHours(24);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly ICleanupEnvironment _env;

    public CleanupModule() : this(new WindowsCleanupEnvironment()) { }

    internal CleanupModule(ICleanupEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(
        "cleanup", L.T("Pulizia e ordine", "Cleanup and order"),
        L.T("Tiene in ordine il PC secondo le tue regole: cartelle che devono restare pulite, cose nuove fuori posto, collegamenti rotti, file pesanti dimenticati, spazio da liberare, programmi nuovi all'avvio.", "Keeps the PC tidy by your rules: folders that must stay clean, new things out of place, broken shortcuts, forgotten heavy files, space to free, new start-up programs."), FindingArea.Tidy);

    public IReadOnlyList<SettingDefinition> Settings { get; } = Grouped(
    [
        SettingDefinition.Table("tidy", [L.T("Cartella", "Folder"), L.T("Nomi ammessi", "Allowed names"), L.T("Dove spostare il resto", "Move the rest to")], L.T("Cartelle che devono contenere solo certe cose", "Folders that must hold only certain things"),
            L.T("Scritte come: cartella | nomi ammessi separati da virgola | dove spostare il resto. Per esempio un Desktop con una sola cartella. Senza l'ultimo campo segnala soltanto.", "Written as: folder | comma-separated allowed names | where to move the rest. For example a Desktop with a single folder. Without the last field it only reports.")),
        SettingDefinition.Table("crowded", [L.T("Cartella", "Folder"), L.T("Elementi al massimo", "Items at most")], L.T("Cartelle da non riempire troppo", "Folders not to fill up"),
            L.T("Scritte come: cartella | quanti elementi al massimo. Per esempio un Desktop: avvisa soltanto, non sposta niente.", "Written as: folder | how many items at most. For example a Desktop: it only warns, it moves nothing.")),
        SettingDefinition.Table("watch", [L.T("Cartella", "Folder"), L.T("Consiglio", "Advice")], L.T("Cartelle in cui non deve comparire roba nuova", "Folders where nothing new should appear"),
            L.T("Scritte come: cartella | consiglio. La prima volta prende nota di cosa c'è; da lì in poi segnala ogni cosa nuova, che puoi accettare ignorando l'avviso.", "Written as: folder | advice. The first time it notes what is there; from then on it reports every new thing, which you can accept by ignoring the warning.")),
        SettingDefinition.List("shortcuts", L.T("Cartelle di collegamenti da controllare", "Shortcut folders to check"),
            L.T("Segnala i collegamenti che puntano a un programma o a una cartella che non esiste più.", "Reports shortcuts pointing to a program or folder that no longer exists.")),
        SettingDefinition.Table("heavy", [L.T("Cartella", "Folder"), "GB", L.T("Giorni", "Days")], L.T("Cartelle da svuotare ogni tanto", "Folders to empty now and then"),
            L.T("Scritte come: cartella | GB | giorni. Segnala ciò che pesa almeno così, o che è fermo da almeno quei giorni. Per esempio i Download.", "Written as: folder | GB | days. Reports what weighs at least that much, or has sat still for at least those days. For example Downloads.")),
        SettingDefinition.Table("rebuildable", [L.T("Nome", "Name"), L.T("Dove cercare", "Where to look"), L.T("Nomi delle cartelle", "Folder names"), L.T("Giorni", "Days"), L.T("File accanto", "File next to it")], L.T("Cartelle che si rigenerano da sole", "Folders that rebuild themselves"),
            L.T("Scritte come: nome | dove cercare | nomi delle cartelle | giorni | file che deve esserci accanto. Per esempio: Compilati .NET | C:\\Progetti | bin, obj | 30 | *.csproj. " +
            "Propone di mandare al Cestino quelle ferme da almeno quei giorni; il file accanto serve a non confondere una cartella qualsiasi che ha lo stesso nome.",
            "Written as: name | where to look | folder names | days | file that must be next to it. For example: .NET builds | C:\\Projects | bin, obj | 30 | *.csproj. It offers to send to the Recycle Bin the ones untouched for at least those days; the file next to it avoids mistaking any folder with the same name.")),
        SettingDefinition.Int("minGb", L.T("GB minimi per proporre una pulizia", "Minimum GB to suggest a cleanup"),
            L.T("Sotto questa soglia non vale la pena disturbare.", "Below this it is not worth bothering."), 2, 1, 500),
        SettingDefinition.List("map", L.T("Cartelle di cui seguire la crescita", "Folders whose growth to follow"),
            L.T("Una volta al giorno pesa ciò che contengono e ricorda il peso, per dire cosa è cresciuto nell'ultimo mese.", "Once a day it weighs what they hold and remembers it, to tell what grew in the last month.")),
        SettingDefinition.Int("growthGb", L.T("Crescita da segnalare (GB in un mese)", "Growth to report (GB in a month)"),
            L.T("Una cartella cresciuta almeno così in 30 giorni viene segnalata.", "A folder that grew at least this much in 30 days is reported."), 20, 1, 1000),
        SettingDefinition.Bool("watchStartup", L.T("Segnala i programmi nuovi all'avvio", "Report new start-up programs"),
            L.T("Avvisa quando un programma si aggiunge a quelli che partono con Windows.", "Warns when a program adds itself to those starting with Windows."), true),
        SettingDefinition.List("unwantedStartup", L.T("Programmi da non volere mai all'avvio", "Programs never wanted at start-up"),
            L.T("Parti del nome. Alcuni programmi si rimettono all'avvio ogni volta che li usi: questi vengono segnalati ogni volta, e tolti da soli se l'azione qui sopra è su \"da solo\".", "Parts of the name. Some programs put themselves back at start-up every time you use them: these are reported every time, and removed automatically if the action above is set to \"by itself\".")),
        SettingDefinition.Table("hubSort", [L.T("Sezione (nomi possibili, con /)", "Section (possible names, with /)"), L.T("Parole", "Words")],
            L.T("Dove vanno i collegamenti da sistemare", "Where shortcuts to sort go"),
            L.T("Scritte come: nomi della sezione | parole. Un collegamento nella cartella \"da sistemare\" va nella sezione della cartella sul Desktop che contiene uno di quei nomi, quando il suo nome o ciò che apre contiene una delle parole. Vince la prima riga.",
                "Written as: section names | words. A shortcut in the \"to sort\" folder goes to the Desktop folder's section whose name holds one of those names, when its name or what it opens holds one of the words. The first line wins."),
            HubSort.Defaults),
        SettingDefinition.List("hubSubsections", L.T("Sotto-sezioni della Hub", "Hub sub-sections"),
            L.T("Cartelle dentro una sezione che ricevono anche loro i collegamenti (\"1 Games\\Emulation\"): le scrive la riorganizzazione della Hub.",
                "Folders inside a section that also receive shortcuts (\"1 Games\\Emulation\"): written by the Hub reorganisation.")).AsAdvanced(),
        SettingDefinition.Choice("hubSortMode", L.T("Sistemare i collegamenti", "Sorting shortcuts"),
            L.T("Chiedi: propone la sezione e la sposti tu con un clic. Da solo: sposta da solo quelli per cui la sezione è sicura (una sola possibile); gli altri restano da scegliere. Ogni spostamento si annulla.",
                "Ask: suggests the section and you move it with a click. Automatically: moves by itself those whose section is certain (only one possible); the others wait for your choice. Every move can be undone."),
            L.T("chiedi", "ask"), L.T("da solo", "automatically")),
        SettingDefinition.Choice("brokenMode", L.T("Collegamenti che non aprono più niente", "Shortcuts that open nothing any more"),
            L.T("Sul Desktop e nella cartella dei collegamenti, quelli di un programma disinstallato (o di un'app dello Store tolta). Chiedi: te li propongo con un pulsante. Da solo: li mando nel Cestino appena li trovo. Si riprendono dal Cestino; quelli verso un disco non collegato o la rete non si toccano.",
                "On the Desktop and in the shortcuts folder, those of an uninstalled program (or a removed Store app). Ask: I suggest them with a button. Automatically: I send them to the Recycle Bin as soon as I find them. They can be restored from the Recycle Bin; those to a drive not plugged in or the network are left alone."),
            L.T("chiedi", "ask"), L.T("da solo", "automatically")),
    ]);

    /// <summary>Headings for the settings: the Desktop and its folder, folders to keep clean, space, start-up.</summary>
    private static IReadOnlyList<SettingDefinition> Grouped(IReadOnlyList<SettingDefinition> list) => [.. list.Select(d => d.In(d.Key switch
    {
        "tidy" or "shortcuts" or "hubSort" or "hubSortMode" or "brokenMode" or "crowded" => L.T("Desktop e cartella dei collegamenti", "Desktop and shortcuts folder"),
        "watch" => L.T("Cartelle da tenere pulite", "Folders to keep clean"),
        "heavy" or "rebuildable" or "minGb" or "map" or "growthGb" => L.T("Spazio sui dischi", "Disk space"),
        _ => L.T("Programmi all'avvio", "Start-up programs"),
    }))
        // Each group together, in this order (the list above is in the order the settings were added over time).
        .OrderBy(d => d.Key switch { "tidy" or "shortcuts" or "hubSort" or "hubSortMode" or "brokenMode" or "crowded" => 0, "watch" => 1, "heavy" or "rebuildable" or "minGb" or "map" or "growthGb" => 2, _ => 3 })];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("hub", L.T("Collegamenti", "Shortcuts"), HubKind, [], Days: 365, Menu: ViewMenu.Order, Order: 2,
            EmptyText: L.T("Nessuna cartella \"da sistemare\": serve una regola \"Cartelle che devono contenere solo certe cose\" con una cartella dove spostare il resto.",
                "No \"to sort\" folder: it needs a \"Folders that must hold only certain things\" rule with a folder to move the rest to."),
            Description: L.T("I collegamenti finiti nella cartella \"da sistemare\" (quelli che compaiono sul Desktop vengono spostati lì) con la sezione dove andrebbero, decisa da ciò che aprono. SPOSTA li mette lì; SCEGLI DOVE ti fa scegliere un'altra sezione. Ogni spostamento si annulla.",
                "The shortcuts that ended up in the \"to sort\" folder (those appearing on the Desktop are moved there) with the section they belong in, decided by what they open. MOVE puts them there; PICK WHERE lets you choose another section. Every move can be undone."),
            Layout: ViewLayout.Checklist,
            Buttons: _ =>
            [
                CleanDesktopButton,
                new ViewButton(L.T("Tutti insieme", "All at once"), L.T("Scegli dove va ogni collegamento", "Choose where each shortcut goes"),
                    L.T("Per ogni collegamento le sezioni della cartella sul Desktop, con già spuntata quella proposta.", "For each shortcut the Desktop folder's sections, the suggested one already ticked."),
                    SortPick, ButtonText: L.T("SCEGLI DOVE", "PICK WHERE")),
            ]),
        // The same button among the Tools, where people look for "do it now" things.
        new ModuleView("tidy", L.T("Pulizia", "Cleanup"), "", [], Menu: ViewMenu.Tools, Order: 1, Layout: ViewLayout.Buttons,
            Description: L.T("Mette in ordine il Desktop in un colpo solo. Non cancella niente: sposta, e ogni spostamento si annulla.",
                "Tidies the Desktop in one go. It deletes nothing: it moves, and every move can be undone."),
            Buttons: _ => [CleanDesktopButton]),
    ];

    private static ViewButton CleanDesktopButton => new(L.T("Desktop", "Desktop"), L.T("Pulisci il Desktop", "Clean the Desktop"),
        L.T("Sposta tutto ciò che è sciolto sul Desktop (e sul Desktop pubblico) in \"da sistemare\" e subito propone la sezione della Hub per ogni collegamento; se \"Sistemare i collegamenti\" è su \"da solo\" mette già al loro posto quelli sicuri.",
            "Moves everything loose on the Desktop (and the public Desktop) into \"to sort\" and right away suggests the Hub section for each shortcut; if \"Sorting shortcuts\" is set to \"automatically\" it already puts the certain ones in place."),
        CleanDesktop, ButtonText: L.T("PULISCI", "CLEAN"));

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo dell'ordine", "Checking the order"), TimeSpan.FromMinutes(30), CheckAsync),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(MoveLoose, L.T("Sposta ciò che è fuori posto", "Move what is out of place"),
            L.T("Sposta gli elementi fuori posto nella cartella indicata dalla regola. Non cancella niente.", "Moves the items out of place to the folder the rule names. Deletes nothing."), Permission.Ask,
            (ctx, folder, _) => Task.FromResult(DoMove(ctx, folder))),
        new ModuleAction(LooseUndo, L.T("Rimetti dov'erano le cose spostate", "Put moved things back"),
            L.T("Rimette nella cartella di partenza ciò che \"Sposta ciò che è fuori posto\" aveva spostato, se è ancora dove l'aveva messo e il suo posto è libero.",
                "Puts back in the starting folder what \"Move what is out of place\" had moved, if it is still where it was put and its place is free."),
            Permission.Ask, (ctx, id, _) => Task.FromResult(DoLooseUndo(ctx, id))),
        new ModuleAction(OpenFolder, L.T("Apri la cartella", "Open the folder"), L.T("Apre la cartella in Esplora file.", "Opens the folder in File Explorer."), Permission.Ask,
            (_, folder, _) => Task.FromResult(folder is { Length: > 0 } ? _env.Open(folder) : new ActionResult(false, L.T("Manca la cartella.", "The folder is missing.")))),
        new ModuleAction(Recycle, L.T("Sposta nel Cestino le cartelle che si rigenerano", "Send self-rebuilding folders to the Recycle Bin"),
            L.T("Manda al Cestino le cartelle di una regola \"si rigenerano da sole\" che sono ferme da abbastanza giorni. Dal Cestino si possono riprendere.", "Sends to the Recycle Bin the folders of a \"rebuild themselves\" rule that have sat still long enough. They can be restored from the Recycle Bin."), Permission.Ask,
            (ctx, rule, _) => Task.FromResult(DoRecycle(ctx, rule)), NeverAuto: true),
        new ModuleAction(RemoveStartup, L.T("Togli un programma dall'avvio", "Remove a program from start-up"),
            L.T("Toglie un programma da quelli che partono con Windows. Il programma resta installato.", "Removes a program from those starting with Windows. The program stays installed."), Permission.Ask,
            (_, id, _) => Task.FromResult(id is { Length: > 0 } ? _env.RemoveStartup(id) : new ActionResult(false, L.T("Manca il programma.", "The program is missing."))), NeverAuto: true),
        new ModuleAction(RemoveUnwanted, L.T("Togli dall'avvio i programmi che non vuoi", "Remove unwanted programs from start-up"),
            L.T("Toglie dall'avvio un programma dell'elenco \"da non volere mai all'avvio\" che si è rimesso da solo.", "Removes from start-up a program of the \"never wanted at start-up\" list that put itself back."), Permission.Ask,
            (ctx, id, _) => Task.FromResult(DoRemoveUnwanted(ctx, id))),
        new ModuleAction(SortHub, L.T("Sistema i collegamenti nella Hub", "Sort the shortcuts into the Hub"),
            L.T("Sposta ogni collegamento della cartella \"da sistemare\" nella sezione proposta (quelli senza proposta restano). Si annulla.",
                "Moves each shortcut of the \"to sort\" folder to its suggested section (those without a suggestion stay). Can be undone."),
            Permission.Ask, (ctx, _, _) => Task.FromResult(DoSort(ctx, [.. Proposals(ctx).Where(p => p.Proposal.Section is not null).Select(p => (p.Proposal.Shortcut.Path, p.Hub, p.Proposal.Section!))]))),
        new ModuleAction(SortOne, L.T("Sposta un collegamento", "Move a shortcut"),
            L.T("Sposta il collegamento nella sezione indicata.", "Moves the shortcut to the given section."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoSortOne(ctx, arg))),
        new ModuleAction(SortPick, L.T("Scegli dove va ogni collegamento", "Choose where each shortcut goes"),
            L.T("Sposta i collegamenti nelle sezioni spuntate (per ognuno conta la prima spuntata).", "Moves the shortcuts to the ticked sections (for each, the first ticked one counts)."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoSortPicked(ctx, arg)),
            Choices: ctx => [.. Proposals(ctx).SelectMany(p => p.Sections.Select(s => new ActionChoice($"{p.Proposal.Shortcut.Path}|{s}", s,
                p.Proposal.Shortcut.Target, Checked: s == p.Proposal.Section, Category: p.Proposal.Shortcut.Name, Icon: p.Proposal.Shortcut.Path)))]),
        new ModuleAction(CleanDesktop, L.T("Pulisci il Desktop", "Clean the Desktop"),
            L.T("Sposta ciò che è sciolto sul Desktop in \"da sistemare\" e propone subito dove va ogni collegamento. Non cancella niente.",
                "Moves what is loose on the Desktop into \"to sort\" and right away suggests where each shortcut goes. Deletes nothing."),
            Permission.Ask, (ctx, _, _) => Task.FromResult(DoCleanDesktop(ctx))),
        new ModuleAction(RecycleBroken, L.T("Metti nel Cestino i collegamenti rotti", "Recycle broken shortcuts"),
            L.T("Manda nel Cestino i collegamenti del Desktop e della cartella dei collegamenti che non aprono più niente (programma disinstallato, app tolta). Si riprendono dal Cestino.",
                "Sends to the Recycle Bin the Desktop and shortcuts-folder shortcuts that open nothing any more (uninstalled program, removed app). They can be restored from the Recycle Bin."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoRecycleBroken(ctx, arg)), NeverAuto: true),
        new ModuleAction(DropDuplicate, L.T("Elimina il doppione", "Remove the duplicate"),
            L.T("Manda nel Cestino il collegamento da sistemare quando nella sezione ce n'è già uno uguale (che apre lo stesso programma).",
                "Sends the shortcut to sort to the Recycle Bin when the section already has an identical one (opening the same program)."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoDropDuplicate(ctx, arg)), NeverAuto: true),
        new ModuleAction(SortRename, L.T("Tieni tutti e due", "Keep both"),
            L.T("Sposta il collegamento nella sezione con un nome diverso, quando ce n'è già uno con lo stesso nome che apre un'altra cosa.",
                "Moves the shortcut to the section under another name, when one with the same name opening something else is already there."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoSortRename(ctx, arg))),
        new ModuleAction(TwinKeepHub, L.T("Tieni quello della Hub", "Keep the one in the Hub"),
            L.T("Quando nella sezione c'è già un collegamento con lo stesso nome: resta quello della Hub, il nuovo va nel Cestino.",
                "When the section already has a shortcut with the same name: the one in the Hub stays, the new one goes to the Recycle Bin."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoTwin(ctx, arg, TwinChoice.KeepHub)), NeverAuto: true),
        new ModuleAction(TwinUseNew, L.T("Usa il nuovo", "Use the new one"),
            L.T("Quando nella sezione c'è già un collegamento con lo stesso nome: quello della Hub va nel Cestino e il nuovo prende il suo posto.",
                "When the section already has a shortcut with the same name: the one in the Hub goes to the Recycle Bin and the new one takes its place."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoTwin(ctx, arg, TwinChoice.UseNew)), NeverAuto: true),
        new ModuleAction(TwinDecide, L.T("Scegli per i collegamenti con lo stesso nome", "Choose for the shortcuts with the same name"),
            L.T("Per ogni collegamento che ha già un omonimo nella sua sezione, spunta cosa fare (conta la prima scelta spuntata). Con \"Ricorda\" la prossima volta che un aggiornamento lo ricrea OptLife fa la stessa cosa da solo: lo vedi in Attività, e ciò che va nel Cestino si riprende da lì.",
                "For each shortcut that already has a namesake in its section, tick what to do (the first ticked choice counts). With \"Remember\", the next time an update recreates it OptLife does the same by itself: you see it in Activity, and what goes to the Recycle Bin can be restored from there."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoTwinDecide(ctx, arg)),
            Choices: TwinChoices),
        new ModuleAction(TwinForget, L.T("Dimentica una scelta ricordata", "Forget a remembered choice"),
            L.T("Toglie la scelta ricordata per un collegamento: la prossima volta OptLife chiede di nuovo.", "Removes the remembered choice for a shortcut: next time OptLife asks again."),
            Permission.Ask, (ctx, arg, _) => Task.FromResult(DoTwinForget(ctx, arg))),
        new ModuleAction(SortUndo, L.T("Rimetti un collegamento dov'era", "Put a shortcut back"),
            L.T("Rimette il collegamento nella cartella \"da sistemare\".", "Puts the shortcut back in the \"to sort\" folder."),
            Permission.Ask, (ctx, id, _) => Task.FromResult(DoSortUndo(ctx, id))),
    ];

    // ── Shortcuts in the Hub ─────────────────────────────────────────────────

    /// <summary>The "to sort" folders: where the tidy rules move what is out of place; their Hub is the folder they are in.</summary>
    /// <remarks>
    /// A Hub is a folder inside a folder kept tidy (the Desktop\Hub of "0 To sort"). A place outside it ("Documents\From the Desktop"
    /// of a clean Desktop) is just where things go: its parent is not a Hub, and its folders are not sections.
    /// </remarks>
    private static IEnumerable<(string Inbox, string Hub)> Inboxes(IModuleContext ctx)
    {
        var rules = ctx.Settings.GetList("tidy").Select(TidyRule.Parse).OfType<TidyRule>().Where(r => r.MoveTo is not null).ToList();
        static string Full(string p) => Environment.ExpandEnvironmentVariables(p).TrimEnd('\\');
        return rules.Select(r => r.MoveTo!.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(i => (Inbox: i, Hub: Path.GetDirectoryName(i) ?? i))
            .Where(x => rules.Any(r => Full(x.Hub).StartsWith(Full(r.Folder) + "\\", StringComparison.OrdinalIgnoreCase)));
    }

    private List<(SortProposal Proposal, string Hub, IReadOnlyList<string> Sections)> Proposals(IModuleContext ctx)
    {
        var rules = ctx.Settings.GetList("hubSort").Select(SortRule.Parse).OfType<SortRule>().ToList();
        var list = new List<(SortProposal, string, IReadOnlyList<string>)>();
        foreach (var (inbox, hub) in Inboxes(ctx))
        {
            // The sections, then the sub-sections that exist ("1 Games\Emulation"), more specific than their section.
            var sections = _env.Subfolders(hub).Where(s => !s.Equals(Path.GetFileName(inbox), StringComparison.OrdinalIgnoreCase)).ToList();
            var subs = ctx.Settings.GetList("hubSubsections").Select(x => x.Trim().Trim('\\'))
                .Where(x => x.Contains('\\') && sections.Contains(x.Split('\\')[0], StringComparer.OrdinalIgnoreCase)
                            && _env.Subfolders(Path.Combine(hub, x.Split('\\')[0])).Contains(x.Split('\\')[1], StringComparer.OrdinalIgnoreCase)).ToList();
            sections = [.. subs, .. sections];
            if (sections.Count == 0) continue;
            foreach (var s in _env.Shortcuts(inbox)) list.Add((HubSort.Propose(s, sections, rules), hub, sections));
        }
        return list;
    }

    private ActionResult DoSort(IModuleContext ctx, IReadOnlyList<(string Path, string Hub, string Section)> moves)
    {
        if (moves.Count == 0) return new ActionResult(true, L.T("Niente da spostare.", "Nothing to move."));
        var done = Load<List<HubMove>>(ctx, "hub_moves") ?? [];
        var ok = new List<string>();
        var twins = new List<string>();
        var failed = new List<string>();
        foreach (var (path, hub, section) in moves)
        {
            var to = Path.Combine(hub, section);
            // Already one with this name there: not an error, a choice (remove the duplicate, or keep both) on the Shortcuts page.
            if (Twin(new Shortcut(path, "", ""), hub, section) is not null)
            {
                twins.Add(Path.GetFileNameWithoutExtension(path));
                continue;
            }
            var r = _env.MoveFile(path, to);
            if (!r.Ok)
            {
                failed.Add($"{Path.GetFileNameWithoutExtension(path)} ({r.Detail})");
                continue;
            }
            var move = new HubMove(Guid.NewGuid().ToString("N")[..8], path, Path.Combine(to, Path.GetFileName(path)), ctx.Now);
            done.Add(move);
            ok.Add($"{Path.GetFileNameWithoutExtension(path)} → {section}");
            ctx.RecordEvents([new NewEvent(ctx.Now, "hub_sorted", Severity.Info, L.T($"Collegamento sistemato: {Path.GetFileNameWithoutExtension(path)} → {section}", $"Shortcut sorted: {Path.GetFileNameWithoutExtension(path)} → {section}"), "hub")]);
        }
        ctx.SetState("hub_moves", JsonSerializer.Serialize(done.TakeLast(100).ToList()));
        WriteHubBoard(ctx);
        var parts = new List<string>();
        if (ok.Count > 0) parts.Add(L.T("Spostati: ", "Moved: ") + string.Join(", ", ok) + ".");
        if (twins.Count > 0) parts.Add(L.T($"Da decidere (nella sezione ce n'è già uno con lo stesso nome, vedi la pagina Collegamenti): {string.Join(", ", twins)}.", $"To decide (the section already has one with the same name, see the Shortcuts page): {string.Join(", ", twins)}."));
        if (failed.Count > 0) parts.Add(L.T("Non spostati: ", "Not moved: ") + string.Join("; ", failed) + ".");
        return new ActionResult(failed.Count == 0, string.Join(' ', parts), ok.Count > 0 ? L.T("ANNULLA nella pagina Collegamenti.", "UNDO on the Shortcuts page.") : null);
    }

    /// <summary>"file|section": only a shortcut of a "to sort" folder, only into a section of its own Hub.</summary>
    private ActionResult DoSortOne(IModuleContext ctx, string? arg)
    {
        var cut = (arg ?? "").LastIndexOf('|');
        if (cut <= 0) return new ActionResult(false, L.T("Manca il collegamento o la sezione.", "The shortcut or the section is missing."));
        var (path, section) = (arg![..cut], arg[(cut + 1)..]);
        var p = Proposals(ctx).FirstOrDefault(x => x.Proposal.Shortcut.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (p.Proposal is null || !p.Sections.Contains(section, StringComparer.OrdinalIgnoreCase))
            return new ActionResult(false, L.T("Non è un collegamento da sistemare o una sezione della Hub.", "Not a shortcut to sort or a Hub section."));
        return DoSort(ctx, [(path, p.Hub, section)]);
    }

    private ActionResult DoSortPicked(IModuleContext ctx, string? arg)
    {
        var picked = (arg ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.LastIndexOf('|') is var c and > 0 ? (Path: a[..c], Section: a[(c + 1)..]) : default)
            .Where(a => a.Path is not null).GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var proposals = Proposals(ctx);
        var moves = new List<(string, string, string)>();
        foreach (var (path, section) in picked)
        {
            var p = proposals.FirstOrDefault(x => x.Proposal.Shortcut.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (p.Proposal is not null && p.Sections.Contains(section, StringComparer.OrdinalIgnoreCase)) moves.Add((path, p.Hub, section));
        }
        return DoSort(ctx, moves);
    }

    private ActionResult DoSortUndo(IModuleContext ctx, string? id)
    {
        var moves = Load<List<HubMove>>(ctx, "hub_moves") ?? [];
        if (moves.FirstOrDefault(m => m.Id == id) is not { } move) return new ActionResult(false, L.T("Spostamento non trovato.", "Move not found."));
        var r = _env.MoveFile(move.To, Path.GetDirectoryName(move.From)!);
        if (!r.Ok) return r;
        moves.Remove(move);
        ctx.SetState("hub_moves", JsonSerializer.Serialize(moves));
        WriteHubBoard(ctx);
        return new ActionResult(true, L.T($"{Path.GetFileNameWithoutExtension(move.From)} è tornato in \"da sistemare\".", $"{Path.GetFileNameWithoutExtension(move.From)} is back in \"to sort\"."));
    }

    /// <summary>A shortcut with the same name already in that section of the Hub.</summary>
    private Shortcut? Twin(Shortcut s, string hub, string section) =>
        _env.Shortcuts(Path.Combine(hub, section)).FirstOrDefault(x => Path.GetFileName(x.Path).Equals(Path.GetFileName(s.Path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Two shortcuts open the same thing (same program and arguments, whatever the case or the quotes).</summary>
    private static bool SameTarget(string a, string b) =>
        string.Equals(Environment.ExpandEnvironmentVariables(a).Replace("\"", "").Trim(), Environment.ExpandEnvironmentVariables(b).Replace("\"", "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"file|section" of a shortcut waiting to be sorted, checked: only from the "to sort" folder, only a section of its Hub.</summary>
    private (Shortcut Shortcut, string Hub, string Section)? Waiting(IModuleContext ctx, string? arg)
    {
        var cut = (arg ?? "").LastIndexOf('|');
        if (cut <= 0) return null;
        var (path, section) = (arg![..cut], arg[(cut + 1)..]);
        var p = Proposals(ctx).FirstOrDefault(x => x.Proposal.Shortcut.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        return p.Proposal is not null && p.Sections.Contains(section, StringComparer.OrdinalIgnoreCase) ? (p.Proposal.Shortcut, p.Hub, section) : null;
    }

    /// <summary>To the Recycle Bin, only when the section really has one that opens the same program.</summary>
    private ActionResult DoDropDuplicate(IModuleContext ctx, string? arg)
    {
        if (Waiting(ctx, arg) is not var (s, hub, section)) return new ActionResult(false, L.T("Non è un collegamento da sistemare.", "Not a shortcut to sort."));
        if (Twin(s, hub, section) is not { } twin || !SameTarget(twin.Target, s.Target))
            return new ActionResult(false, L.T("Nella sezione non c'è un collegamento uguale: niente da eliminare.", "The section has no identical shortcut: nothing to remove."));
        var r = _env.Recycle([s.Path]);
        WriteHubBoard(ctx);
        return r.Ok ? new ActionResult(true, L.T($"Doppione di {s.Name} nel Cestino: resta quello in {section}.", $"Duplicate of {s.Name} in the Recycle Bin: the one in {section} stays."), L.T("Si riprende dal Cestino.", "It can be restored from the Recycle Bin.")) : r;
    }

    /// <summary>Two different things with the same name: this one goes in with " (2)" after its name.</summary>
    private ActionResult DoSortRename(IModuleContext ctx, string? arg) =>
        Waiting(ctx, arg) is var (s, hub, section) ? KeepBoth(ctx, s, hub, section) : new ActionResult(false, L.T("Non è un collegamento da sistemare.", "Not a shortcut to sort."));

    /// <summary>"file|section" with a namesake in the section: one of the three ways out.</summary>
    private ActionResult DoTwin(IModuleContext ctx, string? arg, string choice)
    {
        if (Waiting(ctx, arg) is not var (s, hub, section)) return new ActionResult(false, L.T("Non è un collegamento da sistemare.", "Not a shortcut to sort."));
        return ApplyTwin(ctx, s, hub, section, choice);
    }

    private ActionResult ApplyTwin(IModuleContext ctx, Shortcut s, string hub, string section, string choice)
    {
        if (choice == TwinChoice.Both) return KeepBoth(ctx, s, hub, section);
        if (Twin(s, hub, section) is not { } twin)
            return new ActionResult(false, L.T($"In {section} non c'è più un collegamento \"{s.Name}\": usa SPOSTA.", $"{section} no longer has a \"{s.Name}\" shortcut: use MOVE."));
        ActionResult result;
        if (choice == TwinChoice.KeepHub)
        {
            result = _env.Recycle([s.Path]);
            if (result.Ok) result = new ActionResult(true, L.T($"{s.Name}: resta quello in {section}, il nuovo è nel Cestino.", $"{s.Name}: the one in {section} stays, the new one is in the Recycle Bin."),
                L.T("Si riprende dal Cestino.", "It can be restored from the Recycle Bin."));
        }
        else
        {
            result = _env.Recycle([twin.Path]);
            if (result.Ok)
            {
                var folder = Path.Combine(hub, section);
                result = _env.MoveFile(s.Path, folder);
                if (result.Ok)
                {
                    RememberMove(ctx, s.Path, Path.Combine(folder, Path.GetFileName(s.Path)));
                    result = new ActionResult(true, L.T($"{s.Name}: in {section} ora c'è il nuovo; quello di prima è nel Cestino.", $"{s.Name}: {section} now has the new one; the old one is in the Recycle Bin."),
                        L.T("Quello di prima si riprende dal Cestino.", "The old one can be restored from the Recycle Bin."));
                }
            }
        }
        WriteHubBoard(ctx);
        return result;
    }

    private static void RememberMove(IModuleContext ctx, string from, string to)
    {
        var done = Load<List<HubMove>>(ctx, "hub_moves") ?? [];
        done.Add(new HubMove(Guid.NewGuid().ToString("N")[..8], from, to, ctx.Now));
        ctx.SetState("hub_moves", JsonSerializer.Serialize(done.TakeLast(100).ToList()));
    }

    private static List<TwinRule> TwinRules(IModuleContext ctx) => Load<List<TwinRule>>(ctx, "twin_rules") ?? [];

    private static TwinRule? RuleFor(IModuleContext ctx, Shortcut s, string section) =>
        TwinRules(ctx).FirstOrDefault(r => r.Name.Equals(Path.GetFileName(s.Path), StringComparison.OrdinalIgnoreCase) && r.Section.Equals(section, StringComparison.OrdinalIgnoreCase));

    /// <summary>The shortcuts waiting with a namesake in their section, each with its three ways out and "remember".</summary>
    private IReadOnlyList<ActionChoice> TwinChoices(IModuleContext ctx)
    {
        var list = new List<ActionChoice>();
        foreach (var (p, hub, _) in Proposals(ctx))
        {
            if (p.Section is not { } section || Twin(p.Shortcut, hub, section) is not { } twin) continue;
            var key = $"{p.Shortcut.Path}|{section}";
            var category = $"{p.Shortcut.Name} → {section}";
            var opens = L.T($"Quello della Hub apre {twin.Target} · il nuovo apre {p.Shortcut.Target}", $"The one in the Hub opens {twin.Target} · the new one opens {p.Shortcut.Target}");
            // Nothing deleted unless the person ticks it: "keep both" is the one ticked.
            list.Add(new ActionChoice($"{key}|{TwinChoice.Both}", L.T("Tieni tutti e due", "Keep both"), L.T($"Il nuovo entra come \"{p.Shortcut.Name} (2)\". ", $"The new one goes in as \"{p.Shortcut.Name} (2)\". ") + opens, true, category, twin.Path));
            list.Add(new ActionChoice($"{key}|{TwinChoice.KeepHub}", L.T("Tieni quello della Hub", "Keep the one in the Hub"), L.T("Il nuovo va nel Cestino. ", "The new one goes to the Recycle Bin. ") + opens, false, category, twin.Path));
            list.Add(new ActionChoice($"{key}|{TwinChoice.UseNew}", L.T("Sostituisci con il nuovo", "Replace with the new one"), L.T("Quello della Hub va nel Cestino. ", "The one in the Hub goes to the Recycle Bin. ") + opens, false, category, p.Shortcut.Path));
            list.Add(new ActionChoice($"{key}|remember", L.T("Ricorda la scelta per questo collegamento", "Remember the choice for this shortcut"),
                L.T("La prossima volta che compare di nuovo con questo nome, OptLife fa la stessa cosa da solo.", "The next time it comes back with this name, OptLife does the same by itself."), false, category));
        }
        return list;
    }

    private ActionResult DoTwinDecide(IModuleContext ctx, string? arg)
    {
        var ticked = (arg ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.LastIndexOf('|') is var c and > 0 ? (Key: a[..c], What: a[(c + 1)..]) : default)
            .Where(a => a.Key is not null).ToList();
        var rules = TwinRules(ctx);
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var group in ticked.GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            // The first ticked way out counts, in the order they are listed.
            var choice = new[] { TwinChoice.Both, TwinChoice.KeepHub, TwinChoice.UseNew }.FirstOrDefault(c => group.Any(g => g.What == c));
            if (choice is null || Waiting(ctx, group.Key) is not var (s, hub, section)) continue;
            var r = ApplyTwin(ctx, s, hub, section, choice);
            (r.Ok ? done : failed).Add(r.Ok ? r.Detail : $"{s.Name}: {r.Detail}");
            if (r.Ok && group.Any(g => g.What == "remember"))
            {
                rules.RemoveAll(x => x.Name.Equals(Path.GetFileName(s.Path), StringComparison.OrdinalIgnoreCase) && x.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
                rules.Add(new TwinRule(Path.GetFileName(s.Path), section, choice));
                done.Add(L.T($"Ricordato per {s.Name}: {TwinChoice.Describe(choice)}.", $"Remembered for {s.Name}: {TwinChoice.Describe(choice)}."));
            }
        }
        ctx.SetState("twin_rules", JsonSerializer.Serialize(rules));
        WriteHubBoard(ctx);
        if (done.Count == 0 && failed.Count == 0) return new ActionResult(true, L.T("Niente da fare: nessuna scelta spuntata.", "Nothing to do: no choice ticked."));
        return new ActionResult(failed.Count == 0, string.Join(' ', done.Concat(failed)), L.T("Ciò che va nel Cestino si riprende da lì; le scelte ricordate si tolgono dalla pagina Collegamenti.", "What goes to the Recycle Bin can be restored from there; remembered choices are removed from the Shortcuts page."));
    }

    /// <summary>"name|section" of a remembered choice.</summary>
    private ActionResult DoTwinForget(IModuleContext ctx, string? arg)
    {
        var cut = (arg ?? "").LastIndexOf('|');
        if (cut <= 0) return new ActionResult(false, L.T("Manca la scelta da dimenticare.", "The choice to forget is missing."));
        var (name, section) = (arg![..cut], arg[(cut + 1)..]);
        var rules = TwinRules(ctx);
        if (rules.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) == 0)
            return new ActionResult(false, L.T("Scelta non trovata.", "Choice not found."));
        ctx.SetState("twin_rules", JsonSerializer.Serialize(rules));
        WriteHubBoard(ctx);
        return new ActionResult(true, L.T($"Dimenticata la scelta per {Path.GetFileNameWithoutExtension(name)}: la prossima volta OptLife chiede.", $"Forgot the choice for {Path.GetFileNameWithoutExtension(name)}: next time OptLife asks."));
    }

    /// <summary>The choices the person asked OptLife to remember, applied to the shortcuts that came back; each one is an activity.</summary>
    private void ApplyRemembered(IModuleContext ctx)
    {
        foreach (var (p, hub, _) in Proposals(ctx))
        {
            if (p.Section is not { } section || Twin(p.Shortcut, hub, section) is null || RuleFor(ctx, p.Shortcut, section) is not { } rule) continue;
            var activity = ctx.BeginActivity(L.T($"Collegamento {p.Shortcut.Name}: scelta ricordata", $"Shortcut {p.Shortcut.Name}: remembered choice"), byPerson: false);
            var r = ApplyTwin(ctx, p.Shortcut, hub, section, rule.Choice);
            activity.End(r.Ok, r.Detail);
            ctx.RecordEvents([new NewEvent(ctx.Now, "hub_twin_remembered", r.Ok ? Severity.Info : Severity.Low,
                L.T($"{p.Shortcut.Name} è ricomparso: {TwinChoice.Describe(rule.Choice)}", $"{p.Shortcut.Name} came back: {TwinChoice.Describe(rule.Choice)}"), "hub", Data: new Dictionary<string, string> { ["detail"] = r.Detail })]);
        }
    }

    private ActionResult KeepBoth(IModuleContext ctx, Shortcut s, string hub, string section)
    {
        var folder = Path.Combine(hub, section);
        var ext = Path.GetExtension(s.Path);
        var name = Enumerable.Range(2, 50).Select(n => $"{Path.GetFileNameWithoutExtension(s.Path)} ({n}){ext}")
            .First(n => !_env.Shortcuts(folder).Any(x => Path.GetFileName(x.Path).Equals(n, StringComparison.OrdinalIgnoreCase)));
        var r = _env.MoveFile(s.Path, folder, name);
        if (!r.Ok) return r;
        var done = Load<List<HubMove>>(ctx, "hub_moves") ?? [];
        done.Add(new HubMove(Guid.NewGuid().ToString("N")[..8], s.Path, Path.Combine(folder, name), ctx.Now));
        ctx.SetState("hub_moves", JsonSerializer.Serialize(done.TakeLast(100).ToList()));
        WriteHubBoard(ctx);
        return new ActionResult(true, L.T($"{s.Name} spostato in {section} come \"{Path.GetFileNameWithoutExtension(name)}\".", $"{s.Name} moved to {section} as \"{Path.GetFileNameWithoutExtension(name)}\"."),
            L.T("ANNULLA nella pagina Collegamenti.", "UNDO on the Shortcuts page."));
    }

    /// <summary>The Shortcuts page: what waits to be sorted with its suggestion, then the latest moves with UNDO.</summary>
    private void WriteHubBoard(IModuleContext ctx)
    {
        var board = new List<CheckItem>();
        var waiting = L.T("Da sistemare", "To sort");
        foreach (var (p, hub, _) in Proposals(ctx))
        {
            List<CheckButton> buttons = [];
            var text = p.Section is { } sec ? (p.Sure ? L.T($"Va in {sec}", $"Belongs in {sec}") : L.T($"Forse in {sec} (più sezioni possibili)", $"Maybe in {sec} (several sections possible)"))
                                            : L.T("Non so dove va: scegli tu con SCEGLI DOVE", "I do not know where it goes: choose with PICK WHERE");
            if (p.Section is { } s && Twin(p.Shortcut, hub, s) is { } twin)
            {
                // The section already has one with this name (a program reinstalled, an update that put its icon back).
                var same = SameTarget(twin.Target, p.Shortcut.Target);
                var arg = $"{p.Shortcut.Path}|{s}";
                if (same)
                {
                    text = L.T($"In {s} c'è già un collegamento uguale (apre lo stesso programma): questo è un doppione.", $"{s} already has the same shortcut (it opens the same program): this one is a duplicate.");
                    buttons.Add(new CheckButton(DropDuplicate, arg, L.T("ELIMINA IL DOPPIONE", "REMOVE THE DUPLICATE")));
                }
                else
                {
                    // Two different things with one name (an update recreating its own icon next to the person's): three ways out,
                    // and "choose and remember" so the next update is handled by itself.
                    text = L.T($"In {s} c'è già un collegamento con questo nome, ma apre un'altra cosa. Quello della Hub apre: {twin.Target}. Il nuovo apre: {p.Shortcut.Target}.",
                        $"{s} already has a shortcut with this name, but it opens something else. The one in the Hub opens: {twin.Target}. The new one opens: {p.Shortcut.Target}.");
                    buttons.Add(new CheckButton(TwinKeepHub, arg, L.T("TIENI QUELLO DELLA HUB", "KEEP THE HUB ONE")));
                    buttons.Add(new CheckButton(TwinUseNew, arg, L.T("USA IL NUOVO", "USE THE NEW ONE")));
                    buttons.Add(new CheckButton(SortRename, arg, L.T("TIENI TUTTI E DUE", "KEEP BOTH")));
                    buttons.Add(new CheckButton(TwinDecide, arg, L.T("SCEGLI E RICORDA", "CHOOSE AND REMEMBER")));
                }
            }
            else if (p.Section is { } target)
                buttons.Add(new CheckButton(SortOne, $"{p.Shortcut.Path}|{target}", L.T($"SPOSTA IN {target.ToUpperInvariant()}", $"MOVE TO {target.ToUpperInvariant()}")));
            board.Add(new CheckItem(waiting, p.Shortcut.Name, text + "\n" + p.Shortcut.Target,
                p.Section is null ? CheckStatus.Watch : CheckStatus.Info, StatusText: "", Buttons: buttons));
        }
        var fromDesktop = L.T("Spostati da una cartella da tenere in ordine", "Moved from a folder to keep tidy");
        foreach (var m in (Load<List<LooseMove>>(ctx, LooseMovesKey) ?? []).OrderByDescending(m => m.At).Take(10))
            board.Add(new CheckItem(fromDesktop, L.T($"{m.Items.Count} da {Environment.ExpandEnvironmentVariables(m.Folder)}", $"{m.Items.Count} from {Environment.ExpandEnvironmentVariables(m.Folder)}"),
                $"{string.Join(", ", m.Items.Take(12).Select(i => Path.GetFileName(i.From)))}{(m.Items.Count > 12 ? "…" : "")}\n→ {Path.GetDirectoryName(m.Items[0].To)} · {m.At.ToLocalTime().ToString("d MMM HH:mm", L.Culture)}",
                CheckStatus.Info, StatusText: "", Buttons: [new CheckButton(LooseUndo, m.Id, L.T("ANNULLA", "UNDO"))]));
        var moved = L.T("Spostati di recente", "Recently moved");
        foreach (var m in (Load<List<HubMove>>(ctx, "hub_moves") ?? []).OrderByDescending(m => m.At).Take(20))
            board.Add(new CheckItem(moved, Path.GetFileNameWithoutExtension(m.To), $"→ {Path.GetFileName(Path.GetDirectoryName(m.To))}\n{m.At.ToLocalTime().ToString("d MMM HH:mm", L.Culture)}",
                CheckStatus.Info, StatusText: "", Buttons: [new CheckButton(SortUndo, m.Id, L.T("ANNULLA", "UNDO"))]));
        var remembered = L.T("Scelte ricordate", "Remembered choices");
        foreach (var r in TwinRules(ctx))
            board.Add(new CheckItem(remembered, Path.GetFileNameWithoutExtension(r.Name), $"{L.T("In", "In")} {r.Section}: {TwinChoice.Describe(r.Choice)}",
                CheckStatus.Info, StatusText: "", Buttons: [new CheckButton(TwinForget, $"{r.Name}|{r.Section}", L.T("DIMENTICA", "FORGET"))]));
        // Nothing has ever waited here: no page to write (the history does not need "0 to sort" lines).
        if (board.Count == 0 && ctx.GetState("hub_board") is null) return;
        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("hub_board")) return;
        ctx.SetState("hub_board", signature);
        ctx.RecordEvents([new NewEvent(ctx.Now, HubKind, Severity.Info, L.T($"Collegamenti: {board.Count(i => i.Group == waiting)} da sistemare", $"Shortcuts: {board.Count(i => i.Group == waiting)} to sort"), HubKind, Data: data)]);
    }

    /// <summary>
    /// "Clean the Desktop": what is loose goes to "to sort" (as the tidy rules say), then the shortcuts there are looked at at once:
    /// suggested, or already moved when sorting is automatic and the section is certain.
    /// </summary>
    private ActionResult DoCleanDesktop(IModuleContext ctx)
    {
        var rules = ctx.Settings.GetList("tidy").Select(TidyRule.Parse).OfType<TidyRule>().Where(r => r.MoveTo is not null).ToList();
        if (rules.Count == 0)
            return new ActionResult(false, L.T("Manca una regola \"Cartelle che devono contenere solo certe cose\" con una cartella dove spostare il resto (Impostazioni → Pulizia e ordine, o Organizzazione → APPLICA).",
                "A \"Folders that must hold only certain things\" rule with a folder to move the rest to is missing (Settings → Cleanup and order, or Organisation → APPLY)."));
        var moved = 0;
        var failed = new List<string>();
        foreach (var rule in rules)
        {
            ctx.Progress(L.T($"Sposto ciò che è sciolto in {rule.Folder}", $"Moving what is loose in {rule.Folder}"));
            if (_env.List(rule.Folder) is not { } found) continue;
            var loose = CleanupRules.Loose(rule, found);
            if (loose.Count == 0) continue;
            var r = MoveAndRemember(ctx, rule.Folder, loose, rule.MoveTo!);
            if (r.Ok) moved += loose.Count;
            else failed.Add(r.Detail);
        }
        ctx.Progress(L.T("Guardo dove va ogni collegamento", "Looking at where each shortcut goes"));
        var before = (Load<List<HubMove>>(ctx, "hub_moves") ?? []).Count;
        CheckHub(ctx);
        var sorted = Math.Max(0, (Load<List<HubMove>>(ctx, "hub_moves") ?? []).Count - before);
        var waiting = Proposals(ctx);
        var parts = new List<string>
        {
            L.T($"Spostati in \"da sistemare\": {moved}.", $"Moved into \"to sort\": {moved}."),
        };
        if (sorted > 0) parts.Add(L.T($"Messi già nella loro sezione: {sorted}.", $"Already put in their section: {sorted}."));
        if (waiting.Count > 0)
            parts.Add(L.T($"Da sistemare: {waiting.Count} ({waiting.Count(p => p.Proposal.Section is not null)} con una sezione proposta: pagina Collegamenti).",
                $"To sort: {waiting.Count} ({waiting.Count(p => p.Proposal.Section is not null)} with a suggested section: Shortcuts page)."));
        if (failed.Count > 0) parts.Add(L.T("Non riuscito: ", "Failed: ") + string.Join("; ", failed));
        return new ActionResult(failed.Count == 0, string.Join(' ', parts),
            L.T("Ciò che è stato spostato è nella cartella \"da sistemare\" della Hub; i collegamenti sistemati si annullano dalla pagina Collegamenti.",
                "What was moved is in the Hub's \"to sort\" folder; sorted shortcuts are undone from the Shortcuts page."));
    }

    /// <summary>Where shortcuts live: the Desktop folders of the tidy rules (only their top), and each Hub with all its sections.</summary>
    private IEnumerable<(string Folder, bool Recursive)> ShortcutFolders(IModuleContext ctx)
    {
        var rules = ctx.Settings.GetList("tidy").Select(TidyRule.Parse).OfType<TidyRule>().ToList();
        foreach (var r in rules) yield return (r.Folder, false);
        foreach (var (_, hub) in Inboxes(ctx)) yield return (hub, true);
    }

    private IReadOnlyList<string> Broken(IModuleContext ctx) =>
        [.. ShortcutFolders(ctx).SelectMany(f => _env.BrokenShortcuts(f.Folder, f.Recursive)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>To the Recycle Bin, looked up again now: only shortcuts that still open nothing.</summary>
    private ActionResult DoRecycleBroken(IModuleContext ctx, string? only)
    {
        var broken = Broken(ctx);
        var chosen = only is { Length: > 0 } ? broken.Where(b => only.Split(';').Contains(b, StringComparer.OrdinalIgnoreCase)).ToList() : [.. broken];
        if (chosen.Count == 0) return new ActionResult(true, L.T("Non c'è più nessun collegamento rotto.", "There is no broken shortcut any more."));
        ctx.Progress(L.T($"Metto nel Cestino {chosen.Count} collegamenti", $"Recycling {chosen.Count} shortcuts"));
        var r = _env.Recycle(chosen);
        if (!r.Ok) return r;
        WriteHubBoard(ctx);
        return new ActionResult(true, L.T($"Nel Cestino {chosen.Count} collegamenti che non aprivano più niente: ", $"{chosen.Count} shortcuts that opened nothing any more are in the Recycle Bin: ")
            + string.Join(", ", chosen.Select(Path.GetFileNameWithoutExtension)) + ".", L.T("Si riprendono dal Cestino.", "They can be restored from the Recycle Bin."));
    }

    private void CheckBroken(IModuleContext ctx)
    {
        var broken = Broken(ctx);
        if (broken.Count > 0 && ctx.Settings.GetChoice("brokenMode") == L.T("da solo", "automatically"))
        {
            // Automatic, yet shown in Activity like anything OptLife does by itself.
            var activity = ctx.BeginActivity(L.T("Collegamenti rotti nel Cestino", "Broken shortcuts to the Recycle Bin"), byPerson: false);
            var r = DoRecycleBroken(ctx, null);
            activity.End(r.Ok, r.Detail);
            broken = Broken(ctx);
        }
        ctx.SyncFindings("broken", broken.Count == 0 ? [] :
        [
            new NewFinding($"broken|{CleanupRules.Hash(broken)}", Severity.Low,
                L.T($"{broken.Count} collegamenti non aprono più niente", $"{broken.Count} shortcuts open nothing any more"),
                L.T("Sono di programmi disinstallati o di app tolte: ", "They belong to uninstalled programs or removed apps: ") + string.Join(", ", broken.Take(10).Select(Path.GetFileNameWithoutExtension)) + ".",
                L.T("Il pulsante li manda nel Cestino (si riprendono da lì). Per farlo sempre da solo: IMPOSTAZIONI → Pulizia e ordine → Collegamenti che non aprono più niente.",
                    "The button sends them to the Recycle Bin (they can be restored from there). To do it automatically: SETTINGS → Cleanup and order → Shortcuts that open nothing any more."),
                new FindingFix(RecycleBroken, L.T("Mettili nel Cestino", "Recycle them"))),
        ]);
    }

    private void CheckHub(IModuleContext ctx)
    {
        CheckBroken(ctx);
        var proposals = Proposals(ctx);
        // Automatic: only what is certain (one section possible); the rest waits for the person.
        if (ctx.Settings.GetChoice("hubSortMode") == L.T("da solo", "automatically"))
        {
            var sure = proposals.Where(p => p.Proposal.Sure).Select(p => (p.Proposal.Shortcut.Path, p.Hub, p.Proposal.Section!)).ToList();
            if (sure.Count > 0) DoSort(ctx, sure);
            proposals = Proposals(ctx);
        }
        // Shortcuts that came back with a namesake the person already decided about.
        if (TwinRules(ctx).Count > 0)
        {
            ApplyRemembered(ctx);
            proposals = Proposals(ctx);
        }
        var findings = new List<NewFinding>();
        foreach (var group in proposals.GroupBy(p => p.Hub))
        {
            var withPlace = group.Count(p => p.Proposal.Section is not null);
            findings.Add(new NewFinding($"hub|{group.Key.ToLowerInvariant()}", Severity.Low,
                L.T($"{group.Count()} collegamenti da sistemare nella cartella sul Desktop", $"{group.Count()} shortcuts to sort in the Desktop folder"),
                string.Join(", ", group.Take(8).Select(p => p.Proposal.Shortcut.Name + (p.Proposal.Section is { } s ? $" → {s}" : " → ?"))) + ".",
                L.T("Il pulsante sposta quelli con una sezione proposta; per gli altri, o per cambiare sezione, Ordine e spazio → Collegamenti → SCEGLI DOVE.",
                    "The button moves those with a suggested section; for the others, or to change section, Order and space → Shortcuts → PICK WHERE."),
                withPlace > 0 ? new FindingFix(SortHub, L.T("Sistemali", "Sort them")) : null));
        }
        ctx.SyncFindings("hub", findings);
        WriteHubBoard(ctx);
    }

    private static T? Load<T>(IModuleContext ctx, string key) where T : class
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

    private ActionResult DoMove(IModuleContext ctx, string? folder)
    {
        var rule = ctx.Settings.GetList("tidy").Select(TidyRule.Parse).OfType<TidyRule>()
            .FirstOrDefault(r => r.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (rule?.MoveTo is null) return new ActionResult(false, L.T("Non c'è una regola con una cartella di destinazione per questa cartella.", "There is no rule with a destination folder for this folder."));
        if (_env.List(rule.Folder) is not { } found) return new ActionResult(false, L.T($"La cartella {rule.Folder} non c'è.", $"The folder {rule.Folder} does not exist."));
        var loose = CleanupRules.Loose(rule, found);
        return loose.Count == 0 ? new ActionResult(true, L.T("Non c'è più niente fuori posto.", "Nothing is out of place any more.")) : MoveAndRemember(ctx, rule.Folder, loose, rule.MoveTo);
    }

    /// <summary>Moves the loose things and keeps where each went, so UNDO on the Shortcuts page puts them back.</summary>
    private ActionResult MoveAndRemember(IModuleContext ctx, string folder, IReadOnlyList<string> loose, string destination)
    {
        var (result, moved) = _env.MoveTracked(folder, loose, destination);
        if (moved.Count > 0)
        {
            var list = Load<List<LooseMove>>(ctx, LooseMovesKey) ?? [];
            list.Add(new LooseMove(Guid.NewGuid().ToString("N")[..8], folder, ctx.Now, moved));
            ctx.SetState(LooseMovesKey, JsonSerializer.Serialize(list.OrderByDescending(m => m.At).Take(20).ToList()));
            ctx.RecordEvents([new NewEvent(ctx.Now, "loose_moved", Severity.Info,
                L.T($"Spostati {moved.Count} elementi da {Environment.ExpandEnvironmentVariables(folder)}", $"Moved {moved.Count} items from {Environment.ExpandEnvironmentVariables(folder)}"),
                $"loose|{folder.ToLowerInvariant()}", Data: new Dictionary<string, string> { ["items"] = string.Join("\n", moved.Select(m => $"{m.From} → {m.To}")) })]);
            WriteHubBoard(ctx);
        }
        return result with { Undo = moved.Count > 0 ? L.T("ANNULLA in Ordine e spazio → Collegamenti.", "UNDO in Order and space → Shortcuts.") : result.Undo };
    }

    private const string LooseMovesKey = "loose_moves";

    private ActionResult DoLooseUndo(IModuleContext ctx, string? id)
    {
        var list = Load<List<LooseMove>>(ctx, LooseMovesKey) ?? [];
        if (list.FirstOrDefault(m => m.Id == id) is not { } move) return new ActionResult(false, L.T("Questo spostamento non c'è più.", "This move is no longer there."));
        var r = _env.MoveBack(move.Items);
        if (r.Ok) ctx.SetState(LooseMovesKey, JsonSerializer.Serialize(list.Where(m => m.Id != id).ToList()));
        WriteHubBoard(ctx);
        return r;
    }

    private ActionResult DoRecycle(IModuleContext ctx, string? ruleName)
    {
        var rule = ctx.Settings.GetList("rebuildable").Select(RebuildRule.Parse).OfType<RebuildRule>()
            .FirstOrDefault(r => r.Name.Equals(ruleName, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return new ActionResult(false, L.T($"Nessuna regola \"{ruleName}\".", $"No \"{ruleName}\" rule."));
        // Looked up again at this very moment: what goes to the bin is what is stale now, not what was when the problem was written.
        var stale = CleanupRules.Stale(rule, _env.Find(rule.Root, rule.Folders, rule.Marker), ctx.Now);
        return stale.Count == 0 ? new ActionResult(true, L.T("Non c'è più niente da togliere.", "Nothing left to remove.")) : _env.Recycle([.. stale.Select(i => i.Path)]);
    }

    private ActionResult DoRemoveUnwanted(IModuleContext ctx, string? id)
    {
        // Only what the person listed: this action may run by itself, so it must not be usable on anything else.
        var unwanted = ctx.Settings.GetList("unwantedStartup");
        var entry = _env.Startup().FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new ActionResult(true, L.T("Non è più tra i programmi all'avvio.", "It is no longer among the start-up programs."));
        return unwanted.Any(u => entry.Name.Contains(u, StringComparison.OrdinalIgnoreCase))
            ? _env.RemoveStartup(entry.Id)
            : new ActionResult(false, L.T($"\"{entry.Name}\" non è nell'elenco dei programmi da non volere all'avvio.", $"\"{entry.Name}\" is not in the list of programs not wanted at start-up."));
    }

    private Task CheckAsync(IModuleContext ctx, CancellationToken ct)
    {
        if (ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;
        var s = ctx.Settings;
        var now = ctx.Now;

        // ── Quick, every time ──────────────────────────────────────────────
        var tidy = new List<NewFinding>();
        foreach (var rule in s.GetList("tidy").Select(TidyRule.Parse).OfType<TidyRule>())
            if (_env.List(rule.Folder) is { } found && CleanupRules.Tidy(rule, found) is { } f) tidy.Add(f);
        ctx.SyncFindings("tidy", tidy);

        var crowded = new List<NewFinding>();
        foreach (var c in s.GetList("crowded").Select(CleanupRules.Split).Where(c => c.Length >= 2 && c[0].Length > 0))
            if (int.TryParse(c[1], out var most) && _env.List(c[0]) is { } inside && CleanupRules.Crowded(c[0], most, inside) is { } f) crowded.Add(f);
        ctx.SyncFindings("crowded", crowded);

        var newcomers = new List<NewFinding>();
        foreach (var w in s.GetList("watch").Select(CleanupRules.Split).Where(w => w[0].Length > 0))
        {
            if (_env.List(w[0]) is not { } found) continue;
            var key = $"seen:{w[0].ToLowerInvariant()}";
            if (Remembered(ctx, key) is { } reference)
                newcomers.AddRange(CleanupRules.Newcomers(w[0], w.Length > 1 ? w[1] : null, reference, found));
            else
                ctx.SetState(key, JsonSerializer.Serialize(found)); // First look: this is what "already there" means.
        }
        ctx.SyncFindings("new", newcomers);

        var entries = _env.Startup();
        var startupReference = Remembered(ctx, "startup");
        if (startupReference is null) ctx.SetState("startup", JsonSerializer.Serialize(entries.Select(e => e.Id)));
        ctx.SyncFindings("startup", [.. CleanupRules.Startup(entries,
            s.GetBool("watchStartup") ? startupReference : null, s.GetList("unwantedStartup"))]);

        RecordProgramChanges(ctx, now);
        CheckHub(ctx);

        // ── Heavier, twice a day, when the rules were edited, or when asked ──
        var rules = string.Join('\n', new[] { "shortcuts", "heavy", "rebuildable" }.SelectMany(k => s.GetList(k))) + $"\n{s.GetInt("minGb")}";
        if (Due(ctx, "heavy_at", HeavyEvery, now) || ctx.GetState("heavy_for") != rules)
        {
            var links = new List<NewFinding>();
            foreach (var folder in s.GetList("shortcuts").Where(f => f.Length > 0))
                if (CleanupRules.BrokenLinks(folder, _env.BrokenLinks(folder)) is { } f) links.Add(f);
            ctx.SyncFindings("links", links);

            var heavy = new List<NewFinding>();
            foreach (var h in s.GetList("heavy").Select(CleanupRules.Split).Where(h => h.Length >= 3))
            {
                ct.ThrowIfCancellationRequested();
                if (double.TryParse(h[1].Replace(',', '.'), NumberStyles.Float, Invariant, out var gb) && int.TryParse(h[2], out var days)
                    && CleanupRules.Heavy(h[0], gb, days, _env.Weigh(h[0]), now) is { } f)
                    heavy.Add(f);
            }
            ctx.SyncFindings("heavy", heavy);

            var rebuild = new List<NewFinding>();
            foreach (var rule in s.GetList("rebuildable").Select(RebuildRule.Parse).OfType<RebuildRule>())
            {
                ct.ThrowIfCancellationRequested();
                if (CleanupRules.Rebuildable(rule, _env.Find(rule.Root, rule.Folders, rule.Marker), s.GetInt("minGb"), now) is { } f) rebuild.Add(f);
            }
            ctx.SyncFindings("rebuild", rebuild);

            ctx.SetState("heavy_at", now.ToString("o", Invariant));
            ctx.SetState("heavy_for", rules);
        }

        // ── Once a day: what weighs what, and what grew ──
        var roots = s.GetList("map").Where(f => f.Length > 0).ToList();
        if (roots.Count > 0 && (Due(ctx, "map_at", MapEvery, now) || ctx.GetState("map_for") != string.Join('\n', roots)))
        {
            var growth = new List<NewFinding>();
            var top = new List<DiskItem>();
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                // Only what weighs something is remembered: the rest would be thousands of numbers nobody reads.
                foreach (var item in _env.Weigh(root).Where(i => i.Gb >= 1).OrderByDescending(i => i.Bytes).Take(40))
                {
                    top.Add(item);
                    var metric = $"space.{item.Path.ToLowerInvariant()}";
                    var before = ctx.History.Samples(metric, now.AddDays(-GrowthDays), now.AddDays(-6)).FirstOrDefault();
                    ctx.RecordSample(metric, item.Gb);
                    if (CleanupRules.Growth(item, before?.Value, before is null ? GrowthDays : (int)(now - before.Time).TotalDays, s.GetInt("growthGb")) is { } f)
                        growth.Add(f);
                }
            }
            ctx.SyncFindings("growth", growth);
            ctx.SetState("map_at", now.ToString("o", Invariant));
            ctx.SetState("map_for", string.Join('\n', roots));
            if (top.Count > 0)
                ctx.RecordEvents([new NewEvent(now, "space_map", Severity.Info,
                    L.T("Spazio occupato: ", "Space used: ") + string.Join(", ", top.OrderByDescending(i => i.Bytes).Take(3).Select(i => $"{i.Name} {CleanupRules.Size(i.Gb)}")),
                    "space_map",
                    Data: top.OrderByDescending(i => i.Bytes).Take(25).ToDictionary(i => i.Path, i => CleanupRules.Size(i.Gb)))]);
        }
        return Task.CompletedTask;
    }

    /// <summary>Programs installed or removed since the last look: one line each in the history, never a problem.</summary>
    private void RecordProgramChanges(IModuleContext ctx, DateTimeOffset now)
    {
        var programs = _env.InstalledPrograms();
        if (programs.Count == 0) return;
        if (Remembered(ctx, "programs") is { } before)
        {
            var events = programs.Except(before, StringComparer.OrdinalIgnoreCase)
                .Select(p => new NewEvent(now, "program_installed", Severity.Info, L.T("Programma installato: ", "Program installed: ") + p, "programs"))
                .Concat(before.Except(programs, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new NewEvent(now, "program_removed", Severity.Info, L.T("Programma disinstallato: ", "Program uninstalled: ") + p, "programs")))
                .ToList();
            if (events.Count == 0) return;
            ctx.RecordEvents(events);
        }
        ctx.SetState("programs", JsonSerializer.Serialize(programs));
    }

    private static bool Due(IModuleContext ctx, string key, TimeSpan every, DateTimeOffset now) =>
        ctx.OnDemand || ctx.GetState(key) is not { } at || now - DateTimeOffset.Parse(at, Invariant) >= every;

    private static IReadOnlyList<string>? Remembered(IModuleContext ctx, string key)
    {
        try
        {
            return ctx.GetState(key) is { } json ? JsonSerializer.Deserialize<List<string>>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
