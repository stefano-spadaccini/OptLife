using System.Text;

namespace OptLife.Modules.Audio;

public enum Flow { Output, Input }

/// <summary>
/// One audio device as Windows lists it in Sound settings.
/// </summary>
/// <param name="Id">Windows' endpoint id ("{0.0.0.00000000}.{guid}"): what the actions take.</param>
/// <param name="Name">"Speakers (USB Audio DAC)": what it is, then the hardware.</param>
/// <param name="On">Usable: not switched off by the person (it shows in Windows' lists).</param>
/// <param name="Unplugged">On, but nothing is connected to it right now (a jack with no cable).</param>
public sealed record AudioDevice(string Id, Flow Flow, string Name, bool On, bool Unplugged = false);

/// <summary>The default device of each role (0 console, 1 multimedia, 2 communications) for one direction.</summary>
public sealed record AudioDefaults(IReadOnlyList<string?> Output, IReadOnlyList<string?> Input);

/// <summary>The rules of the audio page, without the machine.</summary>
public static class AudioRules
{
    public const string Hide = "hide-device", Show = "show-device", SetDefault = "set-default";

    /// <summary>The roles Windows keeps a default for, by their number.</summary>
    public static string RoleName(int role) => role switch
    {
        0 => L.T("sistema e giochi", "system and games"),
        1 => L.T("musica e video", "music and video"),
        _ => L.T("chiamate", "calls"),
    };

    /// <summary>
    /// What Windows keeps in DeviceState: the low bits say present (1 active, 4 gone, 8 unplugged), 0x10000000 that the person
    /// switched it off in Sound settings. Null for an entry of hardware no longer there: an old copy, not a device.
    /// </summary>
    public static (bool On, bool Unplugged)? FromState(int state)
    {
        var low = state & 0xF;
        if (low is 4 or 0) return null;
        var off = (state & 0x10000000) != 0 || low == 2;
        return (!off, low == 8);
    }

