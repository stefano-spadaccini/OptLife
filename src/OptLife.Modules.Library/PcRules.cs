using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OptLife.Modules.Library;

/// <summary>
/// A PC game, from a launcher or a folder. It is started the way the launcher itself starts it (its own link or command), or by
/// its program when it has no launcher.
/// </summary>
/// <param name="Id">"pc:steam:1030300", "pc:epic:Sugar", "pc:folder:&lt;hash&gt;".</param>
/// <param name="Exe">What is started: the launcher, explorer.exe for a launcher link, or the game itself.</param>
/// <param name="Cover">A picture already on this PC (the launcher's own cache, the game's logo).</param>
/// <param name="CoverUrl">A public picture to fetch once when there is none on the PC (the store's own box art).</param>
/// <param name="LastPlayed">When the launcher says it was last played (Steam keeps it), so recent games show even before OptLife saw them.</param>
public sealed record PcGame(string Id, string Launcher, string Title, string Folder, string Exe, string Args, string? Cover = null, string? CoverUrl = null,
    DateTimeOffset? LastPlayed = null, string? Wide = null);

/// <summary>An entry of Windows' list of installed programs.</summary>
public sealed record UninstallEntry(string Key, string Name, string Publisher, string Location, string Uninstall, string Icon);

/// <summary>Reading what the launchers leave on disk, without the machine. Tested on their own.</summary>
public static class PcRules
{
    public const string Manual = "Manual", Steam = "Steam", Epic = "Epic Games", BattleNet = "Battle.net", Riot = "Riot", Ubisoft = "Ubisoft Connect",
        Gog = "GOG", Ea = "EA app", Xbox = "Xbox", Folder = "Folder";

    /// <summary>The launcher's name as the page shows it ("Cartelle" for portable games).</summary>
    public static string Show(string launcher) => launcher switch
    {
        Folder => L.T("Giochi nelle cartelle", "Games in folders"),
        Manual => L.T("Aggiunti a mano", "Added by hand"),
        _ => launcher,
    };

