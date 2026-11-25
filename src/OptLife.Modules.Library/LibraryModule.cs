using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptLife.Modules.Library.Windows;

namespace OptLife.Modules.Library;

/// <summary>What the module needs from the machine beyond plain files, behind an interface for the tests.</summary>
public interface ILibraryEnvironment
{
    /// <summary>Saves a public file (list or box art); false when it is not there or the network is down.</summary>
    Task<bool> Download(string url, string path, CancellationToken ct);

    Screen PrimaryScreen();

    /// <summary>The graphics card's memory in GB; 0 when it cannot be read.</summary>
    double VramGb();

    string Documents();

    /// <summary>The person's roaming AppData (Dolphin keeps its user folder there).</summary>
    string AppData() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // ── PC launchers (empty where the machine is not read: tests) ──

    /// <summary>A public page's text (a store search answer); null when it cannot be read.</summary>
    Task<string?> GetText(string url, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Windows' lists of installed programs, for the whole PC and for the person.</summary>
    IReadOnlyList<UninstallEntry> Uninstall() => [];

    /// <summary>Steam's own folder, from its registry key; null when Steam is not installed.</summary>
    string? SteamFolder() => null;

    /// <summary>Ubisoft Connect's installs: its game id and folder.</summary>
    IReadOnlyList<(string Id, string Folder)> UbisoftInstalls() => [];

    /// <summary>GOG Galaxy's games: id, name, folder, program; and where GOG Galaxy is.</summary>
    IReadOnlyList<(string Id, string Name, string Folder, string Exe)> GogGames() => [];

    string? GogClient() => null;

    /// <summary>Full names of the installed Store packages (for Xbox games).</summary>
    IReadOnlyList<string> PackageFullNames() => [];

    /// <summary>The roots of the fixed disks ("C:\", "E:\").</summary>
    IReadOnlyList<string> FixedDrives() => [];

    string ProgramData() => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    string Explorer() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Starts the emulator as the person (never as administrator), in its own folder.</summary>
    ActionResult Launch(string exe, string arguments, string folder);

    ActionResult Open(string folder);

    /// <summary>To the Recycle Bin, never destroyed.</summary>
    bool Recycle(string path);
}

/// <summary>One game found on disk, as kept between scans (so unchanged files are not read again).</summary>
/// <param name="Boot">What the emulator is given: the disc image, or the game's EBOOT.BIN.</param>
/// <param name="Serial">PS2: "SLES-82009"; PS3: the title id "BLES00246".</param>
/// <param name="Crc">PS2 only: the code PCSX2 adds to the per-game file's name.</param>
/// <param name="Icon">The game's own small picture when it has one (PS3 ICON0.PNG): shown when there is no box art.</param>
public sealed record GameEntry(string Id, string Console, string Path, string Boot, string Title, string? Serial, string? Crc, string? DatName,
    long Size, DateTime Modified, string? Cover = null, DateTimeOffset? CoverTried = null, string? Icon = null);

/// <summary>What OptLife wrote into an emulator's per-game file, to tell its own writing from the person's.</summary>
/// <param name="Hash">Of the whole file right after OptLife wrote it: a different file means the person changed it since.</param>
/// <param name="Backup">The copy of the file as it was before OptLife first touched it; null when OptLife created it.</param>
public sealed record Written(string Hash, string? Backup, bool Created);

/// <summary>
/// The game library: finds the games in the folders (PlayStation 2 and 3 in this first piece), names them from the public
/// Redump lists, fetches their box art from libretro-thumbnails, and starts them with PLAY in the right emulator, full screen.
/// Before starting, it writes in the emulator's own per-game file only the few values that depend on this PC (internal
/// resolution, sync, renderer), from the chosen preset; everything else in the file stays the person's. Never downloads
/// games, BIOS, firmware or keys.
/// </summary>
public sealed class LibraryModule : IModule
{
    public const string BoardKind = "library_board", Launched = "game_launched",
        Play = "play", SetPreset = "preset", OpenFolder = "open-folder", UndoSettings = "undo-settings", Hide = "hide", Unhide = "unhide", Rescan = "rescan";
    private const string Mine = "mine", General = "default";
    private static readonly TimeSpan DatAge = TimeSpan.FromDays(30), CoverRetry = TimeSpan.FromDays(30);
    private const int CoversPerRun = 40;

    private readonly ILibraryEnvironment _env;

    public LibraryModule() : this(new WindowsLibraryEnvironment()) { }

