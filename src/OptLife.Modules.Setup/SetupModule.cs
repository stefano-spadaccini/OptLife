using System.Globalization;
using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Setup.Windows;

namespace OptLife.Modules.Setup;

/// <summary>One thing about how the PC is set up, as found right now.</summary>
/// <param name="Key">Stable name of the thing ("display.GSM5B7F.mode"), independent of its value.</param>
/// <param name="Problem">Set when the value is wrong in itself, whatever it was before (a 165 Hz screen running at 60).</param>
/// <param name="Note">A remark on something guides disagree about: shown as information, the person decides.</param>
/// <param name="Good">For the Setup page: true when the value is the right one, false when it is worth a look, null when there is no right answer.</param>
/// <param name="Expected">For the Setup page: the right value in words ("144 Hz, la massima").</param>
public sealed record Reading(
    string Key,
    string Label,
    string Value,
    string? Problem = null,
    string? Advice = null,
    Severity Severity = Severity.Low,
    string? Note = null,
    FindingFix? Fix = null,
    bool? Good = null,
    string? Expected = null);

/// <summary>What the module needs to know about the machine, so its judgement can be tested without one.</summary>
public interface IPcProbe
{
    IReadOnlyList<Reading> ReadSettings();

    /// <summary>The parts the PC is made of, by name ("Scheda video" → "NVIDIA GeForce RTX 3080").</summary>
    IReadOnlyDictionary<string, string> ReadHardware();

    /// <summary>Versions of what gets updated under the person's feet: Windows, graphics driver, BIOS.</summary>
    IReadOnlyDictionary<string, string> ReadVersions();

    /// <summary>Carries out one of the module's actions on the machine.</summary>
    ActionResult Apply(string action, string? argument);
}

/// <summary>
/// Checks that the PC is set up the way its hardware deserves, and notices when something is
/// changed behind the person's back. Two kinds of judgement:
///  - rules: values wrong in themselves (screen below its top refresh rate, memory profile off);
///  - reference: the first value seen for each setting is remembered, and a later difference is
///    reported together with what was updated shortly before (the usual culprit).
/// The checks are derived from the hardware found, so they follow it when it changes.
/// It only reads: nothing is changed on the PC.
/// </summary>
public sealed class SetupModule : IModule
{
    private const string StateKey = "state";
    private static readonly TimeSpan CauseWindow = TimeSpan.FromHours(72);
    private static CultureInfo Lang => L.Culture;
    private readonly IPcProbe _probe;

    public SetupModule() : this(new WindowsPcProbe()) { }

    internal SetupModule(IPcProbe probe) => _probe = probe;

    public ModuleInfo Info { get; } = new(
        "setup", L.T("Impostazioni del PC", "PC settings"),
        L.T("Controlla che schermi, memoria, energia e Windows siano impostati come l'hardware merita, e avvisa quando un aggiornamento rimette qualcosa com'era.", "Checks that screens, memory, power and Windows are set up as the hardware deserves, and warns when an update puts something back as it was."), FindingArea.Setup);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Bool("showAdvice", L.T("Mostra anche i consigli discutibili", "Also show debatable advice"),
            L.T("Voci su cui le guide non sono d'accordo (per esempio l'integrità della memoria): compaiono come informazione, decidi tu.", "Items guides disagree on (for example memory integrity): shown as information, you decide."), true),
        SettingDefinition.List("dns", L.T("DNS da usare", "DNS to use"),
            L.T("Gli indirizzi dei server DNS, nell'ordine (es. 1.1.1.1 poi 8.8.8.8). Se la connessione in uso ne ha altri è segnalato, e un pulsante li rimette. Vuoto: nessun controllo.",
                "The DNS server addresses, in order (e.g. 1.1.1.1 then 8.8.8.8). If the connection in use has others it is reported, and a button puts them back. Empty: no check.")),
        SettingDefinition.Pairs("expected", [L.T("Voce", "Item"), L.T("Valore", "Value")], L.T("Valori che devono restare così", "Values that must stay as they are"),
            L.T("Scritti come nome=valore, con nome e valore come compaiono nello storico alla riga \"Impostazioni del PC\" " +
            "(per esempio gpu.power_limit=250 W). Se il valore trovato è diverso viene segnalato come problema, anche al primo controllo.",
            "Written as name=value, with name and value as they appear in the history on the \"PC settings\" line (for example gpu.power_limit=250 W). If the value found is different it is reported as a problem, even at the first check.")),
        SettingDefinition.List("ignored", L.T("Voci da non controllare", "Items not to check"),
            L.T("Nomi delle voci da saltare del tutto, come compaiono nello storico alla riga \"Impostazioni del PC\" (per esempio windows.gamemode).", "Names of the items to skip entirely, as they appear in the history on the \"PC settings\" line (for example windows.gamemode).")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo delle impostazioni", "Checking the settings"), TimeSpan.FromMinutes(10), CheckAsync),
    ];

