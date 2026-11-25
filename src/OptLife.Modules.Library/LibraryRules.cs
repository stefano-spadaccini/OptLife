using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OptLife.Modules.Library;

/// <summary>The three ways a game can be set up. Balanced is the default: high and steady.</summary>
public enum Preset { Balanced, Performance, Quality }

/// <summary>How strong the graphics card is, from its memory: it caps how far the presets go.</summary>
public enum GpuLevel { Low, Medium, High, VeryHigh }

/// <summary>The screen games run on.</summary>
public sealed record Screen(int Width, int Height, int Hz);

/// <summary>One value written into an emulator's per-game file: section (ini) or top-level block (yaml), key, value.</summary>
public sealed record ConfigValue(string Section, string Key, string Value);

/// <summary>The emulators the library knows how to start and set up.</summary>
public enum EmuKind { DuckStation, Pcsx2, Rpcs3, Ppsspp, Dolphin, Ryujinx, Cemu, ShadPs4, Azahar, MelonDs, RetroArch, Ares, Mame, Flycast, Xemu, Xenia, Vita3K }

/// <summary>A console the library knows, with what it needs to find, name and start its games.</summary>
/// <param name="Id">The console's own id; its games folder and emulator are published by Emulation under <see cref="Folder"/>.</param>
/// <param name="ThumbRepo">The libretro-thumbnails repository of its box art; null when covers come from elsewhere (GameTDB) or nowhere.</param>
/// <param name="DatPath">The Redump list in libretro-database; null when the disc names itself.</param>
public sealed record LibConsole(string Id, string Name, string Short, string EmulatorId, string EmulatorName, EmuKind Kind,
    string? ThumbRepo, string? DatPath, string[] Extensions)
{
    /// <summary>The Emulation module's console these games belong to ("retro" for the RetroArch systems); null = <see cref="Id"/>.</summary>
    public string? FolderId { get; init; }
    public string Folder => FolderId ?? Id;

    /// <summary>GameTDB's platform for covers by game code ("wii", "wiiu", "3ds", "ds") and the picture's type.</summary>
    public string? Tdb { get; init; }
    public string TdbExt { get; init; } = "png";

    /// <summary>For RetroArch: the cores that play this system, best first (file names without "_libretro.dll").</summary>
    public string[] Cores { get; init; } = [];
}

/// <summary>Rules of the library, without the machine: names, covers, presets, per-game files. Tested on their own.</summary>
public static class LibraryRules
{
    public static readonly LibConsole Ps1 = new("ps1", "PlayStation", "PS1", "duckstation", "DuckStation", EmuKind.DuckStation, "Sony_-_PlayStation",
        "metadat/redump/Sony - PlayStation.dat", [".m3u", ".cue", ".chd", ".pbp", ".iso", ".bin", ".img"]);
    public static readonly LibConsole Ps2 = new("ps2", "PlayStation 2", "PS2", "pcsx2", "PCSX2", EmuKind.Pcsx2, "Sony_-_PlayStation_2",
        "metadat/redump/Sony - PlayStation 2.dat", [".iso", ".bin", ".chd", ".cso", ".gz"]);
    public static readonly LibConsole Ps3 = new("ps3", "PlayStation 3", "PS3", "rpcs3", "RPCS3", EmuKind.Rpcs3, "Sony_-_PlayStation_3",
        "metadat/redump/Sony - PlayStation 3.dat", []);
    public static readonly LibConsole Psp = new("psp", "PSP", "PSP", "ppsspp", "PPSSPP", EmuKind.Ppsspp, "Sony_-_PlayStation_Portable",
        "metadat/redump/Sony - PlayStation Portable.dat", [".iso", ".cso", ".chd"]);
    public static readonly LibConsole GcWii = new("gc-wii", "GameCube / Wii", "GC/Wii", "dolphin", "Dolphin", EmuKind.Dolphin, null, null,
        [".iso", ".gcm", ".rvz", ".wia", ".gcz", ".wbfs", ".ciso"]) { Tdb = "wii" };

