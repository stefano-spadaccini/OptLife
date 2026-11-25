using OptLife.Core;

namespace OptLife.Modules.Setup;

public sealed record DisplayInfo(string Id, string Name, int Width, int Height, int Hz, int MaxHz, bool? Hdr);

/// <param name="RatedMhz">The speed the module is built for, as it declares itself.</param>
/// <param name="ConfiguredMhz">The speed it is running at.</param>
/// <summary>
/// What the BIOS has set and Windows lets anyone read, without drivers. Null = could not be read (then the line is left out).
/// </summary>
/// <param name="BiosVersion">Version and date of the BIOS ("3607 · 18/03/2024").</param>
/// <param name="TpmVersion">2 for TPM 2.0, 1 for 1.2, 0 when there is none (or it is off in the BIOS).</param>
/// <param name="Bar1Mb">The window the processor has on the graphics memory: 256 MB without Resizable BAR, about all of it with.</param>
/// <param name="PcieGen">The PCIe generation the card and the board can reach together; <paramref name="PcieGenCard"/> the card alone.</param>
public sealed record FirmwareFacts(
    string? BiosVersion = null, bool? Uefi = null, bool? SecureBoot = null, int? TpmVersion = null, bool? Virtualization = null,
    string? GpuName = null, long? Bar1Mb = null, long? VramMb = null,
    uint? PcieGen = null, uint? PcieGenCard = null, uint? PcieWidth = null, uint? PcieWidthMax = null);

public sealed record MemoryModule(int SizeMb, string Kind, int RatedMhz, int ConfiguredMhz, string Manufacturer, string PartNumber);

/// <summary>Turns raw facts about the PC into readings: a label, a value in words, and a verdict where one exists.</summary>
public static class SetupRules
{
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static IEnumerable<Reading> Display(DisplayInfo d)
    {
        // Rates such as 143.98 are listed as 143 or 144 depending on who is asked: one hertz of slack.
        var slow = d.Hz + 1 < d.MaxHz;
        yield return new Reading(
            $"display.{d.Id}.mode", L.T($"Schermo {d.Name}", $"Screen {d.Name}"), L.T($"{d.Width}×{d.Height} a {d.Hz} Hz", $"{d.Width}×{d.Height} at {d.Hz} Hz"),
            Problem: slow ? L.T($"Lo schermo {d.Name} va a {d.Hz} Hz ma può arrivare a {d.MaxHz} Hz", $"Screen {d.Name} runs at {d.Hz} Hz but can reach {d.MaxHz} Hz") : null,
            Advice: slow
                ? L.T("Impostazioni di Windows → Sistema → Schermo → Schermo avanzato → scegli la frequenza di aggiornamento più alta.", "Windows Settings → System → Display → Advanced display → choose the highest refresh rate.")
                : L.T("Impostazioni di Windows → Sistema → Schermo.", "Windows Settings → System → Display."),
            Severity: Severity.Medium,
            Fix: slow ? new FindingFix(SetupModule.SetRefresh, L.T($"Porta a {d.MaxHz} Hz", $"Set to {d.MaxHz} Hz"), d.Id) : null,
            Good: !slow, Expected: L.T($"{d.MaxHz} Hz, la massima", $"{d.MaxHz} Hz, the highest"));

        if (d.Hdr is { } hdr)
            yield return new Reading($"display.{d.Id}.hdr", L.T($"HDR su {d.Name}", $"HDR on {d.Name}"), hdr ? L.T("acceso", "on") : L.T("spento", "off"),
                Advice: L.T("Impostazioni di Windows → Sistema → Schermo → HDR.", "Windows Settings → System → Display → HDR."));
    }

