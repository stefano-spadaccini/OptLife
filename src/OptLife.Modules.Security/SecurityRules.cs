using System.Security.Cryptography;
using System.Text;

namespace OptLife.Modules.Security;

/// <summary>What Microsoft Defender says about itself.</summary>
public sealed record DefenderState(bool Service, bool Antivirus, bool RealTime, bool Tamper, int SignatureAge, long FullScanAge);

public sealed record LocalAccount(string Name, bool Enabled, bool PasswordRequired);

/// <summary>A port open to the network (listening on every address), with the program behind it.</summary>
public sealed record OpenPort(int Port, string Process);

/// <summary>What is read in one go (Defender, accounts, optional features, open ports); a part is null when it could not be read.</summary>
public sealed record SecurityReport(DefenderState? Defender, IReadOnlyList<LocalAccount>? Accounts, IReadOnlyList<string>? Features, IReadOnlyList<OpenPort>? Ports);

/// <summary>A firewall rule tied to a program file.</summary>
/// <param name="Id">Stable short name built from what identifies the rule (names repeat).</param>
/// <param name="Missing">The program it is for is no longer there: the rule opens a door for nobody (or for whatever takes its place).</param>
public sealed record FirewallRule(string Id, string Name, string Program, bool Inbound, bool Allow, bool Enabled, bool Missing)
{
    public static string MakeId(string name, string program, bool inbound, string protocol, string ports) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{name}\n{program}\n{inbound}\n{protocol}\n{ports}")))[..12].ToLowerInvariant();
}

/// <summary>The firewall: on or off for each network kind, and the rules tied to programs.</summary>
public sealed record FirewallState(bool? Domain, bool? Private, bool? Public, IReadOnlyList<FirewallRule> Rules);

/// <summary>The rules of the security page, without the machine.</summary>
public static class SecurityRules
{
    public const string DisableRules = "disable-orphan-rules", DisableFeature = "disable-feature", Open = "open";
    public const string DefenderPage = "windowsdefender:", AccountsPage = "ms-settings:otherusers", FeaturesPage = "optionalfeatures";


    public static string DefenderGroup => L.T("Antivirus (Microsoft Defender)", "Antivirus (Microsoft Defender)");
    public static string FirewallGroup => L.T("Firewall", "Firewall");
    public static string AccountsGroup => L.T("Account del PC", "Accounts on this PC");
    public static string FeaturesGroup => L.T("Funzioni di Windows da tenere spente", "Windows features to keep off");
    public static string PortsGroup => L.T("Porte aperte verso la rete", "Ports open to the network");

    public sealed record Inputs(SecurityReport? Report, FirewallState? Firewall, IReadOnlyList<string> UnwantedFeatures, IReadOnlyList<string> KnownAccounts,
        IReadOnlyList<string> AllowedPorts, int SignatureDays, int ScanDays);

    private static CheckButton OpenButton(string target) => new(Open, target, L.T("APRI", "OPEN"));

    /// <summary>Features are named like "MicrosoftWindowsPowerShellV2Root": the person's list may name them in part.</summary>
    public static bool Listed(string name, IReadOnlyList<string> list) =>
        list.Any(x => x.Trim() is { Length: > 0 } e && name.Equals(e, StringComparison.OrdinalIgnoreCase));

