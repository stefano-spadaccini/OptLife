using System.Globalization;
using OptLife.Core.Config;
using OptLife.Core.Storage;

namespace OptLife.Core.Hosting;

public sealed record TaskStatus(string ModuleId, string TaskId, DateTimeOffset? LastRun, string? LastError, int Failures);

/// <summary>
/// Runs module code behind a wall: whatever a module throws is recorded against that module
/// and never reaches the caller, so one broken module cannot stop the others.
/// </summary>
/// <param name="readShared">Outside the agent (panel, command line): modules read the live values from the agent's shared memory.</param>
public sealed class ModuleRunner(Store store, ConfigStore config, TimeProvider clock, LiveBoard? live = null, bool readShared = false)
{
    public const string CoreModuleId = "core";
    private const int FailuresBeforeFinding = 3;

    // A task that runs every couple of seconds must not write its "last run" to disk every time:
    // while nothing changes, the bookkeeping is refreshed at most this often.
    private static readonly TimeSpan BookkeepingEvery = TimeSpan.FromSeconds(30);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Module, string Task), (string? Error, DateTimeOffset At)> _written = new();

    public bool IsEnabled(IModule module) => config.GetModule(module.Info.Id).Enabled;

    public const string AutoActor = "auto";

    // A fix that runs by itself must not run in circles when the problem keeps coming back.
    private static readonly TimeSpan AutoFixEvery = TimeSpan.FromHours(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Module, string Action, string? Argument), DateTimeOffset> _autoFixed = new();

    public Permission PermissionOf(IModule module, ModuleAction action)
    {
        var chosen = config.GetModule(module.Info.Id).Permissions.GetValueOrDefault(action.Id, action.DefaultPermission);
        // Removing things, or acting on a pick the person has to make, is never left to run by itself,
        // not even by a hand-edited configuration.
        return (action.NeverAuto || action.Choices is not null) && chosen == Permission.Auto ? Permission.Ask : chosen;
    }

    /// <param name="onDemand">The person asked for this run, as opposed to the schedule reaching it.</param>
    public async Task<bool> RunTaskAsync(IModule module, ModuleTask task, CancellationToken ct, bool onDemand = false)
    {
        var id = module.Info.Id;
        string? error = null;
        try
        {
            var context = CreateContext(module, onDemand);
            await task.Run(context, ct);

            // Problems that have just appeared and whose fix the person allowed to run without asking.
            foreach (var fix in context.Appeared.Select(f => f.Fix).OfType<FindingFix>().Distinct())
            {
                if (module.Actions.FirstOrDefault(a => a.Id == fix.ActionId) is not { } action) continue;
                if (PermissionOf(module, action) != Permission.Auto) continue;
                var key = (id, fix.ActionId, fix.Argument);
                var moment = clock.GetLocalNow();
                if (_autoFixed.TryGetValue(key, out var previous) && moment - previous < AutoFixEvery) continue;
                _autoFixed[key] = moment;
                await RunActionAsync(module, action, AutoActor, confirmed: false, fix.Argument, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        var now = clock.GetLocalNow();
        var unchanged = error is null
            && _written.TryGetValue((id, task.Id), out var last)
            && last.Error is null
            && now - last.At < BookkeepingEvery;
        if (unchanged) return true;
        _written[(id, task.Id)] = (error, now);

        var failures = error is null ? 0 : ReadStatus(module, task).Failures + 1;
        store.SetState(id, StateKey(task, "last_run"), now.ToString("o"));
        store.SetState(id, StateKey(task, "last_error"), error);
        store.SetState(id, StateKey(task, "failures"), failures.ToString(CultureInfo.InvariantCulture));

        NewFinding[] finding = failures >= FailuresBeforeFinding
            ? [new NewFinding(
                "failing", Severity.Medium,
                L.T($"Il modulo \"{module.Info.Name}\" non riesce a lavorare", $"The \"{module.Info.Name}\" module cannot work"),
                L.T($"L'attività \"{task.Name}\" è fallita {failures} volte di fila. Ultimo errore: {error}", $"The \"{task.Name}\" task failed {failures} times in a row. Last error: {error}"),
                L.T("Gli altri moduli continuano a funzionare. Puoi spegnere questo modulo dal menu finché non viene sistemato.", "The other modules keep working. You can switch this module off from the menu until it is fixed."))]
            : [];
        store.SyncFindings(CoreModuleId, $"task:{id}:{task.Id}", finding, now, FindingArea.Software);

        return error is null;
    }

    public async Task<ActionResult> RunActionAsync(IModule module, ModuleAction action, string actor, bool confirmed, string? argument, CancellationToken ct)
    {
        var permission = PermissionOf(module, action);
        ActionResult result;
        if (permission == Permission.Never)
            result = new ActionResult(false, L.T("Azione disattivata nelle impostazioni.", "Action switched off in the settings."));
        else if (permission == Permission.Ask && !confirmed)
            result = new ActionResult(false, L.T("Questa azione richiede una conferma.", "This action needs a confirmation."));
        else
        {
            try
            {
                // Every action is an activity: the panel and the agent's pop-up show it while it runs and how it ended.
                var activity = store.BeginActivity(module.Info.Id, action.Name, actor == AutoActor ? AutoActor : "person", clock.GetLocalNow());
                try
                {
                    result = await action.Run(CreateContext(module, activity: activity), argument, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new ActionResult(false, $"{ex.GetType().Name}: {ex.Message}");
                }
                catch (OperationCanceledException)
                {
                    // Also when stopped: an activity never stays "running" for ever.
                    store.EndActivity(activity, false, L.T("Interrotta.", "Stopped."), clock.GetLocalNow());
                    throw;
                }
                store.EndActivity(activity, result.Ok, result.Detail, clock.GetLocalNow());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new ActionResult(false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        store.LogAction(clock.GetLocalNow(), module.Info.Id, action.Id, actor, result);
        return result;
    }

    /// <summary>What the person can pick from before the action runs; empty when it has no choices or they could not be read.</summary>
    public IReadOnlyList<ActionChoice> ChoicesOf(IModule module, ModuleAction action, out string? error)
    {
        error = null;
        if (action.Choices is null) return [];
        try
        {
            return action.Choices(CreateContext(module, onDemand: true));
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return [];
        }
    }

    public TaskStatus ReadStatus(IModule module, ModuleTask task)
    {
        var id = module.Info.Id;
        var lastRun = store.GetState(id, StateKey(task, "last_run"));
        _ = int.TryParse(store.GetState(id, StateKey(task, "failures")), out var failures);
        return new TaskStatus(
            id, task.Id,
            lastRun is null ? null : DateTimeOffset.Parse(lastRun, CultureInfo.InvariantCulture),
            store.GetState(id, StateKey(task, "last_error")),
            failures);
    }

    public ISettings SettingsOf(IModule module) =>
        new ModuleSettings(module.Settings, config.GetModule(module.Info.Id).Settings);

    private ModuleContext CreateContext(IModule module, bool onDemand = false, long? activity = null) =>
        new(module.Info.Id, store, SettingsOf(module), clock, live, readShared,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.Path))!, "moduli", module.Info.Id), onDemand, module.Info.Area, activity);

    // Keys starting with "_" are reserved for the core; modules use plain names.
    private static string StateKey(ModuleTask task, string name) => $"_task:{task.Id}:{name}";

    private sealed class ModuleContext(string moduleId, Store store, ISettings settings, TimeProvider clock, LiveBoard? live, bool readShared, string dataDirectory, bool onDemand, string? area, long? activity = null) : IModuleContext, IHistory
    {
        public string ModuleId => moduleId;

        public void Progress(string text, int? done = null, int? total = null)
        {
            if (activity is { } id) store.ProgressActivity(id, text, done, total, clock.GetLocalNow());
        }

        public IActivity BeginActivity(string title, bool byPerson, bool timetable = false) =>
            new StoreActivity(store, store.BeginActivity(moduleId, title, byPerson ? "person" : timetable ? "scheduler" : AutoActor, clock.GetLocalNow()), clock);
        public bool OnDemand => onDemand;

        public string DataDirectory
        {
            get
            {
                Directory.CreateDirectory(dataDirectory);
                return dataDirectory;
            }
        }
        public DateTimeOffset Now => clock.GetLocalNow();
        public ISettings Settings => settings;
        public IHistory History => this;

        public void RecordSample(string metric, double value, DateTimeOffset? at = null) =>
            store.InsertSamples(moduleId, [new(metric, value)], at ?? Now);

        public void RecordSamples(IEnumerable<KeyValuePair<string, double>> samples, DateTimeOffset? at = null) =>
            store.InsertSamples(moduleId, samples, at ?? Now);

        // Without a board (the CLI running a check by hand) live values simply go nowhere.
        public void PublishLive(IReadOnlyCollection<LiveValue> values) => live?.Publish(moduleId, values, Now);

        // In the agent the board is its own; in the panel or the command line it is read from the agent's shared memory.
        public IReadOnlyList<LiveValue> ReadLive() =>
            live?.Current(Now)
            ?? (readShared ? [.. LiveBoard.Read(Now).Select(e => new LiveValue(e.Key, e.Label, e.Group, e.Value, e.Unit, e.Stress, e.Missing, e.MissingFix))] : []);

        public int RecordEvents(IEnumerable<NewEvent> events) => store.InsertEvents(moduleId, events);

        /// <summary>Findings that appeared during this run, for the fixes allowed to run by themselves.</summary>
        public List<NewFinding> Appeared { get; } = [];

        public void SyncFindings(string scope, IReadOnlyCollection<NewFinding> current) =>
            Appeared.AddRange(store.SyncFindings(moduleId, scope, current, Now, area));

        public string? GetState(string key) => store.GetState(moduleId, Guard(key));
        public void SetState(string key, string? value) => store.SetState(moduleId, Guard(key), value);

        public IReadOnlyList<StoredEvent> Events(DateTimeOffset from, DateTimeOffset to, string? module = null, string? kind = null) =>
            store.QueryEvents(from, to, module, kind);

        public IReadOnlyList<SamplePoint> Samples(string metric, DateTimeOffset from, DateTimeOffset to) =>
            store.QuerySamples(metric, from, to);

        private static string Guard(string key) =>
            key.StartsWith('_') ? throw new ArgumentException("State keys starting with '_' are reserved.", nameof(key)) : key;
    }
}

/// <summary>An activity a module started by itself (a Scheduler job), written to the history as it goes.</summary>
internal sealed class StoreActivity(Store store, long id, TimeProvider clock) : IActivity
{
    public void Progress(string text, int? done = null, int? total = null) => store.ProgressActivity(id, text, done, total, clock.GetLocalNow());
    public void End(bool ok, string detail) => store.EndActivity(id, ok, detail, clock.GetLocalNow());
}
