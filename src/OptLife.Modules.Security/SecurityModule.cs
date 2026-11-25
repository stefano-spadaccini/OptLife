using System.Globalization;
using OptLife.Modules.Security.Windows;

namespace OptLife.Modules.Security;

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface ISecurityEnvironment
{
    /// <summary>Defender, local accounts, Windows optional features turned on, ports open to the network; null when nothing could be read.</summary>
    Task<SecurityReport?> Read(CancellationToken ct);

    FirewallState? Firewall();

    /// <summary>Turns off the firewall rules with these ids (never deletes them).</summary>
    ActionResult DisableRules(IReadOnlyCollection<string> ids);

    Task<ActionResult> DisableFeature(string feature, CancellationToken ct);

    ActionResult Open(string target);
}

/// <summary>
/// Security, read-only unless asked: Defender (real-time, definitions, last full scan, tamper protection), the firewall (on for
/// each network, rules left by programs no longer there), local accounts, Windows features better left off, ports open to the
/// network. The only changes it can make, each on a button: turn off orphan firewall rules, turn off a listed Windows feature.
/// </summary>
public sealed class SecurityModule : IModule
{
    public const string BoardKind = "security_board";
    private readonly ISecurityEnvironment _env;

    public SecurityModule() : this(new WindowsSecurityEnvironment()) { }

    internal SecurityModule(ISecurityEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("security", L.T("Sicurezza", "Security"),
        L.T("Antivirus, firewall, account del PC, funzioni di Windows vecchie e porte aperte verso la rete.",
            "Antivirus, firewall, accounts on this PC, old Windows features and ports open to the network."),
        FindingArea.Setup);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.List("unwantedFeatures", L.T("Funzioni di Windows da tenere spente", "Windows features to keep off"),
            L.T("Nomi delle funzioni facoltative di Windows (come li dà Get-WindowsOptionalFeature). Quelle accese vengono segnalate, col pulsante per spegnerle.",
                "Names of Windows optional features (as Get-WindowsOptionalFeature gives them). Those on are reported, with the button to turn them off."),
            "SMB1Protocol", "SMB1Protocol-Client", "SMB1Protocol-Server", "MicrosoftWindowsPowerShellV2Root", "MicrosoftWindowsPowerShellV2", "TelnetClient", "TFTP", "SimpleTCP"),
        SettingDefinition.List("knownAccounts", L.T("Account conosciuti", "Known accounts"),
            L.T("I nomi degli account del PC che conosci. Se l'elenco non è vuoto, un account attivo che non c'è viene segnalato.",
                "The names of the PC's accounts you know. If the list is not empty, an active account not in it is reported.")),
        SettingDefinition.List("allowedPorts", L.T("Porte ammesse", "Allowed ports"),
            L.T("Numeri di porta o nomi di programma che possono restare in ascolto su tutte le reti. Vuoto = le porte si mostrano soltanto.",
                "Port numbers or program names allowed to listen on every network. Empty = ports are only shown.")),
        SettingDefinition.Int("signatureDays", L.T("Età massima delle definizioni (giorni)", "Maximum age of definitions (days)"),
            L.T("Definizioni dei virus più vecchie di così sono un problema.", "Virus definitions older than this are a problem."), 3, 1, 30),
        SettingDefinition.Int("scanDays", L.T("Giorni tra due scansioni complete", "Days between two full scans"),
            L.T("Oltre questo, la mancanza di una scansione completa viene segnalata.", "Beyond this, the lack of a full scan is reported."), 35, 7, 365),
        SettingDefinition.Int("everyHours", L.T("Ogni quante ore controllare", "Check every how many hours"),
            L.T("Il controllo interroga Windows in più punti: non serve farlo spesso. \"Controlla ora\" lo fa subito.", "The check asks Windows in several places: no need to do it often. \"Check now\" does it at once."), 6, 1, 48),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo della sicurezza", "Security check"), TimeSpan.FromMinutes(30), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(SecurityRules.DisableRules, L.T("Spegni le regole del firewall rimaste", "Turn off leftover firewall rules"),
            L.T("Spegne (non cancella) le regole del firewall che spunti: sono di programmi che non ci sono più. Serve l'amministratore.",
                "Turns off (does not delete) the firewall rules you tick: they belong to programs that are gone. Needs administrator rights."),
            Permission.Ask, DoDisableRules,
            Choices: _ => _env.Firewall() is { } fw
                ? [.. SecurityRules.Orphans(fw).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(r => new ActionChoice(r.Id, r.Name, $"{(r.Inbound ? L.T("in entrata", "inbound") : L.T("in uscita", "outbound"))} · {r.Program}", true,
                        r.Allow ? L.T("Permettono", "Allow") : L.T("Bloccano", "Block")))]
                : []),
        new ModuleAction(SecurityRules.DisableFeature, L.T("Spegni una funzione di Windows", "Turn off a Windows feature"),
            L.T("Spegne una funzione facoltativa di Windows dell'elenco \"da tenere spente\" (serve l'amministratore, poi un riavvio). Si riaccende da Funzionalità di Windows.",
                "Turns off a Windows optional feature of the \"keep off\" list (needs administrator rights, then a restart). It can be turned back on from Windows Features."),
            Permission.Ask, DoDisableFeature),
        new ModuleAction(SecurityRules.Open, L.T("Apri", "Open"), L.T("Apre Sicurezza di Windows, gli account o le funzionalità di Windows.", "Opens Windows Security, accounts or Windows Features."),
            Permission.Ask, (_, target, _) => Task.FromResult(SecurityRules.SafeTarget(target) ? _env.Open(target!) : new ActionResult(false, L.T("Indirizzo non valido.", "Invalid address.")))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("security", L.T("Sicurezza", "Security"), BoardKind, [], Days: 365, Menu: ViewMenu.Security,
            Guide: L.T("Antivirus, firewall e account del PC, controllati ogni poche ore. OptLife qui legge soltanto: cambia qualcosa solo quando premi un pulsante.", "Antivirus, firewall and the PC's accounts, checked every few hours. OptLife only reads here: it changes something only when you press a button."),
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("Antivirus, firewall, account, funzioni di Windows e porte aperte. Verde: a posto. Rosso: da sistemare. Ambra: da guardare. Grigio: solo informazione (per le porte, finché non scrivi quelle ammesse). " +
                             "OptLife qui legge soltanto; spegne una regola o una funzione solo quando premi il pulsante.",
                             "Antivirus, firewall, accounts, Windows features and open ports. Green: fine. Red: to fix. Amber: worth a look. Grey: information only (for ports, until you write the allowed ones). " +
                             "OptLife only reads here; it turns a rule or a feature off only when you press the button."),
            Layout: ViewLayout.Checklist),
    ];

