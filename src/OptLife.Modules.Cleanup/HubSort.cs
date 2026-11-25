namespace OptLife.Modules.Cleanup;

/// <summary>A shortcut waiting in the Hub's "to sort" folder: its file, the name people see, and what it opens.</summary>
public sealed record Shortcut(string Path, string Name, string Target);

/// <summary>"Games/Giochi | steam, epic, …": the section (any of the names, as part of a Hub folder's name) and the words that send a shortcut there.</summary>
public sealed record SortRule(IReadOnlyList<string> Sections, IReadOnlyList<string> Words)
{
    public static SortRule? Parse(string line)
    {
        var p = CleanupRules.Split(line);
        if (p.Length < 2) return null;
        var sections = p[0].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = CleanupRules.Names(p[1]).Select(w => w.ToLowerInvariant()).ToList();
        return sections.Length > 0 && words.Count > 0 ? new SortRule(sections, words) : null;
    }
}

/// <summary>Where a shortcut belongs in the Hub, and how sure that is.</summary>
public sealed record SortProposal(Shortcut Shortcut, string? Section, bool Sure);

/// <summary>A shortcut OptLife moved into a Hub section, kept so it can be put back.</summary>
public sealed record HubMove(string Id, string From, string To, DateTimeOffset At);

/// <summary>
/// What the person chose for a shortcut that keeps coming back with the name of one already in the Hub (an update that puts its
/// own icon back): the next time the same name lands for the same section, the same choice is made by itself.
/// </summary>
/// <param name="Name">The file name of the shortcut, "Suwayomi Launcher.lnk".</param>
/// <param name="Choice">One of <see cref="TwinChoice"/>.</param>
public sealed record TwinRule(string Name, string Section, string Choice);

/// <summary>The three ways out when a section already has a shortcut with the same name.</summary>
public static class TwinChoice
{
    /// <summary>Keep the one in the Hub: the new one goes to the Recycle Bin.</summary>
    public const string KeepHub = "hub";
    /// <summary>Use the new one: the one in the Hub goes to the Recycle Bin.</summary>
    public const string UseNew = "new";
    /// <summary>Keep both: the new one goes in as "Name (2)".</summary>
    public const string Both = "both";

    public static bool IsKnown(string? c) => c is KeepHub or UseNew or Both;

    public static string Describe(string c) => c switch
    {
        KeepHub => L.T("tieni quello della Hub (il nuovo nel Cestino)", "keep the one in the Hub (the new one to the Recycle Bin)"),
        UseNew => L.T("usa il nuovo (quello della Hub nel Cestino)", "use the new one (the one in the Hub to the Recycle Bin)"),
        _ => L.T("tieni tutti e due (il nuovo come \"Nome (2)\")", "keep both (the new one as \"Name (2)\")"),
    };
}

/// <summary>
/// Sorting the shortcuts that land in the Hub's "to sort" folder (new programs, updates that put their icon back on the Desktop):
/// each goes to the section its target says (a game launcher to the games, an emulator to the emulators…), by words the person
/// can change. Generic defaults, matched against the sections the Hub really has.
/// </summary>
public static class HubSort
{
    /// <summary>Defaults valid for anyone: section names in Italian and English, words about what the shortcut opens.</summary>
    public static string[] Defaults =>
    [
        // Most specific first: a tool installed through Steam (Blender, Wallpaper Engine) is a tool before it is "in a Steam folder".
        "Emulat | emulat, retroarch, pcsx2, rpcs3, duckstation, dolphin, ryujinx, yuzu, citra, cemu, ppsspp, shadps4, xemu, xenia, bluestacks, es-de, emulationstation",
        "Study/Studio/Sviluppo/Lavoro/Work/Development/Projects | visual studio, vscode, code.exe, git, github, unity, jetbrains, rider, terminal, python, node, android studio, office, word, excel, powerpoint, onenote, blender, gimp, krita, photoshop, davinci, premiere, audacity, reaper, ableton, fl studio, obsidian, notion, chatgpt, claude, copilot",
        "Monitoring/Utilities/Sistema/System/Maintenance | control panel, pannello di controllo, task manager, gestione attività, settings, impostazioni, nvidia, amd software, afterburner, hwinfo, cpu-z, gpu-z, crystaldisk, revo, ccleaner, wiztree, everything, 7-zip, unigetui, wingetui, winget, optlife",
        "Network/Rete/NAS/Remote | nas, ugreen, synology, vpn, anydesk, teamviewer, parsec, remote, router, putty, winscp, filezilla",
        "Look/Display/Colore/Color | displaycal, colour, color, calibr, monitor, hdr, wallpaper, translucent",
        "Chat | discord, whatsapp, telegram, signal",
        // Store apps are recognised by their name or by their package family in "shell:AppsFolder\<family>!<app>" (AppleInc.AppleTV, 4DF9E0F8.Netflix…).
        "Entertainment/Intrattenimento/Musica/Music/Media | spotify, music, musica, vlc, itunes, netflix, prime video, primevideo, amazonvideo, disney, apple tv, appletv, crunchyroll, deezer, tidal, wimpmusic, youtube, twitch, plex, kodi, jellyfin, obs",
        // Last, being the broadest: anything from a game launcher or a games folder.
        "Giochi/Games/Gaming | steam, epic games, battle.net, gog galaxy, riot, ubisoft, ea app, origin, xbox, playnite, \\giochi\\, \\games\\, steamapps, launcher, minecraft, roblox, pokemon, overwolf",
    ];

    /// <summary>The Hub section that has one of the rule's names in its folder name.</summary>
    public static string? SectionFor(SortRule rule, IReadOnlyList<string> sections) =>
        sections.FirstOrDefault(s => rule.Sections.Any(n => s.Contains(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Sure when exactly one section matches; otherwise the first match is only a suggestion, and none means the person picks.</summary>
    public static SortProposal Propose(Shortcut s, IReadOnlyList<string> sections, IReadOnlyList<SortRule> rules)
    {
        var text = $"{s.Name} {s.Target}".ToLowerInvariant();
        // A section named after the program ("3 Manga - Suwayomi") wins over any word list: the person made it for that.
        var named = sections.Where(sec => sec.Split([' ', '-', ',', '.', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.Length >= 5 && !w.All(char.IsDigit) && text.Contains(w.ToLowerInvariant(), StringComparison.Ordinal))).ToList();
        if (named.Count == 1) return new SortProposal(s, named[0], true);
        var found = new List<string>();
        foreach (var rule in rules)
            if (rule.Words.Any(w => text.Contains(w, StringComparison.Ordinal)) && SectionFor(rule, sections) is { } section && !found.Contains(section))
                found.Add(section);
        return new SortProposal(s, found.FirstOrDefault(), found.Count == 1);
    }
}
