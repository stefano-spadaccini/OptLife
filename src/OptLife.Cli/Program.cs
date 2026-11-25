using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;

// The command-line door into OptLife. Every read command accepts --json, which is how an
// assistant (or a script) reads the PC's state without going through the panel.

Console.OutputEncoding = Encoding.UTF8;
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var json = args.Contains("--json");
var words = args.Where(a => !a.StartsWith("--")).ToArray();
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
// Values that follow an option are not positional words.
foreach (var opt in new[] { "--since", "--kind", "--group", "--limit", "--days" })
    if (Option(opt) is { } v) words = [.. words.Where(w => w != v)];

var command = words.FirstOrDefault() ?? "status";
if (command is "help" or "-h" or "/?") return Help();

var rt = OptLifeRuntime.Create();
var it = L.Culture; // After Create: that is when the language is chosen.

try
{
    return command switch
    {
        "status" => Status(),
        "findings" => Findings(),
        "events" => Events(),
        "modules" => Modules(),
        "actions" => Actions(),
        "do" => await Do(),
        "choices" => Choices(),
        "permission" => PermissionCommand(),
        "report" => ReportCommand(),
        "profile" => Profile(),
        "layout" => Layout(),
        "activity" => Activity(),
        "notify" => Notify(),
        "live" => Live(),
        "run" => await Run(),
        "ignore" => SetStatus(FindingStatus.Ignored, L.T("ignorato", "ignored")),
        "reopen" => SetStatus(FindingStatus.Open, L.T("riaperto", "reopened")),
        "solve" => Solve(),
        "enable" => Enable(true),
        "disable" => Enable(false),
        "set" => Set(),
        "language" => Language(),
        "stop" => Stop(),
        _ => Fail(L.T($"Comando sconosciuto: {command}. Usa \"optlife help\".", $"Unknown command: {command}. Use \"optlife help\".")),
    };
}
catch (FormatException ex)
{
    return Fail(ex.Message);
}
catch (Exception ex) when (rt.Store.ReadOnly && ex is Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException or IOException)
{
    // An installed copy: its data changes only with administrator rights; reading works from any terminal.
    return Fail(L.T("Questo comando cambia i dati di OptLife, che può cambiare solo un amministratore: aprilo in un terminale come amministratore.",
        "This command changes OptLife's data, which only an administrator may change: run it in a terminal as administrator."));
}

int Status()
{
    var open = rt.Store.GetFindings();
    var modules = rt.Modules.Select(m => new
    {
        m.Info.Id,
        m.Info.Name,
        Enabled = rt.Runner.IsEnabled(m),
        Tasks = m.Tasks.Select(t => rt.Runner.ReadStatus(m, t)).ToList(),
    }).ToList();

    if (json)
        return Print(new { AgentRunning = OptLifeRuntime.IsAgentRunning(), rt.DataDir, Modules = modules, OpenFindings = open, rt.LoadErrors });

    Console.WriteLine(L.T("Agente: ", "Agent: ") + (OptLifeRuntime.IsAgentRunning() ? L.T("attivo", "running") : L.T("non attivo", "not running")) + L.T("   Dati: ", "   Data: ") + rt.DataDir);
    foreach (var m in modules)
    {
        var last = m.Tasks.Max(t => t.LastRun);
        var errors = m.Tasks.Where(t => t.LastError is not null).Select(t => t.LastError).ToList();
        var state = !m.Enabled ? L.T("spento", "off") : errors.Count > 0 ? L.T("IN ERRORE: ", "FAILING: ") + errors[0] : "ok";
        Console.WriteLine($"  [{m.Id}] {m.Name}: {state}{(last is null ? "" : L.T(", ultimo giro ", ", last round ") + last.Value.ToString("d MMM HH:mm", it))}");
    }
    foreach (var e in rt.LoadErrors) Console.WriteLine(L.T("  Modulo non caricato: ", "  Module not loaded: ") + e);
    Console.WriteLine();
    PrintFindings(open);
    return 0;
}

int Findings()
{
    var list = rt.Store.GetFindings(includeClosed: args.Contains("--all"));
    if (json) return Print(list);
    PrintFindings(list);
    return 0;
}

