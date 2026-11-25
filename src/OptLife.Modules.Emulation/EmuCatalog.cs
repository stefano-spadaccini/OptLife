namespace OptLife.Modules.Emulation;

/// <summary>Where an emulator comes from.</summary>
/// <param name="GitHub">"owner/repo": the latest release, the asset whose name matches <paramref name="Asset"/>.</param>
/// <param name="Winget">A winget package id, when the maker publishes there instead.</param>
/// <param name="Site">The official page, always: for downloading by hand, or the only way when the others are null.</param>
public sealed record EmuSource(string Site, string? GitHub = null, string? Asset = null, string? Winget = null);

/// <param name="Exe">File name of the program, with * where the version or build goes ("duckstation*.exe").</param>
/// <param name="Installed">Part of the name it has among the installed programs (installers, winget), when it is not portable.</param>
/// <param name="Launcher">A launcher that downloads and updates the emulator itself: when that is what is found, OptLife leaves updates to it.</param>
public sealed record Emulator(string Id, string Name, string Exe, EmuSource Source, string Note, string? Installed = null, string? Launcher = null);

/// <summary>A file of the console the emulator needs and nobody may hand out: BIOS, firmware, keys.</summary>
/// <param name="Places">Where it is looked for: paths with {bios} (the console's BIOS folder), {emu} (the emulator's
/// folder), {appdata}; the last part may hold * ("scph*.bin"). Found in any = present.</param>
/// <param name="Official">The maker's own download, when there is one (Sony publishes the PS3 and Vita firmware).</param>
/// <param name="Guide">How to take it from your own console, or what the emulator wants.</param>
/// <param name="Required">False when the emulator works without it (a built-in replacement), only better with it.</param>
public sealed record SystemFile(string Id, string Name, string What, IReadOnlyList<string> Places, string? Official, string Guide, bool Required = true);

/// <param name="Folder">The console's folder for games and BIOS ("PS2").</param>
/// <param name="Family">The heading it goes under, and the folder of its emulators ("PlayStation").</param>
public sealed record GameConsole(string Id, string Name, string Family, string Folder, IReadOnlyList<Emulator> Emulators, IReadOnlyList<SystemFile> Files);

/// <summary>
/// Every console worth emulating, with the emulator to use first and the alternatives, where each comes from,
/// and the console files they need. Only official sources: OptLife never downloads BIOS, firmware, keys or games.
/// Checked on 22 Nov 2026 (GitHub releases, winget, official pages).
/// </summary>
public static class EmuCatalog
{
    private static string Optional => L.T("Facoltativo: l'emulatore ha un sostituto incorporato, con l'originale alcuni giochi vanno meglio.",
        "Optional: the emulator has a built-in replacement, some games run better with the original.");

