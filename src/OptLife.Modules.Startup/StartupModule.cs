using System.Text.Json;
using OptLife.Modules.Startup.Windows;

namespace OptLife.Modules.Startup;

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface IStartupEnvironment
{
    /// <summary>Everything that starts with Windows: Run keys, Startup folders, sign-in tasks, Store apps.</summary>
    IReadOnlyList<StartupItem> Read();

    /// <summary>Switches an entry on or off the way Task Manager does: the entry itself stays.</summary>
    ActionResult SetEnabled(StartupItem item, bool enabled);

    /// <summary>Services of other makers that Windows starts by itself: some programs start through one.</summary>
    IReadOnlyList<StartupService> Services() => [];
}

/// <summary>
/// Start-up programs: which must start with Windows and which must not, from two lists in the settings.
/// It reads every place Windows starts things from, shows them all on one page, reports a program that
/// put itself back on, and switches entries on and off the way Task Manager does (never deleting them).
/// </summary>
public sealed class StartupModule : IModule
{
    public const string BoardKind = "startup_board";
    private readonly IStartupEnvironment _env;

    public StartupModule() : this(new WindowsStartupEnvironment()) { }

    internal StartupModule(IStartupEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("startup", L.T("Avvio automatico", "Start-up programs"),
        L.T("Cosa parte con Windows: i programmi che devono partire, quelli che non devono, e chi si rimette all'avvio da solo.",
            "What starts with Windows: the programs that must start, those that must not, and those that put themselves back."),
        FindingArea.Software);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.List("allowedStartup", L.T("Programmi che devono partire", "Programs that must start"),
            L.T("Parti del nome (spazi e punti non contano). Se uno è spento o manca, viene segnalato.",
                "Parts of the name (spaces and dots do not matter). If one is off or missing, it is reported.")),
        SettingDefinition.Table("blockedStartup", [L.T("Nome", "Name"), L.T("Dove si spegne nel programma", "Where to switch it off in the program")],
            L.T("Programmi che non devono partire", "Programs that must not start"),
            L.T("Scritti come: parte del nome | dove si spegne l'opzione \"avvia con Windows\" nel programma (facoltativo). Se uno è acceso viene segnalato, col pulsante per spegnerlo.",
                "Written as: part of the name | where the \"start with Windows\" option is in the program (optional). If one is on it is reported, with the button to turn it off.")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo dei programmi all'avvio", "Checking start-up programs"), TimeSpan.FromMinutes(10), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(StartupRules.Disable, L.T("Spegni all'avvio", "Turn off at start-up"),
            L.T("Spegne una voce di avvio come fa Gestione attività: il programma resta installato e la voce resta, si riaccende col pulsante.",
                "Turns a start-up entry off the way Task Manager does: the program stays installed and the entry stays, it can be turned back on."),
            Permission.Ask, (ctx, id, ct) => Switch(ctx, id, false, ct)),
        new ModuleAction(StartupRules.Enable, L.T("Accendi all'avvio", "Turn on at start-up"),
            L.T("Riaccende una voce di avvio spenta.", "Turns a start-up entry that was off back on."),
            Permission.Ask, (ctx, id, ct) => Switch(ctx, id, true, ct)),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("startup", L.T("Avvio automatico", "Start-up"), BoardKind, [], Days: 365, Menu: ViewMenu.Programs,
            Guide: L.T("I programmi che partono con Windows: meno sono, prima il PC è pronto. Spegni con un clic quelli che non ti servono subito; restano installati e si riaccendono da qui.", "The programs that start with Windows: the fewer, the sooner the PC is ready. Turn off with a click those you do not need right away; they stay installed and can be turned back on here."),
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("In alto i programmi che partono quando accedi (quelli di Gestione attività → App di avvio); sotto le attività pianificate, lavori che Windows fa partire all'accesso, spesso aiutanti o aggiornamenti dei programmi. " +
                             "Verde = acceso, rosso = spento. Se vuoi, in IMPOSTAZIONI → Avvio automatico puoi scrivere quali devono partire e quali no: OptLife ti avvisa quando uno si discosta (per esempio quando un programma si rimette all'avvio da solo).",
                             "On top, the programs that start when you sign in (those in Task Manager → Startup apps); below, the scheduled tasks, jobs Windows runs at sign-in, often helpers or updaters of programs. " +
                             "Green = on, red = off. If you like, in SETTINGS → Start-up programs you can write which must start and which must not: OptLife warns you when one differs (for example when a program puts itself back at start-up)."),
            Layout: ViewLayout.Checklist),
    ];