void PrintFindings(IReadOnlyList<StoredFinding> list)
{
    if (list.Count == 0)
    {
        Console.WriteLine(L.T("Nessun problema aperto.", "No open problems."));
        return;
    }
    Console.WriteLine(L.T("Problemi: ", "Problems: ") + list.Count);
    foreach (var f in list)
    {
        var mark = f.Status switch { FindingStatus.Ignored => L.T(" (ignorato)", " (ignored)"), FindingStatus.Resolved => L.T(" (risolto)", " (solved)"), _ => "" };
        Console.WriteLine($"\n #{f.Id} [{SeverityName(f.Severity)}] {f.Title}{mark}");
        Console.WriteLine($"     {f.Detail}");
        if (f.Advice is not null) Console.WriteLine($"     → {f.Advice}");
        if (f.Fix is not null)
            // Quoted when it holds what a shell would split on (spaces, the ';' between several ids).
        {
            var arg = f.Fix.Argument;
            if (arg is not null && arg.IndexOfAny([' ', ';', '&', '|']) >= 0) arg = $"\"{arg}\"";
            Console.WriteLine($"     ⚙ {f.Fix.Label}: optlife do {string.Join(' ', new[] { f.Module, f.Fix.ActionId, arg }.Where(p => !string.IsNullOrEmpty(p)))} --yes");
        }
    }
}

int Events()
{
    var since = ParseSince(Option("--since") ?? "7d");
    var limit = int.TryParse(Option("--limit"), out var l) ? l : 200;
    var list = rt.Store.QueryEvents(since, DateTimeOffset.Now, kind: Option("--kind"), groupKey: Option("--group"), limit: limit);
    if (json) return Print(list);
    if (list.Count == 0) Console.WriteLine(L.T("Nessun evento nel periodo.", "No events in the period."));
    foreach (var e in list)
        Console.WriteLine($"{e.Time.ToString("dd/MM HH:mm", it)}  {e.Kind,-20} {e.Title}");
    return 0;
}

int Modules()
{
    var list = rt.Modules.Select(m =>
    {
        var settings = rt.Runner.SettingsOf(m);
        return new
        {
            m.Info.Id,
            m.Info.Name,
            m.Info.Description,
            Enabled = rt.Runner.IsEnabled(m),
            Settings = m.Settings.Select(s => new { s.Key, s.Label, s.Description, Value = Current(settings, s) }).ToList(),
            Actions = m.Actions.Select(a => new { a.Id, a.Name, a.Description, a.DefaultPermission, Permission = rt.Runner.PermissionOf(m, a), a.NeverAuto }).ToList(),
        };
    }).ToList();
    if (json) return Print(list);
    foreach (var m in list)
    {
        Console.WriteLine($"[{m.Id}] {m.Name} — {(m.Enabled ? L.T("acceso", "on") : L.T("spento", "off"))}\n  {m.Description}");
        foreach (var s in m.Settings)
            Console.WriteLine($"    {s.Key} = {(s.Value is IEnumerable<string> items ? string.Join("; ", items) : s.Value)}   ({s.Label})");
    }
    return 0;
}

int Actions()
{
    var list = rt.Store.GetActions();
    if (json) return Print(list);
    if (list.Count == 0) Console.WriteLine(L.T("Nessuna azione registrata.", "No actions recorded."));
    foreach (var a in list)
        Console.WriteLine($"{a.Time.ToString("dd/MM HH:mm", it)}  {a.Module}/{a.Action} {L.T("da", "by")} {a.Actor}: {(a.Ok ? "ok" : L.T("non eseguita", "not done"))} — {a.Detail}");
    return 0;
}