    internal LibraryModule(ILibraryEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("library", L.T("Libreria", "Library"),
        L.T("I giochi delle console emulate in una griglia di copertine: GIOCA li apre nell'emulatore giusto, a schermo intero e con le impostazioni adatte a questo PC.",
            "The emulated consoles' games in a grid of covers: PLAY opens them in the right emulator, full screen and with settings that suit this PC."),
        FindingArea.Hardware);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Choice("preset", L.T("Impostazioni dei giochi", "Game settings"),
            L.T("Bilanciato: alta e stabile, un passo sotto la massima. Prestazioni: immagine più leggera, il più fluido possibile. Qualità: alla risoluzione dello schermo o appena sopra. Vale per tutti i giochi; ognuno si può cambiare dalla sua copertina. La risoluzione interna non va mai inutilmente sopra lo schermo, e la sincronia segue i suoi Hz.",
                "Balanced: high and steady, one step below the highest. Performance: lighter picture, as smooth as possible. Quality: at the screen's resolution or just above. Applies to every game; each can be changed from its cover. The internal resolution never goes uselessly above the screen, and sync follows its Hz."),
            L.T("Bilanciato", "Balanced"), L.T("Prestazioni", "Performance"), L.T("Qualità", "Quality"))
            .Guided(L.T("Come vuoi i giochi emulati? Consigliato: bilanciato (alto e stabile).", "How do you want emulated games? Suggested: balanced (high and steady).")),
        SettingDefinition.Bool("fullscreen", L.T("A schermo intero", "Full screen"),
            L.T("GIOCA apre il gioco direttamente a schermo intero.", "PLAY opens the game straight in full screen."), true),
        SettingDefinition.Bool("writeSettings", L.T("Scrivi le impostazioni per gioco", "Write per-game settings"),
            L.T("Prima di avviare, OptLife scrive nel file per gioco dell'emulatore solo risoluzione interna, sincronia e motore grafico. Prima di toccare un file che c'era già ne tiene una copia (si annulla dalla copertina); un file che hai scritto o cambiato tu non lo tocca, finché non scegli un preset per quel gioco.",
                "Before starting, OptLife writes in the emulator's per-game file only internal resolution, sync and renderer. Before touching a file that was already there it keeps a copy (undone from the cover); a file you wrote or changed is left alone until you choose a preset for that game."), true),
        SettingDefinition.Bool("online", L.T("Nomi e copertine da internet", "Names and covers from the internet"),
            L.T("Scarica una volta al mese gli elenchi pubblici dei giochi (Redump, da libretro-database) e una volta per gioco la copertina (libretro-thumbnails). Senza, i giochi si chiamano come i loro file.",
                "Downloads the public game lists once a month (Redump, from libretro-database) and each game's cover once (libretro-thumbnails). Without it, games are named after their files."), true),
        SettingDefinition.Bool("pc", L.T("Anche i giochi del PC", "PC games too"),
            L.T("Mette nella libreria i giochi installati con Steam, Epic Games, Battle.net, Riot, Ubisoft Connect, GOG, EA app e Xbox, letti dai file dei launcher (nessun account). GIOCA li apre attraverso il loro launcher, come dal launcher stesso; nessuna impostazione viene scritta per i giochi del PC.",
                "Puts in the library the games installed with Steam, Epic Games, Battle.net, Riot, Ubisoft Connect, GOG, EA app and Xbox, read from the launchers' files (no account). PLAY opens them through their launcher, as from the launcher itself; no settings are written for PC games."), true),
        SettingDefinition.List("pcFolders", L.T("Cartelle di giochi senza launcher", "Folders of games without a launcher"),
            L.T("Cartelle dove ogni sottocartella è un gioco portatile: OptLife ne avvia il programma principale (non installatori né programmi di disinstallazione).",
                "Folders where each subfolder is a portable game: OptLife starts its main program (not installers or uninstallers).")).PickFolder(),
        SettingDefinition.Table("extraGames", [L.T("Nome", "Name"), L.T("Programma", "Program"), L.T("Argomenti", "Arguments")],
            L.T("Giochi aggiunti a mano", "Games added by hand"),
            L.T("Un gioco che OptLife non trova da solo: nome | il suo programma (.exe) | argomenti (facoltativi). Compare tra i giochi del PC.",
                "A game OptLife does not find by itself: name | its program (.exe) | arguments (optional). It shows among the PC games.")),
        SettingDefinition.Table("folders", [L.T("Console", "Console"), L.T("Cartella", "Folder")],
            L.T("Altre cartelle di giochi", "Other game folders"),
            L.T("Oltre a quelle della pagina Emulazione (una per console): console (ps1, ps2, ps3, psp, gc-wii) | cartella.",
                "Besides those of the Emulation page (one per console): console (ps1, ps2, ps3, psp, gc-wii) | folder.")).AsAdvanced(),
        SettingDefinition.Table("programs", [L.T("Console", "Console"), L.T("Emulatore", "Emulator")],
            L.T("Emulatori scelti a mano", "Emulators chosen by hand"),
            L.T("Di solito non serve: gli emulatori li trova la pagina Emulazione. Console (ps1, ps2, ps3, psp, gc-wii) | file dell'emulatore.",
                "Usually not needed: the Emulation page finds the emulators. Console (ps1, ps2, ps3, psp, gc-wii) | emulator file.")).AsAdvanced(),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("scan", L.T("Ricerca dei giochi", "Looking for games"), TimeSpan.FromMinutes(2), Scan),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(Play, L.T("Gioca", "Play"),
            L.T("Apre il gioco nel suo emulatore, come utente e non come amministratore, scrivendo prima le impostazioni adatte se servono.",
                "Opens the game in its emulator, as the person and not as administrator, writing the suitable settings first if needed."),
            Permission.Ask, (ctx, arg, ct) => Task.FromResult(DoPlay(ctx, arg))),
        new ModuleAction(SetPreset, L.T("Impostazioni di un gioco", "Settings of a game"),
            L.T("Sceglie per un gioco Prestazioni, Bilanciato, Qualità, quelle generali o le tue (OptLife non scrive più niente per quel gioco).",
                "Chooses for a game Performance, Balanced, Quality, the general ones or yours (OptLife writes nothing more for that game)."),
            Permission.Ask, (ctx, arg, ct) => Task.FromResult(DoPreset(ctx, arg))),
        new ModuleAction(UndoSettings, L.T("Annulla le impostazioni scritte", "Undo the written settings"),
            L.T("Rimette il file per gioco dell'emulatore com'era prima di OptLife (quello creato da OptLife va nel Cestino); il gioco passa alle tue impostazioni.",
                "Puts the emulator's per-game file back as it was before OptLife (one OptLife created goes to the Recycle Bin); the game moves to your settings."),
            Permission.Ask, (ctx, arg, ct) => Task.FromResult(DoUndo(ctx, arg))),
        new ModuleAction(OpenFolder, L.T("Apri la cartella del gioco", "Open the game's folder"),
            L.T("Apre in Esplora file la cartella dove sta il gioco.", "Opens the folder the game is in, in File Explorer."),
            Permission.Ask, (ctx, arg, ct) => Task.FromResult(
                FindPc(ctx, arg) is { } pc ? _env.Open(pc.Folder)
                : Find(ctx, arg) is { } g ? _env.Open(Directory.Exists(g.Path) ? g.Path : System.IO.Path.GetDirectoryName(g.Path) ?? g.Path)
                : NotFound())),
        new ModuleAction(Hide, L.T("Togli dalla libreria", "Remove from the library"),
            L.T("Toglie un gioco del PC dalla libreria (un programma che non è un gioco, per esempio). Non disinstalla niente; \"Rimetti i giochi tolti\" lo fa tornare.",
                "Takes a PC game off the library (a program that is not a game, for example). Uninstalls nothing; \"Bring back removed games\" brings it back."),
            Permission.Ask, (ctx, arg, ct) => Task.FromResult(DoHide(ctx, arg))),
        new ModuleAction(Rescan, L.T("Rileggi la libreria", "Read the library again"),
            L.T("Cerca di nuovo tutti i giochi adesso: launcher, cartelle, console, copertine.", "Looks for every game again now: launchers, folders, consoles, covers."),
            Permission.Ask, async (ctx, arg, ct) =>
            {
                await ScanCore(ctx, ct, force: true);
                return new ActionResult(true, L.T("Libreria riletta.", "Library read again."));
            }),
        new ModuleAction(Unhide, L.T("Rimetti i giochi tolti", "Bring back removed games"),
            L.T("Fa tornare nella libreria i giochi del PC tolti a mano.", "Brings back to the library the PC games removed by hand."),
            Permission.Ask, async (ctx, arg, ct) =>
            {
                Save(ctx, "hidden", new List<string>());
                await ScanCore(ctx, ct, force: true);
                return new ActionResult(true, L.T("I giochi tolti sono di nuovo nella libreria.", "Removed games are back in the library."));
            }),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("library", L.T("Libreria", "Library"), BoardKind, [], Days: 365, Menu: ViewMenu.Games, Order: 0,
            Layout: ViewLayout.Gallery,
            Guide: L.T("Tutti i giochi in un posto: quelli del PC (Steam, Epic, Battle.net e gli altri launcher) e quelli delle console emulate. GIOCA li apre; per le console, dal menu della copertina scegli Prestazioni, Bilanciato o Qualità.",
                "All games in one place: PC ones (Steam, Epic, Battle.net and the other launchers) and the emulated consoles' ones. PLAY opens them; for consoles, choose Performance, Balanced or Quality from the cover's menu."),
            EmptyText: L.T("Nessun gioco trovato. I giochi delle console vanno nella cartella di ogni console indicata nella pagina Emulazione (per esempio ...\\PS2); i giochi del PC si trovano da soli se c'è il loro launcher. Poi premi \"Controlla ora\".",
                "No games found. Console games go in each console's folder shown on the Emulation page (for example ...\\PS2); PC games are found by themselves when their launcher is there. Then press \"Check now\"."),
            Description: L.T("I giochi del PC dai loro launcher e quelli delle console emulate. Filtra per PC, console o launcher qui sotto. GIOCA avvia il gioco: quelli del PC attraverso il loro launcher, quelli delle console a schermo intero nel loro emulatore. Nel menu ⋯: apri la cartella, e per PS1, PS2, PS3, PSP, GameCube e Wii le impostazioni Prestazioni, Bilanciato o Qualità (OptLife scrive solo risoluzione interna e sincronia).",
                "PC games from their launchers and the emulated consoles' games. Filter by PC, console or launcher below. PLAY starts the game: PC ones through their launcher, console ones full screen in their emulator. In the ⋯ menu: open the folder, and for PS1, PS2, PS3, PSP, GameCube and Wii the Performance, Balanced or Quality settings (OptLife writes only internal resolution and sync)."),
            Buttons: _ =>
            [
                // No "read the library again": the page header's CHECK NOW does the same full look (asked by the person = forced).
                new ViewButton(L.T("Giochi che hai tolto", "Games you removed"), L.T("Rimetti nella libreria i giochi tolti a mano", "Put back the games removed by hand"),
                    L.T("Serve solo se hai usato ⋯ → Togli dalla libreria (per un programma che non è un gioco, per esempio): li fa tornare tutti. Non c'entra con i giochi disinstallati, che spariscono da soli.", "Only needed if you used ⋯ → Remove from the library (for a program that is not a game, say): brings them all back. It has nothing to do with uninstalled games, which go away by themselves."),
                    Unhide, ButtonText: L.T("RIMETTI", "BRING BACK")),
            ]),
    ];

    // ── Finding the games ────────────────────────────────────────────────────

