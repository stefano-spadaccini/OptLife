using System.Globalization;
using System.Text.RegularExpressions;

namespace OptLife.Modules.Wifi;

/// <summary>The Wi-Fi link as Windows reports it right now.</summary>
/// <param name="BandGHz">2.4, 5 or 6.</param>
/// <param name="RxMbps">The speed the PC receives at over the air (the link, not the internet).</param>
/// <param name="Signal">0-100 %.</param>
/// <summary>The Wi-Fi adapter as Windows names it, and its link speed when connected.</summary>
public sealed record WifiAdapter(string Model, int? SpeedMbps);

public sealed record WifiLink(string Ssid, double? BandGHz, int? Channel, string? Radio, int? RxMbps, int? TxMbps, int? Signal);

/// <summary>A network card with a cable, as Windows reports it.</summary>
/// <param name="Id">Windows' own id of the card: stable, used to follow it between checks.</param>
/// <param name="Up">Connected (cable in, link on).</param>
/// <param name="InUse">It carries the traffic: up, with a gateway (the router).</param>
/// <param name="Errors">Damaged or dropped packets counted by the card since Windows started (both ways).</param>
public sealed record WiredLink(string Id, string Name, string Model, bool Up, bool InUse, long SpeedMbps, long Errors);

/// <summary>The rules for the cable: speed against what the card can do, errors, disconnections.</summary>
public static class WiredRules
{
    /// <summary>Damaged or dropped packets between two checks (5 minutes) worth a look: a few are normal, hundreds are a bad cable or port.</summary>
    public const long ErrorsWorthALook = 100;

    /// <summary>A card made for at least a gigabit, told by its name (most say it; Intel and Realtek by their chip).</summary>
    public static bool Gigabit(string model)
    {
        var m = model.ToUpperInvariant();
        return new[] { "GIGABIT", "GBE", "2.5G", "5G ", "10G", "I210", "I211", "I217", "I218", "I219", "I225", "I226", "RTL8111", "RTL8168", "RTL8125", "RTL8126",
                "KILLER E2", "KILLER E3", "AQC", "AQUANTIA", "MARVELL", "E3100", "E2600", "E2500" }
            .Any(m.Contains);
    }

    /// <summary>"1 Gbps", "100 Mbps", "2.5 Gbps".</summary>
    public static string Speed(long mbps) => mbps >= 1000 ? (mbps / 1000.0).ToString("0.#", L.Culture) + " Gbps" : $"{mbps} Mbps";

    /// <summary>A gigabit (or faster) card that runs at 100 Mbps or less: nearly always the cable or the router port.</summary>
    public static bool Slow(WiredLink w) => w.Up && w.SpeedMbps is > 0 and <= 100 && Gigabit(w.Model);

    public static string SlowAdvice => L.T(
        "Una scheda da 1 Gbps che si collega a 100 Mbps quasi sempre ha un cavo rovinato o vecchio (servono almeno 4 coppie di fili: Cat5e, Cat6), un connettore che non entra bene o una porta del router o dello switch da 100. Prova un altro cavo e un'altra porta del router.",
        "A 1 Gbps card linking at 100 Mbps nearly always has a damaged or old cable (all 4 wire pairs are needed: Cat5e, Cat6), a loose plug or a 100 Mbps router or switch port. Try another cable and another router port.");

    public static string ErrorAdvice => L.T(
        "Pacchetti rovinati sul cavo: di solito il cavo (piegato, schiacciato, troppo lungo) o il connettore. Prova un altro cavo; se restano, un'altra porta del router o il driver della scheda.",
        "Damaged packets on the cable: usually the cable (bent, crushed, too long) or the plug. Try another cable; if they stay, another router port or the card's driver.");

