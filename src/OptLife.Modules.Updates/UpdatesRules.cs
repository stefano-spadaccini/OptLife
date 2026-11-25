using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OptLife.Modules.Updates;

/// <summary>A program winget can update: what is installed and what is available.</summary>
public sealed record AppUpdate(string Name, string Id, string Installed, string Available)
{
    /// <summary>winget says "Unknown" (or "&lt; 1.2") when it cannot tell the installed version: updating may reinstall it.</summary>
    public bool KnownVersion => Installed.Length > 0 && (char.IsDigit(Installed[0]) || Installed[0] is 'v' or 'V' && Installed.Length > 1 && char.IsDigit(Installed[1]));

    /// <summary>A narrow table cuts long ids with "…": such an id cannot be passed back to winget.</summary>
    public bool WholeId => !Id.Contains('…') && !Id.Contains(' ');
}

/// <summary>Something Windows Update has ready and not installed: a driver, an optional update, a normal one.</summary>
public sealed record WindowsUpdateItem(string Title, bool Driver, bool Optional);

/// <summary>A program in the uninstall lists, with its version and when it was installed.</summary>
public sealed record InstalledProgram(string Name, string Version, DateTimeOffset? InstalledOn);

/// <summary>The graphics driver as Windows has it: maker, version in Windows' numbering, date.</summary>
public sealed record GraphicsDriver(string Provider, string Card, string Version, DateTimeOffset? Date);

/// <param name="Processor">The processor's name as Windows gives it ("AMD Ryzen 7 5800X 6-Core Processor"), for the chipset driver page.</param>
public sealed record BiosInfo(string BoardMaker, string Board, string Version, DateTimeOffset? Date, string? Processor = null);

/// <summary>A driver package to watch through the uninstall lists ("AMD chipset | AMD Chipset Software | site | 365").</summary>
public sealed record DriverRule(string Name, string Installed, string? Site, int Days)
{
    public static DriverRule? Parse(string line)
    {
        var p = line.Split('|', StringSplitOptions.TrimEntries);
        if (p.Length < 2 || p[0].Length == 0 || p[1].Length == 0) return null;
        var days = p.Length > 3 && int.TryParse(p[3], out var d) && d > 0 ? d : 365;
        return new DriverRule(p[0], p[1], p.Length > 2 && p[2].Length > 0 ? p[2] : null, days);
    }
}

/// <summary>The rules of the updates page, without the machine.</summary>
public static class UpdatesRules
{
    public const string Upgrade = "upgrade", UpgradeOne = "upgrade-one", OpenSite = "open-site", RestartBios = "restart-bios";
    public const string WindowsUpdatePage = "ms-settings:windowsupdate", OptionalPage = "ms-settings:windowsupdate-optionalupdates";

