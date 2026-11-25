using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Tools;

/// <summary>One running process of the person's session, as much as can be read about it.</summary>
/// <param name="Path">Null when it cannot be read (another user, or a program running as administrator).</param>
/// <param name="Description">The name the program gives itself ("Google Chrome"); null when it has none.</param>
/// <param name="Fullscreen">It has a window covering a whole screen: what a game looks like, recognised or not.</param>
public sealed record ProcessInfo(int Pid, int ParentPid, string Name, string? Path, long PrivateBytes, bool HasVisibleWindow, string? Description = null, bool Fullscreen = false);

/// <summary>
/// A program as the person thinks of it: the first process and all the ones it started
/// (Steam and its eleven web helpers are one thing to close).
/// </summary>
public sealed record AppGroup(ProcessInfo Root, IReadOnlyList<ProcessInfo> Members)
{
    public long Bytes => Members.Sum(m => m.PrivateBytes);
    public bool HasVisibleWindow => Members.Any(m => m.HasVisibleWindow);
    public string Label => string.IsNullOrWhiteSpace(Root.Description) ? Root.Name : Root.Description.Trim();
}

/// <summary>A program launched from the Strumenti page, as the person wrote it in the settings.</summary>
public sealed record Tool(string Group, string Name, string File, string Arguments, bool Admin, string Description);

/// <summary>What can be closed, and what never: pure rules, so they can be tested without a PC.</summary>
public static class AppRules
{
    private static CultureInfo Lang => L.Culture;

    /// <summary>
    /// Folds the processes into programs: a process belongs to the program of the nearest ancestor that is
    /// itself a candidate. Processes started by Windows (by Explorer, a service) are the roots.
    /// </summary>
    public static IReadOnlyList<AppGroup> Group(IReadOnlyList<ProcessInfo> processes)
    {
        var byPid = processes.ToDictionary(p => p.Pid);
        ProcessInfo RootOf(ProcessInfo p)
        {
            var current = p;
            var seen = new HashSet<int>();
            // Climb while the parent is another program of the same session, not part of Windows itself.
            while (byPid.TryGetValue(current.ParentPid, out var parent) && seen.Add(parent.Pid) && !IsWindows(parent) && !IsShell(parent))
                current = parent;
            return current;
        }
        return [.. processes
            .GroupBy(RootOf)
            .Select(g => new AppGroup(g.Key, [.. g]))];
    }

