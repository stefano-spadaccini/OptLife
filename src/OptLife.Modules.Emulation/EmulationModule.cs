using System.Globalization;
using System.Text.RegularExpressions;
using OptLife.Core;
using OptLife.Modules.Emulation.Windows;

namespace OptLife.Modules.Emulation;

/// <summary>The latest release of an emulator on GitHub: its tag and the download of the file for Windows.</summary>
public sealed record Release(string Tag, string? Download);

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface IEmulationEnvironment
{
    /// <summary>The first file under the folder matching the name (with *), a few levels deep; null when none.</summary>
    string? FindFile(string folder, string pattern);

    /// <summary>True when at least one file matches a path whose last part may hold *.</summary>
    bool Exists(string pathPattern);

    /// <summary>Names of the installed programs (uninstall lists).</summary>
    IReadOnlyList<string> InstalledNames();

    string? Version(string exe);
    Task<Release?> Latest(string repo, string assetPattern, CancellationToken ct);

    /// <summary>Downloads an archive (.zip or .7z) and unpacks it into the folder, replacing the program files and keeping the rest.</summary>
    Task<ActionResult> DownloadInto(string url, string folder, CancellationToken ct);

    Task<ActionResult> Winget(string id, CancellationToken ct);
    ActionResult Open(string target);
    void CreateFolder(string path);
}

/// <summary>
/// Emulation: every console worth emulating, one closable section each, with its emulators (installed or not,
/// version, update), and the console files they need (BIOS, firmware, keys) with where they go and how to get
/// them legally. Installs from the official releases or opens the official site; never downloads console files
/// or games. Folders come from the settings, so a friend's PC can follow its own layout.
/// </summary>
public sealed class EmulationModule : IModule
{
    public const string Install = "install", InstallOne = "install-one", OpenSite = "open-site", OpenGuide = "open-guide",
        OpenOfficial = "open-official", OpenFolder = "open-folder", BoardKind = "emulation_board";
    private static readonly TimeSpan ReleaseCheck = TimeSpan.FromHours(12);
    private readonly IEmulationEnvironment _env;

    public EmulationModule() : this(new WindowsEmulationEnvironment()) { }