    public static IEnumerable<Reading> Memory(IReadOnlyList<MemoryModule> modules)
    {
        var known = modules.Where(m => m.ConfiguredMhz > 0).ToList();
        if (known.Count == 0) yield break;

        var configured = known.Min(m => m.ConfiguredMhz);
        var rated = known.Min(m => m.RatedMhz);
        var kind = known[0].Kind;
        // What every module of that generation does with no profile at all.
        var baseSpeed = kind switch { "DDR4" => 2666, "DDR5" => 4800, _ => 0 };

        var below = rated > 0 && configured + 30 < rated;
        var atBase = !below && baseSpeed > 0 && configured <= baseSpeed;
        yield return new Reading(
            "ram.speed", L.T("Velocità della memoria", "Memory speed"), $"{configured} MHz",
            Problem: below ? L.T($"La memoria va a {configured} MHz ma è fatta per {rated} MHz", $"Memory runs at {configured} MHz but is made for {rated} MHz") : null,
            Advice: L.T("Nel BIOS attiva il profilo di memoria (XMP, DOCP o EXPO). È un'impostazione del BIOS: OptLife la segnala ma non la tocca.", "In the BIOS turn on the memory profile (XMP, DOCP or EXPO). It is a BIOS setting: OptLife reports it but does not touch it."),
            Severity: Severity.Medium,
            Note: atBase
                ? L.T($"La memoria va a {configured} MHz, la velocità di base delle {kind}. Se il kit è venduto come più veloce, il profilo di memoria nel BIOS non è attivo.", $"Memory runs at {configured} MHz, the base speed of {kind}. If the kit is sold as faster, the memory profile in the BIOS is not on.")
                : null,
            Good: !atBase, Expected: rated > 0 ? L.T($"{rated} MHz, quella di targa", $"{rated} MHz, the rated one") : null);
    }

    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public static Reading PowerPlan(string guid, string name)
    {
        var saver = guid.Equals(PowerSaver, StringComparison.OrdinalIgnoreCase);
        return new Reading(
            "power.plan", L.T("Piano energetico", "Power plan"), name,
            Problem: saver ? L.T("Il piano energetico è su Risparmio di energia", "The power plan is on Power saver") : null,
            Advice: L.T("Pannello di controllo → Opzioni risparmio energia: su un PC fisso da gioco va bene Bilanciato o Prestazioni elevate.", "Control Panel → Power Options: on a gaming desktop Balanced or High performance are fine."),
            Severity: Severity.Medium,
            Fix: saver ? new FindingFix(SetupModule.SetPowerPlan, L.T("Passa a Bilanciato", "Switch to Balanced"), Balanced) : null,
            Good: !saver, Expected: L.T("Bilanciato o Prestazioni elevate", "Balanced or High performance"));
    }

    /// <param name="value">The registry number; null when Windows has never written it (its default applies).</param>
    public static Reading GameMode(int? value)
    {
        var off = value == 0;
        return new Reading(
            "windows.gamemode", L.T("Modalità gioco di Windows", "Windows Game Mode"), off ? L.T("spenta", "off") : L.T("accesa", "on"),
            Problem: off ? L.T("La Modalità gioco di Windows è spenta", "Windows Game Mode is off") : null,
            Advice: L.T("Impostazioni di Windows → Giochi → Modalità gioco. Tiene fermi aggiornamenti e attività in background mentre giochi.", "Windows Settings → Gaming → Game Mode. It holds back updates and background tasks while you play."),
            Fix: off ? new FindingFix(SetupModule.EnableGameMode, L.T("Accendila", "Turn it on")) : null,
            Good: !off, Expected: L.T("accesa", "on"));
    }

