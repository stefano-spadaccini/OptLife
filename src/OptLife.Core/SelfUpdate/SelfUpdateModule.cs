using System.Text.Json;

namespace OptLife.Core.SelfUpdate;

/// <summary>What OptLife's own updates need from the machine, behind an interface for the tests.</summary>
public interface ISelfUpdateEnvironment
{
    /// <summary>A text from the web (GitHub's answer, the checksum list); null when it cannot be read.</summary>
    Task<string?> GetText(string url, CancellationToken ct);

    Task<bool> Download(string url, string path, Action<long, long?> progress, CancellationToken ct);

    string Sha256(string path);

    /// <summary>True for a valid code signature, false for a broken or untrusted one, null when the file is not signed.</summary>
    bool? SignatureValid(string path);

    /// <summary>Starts the installer on its own (it outlives OptLife, which it stops and starts again).</summary>
    ActionResult Launch(string installer, string arguments);

    bool PanelRunning();

    ActionResult Open(string url);
}

/// <summary>
/// OptLife keeps itself up to date: it reads the latest release of its GitHub repository (the public API, no account), shows
/// what is new, and installs it when the person says so (or by itself, when chosen): the installer is downloaded, checked
/// against the SHA-256 published in the same release (and its signature, when it has one), then run silently. Never during a
/// game. Part of the core, shown like a module page.
/// </summary>
public sealed class SelfUpdateModule : IModule
{
    public const string Id = "optlife", BoardKind = "optlife_board", Install = "install", OpenRelease = "open-release";
    private readonly ISelfUpdateEnvironment _env;

    public SelfUpdateModule() : this(new WindowsSelfUpdateEnvironment()) { }

