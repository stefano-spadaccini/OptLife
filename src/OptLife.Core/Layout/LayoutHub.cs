using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptLife.Core.Layout;

/// <summary>
/// One step of a Hub reorganisation: a folder renamed or moved (same drive, instant), a shortcut moved, a new section made, an
/// emptied section sent to the Recycle Bin.
/// </summary>
/// <param name="Kind">"folder", "file", "create" or "remove".</param>
public sealed record HubOp(string Kind, string From, string To = "");

/// <summary>A Hub reorganisation as done, kept to undo it: the steps in the order they ran.</summary>
/// <param name="OldSections">The section names the layout had before, put back by the undo.</param>
public sealed record HubChange(string Id, DateTimeOffset At, IReadOnlyList<HubOp> Done, IReadOnlyList<string> OldSections);

/// <summary>
/// The Hub's sections renamed or regrouped to follow the layout: the plan (from a prepared file, or by section number when only
/// the names change), its description, and the texts outside OptLife that name the old folders (to fix by hand).
/// </summary>
public static class LayoutHub
{
    public const string PlanFile = "hub-plan.json";

    /// <summary>
    /// The plan when nothing was prepared: each section whose number is in the new list gets the new name ("1 Giochi" → "1 Games").
    /// Sections whose number is not in the new list stay as they are.
    /// </summary>
    public static IReadOnlyList<HubOp> ByNumber(string hub, IEnumerable<string> existing, IReadOnlyList<string> wanted)
    {
        var ops = new List<HubOp>();
        foreach (var folder in existing)
        {
            var number = Number(folder);
            if (number is null) continue;
            var target = wanted.FirstOrDefault(w => Number(w) == number);
            if (target is null || target.Equals(folder, StringComparison.Ordinal)) continue;
            ops.Add(new HubOp("folder", Path.Combine(hub, folder), Path.Combine(hub, target)));
        }
        foreach (var w in wanted.Where(w => !existing.Any(e => Number(e) == Number(w) && Number(w) is not null) && !existing.Contains(w, StringComparer.OrdinalIgnoreCase)))
            ops.Add(new HubOp("create", Path.Combine(hub, w)));
        return ops;
    }

    private static string? Number(string name) => Regex.Match(name, @"^\d+") is { Success: true } m ? m.Value : null;

    public static IReadOnlyList<HubOp>? Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, PlanFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<HubOp>>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A step in words, short: "1 Giochi → 1 Games", "Discord.lnk → 3 Chat".</summary>
    public static string Describe(HubOp op, string hub)
    {
        string Rel(string p) => Path.GetRelativePath(hub, p);
        return op.Kind switch
        {
            "folder" => L.T($"Cartella {Rel(op.From)} → {Rel(op.To)}", $"Folder {Rel(op.From)} → {Rel(op.To)}"),
            "file" => L.T($"{Path.GetFileName(op.From)}: {Rel(Path.GetDirectoryName(op.From)!)} → {Rel(Path.GetDirectoryName(op.To)!)}",
                          $"{Path.GetFileName(op.From)}: {Rel(Path.GetDirectoryName(op.From)!)} → {Rel(Path.GetDirectoryName(op.To)!)}"),
            "create" => L.T($"Nuova sezione {Rel(op.From)}", $"New section {Rel(op.From)}"),
            "remove" => L.T($"{Rel(op.From)}, rimasta vuota, va nel Cestino", $"{Rel(op.From)}, left empty, goes to the Recycle Bin"),
            _ => op.Kind,
        };
    }

    /// <summary>The step that undoes another.</summary>
    public static HubOp Reverse(HubOp op) => op.Kind switch
    {
        "folder" or "file" => new HubOp(op.Kind, op.To, op.From),
        "create" => new HubOp("remove", op.From),
        "remove" => new HubOp("create", op.From),
        _ => op,
    };

    /// <summary>
    /// Text files (guides, scripts, notes) that name one of the folders being renamed: OptLife does not rewrite other people's files,
    /// it lists them so the person can fix them. Searched in the given folders, small text files only.
    /// </summary>
    public static IReadOnlyList<(string File, string Mentions)> MentionsOutside(IEnumerable<string> searchIn, IEnumerable<string> oldFolders)
    {
        var names = oldFolders.Select(f => Path.GetFileName(f.TrimEnd('\\'))).Where(n => n.Length > 0).Distinct().ToList();
        var found = new List<(string, string)>();
        foreach (var dir in searchIn.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md" or ".ps1" or ".cmd" or ".bat" or ".ini" or ".json" or ".vbs");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                try
                {
                    if (new FileInfo(file).Length > 512 * 1024) continue;
                    var text = File.ReadAllText(file);
                    var hits = names.Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count > 0) found.Add((file, string.Join(", ", hits)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return found;
    }
}