    /// <param name="output">The default playback device as Windows names it; null when there is none.</param>
    /// <param name="equalizerOnOutput">Whether Equalizer APO is attached to it; null when Equalizer APO is not installed.</param>
    /// <param name="equalizerOnTwin">
    /// Another entry of the very same device has the equalizer: Windows made a new entry for it (a different
    /// USB port, an update) and the equalizer stayed on the old one. That is the case worth calling a problem.
    /// </param>
    public static IEnumerable<Reading> Audio(string? output, string? input, bool? equalizerOnOutput, bool equalizerOnTwin)
    {
        var where = L.T("Impostazioni di Windows → Sistema → Audio.", "Windows Settings → System → Sound.");
        if (output is not null) yield return new Reading("audio.output", L.T("Uscita audio predefinita", "Default audio output"), output, Advice: where);
        if (input is not null) yield return new Reading("audio.input", L.T("Microfono predefinito", "Default microphone"), input, Advice: where);
        if (output is null || equalizerOnOutput is not { } on) yield break;

        var lost = !on && equalizerOnTwin;
        yield return new Reading(
            "audio.eq", L.T("Equalizzatore sull'uscita predefinita", "Equalizer on the default output"), on ? L.T("attivo", "active") : L.T("non attivo", "not active"),
            Problem: lost ? L.T($"L'equalizzatore non è più attivo su {output}", $"The equalizer is no longer active on {output}") : null,
            Advice: lost
                ? L.T("Windows ha creato una nuova voce per lo stesso dispositivo (capita cambiando porta USB o dopo un aggiornamento) e l'equalizzatore è rimasto sulla vecchia. " +
                  "Nel selettore dei dispositivi di Equalizer APO spunta l'uscita in uso, conferma e riavvia il PC.",
                  "Windows made a new entry for the same device (it happens when changing USB port or after an update) and the equalizer stayed on the old one. In the Equalizer APO Device Selector tick the output in use, confirm and restart the PC.")
                : L.T("Selettore dei dispositivi di Equalizer APO (Device Selector).", "Equalizer APO Device Selector."),
            Severity: Severity.Medium,
            Fix: lost ? new FindingFix(SetupModule.OpenEqualizer, L.T("Apri il selettore", "Open the selector")) : null,
            // Off on purpose is a choice; off because Windows moved the device is not.
            Good: on ? true : null, Expected: on || lost ? L.T("attivo", "active") : null);
    }

    public static Reading HardwareScheduling(int? value) => new(
        "windows.hags", L.T("Pianificazione GPU con accelerazione hardware", "Hardware-accelerated GPU scheduling"),
        value switch { 2 => L.T("accesa", "on"), 1 => L.T("spenta", "off"), _ => L.T("predefinita", "default") },
        Advice: L.T("Impostazioni di Windows → Sistema → Schermo → Grafica → Impostazioni grafiche avanzate. Con una scheda video recente conviene accesa: abbassa un po' la latenza e serve alla generazione di frame.", "Windows Settings → System → Display → Graphics → Advanced graphics settings. With a recent graphics card keep it on: it lowers latency a little and frame generation needs it."),
        Good: value switch { 2 => true, 1 => false, _ => null }, Expected: L.T("accesa", "on"));

    public static Reading MemoryIntegrity(int? value)
    {
        var on = value == 1;
        return new Reading(
            "windows.memory_integrity", L.T("Integrità della memoria (isolamento core)", "Memory integrity (core isolation)"), on ? L.T("accesa", "on") : L.T("spenta", "off"),
            Advice: L.T("Sicurezza di Windows → Sicurezza dispositivi → Isolamento core.", "Windows Security → Device security → Core isolation."),
            Note: on
                ? L.T("Protegge da driver malevoli, ma nei giochi può costare qualche punto percentuale di frame. Le guide non sono d'accordo se convenga spegnerla: è uno scambio tra sicurezza e prestazioni.", "It protects against malicious drivers, but in games it can cost a few percent of frames. Guides disagree on whether to turn it off: it is a trade between security and performance.")
                : null);
    }

