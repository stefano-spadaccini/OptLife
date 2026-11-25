using System.Text.Json;
using System.Text.Json.Serialization;
using OptLife.Core.Config;

namespace OptLife.Core.Layout;

/// <summary>A folder of the layout, with what goes in it. "each" repeats it for every entry of a list variable ({section}).</summary>
/// <param name="Presets">The ways of keeping the Desktop (<see cref="DesktopPreset"/>) this place belongs to; null = all of them.</param>
public sealed record LayoutPlace(string Path, string? It = null, string? En = null, string? Each = null, IReadOnlyList<string>? Presets = null);

/// <summary>
/// One setting of a module the layout wants: a line added to a list ("line") or a single value ("value").
/// The module is named as data: when that module is not installed the rule is simply left out.
/// </summary>
/// <param name="Presets">The ways of keeping the Desktop this rule belongs to; null = all of them.</param>
public sealed record LayoutRule(string Module, string Key, string? Line = null, string? Value = null, string? It = null, string? En = null, string? SkipIfSame = null,
    IReadOnlyList<string>? Presets = null);

/// <summary>
/// How the Desktop is kept, chosen in the guided tour and changeable later: only warnings (the default: nothing is moved), a clean
/// Desktop (only shortcuts on it, the rest into a folder), a Hub by category (one folder with sections), or custom (the person's
/// own rules, the layout adds none for the Desktop).
/// </summary>
public static class DesktopPreset
{
    public const string Report = "report", Clean = "clean", Hub = "hub", Custom = "custom";

    public static string[] Ids => [Report, Clean, Hub, Custom];

    /// <summary>The words of the setting, in the order of <see cref="Ids"/> (the first is the default).</summary>
    public static string[] Names =>
    [
        L.T("Solo segnalazioni", "Warnings only"), L.T("Desktop pulito", "Clean Desktop"),
        L.T("Hub per categorie", "Hub by category"), L.T("Personalizzato", "Custom"),
    ];

    public static string IdOf(string name) => Array.IndexOf(Names, name) is var i and >= 0 ? Ids[i] : Report;

    public static string NameOf(string id) => Array.IndexOf(Ids, id) is var i and >= 0 ? Names[i] : Names[0];