    public static string Norm(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool Matches(string name, string pattern) => Norm(pattern) is { Length: > 0 } p && Norm(name).Contains(p, StringComparison.Ordinal);

    public static bool Allowed(AudioDevice d, IReadOnlyList<string> allowed) => allowed.Any(a => Matches(d.Name, a));

    /// <summary>The device that should be the default: the first one on that matches the wanted name, preferring one with something connected.</summary>
    public static AudioDevice? Wanted(IReadOnlyList<AudioDevice> devices, Flow flow, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) ? null
            : devices.Where(d => d.Flow == flow && d.On && Matches(d.Name, wanted)).OrderBy(d => d.Unplugged).FirstOrDefault();

    private static string OnOff(AudioDevice d) => !d.On ? L.T("Spento", "Off") : d.Unplugged ? L.T("Acceso, niente collegato", "On, nothing plugged in") : L.T("Acceso", "On");

    public static string OutputGroup => L.T("Uscite", "Outputs");
    public static string InputGroup => L.T("Ingressi", "Inputs");
    public static string DefaultsGroup => L.T("Predefiniti", "Defaults");

    private static string FlowGroup(Flow f) => f == Flow.Output ? OutputGroup : InputGroup;

    public sealed record Inputs(IReadOnlyList<AudioDevice> Devices, AudioDefaults Defaults, IReadOnlyList<string> Allowed,
        string? DefaultOutput, string? DefaultInput, IReadOnlySet<string> Known, IReadOnlySet<string> WasOff);

    public static IReadOnlyList<CheckItem> Board(Inputs x)
    {
        var board = new List<CheckItem>();
        foreach (var (flow, wanted, current) in new[] { (Flow.Output, x.DefaultOutput, x.Defaults.Output), (Flow.Input, x.DefaultInput, x.Defaults.Input) })
        {
            if (string.IsNullOrWhiteSpace(wanted)) continue;
            var target = Wanted(x.Devices, flow, wanted);
            var label = flow == Flow.Output ? L.T("Uscita predefinita", "Default output") : L.T("Microfono predefinito", "Default microphone");
            var names = current.Select(id => x.Devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? L.T("nessuno", "none")).ToList();
            var right = target is not null && current.All(id => target.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var value = names.Distinct().Count() == 1 ? names[0] : string.Join(" · ", names.Select((n, r) => $"{RoleName(r)}: {n}"));
            board.Add(new CheckItem(DefaultsGroup, label, value, right ? CheckStatus.Ok : target is null ? CheckStatus.Watch : CheckStatus.Wrong,
                target is null ? L.T($"{wanted}: non collegato", $"{wanted}: not connected") : L.T($"{target.Name}, per tutti gli usi", $"{target.Name}, for every use"),
                target is null ? L.T("Il dispositivo scelto come predefinito non c'è o è spento: collegalo, o cambia il nome in IMPOSTAZIONI → Dispositivi audio.",
                                     "The device chosen as default is missing or off: connect it, or change the name in SETTINGS → Audio devices.")
                               : L.T("Windows tiene un predefinito per sistema e giochi, uno per musica e video e uno per le chiamate: devono essere tutti lo stesso.",
                                     "Windows keeps one default for system and games, one for music and video and one for calls: they should all be the same."),
                Buttons: !right && target is not null ? [new CheckButton(SetDefault, target.Id, L.T("IMPOSTA", "SET"))] : null));
        }

        // Coloured by state only (green on, red off), the same for anyone; the allowed list is an optional wish that adds a line when differing.
        foreach (var d in x.Devices.OrderBy(d => d.Flow).ThenBy(d => !d.On).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var judged = x.Allowed.Count > 0;
            var allowed = Allowed(d, x.Allowed);
            var wish = !judged ? "" : allowed && !d.On ? L.T("\nÈ tra i tuoi dispositivi ammessi ma è spento.", "\nIt is one of your allowed devices but it is off.")
                : !allowed && d.On ? L.T("\nNon è tra i tuoi dispositivi ammessi.", "\nIt is not one of your allowed devices.") : "";
            var state = d.On ? CheckStatus.Ok : CheckStatus.Wrong;
            board.Add(new CheckItem(FlowGroup(d.Flow), d.Name, OnOff(d) + wish, state, null, null,
                !judged ? null : allowed ? L.T("Ammessi", "Allowed") : L.T("Altri", "Others"),
                [d.On ? new CheckButton(Hide, d.Id, L.T("SPEGNI", "TURN OFF")) : new CheckButton(Show, d.Id, L.T("ACCENDI", "TURN ON"))],
                state, ""));
        }
        foreach (var a in x.Allowed.Where(a => Norm(a).Length > 0 && !x.Devices.Any(d => Matches(d.Name, a))))
            board.Add(new CheckItem(OutputGroup, a, L.T("Non collegato", "Not connected"), CheckStatus.Watch, L.T("Acceso", "On"),
                L.T("È tra i dispositivi ammessi ma Windows non lo vede: è scollegato o spento?", "It is an allowed device but Windows does not see it: is it unplugged or off?"),
                L.T("Ammessi", "Allowed")));
        return board;
    }

    private static string KeptAdvice => L.T(
        "OptLife non lo rispegne: l'hai riacceso tu. Se ti serve, aggiungilo a \"Dispositivi ammessi\" in IMPOSTAZIONI → Dispositivi audio e questo avviso sparisce; se no, SPEGNI.",
        "OptLife does not turn it off again: you switched it back on. If you need it, add it to \"Allowed devices\" in SETTINGS → Audio devices and this notice goes; if not, TURN OFF.");

    private static string ExtraAdvice => L.T(
        "Ogni dispositivo acceso compare negli elenchi di Windows e dei giochi, e un gioco o un aggiornamento può sceglierlo da solo. Se ti serve (per esempio un DAC nuovo), aggiungilo a \"Dispositivi ammessi\" e, se prende il posto di quello di sempre, cambia anche il predefinito in IMPOSTAZIONI → Dispositivi audio. Se no, SPEGNI lo toglie dagli elenchi (si riaccende da qui o da Impostazioni audio).",
        "Every device that is on shows in Windows' and games' lists, and a game or an update can pick it by itself. If you need it (a new DAC, say), add it to \"Allowed devices\" and, if it replaces the usual one, change the default too in SETTINGS → Audio devices. If not, TURN OFF removes it from the lists (it can be turned back on here or in Sound settings).");

    /// <summary>A new entry with the name of one that is off: Windows made the same device again.</summary>
    public static bool Recreated(AudioDevice d, IEnumerable<AudioDevice> all) =>
        all.Any(o => !o.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase) && o.Flow == d.Flow && !o.On && o.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The devices to turn off by themselves when the person chose so: on, not allowed, and new (a device Windows made again has a new
    /// entry, so it is new too). One seen before that is on again was switched on by the person (in Windows or with the button):
    /// their choice wins, it stays on (on 24 Nov a monitor's sound was switched back on by hand and turned off again, twice).
    /// </summary>
    public static IReadOnlyList<AudioDevice> ToTurnOff(Inputs x) =>
        x.Allowed.Count == 0 ? [] : [.. x.Devices.Where(d => d.On && !Allowed(d, x.Allowed) && !x.Known.Contains(d.Id))];

    public static IEnumerable<NewFinding> Findings(Inputs x)
    {
        foreach (var d in x.Allowed.Count == 0 ? [] : x.Devices)
        {
            var allowed = Allowed(d, x.Allowed);
            if (!allowed && d.On)
            {
                var isNew = !x.Known.Contains(d.Id);
                var back = x.WasOff.Contains(d.Id);
                // Windows makes a new entry for the same device (a monitor's sound after a driver update or a restart): same name,
                // new id, switched on, while the old one stays off.
                var remade = isNew && Recreated(d, x.Devices);
                // Back on with the same entry: switched on by the person, left on as chosen; only the suggestion to allow it.
                yield return new NewFinding($"extra|{d.Id.ToLowerInvariant()}", back ? Severity.Info : Severity.Low,
                    isNew ? L.T($"Nuovo dispositivo audio: {d.Name}", $"New audio device: {d.Name}")
                    : back ? L.T($"{d.Name} è di nuovo acceso: lasciato acceso come hai scelto", $"{d.Name} is on again: left on as you chose")
                    : L.T($"{d.Name} è acceso ma non è tra i dispositivi ammessi", $"{d.Name} is on but is not an allowed device"),
                    remade
                        ? L.T("Windows ha ricreato questo dispositivo (succede dopo un riavvio o un aggiornamento del driver, per esempio con l'audio dei monitor): la voce nuova parte accesa, quella che avevi spento resta spenta.",
                              "Windows made this device again (it happens after a restart or a driver update, for example with monitors' sound): the new entry starts on, the one you had turned off stays off.")
                        : (d.Flow == Flow.Output ? L.T("Uscita audio", "Audio output") : L.T("Ingresso audio", "Audio input")) + " · " + OnOff(d) + ".",
                    back ? KeptAdvice : ExtraAdvice, new FindingFix(Hide, L.T("Spegnilo", "Turn it off"), d.Id), FindingArea.Setup);
            }
            else if (allowed && !d.On)
                yield return new NewFinding($"off|{d.Id.ToLowerInvariant()}", Severity.Low, L.T($"{d.Name} è spento", $"{d.Name} is off"),
                    L.T("È tra i dispositivi ammessi ma è spento in Windows: non compare negli elenchi.", "It is an allowed device but it is off in Windows: it does not show in the lists."),
                    L.T("Riaccendilo col pulsante, o toglilo dagli ammessi.", "Turn it back on with the button, or remove it from the allowed list."),
                    new FindingFix(Show, L.T("Riaccendilo", "Turn it back on"), d.Id), FindingArea.Setup);
        }
        foreach (var (flow, wanted, current) in new[] { (Flow.Output, x.DefaultOutput, x.Defaults.Output), (Flow.Input, x.DefaultInput, x.Defaults.Input) })
        {
            if (Wanted(x.Devices, flow, wanted) is not { } target) continue;
            var wrong = current.Select((id, role) => (id, role)).Where(p => !target.Id.Equals(p.id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (wrong.Count == 0) continue;
            var what = flow == Flow.Output ? L.T("L'uscita audio predefinita", "The default audio output") : L.T("Il microfono predefinito", "The default microphone");
            yield return new NewFinding($"default|{flow.ToString().ToLowerInvariant()}", Severity.Medium,
                L.T($"{what} non è {target.Name}", $"{what} is not {target.Name}"),
                L.T("Diverso per: ", "Different for: ") + string.Join(", ", wrong.Select(p => RoleName(p.role) + " → " +
                    (x.Devices.FirstOrDefault(d => d.Id.Equals(p.id, StringComparison.OrdinalIgnoreCase))?.Name ?? L.T("nessuno", "none")))) + ".",
                L.T("Capita quando si collega un dispositivo nuovo o dopo un aggiornamento: il pulsante lo rimette per tutti gli usi.",
                    "It happens when a new device is connected or after an update: the button sets it back for every use."),
                new FindingFix(SetDefault, L.T($"Rimetti {target.Name}", $"Set back {target.Name}"), target.Id), FindingArea.Setup);
        }
    }

    /// <summary>What changed since the last look: devices new, back on, off, and defaults moved.</summary>
    public static IEnumerable<NewEvent> Changes(IReadOnlyDictionary<string, bool> before, IReadOnlyList<AudioDevice> now, string? defaultsBefore, string defaultsNow,
        AudioDefaults defaults, DateTimeOffset at)
    {
        foreach (var d in now)
        {
            if (!before.TryGetValue(d.Id, out var wasOn))
                yield return new NewEvent(at, "audio_device_added", d.On ? Severity.Low : Severity.Info, L.T($"Nuovo dispositivo audio: {d.Name}", $"New audio device: {d.Name}") +
                    (d.On ? "" : L.T(" (spento)", " (off)")), "audio", Data: new Dictionary<string, string> { ["id"] = d.Id, ["name"] = d.Name });
            else if (d.On != wasOn)
                yield return new NewEvent(at, d.On ? "audio_device_enabled" : "audio_device_disabled", Severity.Info,
                    d.On ? L.T($"Riacceso: {d.Name}", $"Back on: {d.Name}") : L.T($"Spento: {d.Name}", $"Turned off: {d.Name}"), "audio",
                    Data: new Dictionary<string, string> { ["id"] = d.Id, ["name"] = d.Name });
        }
        if (defaultsBefore is not null && defaultsBefore != defaultsNow)
        {
            string NameOf(string? id) => now.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? L.T("nessuno", "none");
            yield return new NewEvent(at, "audio_default_changed", Severity.Info,
                L.T($"Predefiniti audio cambiati: uscita {NameOf(defaults.Output.ElementAtOrDefault(0))}, microfono {NameOf(defaults.Input.ElementAtOrDefault(0))}",
                    $"Audio defaults changed: output {NameOf(defaults.Output.ElementAtOrDefault(0))}, microphone {NameOf(defaults.Input.ElementAtOrDefault(0))}"), "audio");
        }
    }

    public static string Signature(AudioDefaults d) => string.Join('|', d.Output.Concat(d.Input).Select(x => x ?? ""));
}