    // Piece 3: games in folders (PS4, Wii U) and cartridge images read by their header.
    public static readonly LibConsole Ps4 = new("ps4", "PlayStation 4", "PS4", "shadps4", "shadPS4", EmuKind.ShadPs4, null, null, []);
    public static readonly LibConsole Switch = new("switch", "Nintendo Switch", "Switch", "ryujinx", "Ryujinx", EmuKind.Ryujinx, null, null,
        [".nsp", ".xci", ".nsz", ".xcz"]);
    public static readonly LibConsole WiiU = new("wiiu", "Wii U", "Wii U", "cemu", "Cemu", EmuKind.Cemu, null, null, [".wua", ".wux", ".wud"])
        { Tdb = "wiiu", TdbExt = "jpg" };
    public static readonly LibConsole N3ds = new("3ds", "Nintendo 3DS", "3DS", "azahar", "Azahar", EmuKind.Azahar, null, null,
        [".3ds", ".cci", ".cxi", ".cia", ".3dsx"]) { Tdb = "3ds", TdbExt = "jpg" };
    public static readonly LibConsole Ds = new("ds", "Nintendo DS", "DS", "melonds", "melonDS", EmuKind.MelonDs, null, null, [".nds", ".dsi"])
        { Tdb = "ds", TdbExt = "jpg" };

    // Piece 4: the RetroArch systems share the "retro" folder and tell themselves apart by extension; then arcade, Sega, Xbox, Vita.
    private static LibConsole Retro(string id, string name, string shortName, string repo, string[] extensions, params string[] cores) =>
        new(id, name, shortName, "retroarch", "RetroArch", EmuKind.RetroArch, repo, null, extensions) { FolderId = "retro", Cores = cores };

    public static readonly LibConsole Nes = Retro("nes", "NES", "NES", "Nintendo_-_Nintendo_Entertainment_System", [".nes", ".fds"], "mesen", "nestopia", "fceumm");
    public static readonly LibConsole Snes = Retro("snes", "Super Nintendo", "SNES", "Nintendo_-_Super_Nintendo_Entertainment_System", [".sfc", ".smc"], "snes9x", "bsnes");
    public static readonly LibConsole Gb = Retro("gb", "Game Boy", "GB", "Nintendo_-_Game_Boy", [".gb"], "gambatte", "sameboy", "mgba");
    public static readonly LibConsole Gbc = Retro("gbc", "Game Boy Color", "GBC", "Nintendo_-_Game_Boy_Color", [".gbc"], "gambatte", "sameboy", "mgba");
    public static readonly LibConsole Gba = Retro("gba", "Game Boy Advance", "GBA", "Nintendo_-_Game_Boy_Advance", [".gba"], "mgba", "vba_next");
    public static readonly LibConsole MegaDrive = Retro("md", "Mega Drive", "MD", "Sega_-_Mega_Drive_-_Genesis", [".md", ".gen", ".smd"], "genesis_plus_gx", "picodrive");
    public static readonly LibConsole MasterSystem = Retro("sms", "Master System", "SMS", "Sega_-_Master_System_-_Mark_III", [".sms"], "genesis_plus_gx", "picodrive");
    public static readonly LibConsole GameGear = Retro("gg", "Game Gear", "GG", "Sega_-_Game_Gear", [".gg"], "genesis_plus_gx");
    public static readonly LibConsole PcEngine = Retro("pce", "PC Engine", "PCE", "NEC_-_PC_Engine_-_TurboGrafx_16", [".pce"], "mednafen_pce_fast", "mednafen_pce");
    public static readonly LibConsole Atari2600 = Retro("a2600", "Atari 2600", "2600", "Atari_-_2600", [".a26"], "stella");
    public static readonly LibConsole N64 = new("n64", "Nintendo 64", "N64", "ares", "ares", EmuKind.Ares, "Nintendo_-_Nintendo_64", null, [".z64", ".n64", ".v64"]);
    public static readonly LibConsole Arcade = new("arcade", "Arcade", "Arcade", "mame", "MAME", EmuKind.Mame, null, null, [".zip", ".7z"]);
    public static readonly LibConsole Dreamcast = new("dreamcast", "Dreamcast", "DC", "flycast", "Flycast", EmuKind.Flycast, "Sega_-_Dreamcast",
        "metadat/redump/Sega - Dreamcast.dat", [".gdi", ".cdi", ".chd"]);
    public static readonly LibConsole Xbox = new("xbox", "Xbox", "Xbox", "xemu", "xemu", EmuKind.Xemu, "Microsoft_-_Xbox", null, [".iso", ".xiso"]);
    public static readonly LibConsole X360 = new("x360", "Xbox 360", "X360", "xenia", "Xenia Canary", EmuKind.Xenia, "Microsoft_-_Xbox_360", null, [".iso", ".xex", ".zar"]);
    public static readonly LibConsole Vita = new("vita", "PS Vita", "Vita", "vita3k", "Vita3K", EmuKind.Vita3K, null, null, []);