    private static bool Playing(IModuleContext ctx) => ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));

    /// <summary>Where the Emulation module says each emulator and each console's games folder are (it may be missing).</summary>
    private static IReadOnlyDictionary<string, string> Found(IModuleContext ctx) =>
        ctx.History.Events(ctx.Now.AddDays(-365), ctx.Now.AddMinutes(1), "emulation", "emulators_found").MaxBy(e => e.Time)?.Data
        ?? new Dictionary<string, string>();

    private static IEnumerable<(string Console, string Value)> Rows(IModuleContext ctx, string key) =>
        ctx.Settings.GetList(key).Select(r => r.Split('|', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2 && p[1].Length > 0)
            .Select(p => (p[0].ToLowerInvariant(), Environment.ExpandEnvironmentVariables(p[1])));

    internal static IReadOnlyList<string> FoldersOf(IModuleContext ctx, LibConsole c, IReadOnlyDictionary<string, string> found)
    {
        var list = new List<string>();
        if (found.TryGetValue($"games.{c.Folder}", out var main) && main.Length > 0) list.Add(main);
        list.AddRange(Rows(ctx, "folders").Where(r => r.Console == c.Id || r.Console == c.Folder).Select(r => r.Value));
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static string? ExeOf(IModuleContext ctx, LibConsole c, IReadOnlyDictionary<string, string> found)
    {
        if (Rows(ctx, "programs").FirstOrDefault(r => r.Console == c.Id || r.Console == c.Folder) is { Value.Length: > 0 } byHand && File.Exists(byHand.Value)) return byHand.Value;
        return found.TryGetValue($"exe.{c.Folder}.{c.EmulatorId}", out var exe) && File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Every two minutes it only checks whether there is a reason to look again: asked by the person, a setting of the library changed
    /// (a folder added), or half an hour passed. The full look happens then.
    /// </summary>
    private Task Scan(IModuleContext ctx, CancellationToken ct)
    {
        var settings = string.Join("\n", new[] { "pc", "online" }.Select(k => ctx.Settings.GetBool(k).ToString())
            .Concat(new[] { "pcFolders", "folders", "programs" }.SelectMany(k => ctx.Settings.GetList(k))));
        var last = ctx.GetState("last-scan") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : DateTimeOffset.MinValue;
        if (!ctx.OnDemand && settings == ctx.GetState("settings") && ctx.Now - last < FullLook) return Task.CompletedTask;
        return ScanCore(ctx, ct, ctx.OnDemand, settings);
    }

    private static readonly TimeSpan FullLook = TimeSpan.FromMinutes(30);

    private PcFolders PcRoots(IModuleContext ctx) => new([.. ctx.Settings.GetList("pcFolders").Select(Environment.ExpandEnvironmentVariables)]);

    private sealed record PcFolders(IReadOnlyList<string> Roots);

    private async Task ScanCore(IModuleContext ctx, CancellationToken ct, bool force, string? settings = null)
    {
        if (Playing(ctx) && !force) return;
        ctx.SetState("last-scan", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        if (settings is not null) ctx.SetState("settings", settings);
        var found = Found(ctx);
        var old = Load(ctx).ToDictionary(g => g.Path, StringComparer.OrdinalIgnoreCase);
        var games = new List<GameEntry>();
        var pcRoots = PcRoots(ctx).Roots;

        foreach (var c in LibraryRules.Consoles.Where(c => c.Extensions.Length > 0))
            // Switch repacks sit among the PC games' folders: their packages are Switch games.
            foreach (var path in Discs(FoldersOf(ctx, c, found).Concat(c == LibraryRules.Switch ? pcRoots : Array.Empty<string>()).SelectMany(f => FilesIn(f, c.Extensions, 3))))
            {
                if (ct.IsCancellationRequested) return;
                games.Add(DiscEntry(c, path, old));
            }
        games.AddRange(Ps3Entries(ctx, found, old));
        games.AddRange(FolderEntries(ctx, found));

        // Two copies of the same game keep apart: the second id gets the file's mark.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < games.Count; i++)
            if (!ids.Add(games[i].Id))
            {
                games[i] = games[i] with { Id = $"{games[i].Id}~{Short(games[i].Path)}" };
                ids.Add(games[i].Id);
            }

        if (ctx.Settings.GetBool("online"))
        {
            games = await Name(ctx, games, ct);
            games = await Covers(ctx, games, ct);
        }
        Store(ctx, games);

        var pc = ctx.Settings.GetBool("pc")
            ? [.. PcScanner.Find(_env, pcRoots, Hidden(ctx)), .. PcRules.ByHand(ctx.Settings.GetList("extraGames"))]
            : new List<PcGame>();
        if (ctx.Settings.GetBool("online")) pc = await PcCovers(ctx, pc, ct);
        StorePc(ctx, pc);
        Board(ctx, games, found);
    }

    private static HashSet<string> Hidden(IModuleContext ctx) => new(Cached<List<string>>(ctx, "hidden") ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Box art for PC games that have none on the PC: the store's own public picture (Steam, Epic), fetched once into the module's
    /// folder; one that is not there is asked again after a month.
    /// </summary>
    private async Task<List<PcGame>> PcCovers(IModuleContext ctx, List<PcGame> games, CancellationToken ct)
    {
        var tried = Cached<Dictionary<string, DateTimeOffset>>(ctx, "pc-covers-3") ?? [];
        var budget = CoversPerRun;
        for (var i = 0; i < games.Count; i++)
        {
            var g = games[i];
            // Steam games: the wide art for the big panel, once, when Steam's own cache has none.
            if (g.Id.StartsWith("pc:steam:", StringComparison.Ordinal) && (g.Wide is null || !File.Exists(g.Wide)) && budget > 0)
            {
                var hero = Dir(ctx, "covers", "pc", LibraryRules.SafeName(g.Id.Replace(':', '_')) + "-hero.jpg");
                if (File.Exists(hero)) games[i] = g = g with { Wide = hero };
                else if (!(tried.TryGetValue("hero:" + g.Id, out var heroWhen) && ctx.Now - heroWhen < CoverRetry))
                {
                    budget--;
                    tried["hero:" + g.Id] = ctx.Now;
                    if (await _env.Download(PcRules.SteamHeroUrl(g.Id["pc:steam:".Length..]), hero, ct)) games[i] = g = g with { Wide = hero };
                }
            }
            if (g.Cover is { } local && File.Exists(local)) continue;
            var target = Dir(ctx, "covers", "pc", LibraryRules.SafeName(g.Id.Replace(':', '_')) + ".jpg");
            if (File.Exists(target)) { games[i] = g with { Cover = target }; continue; }
            if (budget <= 0 || ct.IsCancellationRequested) continue;
            if (tried.TryGetValue(g.Id, out var when) && ctx.Now - when < CoverRetry) continue;
            budget--;
            tried[g.Id] = ctx.Now;
            // No picture from its launcher: the same game in Steam's public store, found by its name (no account, no key).
            var url = g.CoverUrl;
            // Only for games not from Steam, and only when the name found is the same game (never "close enough").
            if (url is null && !g.Id.StartsWith("pc:steam:", StringComparison.Ordinal))
            {
                var appId = await _env.GetText(PcRules.SteamSearchUrl(g.Title), ct) is { } answer ? PcRules.SteamMatch(answer, g.Title) : null;
                if (appId is null && PcRules.ShorterQuery(g.Title) is { } shorter && await _env.GetText(PcRules.SteamSearchUrl(shorter), ct) is { } more)
                    appId = PcRules.SteamMatch(more, g.Title);
                if (appId is not null) url = PcRules.SteamCoverUrl(appId);
            }
            if (url is not null && await _env.Download(url, target, ct)) games[i] = g with { Cover = target };
            // Newer Steam games have no tall box art at the old address: their store page's wide picture instead.
            else if (url is not null && PcRules.SteamAppOf(url) is { } steamId
                     && await _env.GetText(PcRules.SteamDetailsUrl(steamId), ct) is { } details && PcRules.SteamHeader(details) is { } header
                     && await _env.Download(header, target, ct))
                games[i] = g with { Cover = target };
        }
        Save(ctx, "pc-covers-3", tried);
        return games;
    }

    private static IEnumerable<string> FilesIn(string folder, string[] extensions, int depth)
    {
        if (!Directory.Exists(folder)) yield break;
        var pending = new Queue<(string, int)>([(folder, 0)]);
        while (pending.Count > 0)
        {
            var (dir, level) = pending.Dequeue();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = level < depth ? Directory.GetDirectories(dir) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var f in files.Where(f => extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
                yield return f;
            foreach (var d in dirs) pending.Enqueue((d, level + 1));
        }
    }

    /// <summary>
    /// One file per disc: in a folder with a playlist (.m3u) only the playlist, in one with cue sheets the sheets and not the
    /// .bin tracks they name.
    /// </summary>
    private static IEnumerable<string> Discs(IEnumerable<string> files)
    {
        foreach (var dir in files.GroupBy(f => System.IO.Path.GetDirectoryName(f) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var list = dir.ToList();
            bool Has(string ext) => list.Any(f => System.IO.Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));
            foreach (var f in list)
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (Has(".m3u") && ext != ".m3u") continue;
                if (Has(".cue") && ext is ".bin" or ".img") continue;
                yield return f;
            }
        }
    }

    private static GameEntry DiscEntry(LibConsole c, string path, Dictionary<string, GameEntry> old)
    {
        var info = new FileInfo(path);
        if (old.TryGetValue(path, out var known) && known.Size == info.Length && known.Modified == info.LastWriteTimeUtc) return known;
        var title = LibraryRules.CleanTitle(System.IO.Path.GetFileNameWithoutExtension(path));
        string? serial = null, crc = null;
        // Only the disc's header is read; compressed images (.chd, .cso, .pbp, .gcz) keep the file's name and no per-game file.
        try
        {
            var image = ImageOf(path);
            if (image is not null)
            {
                using var s = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                switch (c.Kind)
                {
                    case EmuKind.Pcsx2:
                        if (DiscReader.ReadPs2(s) is { } ps2) (serial, crc) = (ps2.Serial, ps2.Crc);
                        break;
                    case EmuKind.DuckStation:
                        serial = DiscReader.ReadPs1Serial(s);
                        break;
                    case EmuKind.Ppsspp:
                        if (DiscReader.ReadPsp(s) is { } psp) (serial, title) = (psp.TitleId, psp.Title);
                        break;
                    case EmuKind.Dolphin:
                        if (DiscReader.ReadNintendo(s) is { } disc) (serial, title) = (disc.Id, disc.Title);
                        break;
                    case EmuKind.Ryujinx:
                        serial = DiscReader.SwitchTitleId(s);
                        break;
                    case EmuKind.Azahar:
                        serial = DiscReader.N3dsCode(s);
                        break;
                    case EmuKind.MelonDs:
                        serial = DiscReader.DsHeader(s)?.Code;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // In use or unreadable: listed by its name.
        }
        // Cartridge sets are named as in the public lists (No-Intro): the file name is the name to find the box art by.
        var listName = c.ThumbRepo is not null && c.DatPath is null ? System.IO.Path.GetFileNameWithoutExtension(path) : null;
        if (c.Kind == EmuKind.Ryujinx) title = title.Replace('_', ' ');
        return new GameEntry($"{c.Id}:{serial ?? Short(path)}", c.Id, path, path, title, serial, crc, listName, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>The file holding the disc's first sectors: the image itself, the first track of a cue sheet, the first disc of a playlist.</summary>
    private static string? ImageOf(string path, int depth = 0)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        switch (ext)
        {
            case ".iso" or ".bin" or ".img" or ".gcm" or ".rvz" or ".wia" or ".wbfs" or ".ciso" or ".nsp" or ".3ds" or ".cci" or ".cxi" or ".nds" or ".dsi":
                return path;
            case ".cue" when depth < 2:
                return DiscReader.CueFirstFile(File.ReadAllText(path)) is { } track && File.Exists(System.IO.Path.Combine(dir, track)) ? System.IO.Path.Combine(dir, track) : null;
            case ".m3u" when depth < 2:
                var first = File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                return first is null ? null : ImageOf(System.IO.Path.Combine(dir, first), depth + 1);
            default:
                return null;
        }
    }

    /// <summary>
    /// PS3 games: folders with PS3_GAME\PARAM.SFO in the games folders, the disc games RPCS3 remembers (games.yml), and the games
    /// installed in RPCS3's own disk (dev_hdd0\game, only those that are games: patches and data stay out).
    /// </summary>
    private static IEnumerable<GameEntry> Ps3Entries(IModuleContext ctx, IReadOnlyDictionary<string, string> found, Dictionary<string, GameEntry> old)
    {
        var folders = new List<string>();
        foreach (var root in FoldersOf(ctx, LibraryRules.Ps3, found).Where(Directory.Exists))
            folders.AddRange(DiscFolders(root, 3));
        var exe = ExeOf(ctx, LibraryRules.Ps3, found);
        var emuDir = exe is null ? null : System.IO.Path.GetDirectoryName(exe);
        if (emuDir is not null)
        {
            var yml = System.IO.Path.Combine(emuDir, "config", "games.yml");
            if (File.Exists(yml) && ReadText(yml) is { } text)
                folders.AddRange(LibraryRules.Rpcs3GamesYml(text).Select(p => p.Folder).Where(f => File.Exists(System.IO.Path.Combine(f, "PS3_GAME", "PARAM.SFO"))));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sfo = System.IO.Path.Combine(folder, "PS3_GAME", "PARAM.SFO");
            if (Ps3Entry(folder, sfo, System.IO.Path.Combine(folder, "PS3_GAME", "USRDIR", "EBOOT.BIN"), System.IO.Path.Combine(folder, "PS3_GAME", "ICON0.PNG"), old, null) is { } g && seen.Add(g.Serial!))
                yield return g;
        }
        if (emuDir is null) yield break;
        var hdd = System.IO.Path.Combine(emuDir, "dev_hdd0", "game");
        if (!Directory.Exists(hdd)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(hdd); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { yield break; }
        foreach (var dir in dirs.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Ps3Entry(dir, System.IO.Path.Combine(dir, "PARAM.SFO"), System.IO.Path.Combine(dir, "USRDIR", "EBOOT.BIN"), System.IO.Path.Combine(dir, "ICON0.PNG"), old, "HG") is { } g && seen.Add(g.Serial!))
                yield return g;
        }
    }

    /// <summary>Folders up to <paramref name="depth"/> levels below the roots that hold <paramref name="marker"/> (a relative file path).</summary>
    private static IEnumerable<string> FoldersWith(IEnumerable<string> roots, string marker, int depth)
    {
        foreach (var root in roots.Where(Directory.Exists))
        {
            var pending = new Queue<(string, int)>([(root, 0)]);
            while (pending.Count > 0)
            {
                var (dir, level) = pending.Dequeue();
                if (File.Exists(System.IO.Path.Combine(dir, marker))) { yield return dir; continue; }
                if (level >= depth) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                foreach (var s in subs.Order(StringComparer.OrdinalIgnoreCase)) pending.Enqueue((s, level + 1));
            }
        }
    }

    /// <summary>When a game arrived on the PC: its file or folder was created.</summary>
    private static DateTimeOffset? Created(string path)
    {
        try
        {
            if (File.Exists(path)) return new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero);
            if (Directory.Exists(path)) return new DateTimeOffset(Directory.GetCreationTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static FileInfo? FileOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Games kept as folders: PS4 (sce_sys\param.sfo, games only, not patches), Wii U (meta\meta.xml with the program in code\),
    /// PS Vita (installed in Vita3K's ux0\app, started by title id).
    /// </summary>
    private IEnumerable<GameEntry> FolderEntries(IModuleContext ctx, IReadOnlyDictionary<string, string> found)
    {
        foreach (var dir in FoldersWith(FoldersOf(ctx, LibraryRules.Ps4, found), System.IO.Path.Combine("sce_sys", "param.sfo"), 3))
        {
            var eboot = System.IO.Path.Combine(dir, "eboot.bin");
            var sfoPath = System.IO.Path.Combine(dir, "sce_sys", "param.sfo");
            if (!File.Exists(eboot) || FileOf(sfoPath) is not { } info) continue;
            SfoInfo? sfo;
            try { sfo = DiscReader.ReadSfo(File.ReadAllBytes(sfoPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (sfo is null || !sfo.Category.StartsWith("gd", StringComparison.OrdinalIgnoreCase)) continue;
            var icon = System.IO.Path.Combine(dir, "sce_sys", "icon0.png");
            yield return new GameEntry($"ps4:{sfo.TitleId}", "ps4", dir, eboot, sfo.Title, sfo.TitleId, null, null, info.Length, info.LastWriteTimeUtc,
                Icon: File.Exists(icon) ? icon : null);
        }

        foreach (var dir in FoldersWith(FoldersOf(ctx, LibraryRules.WiiU, found), System.IO.Path.Combine("meta", "meta.xml"), 3))
        {
            var meta = System.IO.Path.Combine(dir, "meta", "meta.xml");
            var rpx = FilesIn(System.IO.Path.Combine(dir, "code"), [".rpx"], 0).FirstOrDefault();
            if (rpx is null || FileOf(meta) is not { } info || ReadText(meta) is not { } xml || LibraryRules.WiiUMeta(xml) is not { } m) continue;
            yield return new GameEntry($"wiiu:{m.Id}", "wiiu", dir, rpx, m.Title, m.Id, null, null, info.Length, info.LastWriteTimeUtc);
        }

        // Vita3K keeps installed games beside it (portable) or in AppData.
        var vita = ExeOf(ctx, LibraryRules.Vita, found);
        var apps = new List<string>();
        if (vita is not null) apps.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(vita)!, "ux0", "app"));
        apps.Add(System.IO.Path.Combine(_env.AppData(), "Vita3K", "Vita3K", "ux0", "app"));
        foreach (var dir in FoldersWith(apps, System.IO.Path.Combine("sce_sys", "param.sfo"), 1))
        {
            var sfoPath = System.IO.Path.Combine(dir, "sce_sys", "param.sfo");
            if (FileOf(sfoPath) is not { } info) continue;
            SfoInfo? sfo;
            try { sfo = DiscReader.ReadSfo(File.ReadAllBytes(sfoPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (sfo is null) continue;
            var icon = System.IO.Path.Combine(dir, "sce_sys", "icon0.png");
            yield return new GameEntry($"vita:{sfo.TitleId}", "vita", dir, sfo.TitleId, sfo.Title, sfo.TitleId, null, null, info.Length, info.LastWriteTimeUtc,
                Icon: File.Exists(icon) ? icon : null);
        }
    }

    private static IEnumerable<string> DiscFolders(string root, int depth)
    {
        var pending = new Queue<(string, int)>([(root, 0)]);
        while (pending.Count > 0)
        {
            var (dir, level) = pending.Dequeue();
            if (File.Exists(System.IO.Path.Combine(dir, "PS3_GAME", "PARAM.SFO"))) { yield return dir; continue; }
            if (level >= depth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            foreach (var s in subs.Order(StringComparer.OrdinalIgnoreCase)) pending.Enqueue((s, level + 1));
        }
    }

    private static GameEntry? Ps3Entry(string folder, string sfoPath, string eboot, string icon, Dictionary<string, GameEntry> old, string? onlyCategory)
    {
        if (!File.Exists(sfoPath) || !File.Exists(eboot)) return null;
        var info = new FileInfo(sfoPath);
        if (old.TryGetValue(folder, out var known) && known.Size == info.Length && known.Modified == info.LastWriteTimeUtc) return known;
        SfoInfo? sfo;
        try { sfo = DiscReader.ReadSfo(File.ReadAllBytes(sfoPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (sfo is null || (onlyCategory is not null && sfo.Category != onlyCategory)) return null;
        return new GameEntry($"ps3:{sfo.TitleId}", "ps3", folder, eboot, sfo.Title.Replace('\n', ' ').Trim(), sfo.TitleId, null, null, info.Length, info.LastWriteTimeUtc,
            Icon: File.Exists(icon) ? icon : null);
    }

    // ── Names and covers ─────────────────────────────────────────────────────

    private string Dir(IModuleContext ctx, params string[] parts) => System.IO.Path.Combine([ctx.DataDirectory, .. parts]);

    /// <summary>The public list of a console, downloaded at most once a month and only when it has games.</summary>
    private async Task<Dictionary<string, List<string>>?> Dat(IModuleContext ctx, LibConsole c, CancellationToken ct)
    {
        var path = Dir(ctx, "db", $"{c.Id}.dat");
        var age = File.Exists(path) ? ctx.Now - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : TimeSpan.MaxValue;
        // A list that could not be fetched is asked again a day later, not at every look.
        var tried = ctx.GetState($"dat-tried.{c.Id}") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : DateTimeOffset.MinValue;
        if (age > DatAge && ctx.Now - tried > TimeSpan.FromDays(1))
        {
            ctx.SetState($"dat-tried.{c.Id}", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
            await _env.Download(LibraryRules.DatUrl(c), path, ct);
        }
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new StreamReader(path);
            return LibraryRules.ParseDat(reader);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<List<GameEntry>> Name(IModuleContext ctx, List<GameEntry> games, CancellationToken ct)
    {
        foreach (var c in LibraryRules.Consoles.Where(c => c.DatPath is not null))
        {
            if (!games.Any(g => g.Console == c.Id && g.Serial is not null && g.DatName is null)) continue;
            if (await Dat(ctx, c, ct) is not { } dat) continue;
            for (var i = 0; i < games.Count; i++)
            {
                var g = games[i];
                if (g.Console != c.Id || g.Serial is null || g.DatName is not null) continue;
                var serial = c.Kind is EmuKind.Rpcs3 or EmuKind.Ppsspp ? LibraryRules.DashedId(g.Serial) : g.Serial;
                if (LibraryRules.DatName(dat, serial, System.IO.Path.GetFileName(g.Path)) is not { } name) continue;
                // Titles come from the list; PS3 keep their own (already right in PARAM.SFO).
                games[i] = g with { DatName = name, Title = c.Kind == EmuKind.Rpcs3 ? g.Title : LibraryRules.CleanTitle(name) };
            }
        }
        return games;
    }

    private async Task<List<GameEntry>> Covers(IModuleContext ctx, List<GameEntry> games, CancellationToken ct)
    {
        var budget = CoversPerRun;
        for (var i = 0; i < games.Count && budget > 0; i++)
        {
            var g = games[i];
            if (g.Cover is { } have && File.Exists(have)) continue;
            if (g.CoverTried is { } tried && ctx.Now - tried < CoverRetry) continue;
            if (LibraryRules.ConsoleOf(g.Console) is not { } c) continue;
            // libretro-thumbnails by the list's name; GameCube and Wii from GameTDB by the disc's id.
            var tries = c.ThumbRepo is null
                ? c.Tdb is { } tdb && g.Serial is { } id ? LibraryRules.GameTdbUrls(id, tdb, c.TdbExt).Select(u => (Url: u, File: id)).ToList() : new List<(string Url, string File)>()
                : LibraryRules.CoverNames(g.DatName ?? g.Title).Select(n => (Url: LibraryRules.CoverUrl(c, n), File: n)).ToList();
            string? saved = null;
            foreach (var (url, file) in tries)
            {
                if (ct.IsCancellationRequested) return games;
                budget--;
                var target = Dir(ctx, "covers", c.Id, file + (c.ThumbRepo is null && c.Tdb is not null ? "." + c.TdbExt : ".png"));
                if (await _env.Download(url, target, ct)) { saved = target; break; }
            }
            games[i] = g with { Cover = saved, CoverTried = ctx.Now };
        }
        return games;
    }

    // ── The page ─────────────────────────────────────────────────────────────

    private static Dictionary<string, DateTimeOffset> LastPlayed(IModuleContext ctx)
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ctx.History.Events(ctx.Now.AddDays(-180), ctx.Now.AddMinutes(1), ctx.ModuleId, Launched))
            if (e.Data.TryGetValue("game", out var id) && (!map.TryGetValue(id, out var t) || e.Time > t)) map[id] = e.Time;
        return map;
    }

    private void Board(IModuleContext ctx, IReadOnlyList<GameEntry> games, IReadOnlyDictionary<string, string> found)
    {
        var screen = _env.PrimaryScreen();
        var level = LibraryRules.LevelOf(_env.VramGb());
        var general = LibraryRules.FromWord(ctx.Settings.GetChoice("preset"));
        var prefs = Prefs(ctx);
        var written = WrittenFiles(ctx);
        var played = LastPlayed(ctx);
        // Game sessions the Games module recorded (since it was installed): by the program's folder, or by name.
        var sessions = ctx.History.Events(ctx.Now.AddDays(-180), ctx.Now.AddMinutes(1), "games", "game_session")
            .Select(e => (e.Time, Path: e.Data.GetValueOrDefault("path") ?? "", Game: e.Data.GetValueOrDefault("game") ?? "", Data: e.Data)).ToList();
        // What OptLife measured while the game ran: time, average FPS, stutters of the last sessions.
        List<string> SessionFacts(PcGame g)
        {
            var folder = g.Folder.TrimEnd('\\') + "\\";
            var mine = sessions.Where(x => (g.Folder.Length > 3 && x.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) || PcRules.SameGame(x.Game, g.Title))
                .OrderByDescending(x => x.Time).ToList();
            var facts = new List<string>();
            if (mine.Count == 0) return facts;
            static double Num(IReadOnlyDictionary<string, string> d, string k) =>
                double.TryParse(d.GetValueOrDefault(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var seconds = mine.Sum(x => Num(x.Data, "seconds"));
            facts.Add(L.T($"Giocato con OptLife: {Hours(seconds)} in {mine.Count} sessioni", $"Played with OptLife: {Hours(seconds)} in {mine.Count} sessions"));
            var recent = mine.Take(5).ToList();
            var fps = recent.Select(x => Num(x.Data, "fps_avg")).Where(v => v > 0).ToList();
            if (fps.Count > 0) facts.Add(L.T($"FPS medi (ultime sessioni): {fps.Average():0}", $"Average FPS (last sessions): {fps.Average():0}"));
            var hitches = recent.Sum(x => Num(x.Data, "hitches"));
            facts.Add(L.T($"Scatti nelle ultime {recent.Count} sessioni: {hitches:0}", $"Stutters in the last {recent.Count} sessions: {hitches:0}"));
            return facts;
        }
        static string Hours(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.#} h" : $"{Math.Max(1, seconds / 60):0} min";
        DateTimeOffset? LastOf(PcGame g)
        {
            var times = new List<DateTimeOffset>();
            if (played.TryGetValue(g.Id, out var launched)) times.Add(launched);
            if (g.LastPlayed is { } steam) times.Add(steam);
            var folder = g.Folder.TrimEnd('\\') + "\\";
            times.AddRange(sessions.Where(x => (g.Folder.Length > 3 && x.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) || PcRules.SameGame(x.Game, g.Title)).Select(x => x.Time));
            return times.Count > 0 ? times.Max() : null;
        }
        var items = new List<CheckItem>();
        var launches = ctx.History.Events(ctx.Now.AddDays(-180), ctx.Now.AddMinutes(1), ctx.ModuleId, Launched)
            .GroupBy(e => e.Data.GetValueOrDefault("game") ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        CheckItem Card(GameEntry g, int discs, string group)
        {
            var c = LibraryRules.ConsoleOf(g.Console)!;
            var exe = ExeOf(ctx, c, found);
            var pref = prefs.GetValueOrDefault(g.Id);
            var file = SettingsFile(ctx, g, exe);
            var theirs = pref == Mine || (file is not null && File.Exists(file) && !OursUnchanged(file, written));
            var presets = LibraryRules.HasPresets(c.Kind);
            var preset = LibraryRules.FromKey(pref) ?? general;
            var setting = theirs
                ? L.T("impostazioni tue", "your settings")
                : LibraryRules.Word(preset) + (pref is null ? L.T(" (generale)", " (general)") : "");
            var parts = new List<string> { c.Short };
            if (g.Serial is not null) parts.Add(g.Serial);
            if (discs > 1) parts.Add(L.T($"{discs} dischi", $"{discs} discs"));
            if (presets) parts.Add(setting);
            if (LibraryRules.Ago(played.TryGetValue(g.Id, out var when) ? when : null, ctx.Now) is { } ago) parts.Add(L.T($"giocato {ago}", $"played {ago}"));

            var advice = new List<string>();
            if (exe is null)
                advice.Add(L.T($"{c.EmulatorName} non è installato: installalo dalla pagina Emulazione.", $"{c.EmulatorName} is not installed: install it from the Emulation page."));
            if (!presets)
                advice.Add(L.T($"OptLife non scrive impostazioni per {c.EmulatorName}: valgono quelle che hai scelto lì.", $"OptLife writes no settings for {c.EmulatorName}: the ones you chose there apply."));
            else if (theirs)
                advice.Add(pref == Mine
                    ? L.T("OptLife non scrive niente per questo gioco.", "OptLife writes nothing for this game.")
                    : L.T($"C'è già un file di impostazioni di {c.EmulatorName} per questo gioco, scritto da te: OptLife non lo tocca. Scegli un preset dal menu per usare quelli di OptLife (prima ne tiene una copia).",
                          $"There is already a {c.EmulatorName} settings file for this game, written by you: OptLife leaves it alone. Choose a preset from the menu to use OptLife's (it keeps a copy first)."));
            else if (file is null && exe is not null)
                advice.Add(L.T($"Immagine compressa o senza codice leggibile: le impostazioni per gioco non si possono scrivere, valgono quelle generali di {c.EmulatorName}.",
                               $"Compressed image or no readable code: per-game settings cannot be written, {c.EmulatorName}'s general ones apply."));
            else
                advice.Add($"{LibraryRules.Word(preset)}: {LibraryRules.Describe(c, screen, level, preset)}.");
            if (discs > 1)
                advice.Add(L.T($"Parte dal disco 1; gli altri si cambiano dal menu di {c.EmulatorName} quando il gioco lo chiede.",
                               $"Starts from disc 1; change to the others from {c.EmulatorName}'s menu when the game asks."));

            var buttons = new List<CheckButton> { new(Play, g.Id, L.T("GIOCA", "PLAY")) };
            if (presets)
                foreach (var p in new[] { Preset.Performance, Preset.Balanced, Preset.Quality })
                    buttons.Add(new CheckButton(SetPreset, $"{g.Id}|{LibraryRules.Key(p)}", LibraryRules.Word(p)));
            if (presets && pref is not null && pref != Mine) buttons.Add(new CheckButton(SetPreset, $"{g.Id}|{General}", L.T("Come gli altri giochi", "Like the other games")));
            if (presets && pref != Mine) buttons.Add(new CheckButton(SetPreset, $"{g.Id}|{Mine}", L.T("Lascia le mie impostazioni", "Keep my settings")));
            if (file is not null && written.ContainsKey(file)) buttons.Add(new CheckButton(UndoSettings, g.Id, L.T("Annulla le impostazioni scritte", "Undo the written settings")));
            buttons.Add(new CheckButton(OpenFolder, g.Id, L.T("Apri la cartella", "Open the folder")));

            var image = g.Cover is { } cover && File.Exists(cover) ? cover : g.Icon is { } icon && File.Exists(icon) ? icon : null;
            return new CheckItem(group, g.Title, string.Join(" · ", parts), exe is null ? CheckStatus.Watch : CheckStatus.Info,
                Advice: string.Join(' ', advice), Kind: ConsoleKind, Buttons: buttons, StatusText: "", Image: image,
                Used: played.TryGetValue(g.Id, out var used) ? used : null, Added: Created(g.Path),
                Details:
                [
                    L.T($"Emulatore: {c.EmulatorName}{(exe is null ? " (non installato)" : "")}", $"Emulator: {c.EmulatorName}{(exe is null ? " (not installed)" : "")}"),
                    .. presets ? [L.T($"Impostazioni: {setting}", $"Settings: {setting}")] : Array.Empty<string>(),
                    .. launches.GetValueOrDefault(g.Id) is > 0 and var n ? [L.T($"Avviato dalla libreria: {n} volte", $"Started from the library: {n} times")] : Array.Empty<string>(),
                ]);
        }

        CheckItem PcCard(PcGame g, string group)
        {
            var parts = new List<string> { PcRules.Show(g.Launcher) };
            var last = LastOf(g);
            if (LibraryRules.Ago(last, ctx.Now) is { } ago) parts.Add(L.T($"giocato {ago}", $"played {ago}"));
            var how = g.Launcher == PcRules.Folder
                ? L.T($"Si avvia il suo programma: {System.IO.Path.GetFileName(g.Exe)}.", $"Its program starts: {System.IO.Path.GetFileName(g.Exe)}.")
                : L.T($"Si apre attraverso {g.Launcher}, come dal launcher.", $"Opens through {g.Launcher}, as from the launcher.");
            var buttons = new List<CheckButton>
            {
                new(Play, g.Id, L.T("GIOCA", "PLAY")),
                new(OpenFolder, g.Id, L.T("Apri la cartella", "Open the folder")),
                new(Hide, g.Id, L.T("Togli dalla libreria", "Remove from the library")),
            };
            return new CheckItem(group, g.Title, string.Join(" · ", parts), CheckStatus.Info, Advice: how, Kind: PcKind, Buttons: buttons, StatusText: "",
                Image: g.Cover is { } cover && File.Exists(cover) ? cover : null, Used: last, Added: Created(g.Folder), Details: SessionFacts(g), Wide: g.Wide);
        }

        var visible = Visible(games);
        var pc = LoadPc(ctx);
        // One list of every game: the page orders it (by name, last played, last added); no group of its own for recent ones.
        foreach (var launcher in pc.Select(g => g.Launcher).Distinct().OrderBy(l => l == PcRules.Folder ? 1 : 0).ThenBy(l => l, StringComparer.OrdinalIgnoreCase))
            foreach (var g in pc.Where(g => g.Launcher == launcher).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase))
                items.Add(PcCard(g, PcRules.Show(launcher)));
        foreach (var c in LibraryRules.Consoles)
            foreach (var (g, discs) in visible.Where(v => v.Game.Console == c.Id).OrderBy(v => v.Game.Title, StringComparer.CurrentCultureIgnoreCase))
                items.Add(Card(g, discs, c.Name));

        var data = CheckItem.ToData(items);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("board")) return;
        ctx.SetState("board", signature);
        var total = visible.Count + pc.Count;
        ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
            L.T($"Libreria: {total} giochi", $"Library: {total} games"), BoardKind, Data: data)]);
    }

    // ── Per-game settings ────────────────────────────────────────────────────

    /// <summary>The emulator's own per-game file for this game; null when it has none (no CRC, compressed image).</summary>
    internal string? SettingsFile(IModuleContext ctx, GameEntry g, string? exe)
    {
        if (exe is null || g.Serial is null) return null;
        var dir = System.IO.Path.GetDirectoryName(exe)!;
        bool Beside(params string[] names) => names.Any(n => File.Exists(System.IO.Path.Combine(dir, n)) || Directory.Exists(System.IO.Path.Combine(dir, n)));
        switch (LibraryRules.ConsoleOf(g.Console)?.Kind)
        {
            case EmuKind.Pcsx2:
                if (g.Crc is null) return null;
                // Portable PCSX2 keeps everything beside the program; installed, in Documents\PCSX2.
                var pcsx2 = Beside("portable.txt", "portable.ini", "inis") ? dir : System.IO.Path.Combine(_env.Documents(), "PCSX2");
                return System.IO.Path.Combine(pcsx2, "gamesettings", $"{g.Serial}_{g.Crc}.ini");
            case EmuKind.Rpcs3:
                return System.IO.Path.Combine(dir, "config", "custom_configs", $"config_{g.Serial}.yml");
            case EmuKind.DuckStation:
                // Portable with portable.txt (or settings.ini) beside it; otherwise Documents\DuckStation.
                var duck = Beside("portable.txt", "settings.ini") ? dir : System.IO.Path.Combine(_env.Documents(), "DuckStation");
                return System.IO.Path.Combine(duck, "gamesettings", $"{g.Serial}.ini");
            case EmuKind.Ppsspp:
                // PPSSPP keeps its memory stick beside the program unless installed.txt says it was installed (then Documents\PPSSPP).
                var stick = Beside("installed.txt") ? System.IO.Path.Combine(_env.Documents(), "PPSSPP") : System.IO.Path.Combine(dir, "memstick");
                return System.IO.Path.Combine(stick, "PSP", "SYSTEM", $"{g.Serial}_ppsspp.ini");
            case EmuKind.Dolphin:
                // Portable: User beside the program; otherwise Documents\Dolphin Emulator when it exists (older setups), else AppData.
                var documents = System.IO.Path.Combine(_env.Documents(), "Dolphin Emulator");
                var user = Beside("portable.txt") ? System.IO.Path.Combine(dir, "User")
                    : Directory.Exists(documents) ? documents : System.IO.Path.Combine(_env.AppData(), "Dolphin Emulator");
                return System.IO.Path.Combine(user, "GameSettings", $"{g.Serial}.ini");
            default:
                return null;
        }
    }

    /// <summary>
    /// The games as the page shows them: the discs of one game (same title, "(Disc 1)", "(Disc 2)"…) are one card, started from
    /// the first disc; with the count beside it.
    /// </summary>
    internal static IReadOnlyList<(GameEntry Game, int Discs)> Visible(IEnumerable<GameEntry> games)
    {
        var list = new List<(GameEntry, int)>();
        foreach (var group in games.GroupBy(g => (g.Console, Key: LibraryRules.DiscNumber(System.IO.Path.GetFileName(g.Path)) > 0 ? g.Title.ToLowerInvariant() : g.Id)))
        {
            var first = group.OrderBy(g => LibraryRules.DiscNumber(System.IO.Path.GetFileName(g.Path))).First();
            list.Add((first, group.Count()));
        }
        return list;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool OursUnchanged(string file, IReadOnlyDictionary<string, Written> written) =>
        written.TryGetValue(file, out var w) && ReadText(file) is { } text && Hash(text) == w.Hash;

    /// <summary>
    /// Writes the preset's values into the game's file, only when needed. Without <paramref name="chosen"/> (before PLAY) a file the
    /// person wrote or changed is left alone; chosen (a preset picked for this game) it is copied first, then written.
    /// </summary>
    private (bool Ok, string Detail) Apply(IModuleContext ctx, GameEntry g, string exe, bool chosen)
    {
        var c = LibraryRules.ConsoleOf(g.Console)!;
        var prefs = Prefs(ctx);
        var pref = prefs.GetValueOrDefault(g.Id);
        if (pref == Mine) return (true, L.T("impostazioni tue", "your settings"));
        if (!chosen && !ctx.Settings.GetBool("writeSettings")) return (true, "");
        if (SettingsFile(ctx, g, exe) is not { } file) return (true, "");
        var written = WrittenFiles(ctx);
        var exists = File.Exists(file);
        var current = exists ? ReadText(file) : "";
        if (current is null) return (false, L.T("il file delle impostazioni non si legge", "the settings file cannot be read"));
        var ours = written.TryGetValue(file, out var record) && Hash(current) == record.Hash;
        if (exists && !ours && !chosen) return (true, L.T("impostazioni tue", "your settings"));

        var preset = LibraryRules.FromKey(pref) ?? LibraryRules.FromWord(ctx.Settings.GetChoice("preset"));
        var screen = _env.PrimaryScreen();
        var level = LibraryRules.LevelOf(_env.VramGb());
        var yaml = c.Kind == EmuKind.Rpcs3;
        var values = LibraryRules.Values(c.Kind, screen, level, preset);
        if (exists && ours && LibraryRules.Holds(current, values, yaml)) return (true, LibraryRules.Word(preset));

        string? backup = record?.Backup;
        var created = record?.Created ?? !exists;
        // The emulators' folders are the person's: as administrator, never write through a link they could have made.
        if (Protection.IsElevated() && Protection.ThroughLink(file))
            return (false, L.T("il file delle impostazioni passa attraverso un collegamento a un'altra cartella: non lo scrivo", "the settings file goes through a link to another folder: not written"));
        try
        {
            if (exists && !ours)
            {
                // The person's file as it was: kept once, before OptLife's first change since.
                backup = Dir(ctx, "backup", $"{System.IO.Path.GetFileName(file)}.{ctx.Now.ToLocalTime():yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                File.Copy(file, backup, overwrite: true);
                created = false;
            }
            var text = yaml ? LibraryRules.SetYaml(current, values) : LibraryRules.SetIni(current, values);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            var map = written.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            map[file] = new Written(Hash(text), backup, created);
            Save(ctx, "written", map);
            return (true, LibraryRules.Word(preset));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, L.T($"impostazioni non scritte ({ex.Message})", $"settings not written ({ex.Message})"));
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private static ActionResult NotFound() =>
        new(false, L.T("Gioco non trovato: premi \"Controlla ora\" sulla pagina Libreria.", "Game not found: press \"Check now\" on the Library page."));

    private static GameEntry? Find(IModuleContext ctx, string? id) =>
        id is null ? null : Load(ctx).FirstOrDefault(g => g.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The two families the page filters by first.</summary>
    private static string PcKind => L.T("PC", "PC");
    private static string ConsoleKind => L.T("Console", "Consoles");

    private ActionResult DoPlay(IModuleContext ctx, string? id)
    {
        if (FindPc(ctx, id) is { } pc) return PlayPc(ctx, pc);
        if (Find(ctx, id) is not { } g) return NotFound();
        var c = LibraryRules.ConsoleOf(g.Console)!;
        var found = Found(ctx);
        if (ExeOf(ctx, c, found) is not { } exe)
            return new ActionResult(false, L.T($"{c.EmulatorName} non è installato: installalo dalla pagina Emulazione.", $"{c.EmulatorName} is not installed: install it from the Emulation page."));
        if (c.Kind != EmuKind.Vita3K && !File.Exists(g.Boot) && !Directory.Exists(g.Boot))
            return new ActionResult(false, L.T($"Il gioco non c'è più in {g.Path}: premi \"Controlla ora\".", $"The game is no longer in {g.Path}: press \"Check now\"."));

        var (ok, setup) = Apply(ctx, g, exe, chosen: false);
        var full = ctx.Settings.GetBool("fullscreen");
        string args;
        if (c.Kind == EmuKind.RetroArch)
        {
            var cores = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "cores");
            if (LibraryRules.RetroArchArguments(cores, c.Cores, g.Boot, full, File.Exists) is not { } retro)
                return new ActionResult(false, L.T($"Manca il core di RetroArch per {c.Name} ({string.Join(" o ", c.Cores)}): scaricalo da RetroArch, Menu principale → Online Updater → Core Downloader.",
                    $"RetroArch has no core for {c.Name} ({string.Join(" or ", c.Cores)}): download it in RetroArch, Main Menu → Online Updater → Core Downloader."));
            args = retro;
        }
        else args = c.Kind == EmuKind.Mame ? LibraryRules.MameArguments(g.Boot, full) : LibraryRules.Arguments(c.Kind, g.Boot, full);
        var result = _env.Launch(exe, args, System.IO.Path.GetDirectoryName(exe)!);
        if (!result.Ok) return new ActionResult(false, L.T($"{c.EmulatorName}: {result.Detail}", $"{c.EmulatorName}: {result.Detail}"));
        ctx.RecordEvents([new NewEvent(ctx.Now, Launched, Severity.Info, L.T($"Avviato {g.Title}", $"Started {g.Title}"), $"{Launched}:{g.Id}",
            Data: new Dictionary<string, string> { ["game"] = g.Id, ["title"] = g.Title, ["console"] = c.Id, ["emulator"] = c.EmulatorId, ["path"] = g.Path })]);
        var note = !ok ? $" ({setup})" : setup.Length > 0 ? L.T($", impostazioni: {setup}", $", settings: {setup}") : "";
        return new ActionResult(true, L.T($"{g.Title} avviato in {c.EmulatorName}{note}.", $"{g.Title} started in {c.EmulatorName}{note}."));
    }

    private ActionResult DoPreset(IModuleContext ctx, string? argument)
    {
        var parts = (argument ?? "").Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || Find(ctx, parts[0]) is not { } g) return NotFound();
        var choice = parts[1].ToLowerInvariant();
        if (choice != Mine && choice != General && LibraryRules.FromKey(choice) is null)
            return new ActionResult(false, L.T("Scelta non riconosciuta.", "Choice not recognised."));
        var prefs = Prefs(ctx);
        if (choice == General) prefs.Remove(g.Id);
        else prefs[g.Id] = choice;
        Save(ctx, "prefs", prefs);
        var found = Found(ctx);
        var c = LibraryRules.ConsoleOf(g.Console)!;
        string detail;
        if (choice == Mine)
            detail = L.T($"{g.Title}: OptLife non scrive più le impostazioni di questo gioco.", $"{g.Title}: OptLife no longer writes this game's settings.");
        else if (ExeOf(ctx, c, found) is { } exe)
        {
            var (ok, what) = Apply(ctx, g, exe, chosen: true);
            if (!ok) return new ActionResult(false, $"{g.Title}: {what}");
            detail = L.T($"{g.Title}: {what}, scritto nel file di {c.EmulatorName}.", $"{g.Title}: {what}, written in {c.EmulatorName}'s file.");
        }
        else
            detail = L.T($"{g.Title}: scelta salvata, la scrivo quando {c.EmulatorName} c'è.", $"{g.Title}: choice saved, written once {c.EmulatorName} is there.");
        Board(ctx, Load(ctx), found);
        return new ActionResult(true, detail, L.T("Scegli un'altra impostazione dal menu della copertina", "Choose another setting from the cover's menu"));
    }

    private ActionResult DoUndo(IModuleContext ctx, string? id)
    {
        if (Find(ctx, id) is not { } g) return NotFound();
        var found = Found(ctx);
        var c = LibraryRules.ConsoleOf(g.Console)!;
        var written = WrittenFiles(ctx).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (SettingsFile(ctx, g, ExeOf(ctx, c, found)) is not { } file || !written.TryGetValue(file, out var w))
            return new ActionResult(true, L.T("OptLife non aveva scritto niente per questo gioco.", "OptLife had written nothing for this game."));
        string detail;
        try
        {
            if (Protection.IsElevated() && Protection.ThroughLink(file))
                detail = L.T("il file passa attraverso un collegamento a un'altra cartella: non lo tocco", "the file goes through a link to another folder: not touched");
            else if (w.Backup is { } backup && File.Exists(backup))
            {
                File.Copy(backup, file, overwrite: true);
                detail = L.T($"{g.Title}: file di {c.EmulatorName} rimesso com'era.", $"{g.Title}: {c.EmulatorName}'s file put back as it was.");
            }
            else if (w.Created)
            {
                if (!_env.Recycle(file)) return new ActionResult(false, L.T("Il file non è andato nel Cestino (in uso?).", "The file did not go to the Recycle Bin (in use?)."));
                detail = L.T($"{g.Title}: il file creato da OptLife è nel Cestino, valgono le impostazioni generali di {c.EmulatorName}.",
                             $"{g.Title}: the file OptLife created is in the Recycle Bin, {c.EmulatorName}'s general settings apply.");
            }
            else return new ActionResult(false, L.T("La copia di prima non c'è più.", "The earlier copy is gone."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionResult(false, ex.Message);
        }
        written.Remove(file);
        Save(ctx, "written", written);
        var prefs = Prefs(ctx);
        prefs[g.Id] = Mine;
        Save(ctx, "prefs", prefs);
        Board(ctx, Load(ctx), found);
        return new ActionResult(true, detail);
    }

    // ── Memory ───────────────────────────────────────────────────────────────

    /// <summary>A PC game through its launcher (or its own program), as the person: nothing is written for it.</summary>
    private ActionResult PlayPc(IModuleContext ctx, PcGame g)
    {
        if (g.Exe.Length == 0 || !File.Exists(g.Exe))
            return new ActionResult(false, L.T($"{g.Launcher} non si trova più: premi \"Controlla ora\".", $"{g.Launcher} can no longer be found: press \"Check now\"."));
        var folder = g.Launcher == PcRules.Folder ? System.IO.Path.GetDirectoryName(g.Exe)! : System.IO.Path.GetDirectoryName(g.Exe) ?? "";
        var result = _env.Launch(g.Exe, g.Args, folder);
        if (!result.Ok) return new ActionResult(false, $"{g.Title}: {result.Detail}");
        ctx.RecordEvents([new NewEvent(ctx.Now, Launched, Severity.Info, L.T($"Avviato {g.Title}", $"Started {g.Title}"), $"{Launched}:{g.Id}",
            Data: new Dictionary<string, string> { ["game"] = g.Id, ["title"] = g.Title, ["launcher"] = g.Launcher, ["path"] = g.Folder })]);
        return new ActionResult(true, g.Launcher == PcRules.Folder
            ? L.T($"{g.Title} avviato.", $"{g.Title} started.")
            : L.T($"{g.Title}: chiesto a {g.Launcher} di avviarlo.", $"{g.Title}: {g.Launcher} asked to start it."));
    }

    private ActionResult DoHide(IModuleContext ctx, string? id)
    {
        if (FindPc(ctx, id) is not { } g) return NotFound();
        var hidden = Hidden(ctx);
        hidden.Add(g.Id);
        Save(ctx, "hidden", hidden.ToList());
        StorePc(ctx, [.. LoadPc(ctx).Where(x => !x.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase))]);
        Board(ctx, Load(ctx), Found(ctx));
        return new ActionResult(true, L.T($"{g.Title} tolto dalla libreria (non disinstallato).", $"{g.Title} removed from the library (not uninstalled)."),
            L.T("\"Rimetti i giochi tolti\" in cima alla pagina", "\"Bring back removed games\" at the top of the page"));
    }

    private static PcGame? FindPc(IModuleContext ctx, string? id) =>
        id is null || !id.StartsWith("pc:", StringComparison.OrdinalIgnoreCase) ? null
            : LoadPc(ctx).FirstOrDefault(g => g.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string PcFile(IModuleContext ctx) => System.IO.Path.Combine(ctx.DataDirectory, "pc.json");

    internal static List<PcGame> LoadPc(IModuleContext ctx)
    {
        try
        {
            var path = PcFile(ctx);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<PcGame>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void StorePc(IModuleContext ctx, List<PcGame> games)
    {
        Directory.CreateDirectory(ctx.DataDirectory);
        var path = PcFile(ctx);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string LibraryFile(IModuleContext ctx) => System.IO.Path.Combine(ctx.DataDirectory, "library.json");

    internal static List<GameEntry> Load(IModuleContext ctx)
    {
        try
        {
            var path = LibraryFile(ctx);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Store(IModuleContext ctx, List<GameEntry> games)
    {
        Directory.CreateDirectory(ctx.DataDirectory);
        var path = LibraryFile(ctx);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static Dictionary<string, string> Prefs(IModuleContext ctx) =>
        new(Cached<Dictionary<string, string>>(ctx, "prefs") ?? [], StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, Written> WrittenFiles(IModuleContext ctx) =>
        new Dictionary<string, Written>(Cached<Dictionary<string, Written>>(ctx, "written") ?? [], StringComparer.OrdinalIgnoreCase);

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Short(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..8];

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
}
