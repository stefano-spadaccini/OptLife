using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Games;

/// <summary>Reads the finished sessions of a game and says what they add up to. Pure: no I/O, no clock.</summary>
public static class GamesRules
{
    public const string SessionKind = "game_session";
    private static CultureInfo Lang => L.Culture;

    // What each measurement means to a person, and what to do when it is the one that precedes the lags.
    private static Dictionary<string, (string Label, string Advice)> Meaning => new()
    {
        ["ping_gw_max"] = (L.T("il ping verso il router che sale", "the ping to the router going up"), L.T("Il ritardo nasce tra il PC e il router: in Wi-Fi è quasi sempre il segnale. Il cavo, o un canale meno affollato, lo toglie.", "The delay starts between the PC and the router: on Wi-Fi it is almost always the signal. A cable, or a less crowded channel, removes it.")),
        ["ping_gw_loss"] = (L.T("i pacchetti persi verso il router", "packets lost to the router"), L.T("I dati si perdono tra il PC e il router: in Wi-Fi è quasi sempre il segnale. Il cavo, o un canale meno affollato, lo toglie.", "Data are lost between the PC and the router: on Wi-Fi it is almost always the signal. A cable, or a less crowded channel, removes it.")),
        ["ping_ext_max"] = (L.T("il ping verso internet che sale", "the ping to the internet going up"), L.T("Il collegamento in casa regge: il ritardo è sulla linea, dal provider o sui server del gioco.", "The home link holds: the delay is on the line, at the provider or on the game servers.")),
        ["ping_ext_loss"] = (L.T("i pacchetti persi verso internet", "packets lost to the internet"), L.T("Il collegamento in casa regge: i dati si perdono sulla linea, dal provider o sui server del gioco.", "The home link holds: data are lost on the line, at the provider or on the game servers.")),
        ["ping_srv_max"] = (L.T("il ping verso il server del gioco che sale", "the ping to the game server going up"), L.T("Casa e linea reggono: il ritardo nasce sul percorso dal provider al datacenter del gioco. Non dipende dal PC: prova un'altra regione di server, o segnalalo al provider.", "Home and line hold: the delay starts on the route from the provider to the game's datacenter. It does not depend on the PC: try another server region, or report it to the provider.")),
        ["ping_srv_loss"] = (L.T("i pacchetti persi verso il server del gioco", "packets lost to the game server"), L.T("Casa e linea reggono: i dati si perdono sul percorso dal provider al datacenter del gioco. Non dipende dal PC: prova un'altra regione di server, o segnalalo al provider.", "Home and line hold: data are lost on the route from the provider to the game's datacenter. It does not depend on the PC: try another server region, or report it to the provider.")),
        ["cpu_core_max"] = (L.T("un core del processore saturo", "one processor core maxed out"), L.T("Il gioco è frenato da un singolo core: chiudi i programmi in background e abbassa le impostazioni che pesano sul processore (distanza visiva, fisica, folla).", "The game is held back by a single core: close background programs and lower the settings that load the processor (view distance, physics, crowds).")),
        ["cpu_tot"] = (L.T("il processore tutto occupato", "the processor fully busy"), L.T("Il processore non ha margine: chiudi i programmi in background mentre giochi.", "The processor has no headroom: close background programs while you play.")),
        ["dpc"] = (L.T("i driver che tengono occupato il sistema", "drivers keeping the system busy"), L.T("È tipico di driver audio, di rete o USB: aggiornarli, o scollegare la periferica sospetta, di solito risolve.", "Typical of audio, network or USB drivers: updating them, or unplugging the suspect device, usually fixes it.")),
        ["pages_in"] = (L.T("Windows che rilegge memoria dal disco", "Windows reading memory back from disk"), L.T("È il segno della RAM insufficiente: chiudi i programmi pesanti prima di giocare; la soluzione definitiva è più RAM.", "It is the sign of too little RAM: close heavy programs before playing; the real fix is more RAM.")),
        ["ram_low"] = (L.T("la RAM libera quasi finita", "free RAM almost gone"), L.T("È il segno della RAM insufficiente: chiudi i programmi pesanti prima di giocare; la soluzione definitiva è più RAM.", "It is the sign of too little RAM: close heavy programs before playing; the real fix is more RAM.")),
        ["game_faults"] = (L.T("il gioco che ricarica la sua memoria", "the game reloading its memory"), L.T("Il gioco viene spostato dentro e fuori dalla RAM: chiudi i programmi pesanti prima di giocare; la soluzione definitiva è più RAM.", "The game is moved in and out of RAM: close heavy programs before playing; the real fix is more RAM.")),
        ["disk_ms"] = (L.T("il disco lento a rispondere", "the disk slow to respond"), L.T("Il disco su cui sta il gioco (o il file di paging) risponde a rilento: controlla che non ci siano download o copie in corso.", "The disk the game (or the paging file) is on responds slowly: check there are no downloads or copies running.")),
        ["gpu_idle"] = (L.T("la scheda video che resta in attesa", "the graphics card waiting"), L.T("La scheda video aspetta il resto del sistema: il collo di bottiglia non è lei ma processore, memoria o disco.", "The graphics card waits for the rest of the system: the bottleneck is not it but the processor, memory or disk.")),
        ["gpu_clock_drop"] = (L.T("la scheda video che abbassa la frequenza", "the graphics card lowering its clock"), L.T("La scheda video rallenta da sola: controlla temperature e limiti di potenza impostati.", "The graphics card slows down by itself: check temperatures and the power limits set.")),
        ["gpu_throttled"] = (L.T("la scheda video frenata da un limite", "the graphics card held back by a limit"), L.T("La scheda video viene limitata (temperatura o potenza): controlla ventole e limiti impostati.", "The graphics card is being limited (temperature or power): check the fans and the limits set.")),
    };