    public static IReadOnlyList<LibConsole> Consoles { get; } =
    [
        Ps1, Ps2, Ps3, Ps4, Psp, Vita, GcWii, WiiU, Switch, Ds, N3ds, N64, Xbox, X360, Dreamcast,
        Nes, Snes, Gb, Gbc, Gba, MegaDrive, MasterSystem, GameGear, PcEngine, Atari2600, Arcade,
    ];

    /// <summary>Whether OptLife writes this emulator's per-game file (the presets); the others keep their own settings.</summary>
    public static bool HasPresets(EmuKind kind) => kind is EmuKind.DuckStation or EmuKind.Pcsx2 or EmuKind.Rpcs3 or EmuKind.Ppsspp or EmuKind.Dolphin;

    public static LibConsole? ConsoleOf(string id) => Consoles.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ── The public game lists (Redump, via libretro-database) ───────────────

    /// <summary>
    /// Serial → names, from a clrmamepro list. A serial can have several entries (versions, regions): all are kept,
    /// so the one matching the file's own name can win.
    /// </summary>
    public static Dictionary<string, List<string>> ParseDat(TextReader reader)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("game (", StringComparison.Ordinal)) { name = null; continue; }
            if (name is null && line.StartsWith("name \"", StringComparison.Ordinal)) { name = Quoted(line, 5); continue; }
            if (name is not null && line.StartsWith("serial \"", StringComparison.Ordinal) && Quoted(line, 7) is { Length: > 0 } serial)
            {
                foreach (var one in serial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!map.TryGetValue(one, out var list)) map[one] = list = [];
                    if (!list.Contains(name)) list.Add(name);
                }
            }
        }
        return map;
    }

    private static string? Quoted(string line, int from)
    {
        var start = line.IndexOf('"', from);
        if (start < 0) return null;
        var end = line.IndexOf('"', start + 1);
        return end > start ? line[(start + 1)..end] : null;
    }

    /// <summary>"BLES00246" → "BLES-00246", the form the lists use for PS3 codes.</summary>
    public static string DashedId(string titleId) =>
        titleId.Length == 9 && !titleId.Contains('-') ? $"{titleId[..4]}-{titleId[4..]}" : titleId;

    /// <summary>The list's full name for a game: the one equal to the file's name when there is one, else the first.</summary>
    public static string? DatName(IReadOnlyDictionary<string, List<string>> dat, string? serial, string fileName)
    {
        if (serial is null || !dat.TryGetValue(serial, out var names) || names.Count == 0) return null;
        var bare = Path.GetFileNameWithoutExtension(fileName);
        return names.FirstOrDefault(n => n.Equals(bare, StringComparison.OrdinalIgnoreCase)) ?? names[0];
    }

    private static readonly Regex Brackets = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

    /// <summary>"Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It)" → "Metal Gear Solid 2 - Substance".</summary>
    public static string CleanTitle(string name)
    {
        var clean = Brackets.Replace(name, "").Trim();
        return clean.Length > 0 ? clean : name.Trim();
    }

    /// <summary>"(Europe)" from a full name, or null.</summary>
    public static string? Region(string name)
    {
        var m = Regex.Match(name, @"\((?<r>(Europe|USA|Japan|World|Asia|Korea|Australia|Germany|France|Italy|Spain|UK)[^\)]*)\)");
        return m.Success ? m.Groups["r"].Value : null;
    }

    /// <summary>
    /// Names to try for the box art, best first. The thumbnails are named after the lists, but not every version has one:
    /// the exact name, then without version and languages, then the same game in other regions.
    /// Characters a file name cannot hold become '_' as in libretro-thumbnails.
    /// </summary>
    public static IReadOnlyList<string> CoverNames(string datName)
    {
        var names = new List<string> { datName };
        var noVersion = Regex.Replace(datName, @"\s*\((v[\d.]+|Rev [^\)]+)\)", "").Trim();
        names.Add(noVersion);
        var noLanguages = Regex.Replace(noVersion, @"\s*\((?:[A-Z][a-z](?:,[A-Z][a-z])+)\)", "").Trim();
        names.Add(noLanguages);
        var title = CleanTitle(datName);
        foreach (var region in new[] { "Europe", "USA", "World", "Japan" })
            names.Add($"{title} ({region})");
        return [.. names.Select(SafeName).Distinct(StringComparer.Ordinal)];
    }

    public static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append("&*/:`<>?\\|\"".Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    public static string CoverUrl(LibConsole c, string name) =>
        $"https://raw.githubusercontent.com/libretro-thumbnails/{c.ThumbRepo}/master/Named_Boxarts/{Uri.EscapeDataString(name).Replace("'", "%27")}.png";

    public static string DatUrl(LibConsole c) =>
        $"https://raw.githubusercontent.com/libretro/libretro-database/master/{string.Join('/', (c.DatPath ?? "").Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>
    /// GameCube and Wii box art from GameTDB, by the disc's id (as Dolphin itself fetches it): the disc's own region first,
    /// then English and American.
    /// </summary>
    public static IReadOnlyList<string> GameTdbUrls(string id) => GameTdbUrls(id, "wii", "png");

    public static IReadOnlyList<string> GameTdbUrls(string id, string platform, string ext)
    {
        var region = id.Length >= 4 ? id[3] switch
        {
            'E' => "US", 'J' => "JA", 'K' => "KO", 'D' => "DE", 'F' => "FR", 'I' => "IT", 'S' => "ES", 'H' => "NL", 'U' => "AU",
            _ => "EN",
        } : "EN";
        return [.. new[] { region, "EN", "US" }.Distinct().Select(r => $"https://art.gametdb.com/{platform}/cover/{r}/{id}.{ext}")];
    }

    // ── Presets ──────────────────────────────────────────────────────────────

    public static string Word(Preset p) => p switch
    {
        Preset.Performance => L.T("Prestazioni", "Performance"),
        Preset.Quality => L.T("Qualità", "Quality"),
        _ => L.T("Bilanciato", "Balanced"),
    };

    public static string Meaning(Preset p) => p switch
    {
        Preset.Performance => L.T("immagine più leggera, il più fluido possibile", "lighter picture, as smooth as possible"),
        Preset.Quality => L.T("immagine alla risoluzione dello schermo o appena sopra", "picture at the screen's resolution or just above"),
        _ => L.T("alta e stabile, un passo sotto la massima", "high and steady, one step below the highest"),
    };

    /// <summary>Stored keys, never translated, so a change of language keeps the choice.</summary>
    public static string Key(Preset p) => p switch { Preset.Performance => "performance", Preset.Quality => "quality", _ => "balanced" };

    public static Preset? FromKey(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "performance" => Preset.Performance,
        "quality" => Preset.Quality,
        "balanced" => Preset.Balanced,
        _ => null,
    };

    /// <summary>The general choice from the setting's words, in either language; anything else is Balanced.</summary>
    public static Preset FromWord(string? word)
    {
        var w = (word ?? "").Trim().ToLowerInvariant();
        if (w.StartsWith("prestazioni", StringComparison.Ordinal) || w.StartsWith("performance", StringComparison.Ordinal)) return Preset.Performance;
        if (w.StartsWith("qualit", StringComparison.Ordinal) || w.StartsWith("quality", StringComparison.Ordinal)) return Preset.Quality;
        return Preset.Balanced;
    }

    public static GpuLevel LevelOf(double vramGb) => vramGb switch
    {
        <= 0 => GpuLevel.Medium,     // not read: a middle guess
        < 3 => GpuLevel.Low,
        < 6 => GpuLevel.Medium,
        < 10 => GpuLevel.High,
        _ => GpuLevel.VeryHigh,
    };

    /// <summary>The console's own picture height, and how far each card level may push the multiplier.</summary>
    private static (int Lines, int[] Caps) Native(EmuKind kind) => kind switch
    {
        EmuKind.DuckStation => (240, [4, 6, 8, 12]),
        EmuKind.Ppsspp => (272, [3, 5, 8, 10]),
        EmuKind.Dolphin => (528, [2, 3, 4, 6]),
        _ => (448, [2, 3, 4, 6]),   // PCSX2
    };

    /// <summary>
    /// The internal resolution as a multiple of the console's own. Quality = the first step that reaches the screen's height
    /// (never more); Balanced = one step below; Performance = two below. Weaker cards are capped.
    /// </summary>
    public static int Scale(EmuKind kind, Screen screen, GpuLevel level, Preset preset)
    {
        var (lines, caps) = Native(kind);
        var quality = (int)Math.Ceiling(Math.Max(1.0, (double)screen.Height / lines) - 0.05);
        var value = preset switch
        {
            Preset.Quality => quality,
            Preset.Performance => quality - 2,
            _ => quality - 1,
        };
        return Math.Clamp(value, 1, caps[(int)level]);
    }

    public static int Pcsx2Scale(Screen screen, GpuLevel level, Preset preset) => Scale(EmuKind.Pcsx2, screen, level, preset);

    /// <summary>
    /// RPCS3's resolution scale in percent of 720p, in steps of 50. Quality = the screen's height; Balanced = one step below
    /// (RPCS3 leans on the processor: steady first); Performance = 100. Weaker cards are capped.
    /// </summary>
    public static int Rpcs3Scale(Screen screen, GpuLevel level, Preset preset)
    {
        var quality = (int)Math.Floor(screen.Height / 720.0 * 100 / 50 + 0.05) * 50;
        quality = Math.Clamp(quality, 100, 300);
        var value = preset switch
        {
            Preset.Quality => quality,
            Preset.Performance => 100,
            _ => Math.Max(100, quality - 50),
        };
        var cap = level switch { GpuLevel.Low => 100, GpuLevel.Medium => 150, GpuLevel.High => 200, _ => 300 };
        return Math.Min(value, cap);
    }

    /// <summary>
    /// Vertical sync: on with ordinary screens (no tearing); off from 120 Hz, where a 30 or 60 fps game tears little and
    /// sync would only add delay.
    /// </summary>
    public static bool Vsync(Screen screen) => screen.Hz < 120;

    public static IReadOnlyList<ConfigValue> Pcsx2Values(Screen screen, GpuLevel level, Preset preset) =>
    [
        new("EmuCore/GS", "upscale_multiplier", Pcsx2Scale(screen, level, preset).ToString(CultureInfo.InvariantCulture)),
        new("EmuCore/GS", "VsyncEnable", Vsync(screen) ? "true" : "false"),
    ];

    public static IReadOnlyList<ConfigValue> Rpcs3Values(Screen screen, GpuLevel level, Preset preset) =>
    [
        new("Video", "Renderer", "Vulkan"),
        new("Video", "Resolution Scale", Rpcs3Scale(screen, level, preset).ToString(CultureInfo.InvariantCulture)),
        // The game's own pace (30 or 60), whatever the screen's Hz.
        new("Video", "Frame limit", "Auto"),
        new("Video", "VSync", Vsync(screen) ? "true" : "false"),
        new("Video", "Shader Mode", preset == Preset.Quality ? "Async with Shader Interpreter" : "Async Shader Recompiler"),
    ];

    /// <summary>DuckStation: internal resolution and sync; the rest (renderer, PGXP) stays as the person set it.</summary>
    public static IReadOnlyList<ConfigValue> DuckStationValues(Screen screen, GpuLevel level, Preset preset) =>
    [
        new("GPU", "ResolutionScale", Scale(EmuKind.DuckStation, screen, level, preset).ToString(CultureInfo.InvariantCulture)),
        new("Display", "VSync", Vsync(screen) ? "true" : "false"),
    ];

    /// <summary>PPSSPP: internal resolution only (its sync key has changed name between versions: left alone).</summary>
    public static IReadOnlyList<ConfigValue> PpssppValues(Screen screen, GpuLevel level, Preset preset) =>
    [
        new("Graphics", "InternalResolution", Scale(EmuKind.Ppsspp, screen, level, preset).ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>Dolphin's per-game file: internal resolution and sync, in the sections its game files use.</summary>
    public static IReadOnlyList<ConfigValue> DolphinValues(Screen screen, GpuLevel level, Preset preset) =>
    [
        new("Video_Settings", "InternalResolution", Scale(EmuKind.Dolphin, screen, level, preset).ToString(CultureInfo.InvariantCulture)),
        new("Video_Hardware", "VSync", Vsync(screen) ? "True" : "False"),
    ];

    public static IReadOnlyList<ConfigValue> Values(EmuKind kind, Screen screen, GpuLevel level, Preset preset) => kind switch
    {
        EmuKind.Rpcs3 => Rpcs3Values(screen, level, preset),
        EmuKind.DuckStation => DuckStationValues(screen, level, preset),
        EmuKind.Ppsspp => PpssppValues(screen, level, preset),
        EmuKind.Dolphin => DolphinValues(screen, level, preset),
        _ => Pcsx2Values(screen, level, preset),
    };

    /// <summary>The values in words, for the card: "risoluzione interna ×3 (circa 1344 righe, schermo 1440)".</summary>
    public static string Describe(LibConsole c, Screen screen, GpuLevel level, Preset preset)
    {
        if (c.Kind == EmuKind.Rpcs3)
        {
            var percent = Rpcs3Scale(screen, level, preset);
            return L.T($"risoluzione {percent}% (circa {720 * percent / 100} righe, schermo {screen.Height})",
                       $"resolution {percent}% (about {720 * percent / 100} lines, screen {screen.Height})");
        }
        var times = Scale(c.Kind, screen, level, preset);
        var lines = Native(c.Kind).Lines * times;
        return L.T($"risoluzione interna ×{times} (circa {lines} righe, schermo {screen.Height})",
                   $"internal resolution ×{times} (about {lines} lines, screen {screen.Height})");
    }

    /// <summary>
    /// The emulator's arguments to open a game straight away. PCSX2 and DuckStation: -batch closes the emulator with the game,
    /// "--" keeps a path that starts with '-' a path. Dolphin takes full screen as a setting for this run only.
    /// </summary>
    public static string Arguments(EmuKind kind, string boot, bool fullscreen) => kind switch
    {
        EmuKind.Pcsx2 or EmuKind.DuckStation => (fullscreen ? "-batch -fullscreen" : "-batch") + $" -- \"{boot}\"",
        EmuKind.Rpcs3 => "--no-gui" + (fullscreen ? " --fullscreen" : "") + $" \"{boot}\"",
        EmuKind.Ppsspp => (fullscreen ? "--fullscreen " : "") + $"\"{boot}\"",
        EmuKind.Dolphin => "-b" + (fullscreen ? " -C Dolphin.Display.Fullscreen=True" : "") + $" -e \"{boot}\"",
        EmuKind.Ryujinx or EmuKind.Ares => (fullscreen ? "--fullscreen " : "") + $"\"{boot}\"",
        EmuKind.Cemu => (fullscreen ? "-f " : "") + $"-g \"{boot}\"",
        EmuKind.ShadPs4 => $"-g \"{boot}\"" + (fullscreen ? " --fullscreen true" : ""),
        EmuKind.Azahar or EmuKind.MelonDs => (fullscreen ? "-f " : "") + $"\"{boot}\"",
        EmuKind.Flycast => (fullscreen ? "-config window:fullscreen=yes " : "") + $"\"{boot}\"",
        EmuKind.Xemu => (fullscreen ? "-full-screen " : "") + $"-dvd_path \"{boot}\"",
        EmuKind.Xenia => (fullscreen ? "--fullscreen " : "") + $"\"{boot}\"",
        // Vita3K starts an installed game by its title id.
        EmuKind.Vita3K => (fullscreen ? "-F " : "") + $"-r {boot}",
        _ => $"\"{boot}\"",
    };

    /// <summary>
    /// A Wii U game's meta.xml: its GameTDB id (the product code's last 4 letters and the company code's last 2: "WUP-P-AMKP" + "0001"
    /// → "AMKP01") and its English name.
    /// </summary>
    public static (string Id, string Title)? WiiUMeta(string xml)
    {
        string? Tag(string name)
        {
            var m = Regex.Match(xml, $@"<{name}[^>]*>(?<v>[^<]*)</{name}>");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value).Trim() : null;
        }
        var product = Tag("product_code");
        var company = Tag("company_code");
        if (product is null || product.Length < 4) return null;
        var id = product[^4..] + (company is { Length: >= 2 } ? company[^2..] : "01");
        var title = (Tag("longname_en") ?? Tag("shortname_en") ?? id).Split('\n')[0].Trim();
        return (id, title);
    }

    /// <summary>
    /// RetroArch's arguments: the core (the first of the system's cores that is installed), the rom, full screen.
    /// Null when no core is there yet (they come from RetroArch's Online Updater).
    /// </summary>
    public static string? RetroArchArguments(string coresFolder, IEnumerable<string> cores, string rom, bool fullscreen, Func<string, bool> exists)
    {
        var core = cores.Select(c => Path.Combine(coresFolder, $"{c}_libretro.dll")).FirstOrDefault(exists);
        return core is null ? null : (fullscreen ? "-f " : "") + $"-L \"{core}\" \"{rom}\"";
    }

    /// <summary>MAME: the set's folder as the rom path and the set's name; it opens full screen unless told -window.</summary>
    public static string MameArguments(string zip, bool fullscreen) =>
        $"-rompath \"{Path.GetDirectoryName(zip)}\" {Path.GetFileNameWithoutExtension(zip)}" + (fullscreen ? "" : " -window");

    /// <summary>"Metal Gear Solid (Europe) (Disc 2)" → 2; 0 when the name says no disc number.</summary>
    public static int DiscNumber(string name)
    {
        var m = Regex.Match(name, @"\((?:Disc|Disk|CD)\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // ── Writing only our own keys ────────────────────────────────────────────

    /// <summary>
    /// Sets the keys in an ini text, leaving every other line as it is: a key already there is replaced in place, a missing one
    /// goes at the end of its section, a missing section at the end of the file. Keeps the file's line ends.
    /// </summary>
    public static string SetIni(string text, IEnumerable<ConfigValue> values)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        foreach (var v in values)
        {
            var (start, end) = IniSection(lines, v.Section);
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add($"[{v.Section}]");
                lines.Add($"{v.Key} = {v.Value}");
                continue;
            }
            var found = false;
            for (var i = start + 1; i < end; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq <= 0 || !lines[i][..eq].Trim().Equals(v.Key, StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = $"{v.Key} = {v.Value}";
                found = true;
                break;
            }
            if (!found)
            {
                var at = end;
                while (at > start + 1 && lines[at - 1].Trim().Length == 0) at--;
                lines.Insert(at, $"{v.Key} = {v.Value}");
            }
        }
        return string.Join(nl, lines) + nl;
    }

    private static (int Start, int End) IniSection(List<string> lines, string section)
    {
        var start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return (-1, -1);
        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        return (start, end < 0 ? lines.Count : end);
    }

    /// <summary>The value of a key in an ini text, or null.</summary>
    public static string? GetIni(string text, string section, string key)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (start, end) = IniSection(lines, section);
        if (start < 0) return null;
        for (var i = start + 1; i < end; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return lines[i][(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The same for RPCS3's YAML, at the level it uses for its settings: "Video:" at the start of a line, then "  Key: value".
    /// Only lines of that block indented by exactly two spaces are touched.
    /// </summary>
    public static string SetYaml(string text, IEnumerable<ConfigValue> values)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        foreach (var v in values)
        {
            var (start, end) = YamlBlock(lines, v.Section);
            var line = $"  {v.Key}: {v.Value}";
            if (start < 0)
            {
                lines.Add($"{v.Section}:");
                lines.Add(line);
                continue;
            }
            var found = false;
            for (var i = start + 1; i < end; i++)
            {
                if (!lines[i].StartsWith("  ", StringComparison.Ordinal) || lines[i].StartsWith("   ", StringComparison.Ordinal)) continue;
                var colon = lines[i].IndexOf(':');
                if (colon <= 0 || lines[i][2..colon] != v.Key) continue;
                lines[i] = line;
                found = true;
                break;
            }
            if (!found) lines.Insert(end, line);
        }
        return string.Join(nl, lines) + nl;
    }

    private static (int Start, int End) YamlBlock(List<string> lines, string block)
    {
        var start = lines.FindIndex(l => l.TrimEnd() == $"{block}:");
        if (start < 0) return (-1, -1);
        var end = lines.FindIndex(start + 1, l => l.Length > 0 && !char.IsWhiteSpace(l[0]));
        return (start, end < 0 ? lines.Count : end);
    }

    public static string? GetYaml(string text, string block, string key)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (start, end) = YamlBlock(lines, block);
        if (start < 0) return null;
        for (var i = start + 1; i < end; i++)
            if (lines[i].StartsWith($"  {key}:", StringComparison.Ordinal)) return lines[i][(key.Length + 3)..].Trim();
        return null;
    }

    /// <summary>True when every value is already in the text: nothing to write.</summary>
    public static bool Holds(string text, IEnumerable<ConfigValue> values, bool yaml) =>
        values.All(v => string.Equals(yaml ? GetYaml(text, v.Section, v.Key) : GetIni(text, v.Section, v.Key), v.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Paths in RPCS3's games.yml ("BLES00246: E:/Giochi/.../Metal gear 4/"): where its disc games are.</summary>
    public static IReadOnlyList<(string Id, string Folder)> Rpcs3GamesYml(string text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(raw[0])) continue;
            var id = raw[..colon].Trim();
            var path = raw[(colon + 1)..].Trim().Trim('"');
            if (id.Length == 9 && path.Length > 2) list.Add((id, path.Replace('/', '\\').TrimEnd('\\')));
        }
        return list;
    }

    /// <summary>"2 giorni fa", "oggi", or null for never.</summary>
    public static string? Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is null) return null;
        var days = (int)Math.Floor((now.Date - when.Value.ToLocalTime().Date).TotalDays);
        return days switch
        {
            <= 0 => L.T("oggi", "today"),
            1 => L.T("ieri", "yesterday"),
            _ => L.T($"{days} giorni fa", $"{days} days ago"),
        };
    }
}