// The say the person has over one action: run by itself, ask first, never. The same choice the panel offers.
int PermissionCommand()
{
    var usage = L.T("Uso: optlife permission <modulo> <azione> <da-solo|chiedi|mai>", "Usage: optlife permission <module> <action> <auto|ask|never>");
    if (words.Length < 4) return Fail(usage);
    if (FindModule(words[1]) is not { } m) return 1;
    var action = m.Actions.FirstOrDefault(a => a.Id.Equals(words[2], StringComparison.OrdinalIgnoreCase));
    if (action is null) return Fail(L.T($"Il modulo \"{m.Info.Id}\" non ha l'azione \"{words[2]}\". Disponibili: ", $"The \"{m.Info.Id}\" module has no \"{words[2]}\" action. Available: ") + string.Join(", ", m.Actions.Select(a => a.Id)));
    Permission? chosen = words[3].ToLowerInvariant() switch
    {
        "da-solo" or "auto" => Permission.Auto,
        "chiedi" or "ask" => Permission.Ask,
        "mai" or "never" => Permission.Never,
        _ => null,
    };
    if (chosen is null) return Fail(usage);
    if (action.NeverAuto && chosen == Permission.Auto)
        return Fail(L.T($"\"{action.Name}\" toglie qualcosa dal PC: non può partire da sola. Resta su \"chiedi\" o \"mai\".", $"\"{action.Name}\" removes something from the PC: it cannot run by itself. It stays on \"ask\" or \"never\"."));
    rt.Config.UpdateModule(m.Info.Id, c => c.Permissions[action.Id] = chosen.Value);
    Console.WriteLine($"{m.Info.Name} · {action.Name}: {chosen switch { Permission.Auto => L.T("da solo", "by itself"), Permission.Ask => L.T("chiedi prima", "ask first"), _ => L.T("mai", "never") }}.");
    return 0;
}

// The period in words: what came and went, what was recorded, what was changed, what to do next.
int ReportCommand()
{
    var days = int.TryParse(Option("--days"), out var d) && d > 0 ? d : 7;
    var report = OptLife.Core.Reporting.Report.Build(rt.Store,
        id => id == ModuleRunner.CoreModuleId ? "OptLife" : rt.Modules.FirstOrDefault(m => m.Info.Id == id)?.Info.Name ?? id,
        (module, action) => rt.Modules.FirstOrDefault(m => m.Info.Id == module)?.Actions.FirstOrDefault(a => a.Id == action)?.Name ?? action,
        DateTimeOffset.Now.AddDays(-days), DateTimeOffset.Now);
    if (json) return Print(report);
    Console.Write(report.ToText());
    return 0;
}

// The way OptLife is set up, without anything personal, to hand to someone else (or to take theirs).
int Profile()
{
    var file = words.ElementAtOrDefault(2);
    if (words.ElementAtOrDefault(1) is not ("export" or "import") || file is null)
        return Fail(L.T("Uso: optlife profile export <file>   |   optlife profile import <file>", "Usage: optlife profile export <file>   |   optlife profile import <file>"));
    if (words[1] == "export")
    {
        File.WriteAllText(file, OptLife.Core.Config.Profile.Export(rt.Config, rt.Modules));
        Console.WriteLine(L.T($"Profilo scritto in {file}: moduli accesi, soglie e permessi. Niente percorsi, elenchi o nomi.", $"Profile written to {file}: modules on, thresholds and permissions. No paths, lists or names."));
        return 0;
    }
    if (!File.Exists(file)) return Fail(L.T($"Non trovo {file}.", $"Cannot find {file}."));
    var applied = OptLife.Core.Config.Profile.Import(rt.Config, rt.Modules, File.ReadAllText(file));
    Console.WriteLine(L.T($"Profilo applicato: {applied} impostazioni. I tuoi elenchi e percorsi non sono stati toccati.", $"Profile applied: {applied} settings. Your lists and paths were not touched."));
    return 0;
}

// What OptLife is doing now and did lately (actions, Scheduler jobs), newest first.
int Activity()
{
    var list = rt.Store.GetActivities(30);
    if (json) return Print(list);
    if (list.Count == 0)
    {
        Console.WriteLine(L.T("Nessuna attività.", "No activity."));
        return 0;
    }
    var now = DateTimeOffset.Now;
    foreach (var a in list.OrderBy(a => a.State == ActivityState.Running ? 0 : 1).ThenByDescending(a => a.Id))
    {
        var state = a.State switch
        {
            ActivityState.Running => L.T("IN CORSO", "RUNNING"),
            ActivityState.Done => L.T("FATTO", "DONE"),
            ActivityState.Failed => L.T("NON RIUSCITO", "FAILED"),
            _ => L.T("INTERROTTO", "STOPPED"),
        };
        Console.WriteLine($"[{state}] {a.Title} ({a.Module}, {a.Actor}) · {a.Start.ToLocalTime():dd/MM HH:mm} · {OptLife.Core.Hosting.ActivityNotices.Duration(a.Elapsed(now))}"
            + (a.State == ActivityState.Running && a.Estimate is { } e ? L.T($" · di solito {OptLife.Core.Hosting.ActivityNotices.Duration(e)}", $" · usually {OptLife.Core.Hosting.ActivityNotices.Duration(e)}") : ""));
        if (a.State == ActivityState.Running && a.Progress is { } p) Console.WriteLine("     " + OptLife.Core.Hosting.ActivityNotices.Step(a, p));
        if (a.State != ActivityState.Running && !string.IsNullOrWhiteSpace(a.Detail)) Console.WriteLine("     " + a.Detail);
    }
    return 0;
}