    /// <summary>The games written in the settings: "name | program | arguments"; a program that is not there is left out.</summary>
    public static IEnumerable<PcGame> ByHand(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var p = line.Split('|', StringSplitOptions.TrimEntries);
            if (p.Length < 2 || p[0].Length == 0) continue;
            var exe = Environment.ExpandEnvironmentVariables(p[1].Trim('"'));
            if (!File.Exists(exe)) continue;
            yield return new PcGame($"pc:manual:{p[0].ToLowerInvariant()}", Manual, p[0], Path.GetDirectoryName(exe) ?? "", exe, p.Length > 2 ? p[2] : "");
        }
    }

    /// <summary>Steam software that is not a game, left out unless the person adds it back (names, any case).</summary>
    public static IReadOnlyList<string> NotGames { get; } =
        ["Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "SteamVR", "Wallpaper Engine", "Lossless Scaling", "Steam Audio", "Dedicated Server", "SDK"];

    /// <summary>The same game under two spellings ("Rocket League" and "Rocket League®", "RocketLeague"): equal without signs and spaces.</summary>
    public static bool SameGame(string a, string b)
    {
        static string Plain(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var (x, y) = (Plain(a), Plain(b));
        return x.Length > 0 && x == y;
    }

    public static bool LooksLikeTool(string name) => NotGames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    // ── Steam ────────────────────────────────────────────────────────────────

    /// <summary>The "key" "value" pairs of a Valve text file, flattened (nested blocks are walked through).</summary>
    public static IEnumerable<(string Key, string Value)> VdfPairs(string text)
    {
        foreach (Match m in Regex.Matches(text, "^\\s*\"(?<k>[^\"]*)\"\\s+\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Multiline))
            yield return (m.Groups["k"].Value, m.Groups["v"].Value.Replace("\\\\", "\\"));
    }

    /// <summary>The library folders listed in libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> SteamLibraries(string vdf) =>
        [.. VdfPairs(vdf).Where(p => p.Key.Equals("path", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>An appmanifest_*.acf: id, name, folder; null when it is not fully installed (StateFlags without 4).</summary>
    public static (string Id, string Name, string InstallDir)? SteamApp(string acf)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in VdfPairs(acf)) map.TryAdd(k, v);
        if (!map.TryGetValue("appid", out var id) || !map.TryGetValue("name", out var name)) return null;
        if (map.TryGetValue("StateFlags", out var flags) && int.TryParse(flags, out var f) && (f & 4) == 0) return null;
        return (id, name, map.GetValueOrDefault("installdir", ""));
    }

    /// <summary>Steam's public store search (no account): used only to find the box art of a game Steam also sells.</summary>
    public static string SteamSearchUrl(string title) =>
        $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(Clean(title))}&l=english&cc=US";

    private static string Clean(string title) => Regex.Replace(title.Replace('™', ' ').Replace('®', ' '), @"\s+[-–:]\s+", " ").Trim();

    private static string Plain(string s) => new([.. s.ToLowerInvariant().Replace("δ", "delta").Replace("ω", "omega").Replace("α", "alpha").Replace("β", "beta")
        .Replace("γ", "gamma").Replace("σ", "sigma").Where(char.IsLetterOrDigit)]);

    /// <summary>A shorter search when the whole title finds nothing ("Metal Gear Solid Delta" → "Metal Gear Solid"): its first three words.</summary>
    public static string? ShorterQuery(string title)
    {
        var words = Clean(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 3 ? string.Join(' ', words.Take(3)) : null;
    }

    /// <summary>
    /// The Steam app whose name is the same game: equal once spaces, signs and case are left out ("Metal Gear Solid V - The Phantom
    /// Pain" = "METAL GEAR SOLID V: THE PHANTOM PAIN"), or one that starts with the whole title. Null when none is close enough.
    /// </summary>
    public static string? SteamMatch(string json, string title)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return null;
            var want = Plain(title);
            if (want.Length < 3) return null;
            var found = items.EnumerateArray()
                .Select(i => (Id: i.TryGetProperty("id", out var id) ? id.ToString() : "", Name: i.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
                .Where(i => i.Id.Length > 0).ToList();
            return found.FirstOrDefault(i => Plain(i.Name) == want).Id
                   ?? found.FirstOrDefault(i => Plain(i.Name).StartsWith(want, StringComparison.Ordinal) && Plain(i.Name).Length - want.Length <= 12).Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Steam app id in one of Steam's picture addresses, or null.</summary>
    public static string? SteamAppOf(string url) =>
        Regex.Match(url, @"steamstatic\.com/store_item_assets/steam/apps/(\d+)/") is { Success: true } m ? m.Groups[1].Value : null;

    public static string SteamDetailsUrl(string appId) => $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";

    /// <summary>The store page's wide picture from a public app-details answer.</summary>
    public static string? SteamHeader(string json)
    {
        var m = Regex.Match(json, "\"header_image\"\\s*:\\s*\"(?<u>https:[^\"]+)\"");
        return m.Success ? m.Groups["u"].Value.Replace("\\/", "/") : null;
    }

    /// <summary>"LastPlayed" of an appmanifest (seconds since 1970); null when never played.</summary>
    public static DateTimeOffset? SteamLastPlayed(string acf) =>
        VdfPairs(acf).FirstOrDefault(p => p.Key.Equals("LastPlayed", StringComparison.OrdinalIgnoreCase)).Value is { } v && long.TryParse(v, out var t) && t > 0
            ? DateTimeOffset.FromUnixTimeSeconds(t) : null;

    /// <summary>Steam's public wide art of a game (1920×620), for the big panel when Steam's cache has none.</summary>
    public static string SteamHeroUrl(string appId) => $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg";

    public static string SteamCoverUrl(string appId) => $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg";

    // ── Epic ─────────────────────────────────────────────────────────────────

    /// <summary>One Epic manifest (.item): what to show and the launcher's own link to start it; null for something not a game.</summary>
    public static PcGame? EpicGame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string name) => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var categories = r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ? cats.EnumerateArray().Select(c => c.GetString() ?? "").ToList() : [];
            if (categories.Count > 0 && !categories.Contains("games")) return null;
            if (r.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return null;
            var app = S("AppName");
            if (app.Length == 0 || S("DisplayName").Length == 0) return null;
            var link = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(S("CatalogNamespace"))}%3A{Uri.EscapeDataString(S("CatalogItemId"))}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true";
            return new PcGame($"pc:epic:{app}", Epic, S("DisplayName"), S("InstallLocation"), "", link, CoverUrl: S("CatalogItemId"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Catalog item → its tall box art, from the launcher's own catalog cache (base64 JSON).</summary>
    public static Dictionary<string, string> EpicCovers(string catcache)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(catcache.Trim()));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("keyImages", out var images)) continue;
                foreach (var image in images.EnumerateArray())
                    if (image.TryGetProperty("type", out var type) && type.GetString() is "DieselGameBoxTall" or "OfferImageTall"
                        && image.TryGetProperty("url", out var url) && url.GetString() is { } u && u.StartsWith("https://", StringComparison.Ordinal))
                    {
                        map.TryAdd(id.GetString() ?? "", u);
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            // An older or newer cache format: Epic games show their initial.
        }
        return map;
    }

    // ── Battle.net, Riot, EA (from Windows' list of programs) ────────────────

    /// <summary>Battle.net's own short codes for "launch", by the id its uninstaller uses.</summary>
    private static readonly Dictionary<string, string> BattleNetCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prometheus"] = "Pro", ["hs_beta"] = "WTCG", ["wow"] = "WoW", ["wow_classic"] = "WoWC", ["d3"] = "D3", ["fenris"] = "Fen",
        ["s2"] = "S2", ["s1"] = "S1", ["hero"] = "Hero", ["w3"] = "W3", ["anbs"] = "ANBS", ["odin"] = "ODIN", ["lazarus"] = "LAZR",
        ["viper"] = "VIPR", ["zeus"] = "ZEUS", ["fore"] = "FORE", ["auks"] = "AUKS", ["wlby"] = "WLBY", ["rtro"] = "RTRO",
    };

    private static string? Arg(string command, string name)
    {
        var m = Regex.Match(command, $@"--{Regex.Escape(name)}=(""(?<v>[^""]*)""|(?<v>\S*))");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string FirstQuoted(string command)
    {
        var m = Regex.Match(command.Trim(), "^\"(?<p>[^\"]+)\"|^(?<p>\\S+)");
        return m.Success ? m.Groups["p"].Value : "";
    }

    /// <summary>
    /// Games known from the list of installed programs: Battle.net (started through Battle.net with its code), Riot (through the
    /// Riot Client), EA (by the game's own program, which goes through the EA app).
    /// </summary>
    public static IEnumerable<PcGame> FromUninstall(IReadOnlyList<UninstallEntry> entries)
    {
        var battleNet = entries.FirstOrDefault(e => e.Key.Equals("Battle.net", StringComparison.OrdinalIgnoreCase));
        var battleExe = battleNet is null ? "" : Path.Combine(battleNet.Location, "Battle.net.exe");
        foreach (var e in entries)
        {
            if (e.Publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) && e.Uninstall.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase)
                && Arg(e.Uninstall, "uid") is { } uid && uid != "battle.net" && battleExe.Length > 0)
            {
                var args = BattleNetCodes.TryGetValue(uid, out var code) ? $"--exec=\"launch {code}\"" : "";
                yield return new PcGame($"pc:battlenet:{uid}", BattleNet, e.Name, e.Location, battleExe, args);
            }
            else if (e.Key.StartsWith("Riot Game ", StringComparison.OrdinalIgnoreCase) && Arg(e.Uninstall, "uninstall-product") is { Length: > 0 } product
                     && !product.Equals("Riot_Client", StringComparison.OrdinalIgnoreCase))
            {
                var patchline = Arg(e.Uninstall, "uninstall-patchline") ?? "live";
                var icon = IconFile(e.Icon)?.Replace('/', '\\');
                yield return new PcGame($"pc:riot:{product}.{patchline}", Riot, e.Name, e.Location.Replace('/', '\\'), FirstQuoted(e.Uninstall),
                    $"--launch-product={product} --launch-patchline={patchline}", Cover: icon is not null && File.Exists(icon) ? icon : null);
            }
            else if (e.Publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("EA app", StringComparison.OrdinalIgnoreCase)
                     && IconFile(e.Icon) is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return new PcGame($"pc:ea:{e.Key}", Ea, e.Name, e.Location, exe, "");
            }
        }
    }

    /// <summary>"C:\Game\game.exe,0" → "C:\Game\game.exe".</summary>
    public static string? IconFile(string icon)
    {
        var path = icon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 2 && !path[(comma + 1)..].Contains('\\')) path = path[..comma];
        return path.Trim('"').Length > 0 ? path.Trim('"') : null;
    }

    // ── Xbox / Microsoft Store ───────────────────────────────────────────────

    /// <summary>MicrosoftGame.config: the package name, the shown name, the logo file and the app id to start.</summary>
    public static (string Identity, string Name, string? Logo, string AppId)? XboxConfig(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
            if (identity is null) return null;
            var visuals = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
            var name = visuals?.Attribute("DefaultDisplayName")?.Value ?? identity;
            var logo = visuals?.Attribute("Square480x480Logo")?.Value ?? visuals?.Attribute("Square150x150Logo")?.Value ?? visuals?.Attribute("StoreLogo")?.Value;
            var exe = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable");
            return (identity, name, logo, exe?.Attribute("Id")?.Value ?? "Game");
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The package family name from the installed full names ("Name_1.0.0.0_x64__8wekyb3d8bbwe" → "Name_8wekyb3d8bbwe").</summary>
    public static string? FamilyName(string identity, IEnumerable<string> fullNames)
    {
        var full = fullNames.FirstOrDefault(f => f.StartsWith(identity + "_", StringComparison.OrdinalIgnoreCase) && f.Contains("__"));
        return full is null ? null : $"{identity}_{full[(full.LastIndexOf("__", StringComparison.Ordinal) + 2)..]}";
    }

    // ── Games in folders (portable, repacks) ─────────────────────────────────

    private static readonly string[] NotTheGame = ["unins", "uninstall", "setup", "crash", "report", "redist", "vc_redist", "dxsetup", "launcherhelper", "update", "dotnet", "ue4prereq", "easyanticheat", "be_service", "cleanup"];

    /// <summary>Folders that hold installers and extras, never the game: "_CommonRedist", "_Redist", "DirectX", "Bonus Content"…</summary>
    public static bool SkipFolder(string name) =>
        name.StartsWith('_') || name.Contains("redist", StringComparison.OrdinalIgnoreCase) || name.Contains("directx", StringComparison.OrdinalIgnoreCase)
        || name.Contains("bonus", StringComparison.OrdinalIgnoreCase) || name.Contains("support", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Engine", StringComparison.OrdinalIgnoreCase) || name.Contains("update-old", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A bundle of games: first-level subfolders that each have a program of their own at their top (FF1, FF2…). Each one with its
    /// program; empty when the folder is one game.
    /// </summary>
    public static IReadOnlyList<(string Folder, string Exe)> Bundle(string dir, IEnumerable<(string Path, long Size)> exes)
    {
        var list = new List<(string, string)>();
        foreach (var group in exes.Where(e => !string.Equals(Path.GetDirectoryName(e.Path), dir, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(e => FirstLevel(dir, e.Path), StringComparer.OrdinalIgnoreCase))
        {
            var top = group.Where(e => string.Equals(Path.GetDirectoryName(e.Path), group.Key, StringComparison.OrdinalIgnoreCase));
            if (MainExe(top) is { } exe) list.Add((group.Key, exe));
        }
        return list;
    }

    private static string FirstLevel(string dir, string path)
    {
        var rest = Path.GetRelativePath(dir, path);
        var cut = rest.IndexOf(Path.DirectorySeparatorChar);
        return cut < 0 ? dir : Path.Combine(dir, rest[..cut]);
    }

    /// <summary>The game's program among the .exe files of its folder: not uninstallers, setups or helpers; the biggest of the rest.</summary>
    public static string? MainExe(IEnumerable<(string Path, long Size)> exes) =>
        exes.Where(e => !NotTheGame.Any(n => Path.GetFileNameWithoutExtension(e.Path).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Path.Count(ch => ch == '\\'))
            .ThenByDescending(e => e.Size)
            .Select(e => e.Path).FirstOrDefault();
}
