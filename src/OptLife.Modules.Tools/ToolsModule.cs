using OptLife.Core;
using OptLife.Modules.Tools.Windows;

namespace OptLife.Modules.Tools;

/// <summary>What the module needs from the machine, so the rules can be tested without touching real programs.</summary>
public interface IToolsEnvironment
{
    /// <summary>The processes of the person's own session.</summary>
    IReadOnlyList<ProcessInfo> Processes();

    /// <summary>Closes the programs whose first process has one of these names, as gently as possible; says what happened.</summary>
    ActionResult Close(IReadOnlyList<AppGroup> apps);

    /// <summary>Starts a program or script the person put on the Strumenti page.</summary>
    ActionResult Launch(Tool tool);

    bool FileExists(string path) => false;

    /// <summary>A file with this name in the folder or one level below (an unzipped program); null when there is none.</summary>
    string? FindFile(string folder, string name) => null;

    /// <summary>
    /// Starts a known program found on the PC. One that needs administrator rights gets them only when its file cannot be changed
    /// by a non-administrator; otherwise it starts as the person and Windows asks.
    /// </summary>
    ActionResult LaunchKnown(string file, bool admin) => Launch(new Tool("", Path.GetFileNameWithoutExtension(file), file, "", false, ""));

    /// <summary>The graphics driver's shader caches that exist, with their weight.</summary>
    IReadOnlyList<ShaderCache> ShaderCaches() => [];

    /// <summary>Sends what is inside these folders (not the folders) to the Recycle Bin; what is in use is left.</summary>
    ActionResult RecycleContents(IReadOnlyList<string> folders) => new(false, "");
}

/// <summary>
/// The Strumenti page: the person's own scripts and programs as buttons, and a way to close the programs
/// running in the background before playing. Closing always shows the list first and acts only on what
/// stays ticked; the game, Discord, music, drivers and OptLife are never in the list.
/// </summary>
public sealed class ToolsModule : IModule
{
    public const string CloseApps = "close-apps", RunTool = "run-tool";
    private readonly IToolsEnvironment _env;

    public ToolsModule() : this(new WindowsToolsEnvironment()) { }