    public static IReadOnlyList<GameConsole> All =>
    [
        // ── Sony ──
        new("ps1", "PlayStation", "PlayStation", "PS1",
        [
            new("duckstation", "DuckStation", "duckstation-qt*.exe",
                new("https://www.duckstation.org", "stenzek/duckstation", @"^duckstation-windows-x64-release\.zip$"),
                L.T("Il migliore per PS1: preciso, veloce, upscaling e salvataggi rapidi.", "The best for PS1: accurate, fast, upscaling and quick saves.")),
        ],
        [
            new("ps1-bios", "BIOS PS1", L.T("Il BIOS della PlayStation (es. scph5501.bin).", "The PlayStation BIOS (e.g. scph5501.bin)."),
                [@"{bios}\*.bin", @"{emu}\bios\*.bin"], null, "https://github.com/stenzek/duckstation#bios-images"),
        ]),
        new("ps2", "PlayStation 2", "PlayStation", "PS2",
        [
            new("pcsx2", "PCSX2", "pcsx2*.exe",
                new("https://pcsx2.net/downloads", "PCSX2/pcsx2", @"^pcsx2-v[\d.]+-windows-x64-Qt\.7z$"),
                L.T("L'emulatore PS2 di riferimento.", "The reference PS2 emulator.")),
        ],
        [
            new("ps2-bios", "BIOS PS2", L.T("Il BIOS della PS2, estratto dalla tua console.", "The PS2 BIOS, dumped from your console."),
                [@"{bios}\*.bin", @"{emu}\bios\*.bin", @"{documents}\PCSX2\bios\*.bin"], null, "https://pcsx2.net/docs/setup/bios/"),
        ]),
        new("ps3", "PlayStation 3", "PlayStation", "PS3",
        [
            new("rpcs3", "RPCS3", "rpcs3.exe",
                new("https://rpcs3.net/download", "RPCS3/rpcs3-binaries-win", @"_win64_msvc\.7z$"),
                L.T("L'unico emulatore PS3 maturo.", "The only mature PS3 emulator.")),
        ],
        [
            new("ps3-fw", L.T("Firmware PS3", "PS3 firmware"),
                L.T("Il file PS3UPDAT.PUP: si scarica gratis dal sito di Sony, poi in RPCS3 File → Install Firmware.",
                    "The PS3UPDAT.PUP file: free from Sony's site, then in RPCS3 File → Install Firmware."),
                [@"{emu}\dev_flash\sys\external\*.sprx"], "https://www.playstation.com/en-us/support/hardware/ps3/system-software/", "https://rpcs3.net/quickstart"),
        ]),
        new("ps4", "PlayStation 4", "PlayStation", "PS4",
        [
            new("shadps4", "shadPS4", "shadPS4.exe",
                new("https://shadps4.net", "shadps4-emu/shadPS4", @"^shadps4-win64-sdl-[\d.]+\.zip$"),
                L.T("Emulatore PS4 giovane: molti giochi vanno, altri no. Con lo shadPS4 Qt Launcher le versioni le scarica e aggiorna il launcher.",
                    "A young PS4 emulator: many games run, others do not. With the shadPS4 Qt Launcher, the launcher downloads and updates the versions."),
                Launcher: "shadPS4QtLauncher.exe"),
        ],
        [
            new("ps4-modules", L.T("Moduli di sistema PS4", "PS4 system modules"),
                L.T("Alcuni giochi chiedono i moduli (.sprx) estratti dalla tua PS4, nella cartella sys_modules.",
                    "Some games want the modules (.sprx) dumped from your PS4, in the sys_modules folder."),
                [@"{emu}\user\sys_modules\*.sprx", @"{appdata}\shadPS4\sys_modules\*.sprx"], null, "https://github.com/shadps4-emu/shadPS4#readme", Required: false),
        ]),
        new("psp", "PSP", "PlayStation", "PSP",
        [
            new("ppsspp", "PPSSPP", "PPSSPPWindows64.exe",
                new("https://www.ppsspp.org/download/", "hrydgard/ppsspp", @"^PPSSPP-v[\d.]+-Windows-x64\.zip$", "PPSSPPTeam.PPSSPP"),
                L.T("Funziona quasi tutto, non serve nessun BIOS.", "Almost everything runs, no BIOS needed.")),
        ], []),
        new("vita", "PS Vita", "PlayStation", "Vita",
        [
            new("vita3k", "Vita3K", "Vita3K.exe",
                new("https://vita3k.org", "Vita3K/Vita3K", @"^windows-latest\.zip$"),
                L.T("Emulatore PS Vita in sviluppo.", "PS Vita emulator in development.")),
        ],
        [
            new("vita-fw", L.T("Firmware PS Vita", "PS Vita firmware"),
                L.T("PSVUPDAT.PUP dal sito di Sony, installato al primo avvio di Vita3K.", "PSVUPDAT.PUP from Sony's site, installed when Vita3K first starts."),
                [@"{emu}\data\os0\*", @"{appdata}\Vita3K\Vita3K\os0\*"], "https://www.playstation.com/en-us/support/hardware/psvita/system-software/", "https://vita3k.org/quickstart.html"),
        ]),

        // ── Nintendo ──
        new("switch", "Nintendo Switch", "Nintendo", "Switch",
        [
            new("ryujinx", "Ryujinx", "Ryujinx.exe", new("https://ryujinx.app"),
                L.T("Solo dal sito ufficiale: dopo le azioni di Nintendo del 2026 gli emulatori Switch non si scaricano più da GitHub.",
                    "Only from the official site: after Nintendo's 2026 actions Switch emulators are no longer on GitHub.")),
        ],
        [
            new("switch-keys", L.T("Chiavi Switch (prod.keys)", "Switch keys (prod.keys)"),
                L.T("Estratte dalla tua Switch: senza, nessun gioco parte.", "Dumped from your Switch: without them no game starts."),
                [@"{appdata}\Ryujinx\system\prod.keys", @"{emu}\portable\system\prod.keys"], null, "https://ryujinx.app"),
            new("switch-fw", L.T("Firmware Switch", "Switch firmware"),
                L.T("Estratto dalla tua Switch, installato da Ryujinx (Strumenti → Installa firmware).", "Dumped from your Switch, installed from Ryujinx (Tools → Install firmware)."),
                [@"{appdata}\Ryujinx\bis\system\Contents\registered\*", @"{emu}\portable\bis\system\Contents\registered\*"], null, "https://ryujinx.app"),
        ]),
        new("wiiu", "Wii U", "Nintendo", "WiiU",
        [
            new("cemu", "Cemu", "Cemu.exe",
                new("https://cemu.info/", "cemu-project/Cemu", @"^cemu-[\d.]+-windows-x64\.zip$"),
                L.T("L'emulatore Wii U, maturo.", "The Wii U emulator, mature.")),
        ],
        [
            new("wiiu-keys", L.T("Chiavi Wii U (keys.txt)", "Wii U keys (keys.txt)"),
                L.T("Servono solo per i giochi cifrati; con i dump decifrati della tua console no.", "Needed only for encrypted games; not with decrypted dumps of your console."),
                [@"{emu}\keys.txt"], null, "https://cemu.info/", Required: false),
        ]),
        new("gc-wii", "GameCube / Wii", "Nintendo", "GC-Wii",
        [
            new("dolphin", "Dolphin", "Dolphin.exe", new("https://dolphin-emu.org/download/", Winget: "DolphinEmulator.Dolphin"),
                L.T("Eccellente, non serve nessun BIOS.", "Excellent, no BIOS needed."), Installed: "Dolphin"),
        ], []),
        new("3ds", "Nintendo 3DS", "Nintendo", "3DS",
        [
            new("azahar", "Azahar", "azahar*.exe",
                new("https://azahar-emu.org", "azahar-emu/azahar", @"^azahar-windows-msvc-[\d.]+\.zip$"),
                L.T("Il successore di Citra. Vuole giochi decifrati, dalla tua console.", "Citra's successor. Wants decrypted games, from your console.")),
        ], []),
        new("ds", "Nintendo DS", "Nintendo", "DS",
        [
            new("melonds", "melonDS", "melonDS.exe",
                new("https://melonds.kuribo64.net", "melonDS-emu/melonDS", @"^melonDS-[\d.]+-windows-x86_64\.zip$"),
                L.T("Preciso, anche il Wi-Fi locale.", "Accurate, local Wi-Fi too.")),
        ],
        [
            new("ds-bios", "BIOS DS", L.T("bios7.bin, bios9.bin, firmware.bin dalla tua DS.", "bios7.bin, bios9.bin, firmware.bin from your DS.") + " " + Optional,
                [@"{bios}\bios9.bin", @"{emu}\bios9.bin"], null, "https://melonds.kuribo64.net/faq.php", Required: false),
        ]),
        new("n64", "Nintendo 64", "Nintendo", "N64",
        [
            new("ares", "ares", "ares.exe",
                new("https://ares-emu.net", "ares-emulator/ares", @"^ares-windows-x64\.zip$"),
                L.T("Molto preciso; fa anche tante console retro.", "Very accurate; it does many retro consoles too.")),
        ], []),

        // ── Microsoft ──
        new("xbox", "Xbox", "Xbox", "Xbox",
        [
            new("xemu", "xemu", "xemu.exe",
                new("https://xemu.app", "xemu-project/xemu", @"^xemu-[\d.]+-windows-x86_64\.zip$"),
                L.T("L'emulatore della prima Xbox.", "The original Xbox emulator.")),
        ],
        [
            new("xbox-files", L.T("BIOS, boot ROM e disco Xbox", "Xbox BIOS, boot ROM and hard disk"),
                L.T("Tre file dalla tua Xbox (MCPX, BIOS, immagine del disco): la guida di xemu spiega quali.", "Three files from your Xbox (MCPX, BIOS, disk image): the xemu guide says which."),
                [@"{bios}\*.bin", @"{appdata}\xemu\xemu\*.qcow2"], null, "https://xemu.app/docs/required-files/"),
        ]),
        new("x360", "Xbox 360", "Xbox", "X360",
        [
            new("xenia", "Xenia Canary", "xenia_canary.exe",
                new("https://xenia.jp", "xenia-canary/xenia-canary-releases", @"^xenia_canary_windows.*\.zip$"),
                L.T("Xbox 360: molti giochi, serve una scheda video recente.", "Xbox 360: many games, needs a recent graphics card.")),
        ], []),

        // ── Sega ──
        new("dreamcast", "Dreamcast / Naomi", "Sega", "Dreamcast",
        [
            new("flycast", "Flycast", "flycast.exe",
                new("https://flyinghead.github.io/flycast-builds/", "flyinghead/flycast", @"^flycast-win64-[\d.]+\.zip$"),
                L.T("Dreamcast, Naomi e Atomiswave, con il gioco online.", "Dreamcast, Naomi and Atomiswave, with online play.")),
        ],
        [
            new("dc-bios", "BIOS Dreamcast", "dc_boot.bin, dc_flash.bin. " + Optional,
                [@"{bios}\dc_boot.bin", @"{emu}\data\dc_boot.bin"], null, "https://flyinghead.github.io/flycast-builds/", Required: false),
        ]),

        // ── Retro, arcade, Android ──
        new("retro", L.T("Console retro (NES, SNES, Mega Drive, Game Boy, GBA, Saturn, PC Engine…)", "Retro consoles (NES, SNES, Mega Drive, Game Boy, GBA, Saturn, PC Engine…)"),
            L.T("Retro e arcade", "Retro and arcade"), "Retro",
        [
            new("retroarch", "RetroArch", "retroarch.exe", new("https://www.retroarch.com/?page=platforms", Winget: "Libretro.RetroArch"),
                L.T("Un programma, tanti \"core\": uno per console. I core si scaricano da RetroArch (Online Updater).",
                    "One program, many \"cores\": one per console. Cores download from RetroArch (Online Updater)."), Installed: "RetroArch"),
        ],
        [
            new("retro-bios", L.T("BIOS delle console retro", "Retro console BIOS"),
                L.T("Saturn, PlayStation, PC Engine CD e altre li vogliono nella cartella system di RetroArch; la guida dice nome per nome.",
                    "Saturn, PlayStation, PC Engine CD and others want them in RetroArch's system folder; the guide lists them by name."),
                [@"{emu}\system\*.bin", @"{bios}\*.bin"], null, "https://docs.libretro.com/guides/bios/", Required: false),
        ]),
        new("arcade", "Arcade", L.T("Retro e arcade", "Retro and arcade"), "Arcade",
        [
            new("mame", "MAME", "mame.exe", new("https://www.mamedev.org/", Winget: "MAMEdev.MAME"),
                L.T("Le sale giochi: ogni gioco è un \"set\" con i suoi file.", "The arcades: every game is a \"set\" with its own files."), Installed: "MAME"),
        ], []),
        new("android", "Android", "Android", "Android",
        [
            new("bluestacks", "BlueStacks", "HD-Player.exe", new("https://www.bluestacks.com", Winget: "BlueStack.BlueStacks"),
                L.T("Le app e i giochi Android sul PC.", "Android apps and games on the PC."), Installed: "BlueStacks"),
        ], []),
    ];

    /// <summary>The emulator with this id, and its console.</summary>
    public static (GameConsole Console, Emulator Emulator)? Find(string id) =>
        All.SelectMany(c => c.Emulators.Select(e => (c, e))).FirstOrDefault(p => p.e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is var (con, emu) && con is not null
            ? (con, emu) : null;
}