// The agent's pop-up: on/off, with sound or not, also for Scheduler jobs or not.
int Notify()
{
    var n = rt.Config.Load().Notifications;
    if (words.Length < 3)
    {
        Console.WriteLine($"popup={(n.Popup ? "on" : "off")} sound={(n.Sound ? "on" : "off")} scheduler={(n.Scheduler ? "on" : "off")}");
        Console.WriteLine(L.T("Uso: optlife notify popup|sound|scheduler on|off", "Usage: optlife notify popup|sound|scheduler on|off"));
        return 0;
    }
    if (words[2] is not ("on" or "off") || words[1] is not ("popup" or "sound" or "scheduler"))
        return Fail(L.T("Uso: optlife notify popup|sound|scheduler on|off", "Usage: optlife notify popup|sound|scheduler on|off"));
    var on = words[2] == "on";
    rt.Config.Update(f =>
    {
        if (words[1] == "popup") f.Notifications.Popup = on;
        else if (words[1] == "sound") f.Notifications.Sound = on;
        else f.Notifications.Scheduler = on;
    });
    Console.WriteLine($"{words[1]} = {words[2]}");
    return 0;
}

// The PC organisation, to pass to someone else: "export" makes it general, "import" takes it and adapts the drives.
int Layout()
{
    var file = words.ElementAtOrDefault(2);
    if (words.ElementAtOrDefault(1) is not ("export" or "import") || file is null)
        return Fail(L.T("Uso: optlife layout export <file>   |   optlife layout import <file>   (vederla: optlife run layout; applicarla: optlife do layout apply --yes)",
            "Usage: optlife layout export <file>   |   optlife layout import <file>   (see it: optlife run layout; apply it: optlife do layout apply --yes)"));
    if (rt.Modules.OfType<OptLife.Core.Layout.LayoutModule>().FirstOrDefault() is not { } layout) return Fail("layout?");
    if (words[1] == "export")
    {
        File.WriteAllText(file, layout.Export(rt.Runner.SettingsOf(layout), layout.DataDirectory));
        Console.WriteLine(L.T($"Organizzazione scritta in {file}: cartelle e regole con i percorsi resi generici.", $"Organisation written to {file}: folders and rules with general paths."));
        return 0;
    }
    if (!File.Exists(file)) return Fail(L.T($"Non trovo {file}.", $"Cannot find {file}."));
    var notes = layout.Import(File.ReadAllText(file), layout.DataDirectory);
    Console.WriteLine(L.T("Organizzazione importata. Niente è stato creato o spostato: guarda la pagina Organizzazione e premi APPLICA.", "Organisation imported. Nothing was created or moved: look at the Organisation page and press APPLY."));
    foreach (var note in notes) Console.WriteLine("  " + note);
    return 0;
}

// Carries out one action of a module. An action set to "ask first" needs --yes: that is the confirmation.
async Task<int> Do()
{
    if (words.Length < 3) return Fail(L.T("Uso: optlife do <modulo> <azione> [argomento] [--yes]   (le azioni si vedono con \"optlife modules\")", "Usage: optlife do <module> <action> [argument] [--yes]   (actions are listed by \"optlife modules\")"));
    if (FindModule(words[1]) is not { } m) return 1;
    var action = m.Actions.FirstOrDefault(a => a.Id.Equals(words[2], StringComparison.OrdinalIgnoreCase));
    if (action is null) return Fail(L.T($"Il modulo \"{m.Info.Id}\" non ha l'azione \"{words[2]}\". Disponibili: ", $"The \"{m.Info.Id}\" module has no \"{words[2]}\" action. Available: ") + string.Join(", ", m.Actions.Select(a => a.Id)));
    var argument = words.Length > 3 ? string.Join(' ', words.Skip(3)) : null;

    var result = await rt.Runner.RunActionAsync(m, action, "cli", confirmed: args.Contains("--yes"), argument, CancellationToken.None);
    if (json) return Print(result);
    Console.WriteLine($"{result.Outcome switch
    {
        ActionOutcome.Done => L.T("Fatto", "Done"),
        ActionOutcome.Nothing => L.T("Niente da fare", "Nothing to do"),
        ActionOutcome.Choice => L.T("Serve una scelta", "A choice is needed"),
        _ => L.T("Non eseguita", "Not done"),
    }}: {result.Detail}");
    // The ways out of a choice, as commands to type.
    foreach (var way in result.Needs ?? [])
        Console.WriteLine($"  {way.Label}: optlife do {m.Info.Id} {way.ActionId}{(way.Argument is { } a ? $" \"{a}\"" : "")} --yes");
    if (result.Undo is not null) Console.WriteLine(L.T("Per tornare indietro: ", "To undo: ") + result.Undo);
    // 0 done or nothing to do, 1 not done, 2 waiting for a choice.
    return result.Outcome switch { ActionOutcome.Done or ActionOutcome.Nothing => 0, ActionOutcome.Choice => 2, _ => 1 };
}