    internal EmulationModule(IEmulationEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("emulation", L.T("Emulazione", "Emulation"),
        L.T("Gli emulatori di tutte le console: quali hai, che versione, se c'è un aggiornamento, e i file della console che servono (BIOS, firmware, chiavi) con dove vanno e come averli in modo legale.",
            "Emulators for every console: which you have, which version, whether there is an update, and the console files they need (BIOS, firmware, keys) with where they go and how to get them legally."),
        FindingArea.Software);

    private static string Auto => L.T("da solo", "automatically");
    private static string Notify => L.T("avvisa", "notify");

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Text("emulators", L.T("Cartella degli emulatori", "Emulators folder"),
            L.T("Dove stanno (e vengono installati) gli emulatori: uno per cartella, divisi per famiglia (PlayStation, Nintendo…).",
                "Where emulators are (and get installed): one per folder, by family (PlayStation, Nintendo…)."), @"C:\Software\Consoles"),
        SettingDefinition.Text("games", L.T("Cartella dei giochi", "Games folder"),
            L.T("Una sottocartella per console (PS1, PS2, Switch…) per i giochi, e BIOS\\<console> per i file di sistema.",
                "A subfolder per console (PS1, PS2, Switch…) for games, and BIOS\\<console> for system files."), @"C:\Giochi\Emulazione"),
        SettingDefinition.List("consoles", L.T("Console da mostrare", "Consoles to show"),
            L.T("Gli id delle console che ti interessano; le altre non compaiono.", "The ids of the consoles you care about; the others do not show."),
            [.. EmuCatalog.All.Select(c => c.Id)]),
        SettingDefinition.Choice("updates", L.T("Aggiornamenti degli emulatori", "Emulator updates"),
            L.T("Avvisa: ti dice che c'è una versione nuova e la installi col pulsante. Da solo: OptLife la installa quando non stai giocando (solo quelli scaricati dalle release ufficiali).",
                "Notify: tells you there is a new version and you install it with the button. Automatically: OptLife installs it when you are not playing (only those from the official releases)."),
            Notify, Auto),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo degli emulatori", "Checking the emulators"), TimeSpan.FromHours(1), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(Install, L.T("Installa gli emulatori scelti", "Install the chosen emulators"),
            L.T("Mostra gli emulatori che mancano o hanno un aggiornamento, divisi per famiglia, e installa quelli che spunti (dalle release ufficiali o con winget).",
                "Shows the emulators missing or with an update, by family, and installs the ones you tick (from the official releases or with winget)."),
            Permission.Ask, DoInstall,
            Choices: ctx => [.. Shown(ctx).SelectMany(c => c.Emulators.Select(e => (c, e)))
                .Where(p => p.e.Source.GitHub is not null || p.e.Source.Winget is not null)
                .Select(p => (p.c, p.e, State: StateOf(ctx, p.e)))
                .Where(p => p.State.Exe is null || p.State.Update is not null)
                .Select(p => new ActionChoice(p.e.Id, $"{p.e.Name} · {p.c.Name}",
                    p.State.Exe is null ? L.T("non installato", "not installed") : L.T($"aggiornamento a {p.State.Update}", $"update to {p.State.Update}"),
                    p.State.Exe is not null, p.c.Family))]),
        new ModuleAction(InstallOne, L.T("Installa o aggiorna un emulatore", "Install or update one emulator"),
            L.T("Scarica l'ultima versione ufficiale dell'emulatore della riga.", "Downloads the latest official version of the emulator of the line."),
            Permission.Ask, DoInstall),
        new ModuleAction(OpenSite, L.T("Apri il sito ufficiale", "Open the official site"), L.T("Il sito dell'emulatore.", "The emulator's site."),
            Permission.Auto, (_, id, _) => Task.FromResult(EmuCatalog.Find(id ?? "") is { } p ? _env.Open(p.Emulator.Source.Site) : Unknown(id))),
        new ModuleAction(OpenGuide, L.T("Apri la guida", "Open the guide"), L.T("Come estrarre il file dalla tua console, o cosa vuole l'emulatore.", "How to dump the file from your console, or what the emulator wants."),
            Permission.Auto, (_, id, _) => Task.FromResult(FileById(id) is { } f ? _env.Open(f.File.Guide) : Unknown(id))),
        new ModuleAction(OpenOfficial, L.T("Scarica dal produttore", "Download from the maker"), L.T("La pagina di download ufficiale del produttore della console.", "The console maker's official download page."),
            Permission.Auto, (_, id, _) => Task.FromResult(FileById(id) is { File.Official: { } url } ? _env.Open(url) : Unknown(id))),
        new ModuleAction(OpenFolder, L.T("Apri la cartella", "Open the folder"), L.T("Crea la cartella se manca e la apre.", "Creates the folder if missing and opens it."),
            Permission.Auto, (ctx, where, _) => Task.FromResult(DoOpenFolder(ctx, where))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("emulation", L.T("Emulazione", "Emulation"), BoardKind, [], Days: 365, Menu: ViewMenu.Games, Order: 1,
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("Ogni console ha la sua sezione: apri quelle che ti interessano. Verde: installato e a posto. Ambra: c'è un aggiornamento. Rosso: manca un file della console che serve. " +
                             "OptLife scarica gli emulatori solo dai siti ufficiali; BIOS, firmware e chiavi vanno estratti dalla tua console (la GUIDA spiega come), tranne i firmware che Sony pubblica.",
                             "Every console has its own section: open the ones you care about. Green: installed and fine. Amber: there is an update. Red: a console file it needs is missing. " +
                             "OptLife downloads emulators only from the official sites; BIOS, firmware and keys must be dumped from your console (the GUIDE explains how), except the firmware Sony publishes."),
            Layout: ViewLayout.Checklist, GroupsStartClosed: true,
            Buttons: _ =>
            [
                new ViewButton(L.T("Più emulatori insieme", "Several at once"), L.T("Installa gli emulatori scelti", "Install the chosen emulators"),
                    L.T("Ti mostra quelli che mancano o hanno un aggiornamento, divisi per famiglia, e installa quelli che spunti.",
                        "Shows the ones missing or with an update, by family, and installs the ones you tick."), Install,
                    ButtonText: L.T("SCEGLI E INSTALLA", "PICK AND INSTALL")),
            ]),
    ];

    // ── Reading the machine ──────────────────────────────────────────────────