    public static string DropAdvice => L.T(
        "Il cavo si è staccato o la porta ha perso il collegamento. Se succede da solo: cavo o connettore lenti, risparmio energia della scheda (Gestione dispositivi → la scheda → Risparmio energia: togli \"Consenti al computer di spegnere il dispositivo\"), o il router che si riavvia.",
        "The cable came out or the port lost the link. If it happens by itself: a loose cable or plug, the card's power saving (Device Manager → the card → Power Management: untick \"Allow the computer to turn off this device\"), or the router restarting.");

    /// <summary>What the gateway ping of the last minutes means for gaming.</summary>
    public static string PingMeaning(double ms, double loss) => loss >= 2
        ? L.T("Si perdono pacchetti già verso il router: è la rete di casa, non internet.", "Packets are lost already on the way to the router: it is the home network, not the internet.")
        : ms <= 5 ? L.T("Ottimo: verso il router il ritardo è quasi zero, come deve essere.", "Excellent: the delay to the router is almost zero, as it should be.")
        : ms <= 20 ? L.T("Buono, anche se un cavo darebbe quasi zero.", "Good, though a cable would give almost zero.")
        : L.T("Alto per una rete di casa: Wi-Fi disturbato o router sotto sforzo.", "High for a home network: crowded Wi-Fi or a router under strain.");
}