    /// <summary>
    /// What one session alone points to, in a few words for the sessions table; null when nothing stands
    /// out, or when there were too few lags or stutters to say. Marked lags count more than stutters
    /// found automatically: the person felt those.
    /// </summary>
    public static string? Cause(SessionSummary summary)
    {
        static MetricVerdict? Top(IReadOnlyList<MetricVerdict> verdicts) =>
            verdicts.FirstOrDefault(v => Meaning.ContainsKey(v.Metric) && v.Marks >= 3 && v.Lift >= 2 && v.HitRate >= 0.4);

        if (Top(summary.Verdicts) is { } lag) return L.T("prima dei lag: ", "before the lags: ") + Meaning[lag.Metric].Label;
        if (Top(summary.HitchVerdicts) is { } hitch) return L.T("durante gli scatti: ", "during the stutters: ") + Meaning[hitch.Metric].Label;
        // No stutter to explain, but the connection misbehaved: say where, since that decides what to do about it.
        if (summary.NetLags >= 3)
            return summary.NetWhere switch
            {
                "router" => L.T("rete: il tratto fino al router (Wi-Fi)", "network: the stretch to the router (Wi-Fi)"),
                "server" => L.T("rete: il percorso verso il server del gioco", "network: the route to the game server"),
                _ => L.T("rete: la linea, oltre il router", "network: the line, beyond the router"),
            };
        return null;
    }

    /// <summary>Every cause <see cref="Cause"/> can write, as a way to build it in the current language.</summary>
    private static IEnumerable<Func<string>> AllCauses()
    {
        foreach (var metric in Meaning.Keys)
        {
            yield return () => L.T("prima dei lag: ", "before the lags: ") + Meaning[metric].Label;
            yield return () => L.T("durante gli scatti: ", "during the stutters: ") + Meaning[metric].Label;
        }
        yield return () => L.T("rete: il tratto fino al router (Wi-Fi)", "network: the stretch to the router (Wi-Fi)");
        yield return () => L.T("rete: il percorso verso il server del gioco", "network: the route to the game server");
        yield return () => L.T("rete: la linea, oltre il router", "network: the line, beyond the router");
    }

    /// <summary>A cause saved with a session, in the language of now (sessions saved in the other language included).</summary>
    public static string InCurrentLanguage(string saved)
    {
        foreach (var cause in AllCauses())
        {
            var (italian, english) = L.Both(cause);
            if (saved == italian || saved == english) return cause();
        }
        return saved;
    }

    /// <summary>What the marked lags of a game have in common, once there are enough of them to say.</summary>
    public static NewFinding? Lag(string game, IReadOnlyList<StoredEvent> sessions, int minMarks, int days)
    {
        var (marks, suspects) = Suspects(sessions, "marks", "m.");
        if (marks < minMarks) return null;

        var intro = L.T($"{marks} lag segnati negli ultimi {days} giorni.", $"{marks} lags marked in the last {days} days.");
        if (suspects.Count == 0)
        {
            return new NewFinding($"lag|{game.ToLowerInvariant()}", Severity.Info,
                L.T($"Lag in {game}: nessuna causa evidente tra quelle misurate", $"Lag in {game}: no clear cause among those measured"),
                intro + L.T(" Rete, processore, memoria, disco e scheda video non mostrano niente di insolito prima dei lag.", " Network, processor, memory, disk and graphics card show nothing unusual before the lags."),
                L.T("Se i lag ci sono, la causa è fuori da ciò che il PC può misurare: più probabilmente i server del gioco o il gioco stesso.", "If the lags are real, the cause is outside what the PC can measure: most likely the game servers or the game itself."),
                Area: FindingArea.Network);
        }

        var main = suspects[0];
        var detail = intro + L.T(" Prima di un lag, ", " Before a lag, ") + Describe(main);
        if (suspects.Count > 1) detail += L.T(" Anche ", " Also, ") + Describe(suspects[1]);
        return new NewFinding($"lag|{game.ToLowerInvariant()}", Severity.Medium,
            L.T($"Lag in {game}: l'indiziato è {Meaning[main.Metric].Label}", $"Lag in {game}: the suspect is {Meaning[main.Metric].Label}"), detail, Meaning[main.Metric].Advice,
            Area: main.Metric.StartsWith("ping_", StringComparison.Ordinal) ? FindingArea.Network : FindingArea.Hardware);
    }