    internal ToolsModule(IToolsEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(
        "tools", L.T("Strumenti", "Tools"),
        L.T("I tuoi script e programmi come pulsanti, e un comando che chiude i programmi in background prima di giocare (ti mostra l'elenco e chiude solo ciò che lasci spuntato).", "Your scripts and programs as buttons, and a command that closes background programs before playing (it shows you the list and closes only what you leave ticked)."));

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Table("tools", [L.T("Gruppo", "Group"), L.T("Nome", "Name"), "File", L.T("Argomenti", "Arguments"), L.T("Admin (sì/no)", "Admin (yes/no)"), L.T("Descrizione", "Description")], L.T("Strumenti", "Tools"),
            L.T("Scritti come: gruppo | nome | file | argomenti | amministratore (sì/no) | descrizione. Il file può essere un programma, uno script .cmd/.bat/.ps1 o un collegamento. " +
            "Con \"sì\" Windows chiede il permesso di amministratore, se OptLife non lo è già.",
            "Written as: group | name | file | arguments | administrator (yes/no) | description. The file can be a program, a .cmd/.bat/.ps1 script or a shortcut. With \"yes\" Windows asks for administrator permission, if OptLife does not have it already.")),
        SettingDefinition.List("protect", L.T("Mai chiudere", "Never close"),
            L.T("Parti del nome del programma (o della sua descrizione) che non compaiono mai nell'elenco da chiudere: musica, chat, driver, periferiche, anti-cheat.", "Parts of the program's name (or description) that never show up in the list to close: music, chat, drivers, peripherals, anti-cheat."),
            "discord", "spotify", "optlife", "lghub", "logi", "icue", "corsair", "razer", "steelseries",
            "msiafterburner", "rtss", "rivatuner", "encoderserver", "nvcontainer", "nvdisplay", "fvcontainer", "nvrla",
            "displaycal", "xboxservice", "gameinput", "vgtray", "vgc", "equalizer", "peace", "soundswitch", "eartrumpet",
            "translucenttb", "twinkle", "powertoys", "obs64", "medal",
            // A piece of other programs (WhatsApp, widgets): closed alone it breaks the program it belongs to.
            "msedgewebview2"),
        SettingDefinition.List("protectFolders", L.T("Cartelle dei giochi", "Game folders"),
            L.T("Un programma che sta in una di queste cartelle (basta una parte del percorso) non compare mai nell'elenco: è il gioco. Neanche il programma che lo ha avviato.", "A program in one of these folders (part of the path is enough) never shows up in the list: it is the game. Neither does the program that started it."),
            // The folders where games go, not those of the launchers next to them: "Program Files (x86)\Epic Games"
            // is the Epic launcher, "Riot Games\Riot Client" the Riot one. A launcher that started the running game
            // is protected anyway, as part of the game's family.
            @"steamapps\common", @"Program Files\Epic Games", @"Riot Games\League of Legends", @"Riot Games\Teamfight Tactics",
            @"Riot Games\VALORANT", @"XboxGames", @"GOG Games", @"EA Games", @"Ubisoft Game Launcher\games"),
        SettingDefinition.List("uncheck", L.T("Mostra ma non spuntare", "Show but do not tick"),
            L.T("Programmi che compaiono nell'elenco già senza spunta: da chiudere solo se lo decidi tu (launcher che servono al gioco, sincronizzazioni, Claude).", "Programs that show up in the list already unticked: to close only if you decide so (launchers the game needs, syncing, Claude)."),
            "steam", "battle.net", "riotclient", "epicgameslauncher", "galaxyclient", "eadesktop", "upc", "claude", "ugreen", "syncspace", "wallpaper",
            // Heavy, and safe to close, but they do something you may want: RGB lighting, NVIDIA recording and overlay.
            "signalrgb", "nvidia overlay", "nvidia app"),
        SettingDefinition.Table("categories", [L.T("Categoria", "Category"), L.T("Parole", "Words")], L.T("Categorie", "Categories"),
            L.T("Come dividere l'elenco dei programmi da chiudere. Scritte come: categoria | parole separate da virgola. Vince la prima riga con una parola che compare nel nome, nella descrizione o nella cartella del programma; il resto va in \"Altro\".",
                "How to split the list of programs to close. Written as: category | comma-separated words. The first line with a word found in the program's name, description or folder wins; the rest goes under \"Other\"."),
            L.T("Launcher", "Launchers") + " | steam, epicgames, battle.net, riotclient, riot client, eadesktop, ea app, upc, ubisoft, galaxyclient, gog galaxy, playnite, xboxapp, prismlauncher, minecraft launcher, itch",
            L.T("Emulatori", "Emulators") + " | bluestacks, hd-player, shadps4, pcsx2, rpcs3, ryujinx, yuzu, suyu, dolphin, retroarch, ppsspp, cemu, duckstation, xemu, xenia, citra, lime3ds, melonds, mgba, vita3k, consoles",
            L.T("Luci e colori", "Lighting and colour") + " | signalrgb, openrgb, icue, lghub, corsair, razer, displaycal, colorcontrol, dccw, wallpaper, lively",
            "Audio | equalizer, peace, voicemeeter, fiio, soundswitch, eartrumpet, audacity, reaper, fl studio, ableton, spotify, foobar, musicbee, apple music, itunes, tidal",
            "Browser | chrome, msedge, firefox, opera, brave, vivaldi, zen browser, arc",
            "Chat | whatsapp, telegram, teams, zoom, slack, skype, signal, messenger",
            L.T("Cloud e sincronizzazione", "Cloud and sync") + " | onedrive, dropbox, google drive, googledrivefs, ugreen, syncspace, syncthing, nextcloud, megasync, icloud",
            L.T("Intelligenza artificiale", "AI") + " | claude, chatgpt, copilot, ollama, lm studio",
            L.T("Sviluppo", "Development") + " | visual studio, devenv, rider, intellij, unity, godot, node, java, docker, github desktop, windowsterminal, powershell, python, dotnet, .net host",
            L.T("Video e registrazione", "Video and recording") + " | obs, medal, vlc, mpc-hc, potplayer, nvidia app, nvidia overlay, shadowplay, streamlabs",
            L.T("Utilità", "Utilities") + " | powertoys, afterburner, rtss, rivatuner, hwinfo, 7zfm, everything, sharex, flux, ditto, optlife, unigetui, windowspackagemanager, winget, phoneexperiencehost, phone link"),
        SettingDefinition.Int("minMb", L.T("Ignora i programmi più piccoli di (MB)", "Ignore programs smaller than (MB)"),
            L.T("Chiudere un programma che occupa pochi megabyte non cambia niente.", "Closing a program using a few megabytes changes nothing."), 30, 0, 2000),
        SettingDefinition.Bool("known", L.T("Mostra gli strumenti trovati sul PC", "Show the tools found on the PC"),
            L.T("Pulsanti per programmi noti che OptLife trova installati (pulizia dei driver, Equalizer APO, MSI Afterburner, HWiNFO…) e per svuotare la cache degli shader NVIDIA. Compaiono solo se ci sono.",
                "Buttons for known programs OptLife finds installed (driver cleanup, Equalizer APO, MSI Afterburner, HWiNFO…) and to empty the NVIDIA shader cache. They show only if present."), true),
        SettingDefinition.List("hiddenKnown", L.T("Strumenti trovati da non mostrare", "Found tools not to show"),
            L.T("Gli identificativi da nascondere: ddu, nvidia-app, amd-software, eq-selector, eq-editor, afterburner, hwinfo, crystaldiskinfo, nvidia-shader-cache.",
                "The ids to hide: ddu, nvidia-app, amd-software, eq-selector, eq-editor, afterburner, hwinfo, crystaldiskinfo, nvidia-shader-cache.")).AsAdvanced(),
    ];