/// <summary>The rules of the Wi-Fi page, without the machine.</summary>
public static partial class WifiRules
{
    /// <summary>
    /// Reads "netsh wlan show interfaces" in any language: values are told by their shape (GHz, %, 802.11) and the two "(Mbps)"
    /// lines come in the order receive, transmit; only "SSID" and the channel are found by their label. The first connected
    /// interface (the one with an SSID) wins.
    /// </summary>
    public static WifiLink? Parse(string output)
    {
        string? ssid = null, radio = null;
        double? band = null;
        int? channel = null, signal = null;
        var rates = new List<int>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var cut = raw.IndexOf(" : ", StringComparison.Ordinal);
            if (cut < 0) continue;
            var key = raw[..cut].Trim();
            var value = raw[(cut + 3)..].Trim();
            if (key.Length == 0) continue;
            // A second interface starts with its name: stop once the first connected one is read.
            if (ssid is not null && key is "Name" or "Nome" && rates.Count >= 2) break;
            if (key == "SSID") { ssid ??= value; continue; }
            if (Ghz().Match(value) is { Success: true } g) { band ??= double.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            if (value.StartsWith("802.11", StringComparison.Ordinal)) { radio ??= value; continue; }
            if (key.Contains("(Mbps)", StringComparison.Ordinal) && int.TryParse(value.Split('.', ',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) { rates.Add(r); continue; }
            if (Percent().Match(value) is { Success: true } p) { signal ??= int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            if (key is "Channel" or "Canale" && int.TryParse(value, out var c)) channel ??= c;
        }
        if (ssid is null || ssid.Length == 0) return null;
        band ??= channel switch { >= 1 and <= 14 => 2.4, >= 32 and <= 177 => 5, _ => null };
        return new WifiLink(ssid, band, channel, radio, rates.Count > 0 ? rates[0] : null, rates.Count > 1 ? rates[1] : null, signal);
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*GHz$")]
    private static partial Regex Ghz();

    [GeneratedRegex(@"^(\d{1,3})\s*%$")]
    private static partial Regex Percent();

    /// <summary>Windows needs Location on (or administrator rights) to tell anything about Wi-Fi since Windows 11 24H2.</summary>
    public static bool NeedsLocation(string output) =>
        output.Contains("location", StringComparison.OrdinalIgnoreCase) || output.Contains("posizione", StringComparison.OrdinalIgnoreCase)
        || output.Contains("ms-settings:privacy-location", StringComparison.OrdinalIgnoreCase);

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>The band seen most often: what this PC normally gets.</summary>
    public static double? UsualBand(IReadOnlyList<double> bands) =>
        bands.Count == 0 ? null : bands.GroupBy(b => b).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

    public static string Band(double? ghz) => ghz is { } g ? g.ToString("0.#", L.Culture) + " GHz" : L.T("banda sconosciuta", "unknown band");

    /// <summary>Readings needed (every 5 minutes) before "usual" means anything: about an hour.</summary>
    public const int Needed = 12;

    public static int MinutesLeft(int readings) => Math.Max(5, (Needed - readings) * 5);

    /// <summary>What a link speed is enough for, in words anyone reads. It is the link: the internet line may be slower.</summary>
    public static string Meaning(int mbps) => mbps switch
    {
        >= 300 => L.T("Ottima: più che abbastanza per giocare online, video in 4K e scaricare veloce (se la linea internet lo permette).",
                      "Excellent: more than enough for online gaming, 4K video and fast downloads (if the internet line allows)."),
        >= 100 => L.T("Buona: va bene per giocare online e per i video in 4K; per scaricare giochi grandi conta soprattutto la linea internet.",
                      "Good: fine for online gaming and 4K video; for downloading big games the internet line matters most."),
        >= 30 => L.T("Sufficiente per giocare e per i video in HD, ma i download sono lenti e basta poco perché scatti.",
                     "Enough for gaming and HD video, but downloads are slow and it takes little to stutter."),
        _ => L.T("Bassa: i giochi online possono avere lag e i video scattare. Avvicinati al router o usa un cavo.",
                 "Low: online games may lag and videos stutter. Move closer to the router or use a cable."),
    };

    public static string BandMeaning(double ghz) => ghz >= 5
        ? L.T("Banda veloce e poco disturbata: la migliore per giocare.", "Fast, uncrowded band: the best for gaming.")
        : L.T("Banda lenta e affollata (la usano anche i vicini e molti dispositivi), ma arriva più lontano.", "Slow, crowded band (neighbours and many devices use it too), but it reaches farther.");

    public static string SignalMeaning(int signal) => signal switch
    {
        >= 70 => L.T("Forte.", "Strong."),
        >= 40 => L.T("Discreto: può bastare, ma i muri lo indeboliscono.", "Fair: may be enough, but walls weaken it."),
        _ => L.T("Debole: la velocità scende e si perdono pacchetti.", "Weak: the speed drops and packets get lost."),
    };

    /// <summary>The Wi-Fi generation, from what Windows reports (802.11ax…) or the adapter's name ("Wi-Fi 6E AX210").</summary>
    public static string? Standard(string model, string? radio)
    {
        var text = $"{radio} {model}".ToUpperInvariant();
        return text switch
        {
            var t when t.Contains("802.11BE") || t.Contains("WI-FI 7") || t.Contains("BE200") => L.T("Wi-Fi 7: la generazione più recente.", "Wi-Fi 7: the latest generation."),
            var t when t.Contains("6E") || t.Contains("AX210") || t.Contains("AX211") || t.Contains("AX411") => L.T("Wi-Fi 6E: recente, usa anche la banda 6 GHz.", "Wi-Fi 6E: recent, also uses the 6 GHz band."),
            var t when t.Contains("802.11AX") || t.Contains("WI-FI 6") || t.Contains("AX200") || t.Contains("AX201") || t.Contains(" AX") => L.T("Wi-Fi 6: recente e veloce.", "Wi-Fi 6: recent and fast."),
            var t when t.Contains("802.11AC") || t.Contains("WI-FI 5") || t.Contains(" AC") => L.T("Wi-Fi 5: va bene, ma un router e una scheda Wi-Fi 6 sono più stabili con tanti dispositivi.", "Wi-Fi 5: fine, but a Wi-Fi 6 router and adapter are steadier with many devices."),
            var t when t.Contains("802.11N") => L.T("Wi-Fi 4: vecchio e lento; una scheda nuova (o un cavo) cambia molto.", "Wi-Fi 4: old and slow; a new adapter (or a cable) makes a big difference."),
            _ => null,
        };
    }
}