    private static bool PortAllowed(OpenPort p, IReadOnlyList<string> allowed) =>
        allowed.Any(a => a.Trim() is { Length: > 0 } e && (e == p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) || p.Process.Equals(e, StringComparison.OrdinalIgnoreCase)));

    public static IReadOnlyList<CheckItem> Board(Inputs x)
    {
        var board = new List<CheckItem>();
        var d = x.Report?.Defender;
        if (d is null)
            board.Add(new CheckItem(DefenderGroup, L.T("Stato", "State"), L.T("Non letto", "Not read"), CheckStatus.Info));
        else
        {
            board.Add(new CheckItem(DefenderGroup, L.T("Protezione in tempo reale", "Real-time protection"), On(d.RealTime), d.RealTime ? CheckStatus.Ok : CheckStatus.Wrong, On(true),
                d.Antivirus ? null : L.T("Defender non è l'antivirus attivo: se ne hai installato un altro è normale.", "Defender is not the active antivirus: normal if you installed another one."),
                Buttons: d.RealTime ? null : [OpenButton(DefenderPage)]));
            board.Add(new CheckItem(DefenderGroup, L.T("Definizioni dei virus", "Virus definitions"), Days(d.SignatureAge), d.SignatureAge > x.SignatureDays ? CheckStatus.Wrong : CheckStatus.Ok,
                L.T($"al massimo {x.SignatureDays} giorni", $"at most {x.SignatureDays} days"), L.T("Si aggiornano con Windows Update.", "They update with Windows Update.")));
            board.Add(new CheckItem(DefenderGroup, L.T("Ultima scansione completa", "Last full scan"), d.FullScanAge >= 100_000 ? L.T("Mai", "Never") : Days(d.FullScanAge),
                d.FullScanAge > x.ScanDays ? CheckStatus.Watch : CheckStatus.Ok, L.T($"al massimo {x.ScanDays} giorni", $"at most {x.ScanDays} days"),
                L.T("Si lancia da Sicurezza di Windows → Opzioni di analisi → Analisi completa, o si mette nel Pianificatore di OptLife.", "Run it from Windows Security → Scan options → Full scan, or put it in OptLife's Scheduler."),
                Buttons: d.FullScanAge > x.ScanDays ? [OpenButton(DefenderPage)] : null));
            board.Add(new CheckItem(DefenderGroup, L.T("Protezione antimanomissione", "Tamper protection"), On(d.Tamper), d.Tamper ? CheckStatus.Ok : CheckStatus.Watch, On(true),
                L.T("Impedisce ai programmi di spegnere Defender di nascosto.", "Stops programs from switching Defender off behind your back.")));
        }

        if (x.Firewall is { } fw)
        {
            foreach (var (label, on) in new[] { (L.T("Rete privata", "Private network"), fw.Private), (L.T("Rete pubblica", "Public network"), fw.Public), (L.T("Rete di dominio", "Domain network"), fw.Domain) })
                if (on is { } v)
                    board.Add(new CheckItem(FirewallGroup, label, On(v), v ? CheckStatus.Ok : CheckStatus.Wrong, On(true), Buttons: v ? null : [OpenButton(DefenderPage)]));
            var orphans = Orphans(fw).ToList();
            board.Add(new CheckItem(FirewallGroup, L.T("Regole di programmi che non ci sono più", "Rules for programs no longer there"),
                orphans.Count == 0 ? L.T("Nessuna", "None") : L.T($"{orphans.Count} accese", $"{orphans.Count} on"),
                orphans.Count == 0 ? CheckStatus.Ok : CheckStatus.Watch, L.T("nessuna", "none"),
                orphans.Count == 0 ? null : OrphansAdvice + " " + string.Join(", ", orphans.Select(r => r.Name).Distinct().Take(10)) + (orphans.Count > 10 ? "…" : ""),
                Buttons: orphans.Count == 0 ? null : [new CheckButton(DisableRules, null, L.T("SCEGLI E SPEGNI", "PICK AND TURN OFF"))]));
        }

        if (x.Report?.Accounts is { } accounts)
            foreach (var a in accounts.Where(a => a.Enabled || a.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase)))
            {
                var (status, why) = Judge(a, x.KnownAccounts);
                // The colour is the judgement, said in words too: a built-in account switched off is right, not "red because off".
                board.Add(new CheckItem(AccountsGroup, a.Name, AccountValue(a, status) + (AccountMeaning(a) is { } meaning ? "\n" + meaning : ""),
                    status, null, why, Buttons: status is CheckStatus.Wrong or CheckStatus.Watch ? [OpenButton(AccountsPage)] : null));
            }

        if (x.Report?.Features is { } features)
            foreach (var f in x.UnwantedFeatures.Where(f => f.Trim().Length > 0))
            {
                var on = features.Any(e => e.Equals(f.Trim(), StringComparison.OrdinalIgnoreCase));
                board.Add(new CheckItem(FeaturesGroup, f.Trim(), on ? L.T("Accesa — va spenta", "On — should be off") : L.T("Spenta — giusto così", "Off — as it should be"), on ? CheckStatus.Wrong : CheckStatus.Ok, L.T("Spenta", "Off"),
                    on ? FeatureAdvice(f.Trim()) : null, Buttons: on ? [new CheckButton(DisableFeature, f.Trim(), L.T("SPEGNI", "TURN OFF")), new CheckButton(Open, FeaturesPage, L.T("APRI", "OPEN"))] : null));
            }
        else if (x.UnwantedFeatures.Count > 0)
            board.Add(new CheckItem(FeaturesGroup, L.T("Funzioni", "Features"), L.T("Non lette: serve OptLife da amministratore", "Not read: needs OptLife running as administrator"), CheckStatus.Info));

        if (x.Report?.Ports is { } ports)
            foreach (var p in ports.OrderBy(p => p.Port))
            {
                var judged = x.AllowedPorts.Count > 0;
                var ok = !judged || PortAllowed(p, x.AllowedPorts);
                board.Add(new CheckItem(PortsGroup, $"{p.Port} · {p.Process}", L.T("In ascolto su tutte le reti", "Listening on every network"),
                    !judged ? CheckStatus.Info : ok ? CheckStatus.Ok : CheckStatus.Watch, null,
                    judged && !ok ? L.T("Non è tra le porte ammesse: se non sai cos'è, cerca il programma; il firewall decide comunque chi può entrare.", "Not among the allowed ports: if you do not know it, look up the program; the firewall still decides who may come in.") : null));
            }
        return board;
    }

    /// <summary>The account's state with the judgement in words: "Off — as it should be" for a built-in account left off.</summary>
    private static string AccountValue(LocalAccount a, CheckStatus status)
    {
        var builtIn = IsBuiltIn(a);
        if (!a.Enabled) return builtIn ? L.T("Spento — giusto così", "Off — as it should be") : L.T("Spento", "Off");
        if (builtIn) return L.T("Acceso — va spento", "On — should be off");
        return L.T("Acceso", "On") + (!a.PasswordRequired ? L.T(" · senza password", " · no password") : status == CheckStatus.Ok ? L.T(" · con password", " · with password") : "");
    }

    private static bool IsBuiltIn(LocalAccount a) =>
        a.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the two built-in accounts are, for whoever never heard of them.</summary>
    private static string? AccountMeaning(LocalAccount a) =>
        a.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
            ? L.T("L'amministratore nascosto che Windows crea da solo: non serve, si usa il proprio account. Spento non dà fastidio a niente.", "The hidden administrator Windows creates by itself: it is not needed, you use your own account. Off, it gets in the way of nothing.")
            : a.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase)
                ? L.T("L'account Ospite di Windows: lascia entrare chiunque senza password. Spento non dà fastidio a niente.", "Windows' Guest account: it lets anyone in without a password. Off, it gets in the way of nothing.")
                : null;

    private static string On(bool on) => on ? L.T("Acceso", "On") : L.T("Spento", "Off");
    private static string Days(long days) => days == 0 ? L.T("oggi", "today") : days == 1 ? L.T("1 giorno fa", "1 day ago") : L.T($"{days} giorni fa", $"{days} days ago");

    /// <summary>Rules that let a program in or out although the program is gone.</summary>
    public static IEnumerable<FirewallRule> Orphans(FirewallState fw) => fw.Rules.Where(r => r.Enabled && r.Missing);

    private static string OrphansAdvice => L.T(
        "Ogni programma installato lascia le sue regole nel firewall; disinstallato il programma, le regole restano. Spegnerle chiude porte che non servono a nessuno (si riaccendono da Windows Defender Firewall → Impostazioni avanzate).",
        "Every installed program leaves its rules in the firewall; when it is uninstalled, the rules stay. Turning them off closes doors nobody uses (they can be turned back on in Windows Defender Firewall → Advanced settings).");

    private static string FeatureAdvice(string feature) => feature.ToUpperInvariant() switch
    {
        var f when f.StartsWith("SMB1", StringComparison.Ordinal) => L.T("SMB 1 è il vecchio protocollo di condivisione file sfruttato da WannaCry: nessun dispositivo recente lo usa.", "SMB 1 is the old file-sharing protocol exploited by WannaCry: no recent device uses it."),
        var f when f.Contains("POWERSHELLV2", StringComparison.Ordinal) => L.T("PowerShell 2.0 non ha le protezioni delle versioni nuove ed è usato per aggirarle.", "PowerShell 2.0 lacks the protections of newer versions and is used to get around them."),
        var f when f.Contains("TELNET", StringComparison.Ordinal) || f.Contains("TFTP", StringComparison.Ordinal) => L.T("Strumento di rete vecchio e senza cifratura.", "Old network tool without encryption."),
        _ => L.T("Nell'elenco delle funzioni da tenere spente.", "In the list of features to keep off."),
    } + " " + L.T("Spegnerla chiede un riavvio.", "Turning it off asks for a restart.");

    private static (CheckStatus, string?) Judge(LocalAccount a, IReadOnlyList<string> known)
    {
        if (a.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase) && a.Enabled)
            return (CheckStatus.Wrong, L.T("L'account Ospite permette di entrare senza password.", "The Guest account lets anyone in without a password."));
        if (a.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) && a.Enabled)
            return (CheckStatus.Wrong, L.T("L'Administrator integrato è il primo che si prova a forzare: va lasciato disattivato.", "The built-in Administrator is the first one attacked: leave it disabled."));
        if (!a.Enabled) return (CheckStatus.Ok, null);
        if (!a.PasswordRequired)
            return (CheckStatus.Wrong, L.T("Account attivo senza password obbligatoria: chiunque al PC può entrare.", "Active account with no password required: anyone at the PC can get in."));
        if (known.Count > 0 && !known.Any(k => k.Trim().Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
            return (CheckStatus.Watch, L.T("Non è tra gli account conosciuti: se non sai chi l'ha creato, controlla in Impostazioni → Account.", "Not among the known accounts: if you do not know who made it, check in Settings → Accounts."));
        return (known.Count > 0 ? CheckStatus.Ok : CheckStatus.Info, null);
    }

    public static IEnumerable<NewFinding> Findings(Inputs x)
    {
        if (x.Report?.Defender is { } d)
        {
            if (!d.RealTime && d.Antivirus)
                yield return new NewFinding("defender_realtime", Severity.High, L.T("La protezione in tempo reale di Defender è spenta", "Defender real-time protection is off"),
                    L.T("Nessun antivirus controlla i file mentre vengono aperti.", "No antivirus checks files as they are opened."),
                    L.T("Riaccendila in Sicurezza di Windows → Protezione da virus e minacce.", "Turn it back on in Windows Security → Virus & threat protection."),
                    new FindingFix(Open, L.T("Apri Sicurezza di Windows", "Open Windows Security"), DefenderPage), FindingArea.Setup);
            if (d.SignatureAge > x.SignatureDays)
                yield return new NewFinding("defender_signatures", Severity.Medium, L.T($"Le definizioni dei virus hanno {d.SignatureAge} giorni", $"Virus definitions are {d.SignatureAge} days old"),
                    L.T("Defender riconosce solo le minacce che conosce.", "Defender only recognises the threats it knows."),
                    L.T("Controlla che Windows Update funzioni, o aggiornale da Sicurezza di Windows.", "Check that Windows Update works, or update them from Windows Security."),
                    new FindingFix(Open, L.T("Apri Sicurezza di Windows", "Open Windows Security"), DefenderPage), FindingArea.Setup);
            if (d.FullScanAge > x.ScanDays)
                yield return new NewFinding("defender_fullscan", Severity.Low,
                    d.FullScanAge >= 100_000 ? L.T("Defender non ha mai fatto una scansione completa", "Defender has never run a full scan")
                                             : L.T($"L'ultima scansione completa è di {d.FullScanAge} giorni fa", $"The last full scan was {d.FullScanAge} days ago"),
                    L.T("La scansione veloce di ogni giorno non guarda tutti i file.", "The daily quick scan does not look at every file."),
                    L.T("Lanciala da Sicurezza di Windows → Opzioni di analisi → Analisi completa, o mettila nel Pianificatore di OptLife.", "Run it from Windows Security → Scan options → Full scan, or put it in OptLife's Scheduler."),
                    new FindingFix(Open, L.T("Apri Sicurezza di Windows", "Open Windows Security"), DefenderPage), FindingArea.Setup);
            if (!d.Tamper && d.Antivirus)
                yield return new NewFinding("defender_tamper", Severity.Low, L.T("La protezione antimanomissione di Defender è spenta", "Defender tamper protection is off"),
                    L.T("Un programma può spegnere Defender senza che tu lo sappia.", "A program can switch Defender off without you knowing."),
                    L.T("Riaccendila in Sicurezza di Windows → Protezione da virus e minacce → Gestisci impostazioni.", "Turn it back on in Windows Security → Virus & threat protection → Manage settings."),
                    new FindingFix(Open, L.T("Apri Sicurezza di Windows", "Open Windows Security"), DefenderPage), FindingArea.Setup);
        }
        if (x.Firewall is { } fw)
        {
            foreach (var (key, label, on) in new[] { ("private", L.T("privata", "private"), fw.Private), ("public", L.T("pubblica", "public"), fw.Public) })
                if (on is false)
                    yield return new NewFinding($"firewall|{key}", Severity.High, L.T($"Il firewall è spento sulla rete {label}", $"The firewall is off on the {label} network"),
                        L.T("Qualunque programma in ascolto è raggiungibile dalla rete.", "Any listening program can be reached from the network."),
                        L.T("Riaccendilo in Sicurezza di Windows → Firewall e protezione della rete.", "Turn it back on in Windows Security → Firewall & network protection."),
                        new FindingFix(Open, L.T("Apri Sicurezza di Windows", "Open Windows Security"), DefenderPage), FindingArea.Setup);
            var orphans = Orphans(fw).ToList();
            if (orphans.Count > 0)
                yield return new NewFinding("firewall|orphans", Severity.Low, L.T($"{orphans.Count} regole del firewall per programmi che non ci sono più", $"{orphans.Count} firewall rules for programs no longer there"),
                    string.Join(", ", orphans.Select(r => r.Name).Distinct().Take(12)) + (orphans.Count > 12 ? "…" : "") + ".",
                    OrphansAdvice, new FindingFix(DisableRules, L.T("Spegnile tutte", "Turn them all off"), string.Join(';', orphans.Select(r => r.Id))), FindingArea.Setup);
        }
        if (x.Report?.Accounts is { } accounts)
            foreach (var a in accounts)
                if (Judge(a, x.KnownAccounts) is var (status, why) && status is CheckStatus.Wrong or CheckStatus.Watch && why is not null)
                    yield return new NewFinding($"account|{a.Name.ToLowerInvariant()}", status == CheckStatus.Wrong ? Severity.Medium : Severity.Low,
                        L.T($"Account da controllare: {a.Name}", $"Account to check: {a.Name}"), why,
                        L.T("Impostazioni → Account → Altri utenti.", "Settings → Accounts → Other users."),
                        new FindingFix(Open, L.T("Apri gli account", "Open accounts"), AccountsPage), FindingArea.Setup);
        if (x.Report?.Features is { } features)
            foreach (var f in x.UnwantedFeatures.Select(f => f.Trim()).Where(f => f.Length > 0 && features.Any(e => e.Equals(f, StringComparison.OrdinalIgnoreCase))))
                yield return new NewFinding($"feature|{f.ToLowerInvariant()}", Severity.Medium, L.T($"Funzione di Windows accesa: {f}", $"Windows feature on: {f}"),
                    L.T("È nell'elenco delle funzioni da tenere spente.", "It is in the list of features to keep off."), FeatureAdvice(f),
                    new FindingFix(DisableFeature, L.T("Spegnila", "Turn it off"), f), FindingArea.Setup);
        if (x.Report?.Ports is { } ports && x.AllowedPorts.Count > 0)
            foreach (var p in ports.Where(p => !PortAllowed(p, x.AllowedPorts)))
                yield return new NewFinding($"port|{p.Port}|{p.Process.ToLowerInvariant()}", Severity.Low, L.T($"Porta {p.Port} aperta da {p.Process}", $"Port {p.Port} opened by {p.Process}"),
                    L.T("Un programma ascolta su tutte le reti e non è tra le porte ammesse.", "A program listens on every network and is not among the allowed ports."),
                    L.T("Se è un programma che usi (giochi, NAS, sincronizzazione), aggiungilo alle porte ammesse; se no, cerca cos'è e disinstallalo.", "If it is a program you use (games, NAS, sync), add it to the allowed ports; if not, look it up and uninstall it."),
                    Area: FindingArea.Setup);
    }

    /// <summary>Only what this module opens: Windows Security, Windows' settings pages, the optional features window.</summary>
    public static bool SafeTarget(string? t) => t is not null && (t == FeaturesPage || t.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase)
        || t.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) && !t.Contains('"');
}