// What an action with a list to tick would offer, without doing anything.
int Choices()
{
    if (words.Length < 3) return Fail(L.T("Uso: optlife choices <modulo> <azione>", "Usage: optlife choices <module> <action>"));
    if (FindModule(words[1]) is not { } m) return 1;
    var action = m.Actions.FirstOrDefault(a => a.Id.Equals(words[2], StringComparison.OrdinalIgnoreCase));
    if (action?.Choices is null) return Fail(L.T($"L'azione \"{words[2]}\" non ha un elenco da scegliere.", $"The \"{words[2]}\" action has no list to choose from."));
    var choices = rt.Runner.ChoicesOf(m, action, out var error);
    if (error is not null) return Fail(error);
    if (json) return Print(choices);
    foreach (var c in choices) Console.WriteLine($" [{(c.Checked ? "x" : " ")}] {c.Id,-28} {c.Label} · {c.Detail}");
    Console.WriteLine(L.T("Per eseguire: ", "To run: ") + $"optlife do {m.Info.Id} {action.Id} \"{string.Join(';', choices.Where(c => c.Checked).Select(c => c.Id))}\" --yes");
    return 0;
}

async Task<int> Run()
{
    var only = words.ElementAtOrDefault(1);
    var selected = rt.Modules.Where(m => only is null ? rt.Runner.IsEnabled(m) : m.Info.Id == only).ToList();
    if (selected.Count == 0) return Fail(only is null ? L.T("Nessun modulo acceso.", "No module is on.") : L.T("Modulo sconosciuto: ", "Unknown module: ") + only);
    var allOk = true;
    foreach (var m in selected)
        foreach (var t in m.Tasks.Where(t => !t.Continuous))
        {
            var ok = await rt.Runner.RunTaskAsync(m, t, CancellationToken.None, onDemand: true);
            allOk &= ok;
            if (!json) Console.WriteLine($"{m.Info.Id}/{t.Id}: {(ok ? "ok" : L.T("ERRORE — ", "ERROR — ") + rt.Runner.ReadStatus(m, t).LastError)}");
        }
    if (json) return Print(new { Ok = allOk });
    return allOk ? 0 : 1;
}

int SetStatus(FindingStatus status, string done)
{
    if (!long.TryParse(words.ElementAtOrDefault(1)?.TrimStart('#'), out var id)) return Fail(L.T("Indica il numero del problema, per esempio: optlife ignore 3", "Give the problem number, for example: optlife ignore 3"));
    if (!rt.Store.SetFindingStatus(id, status)) return Fail(L.T($"Nessun problema con numero {id}.", $"No problem with number {id}."));
    Console.WriteLine(L.T($"Problema #{id} {done}.", $"Problem #{id} {done}."));
    return 0;
}

// The cause is fixed: the problem closes now and comes back only if it happens again.
int Solve()
{
    if (!long.TryParse(words.ElementAtOrDefault(1)?.TrimStart('#'), out var id)) return Fail(L.T("Indica il numero del problema, per esempio: optlife solve 3", "Give the problem number, for example: optlife solve 3"));
    if (!rt.Store.SolveFinding(id, DateTimeOffset.Now)) return Fail(L.T($"Il problema #{id} non c'è, oppure non dice quando è successo l'ultima volta: si può solo ignorare.", $"Problem #{id} does not exist, or does not say when it last happened: it can only be ignored."));
    Console.WriteLine(L.T($"Problema #{id} risolto: si riapre solo se succede di nuovo.", $"Problem #{id} solved: it reopens only if it happens again."));
    return 0;
}