    public static bool Fits(IReadOnlyList<string>? presets, string preset) => presets is null || presets.Contains(preset, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A way to organise a PC: where every kind of thing goes (places) and the module settings that keep it that way (rules), written
/// with variables ({games}, {software}…) so it fits any PC. The person's choices for the variables travel with it when exported.
/// </summary>
public sealed record LayoutTemplate(string Name, IReadOnlyList<LayoutPlace> Places, IReadOnlyList<LayoutRule> Rules,
    IReadOnlyDictionary<string, string>? Variables = null, IReadOnlyList<string>? HubSections = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The template that ships with OptLife: generic, valid for anyone.</summary>
    public static LayoutTemplate Default
    {
        get
        {
            using var stream = typeof(LayoutTemplate).Assembly.GetManifestResourceStream("OptLife.Core.Layout.default-layout.json")!;
            return JsonSerializer.Deserialize<LayoutTemplate>(stream, Json)!;
        }
    }

    public static LayoutTemplate Parse(string json)
    {
        try
        {
            var t = JsonSerializer.Deserialize<LayoutTemplate>(json, Json);
            if (t is null || t.Places is null || t.Rules is null) throw new JsonException();
            return t;
        }
        catch (JsonException)
        {
            throw new FormatException(L.T("Il file non è un modello di organizzazione di OptLife.", "The file is not an OptLife layout."));
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>A place with its variables filled in.</summary>
public sealed record ResolvedPlace(string Path, string What);

/// <summary>A rule with its variables filled in.</summary>
/// <param name="PresetBound">The rule belongs to some ways of keeping the Desktop only: it is the one replaced when the way changes.</param>
public sealed record ResolvedRule(string Module, string Key, string? Line, string? Value, string What, bool PresetBound = false);

/// <summary>The person's choices: every variable has a value here (the automatic ones already worked out).</summary>
/// <param name="Preset">How the Desktop is kept (<see cref="DesktopPreset"/>): it decides which places and rules of the layout count.</param>
public sealed record LayoutChoices(string Games, string Software, string Emulators, string Backups, string Hub, IReadOnlyList<string> HubSections,
    string Preset = DesktopPreset.Hub)
{
    public string HubPath => $@"%USERPROFILE%\Desktop\{Hub}";
    public string InboxName => HubSections.FirstOrDefault() ?? L.T("Da sistemare", "To sort");
    public string Inbox => $@"{HubPath}\{InboxName}";

    /// <summary>With a clean Desktop, where what is not a shortcut goes: a folder in Documents, out of sight but easy to find.</summary>
    public static string DesktopInbox => $@"%USERPROFILE%\Documents\{L.T("Dal Desktop", "From the Desktop")}";
    public static string SystemDrive => Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } d ? d : "C:";
    public string GamesDrive => LayoutPaths.Drive(Games) ?? SystemDrive;

    public IReadOnlyDictionary<string, string> Variables => new Dictionary<string, string>
    {
        ["games"] = Games, ["software"] = Software, ["emulators"] = Emulators, ["backups"] = Backups, ["hub"] = Hub,
        ["hubPath"] = HubPath, ["inbox"] = Inbox, ["inboxName"] = InboxName, ["systemDrive"] = SystemDrive, ["gamesDrive"] = GamesDrive,
        ["desktopInbox"] = DesktopInbox,
    };

    /// <summary>The same choices with another way of keeping the Desktop (to see what a change of preset takes away).</summary>
    public LayoutChoices With(string preset) => this with { Preset = preset };

    public string Fill(string text)
    {
        foreach (var (k, v) in Variables) text = text.Replace("{" + k + "}", v, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    public IReadOnlyList<ResolvedPlace> Places(LayoutTemplate t)
    {
        var list = new List<ResolvedPlace>();
        foreach (var p in t.Places.Where(p => DesktopPreset.Fits(p.Presets, Preset)))
        {
            var what = L.T(p.It ?? "", p.En ?? p.It ?? "");
            if (p.Each == "hubSections")
            {
                foreach (var s in HubSections.Where(s => s.Trim().Length > 0))
                    list.Add(new ResolvedPlace(Fill(p.Path.Replace("{section}", s.Trim())), what));
                continue;
            }
            // A place whose variable is empty (no backup folder chosen) is not part of this PC's layout.
            if (p.Path.Contains("{backups}", StringComparison.OrdinalIgnoreCase) && Backups.Trim().Length == 0) continue;
            list.Add(new ResolvedPlace(Fill(p.Path), what));
        }
        return [.. list.GroupBy(p => LayoutPaths.Key(p.Path)).Select(g => g.First())];
    }

    public IReadOnlyList<ResolvedRule> Rules(LayoutTemplate t) =>
        [.. t.Rules.Where(r => DesktopPreset.Fits(r.Presets, Preset))
            .Where(r => r.SkipIfSame is not { } same || Fill(same).Split('|') is not [var a, var b] || !a.Equals(b, StringComparison.OrdinalIgnoreCase))
            .Select(r => new ResolvedRule(r.Module, r.Key, r.Line is null ? null : Fill(r.Line), r.Value is null ? null : Fill(r.Value), Fill(L.T(r.It ?? r.Key, r.En ?? r.It ?? r.Key)),
                r.Presets is not null))];
}

/// <summary>Paths as people write them (with %USERPROFILE%, trailing backslashes, any case), compared as Windows sees them.</summary>
public static class LayoutPaths
{
    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    /// <summary>For comparing: expanded, no trailing backslash (except a drive root), lower case.</summary>
    public static string Key(string path)
    {
        var p = Expand(path).Replace('/', '\\');
        if (p.Length > 3) p = p.TrimEnd('\\');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p.ToLowerInvariant();
    }

    public static bool Same(string a, string b) => Key(a) == Key(b);

    /// <summary>True when <paramref name="inner"/> is <paramref name="outer"/> or inside it.</summary>
    public static bool Within(string inner, string outer)
    {
        var i = Key(inner);
        var o = Key(outer);
        return i == o || i.StartsWith(o.EndsWith('\\') ? o : o + "\\", StringComparison.Ordinal);
    }

    public static string? Drive(string path) => Expand(path) is { Length: >= 2 } p && p[1] == ':' ? p[..2].ToUpperInvariant() : null;

    /// <summary>Replaces a folder at the start of a path (and only a whole folder: E:\Game is not the start of E:\Games).</summary>
    public static string? Rebase(string path, string from, string to)
    {
        var expanded = Expand(path);
        if (!Within(expanded, from)) return null;
        var rest = expanded.Length > Expand(from).TrimEnd('\\').Length ? expanded[Expand(from).TrimEnd('\\').Length..] : "";
        return to.TrimEnd('\\') + rest;
    }

    /// <summary>Every path-looking piece of a setting line that starts with <paramref name="from"/> moved to <paramref name="to"/>.</summary>
    public static string RebaseText(string text, string from, string to)
    {
        var result = RebaseOnce(text, Expand(from).TrimEnd('\\'), to);
        // Settings often name the profile as %USERPROFILE% (so they travel to another PC): that form moves too, and stays that form.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (profile.Length > 0 && Within(from, profile) && result.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            static string Short(string p, string profile) => Within(p, profile) ? "%USERPROFILE%" + Expand(p).TrimEnd('\\')[profile.Length..] : Expand(p).TrimEnd('\\');
            result = RebaseOnce(result, Short(from, profile), Short(to, profile));
        }
        return result;
    }

    private static string RebaseOnce(string text, string f, string to)
    {
        var index = 0;
        var result = text;
        while ((index = result.IndexOf(f, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + f.Length;
            // Only at a folder boundary.
            if (end < result.Length && result[end] is not ('\\' or ' ' or '|' or '"' or ',' or ';'))
            {
                index = end;
                continue;
            }
            var replacement = to.TrimEnd('\\');
            result = result[..index] + replacement + result[end..];
            index += replacement.Length;
        }
        return result;
    }
}

/// <summary>How the layout's rules stand against the modules' settings, and what applying it would change.</summary>
public static class LayoutConfig
{
    /// <summary>Replace: right after a change of the way the Desktop is kept, a line for the same folder is there but says something else.</summary>
    public enum RuleState { Applied, Missing, Different, NoModule, Replace }

    /// <summary>
    /// A list line counts as there when an existing line has the same first field (the folder): the wording may differ.
    /// With <paramref name="replacing"/> (the preset just changed) only the very same line counts: one for the same folder is replaced.
    /// </summary>
    public static RuleState State(ResolvedRule rule, IReadOnlyList<IModule> modules, ConfigStore config, out string? current, bool replacing = false)
    {
        current = null;
        var module = modules.FirstOrDefault(m => m.Info.Id == rule.Module);
        var def = module?.Settings.FirstOrDefault(d => d.Key == rule.Key);
        if (module is null || def is null) return RuleState.NoModule;
        var settings = new ModuleSettings(module.Settings, config.GetModule(module.Info.Id).Settings);
        if (rule.Line is { } line)
        {
            var head = FirstField(line, def.Separator);
            var list = settings.GetList(rule.Key);
            replacing &= rule.PresetBound;
            if (replacing && list.Any(l => SameLine(l, line, def.Separator))) return RuleState.Applied;
            if (list.FirstOrDefault(l => LayoutPaths.Same(FirstField(l, def.Separator), head)) is { } there)
            {
                current = there;
                return replacing ? RuleState.Replace : RuleState.Applied;
            }
            return RuleState.Missing;
        }
        current = settings.GetText(rule.Key);
        if (LayoutPaths.Same(current, rule.Value ?? "")) return RuleState.Applied;
        // Still the module's own default (or empty): the layout may set it. Something the person chose: left alone.
        return current.Trim().Length == 0 || current == def.Default as string ? RuleState.Missing : RuleState.Different;
    }

    private static string FirstField(string line, char separator) => line.Split(separator)[0].Trim();

    /// <summary>Two lines that say the same, whatever the spaces and the case.</summary>
    public static bool SameLine(string a, string b, char separator = '|')
    {
        static string Norm(string s, char sep) => string.Join(sep, s.Split(sep).Select(f => string.Join(",", f.Split(',').Select(x => x.Trim())))).ToLowerInvariant();
        return Norm(a, separator) == Norm(b, separator);
    }

    /// <summary>
    /// The lines of the way the Desktop was kept before that the new one does not have, still exactly as the layout wrote them:
    /// those go when the new one is applied. A line the person wrote or changed is never one of them.
    /// </summary>
    public static IReadOnlyList<ResolvedRule> Leftovers(IReadOnlyList<ResolvedRule> before, IReadOnlyList<ResolvedRule> now, IReadOnlyList<IModule> modules, ConfigStore config)
    {
        var list = new List<ResolvedRule>();
        foreach (var old in before.Where(r => r.Line is not null && r.PresetBound))
        {
            var module = modules.FirstOrDefault(m => m.Info.Id == old.Module);
            var def = module?.Settings.FirstOrDefault(d => d.Key == old.Key);
            if (module is null || def is null) continue;
            // The new way has a line for the same folder: that one replaces it, nothing to take away.
            if (now.Any(n => n.Module == old.Module && n.Key == old.Key && n.Line is { } l && LayoutPaths.Same(FirstField(l, def.Separator), FirstField(old.Line!, def.Separator)))) continue;
            var settings = new ModuleSettings(module.Settings, config.GetModule(module.Info.Id).Settings);
            if (settings.GetList(old.Key).Any(l => SameLine(l, old.Line!, def.Separator))) list.Add(old);
        }
        return list;
    }

    /// <summary>Applies the missing rules: lines added, single values set only where the person has not chosen one.</summary>
    /// <param name="replacing">The way of keeping the Desktop changed: a line for the same folder is replaced, and <paramref name="leftovers"/> go.</param>
    /// <returns>What was changed, in words.</returns>
    public static IReadOnlyList<string> Apply(IReadOnlyList<ResolvedRule> rules, IReadOnlyList<IModule> modules, ConfigStore config,
        bool replacing = false, IReadOnlyList<ResolvedRule>? leftovers = null)
    {
        var done = new List<string>();
        foreach (var old in leftovers ?? [])
        {
            var module = modules.First(m => m.Info.Id == old.Module);
            var def = module.Settings.First(d => d.Key == old.Key);
            config.UpdateModule(old.Module, m =>
            {
                var list = new ModuleSettings(module.Settings, m.Settings).GetList(old.Key).Where(l => !SameLine(l, old.Line!, def.Separator)).ToList();
                m.Settings[old.Key] = JsonSerializer.SerializeToElement(list);
            });
            done.Add(L.T("Tolta: ", "Removed: ") + old.What);
        }
        foreach (var rule in rules)
        {
            var state = State(rule, modules, config, out _, replacing);
            if (state is not (RuleState.Missing or RuleState.Replace)) continue;
            var module = modules.First(m => m.Info.Id == rule.Module);
            var def = module.Settings.First(d => d.Key == rule.Key);
            config.UpdateModule(rule.Module, m =>
            {
                if (rule.Line is { } line)
                {
                    var list = new ModuleSettings(module.Settings, m.Settings).GetList(rule.Key).ToList();
                    var head = FirstField(line, def.Separator);
                    var at = state == RuleState.Replace ? list.FindIndex(l => LayoutPaths.Same(FirstField(l, def.Separator), head)) : -1;
                    if (at >= 0) list[at] = line;
                    else list.Add(line);
                    m.Settings[rule.Key] = JsonSerializer.SerializeToElement(list);
                }
                else m.Settings[rule.Key] = JsonSerializer.SerializeToElement(rule.Value ?? "");
            });
            done.Add(rule.What);
        }
        return done;
    }

    /// <summary>
    /// Moves every mention of a folder in every module's settings (a job's file, a backup source, an emulators folder) to its new
    /// place, so OptLife keeps working after a move. Returns how many settings changed.
    /// </summary>
    public static int Rebase(ConfigStore config, string from, string to)
    {
        var changed = 0;
        config.Update(file =>
        {
            foreach (var module in file.Modules.Values)
                foreach (var key in module.Settings.Keys.ToList())
                {
                    var value = module.Settings[key];
                    if (value.ValueKind == JsonValueKind.String && value.GetString() is { } s && LayoutPaths.RebaseText(s, from, to) is var t && t != s)
                    {
                        module.Settings[key] = JsonSerializer.SerializeToElement(t);
                        changed++;
                    }
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        var items = value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToList();
                        var moved = items.Select(i => LayoutPaths.RebaseText(i, from, to)).ToList();
                        if (!moved.SequenceEqual(items))
                        {
                            module.Settings[key] = JsonSerializer.SerializeToElement(moved);
                            changed++;
                        }
                    }
                }
        });
        return changed;
    }
}
