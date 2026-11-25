using System.Globalization;
using OptLife.Modules.Space.Windows;

namespace OptLife.Modules.Space;

/// <summary>One thing directly inside a temporary folder, with its full weight and the newest change inside it.</summary>
public sealed record TempItem(string Path, long Bytes, DateTimeOffset LastWrite);

/// <summary>The Recycle Bin of one drive.</summary>
public sealed record BinInfo(string Drive, long Bytes, long Items, long DriveBytes);

/// <summary>What the module needs from the machine, behind an interface for the tests.</summary>
public interface ISpaceEnvironment
{
    /// <summary>What is directly inside the folder; null when the folder is not there.</summary>
    IReadOnlyList<TempItem>? Temp(string folder);

    IReadOnlyList<BinInfo> RecycleBins();

    /// <summary>To the Recycle Bin, never a real delete; what is in use stays where it is.</summary>
    ActionResult Recycle(IReadOnlyList<string> paths);

    ActionResult Open(string target);
}

/// <summary>
/// Space: leftovers in the temporary folders (installers unpack there and often leave everything behind) and Recycle Bins grown
/// too big. It sends old leftovers to the Recycle Bin when asked, and opens the bin so the person empties it: it never deletes
/// anything for good.
/// </summary>
public sealed class SpaceModule : IModule
{
    public const string BoardKind = "space_board", RecycleTemp = "recycle-temp", Open = "open";
    public const string BinTarget = "shell:RecycleBinFolder";
    private readonly ISpaceEnvironment _env;

    public SpaceModule() : this(new WindowsSpaceEnvironment()) { }