int Enable(bool enabled)
{
    if (FindModule(words.ElementAtOrDefault(1)) is not { } m) return 1;
    rt.Config.UpdateModule(m.Info.Id, c => c.Enabled = enabled);
    Console.WriteLine(L.T($"Modulo \"{m.Info.Name}\" ", $"Module \"{m.Info.Name}\" ") + (enabled ? L.T("acceso", "on") : L.T("spento", "off")) + ".");
    return 0;
}

int Set()
{
    if (words.Length < 4) return Fail(L.T("Uso: optlife set <modulo> <impostazione> <valore>   (liste separate da ;)", "Usage: optlife set <module> <setting> <value>   (lists separated by ;)"));
    if (FindModule(words[1]) is not { } m) return 1;
    var def = m.Settings.FirstOrDefault(s => s.Key.Equals(words[2], StringComparison.OrdinalIgnoreCase));
    if (def is null) return Fail(L.T($"Il modulo \"{m.Info.Id}\" non ha l'impostazione \"{words[2]}\".", $"The \"{m.Info.Id}\" module has no \"{words[2]}\" setting."));
    var value = ModuleSettings.Parse(def, string.Join(' ', words.Skip(3)));
    rt.Config.UpdateModule(m.Info.Id, c => c.Settings[def.Key] = value);
    Console.WriteLine($"{m.Info.Id}.{def.Key} = {value}");
    return 0;
}

int Stop()
{
    Console.WriteLine(OptLifeRuntime.RequestAgentStop() ? L.T("Arresto dell'agente richiesto.", "Agent stop requested.") : L.T("L'agente non è attivo.", "The agent is not running."));
    return 0;
}

int Language()
{
    if (words.Length < 2)
    {
        var setting = rt.Config.Load().Language;
        Console.WriteLine(L.T($"Lingua: {setting} (in uso: {L.Code})", $"Language: {setting} (in use: {L.Code})"));
        return 0;
    }
    var value = words[1].ToLowerInvariant();
    if (value is not (L.Auto or L.Italian or L.English)) return Fail(L.T("Uso: optlife language auto|it|en", "Usage: optlife language auto|it|en"));
    rt.Config.Update(c => c.Language = value);
    Console.WriteLine(L.T($"Lingua impostata: {value}. Vale dal prossimo avvio di agente e pannello.", $"Language set: {value}. It applies from the next start of the agent and the panel."));
    return 0;
}