    internal sealed record EmuState(string? Exe, string? Version, string? Update);

    private static IEnumerable<GameConsole> Shown(IModuleContext ctx)
    {
        var wanted = ctx.Settings.GetList("consoles").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return EmuCatalog.All.Where(c => wanted.Count == 0 || wanted.Contains(c.Id));
    }

    private static string Root(IModuleContext ctx, string key) => Environment.ExpandEnvironmentVariables(ctx.Settings.GetText(key)).TrimEnd('\\');

    private static string FamilyFolder(IModuleContext ctx, GameConsole c) => Path.Combine(Root(ctx, "emulators"), c.Family);
    private static string BiosFolder(IModuleContext ctx, GameConsole c) => Path.Combine(Root(ctx, "games"), "BIOS", c.Folder);
    private static string GamesFolder(IModuleContext ctx, GameConsole c) => Path.Combine(Root(ctx, "games"), c.Folder);

    private string? ExeOf(IModuleContext ctx, Emulator e) => _env.FindFile(Root(ctx, "emulators"), e.Exe);

    private EmuState StateOf(IModuleContext ctx, Emulator e)
    {
        var exe = ExeOf(ctx, e);
        // Run through its own launcher: installed, and the launcher keeps it up to date.
        if (exe is null && e.Launcher is not null && _env.FindFile(Root(ctx, "emulators"), e.Launcher) is { } launcher)
            return new EmuState(launcher, L.T("con il suo launcher", "through its launcher"), null);
        var installedElsewhere = exe is null && e.Installed is { } name && _env.InstalledNames().Any(n => n.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (exe is null && !installedElsewhere) return new EmuState(null, null, null);
        var version = ctx.GetState($"tag:{e.Id}") ?? (exe is null ? null : _env.Version(exe));
        var latest = ctx.GetState($"latest:{e.Id}");
        return new EmuState(exe ?? "", version, IsNewer(latest, version, ctx.GetState($"tag:{e.Id}")) ? latest : null);
    }

    /// <summary>
    /// A newer release: different from the tag OptLife installed, or (installed by hand) a higher version number.
    /// Builds named by date or commit ("continuous", "build-3bfac60") count only when OptLife knows what it installed.
    /// </summary>
    internal static bool IsNewer(string? latest, string? installed, string? installedTag)
    {
        if (latest is null) return false;
        if (installedTag is not null) return installedTag != latest;
        return Numbers(latest) is { } l && Numbers(installed) is { } i && l > i;
    }

    private static Version? Numbers(string? text)
    {
        var m = Regex.Match(text ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0) : null;
    }

    private bool HasFile(IModuleContext ctx, GameConsole c, SystemFile f, string? exe)
    {
        var emuFolder = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        foreach (var place in f.Places)
        {
            if (place.Contains("{emu}") && emuFolder is null) continue;
            var path = place.Replace("{bios}", BiosFolder(ctx, c)).Replace("{emu}", emuFolder ?? "")
                .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                .Replace("{documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            if (_env.Exists(path)) return true;
        }
        return false;
    }

    // ── The page ─────────────────────────────────────────────────────────────

    private async Task Check(IModuleContext ctx, CancellationToken ct)
    {
        var playing = ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));
        if (playing && !ctx.OnDemand) return;
        await RefreshReleases(ctx, ct);

        if (!playing && ctx.Settings.GetChoice("updates") == Auto)
            foreach (var c in Shown(ctx))
                foreach (var e in c.Emulators.Where(e => e.Source.GitHub is not null))
                    if (StateOf(ctx, e) is { Exe.Length: > 0, Update: not null })
                        await InstallEmulator(ctx, c, e, ct);

        var board = new List<CheckItem>();
        var installed = 0;
        foreach (var c in Shown(ctx))
        {
            var anyInstalled = false;
            foreach (var e in c.Emulators)
            {
                var s = StateOf(ctx, e);
                anyInstalled |= s.Exe is not null;
                if (s.Exe is not null) installed++;
                List<CheckButton> buttons = [];
                var canDownload = e.Source.GitHub is not null || e.Source.Winget is not null;
                if (s.Exe is null && canDownload) buttons.Add(new CheckButton(InstallOne, e.Id, L.T("INSTALLA", "INSTALL")));
                if (s.Update is not null && canDownload) buttons.Add(new CheckButton(InstallOne, e.Id, L.T("AGGIORNA", "UPDATE")));
                if (s.Exe is { Length: > 0 }) buttons.Add(new CheckButton(OpenFolder, $"emu:{e.Id}", L.T("CARTELLA", "FOLDER")));
                buttons.Add(new CheckButton(OpenSite, e.Id, L.T("SITO", "SITE")));
                var value = s.Exe is null ? L.T("Non installato", "Not installed")
                    : s.Update is not null ? L.T($"Aggiornamento disponibile: {s.Update}", $"Update available: {s.Update}")
                    : L.T("Installato", "Installed") + (s.Version is { Length: > 0 } v ? $" · {v}" : "");
                board.Add(new CheckItem(c.Name, e.Name, value,
                    s.Exe is null ? CheckStatus.Info : s.Update is not null ? CheckStatus.Watch : CheckStatus.Ok,
                    Advice: e.Note, Kind: c.Family, Buttons: buttons));
            }
            foreach (var f in c.Files)
            {
                var exe = c.Emulators.Select(e => ExeOf(ctx, e)).FirstOrDefault(x => x is not null);
                var present = HasFile(ctx, c, f, exe);
                List<CheckButton> buttons = [new CheckButton(OpenGuide, f.Id, L.T("GUIDA", "GUIDE"))];
                if (f.Official is not null) buttons.Insert(0, new CheckButton(OpenOfficial, f.Id, L.T("SCARICA DA SONY", "GET FROM SONY")));
                buttons.Add(new CheckButton(OpenFolder, $"bios:{c.Id}", L.T("CARTELLA", "FOLDER")));
                board.Add(new CheckItem(c.Name, f.Name,
                    present ? L.T("Presente", "Present") : f.Required ? L.T("Manca", "Missing") : L.T("Manca (facoltativo)", "Missing (optional)"),
                    present ? CheckStatus.Ok : f.Required && anyInstalled ? CheckStatus.Wrong : CheckStatus.Info,
                    Advice: f.What + " " + L.T($"Cartella: {BiosFolder(ctx, c)}", $"Folder: {BiosFolder(ctx, c)}"),
                    Kind: c.Family, Buttons: buttons));
            }
        }

        // For other modules (the game library), as data in the history: where each emulator is and each console's games folder.
        var found = new Dictionary<string, string>();
        foreach (var c in EmuCatalog.All)
        {
            found[$"games.{c.Id}"] = GamesFolder(ctx, c);
            foreach (var e in c.Emulators)
                if (ExeOf(ctx, e) is { Length: > 0 } path) found[$"exe.{c.Id}.{e.Id}"] = path;
        }
        var foundSignature = string.Join('\n', found.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        if (foundSignature != ctx.GetState("found"))
        {
            ctx.SetState("found", foundSignature);
            ctx.RecordEvents([new NewEvent(ctx.Now, "emulators_found", Severity.Info,
                L.T($"Emulatori trovati: {found.Count(p => p.Key.StartsWith("exe.", StringComparison.Ordinal))}", $"Emulators found: {found.Count(p => p.Key.StartsWith("exe.", StringComparison.Ordinal))}"),
                "emulators_found", Data: found)]);
        }

        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("board")) return;
        ctx.SetState("board", signature);
        var updates = board.Count(i => i.Status == CheckStatus.Watch);
        ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
            L.T($"Emulazione: {installed} emulatori installati, {updates} aggiornamenti", $"Emulation: {installed} emulators installed, {updates} updates"),
            BoardKind, Data: data)]);
    }

    /// <summary>The latest official release of each GitHub emulator, read at most every 12 hours (or when asked).</summary>
    private async Task RefreshReleases(IModuleContext ctx, CancellationToken ct)
    {
        foreach (var e in Shown(ctx).SelectMany(c => c.Emulators).Where(e => e.Source.GitHub is not null))
        {
            var when = ctx.GetState($"checked:{e.Id}") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue;
            // GitHub answers 60 questions an hour without an account: even when asked, not more than every quarter of an hour.
            if (ctx.Now - when < (ctx.OnDemand ? TimeSpan.FromMinutes(15) : ReleaseCheck)) continue;
            if (await _env.Latest(e.Source.GitHub!, e.Source.Asset!, ct) is not { } release) continue;
            ctx.SetState($"latest:{e.Id}", release.Tag);
            ctx.SetState($"download:{e.Id}", release.Download);
            ctx.SetState($"checked:{e.Id}", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private async Task<ActionResult> DoInstall(IModuleContext ctx, string? argument, CancellationToken ct)
    {
        var ids = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return ActionResult.Nothing(L.T("Nessun emulatore scelto.", "No emulator chosen."));
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (EmuCatalog.Find(id) is not var (c, e) || c is null) { failed.Add(id); continue; }
            ctx.Progress(L.T($"Installo {e.Name}", $"Installing {e.Name}"), Array.IndexOf(ids, id) + 1, ids.Length);
            var result = await InstallEmulator(ctx, c, e, ct);
            if (result.Ok) done.Add(e.Name);
            else failed.Add($"{e.Name} ({result.Detail})");
        }
        await Check(new Forced(ctx), ct);
        var parts = new List<string>();
        if (done.Count > 0) parts.Add(L.T("Fatto: ", "Done: ") + string.Join(", ", done) + ".");
        if (failed.Count > 0) parts.Add(L.T("Non riusciti: ", "Failed: ") + string.Join("; ", failed) + ".");
        return new ActionResult(failed.Count == 0, string.Join(' ', parts));
    }

    private async Task<ActionResult> InstallEmulator(IModuleContext ctx, GameConsole c, Emulator e, CancellationToken ct)
    {
        // Its folders first: the emulator is of little use without a place for games and console files.
        _env.CreateFolder(GamesFolder(ctx, c));
        _env.CreateFolder(BiosFolder(ctx, c));
        if (e.Source.GitHub is not null)
        {
            if (ctx.GetState($"download:{e.Id}") is not { Length: > 0 } url || ctx.GetState($"latest:{e.Id}") is not { } tag)
            {
                if (await _env.Latest(e.Source.GitHub, e.Source.Asset!, ct) is not { Download: { } fresh } release)
                    return new ActionResult(false, L.T("non trovo la release ufficiale", "cannot find the official release"));
                (url, tag) = (fresh, release.Tag);
            }
            // Updated where it already is, so its settings and saves stay with it; new ones go under their family.
            // Never over a launcher's folder: the launcher has its own way of updating.
            if (ExeOf(ctx, e) is null && e.Launcher is not null && _env.FindFile(Root(ctx, "emulators"), e.Launcher) is not null)
                return new ActionResult(false, L.T("lo aggiorna il suo launcher", "its launcher updates it"));
            var exe = ExeOf(ctx, e);
            var folder = exe is not null ? Path.GetDirectoryName(exe)! : Path.Combine(FamilyFolder(ctx, c), e.Name);
            var result = await _env.DownloadInto(url, folder, ct);
            if (result.Ok) ctx.SetState($"tag:{e.Id}", tag);
            return result;
        }
        if (e.Source.Winget is not null) return await _env.Winget(e.Source.Winget, ct);
        return _env.Open(e.Source.Site);
    }

    private ActionResult DoOpenFolder(IModuleContext ctx, string? where)
    {
        var (kind, id) = (where ?? "").Split(':', 2) is [var k, var i] ? (k, i) : ("", "");
        string? folder = kind switch
        {
            "emu" when EmuCatalog.Find(id) is var (_, e) && e is not null && ExeOf(ctx, e) is { } exe => Path.GetDirectoryName(exe),
            "bios" when EmuCatalog.All.FirstOrDefault(c => c.Id == id) is { } c => BiosFolder(ctx, c),
            "games" when EmuCatalog.All.FirstOrDefault(c => c.Id == id) is { } c => GamesFolder(ctx, c),
            _ => null,
        };
        if (folder is null) return Unknown(where);
        _env.CreateFolder(folder);
        return _env.Open(folder);
    }

    private static (GameConsole Console, SystemFile File)? FileById(string? id) =>
        EmuCatalog.All.SelectMany(c => c.Files.Select(f => (c, f))).FirstOrDefault(p => p.f.Id == id) is var (con, file) && con is not null ? (con, file) : null;

    private static ActionResult Unknown(string? id) => new(false, L.T($"\"{id}\" non è nel catalogo.", $"\"{id}\" is not in the catalogue."));

    /// <summary>The same context, as asked by the person: right after an installation the page is rewritten even during a game.</summary>
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