    private static IReadOnlyList<BlockRule> Blocked(IModuleContext ctx) => [.. ctx.Settings.GetList("blockedStartup").Select(BlockRule.Parse).OfType<BlockRule>()];

    private static IReadOnlyList<string> Allowed(IModuleContext ctx) => [.. ctx.Settings.GetList("allowedStartup").Where(a => a.Trim().Length > 0).Select(a => a.Trim())];

    private Task Check(IModuleContext ctx, CancellationToken ct)
    {
        if (!ctx.OnDemand && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;
        var items = _env.Read();
        var allowed = Allowed(ctx);
        var blocked = Blocked(ctx);
        // Services only matter when a wanted program has no start-up entry: then it may start through its service.
        IReadOnlyList<StartupService> services = StartupRules.Missing(items, allowed).Any() ? _env.Services() : [];

        // What changed since the last look goes in the history; the first look only takes note.
        if (Load<Dictionary<string, bool>>(ctx, "seen") is { } before)
            ctx.RecordEvents([.. StartupRules.Changes(before, items, ctx.Now)]);
        ctx.SetState("seen", JsonSerializer.Serialize(items.ToDictionary(i => i.Id, i => i.Enabled)));

        var wasOff = Load<HashSet<string>>(ctx, "was_off") ?? [];
        var count = wasOff.Count;
        wasOff.UnionWith(items.Where(i => !i.Enabled).Select(i => i.Id));
        if (wasOff.Count != count) ctx.SetState("was_off", JsonSerializer.Serialize(wasOff));

        ctx.SyncFindings("startup", [.. StartupRules.Findings(items, allowed, blocked, wasOff, services)]);

        var data = CheckItem.ToData(StartupRules.Board(items, allowed, blocked, services));
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature != ctx.GetState("board"))
        {
            ctx.SetState("board", signature);
            ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
                L.T($"Avvio automatico: {items.Count(i => i.Enabled)} programmi accesi su {items.Count}", $"Start-up: {items.Count(i => i.Enabled)} programs on out of {items.Count}"),
                BoardKind, Data: data)]);
        }
        return Task.CompletedTask;
    }

    private async Task<ActionResult> Switch(IModuleContext ctx, string? id, bool enabled, CancellationToken ct)
    {
        // Looked up again now: the entry may have changed or gone since the page was drawn.
        var item = _env.Read().FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new ActionResult(false, L.T($"\"{StartupRules.NameOf(id ?? "")}\" non è più tra i programmi all'avvio.", $"\"{StartupRules.NameOf(id ?? "")}\" is no longer among the start-up programs."));
        if (item.Enabled == enabled)
            return new ActionResult(true, enabled ? L.T($"{item.Name} era già acceso.", $"{item.Name} was already on.") : L.T($"{item.Name} era già spento.", $"{item.Name} was already off."));
        var result = _env.SetEnabled(item, enabled);
        if (result.Ok)
        {
            result = result with
            {
                Undo = result.Undo ?? $"optlife do startup {(enabled ? StartupRules.Disable : StartupRules.Enable)} \"{item.Id}\" --yes",
            };
            await Check(new Forced(ctx), ct);
        }
        return result;
    }

    private static T? Load<T>(IModuleContext ctx, string key) where T : class
    {
        try
        {
            return ctx.GetState(key) is { } json ? JsonSerializer.Deserialize<T>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The same context, as asked by the person: right after a switch the page is rewritten even during a game.</summary>
    private sealed class Forced(IModuleContext inner) : IModuleContext
    {
        public string ModuleId => inner.ModuleId;
        public DateTimeOffset Now => inner.Now;
        public bool OnDemand => true;
        public ISettings Settings => inner.Settings;
        public IHistory History => inner.History;
        public string DataDirectory => inner.DataDirectory;
        public void RecordSample(string metric, double value, DateTimeOffset? at = null) => inner.RecordSample(metric, value, at);
        public void RecordSamples(IEnumerable<KeyValuePair<string, double>> samples, DateTimeOffset? at = null) => inner.RecordSamples(samples, at);
        public void PublishLive(IReadOnlyCollection<LiveValue> values) => inner.PublishLive(values);
        public IReadOnlyList<LiveValue> ReadLive() => inner.ReadLive();
        public int RecordEvents(IEnumerable<NewEvent> events) => inner.RecordEvents(events);
        public void SyncFindings(string scope, IReadOnlyCollection<NewFinding> current) => inner.SyncFindings(scope, current);
        public string? GetState(string key) => inner.GetState(key);
        public void SetState(string key, string? value) => inner.SetState(key, value);
    }
}