    /// <summary>
    /// The programs the person might want to close before playing, biggest first, each ticked or not.
    /// Never listed: the game being played and everything in its family (the launcher that started it),
    /// whatever has a window covering a whole screen, Windows itself, what cannot be read, what is in the
    /// protected names or folders (Discord, music, drivers, OptLife), and programs too small to matter.
    /// </summary>
    /// <param name="gamePids">Processes known to be the game right now: the strongest protection, needing no list.</param>
    public static IReadOnlyList<(AppGroup App, bool Checked)> Candidates(
        IReadOnlyList<ProcessInfo> processes, IReadOnlyList<string> protect, IReadOnlyList<string> protectFolders,
        IReadOnlyList<string> uncheck, int minMb, IReadOnlyCollection<int>? gamePids = null)
    {
        gamePids ??= [];
        bool Matches(ProcessInfo p, IReadOnlyList<string> names) =>
            names.Any(n => n.Length > 0 && (p.Name.Contains(n, StringComparison.OrdinalIgnoreCase)
                                            || (p.Description?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false)));
        bool InFolder(ProcessInfo p) =>
            p.Path is { } path && protectFolders.Any(f => f.Length > 0 && path.Contains(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        return [.. Group(processes)
            .Where(g => g.Root.Path is not null && !IsWindows(g.Root) && !IsShell(g.Root))
            // A protected name counts on the program itself, not on its helpers (nearly every program has a crash
            // reporter). A game folder counts on the whole family: a game started by a launcher keeps the launcher.
            .Where(g => !Matches(g.Root, protect) && !g.Members.Any(InFolder))
            // The game itself, by what is known about it now rather than by any list: never offered, nor its launcher.
            .Where(g => !g.Members.Any(m => gamePids.Contains(m.Pid) || m.Fullscreen))
            // Several copies of the same program started separately (eleven "dotnet") are one line to tick.
            .GroupBy(g => g.Root.Name, StringComparer.OrdinalIgnoreCase)
            .Select(same => new AppGroup(same.MaxBy(g => g.Bytes)!.Root, [.. same.SelectMany(g => g.Members)]))
            .Where(g => g.Bytes >= minMb * 1024L * 1024L)
            .OrderByDescending(g => g.Bytes)
            .Select(g => (g, !Matches(g.Root, uncheck)))];
    }

    public static ActionChoice ToChoice(AppGroup app, bool ticked, string? category = null)
    {
        var parts = new List<string> { Size(app.Bytes) };
        if (app.Members.Count > 1) parts.Add(L.T($"{app.Members.Count} processi", $"{app.Members.Count} processes"));
        if (app.HasVisibleWindow) parts.Add(L.T("finestra aperta: chiusa come col pulsante X", "window open: closed as with the X button"));
        return new ActionChoice(app.Root.Name, app.Label, string.Join(" · ", parts), ticked, category, app.Root.Path);
    }

    /// <summary>
    /// The heading a program goes under, from lines "Category | word, word": the first line with a word found in the
    /// program's name, description or folder wins (so put "signalrgb" under lighting before "signal" under chat).
    /// </summary>
    public static string Categorize(AppGroup app, IReadOnlyList<string> categories)
    {
        var haystack = $"{app.Root.Name} {app.Root.Description} {app.Root.Path}".ToLowerInvariant();
        foreach (var line in categories)
        {
            var p = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (p.Length < 2 || p[0].Length == 0) continue;
            if (p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(w => haystack.Contains(w.ToLowerInvariant())))
                return p[0];
        }
        return L.T("Altro", "Other");
    }

    /// <summary>Headings in the order of the settings, "Other" last; inside a heading, the heaviest first.</summary>
    public static IReadOnlyList<ActionChoice> Grouped(IEnumerable<(AppGroup App, bool Checked)> candidates, IReadOnlyList<string> categories)
    {
        var order = categories.Select(l => l.Split('|', 2, StringSplitOptions.TrimEntries)[0]).ToList();
        int Rank(string c) => order.IndexOf(c) is var i and >= 0 ? i : int.MaxValue;
        return [.. candidates
            .Select(c => (c.App, c.Checked, Category: Categorize(c.App, categories)))
            .OrderBy(c => Rank(c.Category)).ThenByDescending(c => c.App.Bytes)
            .Select(c => ToChoice(c.App, c.Checked, c.Category))];
    }

    public static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{(bytes / (1024d * 1024 * 1024)).ToString("0.0", Lang)} GB"
        : $"{bytes / (1024 * 1024)} MB";

    /// <summary>"gruppo | nome | file | argomenti | amministratore | descrizione": lines that do not read are skipped.</summary>
    public static IReadOnlyList<Tool> ParseTools(IReadOnlyList<string> lines) =>
        [.. lines
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Where(p => p.Length >= 3 && p[1].Length > 0 && p[2].Length > 0)
            .Select(p => new Tool(
                p[0].Length > 0 ? p[0] : L.T("Strumenti", "Tools"), p[1], p[2].Trim('"'),
                p.Length > 3 ? p[3] : "",
                p.Length > 4 && p[4].ToLowerInvariant() is "sì" or "si" or "yes" or "admin" or "amministratore",
                p.Length > 5 ? p[5] : ""))];

    private static bool IsWindows(ProcessInfo p) =>
        p.Path is { } path && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    // Explorer is a parent of most programs the person opens: it is never part of them.
    private static bool IsShell(ProcessInfo p) => p.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase);
}