int Help()
{
    L.Use(new ConfigStore(Path.Combine(OptLifeRuntime.ResolveDataDir(), "config.json")).Load().Language);
    Console.WriteLine(L.IsEnglish ? """
        OptLife — state and history of the PC

          optlife status                 overall state and open problems
          optlife findings [--all]       problems (with --all also ignored and solved)
          optlife events [--since 7d] [--kind type] [--group key] [--limit n]
          optlife live                   values right now (temperatures, memory, network...)
          optlife modules                modules, settings and actions
          optlife run [module]           runs a check round right away
          optlife ignore|reopen <n>      ignores or reopens a problem
          optlife solve <n>              marks solved: it reopens only if it happens again
          optlife enable|disable <module>
          optlife set <module> <setting> <value>
          optlife language [auto|it|en]  language of OptLife (auto = the one of Windows)
          optlife do <module> <action> [argument] [--yes]   runs an action (--yes = confirm)
          optlife choices <module> <action>  what an action with a tick list would offer, doing nothing
          optlife permission <module> <action> <auto|ask|never>   how much an action may do by itself
          optlife actions                log of the actions run
          optlife report [--days 7]      the period in words: problems, events, changes, things to do
          optlife profile export|import <file>   settings without personal data, to pass to someone else
          optlife layout export|import <file>    the PC organisation (folders and rules), to pass to someone else
          optlife activity [--json]         what OptLife is doing now and did lately
          optlife notify [popup|sound|scheduler on|off]   the pop-up above the clock
          optlife stop                   stops the background agent

        Add --json to any read command for structured output.
        """ : """
        OptLife — stato e storico del PC

          optlife status                 stato generale e problemi aperti
          optlife findings [--all]       problemi (con --all anche ignorati e risolti)
          optlife events [--since 7d] [--kind tipo] [--group chiave] [--limit n]
          optlife live                   valori di questo momento (temperature, memoria, rete...)
          optlife modules                moduli, impostazioni e azioni
          optlife run [modulo]           esegue subito un giro di controllo
          optlife ignore|reopen <n>      ignora o riapre un problema
          optlife solve <n>              segna risolto: si riapre solo se succede di nuovo
          optlife enable|disable <modulo>
          optlife set <modulo> <impostazione> <valore>
          optlife language [auto|it|en]  lingua di OptLife (auto = quella di Windows)
          optlife do <modulo> <azione> [argomento] [--yes]   esegue un'azione (--yes = conferma)
          optlife choices <modulo> <azione>  cosa offrirebbe un'azione con un elenco da spuntare, senza fare niente
          optlife permission <modulo> <azione> <da-solo|chiedi|mai>   quanto può fare da sola un'azione
          optlife actions                registro delle azioni eseguite
          optlife report [--days 7]      il periodo a parole: problemi, eventi, cambiamenti, cose da fare
          optlife profile export|import <file>   impostazioni senza dati personali, da passare a qualcun altro
          optlife layout export|import <file>    l'organizzazione del PC (cartelle e regole), da passare a qualcun altro
          optlife activity [--json]         cosa sta facendo OptLife e cosa ha fatto di recente
          optlife notify [popup|sound|scheduler on|off]   l'avviso sopra l'orologio
          optlife stop                   ferma l'agente in background

        Aggiungi --json a qualsiasi comando di lettura per l'output strutturato.
        """);
    return 0;
}

IModule? FindModule(string? id)
{
    var m = rt.Modules.FirstOrDefault(x => x.Info.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (m is null) Fail(L.T("Modulo sconosciuto: ", "Unknown module: ") + (id ?? L.T("(nessuno)", "(none)")) + L.T(". Disponibili: ", ". Available: ") + string.Join(", ", rt.Modules.Select(x => x.Info.Id)));
    return m;
}

static object Current(ISettings settings, SettingDefinition s) => s.Kind switch
{
    SettingKind.Bool => settings.GetBool(s.Key),
    SettingKind.Int => settings.GetInt(s.Key),
    SettingKind.Text => settings.GetText(s.Key),
    SettingKind.Choice => settings.GetChoice(s.Key),
    _ => settings.GetList(s.Key),
};

static string SeverityName(Severity s) => s switch
{
    Severity.High => L.T("ALTO", "HIGH"),
    Severity.Medium => L.T("medio", "medium"),
    Severity.Low => L.T("basso", "low"),
    _ => "info",
};

static DateTimeOffset ParseSince(string text)
{
    if (text.Length >= 2 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
    {
        switch (text[^1])
        {
            case 'm': return DateTimeOffset.Now.AddMinutes(-n);
            case 'h': return DateTimeOffset.Now.AddHours(-n);
            case 'd': return DateTimeOffset.Now.AddDays(-n);
        }
    }
    throw new FormatException(L.T($"Periodo non valido: \"{text}\". Esempi: 30m, 24h, 7d.", $"Invalid period: \"{text}\". Examples: 30m, 24h, 7d."));
}

int Print(object value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

int Live()
{
    var all = LiveBoard.Read(DateTimeOffset.Now);
    if (json) return Print(all); // Everything, for whoever reads it as data.
    var entries = all.Where(e => e.Label.Length > 0).ToList(); // Values with no label are for OptLife itself.
    if (entries.Count == 0)
    {
        Console.WriteLine(L.T("Nessun valore dal vivo: l'agente non è attivo oppure il modulo Monitoraggio è spento.", "No live values: the agent is not running or the Monitoring module is off."));
        return 0;
    }
    foreach (var group in entries.GroupBy(e => e.Group))
    {
        Console.WriteLine(group.Key);
        foreach (var e in group)
            Console.WriteLine(e.IsMissing
                ? $"  {e.Label,-28} {L.T("n/d", "n/a"),8} {"",-4} {e.Key}  ({e.Missing})"
                : $"  {e.Label,-28} {LiveEntry.FormatNumber(e.Value, e.Unit),8} {e.Unit,-4} {e.Key}");
    }
    return 0;
}
