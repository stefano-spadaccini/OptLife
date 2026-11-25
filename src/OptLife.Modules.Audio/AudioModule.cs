using System.Text.Json;
using OptLife.Modules.Audio.Windows;

namespace OptLife.Modules.Audio;

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface IAudioEnvironment
{
    /// <summary>Every audio device Windows knows and is still there (old copies of unplugged hardware are left out).</summary>
    IReadOnlyList<AudioDevice> Devices();

    AudioDefaults Defaults();

    /// <summary>Switches a device on or off in Windows' lists, like Sound settings → Disable.</summary>
    ActionResult SetVisible(string id, bool visible);

    /// <summary>Makes it the default for every use (system and games, music and video, calls).</summary>
    ActionResult SetDefault(string id);
}

/// <summary>
/// Audio devices: which may be on (the rest stays off, so a game or an update cannot pick them), and which is the
/// default output and microphone for every use. Reports a device that is new or came back on, with the button to switch it
/// off; never uninstalls drivers. The equalizer check stays where it was, in "PC settings".
/// </summary>
public sealed class AudioModule : IModule
{
    public const string BoardKind = "audio_board";
    private readonly IAudioEnvironment _env;

    public AudioModule() : this(new WindowsAudioEnvironment()) { }

    internal AudioModule(IAudioEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("audio", L.T("Dispositivi audio", "Audio devices"),
        L.T("Quali dispositivi audio possono restare accesi, quale è il predefinito per uscita e microfono, e chi compare o si riaccende da solo.",
            "Which audio devices may stay on, which is the default output and microphone, and which ones appear or come back on by themselves."),
        FindingArea.Setup);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.List("allowed", L.T("Dispositivi ammessi", "Allowed devices"),
            L.T("Parti del nome come lo scrive Windows (es. \"Cuffie\", \"Altoparlanti (Realtek\", \"Microfono (USB\"). Quelli accesi che non sono qui vengono segnalati, col pulsante per spegnerli. Vuoto = nessun controllo.",
                "Parts of the name as Windows writes it (e.g. \"Headphones\", \"Speakers (Realtek\", \"Microphone (USB\"). Those on that are not here are reported, with the button to turn them off. Empty = no check.")).PickFrom(),
        SettingDefinition.Text("defaultOutput", L.T("Uscita predefinita", "Default output"),
            L.T("Parte del nome dell'uscita che deve essere la predefinita per tutti gli usi (sistema, musica, chiamate). Vuoto = nessun controllo.",
                "Part of the name of the output that must be the default for every use (system, music, calls). Empty = no check."), "").PickFrom().Guided(L.T("L'uscita di tutti i giorni (già scelta quella in uso adesso):", "Your everyday output (the one in use now is already chosen):")),
        SettingDefinition.Text("defaultInput", L.T("Microfono predefinito", "Default microphone"),
            L.T("Parte del nome del microfono che deve essere il predefinito per tutti gli usi. Vuoto = nessun controllo.",
                "Part of the name of the microphone that must be the default for every use. Empty = no check."), "").PickFrom().Guided(L.T("Il microfono di tutti i giorni (già scelto quello in uso adesso):", "Your everyday microphone (the one in use now is already chosen):")),
        SettingDefinition.Choice("newDevices", L.T("Dispositivi nuovi non ammessi", "New devices not allowed"),
            L.T("Quando compare (o si riaccende) un dispositivo che non è tra gli ammessi, per esempio l'audio di un monitor ricreato da Windows dopo un riavvio: chiedi = te lo segnala col pulsante per spegnerlo; spegnilo da solo = OptLife lo spegne e lo scrive nello storico (si riaccende dalla pagina Audio). Vale solo se l'elenco degli ammessi non è vuoto.",
                "When a device that is not allowed appears (or comes back on), for example a monitor's sound made again by Windows after a restart: ask = it is reported with the button to turn it off; turn it off by itself = OptLife turns it off and writes it in the history (it turns back on from the Audio page). Only when the allowed list is not empty."),
            L.T("chiedi", "ask"), L.T("spegnilo da solo", "turn it off by itself")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo dei dispositivi audio", "Checking audio devices"), TimeSpan.FromMinutes(5), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(AudioRules.Hide, L.T("Spegni il dispositivo", "Turn the device off"),
            L.T("Lo spegne in Windows come Impostazioni audio → Disattiva: sparisce dagli elenchi, il driver resta. Si riaccende col pulsante.",
                "Turns it off in Windows like Sound settings → Disable: it leaves the lists, the driver stays. It turns back on with the button."),
            Permission.Ask, (ctx, id, ct) => Do(ctx, id, ct, () => _env.SetVisible(id!, false), AudioRules.Show)),
        new ModuleAction(AudioRules.Show, L.T("Riaccendi il dispositivo", "Turn the device back on"),
            L.T("Lo riaccende in Windows.", "Turns it back on in Windows."),
            Permission.Ask, (ctx, id, ct) => Do(ctx, id, ct, () => _env.SetVisible(id!, true), AudioRules.Hide)),
        new ModuleAction(AudioRules.SetDefault, L.T("Imposta come predefinito", "Set as default"),
            L.T("Lo rende il predefinito per sistema e giochi, musica e video, chiamate.", "Makes it the default for system and games, music and video, calls."),
            Permission.Ask, (ctx, id, ct) => Do(ctx, id, ct, () => _env.SetDefault(id!), null)),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("audio", L.T("Audio", "Audio"), BoardKind, [], Days: 365, Menu: ViewMenu.Status,
            Guide: L.T("Cuffie, casse e microfoni. Se dici qual è l'uscita e il microfono di tutti i giorni, OptLife ti avvisa quando Windows li cambia da solo (succede dopo gli aggiornamenti).", "Headphones, speakers and microphones. If you say which output and microphone you use every day, OptLife warns you when Windows changes them by itself (it happens after updates)."),
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("I predefiniti in cima, poi ogni uscita e ingresso che Windows conosce: verde = acceso, rosso = spento. " +
                             "Spegnere un dispositivo lo toglie dagli elenchi di Windows e dei giochi, senza toccare il driver. Se vuoi, in IMPOSTAZIONI → Dispositivi audio puoi scrivere quali devono restare accesi e quale è il predefinito: OptLife ti avvisa quando qualcosa cambia.",
                             "Defaults on top, then every output and input Windows knows: green = on, red = off. " +
                             "Turning a device off removes it from Windows' and games' lists, without touching the driver. If you like, in SETTINGS → Audio devices you can write which must stay on and which is the default: OptLife warns you when something changes."),
            Layout: ViewLayout.Checklist),
    ];