    public const string BoardKind = "setup_board";

    /// <summary>The Setup page: every checked thing, how it is, how it should be, in colour.</summary>
    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("setup", "Setup", BoardKind, [], Days: 365, Menu: ViewMenu.Status, Order: -10,
            Guide: L.T("Come è impostato il PC: schermi alla frequenza giusta, memoria alla sua velocità, piano energetico. Ciò che è da sistemare compare anche tra i Problemi, spesso con il pulsante che lo sistema.", "How the PC is set up: screens at the right refresh rate, memory at its speed, power plan. What needs fixing also shows among the Problems, often with the button that fixes it."),
            EmptyText: L.T("Ancora nessun controllo: compare dopo il primo giro del modulo (entro 10 minuti dall'avvio).", "Not checked yet: it appears after the module's first round (within 10 minutes of start-up)."),
            Description: L.T("Le impostazioni che contano per un PC da gioco, com'è ognuna adesso e come dovrebbe essere. " +
                         "Verde: a posto. Ambra: da guardare. Rosso: da sistemare. Grigio: non c'è una risposta giusta, è una scelta tua. " +
                         "Si aggiorna ogni 10 minuti, non durante i giochi.",
                         "The settings that matter for a gaming PC, how each one is now and how it should be. Green: fine. Amber: worth a look. Red: to fix. Grey: there is no right answer, it is your choice. Updated every 10 minutes, not during games."),
            Layout: ViewLayout.Checklist),
    ];

    public const string SetDns = "set-dns";
    public const string SetRefresh = "set-refresh", SetPowerPlan = "set-power-plan", EnableGameMode = "enable-game-mode", OpenEqualizer = "open-eq-selector";

    /// <summary>
    /// The few things that can be put right without administrator rights and undone from Windows' own
    /// settings. Each asks first unless the person chooses otherwise; none touches the BIOS or drivers.
    /// </summary>
    public IReadOnlyList<ModuleAction> Actions =>
    [
        Action(SetDns, L.T("Rimetti il DNS scelto", "Put the chosen DNS back"),
            L.T("Imposta sulla connessione i server DNS scritti nelle impostazioni di questo modulo. Serve l'amministratore.", "Sets on the connection the DNS servers written in this module's settings. Needs administrator rights.")),
        Action(SetRefresh, L.T("Porta lo schermo alla frequenza massima", "Set the screen to its highest refresh rate"),
            L.T("Imposta la frequenza di aggiornamento più alta che lo schermo offre alla risoluzione in uso.", "Sets the highest refresh rate the screen offers at the resolution in use.")),
        Action(SetPowerPlan, L.T("Togli il piano Risparmio di energia", "Leave the Power saver plan"),
            L.T("Attiva il piano energetico Bilanciato al posto di Risparmio di energia.", "Switches to the Balanced power plan instead of Power saver.")),
        Action(EnableGameMode, L.T("Accendi la Modalità gioco", "Turn on Game Mode"),
            L.T("Riaccende la Modalità gioco di Windows.", "Turns Windows Game Mode back on.")),
        Action(OpenEqualizer, L.T("Apri il selettore di Equalizer APO", "Open the Equalizer APO selector"),
            L.T("Apre il programma di Equalizer APO in cui spuntare l'uscita audio in uso. Windows chiede il permesso di amministratore; il resto lo fai tu.", "Opens the Equalizer APO program where you tick the audio output in use. Windows asks for administrator permission; you do the rest.")),
    ];

    private ModuleAction Action(string id, string name, string description) => new(id, name, description, Permission.Ask, (ctx, argument, _) =>
    {
        var result = _probe.Apply(id, argument);
        // What the next check finds is the result of this action: believe it at once instead of waiting to see it twice.
        if (result.Ok) ctx.SetState(TrustKey, "1");
        return Task.FromResult(result);
    });

    private const string TrustKey = "trust";

    private Task CheckAsync(IModuleContext ctx, CancellationToken ct)
    {
        // During a game the screen mode may be the game's own, and it is no time for extra work.
        if (ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;

        var state = Load(ctx);
        var now = ctx.Now;
        var trust = ctx.GetState(TrustKey) is not null;
        if (trust) ctx.SetState(TrustKey, null);

        // ── What was updated or replaced: recorded first, so a setting that moved can be tied to it ──
        var changes = new List<NewEvent>();
        foreach (var (part, value) in _probe.ReadVersions())
        {
            if (state.Versions.TryGetValue(part, out var old) && old != value)
                changes.Add(new NewEvent(now, "system_update", Severity.Info, L.T($"{part}: aggiornamento da {old} a {value}", $"{part}: updated from {old} to {value}"), $"update|{part}"));
            state.Versions[part] = value;
        }
        var hardwareChanged = false;
        foreach (var (part, value) in _probe.ReadHardware())
        {
            if (state.Hardware.TryGetValue(part, out var old) && old != value)
            {
                hardwareChanged = true;
                changes.Add(new NewEvent(now, "hardware_change", Severity.Info, L.T($"Hardware cambiato · {part}: {value} (prima: {old})", $"Hardware changed · {part}: {value} (before: {old})"), $"hardware|{part}"));
            }
            state.Hardware[part] = value;
        }
        ctx.RecordEvents(changes);
        var cause = RecentChange(ctx, now);

        // ── Settings: a new value counts only when seen twice in a row, so a passing state is not reported ──
        var ignored = ctx.Settings.GetList("ignored");
        var wantedDns = ctx.Settings.GetList("dns");
        var readings = _probe.ReadSettings().Where(r => !ignored.Contains(r.Key, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Key.StartsWith("network.dns", StringComparison.Ordinal) ? SetupRules.JudgeDns(r, wantedDns) : r).ToList();
        var moved = new List<NewEvent>();
        foreach (var r in readings)
        {
            // Never seen before, or new parts in the PC: what is found now is the new normal, taken at once.
            // A value that is wrong in itself is never taken as normal: the reference waits for a sound one.
            if (hardwareChanged || !state.Items.TryGetValue(r.Key, out var item))
            {
                // The update of the last days is kept with it: a BIOS setting first seen off right after a BIOS update was most likely reset by it.
                state.Items[r.Key] = new Item { Baseline = r.Problem is null ? r.Value : null, Current = r, Cause = cause };
                continue;
            }
            var signature = $"{r.Value}|{r.Problem}";
            if (signature == $"{item.Current.Value}|{item.Current.Problem}")
            {
                item.Current = r;
                item.Candidate = null;
            }
            else if (trust || item.Candidate == signature)
            {
                if (r.Value != item.Current.Value)
                    moved.Add(new NewEvent(now, "setting_changed", Severity.Low, L.T($"{r.Label}: da \"{item.Current.Value}\" a \"{r.Value}\"", $"{r.Label}: from \"{item.Current.Value}\" to \"{r.Value}\""), $"setting|{r.Key}"));
                item.Current = r;
                item.Candidate = null;
                item.Since = now;
                item.Cause = cause;
            }
            else
            {
                item.Candidate = signature;
            }
            if (item.Baseline is null && item.Current.Problem is null) item.Baseline = item.Current.Value;
            // Now as it should be (SVM turned back on): that is the new normal, nothing to warn about (on 24 Nov "on" was reported as a factory reset).
            else if (item.Current.Good == true && item.Current.Problem is null && item.Baseline != item.Current.Value && item.Candidate is null) item.Baseline = item.Current.Value;
        }
        ctx.RecordEvents(moved);

        // Values the person pinned ("gpu.power_limit=250 W"): for those the reference is what they wrote, not what was found.
        var expected = ctx.Settings.GetList("expected")
            .Select(e => e.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(e => e.Length == 2 && e[0].Length > 0)
            .ToDictionary(e => e[0], e => e[1], StringComparer.OrdinalIgnoreCase);

        // ── Problems, only for what exists right now (a screen that is switched off has nothing to say) ──
        var showAdvice = ctx.Settings.GetBool("showAdvice");
        var findings = new List<NewFinding>();
        var current = new List<Reading>();
        foreach (var r in readings)
        {
            var item = state.Items[r.Key];
            var c = item.Current;
            var pinned = expected.TryGetValue(c.Key, out var want);
            if (pinned && c.Problem is null && !want!.Equals(c.Value, StringComparison.OrdinalIgnoreCase))
                c = c with { Problem = L.T($"{c.Label}: dovrebbe essere \"{want}\" ma è \"{c.Value}\"", $"{c.Label}: should be \"{want}\" but is \"{c.Value}\""), Severity = Severity.Medium };
            current.Add(c);
            // The BIOS version changes with every update: the update itself is in the history, it is not a warning.
            var changed = !pinned && item.Baseline is not null && c.Value != item.Baseline && c.Key != "bios.version";
            // A BIOS update puts every BIOS setting back to factory values: said in plain words.
            var biosReset = (changed || c.Problem is not null) && IsBiosSetting(c.Key) && item.Cause is { } why && why.Contains("BIOS:", StringComparison.Ordinal);
            var story = (changed
                ? L.T($" Prima era \"{item.Baseline}\"", $" It was \"{item.Baseline}\"") + (item.Since is { } since ? L.T("; è cambiato ", "; it changed ") + When(since) + "." : ".") + (item.Cause is null ? "" : $" {item.Cause}")
                : "") + (biosReset ? " " + BiosResetText : "");

            if (c.Problem is not null)
                findings.Add(new NewFinding(c.Key, c.Severity, c.Problem, $"{c.Label}: {c.Value}.{story}", c.Advice, c.Fix));
            else if (changed)
                findings.Add(new NewFinding($"{c.Key}={c.Value}", biosReset ? Severity.Medium : Severity.Low,
                    biosReset ? L.T($"Dopo l'aggiornamento del BIOS {c.Label} è tornato di fabbrica: ora è \"{c.Value}\"", $"After the BIOS update {c.Label} went back to factory: now it is \"{c.Value}\"")
                        : L.T($"{c.Label} è cambiato: ora è \"{c.Value}\"", $"{c.Label} changed: now it is \"{c.Value}\""),
                    $"{c.Label}: {c.Value}.{story}",
                    L.T("Se l'hai cambiato tu e va bene così, ignora questo avviso: tornerà solo se cambia di nuovo.", "If you changed it and it is fine, ignore this warning: it comes back only if it changes again.") + (c.Advice is null ? "" : L.T(" Altrimenti: ", " Otherwise: ") + c.Advice)));
            else if (c.Good == false && IsBiosSetting(c.Key) && item.Cause is { } update && update.Contains("BIOS:", StringComparison.Ordinal))
                // Not as it should be, and first seen (or last changed) right after a BIOS update: the update put it back to factory.
                findings.Add(new NewFinding($"{c.Key}={c.Value}", Severity.Medium,
                    L.T($"Dopo l'aggiornamento del BIOS {c.Label} è \"{c.Value}\": probabilmente è tornato di fabbrica", $"After the BIOS update {c.Label} is \"{c.Value}\": it most likely went back to factory"),
                    $"{c.Label}: {c.Value}. {update} {BiosResetText}",
                    c.Advice + L.T(" Se va bene così, ignora questo avviso.", " If it is fine like this, ignore this warning.")));

            if (showAdvice && c.Note is not null)
                findings.Add(new NewFinding($"note:{c.Key}={c.Value}", Severity.Info, $"{c.Label}: {c.Value}", c.Note,
                    L.T("È una scelta tua: se va bene così, ignora questo avviso.", "It is your choice: if it is fine, ignore this warning.")));
        }
        ctx.SyncFindings("setup", findings);

        // ── The Setup page: the same readings, each with how it stands, then the parts of the PC ──
        var board = current.Select(c =>
        {
            var item = state.Items[c.Key];
            var pinned = expected.TryGetValue(c.Key, out var want);
            var changed = !pinned && item.Baseline is not null && c.Value != item.Baseline && c.Key != "bios.version";
            var status = c.Problem is not null ? (c.Severity >= Severity.Medium ? CheckStatus.Wrong : CheckStatus.Watch)
                : pinned ? CheckStatus.Ok
                : c.Good is true ? CheckStatus.Ok
                : c.Good is false ? CheckStatus.Watch
                : changed ? CheckStatus.Watch
                : CheckStatus.Info;
            // What to read under the line: why it matters when there is a doubt, where to change it otherwise.
            var advice = changed && c.Problem is null ? L.T($"Prima era \"{item.Baseline}\". ", $"It was \"{item.Baseline}\". ") + c.Advice : c.Note ?? c.Advice;
            return new CheckItem(GroupOf(c.Key), c.Label, c.Value, status, pinned ? want : c.Expected, advice);
        }).OrderBy(i => Array.IndexOf(Groups, i.Group)).ToList();
        board.AddRange(state.Hardware.Concat(state.Versions)
            .Select(p => new CheckItem(L.T("Componenti e versioni", "Parts and versions"), p.Key, p.Value, CheckStatus.Info)));
        var boardData = CheckItem.ToData(board);
        var boardSignature = string.Join('\n', boardData.OrderBy(p => p.Key).Select(p => p.Value));
        if (boardSignature != ctx.GetState(BoardStateKey) && board.Count > 0)
        {
            ctx.SetState(BoardStateKey, boardSignature);
            var wrong = board.Count(i => i.Status == CheckStatus.Wrong);
            var watch = board.Count(i => i.Status == CheckStatus.Watch);
            ctx.RecordEvents([new NewEvent(now, BoardKind, Severity.Info,
                L.T($"Setup: {board.Count(i => i.Status == CheckStatus.Ok)} a posto, {watch} da guardare, {wrong} da sistemare", $"Setup: {board.Count(i => i.Status == CheckStatus.Ok)} fine, {watch} worth a look, {wrong} to fix"),
                BoardKind, Data: boardData)]);
        }

        // ── One line in the history each time the overall picture changes: the full list, readable by anyone ──
        var picture = string.Join('\n', current.Select(c => $"{c.Key}={c.Value}|{c.Problem}"));
        if (picture != state.Picture && current.Count > 0)
        {
            state.Picture = picture;
            var wrong = current.Count(c => c.Problem is not null);
            ctx.RecordEvents([new NewEvent(now, "settings_check", Severity.Info,
                L.T($"Impostazioni del PC: {current.Count} voci controllate, ", $"PC settings: {current.Count} items checked, ") + (wrong == 0 ? L.T("tutte a posto", "all fine") : wrong == 1 ? L.T("1 da sistemare", "1 to fix") : L.T($"{wrong} da sistemare", $"{wrong} to fix")),
                "settings_check",
                Data: current.ToDictionary(c => c.Key, c => $"{c.Label} = {c.Value}" + (c.Problem is null ? "" : L.T(" (da sistemare)", " (to fix)"))))]);
        }

        state.Lang = L.Code;
        ctx.SetState(StateKey, JsonSerializer.Serialize(state));
        return Task.CompletedTask;
    }

    private const string BoardStateKey = "board";

    private static bool IsBiosSetting(string key) => key.StartsWith("bios.", StringComparison.Ordinal) || key.StartsWith("ram.", StringComparison.Ordinal);

    private static string BiosResetText => L.T(
        "Dopo l'aggiornamento del BIOS questi valori sono tornati di fabbrica: rientra nel BIOS (Strumenti → Riavvia nel BIOS) e rimettili come prima.",
        "After the BIOS update these values went back to factory: go back into the BIOS (Tools → Restart into the BIOS) and set them as before.");

    private static string BiosGroup => L.T("BIOS e firmware", "BIOS and firmware");

    private static string[] Groups => [BiosGroup, L.T("Schermi", "Screens"), L.T("Memoria", "Memory"), L.T("Energia", "Power"), L.T("Scheda video", "Graphics card"), L.T("Rete", "Network"), "Windows", "Audio"];

    /// <summary>The section of the Setup page a reading belongs to, from the first part of its name.</summary>
    internal static string GroupOf(string key) => key.Split('.')[0] switch
    {
        "display" => L.T("Schermi", "Screens"),
        // The memory profile is a BIOS setting: it sits with the others (version, Secure Boot, TPM, Resizable BAR…).
        "ram" or "bios" => BiosGroup,
        "power" => L.T("Energia", "Power"),
        "gpu" => L.T("Scheda video", "Graphics card"),
        "audio" => "Audio",
        "network" => L.T("Rete", "Network"),
        _ => "Windows",
    };

    /// <summary>The latest update or hardware change of the last three days, as a sentence; null when there was none.</summary>
    private static string? RecentChange(IModuleContext ctx, DateTimeOffset now)
    {
        var latest = new[] { "system_update", "hardware_change" }
            .SelectMany(kind => ctx.History.Events(now - CauseWindow, now, ctx.ModuleId, kind))
            .MaxBy(e => e.Time);
        return latest is null ? null : L.T($"Poco prima ({When(latest.Time)}): {latest.Title}.", $"Shortly before ({When(latest.Time)}): {latest.Title}.");
    }

    private static string When(DateTimeOffset time) => time.ToLocalTime().ToString(L.T("'il' d MMMM 'alle' HH:mm", "'on' d MMMM 'at' HH:mm"), Lang);

    private static SetupState Load(IModuleContext ctx)
    {
        try
        {
            var state = ctx.GetState(StateKey) is { } json ? JsonSerializer.Deserialize<SetupState>(json) ?? new SetupState() : new SetupState();
            // The values are words ("accesa", "on"): after a change of language every one would look changed.
            // Start over, as on the first run: what is found now becomes the reference, with no warnings.
            return state.Items.Count > 0 && state.Lang != L.Code ? new SetupState() : state;
        }
        catch (JsonException)
        {
            return new SetupState(); // Unreadable memory: start over, as on the first run.
        }
    }

    private sealed class SetupState
    {
        public Dictionary<string, Item> Items { get; set; } = [];
        public Dictionary<string, string> Hardware { get; set; } = [];
        public Dictionary<string, string> Versions { get; set; } = [];
        public string Picture { get; set; } = "";

        /// <summary>The language the values were written in.</summary>
        public string Lang { get; set; } = "";
    }

    private sealed class Item
    {
        /// <summary>
        /// The value taken as normal: the first sound one seen, or the one found after a hardware change.
        /// Null while the only values seen so far were wrong in themselves.
        /// </summary>
        public string? Baseline { get; set; }
        public Reading Current { get; set; } = new("", "", "");

        /// <summary>A different value seen once, waiting to be seen again before it is believed.</summary>
        public string? Candidate { get; set; }
        public DateTimeOffset? Since { get; set; }
        public string? Cause { get; set; }
    }
}