    public const string RestartBios = "restart-bios", RunKnown = "run-known", ClearShaderCache = "clear-shader-cache";

    // The page asks for its buttons often: what is on the disk is looked at once a minute at most.
    private (DateTime At, string Key, IReadOnlyList<(KnownTool Tool, string File)> Found) _known = (DateTime.MinValue, "", []);
    private (DateTime At, bool Any) _cache = (DateTime.MinValue, false);

    private IReadOnlyList<(KnownTool Tool, string File)> Known(ISettings s)
    {
        if (!s.GetBool("known")) return [];
        var own = AppRules.ParseTools(s.GetList("tools"));
        var key = string.Join('\n', own.Select(t => t.File).Concat(s.GetList("hiddenKnown")));
        if (DateTime.UtcNow - _known.At > TimeSpan.FromMinutes(1) || _known.Key != key)
        {
            _known = (DateTime.UtcNow, key, KnownTools.Found(_env, own, s.GetList("hiddenKnown")));
            _cache = (DateTime.MinValue, false); // Settings changed: the cache folders are looked at again too.
        }
        return _known.Found;
    }

    private bool ShowsCache(ISettings s)
    {
        if (!s.GetBool("known") || s.GetList("hiddenKnown").Contains(KnownTools.ShaderCacheId, StringComparer.OrdinalIgnoreCase)) return false;
        if (DateTime.UtcNow - _cache.At > TimeSpan.FromMinutes(1)) _cache = (DateTime.UtcNow, _env.ShaderCaches().Any(c => c.Items > 0));
        return _cache.Any;
    }

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(CloseApps, L.T("Chiudi i programmi in background", "Close background programs"),
            L.T("Togli la spunta a ciò che vuoi tenere aperto. Ognuno riceve prima la richiesta di chiudersi, come col pulsante X; solo quelli senza finestra che la ignorano vengono fermati.", "Untick what you want to keep open. Each one is first asked to close, as with the X button; only those without a window that ignore it are stopped."),
            Permission.Ask, (ctx, argument, _) => Task.FromResult(DoClose(ctx, argument)), NeverAuto: true,
            Choices: ctx => AppRules.Grouped(Candidates(ctx), ctx.Settings.GetList("categories"))),
        new ModuleAction(RestartBios, Firmware.RestartLabel, Firmware.RestartDescription,
            // Never by itself, and always with the box ticked: it closes everything that is open.
            Permission.Ask, (_, argument, _) => Task.FromResult(Firmware.RestartIntoSetup(argument)), NeverAuto: true,
            Choices: _ => Firmware.RestartChoices()),
        new ModuleAction(RunTool, L.T("Avvia uno strumento", "Run a tool"),
            L.T("Avvia uno degli script o programmi elencati nella pagina Strumenti.", "Runs one of the scripts or programs listed on the Tools page."),
            Permission.Ask, (ctx, argument, _) => Task.FromResult(DoRun(ctx, argument))),
        new ModuleAction(RunKnown, L.T("Avvia un programma trovato sul PC", "Start a program found on the PC"),
            L.T("Avvia uno dei programmi noti trovati sul PC (pagina Strumenti). Uno che serve da amministratore li ottiene solo se il suo file lo cambiano solo gli amministratori; altrimenti chiede Windows.",
                "Starts one of the known programs found on the PC (Tools page). One that needs administrator rights gets them only if only administrators can change its file; otherwise Windows asks."),
            Permission.Ask, (ctx, id, _) => Task.FromResult(Known(ctx.Settings).FirstOrDefault(k => k.Tool.Id == id) is { File: { } file } k
                ? _env.LaunchKnown(file, k.Tool.Admin)
                : new ActionResult(false, L.T("Questo programma non c'è più sul PC.", "This program is no longer on the PC.")))),
        new ModuleAction(ClearShaderCache, L.T("Svuota la cache degli shader NVIDIA", "Empty the NVIDIA shader cache"),
            L.T("Manda al Cestino il contenuto delle cartelle della cache degli shader del driver NVIDIA che spunti. Non durante un gioco; ciò che è in uso resta.",
                "Sends to the Recycle Bin the contents of the NVIDIA driver's shader cache folders you tick. Not during a game; what is in use stays."),
            Permission.Ask, (ctx, argument, _) => Task.FromResult(DoClearCache(ctx, argument)), NeverAuto: true,
            Choices: _ => KnownTools.CacheChoices(_env.ShaderCaches())),
    ];

    private ActionResult DoClearCache(IModuleContext ctx, string? argument)
    {
        if (ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal)))
            return new ActionResult(false, L.T("C'è un gioco aperto: la cache è in uso. Riprova dopo averlo chiuso.", "A game is open: the cache is in use. Try again after closing it."));
        var wanted = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Only the driver's own cache folders, whatever was passed.
        var folders = _env.ShaderCaches().Where(c => wanted.Contains(c.Path, StringComparer.OrdinalIgnoreCase)).Select(c => c.Path).ToList();
        return folders.Count == 0 ? ActionResult.Nothing(L.T("Nessuna cartella scelta: non ho tolto niente.", "No folder chosen: nothing removed.")) : _env.RecycleContents(folders);
    }

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("tools", L.T("Strumenti", "Tools"), "", [], Menu: ViewMenu.Tools, Order: -1,
            EmptyText: L.T("Nessuno strumento: aggiungili in Impostazioni → Strumenti.", "No tools: add them in Settings → Tools."),
            Description: L.T("Ogni pulsante avvia subito quello che c'è scritto: premerlo è la conferma. Quelli che chiedono l'amministratore aprono la finestra di Windows.", "Each button runs right away what it says: pressing it is the confirmation. Those asking for administrator rights open the Windows prompt."),
            Layout: ViewLayout.Buttons,
            Buttons: settings =>
            [
                new ViewButton(L.T("Prima di giocare", "Before playing"), L.T("Chiudi i programmi in background", "Close background programs"),
                    L.T("Ti mostra l'elenco con la memoria di ognuno; chiude solo quelli che lasci spuntati. Gioco, Discord, musica e driver non compaiono mai.", "Shows you the list with each one's memory; closes only the ones you leave ticked. Game, Discord, music and drivers never show up."), CloseApps),
                .. AppRules.ParseTools(settings.GetList("tools"))
                    .Select(t => new ViewButton(t.Group, t.Name, (t.Description + (t.Admin ? L.T(" (amministratore)", " (administrator)") : "")).Trim(), RunTool, t.Name)),
                // Known programs, only those on this PC, after the person's own.
                .. Known(settings).Select(k => new ViewButton(k.Tool.Group, k.Tool.Name,
                    k.Tool.Description + (k.Tool.Admin ? L.T(" (amministratore: Windows può chiedere il permesso)", " (administrator: Windows may ask for permission)") : ""), RunKnown, k.Tool.Id, L.T("APRI", "OPEN"))),
                .. (ShowsCache(settings)
                    ? new[] { new ViewButton(KnownTools.ShaderCacheTool.Group, KnownTools.ShaderCacheTool.Name, KnownTools.ShaderCacheTool.Description, ClearShaderCache, ButtonText: L.T("SVUOTA", "EMPTY")) }
                    : []),
                // Only where Windows can open the firmware setup: UEFI.
                .. (Firmware.IsUefi() == true
                    ? new[] { new ViewButton("PC", Firmware.RestartLabel, L.T("Riavvia ed entra nel BIOS senza premere Canc o F2. Chiede conferma: chiude tutti i programmi aperti.", "Restarts into the BIOS without pressing Del or F2. Asks first: it closes every open program."), RestartBios) }
                    : []),
            ]),
    ];

    /// <summary>The live value naming the process of the game being played (a convention on the data, like the overlay's "game.*").</summary>
    public const string GamePidKey = "game.pid";

    private IReadOnlyList<(AppGroup App, bool Checked)> Candidates(IModuleContext ctx)
    {
        var gamePids = ctx.ReadLive().Where(v => v.Key == GamePidKey).Select(v => (int)v.Value).ToList();
        return AppRules.Candidates(_env.Processes(), ctx.Settings.GetList("protect"), ctx.Settings.GetList("protectFolders"),
            ctx.Settings.GetList("uncheck"), ctx.Settings.GetInt("minMb"), gamePids);
    }

    private ActionResult DoClose(IModuleContext ctx, string? argument)
    {
        var wanted = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return ActionResult.Nothing(L.T("Nessun programma scelto: non ho chiuso niente.", "No program chosen: I closed nothing."));
        // The list is read again now: only what is still a candidate is closed, never something protected meanwhile.
        var apps = Candidates(ctx).Select(c => c.App).Where(a => wanted.Contains(a.Root.Name)).ToList();
        if (apps.Count == 0) return new ActionResult(true, L.T("I programmi scelti non sono più aperti.", "The chosen programs are no longer open."));
        return _env.Close(apps);
    }

    private ActionResult DoRun(IModuleContext ctx, string? name)
    {
        var tool = AppRules.ParseTools(ctx.Settings.GetList("tools")).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return tool is null
            ? new ActionResult(false, L.T($"Lo strumento \"{name}\" non è più nell'elenco.", $"The tool \"{name}\" is no longer in the list."))
            : _env.Launch(tool);
    }
}
