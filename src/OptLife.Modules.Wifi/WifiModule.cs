using System.Globalization;
using OptLife.Modules.Wifi.Windows;

namespace OptLife.Modules.Wifi;

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface IWifiEnvironment
{
    /// <summary>What Windows says about the Wi-Fi interfaces (netsh wlan show interfaces); null when it could not be asked.</summary>
    string? Interfaces();

    /// <summary>The Wi-Fi adapter (model, and its link speed when connected), which Windows gives without Location; null when there is none.</summary>
    WifiAdapter? Adapter();

    ActionResult Open(string target);

    /// <summary>The network cards with a cable (virtual ones left out).</summary>
    IReadOnlyList<WiredLink> Wired() => [];

    /// <summary>The Wi-Fi carries the traffic right now (connected, with a gateway).</summary>
    bool WifiInUse() => false;
}

/// <summary>
/// The network page ("Rete"): the connection in use, the cable (speed against what the card can do, damaged packets,
/// disconnections) and the Wi-Fi link (band, link speed and signal, compared with what this PC normally gets), plus the ping to the
/// router measured by the Monitoring module when it is there. Warnings come after two readings in a row. The id stays "wifi"
/// so the settings of before keep working. The DNS servers are checked by "PC settings".
/// </summary>
public sealed class WifiModule : IModule
{
    public const string BoardKind = "wifi_board";
    private const string AdapterPage = "ms-settings:network-wifi", LocationPage = "ms-settings:privacy-location";
    private readonly IWifiEnvironment _env;

    public WifiModule() : this(new WindowsWifiEnvironment()) { }

    internal WifiModule(IWifiEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("wifi", L.T("Rete", "Network"),
        L.T("La connessione in uso: col cavo velocità, pacchetti rovinati e distacchi; col Wi-Fi banda, velocità e segnale, con un avviso quando peggiorano.",
            "The connection in use: on a cable speed, damaged packets and drops; on Wi-Fi band, speed and signal, with a warning when they get worse."),
        FindingArea.Network);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Int("minPercent", L.T("Avvisa sotto il (% del solito)", "Warn below (% of usual)"),
            L.T("La velocità di collegamento è confrontata con quella solita degli ultimi 7 giorni: sotto questa percentuale, per due letture di fila, arriva l'avviso.",
                "The link speed is compared with the usual one of the last 7 days: below this percentage, for two readings in a row, a warning comes."), 50, 10, 95),
        SettingDefinition.Int("minMbps", L.T("Avvisa sotto (Mbps, 0 = solo rispetto al solito)", "Warn below (Mbps, 0 = only against the usual)"),
            L.T("Una soglia fissa in più, se vuoi.", "An extra fixed threshold, if you want one."), 0, 0, 10_000),
        SettingDefinition.Int("minSignal", L.T("Segnale minimo (%)", "Minimum signal (%)"),
            L.T("Sotto questo, per due letture di fila, arriva l'avviso.", "Below this, for two readings in a row, a warning comes."), 40, 5, 95),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo della rete", "Checking the network"), TimeSpan.FromMinutes(5), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction("open", L.T("Apri le impostazioni", "Open settings"), L.T("Apre le impostazioni del Wi-Fi o della posizione di Windows.", "Opens Windows' Wi-Fi or location settings."),
            Permission.Ask, (_, target, _) => Task.FromResult(target is AdapterPage or LocationPage ? _env.Open(target) : new ActionResult(false, L.T("Indirizzo non valido.", "Invalid address.")))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("wifi", L.T("Rete", "Network"), BoardKind, [], Days: 365, Menu: ViewMenu.Status, Order: 10,
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("Questa pagina tiene d'occhio il collegamento tra il PC e il router, col cavo o col Wi-Fi, e ti avvisa se peggiora (un cavo che va a 100 Mb invece di 1 Gb, pacchetti rovinati, distacchi; il Wi-Fi che passa alla banda più lenta o la velocità che crolla). " +
                             "È la velocità del collegamento, non quella della tua linea internet: se è alta ma internet è lento, il problema è la linea. Verde: va bene. Ambra: è peggiorato rispetto al solito.",
                             "This page keeps an eye on the link between the PC and the router, by cable or Wi-Fi, and warns you when it gets worse (a cable at 100 Mb instead of 1 Gb, damaged packets, drops; Wi-Fi moving to the slower band or the speed collapsing). " +
                             "It is the speed of the link, not of your internet line: if it is high but the internet is slow, the line is the problem. Green: fine. Amber: worse than usual."),
            Layout: ViewLayout.Checklist),
    ];