    // ── winget ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the table "winget upgrade" prints (Name, Id, Version, Available[, Source]), in any language: the columns
    /// start where the header words start, and the table ends at the first empty line (what follows is pinned packages).
    /// </summary>
    public static IReadOnlyList<AppUpdate> ParseWinget(string output)
    {
        // The progress spinner rewrites the same line with \r: what counts is what is left after the last one.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Split('\r').Last().TrimEnd()).ToList();
        var dash = lines.FindIndex(l => l.Length > 20 && l.All(c => c == '-'));
        if (dash < 1) return [];
        var header = lines[dash - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        if (starts.Count < 4) return [];

        var list = new List<AppUpdate>();
        foreach (var line in lines.Skip(dash + 1))
        {
            if (line.Trim().Length == 0) break;
            if (line.Length <= starts[3]) continue; // "29 upgrades available." and similar.
            string Cell(int c) => (c + 1 < starts.Count ? Slice(line, starts[c], starts[c + 1]) : Slice(line, starts[c], line.Length)).Trim();
            var app = new AppUpdate(Cell(0), Cell(1), Cell(2), Cell(3));
            if (app.Id.Length == 0 || app.Available.Length == 0 || !char.IsLetterOrDigit(app.Available[0])) continue;
            list.Add(app);
        }
        return list;
    }

    private static string Slice(string s, int from, int to) => from >= s.Length ? "" : s[from..Math.Min(to, s.Length)];

    /// <summary>The person's "do not touch" list: a winget id, or part of the name.</summary>
    public static bool Excluded(AppUpdate app, IReadOnlyList<string> exclude) =>
        exclude.Any(x => x.Trim() is { Length: > 0 } e && (app.Id.Equals(e, StringComparison.OrdinalIgnoreCase) || app.Name.Contains(e, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What may be updated without asking which: known version, whole id, not excluded, not one that updates itself.</summary>
    public static IEnumerable<AppUpdate> Updatable(IEnumerable<AppUpdate> apps, IReadOnlyList<string> exclude, IReadOnlyList<string>? selfUpdating = null, IReadOnlySet<string>? stubborn = null,
        IReadOnlyDictionary<string, string>? needsOthers = null, IReadOnlyList<string>? managedEmulators = null) =>
        apps.Where(a => a.KnownVersion && a.WholeId && !Excluded(a, exclude) && !SelfUpdating(a, selfUpdating ?? [], stubborn) && needsOthers?.ContainsKey(a.Id) != true
                        && !ManagedElsewhere(a, managedEmulators));

    /// <summary>
    /// Programs winget keeps offering but should not update: an unknown installed version (it may reinstall from scratch), a
    /// launcher or tool with its own updater (G HUB stays on its version whatever winget says), an editor installed side by
    /// side (Unity), or one that "updated" fine and is still listed afterwards (<paramref name="stubborn"/>).
    /// </summary>
    public static bool SelfUpdating(AppUpdate a, IReadOnlyList<string> selfUpdating, IReadOnlySet<string>? stubborn = null) =>
        !a.KnownVersion || stubborn?.Contains(a.Id) == true
        || selfUpdating.Any(s => s.Trim() is { Length: > 0 } e && (a.Id.StartsWith(e, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(e, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Defaults valid for anyone: what has its own updater, or installs a new version beside the old one.</summary>
    public static string[] SelfUpdatingDefaults =>
        ["Logitech.GHUB", "Unity.Unity.", "Blizzard.BattleNet", "RiotGames.", "Roblox.", "Valve.Steam", "Discord.Discord", "Microsoft.Edge", "Google.Chrome", "Mozilla.Firefox"];

    public enum Outcome { Done, Failed, Attention, Reboot }

    /// <summary>How one program's update went, in words.</summary>
    /// <param name="Technical">The technical detail (winget's code, the versions), shown below the plain words.</param>
    public sealed record RunResult(string Id, string Name, Outcome Outcome, string Why, string? Technical = null);

    /// <summary>winget's answer (or the wait running out) in words, with what to do.</summary>
    public static (Outcome Outcome, string Why) Explain(int? code, bool timedOut, int minutes)
    {
        if (timedOut)
            return (Outcome.Attention, L.T($"Dopo {minutes} minuti non aveva finito: di solito l'installatore ha aperto una finestra e aspetta una risposta. Aggiornalo a mano dal programma.",
                $"After {minutes} minutes it had not finished: usually the installer opened a window and is waiting for an answer. Update it by hand from the program."));
        return code switch
        {
            0 or null => (Outcome.Done, L.T("Aggiornato.", "Updated.")),
            unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) or unchecked((int)0x8A15010D) => (Outcome.Done, L.T("Era già all'ultima versione.", "It was already up to date.")),
            unchecked((int)0x8A150109) or 3010 or 1641 => (Outcome.Reboot, L.T("Aggiornato: finisce al prossimo riavvio.", "Updated: it finishes at the next restart.")),
            unchecked((int)0x8A15010A) => (Outcome.Attention, L.T("Serve riavviare il PC prima di poterlo aggiornare: riavvia e riprova.", "The PC must restart before it can be updated: restart and try again.")),
            unchecked((int)0x8A150101) or unchecked((int)0x8A150103) => (Outcome.Attention, L.T("Il programma era aperto: chiudilo (anche dall'icona vicino all'orologio) e riprova.", "The program was open: close it (also from the icon by the clock) and try again.")),
            unchecked((int)0x8A150102) => (Outcome.Attention, L.T("C'era già un'altra installazione in corso: riprova tra poco.", "Another installation was already running: try again shortly.")),
            unchecked((int)0x8A150104) => (Outcome.Failed, L.T("Manca un componente che il programma richiede: aggiornalo dal suo sito.", "A component the program needs is missing: update it from its site.")),
            unchecked((int)0x8A150105) => (Outcome.Failed, L.T("Disco pieno: libera spazio e riprova.", "Disk full: free some space and try again.")),
            unchecked((int)0x8A150107) or unchecked((int)0x80072EE7) => (Outcome.Failed, L.T("Il sito di download non risponde: riprova più tardi.", "The download site does not answer: try again later.")),
            unchecked((int)0x8A15010C) => (Outcome.Attention, L.T("L'installazione è stata annullata (da qualcuno o da una finestra dell'installatore).", "The installation was cancelled (by someone or by an installer window).")),
            unchecked((int)0x8A15010E) => (Outcome.Done, L.T("La versione installata è già più nuova.", "The installed version is already newer.")),
            unchecked((int)0x8A15010F) => (Outcome.Failed, L.T("Bloccato da un criterio di Windows.", "Blocked by a Windows policy.")),
            unchecked((int)0x8A150011) => (Outcome.Failed, L.T("Il produttore ha cambiato il file e winget, per sicurezza, non l'ha installato: riprova tra qualche giorno o aggiornalo dal programma.", "The maker changed the file and winget, for safety, did not install it: try again in a few days or update it from the program.")),
            unchecked((int)0x8A150006) => (Outcome.Failed, L.T("L'installatore del programma si è fermato con un errore (a volte dopo aver mostrato una finestra): aggiornalo dal programma o dal suo sito.", "The program's installer stopped with an error (sometimes after showing a window): update it from the program or its site.")),
            var c => (Outcome.Failed, L.T($"winget non ci è riuscito (codice {c:X8}): aggiornalo dal programma o dal suo sito.", $"winget did not manage (code {c:X8}): update it from the program or its site.")),
        };
    }

    /// <summary>winget said it was fine, but the program is still on the old version: not updated, said plainly.</summary>
    public static RunResult NotChanged(RunResult r, AppUpdate still) => r with
    {
        Outcome = Outcome.Failed,
        Why = L.T($"Non si è aggiornato: è ancora alla versione {still.Installed}. Aggiornalo dal programma stesso (o dal suo sito); se succede di nuovo OptLife smette di riproportelo.",
            $"It did not update: it is still on version {still.Installed}. Update it from the program itself (or its site); if it happens again OptLife stops offering it."),
        Technical = (r.Technical ?? "") + L.T(" · winget ha risposto che era fatto, ma l'elenco dopo mostra la stessa versione", " · winget answered it was done, but the list afterwards shows the same version"),
    };

    /// <summary>"Updated: 21 · Not done: 2 · To check: 1", then each one that did not go well, with why.</summary>
    public static string Summary(IReadOnlyList<RunResult> results)
    {
        int Count(params Outcome[] o) => results.Count(r => o.Contains(r.Outcome));
        var head = L.T($"Riusciti: {Count(Outcome.Done, Outcome.Reboot)} · Non riusciti: {Count(Outcome.Failed)} · Da controllare: {Count(Outcome.Attention)}.",
            $"Updated: {Count(Outcome.Done, Outcome.Reboot)} · Not done: {Count(Outcome.Failed)} · To check: {Count(Outcome.Attention)}.");
        var bad = results.Where(r => r.Outcome is Outcome.Failed or Outcome.Attention).Select(r => $"{r.Name}: {r.Why}");
        return string.Join(' ', new[] { head }.Concat(bad));
    }

    // ── Drivers ──────────────────────────────────────────────────────────────

    /// <summary>NVIDIA's own number from Windows' one: 32.0.16.2124 → 621.24 (the last five digits).</summary>
    public static string? NvidiaVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return null;
        var last = digits[^5..];
        return $"{int.Parse(last[..3], CultureInfo.InvariantCulture)}.{last[3..]}";
    }

    /// <summary>The official driver page of a graphics card maker.</summary>
    public static string? GraphicsSite(string provider) => provider.ToUpperInvariant() switch
    {
        var p when p.Contains("NVIDIA") => "https://www.nvidia.com/en-us/drivers/",
        var p when p.Contains("AMD") || p.Contains("ADVANCED MICRO") => "https://www.amd.com/en/support/download/drivers.html",
        var p when p.Contains("INTEL") => "https://www.intel.com/content/www/us/en/download-center/home.html",
        _ => null,
    };

    /// <summary>The support page of a motherboard maker, where its BIOS versions are listed.</summary>
    /// <summary>
    /// The chipset driver page for this very PC, when the setting names the maker's general page: AMD has one per chipset
    /// (read from the board's model: "ROG STRIX X570-F GAMING" → X570 on AM4), and one per processor for laptops, which have
    /// no desktop chipset; Intel's own detection tool finds its chipset driver. Otherwise the page the setting names.
    /// </summary>
    public static string? DriverSite(string? site, BiosInfo? pc)
    {
        if (site is null || pc is null) return site;
        if (site.Contains("intel.com", StringComparison.OrdinalIgnoreCase) && !site.Contains("/support/detect", StringComparison.OrdinalIgnoreCase))
            return IntelDetect;
        if (!site.Contains("amd.com", StringComparison.OrdinalIgnoreCase) || site.Contains("/chipsets/", StringComparison.OrdinalIgnoreCase)) return site;
        return AmdChipsetPage(pc.Board) ?? AmdProcessorPage(pc.Processor) ?? site;
    }

    public const string IntelDetect = "https://www.intel.com/content/www/us/en/support/detect.html";

    private const string AmdDrivers = "https://www.amd.com/en/support/downloads/drivers.html";

    /// <summary>AMD's page of the chipset in a board's model; null when the model names none.</summary>
    public static string? AmdChipsetPage(string board)
    {
        var m = System.Text.RegularExpressions.Regex.Match(board.ToUpperInvariant(),
            @"(?<![A-Z0-9])(X870E|X870|B850|B840|X670E|X670|B650E|B650|A620|X570|B550|A520|X470|B450|X370|B350|A320|TRX50|TRX40|WRX90|WRX80)(?![0-9])");
        if (!m.Success) return null;
        var chipset = m.Value;
        var socket = chipset switch
        {
            "TRX50" or "WRX90" => "str5",
            "TRX40" or "WRX80" => "strx4",
            _ when chipset[1] >= '6' => "am5",
            _ => "am4",
        };
        return $"{AmdDrivers}/chipsets/{socket}/{chipset.ToLowerInvariant()}.html";
    }

    /// <summary>AMD's page of a Ryzen processor ("AMD Ryzen 7 7840HS" → ryzen-7000-series/amd-ryzen-7-7840hs); null for other names.</summary>
    public static string? AmdProcessorPage(string? processor)
    {
        if (processor is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(processor, @"Ryzen\s+(\d)\s+((\d)\d{3}[A-Z0-9]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success
            ? $"{AmdDrivers}/processors/ryzen/ryzen-{m.Groups[3].Value}000-series/amd-ryzen-{m.Groups[1].Value}-{m.Groups[2].Value.ToLowerInvariant()}.html"
            : null;
    }

    public static string? BoardSite(string maker) => maker.ToUpperInvariant() switch
    {
        var m when m.Contains("ASUS") => "https://www.asus.com/support/download-center/",
        var m when m.Contains("MICRO-STAR") || m.StartsWith("MSI", StringComparison.Ordinal) => "https://www.msi.com/support/download",
        var m when m.Contains("GIGABYTE") => "https://www.gigabyte.com/Support",
        var m when m.Contains("ASROCK") => "https://www.asrock.com/support/index.asp",
        _ => null,
    };

    /// <summary>
    /// The BIOS page of this very board, built from the maker and model the BIOS itself reports: ASUS (ROG pages for ROG boards,
    /// the support page by model for the others), MSI and ASRock by model; Gigabyte's pages carry a revision that cannot be read, so its
    /// search with the model; any other maker, a web search for "maker model BIOS". Never a download link.
    /// </summary>
    public static string? BiosPage(string maker, string board)
    {
        var model = System.Text.RegularExpressions.Regex.Replace(board, @"\s*\([^)]*\)", "").Trim();
        if (model.Length == 0) return BoardSite(maker);
        static string Url(string s) => Uri.EscapeDataString(s);
        static string Slug(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var m = maker.ToUpperInvariant();
        if (m.Contains("ASUS"))
        {
            var words = model.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length >= 3 && words[0].Equals("ROG", StringComparison.OrdinalIgnoreCase)
                ? $"https://rog.asus.com/motherboards/{Slug(words[0] + " " + words[1])}/{Slug(model)}-model/helpdesk_bios/"
                : $"https://www.asus.com/supportonly/{Url(model)}/helpdesk_bios/";
        }
        if (m.Contains("MICRO-STAR") || m.StartsWith("MSI", StringComparison.Ordinal))
            return $"https://www.msi.com/Motherboard/{System.Text.RegularExpressions.Regex.Replace(model, "\\s+", "-")}/support#bios";
        if (m.Contains("GIGABYTE")) return $"https://www.gigabyte.com/Search?kw={Url(model)}";
        if (m.Contains("ASROCK"))
        {
            var amd = System.Text.RegularExpressions.Regex.IsMatch(model, @"\b[ABX](3|4|5|6|8)\d0", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !model.Contains('Z');
            return $"https://www.asrock.com/mb/{(amd ? "AMD" : "Intel")}/{Url(model)}/index.asp#BIOS";
        }
        return $"https://duckduckgo.com/?q={Url($"{maker} {model} BIOS")}";
    }

    public static int AgeDays(DateTimeOffset? date, DateTimeOffset now) => date is { } d ? (int)Math.Floor((now - d).TotalDays) : 0;

    private static string Date(DateTimeOffset? d) => d is { } x ? x.ToString("d MMM yyyy", L.Culture) : "";

    // ── The page ─────────────────────────────────────────────────────────────

    public static string AppsGroup => L.T("Programmi (winget)", "Programs (winget)");
    public static string EmulatorsGroup => L.T("Emulatori", "Emulators");
    public static string DriversGroup => L.T("Driver e BIOS", "Drivers and BIOS");
    public static string WindowsGroup => L.T("Windows Update", "Windows Update");

    public sealed record Inputs(
        IReadOnlyList<AppUpdate>? Apps,
        IReadOnlyList<string> Exclude,
        IReadOnlyList<CheckItem> Emulators,
        GraphicsDriver? Graphics,
        int GraphicsDays,
        IReadOnlyList<(DriverRule Rule, InstalledProgram Program)> Drivers,
        BiosInfo? Bios,
        int BiosDays,
        IReadOnlyList<WindowsUpdateItem>? Windows,
        bool PreviewOn,
        DateTimeOffset Now,
        IReadOnlyList<string>? SelfUpdatingList = null,
        IReadOnlySet<string>? Stubborn = null,
        IReadOnlyList<RunResult>? LastRun = null,
        DateTimeOffset? LastRunAt = null,
        IReadOnlyDictionary<string, string>? NeedsOthers = null,
        IReadOnlyList<string>? ManagedEmulators = null);

    /// <summary>
    /// An emulator the Emulation page installs and updates itself (it keeps it in its own folder): winget must not update it too.
    /// On 23 Nov 2026 winget reinstalled RetroArch in C:\RetroArch-Win64, away from the emulators folder.
    /// </summary>
    public static bool ManagedElsewhere(AppUpdate a, IReadOnlyList<string>? emulators) =>
        emulators?.Any(e => Norm(e) is { Length: >= 4 } n && (Norm(a.Name).StartsWith(n, StringComparison.Ordinal) || Norm(a.Id).Contains(n, StringComparison.Ordinal))) == true;

    private static string Norm(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// The folder a program was installed in, when it is not where installers put things by default (Program Files, the person's
    /// AppData, ProgramData): winget must be told to update it there (--location), or the installer may put it back in its own default
    /// folder. Null when it is in a usual place, or unknown.
    /// </summary>
    public static string? CustomLocation(string? installLocation, IReadOnlyList<string> usualFolders)
    {
        if (installLocation is not { Length: > 3 } raw) return null;
        var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).TrimEnd('\\');
        if (!Path.IsPathRooted(path)) return null;
        return usualFolders.Any(u => u.Length > 0 && (path + "\\").StartsWith(u.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ? null : path;
    }

    /// <summary>After the update the program's folder is another one: winget put it elsewhere (and the old folder may still be there).</summary>
    public static RunResult Moved(RunResult r, string before, string after) => r with
    {
        Outcome = Outcome.Attention,
        Why = L.T($"winget l'ha reinstallato in {after} invece che nella sua cartella {before}. Controlla quale dei due usi; OptLife non lo aggiornerà più con winget (si aggiorna dal programma o a mano).",
            $"winget reinstalled it in {after} instead of its folder {before}. Check which of the two you use; OptLife will not update it with winget any more (update it from the program or by hand)."),
        Technical = (r.Technical ?? "") + $" · InstallLocation {before} → {after}",
    };

    public static string SelfGroup => L.T("Si aggiornano dal loro programma", "They update from their own program");
    public static string ExcludedGroup => L.T("Da non aggiornare", "Not to update");
    public static string LastRunGroup => L.T("Ultimo aggiornamento", "Last update run");

    private static CheckButton Site(string url) => new(OpenSite, url, L.T("SITO", "SITE"));

    public static IReadOnlyList<CheckItem> Board(Inputs x)
    {
        var board = new List<CheckItem>();

        // Windows first: the preview switch is the one that breaks working programs.
        board.Add(new CheckItem(WindowsGroup, L.T("Aggiornamenti di anteprima", "Preview updates"),
            x.PreviewOn ? L.T("Accesi — vanno spenti", "On — should be off") : L.T("Spenti — giusto così", "Off — as it should be"), x.PreviewOn ? CheckStatus.Wrong : CheckStatus.Ok, L.T("Spenti", "Off"),
            PreviewAdvice, Buttons: x.PreviewOn ? [new CheckButton(OpenSite, WindowsUpdatePage, L.T("APRI", "OPEN"))] : null));
        if (x.Windows is null)
            board.Add(new CheckItem(WindowsGroup, L.T("In attesa", "Pending"), L.T("Non ancora letti", "Not read yet"), CheckStatus.Info));
        else if (x.Windows.Count == 0)
            board.Add(new CheckItem(WindowsGroup, L.T("In attesa", "Pending"), L.T("Niente da installare", "Nothing to install"), CheckStatus.Ok));
        foreach (var u in x.Windows ?? [])
            board.Add(new CheckItem(WindowsGroup, u.Title,
                (u.Driver ? L.T("Driver", "Driver") : L.T("Aggiornamento", "Update")) + (u.Optional ? L.T(" facoltativo", " (optional)") : ""),
                u.Optional ? CheckStatus.Info : CheckStatus.Watch, null,
                L.T("Si installa da Impostazioni → Windows Update", "Installed from Settings → Windows Update") + (u.Optional ? L.T(" → Opzioni avanzate → Aggiornamenti facoltativi.", " → Advanced options → Optional updates.") : "."),
                Buttons: [new CheckButton(OpenSite, u.Optional ? OptionalPage : WindowsUpdatePage, L.T("APRI", "OPEN"))]));

        if (x.Graphics is { } g)
        {
            var nvidia = g.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? NvidiaVersion(g.Version) : null;
            var age = AgeDays(g.Date, x.Now);
            var old = g.Date is not null && age > x.GraphicsDays;
            board.Add(new CheckItem(DriversGroup, L.T($"Driver video · {g.Card}", $"Graphics driver · {g.Card}"),
                $"{nvidia ?? g.Version}" + (g.Date is null ? "" : L.T($" del {Date(g.Date)}", $" from {Date(g.Date)}")),
                old ? CheckStatus.Watch : CheckStatus.Ok, null,
                old ? L.T($"Ha più di {x.GraphicsDays} giorni: guarda sul sito se ce n'è uno nuovo. OptLife non installa driver.", $"It is over {x.GraphicsDays} days old: check the site for a newer one. OptLife does not install drivers.")
                    : L.T("L'ultima versione si controlla sul sito del produttore (o nella sua app). OptLife non installa driver.", "The latest version is on the maker's site (or in its app). OptLife does not install drivers."),
                Buttons: GraphicsSite(g.Provider) is { } site ? [Site(site)] : null));
        }
        foreach (var (rule, program) in x.Drivers)
        {
            var old = program.InstalledOn is not null && AgeDays(program.InstalledOn, x.Now) > rule.Days;
            board.Add(new CheckItem(DriversGroup, rule.Name,
                program.Version + (program.InstalledOn is null ? "" : L.T($", installato il {Date(program.InstalledOn)}", $", installed {Date(program.InstalledOn)}")),
                old ? CheckStatus.Watch : CheckStatus.Ok, null,
                old ? L.T($"Installato più di {rule.Days} giorni fa: guarda sul sito se c'è una versione nuova.", $"Installed over {rule.Days} days ago: check the site for a newer version.")
                    : L.T("L'ultima versione si controlla sul sito del produttore.", "The latest version is on the maker's site."),
                Buttons: DriverSite(rule.Site, x.Bios) is { } site ? [Site(site)] : null));
        }
        if (x.Bios is { } b)
        {
            var old = b.Date is not null && AgeDays(b.Date, x.Now) > x.BiosDays;
            board.Add(new CheckItem(DriversGroup, $"BIOS · {b.Board}", $"{b.Version}" + (b.Date is null ? "" : L.T($" del {Date(b.Date)}", $" from {Date(b.Date)}")),
                old ? CheckStatus.Watch : CheckStatus.Ok, null,
                L.T($"Il pulsante apre la pagina BIOS della tua scheda ({b.BoardMaker} {b.Board}): confronta l'ultima versione con la {b.Version}. Aggiorna solo se la nuova risolve qualcosa che ti serve (sicurezza, compatibilità). Come farlo in sicurezza: scarica il file sulla pagina, copialo su una chiavetta FAT32, entra nel BIOS e usa lo strumento della scheda (ASUS EZ Flash, MSI M-Flash, Gigabyte Q-Flash, ASRock Instant Flash); non spegnere durante l'aggiornamento; dopo, rimetti il profilo della RAM (XMP/EXPO/DOCP) e le tue impostazioni. OptLife non scarica né installa mai il BIOS.",
                    $"The button opens your board's BIOS page ({b.BoardMaker} {b.Board}): compare the latest version with {b.Version}. Update only if the new one fixes something you need (security, compatibility). How to do it safely: download the file from the page, copy it to a FAT32 USB stick, enter the BIOS and use the board's tool (ASUS EZ Flash, MSI M-Flash, Gigabyte Q-Flash, ASRock Instant Flash); do not switch off during the update; afterwards set the RAM profile (XMP/EXPO/DOCP) and your settings again. OptLife never downloads or installs a BIOS."),
                Buttons: [.. BiosPage(b.BoardMaker, b.Board) is { } site ? [Site(site)] : Array.Empty<CheckButton>(),
                    .. Firmware.IsUefi() == true ? [new CheckButton(RestartBios, null, L.T("RIAVVIA NEL BIOS", "RESTART INTO BIOS"))] : Array.Empty<CheckButton>()]));
        }

        // Before the programs: their list is long, and these few would end up at the very bottom.
        foreach (var e in x.Emulators)
            board.Add(new CheckItem(EmulatorsGroup, $"{e.Label} · {e.Group}", e.Value, CheckStatus.Watch, null,
                L.T("Si aggiorna dalla pagina GIOCHI → Emulazione.", "Updated from the GAMES → Emulation page.")));

        if (x.Apps is null)
            board.Add(new CheckItem(AppsGroup, "winget", L.T("Non ancora letto (o winget non risponde)", "Not read yet (or winget does not answer)"), CheckStatus.Info));
        else if (x.Apps.Count == 0)
            board.Add(new CheckItem(AppsGroup, L.T("Tutti", "All"), L.T("Aggiornati", "Up to date"), CheckStatus.Ok));
        foreach (var a in (x.Apps ?? []).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var excluded = Excluded(a, x.Exclude);
            var managed = !excluded && ManagedElsewhere(a, x.ManagedEmulators);
            var self = !excluded && (managed || SelfUpdating(a, x.SelfUpdatingList ?? [], x.Stubborn));
            var value = L.T($"{a.Available} disponibile (hai {a.Installed})", $"{a.Available} available (you have {a.Installed})");
            string? advice = excluded ? L.T("Nell'elenco da non aggiornare: OptLife non lo aggiorna da solo e non lo segnala.", "In the do-not-update list: OptLife neither updates nor reports it.")
                : managed ? L.T("È un emulatore che si aggiorna dalla pagina GIOCHI → Emulazione, nella sua cartella: winget non lo tocca, così non si aggiorna due volte né finisce altrove.",
                    "It is an emulator updated from the GAMES → Emulation page, in its own folder: winget leaves it, so it is not updated twice nor put elsewhere.")
                : !a.KnownVersion ? L.T("winget non sa che versione hai e lo riproporrebbe ogni volta: si aggiorna dal programma stesso.", "winget does not know which version you have and would offer it every time: it updates from the program itself.")
                : x.NeedsOthers?.TryGetValue(a.Id, out var others) == true ? L.T($"Attenzione: aggiornandolo, winget installa anche {others}, che non hai. OptLife non lo aggiorna da solo: fallo a mano se ti va bene.", $"Careful: updating it, winget also installs {others}, which you do not have. OptLife does not update it by itself: do it by hand if that is fine.")
                : x.Stubborn?.Contains(a.Id) == true ? L.T("Dopo l'aggiornamento winget lo elenca ancora: lo aggiorna il programma stesso (o installa la versione nuova accanto a quella vecchia).", "After the update winget still lists it: the program updates itself (or installs the new version beside the old one).")
                : self ? L.T("Ha un suo sistema di aggiornamento (o installa la versione nuova accanto): si aggiorna dal programma.", "It has its own updater (or installs the new version beside the old one): it updates from the program.")
                : !a.WholeId ? L.T("Il nome è troppo lungo per winget: aggiornalo dal programma stesso.", "The name is too long for winget: update it from the program itself.")
                : null;
            var group = excluded ? ExcludedGroup : self ? SelfGroup : AppsGroup;
            board.Add(new CheckItem(group, a.Name, value, group == AppsGroup && a.WholeId ? CheckStatus.Watch : CheckStatus.Info, null, advice ?? a.Id,
                Buttons: a.WholeId && group == AppsGroup ? [new CheckButton(UpgradeOne, a.Id, L.T("AGGIORNA", "UPDATE"))] : null));
        }

        // How the last run went, app by app, in words: what worked, what did not and why, what needs a look.
        if (x.LastRun is { Count: > 0 } run)
        {
            board.Add(new CheckItem(LastRunGroup, x.LastRunAt?.ToLocalTime().ToString("d MMM HH:mm", L.Culture) ?? "", Summary(run).Split(". ")[0] + ".", CheckStatus.Info, StatusText: ""));
            foreach (var r in run.OrderBy(r => r.Outcome == Outcome.Done ? 1 : 0).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
                board.Add(new CheckItem(LastRunGroup, r.Name, r.Why + (r.Technical is { Length: > 0 } t ? "\n" + t : ""),
                    r.Outcome switch { Outcome.Done or Outcome.Reboot => CheckStatus.Ok, Outcome.Attention => CheckStatus.Watch, _ => CheckStatus.Wrong },
                    StatusText: r.Outcome switch
                    {
                        Outcome.Done => L.T("FATTO", "DONE"), Outcome.Reboot => L.T("AL RIAVVIO", "AT RESTART"),
                        Outcome.Attention => L.T("DA CONTROLLARE", "TO CHECK"), _ => L.T("NON RIUSCITO", "NOT DONE"),
                    }));
        }
        return board;
    }

    private static string PreviewAdvice => L.T(
        "\"Ricevi gli aggiornamenti più recenti non appena sono disponibili\" installa le anteprime delle funzioni nuove di Windows prima che siano finite. Un'anteprima può rompere programmi che funzionavano o staccare dispositivi ed effetti audio. Spegnilo in Impostazioni → Windows Update.",
        "\"Get the latest updates as soon as they're available\" installs previews of new Windows features before they are finished. A preview can break programs that used to work or detach audio devices and effects. Turn it off in Settings → Windows Update.");

    // ── Problems ─────────────────────────────────────────────────────────────

    public static IEnumerable<NewFinding> Findings(Inputs x)
    {
        if (x.PreviewOn)
            yield return new NewFinding("preview", Severity.Medium, L.T("Gli aggiornamenti di anteprima di Windows sono accesi", "Windows preview updates are on"),
                L.T("Windows installa le funzioni nuove prima che siano pronte per tutti: è il modo più facile di trovarsi con programmi che smettono di funzionare dopo un aggiornamento.",
                    "Windows installs new features before they are ready for everyone: the easiest way to find programs that stop working after an update."),
                PreviewAdvice, new FindingFix(OpenSite, L.T("Apri Windows Update", "Open Windows Update"), WindowsUpdatePage), FindingArea.Setup);

        var apps = Updatable(x.Apps ?? [], x.Exclude, x.SelfUpdatingList, x.Stubborn, x.NeedsOthers, x.ManagedEmulators).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (apps.Count > 0)
            yield return new NewFinding($"apps|{Hash(apps.Select(a => $"{a.Id}={a.Available}"))}", Severity.Low,
                L.T($"{apps.Count} programmi da aggiornare", $"{apps.Count} programs to update"),
                string.Join(", ", apps.Take(12).Select(a => $"{a.Name} {a.Installed} → {a.Available}")) + (apps.Count > 12 ? L.T($" e altri {apps.Count - 12}", $" and {apps.Count - 12} more") : "") + ".",
                L.T("Il pulsante li mette in coda: OptLife li aggiorna da solo, uno alla volta, anche a pannello chiuso e senza chiederti il permesso di amministratore; l'esito app per app è in Attività e nella pagina Aggiornamenti. Quelli da non toccare vanno nell'elenco \"da non aggiornare\".",
                    "The button queues them: OptLife updates them by itself, one at a time, even with the panel closed and without asking for administrator permission; the outcome app by app is in Activity and on the Updates page. Those not to touch go in the \"do not update\" list."),
                new FindingFix(UpgradeOne, L.T("Aggiorna tutti", "Update all"), string.Join(';', apps.Select(a => a.Id))), FindingArea.Software);

        if (x.Windows is { Count: > 0 } pending && pending.Any(u => u.Driver || !u.Optional))
        {
            var shown = pending.Where(u => u.Driver || !u.Optional).ToList();
            var optional = shown.All(u => u.Optional);
            yield return new NewFinding($"windows|{Hash(shown.Select(u => u.Title))}", Severity.Low,
                L.T($"{shown.Count} aggiornamenti di Windows Update in attesa", $"{shown.Count} Windows Update updates pending"),
                string.Join("; ", shown.Take(6).Select(u => u.Title)) + ".",
                optional ? L.T("Sono facoltativi: i driver si installano solo se risolvono un problema (Impostazioni → Windows Update → Opzioni avanzate → Aggiornamenti facoltativi).",
                               "They are optional: drivers are worth installing only if they fix a problem (Settings → Windows Update → Advanced options → Optional updates).")
                         : L.T("Installali da Impostazioni → Windows Update.", "Install them from Settings → Windows Update."),
                new FindingFix(OpenSite, L.T("Apri Windows Update", "Open Windows Update"), optional ? OptionalPage : WindowsUpdatePage), FindingArea.Setup);
        }

        if (x.Graphics is { Date: not null } g && AgeDays(g.Date, x.Now) > x.GraphicsDays && GraphicsSite(g.Provider) is { } gs)
            yield return new NewFinding("graphics_old", Severity.Low, L.T($"Il driver video ha più di {x.GraphicsDays} giorni", $"The graphics driver is over {x.GraphicsDays} days old"),
                L.T($"{g.Card}: driver del {Date(g.Date)}.", $"{g.Card}: driver from {Date(g.Date)}."),
                L.T("Guarda sul sito del produttore (o nella sua app) se c'è una versione nuova. OptLife non installa driver.", "Check the maker's site (or its app) for a newer version. OptLife does not install drivers."),
                new FindingFix(OpenSite, L.T("Apri il sito", "Open the site"), gs), FindingArea.Setup);

        foreach (var (rule, program) in x.Drivers)
            if (program.InstalledOn is not null && AgeDays(program.InstalledOn, x.Now) > rule.Days)
                yield return new NewFinding($"driver|{rule.Name.ToLowerInvariant()}|{program.Version}", Severity.Low,
                    L.T($"{rule.Name}: installato più di {rule.Days} giorni fa", $"{rule.Name}: installed over {rule.Days} days ago"),
                    L.T($"Versione {program.Version}, installata il {Date(program.InstalledOn)}.", $"Version {program.Version}, installed {Date(program.InstalledOn)}."),
                    L.T("Guarda sul sito del produttore se c'è una versione nuova e installala da lì. OptLife non installa driver.", "Check the maker's site for a newer version and install it from there. OptLife does not install drivers."),
                    DriverSite(rule.Site, x.Bios) is { } site ? new FindingFix(OpenSite, L.T("Apri il sito", "Open the site"), site) : null, FindingArea.Setup);
    }

    /// <summary>A short stable name for a set: the same updates keep the same problem, a new one opens a new one.</summary>
    public static string Hash(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts.Order(StringComparer.Ordinal)))))[..12].ToLowerInvariant();

    /// <summary>Only addresses the action is meant for: web pages and Windows' own settings pages, never a file.</summary>
    public static bool SafeTarget(string? target) =>
        target is not null && (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        && !target.Contains('"');
}

/// <summary>Who a program was installed for: the whole PC (updated as administrator) or this person (updated as the person).</summary>
public enum AppScope { Unknown, Machine, User }

/// <summary>What happened to one winget update: its exit code, or that the wait ran out, or why it did not start.</summary>
public sealed record UpgradeAttempt(int? Code, bool TimedOut, string? Error);

/// <summary>What winget would install along with a package: its package dependencies, and those not on this PC.</summary>
public static class Dependencies
{
    /// <summary>
    /// The package ids under "Package Dependencies" in "winget show" (in any language: a header line ending with ':', then
    /// indented ids like "Apple.iTunes").
    /// </summary>
    public static IReadOnlyList<string> Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var list = new List<string>();
        var inside = false;
        var indent = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var depth = line.Length - line.TrimStart().Length;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && trimmed.EndsWith(':') && trimmed.Contains("Package", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("- ", StringComparison.Ordinal) && trimmed.EndsWith(':') && trimmed.Contains("pacchett", StringComparison.OrdinalIgnoreCase))
            {
                inside = true;
                indent = depth;
                continue;
            }
            if (!inside) continue;
            if (trimmed.Length == 0 || depth <= indent) { inside = false; continue; }
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z0-9][\w\-]*(\.[\w\-+]+)+$")) list.Add(trimmed);
        }
        return list;
    }

    /// <summary>
    /// Dependencies not installed: the last part of the id ("iTunes" of "Apple.iTunes") among the installed programs' names.
    /// Microsoft's runtimes and frameworks are not counted: they are parts of Windows, installed quietly and listed differently.
    /// </summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> dependencies, IReadOnlyList<string> installed) =>
        [.. dependencies.Where(d => !d.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Split('.').Last() is var name && !installed.Any(i => i.Contains(name, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>A dependency id as people read it: "Apple.iTunes" → "iTunes".</summary>
    public static string Name(string id) => id.Split('.').Last();
}
