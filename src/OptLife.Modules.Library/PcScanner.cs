namespace OptLife.Modules.Library;

/// <summary>Finds the PC games every launcher has installed, from the files and registry keys the launchers themselves keep.</summary>
internal static class PcScanner
{
    public static List<PcGame> Find(ILibraryEnvironment env, IEnumerable<string> folders, ISet<string> hidden)
    {
        var games = new List<PcGame>();
        void Add(IEnumerable<PcGame> found)
        {
            foreach (var g in found)
                if (!hidden.Contains(g.Id) && !games.Any(x => x.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase))) games.Add(g);
        }
        Add(Steam(env));
        Add(Epic(env));
        Add(PcRules.FromUninstall(env.Uninstall()));
        Add(Ubisoft(env));
        Add(Gog(env));
        Add(Xbox(env));
        // Games in folders last: a folder a launcher already knows is that launcher's game.
        var known = games.Select(g => g.Folder.TrimEnd('\\')).Where(f => f.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Add(InFolders(folders).Where(g => !known.Contains(g.Folder.TrimEnd('\\'))));
        return games;
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string[] Files(string dir, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, option) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] Dirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<PcGame> Steam(ILibraryEnvironment env)
    {
        if (env.SteamFolder() is not { } steam) yield break;
        var libraries = Read(Path.Combine(steam, "steamapps", "libraryfolders.vdf")) is { } vdf ? PcRules.SteamLibraries(vdf).ToList() : [];
        if (!libraries.Contains(steam, StringComparer.OrdinalIgnoreCase)) libraries.Insert(0, steam);
        var steamExe = Path.Combine(steam, "steam.exe");
        foreach (var library in libraries)
            foreach (var manifest in Files(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
            {
                if (Read(manifest) is not { } acf || PcRules.SteamApp(acf) is not { } app || PcRules.LooksLikeTool(app.Name)) continue;
                var folder = Path.Combine(library, "steamapps", "common", app.InstallDir);
                yield return new PcGame($"pc:steam:{app.Id}", PcRules.Steam, app.Name, folder, env.Explorer(), $"\"steam://rungameid/{app.Id}\"",
                    Cover: SteamCover(steam, app.Id), CoverUrl: PcRules.SteamCoverUrl(app.Id), LastPlayed: PcRules.SteamLastPlayed(acf),
                    Wide: Files(Path.Combine(steam, "appcache", "librarycache", app.Id), "library_hero.jpg", SearchOption.AllDirectories).FirstOrDefault());
            }
    }

    /// <summary>The tall box art Steam already keeps (new layout: librarycache\&lt;id&gt;\&lt;hash&gt;\library_capsule.jpg; old: &lt;id&gt;_library_600x900.jpg).</summary>
    private static string? SteamCover(string steam, string id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var old = Path.Combine(cache, $"{id}_library_600x900.jpg");
        if (File.Exists(old)) return old;
        return Files(Path.Combine(cache, id), "library_600x900.jpg", SearchOption.AllDirectories).FirstOrDefault()
               ?? Files(Path.Combine(cache, id), "library_capsule.jpg", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static IEnumerable<PcGame> Epic(ILibraryEnvironment env)
    {
        var data = Path.Combine(env.ProgramData(), "Epic", "EpicGamesLauncher", "Data");
        var covers = Read(Path.Combine(data, "Catalog", "catcache.bin")) is { } cache ? PcRules.EpicCovers(cache) : [];
        foreach (var item in Files(Path.Combine(data, "Manifests"), "*.item"))
        {
            if (Read(item) is not { } json || PcRules.EpicGame(json) is not { } game) continue;
            if (game.Folder.Length > 0 && !Directory.Exists(game.Folder)) continue;
            // CoverUrl holds the catalog item until here: it becomes the box art's address.
            yield return game with { Exe = env.Explorer(), Args = $"\"{game.Args}\"", CoverUrl = covers.GetValueOrDefault(game.CoverUrl ?? "") };
        }
    }

    private static IEnumerable<PcGame> Ubisoft(ILibraryEnvironment env)
    {
        foreach (var (id, folder) in env.UbisoftInstalls())
        {
            if (!Directory.Exists(folder)) continue;
            yield return new PcGame($"pc:ubisoft:{id}", PcRules.Ubisoft, Path.GetFileName(folder), folder, env.Explorer(), $"\"uplay://launch/{id}/0\"");
        }
    }

    private static IEnumerable<PcGame> Gog(ILibraryEnvironment env)
    {
        var client = env.GogClient();
        foreach (var (id, name, folder, exe) in env.GogGames())
        {
            if (!Directory.Exists(folder)) continue;
            yield return client is not null && File.Exists(client)
                ? new PcGame($"pc:gog:{id}", PcRules.Gog, name, folder, client, $"/command=runGame /gameId={id} /path=\"{folder}\"")
                : new PcGame($"pc:gog:{id}", PcRules.Gog, name, folder, exe, "");
        }
    }

    /// <summary>Xbox app games live in &lt;disk&gt;\XboxGames\&lt;game&gt;\Content with a MicrosoftGame.config; started as Store apps.</summary>
    private static IEnumerable<PcGame> Xbox(ILibraryEnvironment env)
    {
        var packages = env.PackageFullNames();
        foreach (var drive in env.FixedDrives())
            foreach (var dir in Dirs(Path.Combine(drive, "XboxGames")))
            {
                var content = Path.Combine(dir, "Content");
                var config = Files(content, "MicrosoftGame.config").FirstOrDefault();
                if (config is null || Read(config) is not { } xml || PcRules.XboxConfig(xml) is not { } game) continue;
                if (PcRules.FamilyName(game.Identity, packages) is not { } family) continue;
                var logo = game.Logo is { } l && File.Exists(Path.Combine(content, l)) ? Path.Combine(content, l) : null;
                yield return new PcGame($"pc:xbox:{game.Identity}", PcRules.Xbox, game.Name, content, env.Explorer(), $"\"shell:AppsFolder\\{family}!{game.AppId}\"", Cover: logo);
            }
    }

    /// <summary>
    /// One game per subfolder of the chosen folders, started by its own program, looked for up to 4 levels down (repacks keep it in
    /// a subfolder, Unreal games in Binaries\Win64). A bundle of games (one subfolder each with its program, like FF I-VI) gives one
    /// game per subfolder.
    /// </summary>
    private static IEnumerable<PcGame> InFolders(IEnumerable<string> folders)
    {
        foreach (var root in folders)
            foreach (var dir in Dirs(root))
            {
                var exes = Exes(dir, 4).Select(p => (p, Size: SizeOf(p))).ToList();
                var top = exes.Where(e => Path.GetDirectoryName(e.p)!.Equals(dir, StringComparison.OrdinalIgnoreCase)).ToList();
                if (PcRules.MainExe(top) is null && PcRules.Bundle(dir, exes) is { Count: >= 2 } parts)
                {
                    foreach (var (sub, exe) in parts)
                        yield return new PcGame($"pc:folder:{Hash(sub)}", PcRules.Folder, Path.GetFileNameWithoutExtension(exe), sub, exe, "");
                    continue;
                }
                if (PcRules.MainExe(exes) is not { } main) continue;
                yield return new PcGame($"pc:folder:{Hash(dir)}", PcRules.Folder, Path.GetFileName(dir), dir, main, "");
            }
    }

    private static string Hash(string dir) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..10];

    /// <summary>Programs down to <paramref name="depth"/> levels, skipping the folders of installers and extras.</summary>
    private static IEnumerable<string> Exes(string dir, int depth)
    {
        foreach (var exe in Files(dir, "*.exe")) yield return exe;
        if (depth <= 0) yield break;
        foreach (var sub in Dirs(dir).Where(d => !PcRules.SkipFolder(Path.GetFileName(d))))
            foreach (var exe in Exes(sub, depth - 1)) yield return exe;
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