    private Task Check(IModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings;
        var output = _env.Interfaces();
        var adapter = _env.Adapter();
        var board = new List<CheckItem>();
        var findings = new List<NewFinding>();
        var link = output is null ? null : WifiRules.Parse(output);
        // Without Location Windows hides network, band and signal, but still gives the adapter's link speed: better than nothing.
        var hidden = link is null && output is not null && WifiRules.NeedsLocation(output);
        if (hidden && adapter?.SpeedMbps is { } fallback) link = new WifiLink("", null, null, null, fallback, null, null);

        var speedGroup = L.T("Velocità", "Speed");
        var netGroup = L.T("Rete e segnale", "Network and signal");
        var cardGroup = L.T("Scheda Wi-Fi", "Wi-Fi adapter");
        var locationButton = new CheckButton("open", LocationPage, L.T("APRI POSIZIONE", "OPEN LOCATION"));

        if (link is null && hidden)
        {
            board.Add(new CheckItem(netGroup, L.T("Collegamento", "Connection"),
                L.T("Windows non dice niente del Wi-Fi senza la Posizione accesa\nÈ una scelta di privacy: i nomi delle reti intorno a te dicono dove sei. Se vuoi che OptLife controlli il Wi-Fi, accendi la Posizione.",
                    "Windows tells nothing about Wi-Fi without Location on\nIt is a privacy choice: the names of the networks around you tell where you are. If you want OptLife to check the Wi-Fi, turn Location on."),
                CheckStatus.Info, StatusText: "", Buttons: [locationButton]));
            ctx.SetState("low", "0");
        }
        else if (link is null && adapter is null)
        {
            ctx.SetState("low", "0"); // No Wi-Fi card at all: nothing about Wi-Fi on the page.
        }
        else if (link is null)
        {
            board.Add(new CheckItem(netGroup, L.T("Collegamento", "Connection"), L.T("Nessuna rete Wi-Fi collegata", "No Wi-Fi network connected") +
                L.T("\nSe il PC usa il cavo non c'è niente da guardare qui: il cavo è la scelta migliore.", "\nIf the PC uses a cable there is nothing to look at here: a cable is the best choice."),
                CheckStatus.Info, StatusText: ""));
            ctx.SetState("low", "0");
        }
        else
        {
            var now = ctx.Now;
            var samples = new List<KeyValuePair<string, double>>();
            if (link.RxMbps is { } rx) samples.Add(new("wifi.rx_mbps", rx));
            if (link.Signal is { } sig) samples.Add(new("wifi.signal", sig));
            if (link.BandGHz is { } b) samples.Add(new("wifi.band", b));
            var pastRx = ctx.History.Samples("wifi.rx_mbps", now.AddDays(-7), now).Select(p => p.Value).ToList();
            var pastBand = ctx.History.Samples("wifi.band", now.AddDays(-7), now).Select(p => p.Value).ToList();
            ctx.RecordSamples(samples);

            var usualRx = pastRx.Count >= WifiRules.Needed ? WifiRules.Median(pastRx) : (double?)null;
            var usualBand = pastBand.Count >= WifiRules.Needed ? WifiRules.UsualBand(pastBand) : null;

            var bandLow = link.BandGHz is { } band && usualBand is { } ub && band < ub;
            var rxLow = link.RxMbps is { } r && (usualRx is { } u && r < u * s.GetInt("minPercent") / 100.0 || s.GetInt("minMbps") > 0 && r < s.GetInt("minMbps"));
            var sigLow = link.Signal is { } si && si < s.GetInt("minSignal");
            // Two readings in a row: a moment of interference is not a problem.
            var streak = bandLow || rxLow || sigLow ? (int.TryParse(ctx.GetState("low"), out var n) ? n : 0) + 1 : 0;
            ctx.SetState("low", streak.ToString(CultureInfo.InvariantCulture));
            var report = streak >= 2;

            if (ctx.GetState("last_band") is { } lastBand && link.BandGHz is { } nb && lastBand != nb.ToString(CultureInfo.InvariantCulture))
                ctx.RecordEvents([new NewEvent(now, "wifi_band_changed", Severity.Info,
                    L.T($"Wi-Fi passato a {WifiRules.Band(link.BandGHz)} ({link.Ssid})", $"Wi-Fi moved to {WifiRules.Band(link.BandGHz)} ({link.Ssid})"), "wifi")]);
            if (link.BandGHz is { } cb) ctx.SetState("last_band", cb.ToString(CultureInfo.InvariantCulture));

            // ── Speed: now, the usual, and what it means ──
            if (link.RxMbps is { } current)
                board.Add(new CheckItem(speedGroup, L.T("Velocità adesso", "Speed now"),
                    $"{current} Mbps" + (link.TxMbps is { } tx ? L.T($" (in invio {tx} Mbps)", $" (sending {tx} Mbps)") : "") + "\n" + WifiRules.Meaning(current),
                    rxLow ? CheckStatus.Watch : CheckStatus.Ok, null, rxLow ? SpeedAdvice : null, StatusText: rxLow ? null : ""));
            board.Add(new CheckItem(speedGroup, L.T("Velocità solita", "Usual speed"),
                usualRx is { } ur
                    ? L.T($"Intorno a {ur:0} Mbps\nÈ la mediana degli ultimi 7 giorni: se quella di adesso scende sotto il {s.GetInt("minPercent")}% per due letture di fila, OptLife ti avvisa.",
                          $"Around {ur:0} Mbps\nIt is the median of the last 7 days: if the speed now drops below {s.GetInt("minPercent")}% of it for two readings in a row, OptLife warns you.")
                    : L.T($"Ancora da imparare\nTra circa {WifiRules.MinutesLeft(pastRx.Count)} minuti OptLife saprà qual è la velocità normale di questo PC, e potrà avvisarti se scende.",
                          $"Still learning\nIn about {WifiRules.MinutesLeft(pastRx.Count)} minutes OptLife will know this PC's normal speed, and can warn you when it drops."),
                CheckStatus.Info, StatusText: ""));

            // ── Network, band, signal: or why Windows hides them ──
            if (link.Ssid.Length == 0)
                board.Add(new CheckItem(netGroup, L.T("Nome della rete, banda e segnale", "Network name, band and signal"),
                    L.T("Nascosti da Windows\n" +
                        "Windows considera questi dati come la tua posizione (dai nomi delle reti intorno si può capire dove sei), quindi li dà ai programmi solo se la Posizione è accesa. " +
                        "È una scelta di privacy: non è necessario accenderla, OptLife controlla comunque la velocità. Accendendola vedresti anche se sei su 2,4 o 5 GHz e quanto è forte il segnale.",
                        "Hidden by Windows\n" +
                        "Windows treats these as your location (the names of the networks around you can tell where you are), so it gives them to programs only with Location on. " +
                        "It is a privacy choice: you do not need to turn it on, OptLife checks the speed anyway. With it on you would also see whether you are on 2.4 or 5 GHz and how strong the signal is."),
                    CheckStatus.Info, StatusText: "", Buttons: [locationButton]));
            else
            {
                board.Add(new CheckItem(netGroup, L.T("Rete", "Network"), link.Ssid, CheckStatus.Info, StatusText: ""));
                if (link.BandGHz is { } bb)
                    board.Add(new CheckItem(netGroup, L.T("Banda", "Band"), WifiRules.Band(bb) + (link.Channel is { } ch ? L.T($", canale {ch}", $", channel {ch}") : "") + "\n" + WifiRules.BandMeaning(bb),
                        bandLow ? CheckStatus.Watch : CheckStatus.Ok, usualBand is { } ubb ? WifiRules.Band(ubb) + L.T(", la solita", ", the usual one") : null,
                        bandLow ? BandAdvice : null, StatusText: bandLow ? null : "", Buttons: bandLow ? [new CheckButton("open", AdapterPage, L.T("APRI", "OPEN"))] : null));
                if (link.Signal is { } sg)
                    board.Add(new CheckItem(netGroup, L.T("Segnale", "Signal"), $"{sg}%\n" + WifiRules.SignalMeaning(sg),
                        sigLow ? CheckStatus.Watch : CheckStatus.Ok, L.T($"almeno {s.GetInt("minSignal")}%", $"at least {s.GetInt("minSignal")}%"), sigLow ? SpeedAdvice : null, StatusText: sigLow ? null : ""));
            }

            if (report && bandLow)
                findings.Add(new NewFinding("band", Severity.Medium,
                    L.T($"Il Wi-Fi è sceso a {WifiRules.Band(link.BandGHz)}", $"Wi-Fi dropped to {WifiRules.Band(link.BandGHz)}"),
                    L.T($"Di solito questo PC è collegato a {WifiRules.Band(usualBand)}: la banda più bassa è più lenta e più disturbata, e nei giochi alza il ping.",
                        $"This PC is usually on {WifiRules.Band(usualBand)}: the lower band is slower and more crowded, and raises the ping in games."),
                    BandAdvice, new FindingFix("open", L.T("Apri le impostazioni Wi-Fi", "Open Wi-Fi settings"), AdapterPage)));
            if (report && rxLow)
                findings.Add(new NewFinding("speed", Severity.Low,
                    L.T($"Velocità del Wi-Fi scesa a {link.RxMbps} Mbps", $"Wi-Fi speed dropped to {link.RxMbps} Mbps"),
                    usualRx is { } u2 ? L.T($"Di solito è intorno a {u2:0} Mbps.", $"It is usually around {u2:0} Mbps.") : L.T("Sotto la soglia scelta.", "Below the chosen threshold."),
                    SpeedAdvice, Area: FindingArea.Network));
            if (report && sigLow)
                findings.Add(new NewFinding("signal", Severity.Low, L.T($"Segnale Wi-Fi debole: {link.Signal}%", $"Weak Wi-Fi signal: {link.Signal}%"),
                    L.T("Un segnale debole abbassa la velocità e fa perdere pacchetti.", "A weak signal lowers the speed and loses packets."), SpeedAdvice, Area: FindingArea.Network));
        }

        // ── The adapter: what it is, and the general advice ──
        if (adapter is not null)
            board.Add(new CheckItem(cardGroup, adapter.Model, (WifiRules.Standard(adapter.Model, link?.Radio) is { } std ? std + "\n" : "") + GeneralAdvice,
                CheckStatus.Info, StatusText: ""));

        var (wiredBoard, wiredFindings, inUse) = CheckWired(ctx, link);
        board.InsertRange(0, wiredBoard);
        findings.AddRange(wiredFindings);

        ctx.SyncFindings("wifi", findings);
        var data = CheckItem.ToData(board);
        // Speeds move all the time: the page is rewritten when what it says changes in substance, or every half hour for the numbers.
        var moving = new[] { speedGroup, L.T("Connessione in uso", "Connection in use"), L.T("Cavo", "Cable") };
        var signature = string.Join('\n', board.Select(i => $"{i.Label}|{i.Status}|{(moving.Contains(i.Group) || i.Label == L.T("Segnale", "Signal") ? "" : i.Value)}"))
                        + "|" + (ctx.Now.ToUnixTimeSeconds() / 1800).ToString(CultureInfo.InvariantCulture);
        if (signature != ctx.GetState("board") || ctx.OnDemand)
        {
            ctx.SetState("board", signature);
            ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info,
                inUse is { } wire ? L.T($"Rete: cavo, {WiredRules.Speed(wire.SpeedMbps)}", $"Network: cable, {WiredRules.Speed(wire.SpeedMbps)}")
                    : link is null ? L.T("Rete: nessun collegamento", "Network: no connection")
                    : link.Ssid.Length == 0 ? $"Wi-Fi: {link.RxMbps} Mbps"
                    : $"Wi-Fi: {link.Ssid}, {WifiRules.Band(link.BandGHz)}, {link.RxMbps} Mbps",
                BoardKind, Data: data)]);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The cable part of the page: which connection carries the traffic, each wired card (speed, damaged packets, drops) and the
    /// ping to the router of the last minutes as the Monitoring module measured it (missing without that module).
    /// </summary>
    private (List<CheckItem> Board, List<NewFinding> Findings, WiredLink? InUse) CheckWired(IModuleContext ctx, WifiLink? link)
    {
        var board = new List<CheckItem>();
        var findings = new List<NewFinding>();
        var now = ctx.Now;
        var wired = _env.Wired();
        var inUse = wired.FirstOrDefault(w => w.InUse);
        var useGroup = L.T("Connessione in uso", "Connection in use");
        var cableGroup = L.T("Cavo", "Cable");

        var wifiInUse = inUse is null && (link is not null || _env.WifiInUse());
        board.Add(new CheckItem(useGroup, L.T("Il PC esce in rete con", "The PC goes online through"),
            inUse is not null ? L.T($"il cavo ({inUse.Model})", $"the cable ({inUse.Model})")
            : wifiInUse ? L.T("il Wi-Fi", "Wi-Fi") + (link is { Ssid.Length: > 0 } ? $" ({link.Ssid})" : "")
            : L.T("nessuna connessione adesso", "no connection right now"),
            inUse is null && !wifiInUse ? CheckStatus.Watch : CheckStatus.Info, StatusText: ""));

        // The ping to the router: written into the history by the Monitoring module every minute (name "router" by default).
        var ms = ctx.History.Samples("net.router.ms", now.AddMinutes(-15), now).Select(p => p.Value).ToList();
        var loss = ctx.History.Samples("net.router.loss", now.AddMinutes(-15), now).Select(p => p.Value).ToList();
        if (ms.Count > 0)
        {
            var avg = ms.Average();
            var lost = loss.Count > 0 ? loss.Average() : 0;
            board.Add(new CheckItem(useGroup, L.T("Ping verso il router (ultimi 15 minuti)", "Ping to the router (last 15 minutes)"),
                $"{avg.ToString("0.#", L.Culture)} ms" + (lost > 0 ? L.T($", {lost.ToString("0.#", L.Culture)}% persi", $", {lost.ToString("0.#", L.Culture)}% lost") : "") + "\n" + WiredRules.PingMeaning(avg, lost),
                lost >= 2 || avg > 20 ? CheckStatus.Watch : CheckStatus.Ok, StatusText: lost >= 2 || avg > 20 ? null : ""));
        }

        var dropsGroupKey = "network_link_down";
        foreach (var w in wired)
        {
            var key = w.Id.ToLowerInvariant();
            // Up then down between two checks: the cable came out or the link dropped.
            if (ctx.GetState($"w.up.{key}") == "1" && !w.Up)
                ctx.RecordEvents([new NewEvent(now, dropsGroupKey, Severity.Low, L.T($"Cavo staccato: {w.Name}", $"Cable disconnected: {w.Name}"), $"wired|{key}")]);
            ctx.SetState($"w.up.{key}", w.Up ? "1" : "0");
            // A card without a cable: said only when nothing at all is connected (a second port on the board is not news).
            if (!w.Up)
            {
                if (inUse is null && !wifiInUse)
                    board.Add(new CheckItem(cableGroup, w.Model, L.T("Non collegato\nNessun cavo, o il router dall'altra parte è spento.", "Not connected\nNo cable, or the router at the other end is off."), CheckStatus.Info, StatusText: ""));
                continue;
            }
            if (w.SpeedMbps > 0) ctx.RecordSamples([new KeyValuePair<string, double>("eth.speed_mbps", w.SpeedMbps)]);

            var slow = WiredRules.Slow(w);
            var slowStreak = slow ? (int.TryParse(ctx.GetState($"w.slow.{key}"), out var s1) ? s1 : 0) + 1 : 0;
            ctx.SetState($"w.slow.{key}", slowStreak.ToString(CultureInfo.InvariantCulture));
            board.Add(new CheckItem(cableGroup, w.Model,
                L.T($"Collegato a {WiredRules.Speed(w.SpeedMbps)}", $"Linked at {WiredRules.Speed(w.SpeedMbps)}") + "\n" +
                (slow ? L.T("La scheda arriva ad almeno 1 Gbps: va più piano del dovuto.", "The card can do at least 1 Gbps: it runs slower than it should.")
                    : w.SpeedMbps >= 1000 ? L.T("Va bene: è la velocità piena del cavo.", "Fine: the full speed of the cable.")
                    : L.T("Se la scheda è da 1 Gbps (quasi tutte quelle recenti), il cavo o la porta del router la frenano.", "If the card is a 1 Gbps one (nearly all recent ones are), the cable or the router port holds it back.")),
                slow ? CheckStatus.Watch : w.SpeedMbps >= 1000 ? CheckStatus.Ok : CheckStatus.Info,
                slow ? "1 Gbps" : null, slow ? WiredRules.SlowAdvice : null, StatusText: slow || w.SpeedMbps < 1000 ? null : ""));
            if (slowStreak >= 2)
                findings.Add(new NewFinding($"wired.slow|{key}", Severity.Medium,
                    L.T($"Il cavo di rete va a {WiredRules.Speed(w.SpeedMbps)} invece di 1 Gbps", $"The network cable runs at {WiredRules.Speed(w.SpeedMbps)} instead of 1 Gbps"),
                    L.T($"{w.Model} può andare ad almeno 1 Gbps, ma è collegata a {WiredRules.Speed(w.SpeedMbps)}.", $"{w.Model} can do at least 1 Gbps, but is linked at {WiredRules.Speed(w.SpeedMbps)}."),
                    WiredRules.SlowAdvice, Area: FindingArea.Network));

            // Damaged packets since the last check (the card counts from when Windows started; a restart starts it again).
            var lastErrors = long.TryParse(ctx.GetState($"w.err.{key}"), out var le) ? le : (long?)null;
            ctx.SetState($"w.err.{key}", w.Errors.ToString(CultureInfo.InvariantCulture));
            var newErrors = lastErrors is { } before && w.Errors >= before ? w.Errors - before : 0;
            var bad = newErrors >= WiredRules.ErrorsWorthALook;
            var errStreak = bad ? (int.TryParse(ctx.GetState($"w.errs.{key}"), out var e1) ? e1 : 0) + 1 : 0;
            ctx.SetState($"w.errs.{key}", errStreak.ToString(CultureInfo.InvariantCulture));
            board.Add(new CheckItem(cableGroup, L.T($"Pacchetti rovinati ({w.Name})", $"Damaged packets ({w.Name})"),
                lastErrors is null ? L.T("Ancora da misurare: tra 5 minuti si vede quanti ne arrivano.", "Still to measure: in 5 minutes it shows how many arrive.")
                    : L.T($"{newErrors} negli ultimi 5 minuti", $"{newErrors} in the last 5 minutes") + "\n" + (bad ? L.T("Troppi: qualcosa disturba il cavo.", "Too many: something disturbs the cable.") : L.T("Va bene: qualcuno ogni tanto è normale.", "Fine: a few now and then are normal.")),
                bad ? CheckStatus.Watch : lastErrors is null ? CheckStatus.Info : CheckStatus.Ok, null, bad ? WiredRules.ErrorAdvice : null, StatusText: bad || lastErrors is null ? null : ""));
            if (errStreak >= 2)
                findings.Add(new NewFinding($"wired.errors|{key}", Severity.Low,
                    L.T($"Pacchetti rovinati sul cavo di rete: {newErrors} in 5 minuti", $"Damaged packets on the network cable: {newErrors} in 5 minutes"),
                    L.T($"La scheda {w.Model} conta pacchetti rovinati o scartati, due controlli di fila.", $"The {w.Model} card counts damaged or dropped packets, two checks in a row."),
                    WiredRules.ErrorAdvice, Area: FindingArea.Network));
        }

        // Drops of the last day: one is someone pulling the cable, three is a cable or a card that loses the link by itself.
        var drops = ctx.History.Events(now.AddDays(-1), now, ctx.ModuleId, dropsGroupKey);
        if (drops.Count > 0)
            board.Add(new CheckItem(cableGroup, L.T("Distacchi nelle ultime 24 ore", "Drops in the last 24 hours"),
                $"{drops.Count}\n" + (drops.Count >= 3 ? WiredRules.DropAdvice : L.T("Visti ai controlli ogni 5 minuti: uno ogni tanto è un cavo tolto apposta.", "Seen at the checks every 5 minutes: one now and then is a cable pulled on purpose.")),
                drops.Count >= 3 ? CheckStatus.Watch : CheckStatus.Info, StatusText: drops.Count >= 3 ? null : ""));
        if (drops.Count >= 3)
            findings.Add(new NewFinding("wired.drops", Severity.Low,
                L.T($"Il cavo di rete si è staccato {drops.Count} volte nelle ultime 24 ore", $"The network cable disconnected {drops.Count} times in the last 24 hours"),
                string.Join(", ", drops.Take(5).Select(d => d.Time.ToLocalTime().ToString("d MMM HH:mm", L.Culture))) + ".", WiredRules.DropAdvice, Area: FindingArea.Network));
        return (board, findings, inUse);
    }

    private static string GeneralAdvice => L.T(
        "Per giocare online un cavo Ethernet è sempre più stabile del Wi-Fi (niente cali improvvisi né ping che salta). Col Wi-Fi, la banda 5 GHz è più veloce e meno disturbata della 2,4 GHz, che però arriva più lontano.",
        "For online gaming an Ethernet cable is always steadier than Wi-Fi (no sudden drops or ping spikes). On Wi-Fi, the 5 GHz band is faster and less crowded than 2.4 GHz, which reaches farther.");

    private static string BandAdvice => L.T(
        "Succede quando il PC si allontana dal router, quando il router sposta i dispositivi da solo, o dopo un riavvio. Prova a scollegarti e ricollegarti; se succede spesso, nelle proprietà della scheda Wi-Fi (Gestione dispositivi → Avanzate) imposta la banda preferita a 5 GHz, o dai alle due bande nomi diversi nel router.",
        "It happens when the PC is farther from the router, when the router moves devices by itself, or after a restart. Try disconnecting and reconnecting; if it happens often, in the Wi-Fi adapter properties (Device Manager → Advanced) set the preferred band to 5 GHz, or give the two bands different names in the router.");

    private static string SpeedAdvice => L.T(
        "Di solito è distanza o ostacoli (muri, mobili metallici), interferenze dei vicini o la banda più bassa. Avvicina il router o un ripetitore, o usa un cavo per giocare.",
        "Usually distance or obstacles (walls, metal furniture), neighbours' interference or the lower band. Move the router or a repeater closer, or use a cable for gaming.");
}