    public SelfUpdateModule(ISelfUpdateEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(Id, L.T("Aggiornamenti di OptLife", "OptLife updates"),
        L.T("Le versioni nuove di OptLife: cosa cambia, e l'installazione con un pulsante (o da sola, se lo scegli). Mai durante un gioco.",
            "New versions of OptLife: what changes, and installing with a button (or by itself, if you choose). Never during a game."),
        FindingArea.Software);

    private static string Notify => L.T("avvisa", "notify");
    private static string Auto => L.T("da solo", "automatically");
    private static string Off => L.T("spento", "off");

    public IReadOnlyList<SettingDefinition> Settings =>
    [
        SettingDefinition.Choice("mode", L.T("Versioni nuove di OptLife", "New versions of OptLife"),
            L.T("Avvisa: ti dice che c'è una versione nuova e la installi col pulsante. Da solo: OptLife la installa appena la trova, quando non c'è un gioco e il pannello è chiuso. Spento: non controlla.",
                "Notify: tells you a new version is out and you install it with the button. Automatically: OptLife installs it as soon as it finds it, when no game is running and the panel is closed. Off: no check."),
            Notify, Auto, Off),
        SettingDefinition.Int("everyHours", L.T("Ogni quante ore controllare", "Check every how many hours"),
            L.T("GitHub risponde senza account a poche richieste l'ora: una volta al giorno basta.", "GitHub answers few requests an hour without an account: once a day is enough."), 24, 1, 168),
        SettingDefinition.Text("repository", L.T("Repository delle versioni", "Repository of the versions"),
            L.T("Su GitHub, scritto come proprietario/nome. Vuoto = quello da cui viene questa copia di OptLife.", "On GitHub, written as owner/name. Empty = the one this copy of OptLife comes from."), ""),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo delle versioni nuove di OptLife", "Checking for new versions of OptLife"), TimeSpan.FromMinutes(30), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(Install, L.T("Installa la versione nuova di OptLife", "Install the new version of OptLife"),
            L.T("Scarica l'installatore dalla release, controlla che lo SHA-256 sia quello pubblicato, poi lo avvia: OptLife si chiude e riparte aggiornato. Storico e impostazioni restano.",
                "Downloads the installer from the release, checks its SHA-256 is the published one, then runs it: OptLife closes and starts again updated. History and settings stay."),
            Permission.Ask, (ctx, _, ct) => DoInstall(ctx, ct)),
        new ModuleAction(OpenRelease, L.T("Apri la pagina della versione", "Open the version's page"),
            L.T("Apre nel browser la pagina della release su GitHub.", "Opens the release's page on GitHub in the browser."),
            Permission.Ask, (ctx, _, _) => Task.FromResult(Load(ctx) is { Page: { Length: > 0 } page } && page.StartsWith("https://github.com/", StringComparison.Ordinal)
                ? _env.Open(page) : new ActionResult(false, L.T("Nessuna pagina da aprire.", "No page to open.")))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("optlife", L.T("Aggiornamenti di OptLife", "OptLife updates"), BoardKind, [], Days: 365, Menu: ViewMenu.Programs, Order: 90,
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("La versione di OptLife che hai e l'ultima pubblicata. L'installatore si controlla con lo SHA-256 della release prima di avviarlo; storico e impostazioni restano.",
                "The OptLife version you have and the latest one published. The installer is checked against the release's SHA-256 before it runs; history and settings stay."),
            Layout: ViewLayout.Checklist),
    ];

    private static string Repository(IModuleContext ctx) =>
        ctx.Settings.GetText("repository").Trim() is { Length: > 0 } chosen ? chosen : SelfUpdateRules.BuiltRepository;

    private static bool Gaming(IModuleContext ctx) => ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));

    private async Task Check(IModuleContext ctx, CancellationToken ct)
    {
        var mode = ctx.Settings.GetChoice("mode");
        var repo = Repository(ctx);
        var current = SelfUpdateRules.Current;
        var board = new List<CheckItem> { new("OptLife", L.T("Versione installata", "Installed version"), current.ToString(3), CheckStatus.Info) };
        if (mode == Off || repo.Length == 0 || !SelfUpdateRules.ValidRepository(repo))
        {
            board.Add(new CheckItem("OptLife", L.T("Controllo", "Check"),
                mode == Off ? L.T("Spento nelle impostazioni.", "Off in the settings.")
                : repo.Length == 0 ? L.T("Questa copia di OptLife non viene da una release pubblicata: nessun controllo. Si può scrivere il repository nelle impostazioni.", "This copy of OptLife does not come from a published release: no check. The repository can be written in the settings.")
                : L.T($"\"{repo}\" non è un repository scritto come proprietario/nome.", $"\"{repo}\" is not a repository written as owner/name."),
                CheckStatus.Info));
            ctx.SyncFindings("optlife", []);
            Publish(ctx, board);
            return;
        }
        var due = !(ctx.GetState("checked_at") is { } at && DateTimeOffset.TryParse(at, out var last) && ctx.Now - last < TimeSpan.FromHours(ctx.Settings.GetInt("everyHours")));
        if ((due && !Gaming(ctx)) || ctx.OnDemand)
        {
            if (await _env.GetText(SelfUpdateRules.LatestUrl(repo), ct) is { } json && SelfUpdateRules.ParseRelease(json) is { } found)
            {
                ctx.SetState("release", JsonSerializer.Serialize(found));
                ctx.SetState("checked_at", ctx.Now.ToString("o"));
            }
            else ctx.SetState("checked_at", ctx.Now.ToString("o")); // No release yet, or no network: tried, next time later.
        }

        var release = Load(ctx);
        var findings = new List<NewFinding>();
        if (release is null)
            board.Add(new CheckItem("OptLife", L.T("Ultima versione", "Latest version"), L.T("Nessuna versione pubblicata letta ancora.", "No published version read yet."), CheckStatus.Info));
        else if (release.Version > current)
        {
            var notes = SelfUpdateRules.Notes(release.Notes);
            board.Add(new CheckItem("OptLife", L.T("Versione nuova", "New version"), $"{release.Version.ToString(3)}\n{notes}", CheckStatus.Watch,
                StatusText: L.T("DA INSTALLARE", "TO INSTALL"), ValueStatus: CheckStatus.Watch,
                Buttons: [new CheckButton(Install, null, L.T("INSTALLA", "INSTALL")), new CheckButton(OpenRelease, null, L.T("NOVITÀ", "WHAT'S NEW"))]));
            findings.Add(new NewFinding($"update|{release.Version.ToString(3)}", Severity.Low,
                L.T($"È uscita la versione {release.Version.ToString(3)} di OptLife", $"OptLife {release.Version.ToString(3)} is out"),
                L.T($"Hai la {current.ToString(3)}. Novità:\n{notes}", $"You have {current.ToString(3)}. What is new:\n{notes}"),
                L.T("Il pulsante scarica l'installatore, ne controlla lo SHA-256 e lo avvia; storico e impostazioni restano.", "The button downloads the installer, checks its SHA-256 and runs it; history and settings stay."),
                new FindingFix(Install, L.T("Installa", "Install"), null)));
            // By itself: only from the agent's own round (not a "check now"), with no game and the panel closed.
            if (mode == Auto && !ctx.OnDemand && !Gaming(ctx) && !_env.PanelRunning() && ctx.GetState("auto_tried") != release.Tag)
            {
                ctx.SetState("auto_tried", release.Tag);
                var activity = ctx.BeginActivity(L.T($"Aggiornamento di OptLife a {release.Version.ToString(3)}", $"Updating OptLife to {release.Version.ToString(3)}"), byPerson: false);
                var result = await Run(ctx, release, activity, ct);
                activity.End(result.Ok, result.Detail);
            }
        }
        else
            board.Add(new CheckItem("OptLife", L.T("Ultima versione", "Latest version"), L.T($"{release.Version.ToString(3)}: sei aggiornato.", $"{release.Version.ToString(3)}: you are up to date."), CheckStatus.Ok));
        if (ctx.GetState("checked_at") is { } when && DateTimeOffset.TryParse(when, out var w))
            board.Add(new CheckItem("OptLife", L.T("Ultimo controllo", "Last check"), w.ToLocalTime().ToString("d MMM HH:mm", L.Culture), CheckStatus.Info));
        ctx.SyncFindings("optlife", findings);
        Publish(ctx, board);
    }

    private static void Publish(IModuleContext ctx, IReadOnlyList<CheckItem> board)
    {
        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature == ctx.GetState("board") && !ctx.OnDemand) return;
        ctx.SetState("board", signature);
        ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info, L.T("Aggiornamenti di OptLife", "OptLife updates"), BoardKind, Data: data)]);
    }

    private static Release? Load(IModuleContext ctx)
    {
        try { return ctx.GetState("release") is { } json ? JsonSerializer.Deserialize<Release>(json) : null; }
        catch (JsonException) { return null; }
    }

    private async Task<ActionResult> DoInstall(IModuleContext ctx, CancellationToken ct)
    {
        if (Load(ctx) is not { } release || release.Version <= SelfUpdateRules.Current)
            return new ActionResult(false, L.T("Non c'è una versione nuova da installare: premi \"Controlla ora\".", "There is no new version to install: press \"Check now\"."));
        if (Gaming(ctx)) return new ActionResult(false, L.T("C'è un gioco in corso: OptLife si aggiorna dopo.", "A game is running: OptLife updates afterwards."));
        return await Run(ctx, release, null, ct);
    }

    private async Task<ActionResult> Run(IModuleContext ctx, Release release, IActivity? activity, CancellationToken ct)
    {
        void Say(string text, int? done = null, int? total = null) { if (activity is null) ctx.Progress(text, done, total); else activity.Progress(text, done, total); }
        if (release.InstallerName is not { } name || release.InstallerUrl is not { } url || !SelfUpdateRules.SafeFileName(name))
            return new ActionResult(false, L.T("La release non contiene l'installatore di OptLife.", "The release has no OptLife installer."));
        if (release.SumsUrl is not { } sumsUrl)
            return new ActionResult(false, L.T("La release non pubblica lo SHA-256 (SHA256SUMS.txt): non installo un file che non posso controllare.", "The release publishes no SHA-256 (SHA256SUMS.txt): I do not install a file I cannot check."));
        Say(L.T("Lettura dello SHA-256 pubblicato", "Reading the published SHA-256"));
        if (await _env.GetText(sumsUrl, ct) is not { } sums || SelfUpdateRules.ExpectedHash(sums, name) is not { } expected)
            return new ActionResult(false, L.T($"In SHA256SUMS.txt non c'è la riga di {name}.", $"SHA256SUMS.txt has no line for {name}."));

        var folder = Path.Combine(ctx.DataDirectory, "downloads");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "OptLife-Setup-*.exe")) { try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        var path = Path.Combine(folder, name);
        Say(L.T($"Download di {name}", $"Downloading {name}"));
        var ok = await _env.Download(url, path, (got, total) => Say(L.T($"Download di {name}", $"Downloading {name}"),
            (int)(got / (1024 * 1024)), total is { } t ? (int)(t / (1024 * 1024)) : null), ct);
        if (!ok) return new ActionResult(false, L.T("Download non riuscito: riprova più tardi.", "Download failed: try again later."));

        var hash = _env.Sha256(path);
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch (IOException) { }
            return new ActionResult(false, L.T($"Lo SHA-256 del file scaricato ({hash}) non è quello pubblicato ({expected}): file cancellato, niente installato.",
                $"The downloaded file's SHA-256 ({hash}) is not the published one ({expected}): file deleted, nothing installed."));
        }
        if (_env.SignatureValid(path) == false)
        {
            try { File.Delete(path); } catch (IOException) { }
            return new ActionResult(false, L.T("La firma dell'installatore non è valida: file cancellato, niente installato.", "The installer's signature is not valid: file deleted, nothing installed."));
        }
        Say(L.T("Avvio dell'installatore", "Starting the installer"));
        // The panel is closed by the installer: it opens again afterwards if it was open.
        var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-" + (_env.PanelRunning() ? " /OPENPANEL=1" : "") + $" /LANG={L.Code}";
        var launched = _env.Launch(path, args);
        return launched.Ok
            ? new ActionResult(true, L.T($"SHA-256 controllato, installazione di OptLife {release.Version.ToString(3)} avviata: OptLife si chiude e riparte tra poco.",
                $"SHA-256 checked, installing OptLife {release.Version.ToString(3)}: OptLife closes and starts again shortly."))
            : launched;
    }
}
