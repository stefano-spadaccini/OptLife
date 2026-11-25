using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Cleanup;

/// <summary>Something on disk with its weight and the last time anything in it changed.</summary>
public sealed record DiskItem(string Path, long Bytes, DateTimeOffset Modified)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public double Gb => Bytes / (1024d * 1024 * 1024);
}

/// <param name="Id">Where it is registered ("HKCU:Discord", "folder:Steam.lnk"): stable across checks.</param>
/// <param name="Removable">Whether it can be taken out without administrator rights.</param>
public sealed record StartupEntry(string Id, string Name, string Command, bool Removable);

/// <summary>"folder | allowed names, comma separated | where to move the rest (optional)".</summary>
public sealed record TidyRule(string Folder, IReadOnlyList<string> Allowed, string? MoveTo)
{
    public static TidyRule? Parse(string line)
    {
        var p = CleanupRules.Split(line);
        return p.Length >= 2 && p[0].Length > 0
            ? new TidyRule(p[0], CleanupRules.Names(p[1]), p.Length > 2 && p[2].Length > 0 ? p[2] : null)
            : null;
    }
}

/// <summary>"name | root | folder names | days | sibling that must be there (optional)".</summary>
public sealed record RebuildRule(string Name, string Root, IReadOnlyList<string> Folders, int Days, string? Marker)
{
    public static RebuildRule? Parse(string line)
    {
        var p = CleanupRules.Split(line);
        return p.Length >= 4 && p[0].Length > 0 && p[1].Length > 0 && CleanupRules.Names(p[2]).Count > 0 && int.TryParse(p[3], out var days)
            ? new RebuildRule(p[0], p[1], CleanupRules.Names(p[2]), days, p.Length > 4 && p[4].Length > 0 ? p[4] : null)
            : null;
    }
}

/// <summary>
/// Whether a shortcut opens nothing any more, from what it holds. Pure, so every kind can be tested: a file or folder, an app of
/// the Apps folder (kept as a shell item with no path, or as "explorer.exe shell:AppsFolder\&lt;id&gt;"), a web address.
/// </summary>
public static class ShortcutCheck
{
    private const string AppsFolder = "shell:AppsFolder\\";

    /// <param name="target">The file it starts, expanded; empty when it keeps only a shell item.</param>
    /// <param name="appsFolderItem">The app id the shell reads from a shortcut with no file ("4DF9E0F8.Netflix_mcm4njqhnhss8!Netflix.App"), or null.</param>
    /// <param name="familyInstalled">Whether a Store package family is installed for the person.</param>
    public static bool IsBroken(string target, string arguments, string? appsFolderItem,
        Func<string, bool> familyInstalled, Func<string, bool> exists, Func<string, bool> driveThere)
    {
        var app = appsFolderItem is { Length: > 0 } item ? item
            : arguments.Trim().Trim('"') is var a && a.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) ? a[AppsFolder.Length..] : null;
        if (app is not null)
        {
            // "<family>!<app>": gone only when that package is no longer installed. An id without a family (a desktop program
            // listed in the Apps folder) is not a package: nothing to judge.
            var family = app.Split('!')[0].Trim().Trim('"');
            return family.Contains('_') && !familyInstalled(family);
        }
        // A web address, an unreadable target, the network, a drive not plugged in now: not broken (it may come back).
        if (target.Contains("://", StringComparison.Ordinal) || target.Length < 3 || target.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var drive = Path.GetPathRoot(target);
        if (string.IsNullOrEmpty(drive) || !driveThere(drive)) return false;
        return !exists(target);
    }
}

/// <summary>Turns what was found on disk into problems. Pure: touches nothing.</summary>
public static class CleanupRules
{
    private static CultureInfo Lang => L.Culture;

