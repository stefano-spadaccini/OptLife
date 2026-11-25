using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Errors;

/// <summary>
/// Collects the errors Windows already records (program crashes, hangs, blue screens, driver resets,
/// failing services) into the shared history, and raises the ones that keep happening.
/// </summary>
public sealed class ErrorsModule : IModule
{
    private static readonly string[] Logs = ["Application", "System"];
    private readonly IEventSource _source;

    public ErrorsModule() : this(new WindowsEventSource()) { }

    internal ErrorsModule(IEventSource source) => _source = source;

    public ModuleInfo Info { get; } = new(
        "errors", L.T("Errori", "Errors"),
        L.T("Raccoglie crash, blocchi ed errori di sistema registrati da Windows e segnala quelli che si ripetono.", "Collects crashes, hangs and system errors recorded by Windows and reports the ones that repeat."), FindingArea.Software);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Int("lookbackDays", L.T("Giorni da recuperare al primo avvio", "Days to read back at the first start"),
            L.T("Quanto indietro leggere il registro di Windows la prima volta.", "How far back to read the Windows event log the first time."), 14, 1, 90),
        SettingDefinition.Int("windowDays", L.T("Periodo considerato", "Period considered"),
            L.T("Un problema viene mostrato in base a quante volte è successo in questi ultimi giorni.", "A problem is shown based on how many times it happened in these last days."), 7, 1, 30),
        SettingDefinition.Int("crashThreshold", L.T("Crash prima di segnalare", "Crashes before reporting"),
            L.T("Quante volte lo stesso programma deve chiudersi o bloccarsi per comparire tra i problemi.", "How many times the same program must crash or hang to show up among the problems."), 2, 1, 20),
        SettingDefinition.Int("recurringThreshold", L.T("Ripetizioni di un errore generico", "Repeats of a generic error"),
            L.T("Quante volte un errore di sistema qualsiasi deve ripetersi per essere mostrato.", "How many times any system error must repeat to be shown."), 10, 2, 500),
        SettingDefinition.List("ignored", L.T("Origini da ignorare", "Sources to ignore"),
            L.T("Errori noti come innocui, scritti come Origine oppure Origine:Numero evento.", "Errors known to be harmless, written as Source or Source:Event number."),
            "Microsoft-Windows-DistributedCOM:10016"),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("scan", L.T("Lettura del registro di Windows", "Reading the Windows event log"), TimeSpan.FromMinutes(10), ScanAsync),
    ];

    private Task ScanAsync(IModuleContext ctx, CancellationToken ct)
    {
        var settings = ctx.Settings;
        var ignored = settings.GetList("ignored");

        foreach (var log in Logs)
        {
            ct.ThrowIfCancellationRequested();
            var cursorKey = $"cursor:{log}";
            var since = ctx.GetState(cursorKey) is { } saved
                ? DateTimeOffset.Parse(saved, CultureInfo.InvariantCulture)
                : ctx.Now.AddDays(-settings.GetInt("lookbackDays"));

            // Reading resumes from the newest record seen; the record itself is read again and
            // dropped as a duplicate, so nothing written in the same instant can be missed.
            var newest = since;
            var events = new List<NewEvent>();
            foreach (var raw in _source.Read(log, since))
            {
                if (raw.Time > newest) newest = raw.Time;
                if (EventClassifier.Classify(raw, ignored) is { } e) events.Add(e);
            }
            ctx.RecordEvents(events);
            ctx.SetState(cursorKey, newest.ToString("o"));
        }

        var days = settings.GetInt("windowDays");
        var window = ctx.History.Events(ctx.Now.AddDays(-days), ctx.Now, module: ctx.ModuleId);
        ctx.SyncFindings("recurring", ErrorRules.Evaluate(
            window, days, settings.GetInt("crashThreshold"), settings.GetInt("recurringThreshold")));
        return Task.CompletedTask;
    }
}