    private AudioRules.Inputs Read(IModuleContext ctx, IReadOnlyList<AudioDevice> devices, AudioDefaults defaults) => new(
        devices, defaults, [.. ctx.Settings.GetList("allowed").Where(a => a.Trim().Length > 0)],
        ctx.Settings.GetText("defaultOutput").Trim(), ctx.Settings.GetText("defaultInput").Trim(),
        (Load<Dictionary<string, bool>>(ctx, "seen") ?? []).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
        Load<HashSet<string>>(ctx, "was_off") ?? []);

    private Task Check(IModuleContext ctx, CancellationToken ct)
    {
        if (!ctx.OnDemand && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;
        var devices = _env.Devices();
        var defaults = _env.Defaults();
        var x = Read(ctx, devices, defaults);
        // For the guided tour: the devices in use now are the likely everyday ones.
        string? NameOf(string? id) => devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name;
        ctx.SetState("suggest.defaultOutput", NameOf(defaults.Output.FirstOrDefault()));
        ctx.SetState("suggest.defaultInput", NameOf(defaults.Input.FirstOrDefault()));
        // What can be picked in the settings and the tour: the devices as Windows names them (the on ones for the defaults).
        var none = new SettingOption("", L.T("Nessun controllo", "No check"));
        SettingOption[] Of(Flow flow, bool onlyOn) => [.. devices.Where(d => d.Flow == flow && (!onlyOn || d.On && !d.Unplugged)).Select(d => d.Name).Distinct().Order().Select(n => new SettingOption(n, n))];
        SettingOption.Save(ctx, "defaultOutput", [none, .. Of(Flow.Output, true)]);
        SettingOption.Save(ctx, "defaultInput", [none, .. Of(Flow.Input, true)]);
        SettingOption.Save(ctx, "allowed", [.. Of(Flow.Output, false), .. Of(Flow.Input, false)]);

        var seen = Load<Dictionary<string, bool>>(ctx, "seen");
        var signature = AudioRules.Signature(defaults);
        if (seen is not null) ctx.RecordEvents([.. AudioRules.Changes(seen, devices, ctx.GetState("defaults"), signature, defaults, ctx.Now)]);
        // The first look knows nothing "new": what is there now is the starting point.
        if (seen is null) x = x with { Known = devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) };

        // Chosen by the person: what appears or comes back on and is not allowed is turned off at once, and said in the history.
        if (seen is not null && ctx.Settings.GetChoice("newDevices") == L.T("spegnilo da solo", "turn it off by itself"))
        {
            var turned = new List<NewEvent>();
            var autoOff = Load<HashSet<string>>(ctx, "auto_off") ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Turned off by OptLife and on again with the same entry: the person switched it on. Said once in the history.
            foreach (var d in devices.Where(d => d.On && autoOff.Contains(d.Id)).ToList())
            {
                turned.Add(new NewEvent(ctx.Now, "audio_device_kept", Severity.Info,
                    L.T($"Lasciato acceso come hai scelto: {d.Name}", $"Left on as you chose: {d.Name}"), $"audio_kept:{d.Id}"));
                autoOff.Remove(d.Id);
            }
            foreach (var d in AudioRules.ToTurnOff(x))
                if (_env.SetVisible(d.Id, false).Ok && autoOff.Add(d.Id))
                    turned.Add(new NewEvent(ctx.Now, "audio_device_disabled", Severity.Info,
                        AudioRules.Recreated(d, devices)
                            ? L.T($"Spento da solo: {d.Name} (Windows l'aveva ricreato)", $"Turned off by itself: {d.Name} (Windows had made it again)")
                            : L.T($"Spento da solo: {d.Name} (non è tra gli ammessi)", $"Turned off by itself: {d.Name} (not allowed)"),
                        $"audio_auto_off:{d.Id}"));
            ctx.SetState("auto_off", JsonSerializer.Serialize(autoOff));
            if (turned.Count > 0)
            {
                ctx.RecordEvents(turned);
                devices = _env.Devices();
                x = x with { Devices = devices };
            }
        }

        ctx.SyncFindings("audio", [.. AudioRules.Findings(x)]);

        // Remembered after judging, so a device stays "new" until it is dealt with only on the round it appeared.
        var known = (seen ?? []).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices) known[d.Id] = d.On;
        ctx.SetState("seen", JsonSerializer.Serialize(known));
        ctx.SetState("defaults", signature);
        var wasOff = x.WasOff.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = wasOff.Count;
        wasOff.UnionWith(devices.Where(d => !d.On).Select(d => d.Id));
        if (wasOff.Count != before) ctx.SetState("was_off", JsonSerializer.Serialize(wasOff));

        var data = CheckItem.ToData(AudioRules.Board(x));
        var board = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (board != ctx.GetState("board"))
        {
            ctx.SetState("board", board);
            ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
                L.T($"Audio: {devices.Count(d => d.On)} dispositivi accesi su {devices.Count}", $"Audio: {devices.Count(d => d.On)} devices on out of {devices.Count}"),
                BoardKind, Data: data)]);
        }
        return Task.CompletedTask;
    }

    private async Task<ActionResult> Do(IModuleContext ctx, string? id, CancellationToken ct, Func<ActionResult> act, string? undoAction)
    {
        if (string.IsNullOrWhiteSpace(id) || _env.Devices().FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } device)
            return new ActionResult(false, L.T("Il dispositivo non c'è più.", "The device is no longer there."));
        var result = act();
        if (!result.Ok) return result;
        await Check(new Forced(ctx), ct);
        return result with
        {
            Detail = $"{device.Name}: {result.Detail}",
            Undo = undoAction is null ? null : $"optlife do audio {undoAction} \"{id}\" --yes",
        };
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

    /// <summary>The same context, as asked by the person: right after an action the page is rewritten even during a game.</summary>
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
