using System.Text;

namespace OptLife.Modules.Startup;

/// <summary>Where Windows keeps a start-up entry: each has its own on/off switch.</summary>
public enum StartupSource { Registry, Folder, Task, Store }

/// <summary>
/// One thing that starts with Windows, as Windows itself sees it (Task Manager → Startup apps).
/// </summary>
/// <param name="Id">Stable: where it is and its name inside it ("run:HKCU:Spotify", "store:SpotifyAB.SpotifyMusic_x\SpotifyStartupTask").</param>
/// <param name="Name">What people call it.</param>
/// <param name="Match">A second text to recognise it by: the program file, the task, the Store package.</param>
/// <param name="Command">What it starts, to show.</param>
/// <param name="Enabled">On: it starts at the next sign-in.</param>
/// <param name="AllUsers">Registered for every user of the PC: switching it needs administrator rights.</param>
/// <param name="Program">What the started file says it is (its description or product name), when it says.</param>
/// <param name="Publisher">Who made the started file, when it says.</param>
/// <param name="Missing">The file it starts is no longer there: it starts nothing.</param>
public sealed record StartupItem(string Id, string Name, string Match, string Command, StartupSource Source, bool Enabled, bool AllUsers = false,
    string? Program = null, string? Publisher = null, bool Missing = false)
{
    /// <summary>
    /// The name people can read. Tasks are often named with a GUID or a user id ("MicrosoftEdgeUpdateTaskUserS-1-5-21-…Core{C56E…}"):
    /// then the started file's own description ("Microsoft Edge Update") is used, or the file's name.
    /// </summary>
    public string Display
    {
        get
        {
            var clean = StartupRules.Clean(Name);
            if (clean == Name && clean.Length > 0) return Name;
            if (Program is { Length: > 0 } p) return p;
            if (clean.Length > 2) return clean;
            var file = StartupRules.ExePath(Command) is { } exe ? System.IO.Path.GetFileNameWithoutExtension(exe) : "";
            return file.Length > 0 ? file : Name;
        }
    }

    /// <summary>What it is, in a line: what the file says it is, who made it, and the file.</summary>
    public string What
    {
        get
        {
            var parts = new List<string>();
            if (Program is { Length: > 0 } p && p != Display) parts.Add(p);
            if (Publisher is { Length: > 0 } pub) parts.Add(pub);
            if (Display != Name) parts.Add(L.T($"nome della voce: {Name}", $"entry name: {Name}"));
            if (Missing) parts.Add(L.T("il file che avvia non esiste più", "the file it starts no longer exists"));
            parts.Add(Command);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// A Windows service of another maker that starts by itself with Windows (automatic). Some programs start this way instead of
/// a start-up entry: Wallpaper Engine's service starts the wallpaper, SignalRGB's service the lighting.
/// </summary>
/// <param name="Name">The service's name ("Wallpaper Engine Service").</param>
/// <param name="Display">The name Services shows.</param>
/// <param name="Command">The program the service runs.</param>
/// <param name="Match">More text to recognise it by: its file and the programs beside it (what it starts).</param>
public sealed record StartupService(string Name, string Display, string Command, string Match, string? Program = null, string? Publisher = null);

/// <summary>A program that must not start with Windows, with the advice for when it puts itself back.</summary>
public sealed record BlockRule(string Name, string? Advice)
{
    public static BlockRule? Parse(string line)
    {
        var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
        return parts[0].Length == 0 ? null : new BlockRule(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }
}

public enum Verdict { Allowed, Blocked, Other }

/// <summary>The rules of the start-up page, without the machine: who must start, who must not, what changed.</summary>
public static class StartupRules
{
    public const string Disable = "disable-startup", Enable = "enable-startup";

    /// <summary>Letters and digits only, lower case: "Proton VPN", "ProtonVPN" and "protonvpn-service" all read alike.</summary>
    public static string Norm(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The name is part of the entry's name or of what it starts (never the folder: a Steam library path is not Steam).</summary>
    public static bool Matches(StartupItem item, string pattern)
    {
        var p = Norm(pattern);
        return p.Length > 0 && (Norm(item.Name).Contains(p, StringComparison.Ordinal) || Norm(item.Match).Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Wanted names win over unwanted ones: they are the more precise wish ("Wallpaper Engine" over "Steam").</summary>
    public static (Verdict Verdict, BlockRule? Rule) Judge(StartupItem item, IReadOnlyList<string> allowed, IReadOnlyList<BlockRule> blocked)
    {
        if (allowed.Any(a => Matches(item, a))) return (Verdict.Allowed, null);
        return blocked.FirstOrDefault(b => Matches(item, b.Name)) is { } rule ? (Verdict.Blocked, rule) : (Verdict.Other, null);
    }

    public static string SourceName(StartupSource s) => s switch
    {
        StartupSource.Registry => L.T("Registro (Run)", "Registry (Run)"),
        StartupSource.Folder => L.T("Cartella Esecuzione automatica", "Startup folder"),
        StartupSource.Task => L.T("Attività pianificata all'accesso", "Scheduled task at sign-in"),
        _ => L.T("App dello Store", "Store app"),
    };

    private static string OnOff(bool on) => on ? L.T("Acceso", "On") : L.T("Spento", "Off");

    private static CheckButton Switch(StartupItem i) => i.Enabled
        ? new CheckButton(Disable, i.Id, L.T("SPEGNI", "TURN OFF"))
        : new CheckButton(Enable, i.Id, L.T("ACCENDI", "TURN ON"));

    // Programs (Run keys, Startup folders, Store apps) and scheduled tasks are told apart: the first are what Task Manager
    // lists as start-up apps, the second are jobs Windows' Task Scheduler runs at sign-in, often helpers or updaters.
    public static string MustStart => L.T("Programmi che devono partire", "Programs that must start");
    public static string MustNot => L.T("Programmi che non devono partire", "Programs that must not start");
    public static string Others => L.T("Altri programmi all'avvio", "Other start-up programs");
    public static string TasksMustStart => L.T("Attività pianificate che devono partire", "Scheduled tasks that must run");
    public static string TasksMustNot => L.T("Attività pianificate che non devono partire", "Scheduled tasks that must not run");
    public static string TasksOthers => L.T("Altre attività pianificate all'accesso", "Other scheduled tasks at sign-in");

    private static bool IsTask(StartupItem i) => i.Source == StartupSource.Task;

    /// <summary>On in green, off in red: the colour is the state, the same for anyone, never a judgement.</summary>
    private static CheckStatus StateColour(StartupItem i) => i.Enabled ? CheckStatus.Ok : CheckStatus.Wrong;

    /// <summary>
    /// The checklist. Each line is coloured by its state only (green on, red off): OptLife cannot know what a person wants.
    /// The two lists in the settings are optional wishes: they decide the section a line goes in and the problems raised,
    /// and the line says when it differs from them. Programs first, then scheduled tasks.
    /// </summary>
    public static IReadOnlyList<CheckItem> Board(IReadOnlyList<StartupItem> items, IReadOnlyList<string> allowed, IReadOnlyList<BlockRule> blocked,
        IReadOnlyList<StartupService>? services = null)
    {
        var groups = new Dictionary<string, List<CheckItem>>();
        void Add(string group, CheckItem item) => (groups.TryGetValue(group, out var l) ? l : groups[group] = []).Add(item);
        foreach (var i in items.OrderBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase))
        {
            var (verdict, rule) = Judge(i, allowed, blocked);
            var wish = verdict switch
            {
                Verdict.Allowed when !i.Enabled => L.T("Diverso dai tuoi elenchi: dovrebbe partire.", "Not as in your lists: it should start.") + "\n",
                Verdict.Blocked when i.Enabled => L.T("Diverso dai tuoi elenchi: non dovrebbe partire.", "Not as in your lists: it should not start.") +
                    (rule?.Advice is { } a ? " " + a : "") + "\n",
                _ => "",
            };
            if (i.Missing && i.Enabled) wish += L.T("Avvia un file che non c'è più: si può spegnere senza perdere niente.", "It starts a file that is no longer there: it can be turned off without losing anything.") + "\n";
            // First line: the state, in colour. Then any difference from the person's lists, then what it is and who made it.
            var value = $"{OnOff(i.Enabled)} · {SourceName(i.Source)}\n{wish}{i.What}";
            var group = (verdict, IsTask(i)) switch
            {
                (Verdict.Allowed, false) => MustStart,
                (Verdict.Blocked, false) => MustNot,
                (_, false) => Others,
                (Verdict.Allowed, true) => TasksMustStart,
                (Verdict.Blocked, true) => TasksMustNot,
                _ => TasksOthers,
            };
            Add(group, new CheckItem("", i.Display, value, StateColour(i), null, null, SourceName(i.Source), [Switch(i)], StateColour(i), ""));
        }
        foreach (var (name, service) in ViaService(items, allowed, services))
        {
            var what = string.Join(" · ", new[] { service.Program, service.Publisher, service.Command }.Where(t => !string.IsNullOrWhiteSpace(t)));
            Add(MustStart, new CheckItem("", name,
                $"{ViaServiceText} · {L.T("Servizio di Windows", "Windows service")}\n" +
                L.T($"Parte col servizio \"{service.Display}\", che Windows avvia da solo: non ha una voce di avvio sua, ed è normale.",
                    $"It starts with the \"{service.Display}\" service, which Windows starts by itself: it has no start-up entry of its own, and that is normal.") +
                (what.Length > 0 ? "\n" + what : ""),
                CheckStatus.Ok, null, null, L.T("Servizio", "Service"), null, CheckStatus.Ok, ""));
        }
        foreach (var name in Missing(items, allowed, services))
            Add(MustStart, new CheckItem("", name, L.T("Non trovato all'avvio", "Not found at start-up"), CheckStatus.Watch, OnOff(true),
                MissingAdvice, L.T("Mancante", "Missing")));
        string[] order = [MustStart, MustNot, Others, TasksMustStart, TasksMustNot, TasksOthers];
        // With no lists at all there is no "other": just the programs, and the tasks.
        string Title(string g) => allowed.Count + blocked.Count > 0 ? g : g == Others ? L.T("Programmi all'avvio", "Start-up programs") : g == TasksOthers ? L.T("Attività pianificate all'accesso", "Scheduled tasks at sign-in") : g;
        return [.. order.Where(groups.ContainsKey).SelectMany(g => groups[g].Select(c => c with { Group = Title(g) }))];
    }

    /// <summary>The program file of a command line, as written (quoted, or up to ".exe", or the first word).</summary>
    public static string? ExePath(string command)
    {
        var c = command.Trim();
        if (c.Length == 0) return null;
        if (c.StartsWith('"')) return c[1..].Split('"')[0];
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>A name without the GUIDs and user ids Windows tasks are often named with.</summary>
    public static string Clean(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(name, @"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?", ""),
            @"S-1-5-[\d-]+", "").Trim(' ', '_', '-', '.');

    /// <summary>Wanted names that match nothing that starts with Windows (neither an entry nor an automatic service).</summary>
    public static IEnumerable<string> Missing(IReadOnlyList<StartupItem> items, IReadOnlyList<string> allowed, IReadOnlyList<StartupService>? services = null) =>
        allowed.Where(a => Norm(a).Length > 0 && !items.Any(i => Matches(i, a)) && ServiceOf(a, services) is null);

    /// <summary>The service a wanted name is started by, when no start-up entry has that name.</summary>
    public static StartupService? ServiceOf(string pattern, IReadOnlyList<StartupService>? services)
    {
        var p = Norm(pattern);
        return p.Length == 0 || services is null ? null
            : services.FirstOrDefault(s => Norm(s.Name).Contains(p, StringComparison.Ordinal) || Norm(s.Display).Contains(p, StringComparison.Ordinal)
                                           || Norm(s.Match).Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Wanted names started by a service and by no start-up entry: they start, just another way.</summary>
    public static IEnumerable<(string Name, StartupService Service)> ViaService(IReadOnlyList<StartupItem> items, IReadOnlyList<string> allowed, IReadOnlyList<StartupService>? services)
    {
        foreach (var a in allowed.Where(a => Norm(a).Length > 0 && !items.Any(i => Matches(i, a))))
            if (ServiceOf(a, services) is { } service) yield return (a, service);
    }

    public static string ViaServiceText => L.T("Acceso (tramite servizio)", "On (via a service)");

    /// <summary>
    /// The program a service really starts: its own file when it is outside Windows' folder, otherwise the first program outside it
    /// named in its arguments. Some programs install a small launcher in System32 that starts them: Wallpaper Engine's service is
    /// "C:\Windows\system32\wallpaperservice64.exe -p "…\wallpaper_engine\wallpaper64.exe"" (25 Nov 2026, the program looked missing).
    /// Null for Windows' own services.
    /// </summary>
    public static string? ServiceProgram(string imagePath, string windowsFolder)
    {
        var windows = windowsFolder.TrimEnd('\\') + "\\";
        var paths = System.Text.RegularExpressions.Regex.Matches(imagePath, "\"([^\"]+?\\.exe)\"|([A-Za-z]:\\\\[^\" ]+?\\.exe)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => Environment.ExpandEnvironmentVariables(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).ToList();
        return paths.FirstOrDefault(p => Path.IsPathRooted(p) && !p.StartsWith(windows, StringComparison.OrdinalIgnoreCase));
    }

    private static string MissingAdvice => L.T(
        "Non c'è nessuna voce di avvio né servizio di Windows con questo nome: accendi \"Avvia con Windows\" nelle impostazioni del programma (o controlla che sia ancora installato). Se il programma parte lo stesso, scrivi nell'elenco una parte del nome del suo file (.exe) o del suo servizio.",
        "There is no start-up entry or Windows service with this name: turn on \"Start with Windows\" in the program's settings (or check it is still installed). If the program starts anyway, write in the list part of the name of its file (.exe) or of its service.");

    private static string ComesBackAdvice => L.T(
        "Molti programmi si rimettono all'avvio da soli ogni volta che li apri, se nelle loro impostazioni è acceso \"Avvia con Windows\" (o \"Apri all'accesso\"): spegnilo lì, poi premi Spegni. Senza, si riaccende.",
        "Many programs put themselves back at start-up every time you open them when \"Start with Windows\" (or \"Open at login\") is on in their own settings: turn it off there, then press Turn off. Otherwise it comes back.");

    /// <param name="wasOff">Entries seen switched off at some point: being on again means the program switched itself back on.</param>
    public static IEnumerable<NewFinding> Findings(IReadOnlyList<StartupItem> items, IReadOnlyList<string> allowed, IReadOnlyList<BlockRule> blocked,
        IReadOnlySet<string> wasOff, IReadOnlyList<StartupService>? services = null)
    {
        foreach (var i in items)
        {
            var (verdict, rule) = Judge(i, allowed, blocked);
            var admin = i.AllUsers ? " " + L.T("È registrato per tutti gli utenti: per cambiarlo OptLife deve girare da amministratore.", "It is registered for all users: changing it needs OptLife running as administrator.") : "";
            if (verdict == Verdict.Blocked && i.Enabled)
            {
                var back = wasOff.Contains(i.Id);
                yield return new NewFinding($"blocked|{i.Id.ToLowerInvariant()}", back ? Severity.Medium : Severity.Low,
                    back ? L.T($"{i.Display} si è riacceso da solo all'avvio", $"{i.Display} switched itself back on at start-up")
                         : L.T($"{i.Display} parte con Windows ma non dovrebbe", $"{i.Display} starts with Windows but should not"),
                    L.T($"È nell'elenco dei programmi che non devono partire. Fonte: {SourceName(i.Source)}. Cos'è: {i.What}",
                        $"It is in the list of programs that must not start. Source: {SourceName(i.Source)}. What it is: {i.What}"),
                    (rule?.Advice is { } advice ? advice + " " : "") + ComesBackAdvice + admin,
                    new FindingFix(Disable, L.T("Spegnilo all'avvio", "Turn it off at start-up"), i.Id), FindingArea.Software);
            }
            else if (verdict == Verdict.Allowed && !i.Enabled)
            {
                yield return new NewFinding($"allowed|{i.Id.ToLowerInvariant()}", Severity.Low,
                    L.T($"{i.Display} non parte più con Windows", $"{i.Display} no longer starts with Windows"),
                    L.T($"È nell'elenco dei programmi che devono partire, ma la sua voce di avvio è spenta. Fonte: {SourceName(i.Source)}.",
                        $"It is in the list of programs that must start, but its start-up entry is off. Source: {SourceName(i.Source)}."),
                    L.T("Riaccendilo col pulsante, o toglilo dall'elenco se non lo vuoi più.", "Turn it back on with the button, or remove it from the list if you no longer want it.") + admin,
                    new FindingFix(Enable, L.T("Riaccendilo all'avvio", "Turn it back on at start-up"), i.Id), FindingArea.Software);
            }
        }
        foreach (var name in Missing(items, allowed, services))
            yield return new NewFinding($"missing|{Norm(name)}", Severity.Low,
                L.T($"{name} non è tra i programmi all'avvio", $"{name} is not among the start-up programs"),
                L.T("È nell'elenco dei programmi che devono partire con Windows, ma nessuna voce di avvio né servizio automatico lo avvia.",
                    "It is in the list of programs that must start with Windows, but no start-up entry or automatic service starts it."),
                MissingAdvice, Area: FindingArea.Software);
    }

    /// <summary>What appeared, came back on, went off or went away since the last look: one line each in the history.</summary>
    public static IEnumerable<NewEvent> Changes(IReadOnlyDictionary<string, bool> before, IReadOnlyList<StartupItem> now, DateTimeOffset at)
    {
        foreach (var i in now)
        {
            if (!before.TryGetValue(i.Id, out var wasOn))
                yield return new NewEvent(at, "startup_added", i.Enabled ? Severity.Low : Severity.Info,
                    L.T($"Nuovo all'avvio: {i.Display}", $"New at start-up: {i.Display}") + (i.Enabled ? "" : L.T(" (spento)", " (off)")), "startup",
                    Data: Data(i));
            else if (i.Enabled && !wasOn)
                yield return new NewEvent(at, "startup_enabled", Severity.Low, L.T($"Riacceso all'avvio: {i.Display}", $"Back on at start-up: {i.Display}"), "startup", Data: Data(i));
            else if (!i.Enabled && wasOn)
                yield return new NewEvent(at, "startup_disabled", Severity.Info, L.T($"Spento all'avvio: {i.Display}", $"Turned off at start-up: {i.Display}"), "startup", Data: Data(i));
        }
        var ids = now.Select(i => i.Id).ToHashSet();
        foreach (var gone in before.Keys.Where(k => !ids.Contains(k)))
            yield return new NewEvent(at, "startup_removed", Severity.Info, L.T($"Tolto dall'avvio: {NameOf(gone)}", $"Removed from start-up: {NameOf(gone)}"), "startup",
                Data: new Dictionary<string, string> { ["id"] = gone });
    }

    private static Dictionary<string, string> Data(StartupItem i) => new()
    {
        ["id"] = i.Id, ["source"] = i.Source.ToString(), ["command"] = i.Command, ["enabled"] = i.Enabled ? "1" : "0",
    };

    /// <summary>The readable end of an id ("run:HKCU:Spotify" → "Spotify").</summary>
    public static string NameOf(string id)
    {
        var tail = id.Split(':', 3) is { Length: 3 } p ? p[2] : id.Split(':', 2) is { Length: 2 } q ? q[1] : id;
        return tail.Split('\\').Last() is { Length: > 0 } last ? last : tail;
    }

    /// <summary>The value Windows keeps under StartupApproved: first byte 2 = on, 3 = off, then when it was switched off.</summary>
    public static byte[] Approved(bool enabled, DateTimeOffset at)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(at.UtcDateTime.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>No value means on; otherwise an odd first byte (3, 7) means switched off.</summary>
    public static bool IsApproved(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    /// <summary>A Store start-up task: 2 on, 4 on by policy; 0 off, 1 off by the person, 3 off by policy.</summary>
    public static bool IsStoreEnabled(int state) => state is 2 or 4;
}