    /// <summary>Windows keeps these two as "Name=0;Name=1;" in a single text value.</summary>
    public static IEnumerable<Reading> DirectX(string? globalSettings)
    {
        var values = (globalSettings ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        string Say(string name) => values.GetValueOrDefault(name) switch { "1" => L.T("accesa", "on"), "0" => L.T("spenta", "off"), _ => L.T("predefinita", "default") };

        var where = L.T("Impostazioni di Windows → Sistema → Schermo → Grafica → Impostazioni grafiche avanzate.", "Windows Settings → System → Display → Graphics → Advanced graphics settings.");
        // On by default in Windows 11: "predefinita" is fine, only switched off is worth a look.
        var windowed = Say("SwapEffectUpgradeEnable");
        yield return new Reading("windows.windowed_optimizations", L.T("Ottimizzazioni per i giochi in finestra", "Optimizations for windowed games"), windowed,
            Advice: where + L.T(" Fanno andare i giochi in finestra senza bordi come quelli a schermo intero: meno latenza.", " They make borderless windowed games run like fullscreen ones: less latency."),
            Good: windowed != L.T("spenta", "off"), Expected: L.T("accesa", "on"));
        yield return new Reading("windows.vrr", L.T("Frequenza di aggiornamento variabile di Windows", "Windows variable refresh rate"), Say("VRROptimizeEnable"), Advice: where);
    }

    /// <param name="mouseSpeed">The registry text: "0" means every movement counts the same.</param>
    public static Reading MouseAcceleration(string? mouseSpeed) => new(
        "windows.mouse_accel", L.T("Accelerazione del mouse (precisione del puntatore)", "Mouse acceleration (enhance pointer precision)"), mouseSpeed is null or "0" ? L.T("spenta", "off") : L.T("accesa", "on"),
        Advice: L.T("Impostazioni di Windows → Bluetooth e dispositivi → Mouse → Impostazioni aggiuntive del mouse → Opzioni puntatore. Spenta, la stessa mossa del polso sposta sempre della stessa misura: è quella che serve per mirare.", "Windows Settings → Bluetooth & devices → Mouse → Additional mouse settings → Pointer Options. Off, the same wrist movement always moves the same distance: that is what aiming needs."),
        Good: mouseSpeed is null or "0", Expected: L.T("spenta", "off"));

    private static string OnOff(bool on) => on ? L.T("acceso", "on") : L.T("spento", "off");

    /// <summary>Firmware lines on the Setup page, in the "BIOS and firmware" section. Settings of the BIOS: OptLife reads them, never changes them.</summary>
    public static IEnumerable<Reading> Firmware(FirmwareFacts f)
    {
        if (f.BiosVersion is { Length: > 0 } version)
            yield return new Reading("bios.version", L.T("Versione del BIOS", "BIOS version"), version,
                Advice: L.T("Gli aggiornamenti del BIOS si trovano sul sito del produttore della scheda madre (pagina Aggiornamenti). Un aggiornamento riporta le impostazioni del BIOS ai valori di fabbrica: dopo, ricontrolla questa sezione.",
                    "BIOS updates are on the motherboard maker's site (Updates page). An update puts the BIOS settings back to factory values: afterwards, check this section again."));
        if (f.Uefi is { } uefi)
            yield return new Reading("bios.mode", L.T("Modalità del firmware", "Firmware mode"), uefi ? "UEFI" : L.T("Legacy (CSM)", "Legacy (CSM)"),
                Advice: L.T("In modalità Legacy (CSM) non funzionano Secure Boot e Resizable BAR. Passare a UEFI richiede che il disco di Windows sia GPT (strumento mbr2gpt di Windows): da fare con calma e con una copia dei dati.",
                    "In Legacy (CSM) mode Secure Boot and Resizable BAR do not work. Moving to UEFI needs the Windows disk to be GPT (Windows' mbr2gpt tool): do it calmly and with a backup."),
                Good: uefi, Expected: "UEFI");
        if (f.SecureBoot is { } secure)
            yield return new Reading("bios.secure_boot", "Secure Boot", OnOff(secure),
                Advice: L.T("Nel BIOS, di solito Boot → Secure Boot (a volte bisogna prima mettere \"OS Type\" su Windows UEFI). Lo richiedono Windows 11 e alcuni anti-cheat dei giochi online.",
                    "In the BIOS, usually Boot → Secure Boot (sometimes \"OS Type\" must be set to Windows UEFI first). Windows 11 and some online games' anti-cheat need it."),
                Good: secure, Expected: OnOff(true));
        if (f.TpmVersion is { } tpm)
            yield return new Reading("bios.tpm", "TPM", tpm switch { 2 => "2.0", 1 => "1.2", _ => L.T("assente o spento", "missing or off") },
                Advice: L.T("Nel BIOS si chiama fTPM (processori AMD) o PTT / Intel Platform Trust Technology (processori Intel). Lo richiedono Windows 11 e alcuni anti-cheat.",
                    "In the BIOS it is called fTPM (AMD processors) or PTT / Intel Platform Trust Technology (Intel processors). Windows 11 and some anti-cheat need it."),
                Good: tpm == 2, Expected: "2.0");
        if (f.Virtualization is { } virt)
            yield return new Reading("bios.virtualization", L.T("Virtualizzazione del processore", "Processor virtualization"), virt ? L.T("accesa", "on") : L.T("spenta", "off"),
                Advice: L.T("Nel BIOS: SVM Mode (AMD) o Intel Virtualization Technology / VT-x (Intel). Serve per gli emulatori Android (BlueStacks), WSL, le macchine virtuali e alcune protezioni di Windows.",
                    "In the BIOS: SVM Mode (AMD) or Intel Virtualization Technology / VT-x (Intel). Android emulators (BlueStacks), WSL, virtual machines and some Windows protections need it."),
                Good: virt, Expected: L.T("accesa", "on"));
        if (f.Bar1Mb is { } bar1 && f.VramMb is { } vram && vram > 0)
        {
            var on = bar1 > 512;
            // NVIDIA cards from the RTX 30 series on support it; for the others the page only says how it stands.
            var supported = f.GpuName is { } name && System.Text.RegularExpressions.Regex.IsMatch(name, @"RTX\s*(30|40|50)\d\d|RTX\s*A\d{4}|RTX\s*PRO", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            yield return new Reading("bios.rebar", "Resizable BAR", on ? L.T($"acceso ({bar1 / 1024.0:0.#} GB visibili)", $"on ({bar1 / 1024.0:0.#} GB visible)") : L.T($"spento ({bar1} MB visibili)", $"off ({bar1} MB visible)"),
                Advice: L.T("Nel BIOS: \"Above 4G Decoding\" e \"Re-Size BAR Support\" su Enabled (serve la modalità UEFI, senza CSM). Il driver NVIDIA lo usa solo nei giochi in cui aiuta, quindi acceso non fa danni.",
                    "In the BIOS: \"Above 4G Decoding\" and \"Re-Size BAR Support\" to Enabled (UEFI mode, no CSM, is needed). The NVIDIA driver uses it only in the games it helps, so on does no harm."),
                Good: on ? true : supported ? false : null, Expected: supported ? OnOff(true) : null);
        }
        if (f.PcieWidth is { } width && f.PcieWidthMax is { } widthMax && f.PcieGen is { } gen)
        {
            var narrow = width < widthMax;
            var slower = f.PcieGenCard is { } card && card > gen;
            yield return new Reading("bios.pcie", L.T("Collegamento PCIe della scheda video", "Graphics card PCIe link"), $"PCIe {gen}.0 x{width}",
                Advice: narrow
                    ? L.T($"La scheda lavora con {width} linee invece di {widthMax}: controlla che sia nello slot principale (quello più vicino al processore) e che nel BIOS il collegamento PCIe sia su Auto. Alcune schede madri dividono le linee quando sono occupati certi slot M.2.",
                        $"The card works with {width} lanes instead of {widthMax}: check it is in the main slot (the one closest to the processor) and that the PCIe link is on Auto in the BIOS. Some motherboards split the lanes when certain M.2 slots are used.")
                    : slower
                        ? L.T($"La scheda arriverebbe a PCIe {f.PcieGenCard}.0, la scheda madre (o il BIOS) la fa andare a {gen}.0: se nel BIOS la generazione PCIe è fissata a mano, rimettila su Auto. La differenza nei giochi di solito è piccola.",
                            $"The card could do PCIe {f.PcieGenCard}.0, the motherboard (or the BIOS) runs it at {gen}.0: if the PCIe generation is set by hand in the BIOS, put it back to Auto. The difference in games is usually small.")
                        : L.T("Il collegamento della scheda video è al massimo possibile. A riposo la scheda abbassa la velocità da sola per risparmiare: è normale.", "The graphics card link is at its maximum. At rest the card lowers its speed by itself to save power: that is normal."),
                Good: !narrow, Expected: narrow ? $"x{widthMax}" : null);
        }
    }

    /// <summary>SMBIOS case types of a PC that runs on a battery: portable, laptop, notebook, hand held, sub notebook, tablet, convertible, detachable.</summary>
    private static readonly byte[] PortableCases = [8, 9, 10, 11, 14, 30, 31, 32];

    /// <summary>The case says desktop, tower, all-in-one, server…: a battery there is a UPS, not a laptop.</summary>
    private static readonly byte[] FixedCases = [3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24, 35, 36];

    /// <summary>A laptop when the case says so; when the case is unknown, when it has a battery.</summary>
    public static bool IsLaptop(byte? chassis, bool battery) =>
        chassis is { } c && PortableCases.Contains(c) || !(chassis is { } f && FixedCases.Contains(f)) && battery;

    /// <summary>
    /// The readings as they fit this kind of PC, with one line that says which kind it is. On a laptop the advice meant
    /// for a desktop goes: the power plan is the person's choice (Power saver on battery is right), the graphics card
    /// limit is the maker's. The value of the line never changes with the battery level, so it never looks "changed".
    /// </summary>
    public static IReadOnlyList<Reading> ForDevice(IReadOnlyList<Reading> readings, byte? chassis, bool battery)
    {
        var laptop = IsLaptop(chassis, battery);
        var list = new List<Reading>
        {
            new("power.device", L.T("Tipo di PC", "Kind of PC"), laptop ? L.T("portatile", "laptop") : L.T("fisso", "desktop"),
                Advice: laptop
                    ? L.T("Su un portatile OptLife non consiglia il piano Prestazioni elevate né limiti della scheda video: decidono batteria e calore. Le altre voci valgono come su un PC fisso.",
                        "On a laptop OptLife does not suggest the High performance plan or graphics card limits: battery and heat decide. The other items count as on a desktop.")
                    : null),
        };
        foreach (var r in readings)
        {
            if (laptop && r.Key == "power.plan")
                list.Add(r with
                {
                    Problem = null, Fix = null, Good = null, Expected = null,
                    Advice = L.T("Su un portatile il piano lo scegli tu: con la batteria Risparmio di energia va bene, collegato alla corrente Bilanciato. Prestazioni elevate scalda e consuma la batteria.",
                        "On a laptop the plan is your choice: on battery Power saver is fine, plugged in Balanced. High performance heats up and drains the battery."),
                });
            else if (laptop && r.Key == "gpu.power_limit")
                list.Add(r with { Advice = L.T("Su un portatile il limite lo fissa il produttore, insieme al raffreddamento: va lasciato com'è.", "On a laptop the maker sets the limit, together with the cooling: leave it as it is.") });
            else
                list.Add(r);
        }
        return list;
    }

    public static Reading GpuPowerLimit(double watts) => new(
        "gpu.power_limit", L.T("Limite di potenza della scheda video", "Graphics card power limit"), $"{watts:0} W",
        Advice: L.T("Se il limite lo mette un'attività all'avvio (per esempio uno script che imposta la scheda video), controlla che sia partita.", "If the limit is set by a start-up task (for example a script that sets up the graphics card), check that it ran."));

    /// <summary>The DNS servers of a connection in use, as found (judged later against the setting).</summary>
    public static Reading Dns(string adapter, IReadOnlyList<string> servers) => new(
        $"network.dns.{adapter}", L.T($"DNS di {adapter}", $"DNS of {adapter}"),
        servers.Count == 0 ? L.T("automatico", "automatic") : string.Join(", ", servers),
        Advice: L.T("Impostazioni di Windows → Rete e Internet → la connessione → Assegnazione server DNS.", "Windows Settings → Network & internet → the connection → DNS server assignment."));

    /// <summary>Against the servers the person chose: right in that order, or wrong with a button that puts them back.</summary>
    public static Reading JudgeDns(Reading r, IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0) return r;
        var want = string.Join(", ", wanted);
        var adapter = r.Key["network.dns.".Length..];
        var right = r.Value == want;
        return r with
        {
            Problem = right ? null : L.T($"Il DNS di {adapter} è \"{r.Value}\" invece di \"{want}\"", $"The DNS of {adapter} is \"{r.Value}\" instead of \"{want}\""),
            Severity = Severity.Low,
            Fix = right ? null : new FindingFix(SetupModule.SetDns, L.T("Rimetti il DNS", "Put the DNS back"), $"{adapter}|{string.Join(",", wanted)}"),
            Good = right,
            Expected = want,
        };
    }

    /// <summary>"2 × 8 GB DDR4 BL8G32C16U4BL.M8FE": changes when modules are added, removed or replaced, not when their speed does.</summary>
    public static string? DescribeMemory(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return null;
        return string.Join(" + ", modules
            .GroupBy(m => (m.SizeMb, m.Kind, m.PartNumber))
            .Select(g => $"{g.Count()} × {g.Key.SizeMb / 1024} GB {g.Key.Kind} {g.Key.PartNumber}".Trim()));
    }
}