    private async Task Check(IModuleContext ctx, CancellationToken ct)
    {
        if (!ctx.OnDemand && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return;
        var s = ctx.Settings;
        var due = ctx.OnDemand || ctx.GetState("read_at") is not { } at || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                  || ctx.Now - t >= TimeSpan.FromHours(s.GetInt("everyHours"));
        if (!due) return;

        var report = await _env.Read(ct);
        var firewall = _env.Firewall();
        ctx.SetState("read_at", ctx.Now.ToString("o", CultureInfo.InvariantCulture));
        var x = new SecurityRules.Inputs(report, firewall, s.GetList("unwantedFeatures"), s.GetList("knownAccounts"), s.GetList("allowedPorts"),
            s.GetInt("signatureDays"), s.GetInt("scanDays"));

        ctx.SyncFindings("security", [.. SecurityRules.Findings(x)]);
        var data = CheckItem.ToData(SecurityRules.Board(x));
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("board")) return;
        ctx.SetState("board", signature);
        var board = SecurityRules.Board(x);
        ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
            L.T($"Sicurezza: {board.Count(i => i.Status == CheckStatus.Wrong)} da sistemare, {board.Count(i => i.Status == CheckStatus.Watch)} da guardare",
                $"Security: {board.Count(i => i.Status == CheckStatus.Wrong)} to fix, {board.Count(i => i.Status == CheckStatus.Watch)} worth a look"),
            BoardKind, Data: data)]);
    }

    private async Task<ActionResult> DoDisableRules(IModuleContext ctx, string? argument, CancellationToken ct)
    {
        var ids = (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        if (ids.Count == 0) return ActionResult.Nothing(L.T("Nessuna regola scelta.", "No rule chosen."));
        // Only rules that are still orphans now: the button never turns off a rule whose program is there.
        var orphans = _env.Firewall() is { } fw ? SecurityRules.Orphans(fw).Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList() : [];
        if (orphans.Count == 0) return ActionResult.Nothing(L.T("Non c'è più niente da spegnere.", "Nothing left to turn off."));
        var result = _env.DisableRules(orphans);
        if (result.Ok) await Check(new Forced(ctx), ct);
        return result;
    }

    private async Task<ActionResult> DoDisableFeature(IModuleContext ctx, string? feature, CancellationToken ct)
    {
        // Only a feature from the person's list: the action cannot be used to turn off anything else.
        if (feature is null || !SecurityRules.Listed(feature, ctx.Settings.GetList("unwantedFeatures")))
            return new ActionResult(false, L.T($"\"{feature}\" non è nell'elenco delle funzioni da tenere spente.", $"\"{feature}\" is not in the list of features to keep off."));
        var result = await _env.DisableFeature(feature, ct);
        if (result.Ok) await Check(new Forced(ctx), ct);
        return result;
    }

    /// <summary>The same context, as asked by the person: right after an action everything is read again.</summary>
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
