using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Monitor;

/// <summary>
/// Turns a day of per-minute measurements into the problems worth showing. Pure: no I/O, no clock.
/// Each sample is one minute, so "how many samples are over the limit" is "for how many minutes".
/// </summary>
public static class MonitorRules
{
    private static CultureInfo Lang => L.Culture;

    public static NewFinding? Memory(IReadOnlyList<SamplePoint> usedPct, int threshold, int minMinutes, string? topConsumers)
    {
        var over = usedPct.Where(s => s.Value >= threshold).ToList();
        if (over.Count < minMinutes) return null;
        var detail = L.T($"La RAM occupata ha superato il {threshold}% (picco {over.Max(s => s.Value).ToString("0", Lang)}%).", $"RAM in use went over {threshold}% (peak {over.Max(s => s.Value).ToString("0", Lang)}%).");
        if (!string.IsNullOrEmpty(topConsumers)) detail += L.T($" Nell'ultimo episodio occupavano di più: {topConsumers}.", $" In the last episode the biggest users were: {topConsumers}.");
        return new NewFinding("ram", over.Count >= 60 ? Severity.High : Severity.Medium,
            L.T($"Memoria quasi piena per {Minutes(over.Count)} nelle ultime 24 ore", $"Memory almost full for {Minutes(over.Count)} in the last 24 hours"), detail,
            L.T("Quando la memoria è piena Windows sposta dati su disco e i giochi scattano. Chiudi i programmi più pesanti prima di giocare; la soluzione definitiva è aggiungere RAM.", "When memory is full Windows moves data to disk and games stutter. Close the heaviest programs before playing; the real fix is adding RAM."));
    }

    public static NewFinding? Temperature(string key, string what, IReadOnlyList<SamplePoint> celsius, int limit, int minMinutes, string advice)
    {
        var over = celsius.Where(s => s.Value >= limit).ToList();
        if (over.Count < minMinutes) return null;
        return new NewFinding(key, over.Count >= 30 ? Severity.High : Severity.Medium,
            L.T($"{what} sopra {limit} °C per {Minutes(over.Count)} nelle ultime 24 ore", $"{what} above {limit} °C for {Minutes(over.Count)} in the last 24 hours"),
            L.T($"Picco {over.Max(s => s.Value).ToString("0", Lang)} °C, l'ultima volta alle {over.MaxBy(s => s.Time)!.Time.ToLocalTime():HH:mm}.", $"Peak {over.Max(s => s.Value).ToString("0", Lang)} °C, last time at {over.MaxBy(s => s.Time)!.Time.ToLocalTime():HH:mm}."),
            advice);
    }

    /// <param name="routerLoss">Packet loss towards the router, per minute.</param>
    /// <param name="internetLoss">Packet loss towards an internet host, per minute.</param>
    public static NewFinding? Network(IReadOnlyList<SamplePoint> routerLoss, IReadOnlyList<SamplePoint> internetLoss, int lossThreshold, int minMinutes)
    {
        static long Minute(SamplePoint s) => s.Time.ToUnixTimeSeconds() / 60;
        var badRouter = routerLoss.Where(s => s.Value >= lossThreshold).Select(Minute).ToHashSet();
        var badInternet = internetLoss.Where(s => s.Value >= lossThreshold).Select(Minute).ToHashSet();
        var bad = badRouter.Union(badInternet).Count();
        if (bad < minMinutes) return null;

        // If the router itself stops answering, whatever lies beyond it cannot be blamed.
        var atHome = badRouter.Count * 2 >= bad;
        return new NewFinding("net", bad >= 30 ? Severity.Medium : Severity.Low,
            L.T($"Connessione instabile per {Minutes(bad)} nelle ultime 24 ore", $"Unstable connection for {Minutes(bad)} in the last 24 hours"),
            atHome
                ? L.T($"I pacchetti si perdono già tra il PC e il router ({Minutes(badRouter.Count)}): il problema è in casa.", $"Packets are already lost between the PC and the router ({Minutes(badRouter.Count)}): the problem is at home.")
                : L.T("Il collegamento con il router è pulito: i pacchetti si perdono oltre, sulla linea o dal provider.", "The link to the router is clean: packets are lost further out, on the line or at the provider."),
            atHome
                ? L.T("Se sei in Wi-Fi, il cavo o un canale meno affollato risolvono quasi sempre; altrimenti controlla cavo e router.", "On Wi-Fi, a cable or a less crowded channel almost always fixes it; otherwise check the cable and the router.")
                : L.T("Se succede sempre agli stessi orari è congestione della linea: questi dati servono per segnalarlo al provider.", "If it always happens at the same times it is line congestion: these data help to report it to the provider."),
            Area: FindingArea.Network);
    }

    public static List<NewFinding> Disks(IReadOnlyCollection<LiveValue> current, int minFreePct)
    {
        var findings = new List<NewFinding>();
        foreach (var pct in current.Where(v => v.Key.StartsWith("disk.") && v.Key.EndsWith(".free_pct") && v.Value < minFreePct))
        {
            var letter = pct.Key.Split('.')[1];
            var gb = current.FirstOrDefault(v => v.Key == $"disk.{letter}.free_gb")?.Value;
            findings.Add(new NewFinding($"disk.{letter}", pct.Value < minFreePct / 2d ? Severity.High : Severity.Medium,
                L.T($"Disco {letter}: quasi pieno", $"Disk {letter}: almost full"),
                L.T("Resta libero il ", "Free: ") + $"{pct.Value.ToString("0", Lang)}%{(gb is null ? "" : $" ({gb.Value.ToString("0", Lang)} GB)")}.",
                L.T("Con poco spazio Windows e i giochi rallentano e gli aggiornamenti possono fallire.", "With little space Windows and games slow down and updates can fail.")));
        }
        return findings;
    }

    private static string Minutes(int n) => n == 1 ? L.T("1 minuto", "1 minute") : n < 120 ? L.T($"{n} minuti", $"{n} minutes") : L.T($"{n / 60} ore", $"{n / 60} hours");
}