    /// <summary>A short stable name for a set (of paths): the same set keeps the same problem, a new one opens a new one.</summary>
    public static string Hash(IEnumerable<string> parts) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts.Select(p => p.ToLowerInvariant()).Order(StringComparer.Ordinal)))))[..12].ToLowerInvariant();

    public static string[] Split(string line) => line.Split('|', StringSplitOptions.TrimEntries);

    public static IReadOnlyList<string> Names(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Size(double gb) => gb >= 10 ? $"{gb.ToString("0", Lang)} GB" : $"{gb.ToString("0.0", Lang)} GB";

    private static string FolderName(string path) =>
        Path.GetFileName(Environment.ExpandEnvironmentVariables(path).TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

    private static string Listed(IEnumerable<string> names, int show = 8)
    {
        var all = names.ToList();
        return string.Join(", ", all.Take(show)) + (all.Count > show ? L.T($" e altri {all.Count - show}", $" and {all.Count - show} more") : "");
    }

    /// <summary>What sits in a folder that should hold only a few known things (a Desktop with just one folder on it).</summary>
    public static IReadOnlyList<string> Loose(TidyRule rule, IReadOnlyList<string> found)
    {
        // The place loose things are moved to may be inside the folder itself: it is allowed by definition.
        var inbox = rule.MoveTo is null ? null : Path.GetFileName(rule.MoveTo.TrimEnd('\\', '/'));
        return [.. found.Where(n => !rule.Allowed.Any(a => Matches(n, a)) && !n.Equals(inbox, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>A name against an allowed one: the same name, or a pattern with * and ? ("*.lnk" = every shortcut). Case does not matter.</summary>
    public static bool Matches(string name, string allowed)
    {
        if (!allowed.Contains('*') && !allowed.Contains('?')) return name.Equals(allowed, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(allowed).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>"folder | how many things at most at its top": a Desktop that fills up.</summary>
    public static NewFinding? Crowded(string folder, int most, IReadOnlyList<string> found)
    {
        var count = found.Count(n => !n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
        if (most <= 0 || count <= most) return null;
        var name = Environment.ExpandEnvironmentVariables(folder);
        return new NewFinding($"crowded|{folder.ToLowerInvariant()}", Severity.Low,
            L.T($"In {name} ci sono {count} elementi", $"{name} holds {count} items"),
            L.T($"Il limite che hai scelto è {most}. Un Desktop pieno si legge male e rallenta l'accesso a Windows.", $"The limit you chose is {most}. A full Desktop is hard to read and slows down signing in to Windows."),
            L.T("Sposta in Documenti ciò che non usi spesso. Per farlo fare a OptLife: Ordine e spazio → Organizzazione, Desktop pulito o Hub per categorie.",
                "Move to Documents what you do not use often. To have OptLife do it: Order and space → Organisation, Clean Desktop or Hub by category."));
    }

    public static NewFinding? Tidy(TidyRule rule, IReadOnlyList<string> found)
    {
        var loose = Loose(rule, found);
        if (loose.Count == 0) return null;
        // The whole path: "Desktop" alone could be the person's own or the one shared by all users.
        var name = Environment.ExpandEnvironmentVariables(rule.Folder);
        return new NewFinding($"tidy|{rule.Folder.ToLowerInvariant()}", Severity.Low,
            loose.Count == 1 ? L.T($"In {name} c'è 1 elemento fuori posto", $"{name} has 1 item out of place") : L.T($"In {name} ci sono {loose.Count} elementi fuori posto", $"{name} has {loose.Count} items out of place"),
            // The whole list when they can be moved: it is what the button will move, read before pressing it.
            L.T($"Dovrebbe contenere solo: {string.Join(", ", rule.Allowed)}. In più ci sono: {Listed(loose, rule.MoveTo is null ? 8 : 40)}.", $"It should hold only: {string.Join(", ", rule.Allowed)}. On top of that there are: {Listed(loose, rule.MoveTo is null ? 8 : 40)}."),
            rule.MoveTo is null ? L.T("Sposta o elimina ciò che non serve.", "Move or delete what is not needed.") : L.T($"Il pulsante sposta proprio questi in {Environment.ExpandEnvironmentVariables(rule.MoveTo)}, da dove li sistemi con calma: non viene cancellato niente, e Ordine e spazio → Collegamenti → ANNULLA li rimette dov'erano.", $"The button moves exactly these to {Environment.ExpandEnvironmentVariables(rule.MoveTo)}, where you sort them out calmly: nothing is deleted, and Order and space → Shortcuts → UNDO puts them back."),
            rule.MoveTo is null ? null : new FindingFix(CleanupModule.MoveLoose, L.T("Spostali", "Move them"), rule.Folder));
    }

    /// <summary>What appeared in a folder since it was first looked at. Each new thing is its own problem, so it can be accepted on its own.</summary>
    public static IEnumerable<NewFinding> Newcomers(string folder, string? hint, IReadOnlyList<string> reference, IReadOnlyList<string> found)
    {
        foreach (var name in found.Where(n => !reference.Contains(n, StringComparer.OrdinalIgnoreCase)))
            yield return new NewFinding($"new|{folder.ToLowerInvariant()}|{name.ToLowerInvariant()}", Severity.Low,
                L.T($"Nuovo in {folder}: {name}", $"New in {folder}: {name}"),
                L.T($"\"{name}\" è comparso in {folder} dopo l'ultima volta che l'ho guardata.", $"\"{name}\" appeared in {folder} since I last looked at it."),
                (hint is { Length: > 0 } ? hint + " " : "") + L.T("Se va bene dov'è, ignora questo avviso.", "If it is fine where it is, ignore this warning."));
    }

    public static NewFinding? BrokenLinks(string folder, IReadOnlyList<string> broken) =>
        broken.Count == 0 ? null : new NewFinding($"links|{folder.ToLowerInvariant()}", Severity.Low,
            broken.Count == 1 ? L.T($"1 collegamento rotto in {FolderName(folder)}", $"1 broken shortcut in {FolderName(folder)}") : L.T($"{broken.Count} collegamenti rotti in {FolderName(folder)}", $"{broken.Count} broken shortcuts in {FolderName(folder)}"),
            L.T($"Puntano a qualcosa che non esiste più: {Listed(broken, 6)}.", $"They point to something that no longer exists: {Listed(broken, 6)}."),
            L.T("Aggiorna il collegamento o toglilo.", "Update the shortcut or remove it."),
            new FindingFix(CleanupModule.OpenFolder, L.T("Apri la cartella", "Open the folder"), folder));

    /// <param name="minGb">What is at least this heavy is worth a look whatever its age.</param>
    /// <param name="days">What has not been touched for this long is worth a look even if lighter.</param>
    public static NewFinding? Heavy(string folder, double minGb, int days, IReadOnlyList<DiskItem> items, DateTimeOffset now)
    {
        var worth = items
            .Where(i => i.Gb >= minGb || (i.Gb >= 0.1 && now - i.Modified >= TimeSpan.FromDays(days)))
            .OrderByDescending(i => i.Bytes)
            .ToList();
        if (worth.Count == 0) return null;
        var total = worth.Sum(i => i.Gb);
        return new NewFinding($"heavy|{folder.ToLowerInvariant()}", Severity.Info,
            L.T($"{FolderName(folder)}: {(worth.Count == 1 ? "1 elemento" : $"{worth.Count} elementi")} da valutare, {Size(total)}", $"{FolderName(folder)}: {(worth.Count == 1 ? "1 item" : $"{worth.Count} items")} to review, {Size(total)}"),
            L.T("I più pesanti: ", "The heaviest: ") + string.Join("; ", worth.Take(5).Select(i => $"{i.Name} ({Size(i.Gb)}, {L.T("fermo da", "untouched for")} {(int)(now - i.Modified).TotalDays} {L.T("giorni", "days")})")) + ".",
            L.T("Decidi tu cosa tenere: qui non viene tolto niente in automatico.", "You decide what to keep: nothing is removed automatically here."),
            new FindingFix(CleanupModule.OpenFolder, L.T("Apri la cartella", "Open the folder"), folder));
    }

    /// <summary>Folders that a program rebuilds by itself (compiled output, caches) and that nobody has touched for a while.</summary>
    public static IReadOnlyList<DiskItem> Stale(RebuildRule rule, IReadOnlyList<DiskItem> found, DateTimeOffset now) =>
        [.. found.Where(i => now - i.Modified >= TimeSpan.FromDays(rule.Days)).OrderByDescending(i => i.Bytes)];

    public static NewFinding? Rebuildable(RebuildRule rule, IReadOnlyList<DiskItem> found, double minGb, DateTimeOffset now)
    {
        var stale = Stale(rule, found, now);
        var total = stale.Sum(i => i.Gb);
        if (stale.Count == 0 || total < minGb) return null;
        return new NewFinding($"rebuild|{rule.Name.ToLowerInvariant()}", Severity.Info,
            L.T($"{rule.Name}: si possono liberare {Size(total)}", $"{rule.Name}: {Size(total)} can be freed"),
            L.T($"{(stale.Count == 1 ? "1 cartella che si rigenera da sola" : $"{stale.Count} cartelle che si rigenerano da sole")}, ferme da almeno {rule.Days} giorni. ", $"{(stale.Count == 1 ? "1 self-rebuilding folder" : $"{stale.Count} self-rebuilding folders")}, untouched for at least {rule.Days} days. ") +
            L.T("Le più pesanti: ", "The heaviest: ") + string.Join("; ", stale.Take(5).Select(i => $"{i.Path} ({Size(i.Gb)})")) + ".",
            L.T("Finiscono nel Cestino, da cui si possono riprendere; il programma che le usa le ricrea alla prossima apertura, mettendoci un po' di più la prima volta.", "They go to the Recycle Bin, where they can be restored; the program using them recreates them next time it opens, taking a little longer the first time."),
            new FindingFix(CleanupModule.Recycle, L.T("Sposta nel Cestino", "Send to the Recycle Bin"), rule.Name));
    }

    /// <param name="reference">The entries that were there the first time; null on that first time.</param>
    /// <param name="unwanted">Names the person never wants at startup, however often a program puts itself back.</param>
    public static IEnumerable<NewFinding> Startup(IReadOnlyList<StartupEntry> entries, IReadOnlyList<string>? reference, IReadOnlyList<string> unwanted)
    {
        foreach (var e in entries)
        {
            if (unwanted.Any(u => e.Name.Contains(u, StringComparison.OrdinalIgnoreCase)))
                yield return new NewFinding($"unwanted|{e.Id.ToLowerInvariant()}", Severity.Low,
                    L.T($"{e.Name} si è rimesso tra i programmi all'avvio", $"{e.Name} put itself back among the start-up programs"),
                    L.T($"È tra quelli che non vuoi all'avvio. Comando: {e.Command}", $"It is one you do not want at start-up. Command: {e.Command}"),
                    e.Removable ? L.T("Si può togliere di nuovo.", "It can be removed again.") : L.T("È registrato per tutti gli utenti: per toglierlo serve l'amministratore (Gestione attività → Avvio).", "It is registered for all users: removing it needs administrator rights (Task Manager → Startup)."),
                    e.Removable ? new FindingFix(CleanupModule.RemoveUnwanted, "Toglilo", e.Id) : null, FindingArea.Software);
            else if (reference is not null && !reference.Contains(e.Id, StringComparer.OrdinalIgnoreCase))
                yield return new NewFinding($"startup|{e.Id.ToLowerInvariant()}", Severity.Low,
                    L.T($"Nuovo programma all'avvio: {e.Name}", $"New start-up program: {e.Name}"),
                    L.T($"Da poco parte insieme a Windows. Comando: {e.Command}", $"It recently started launching with Windows. Command: {e.Command}"),
                    L.T("Se lo vuoi all'avvio, ignora questo avviso. ", "If you want it at start-up, ignore this warning. ") + (e.Removable ? "" : L.T("Per toglierlo serve l'amministratore (Gestione attività → Avvio).", "Removing it needs administrator rights (Task Manager → Startup).")),
                    e.Removable ? new FindingFix(CleanupModule.RemoveStartup, L.T("Toglilo dall'avvio", "Remove it from start-up"), e.Id) : null, FindingArea.Software);
        }
    }

    /// <param name="before">The size some weeks ago (the oldest one remembered); null when there is no memory that old.</param>
    public static NewFinding? Growth(DiskItem item, double? before, int days, double growthGb) =>
        before is { } was && item.Gb - was >= growthGb
            ? new NewFinding($"growth|{item.Path.ToLowerInvariant()}", Severity.Low,
                L.T($"{item.Path} è cresciuta di {Size(item.Gb - was)} in {days} giorni", $"{item.Path} grew by {Size(item.Gb - was)} in {days} days"),
                L.T($"Era {Size(was)}, ora è {Size(item.Gb)}.", $"It was {Size(was)}, now it is {Size(item.Gb)}."),
                L.T("Se è un gioco o un progetto nuovo è normale: ignora l'avviso. Altrimenti guarda cosa c'è dentro.", "If it is a new game or project it is normal: ignore the warning. Otherwise look at what is inside."),
                new FindingFix(CleanupModule.OpenFolder, L.T("Apri la cartella", "Open the folder"), item.Path))
            : null;
}