    /// <summary>
    /// What the stutters the recorder found by itself have in common. Unlike marked lags, these need no
    /// key press; but only a clear cause is worth reporting, since some stutter is normal in any game.
    /// </summary>
    public static NewFinding? Stutter(string game, IReadOnlyList<StoredEvent> sessions, int minHitches, int days)
    {
        var (hitches, suspects) = Suspects(sessions, "hitches", "h.");
        if (hitches < minHitches || suspects.Count == 0) return null;

        var main = suspects[0];
        var detail = L.T($"{hitches} scatti (cali improvvisi dei frame) negli ultimi {days} giorni. Durante uno scatto, ", $"{hitches} stutters (sudden frame drops) in the last {days} days. During a stutter, ") + Describe(main);
        if (suspects.Count > 1) detail += L.T(" Anche ", " Also, ") + Describe(suspects[1]);
        return new NewFinding($"stutter|{game.ToLowerInvariant()}", Severity.Medium,
            L.T($"Scatti in {game}: l'indiziato è {Meaning[main.Metric].Label}", $"Stutters in {game}: the suspect is {Meaning[main.Metric].Label}"), detail, Meaning[main.Metric].Advice);
    }

    /// <summary>
    /// Adds up, over the sessions, how often each measurement was unusually bad at the moments counted
    /// under <paramref name="countKey"/>, and keeps the (at most two) that stand out from chance.
    /// </summary>
    private static (int Moments, List<MetricVerdict> Suspects) Suspects(IReadOnlyList<StoredEvent> sessions, string countKey, string prefix)
    {
        var relevant = sessions.Where(s => Int(s, countKey) > 0).ToList();
        var moments = relevant.Sum(s => Int(s, countKey));
        var seconds = relevant.Sum(s => Int(s, "seconds"));

        var suspects = Meaning.Keys
            .Select(metric =>
            {
                var hits = 0;
                var weightedBase = 0d;
                foreach (var s in relevant)
                {
                    var parts = s.Data.GetValueOrDefault($"{prefix}{metric}", "0;0").Split(';');
                    hits += int.Parse(parts[0], CultureInfo.InvariantCulture);
                    weightedBase += double.Parse(parts[1], CultureInfo.InvariantCulture) * Int(s, "seconds");
                }
                return new MetricVerdict(metric, 0, seconds == 0 ? 0 : weightedBase / seconds, hits, moments);
            })
            .Where(v => v.Lift >= 2 && v.HitRate >= 0.4)
            .OrderByDescending(v => v.Lift)
            .Take(2)
            .ToList();
        return (moments, suspects);
    }

    private static string Describe(MetricVerdict v) =>
        L.T($"{Meaning[v.Metric].Label} si presenta {v.HitRate.ToString("P0", Lang)} delle volte, contro {v.BaseRate.ToString("P0", Lang)} di un momento qualsiasi.", $"{Meaning[v.Metric].Label} shows up {v.HitRate.ToString("P0", Lang)} of the time, against {v.BaseRate.ToString("P0", Lang)} at any other moment.");

    /// <summary>The last session ran clearly slower than this game usually does.</summary>
    public static NewFinding? FpsDrop(string game, IReadOnlyList<StoredEvent> sessions)
    {
        var usable = sessions
            .Where(s => Number(s, "fps_avg") > 0 && Int(s, "seconds") >= 600)
            .OrderBy(s => s.Time)
            .ToList();
        if (usable.Count < 4) return null;

        var latest = Number(usable[^1], "fps_avg");
        var earlier = usable.Take(usable.Count - 1).Select(s => Number(s, "fps_avg")).Order().ToList();
        var usual = earlier[earlier.Count / 2];
        if (latest >= usual * 0.8) return null;

        return new NewFinding($"fps|{game.ToLowerInvariant()}", Severity.Low,
            L.T($"{game}: FPS calati del {((1 - latest / usual) * 100).ToString("0", Lang)}% rispetto al solito", $"{game}: FPS down {((1 - latest / usual) * 100).ToString("0", Lang)}% from usual"),
            L.T($"Nell'ultima sessione {latest.ToString("0", Lang)} FPS medi; nelle {earlier.Count} precedenti di solito {usual.ToString("0", Lang)}.", $"Last session {latest.ToString("0", Lang)} FPS average; in the {earlier.Count} before, usually {usual.ToString("0", Lang)}."),
            L.T("Guarda cosa è cambiato da allora: driver video, aggiornamento del gioco, mod o impostazioni grafiche.", "Look at what changed since then: graphics driver, game update, mods or graphics settings."));
    }

    private static int Int(StoredEvent e, string key) =>
        int.TryParse(e.Data.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Number(StoredEvent e, string key) =>
        double.TryParse(e.Data.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