    internal SpaceModule(ISpaceEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new("space", L.T("Spazio", "Space"),
        L.T("Resti di installazioni nelle cartelle temporanee e Cestino troppo pieno.", "Installation leftovers in temporary folders and a Recycle Bin grown too full."),
        FindingArea.Tidy);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.List("tempFolders", L.T("Cartelle temporanee", "Temporary folders"),
            L.T("Dove i programmi e gli installatori lasciano file di passaggio.", "Where programs and installers leave files on their way."), @"%TEMP%", @"%SystemRoot%\Temp"),
        SettingDefinition.Int("tempDays", L.T("Resti: fermi da almeno (giorni)", "Leftovers: untouched for at least (days)"),
            L.T("Solo ciò che non viene toccato da almeno così tanto conta come resto: quello recente può servire a un programma aperto.", "Only what has not been touched for at least this long counts as leftover: recent things may be in use by an open program."), 7, 1, 365),
        SettingDefinition.Int("tempMb", L.T("Resti: segnala da (MB)", "Leftovers: report from (MB)"),
            L.T("Sotto questa quantità non vale la pena disturbare.", "Below this amount it is not worth bothering."), 500, 10, 100_000),
        SettingDefinition.Int("binGb", L.T("Cestino: segnala da (GB)", "Recycle Bin: report from (GB)"),
            L.T("Un Cestino più grande di così su un disco viene segnalato: si svuota da Esplora file, OptLife non lo fa mai.", "A Recycle Bin bigger than this on a drive is reported: it is emptied from File Explorer, OptLife never does it."), 10, 1, 10_000),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo dello spazio", "Checking space"), TimeSpan.FromHours(2), Check),
    ];

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(RecycleTemp, L.T("Manda al Cestino i resti", "Send leftovers to the Recycle Bin"),
            L.T("Manda al Cestino ciò che è fermo da abbastanza giorni nella cartella temporanea. Ciò che è in uso resta. Dal Cestino si può riprendere.",
                "Sends to the Recycle Bin what has sat still long enough in the temporary folder. What is in use stays. It can be restored from the Recycle Bin."),
            Permission.Ask, (ctx, folder, ct) => DoRecycle(ctx, folder, ct), NeverAuto: true),
        new ModuleAction(Open, L.T("Apri", "Open"), L.T("Apre la cartella o il Cestino.", "Opens the folder or the Recycle Bin."),
            Permission.Ask, (ctx, target, _) => Task.FromResult(Allowed(ctx, target) ? _env.Open(target!) : new ActionResult(false, L.T("Non è una cartella di questo modulo.", "Not a folder of this module.")))),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("space", L.T("Spazio", "Space"), BoardKind, [], Days: 365, Menu: ViewMenu.Order,
            EmptyText: L.T("Ancora nessun controllo: premi \"Controlla ora\".", "Not checked yet: press \"Check now\"."),
            Description: L.T("I resti nelle cartelle temporanee (gli installatori ci scompattano e spesso lasciano tutto) e il Cestino di ogni disco. CESTINO manda i resti vecchi nel Cestino; il Cestino lo svuoti tu, OptLife non cancella mai niente per sempre.",
                             "Leftovers in the temporary folders (installers unpack there and often leave everything) and each drive's Recycle Bin. BIN sends old leftovers to the Recycle Bin; you empty the bin yourself, OptLife never deletes anything for good."),
            Layout: ViewLayout.Checklist),
    ];

    private static IReadOnlyList<string> Folders(IModuleContext ctx) => [.. ctx.Settings.GetList("tempFolders").Select(f => f.Trim()).Where(f => f.Length > 0)];

    /// <summary>Only the module's own folders and the Recycle Bin can be opened: the action is not a way to open anything.</summary>
    private static bool Allowed(IModuleContext ctx, string? target) =>
        target is not null && (target == BinTarget || Folders(ctx).Contains(target, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<TempItem> Old(IReadOnlyList<TempItem> items, int days, DateTimeOffset now) =>
        [.. items.Where(i => now - i.LastWrite >= TimeSpan.FromDays(days))];

    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", L.Culture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", L.Culture) + " MB";

    private Task Check(IModuleContext ctx, CancellationToken ct)
    {
        if (!ctx.OnDemand && ctx.ReadLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal))) return Task.CompletedTask;
        var s = ctx.Settings;
        var days = s.GetInt("tempDays");
        var board = new List<CheckItem>();
        var findings = new List<NewFinding>();
        var tempGroup = L.T("Cartelle temporanee", "Temporary folders");
        foreach (var folder in Folders(ctx))
        {
            ct.ThrowIfCancellationRequested();
            if (_env.Temp(folder) is not { } items)
            {
                board.Add(new CheckItem(tempGroup, folder, L.T("Non c'è o non si legge", "Missing or unreadable"), CheckStatus.Info));
                continue;
            }
            var old = Old(items, days, ctx.Now);
            var oldBytes = old.Sum(i => i.Bytes);
            var big = oldBytes >= s.GetInt("tempMb") * (1L << 20);
            board.Add(new CheckItem(tempGroup, folder,
                L.T($"{Size(oldBytes)} fermi da più di {days} giorni ({old.Count} elementi), su {Size(items.Sum(i => i.Bytes))} in tutto",
                    $"{Size(oldBytes)} untouched for over {days} days ({old.Count} items), out of {Size(items.Sum(i => i.Bytes))} in all"),
                big ? CheckStatus.Watch : CheckStatus.Ok, null,
                big ? L.T("Sono resti di installazioni e programmi: si possono mandare al Cestino.", "They are leftovers of installations and programs: they can go to the Recycle Bin.") : null,
                Buttons: [.. old.Count > 0 ? [new CheckButton(RecycleTemp, folder, L.T("CESTINO", "BIN"))] : Array.Empty<CheckButton>(), new CheckButton(Open, folder, L.T("APRI", "OPEN"))]));
            if (big)
                findings.Add(new NewFinding($"temp|{folder.ToLowerInvariant()}", Severity.Low,
                    L.T($"{Size(oldBytes)} di resti in {folder}", $"{Size(oldBytes)} of leftovers in {folder}"),
                    L.T($"{old.Count} elementi fermi da più di {days} giorni: di solito installatori scompattati e lasciati lì.", $"{old.Count} items untouched for over {days} days: usually installers unpacked and left there."),
                    L.T("Il pulsante li manda al Cestino (ciò che è in uso resta). Poi svuota il Cestino quando hai controllato.", "The button sends them to the Recycle Bin (what is in use stays). Then empty the bin once you have checked."),
                    new FindingFix(RecycleTemp, L.T("Manda al Cestino", "Send to the Recycle Bin"), folder)));
        }

        var binGroup = L.T("Cestino", "Recycle Bin");
        foreach (var bin in _env.RecycleBins().OrderBy(b => b.Drive))
        {
            var full = bin.Bytes >= s.GetInt("binGb") * (1L << 30);
            board.Add(new CheckItem(binGroup, L.T($"Disco {bin.Drive}", $"Drive {bin.Drive}"),
                L.T($"{Size(bin.Bytes)} in {bin.Items} elementi", $"{Size(bin.Bytes)} in {bin.Items} items"),
                full ? CheckStatus.Watch : CheckStatus.Ok, null,
                full ? L.T("Lo spazio nel Cestino è ancora occupato: aprilo, controlla e svuotalo tu.", "Space in the Recycle Bin is still taken: open it, check and empty it yourself.") : null,
                Buttons: [new CheckButton(Open, BinTarget, L.T("APRI", "OPEN"))]));
            if (full)
                findings.Add(new NewFinding($"bin|{bin.Drive.ToLowerInvariant()}", Severity.Low,
                    L.T($"Il Cestino del disco {bin.Drive} occupa {Size(bin.Bytes)}", $"The Recycle Bin on drive {bin.Drive} takes {Size(bin.Bytes)}"),
                    L.T($"{bin.Items} elementi buttati ma non ancora tolti dal disco.", $"{bin.Items} items thrown away but not yet removed from the drive."),
                    L.T("Apri il Cestino, riprendi ciò che serve e svuotalo. OptLife non lo svuota mai da solo.", "Open the Recycle Bin, restore what you need and empty it. OptLife never empties it by itself."),
                    new FindingFix(Open, L.T("Apri il Cestino", "Open the Recycle Bin"), BinTarget)));
        }

        ctx.SyncFindings("space", findings);
        var data = CheckItem.ToData(board);
        var signature = string.Join('\n', data.OrderBy(p => p.Key).Select(p => p.Value));
        if (signature != ctx.GetState("board"))
        {
            ctx.SetState("board", signature);
            ctx.RecordEvents([new NewEvent(ctx.Now, BoardKind, Severity.Info, L.T($"Spazio: {findings.Count} da guardare", $"Space: {findings.Count} to look at"), BoardKind, Data: data)]);
        }
        return Task.CompletedTask;
    }

    private async Task<ActionResult> DoRecycle(IModuleContext ctx, string? folder, CancellationToken ct)
    {
        if (folder is null || !Folders(ctx).Contains(folder, StringComparer.OrdinalIgnoreCase))
            return new ActionResult(false, L.T("Non è una cartella temporanea dell'elenco.", "Not a temporary folder of the list."));
        // Looked up again now: only what is old at this very moment goes.
        var old = Old(_env.Temp(folder) ?? [], ctx.Settings.GetInt("tempDays"), ctx.Now);
        if (old.Count == 0) return new ActionResult(true, L.T("Non c'è più niente di vecchio.", "Nothing old left."));
        var result = _env.Recycle([.. old.Select(i => i.Path)]);
        await Check(new Forced(ctx), ct);
        return result;
    }

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
