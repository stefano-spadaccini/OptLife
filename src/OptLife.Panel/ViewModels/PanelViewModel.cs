using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Core.Storage;

namespace OptLife.Panel.ViewModels;

public enum PanelPage { Problems, Monitor, History, Actions, Modules, View, Report, Activity, Intro }

/// <summary>What the Problems page lists: problems by state, or everything that was recorded (the history they come from).</summary>
public enum FindingFilter { Open, Ignored, Resolved, History }

/// <summary>
/// Everything the panel shows, with no window in it: it reads the shared history and config
/// directly and asks the agent (when there is one) to run the checks.
/// </summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private const string ModeKey = "mode";
    private const string PinnedKey = "pinned";
    private const int FeaturedCount = 6;
    private readonly List<string> _pinned;
    private string _featuredShown = "";
    private readonly Store _store;
    private readonly ConfigStore _config;
    private readonly ModuleRunner _runner;
    private readonly IReadOnlyList<IModule> _modules;
    private readonly Func<bool> _agentRunning;
    private readonly Func<bool> _requestRunNow;
    private readonly TimeProvider _clock;
    private string _findingsShown = "";
    private string _eventsShown = "";
    private string _actionsShown = "";
    private string _reportShown = "";
    private readonly Func<IReadOnlyList<LiveEntry>> _readLive;
    private readonly Dictionary<string, MonitorTile> _tiles = [];
    private DateTimeOffset _chartsLoadedAt = DateTimeOffset.MinValue;

    public PanelViewModel(
        Store store, ConfigStore config, ModuleRunner runner, IReadOnlyList<IModule> modules,
        Func<bool> agentRunning, Func<bool> requestRunNow, TimeProvider clock,
        Func<IReadOnlyList<LiveEntry>>? readLive = null)
    {
        _readLive = readLive ?? (() => LiveBoard.Read(clock.GetLocalNow()));
        _store = store;
        _config = config;
        _runner = runner;
        _modules = modules;
        _agentRunning = agentRunning;
        _requestRunNow = requestRunNow;
        _clock = clock;
#pragma warning disable MVVMTK0034 // Initial value: assigning the property would write it back to the config.
        _expertMode = true; // There is one view now: everything, always.
#pragma warning restore MVVMTK0034
        _pinned = [.. (config.Load().Ui.GetValueOrDefault(PinnedKey) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)];
        foreach (var m in modules) Modules.Add(new ModuleItem(m, config, runner, Now, key => SettingOption.Read(store.GetState(m.Info.Id, SettingOption.StateKey(key)))));
        foreach (var m in modules)
            foreach (var v in m.Views) Views.Add(new ViewTab(m, v));
        LoadAppearance();
        LoadShell();
        BuildNavigation();
        Refresh();
        RestorePage();
        ResumeTour();
    }

    /// <summary>Pages the modules asked for (the game sessions, for one).</summary>
    public ObservableCollection<ViewTab> Views { get; } = [];

    [ObservableProperty] private ViewTab? _selectedView;

    /// <summary>Opens the full-screen view of a gallery page (set by the application; absent in tests).</summary>
    public Action<ViewTab>? OpenBig { get; set; }

    [RelayCommand]
    private void BigLibrary()
    {
        if (SelectedView is { IsGallery: true } tab) OpenBig?.Invoke(tab);
    }

    /// <summary>A game is running now (the Games module's values are on the live board).</summary>
    public bool GameRunning() => _readLive().Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));

    /// <summary>The page with the full-screen view: the first gallery page (the library), shown so its buttons work.</summary>
    public ViewTab? ShowGallery()
    {
        if (Views.FirstOrDefault(v => v.IsGallery) is not { } tab) return null;
        ShowView(tab);
        return tab;
    }

    [RelayCommand]
    private void ShowView(ViewTab tab)
    {
        foreach (var v in Views) v.IsSelected = v == tab;
        SelectedView = tab;
        if (Page == PanelPage.View) Refresh();
        else Page = PanelPage.View;
        UpdateNavigation();
    }

    public ObservableCollection<FindingItem> Findings { get; } = [];

    /// <summary>The same problems, one section per area.</summary>
    public ObservableCollection<FindingSection> FindingSections { get; } = [];
    public ObservableCollection<EventItem> Events { get; } = [];
    public ObservableCollection<ModuleItem> Modules { get; } = [];
    public ObservableCollection<MonitorGroup> MonitorGroups { get; } = [];

    /// <summary>The values this PC cannot read, each with why (the processor temperature without MSI Afterburner).</summary>
    public ObservableCollection<MonitorNotice> MonitorNotices { get; } = [];

    /// <summary>The button of a notice on the Monitor page: an action of the module that published it (it opens a site); pressing it is the confirmation.</summary>
    [RelayCommand]
    private async Task MonitorNoticeAsync(MonitorNotice notice)
    {
        if (notice.Fix is not { } fix || IsChecking || _modules.FirstOrDefault(m => m.Info.Id == notice.Module) is not { } module
            || module.Actions.FirstOrDefault(a => a.Id == fix.ActionId) is not { } action || _runner.PermissionOf(module, action) == Permission.Never) return;
        IsChecking = true;
        try
        {
            var result = await Task.Run(() => _runner.RunActionAsync(module, action, "pannello", confirmed: true, fix.Argument, CancellationToken.None));
            notice.Result = result.Detail;
        }
        finally
        {
            IsChecking = false;
        }
    }
    public ObservableCollection<ActionLogItem> ActionLog { get; } = [];

    [ObservableProperty] private string _monitorEmptyText = "";
    [ObservableProperty] private string _actionLogEmptyText = "";

    /// <summary>What the last "put it right" button did, shown above the list of problems.</summary>
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private bool _noticeIsError;

    /// <summary>How long to wait for the agent after asking it to check; tests set it to zero.</summary>
    internal TimeSpan AgentGrace { get; set; } = TimeSpan.FromSeconds(2);

    // The first thing shown is how the PC is doing right now; problems are one tab away.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InProblems))] private PanelPage _page = PanelPage.Monitor;

    /// <summary>The history is the fourth view of the problems page, so both light up the same tab.</summary>
    public bool InProblems => Page is PanelPage.Problems;

    [RelayCommand]
    private void ShowProblems(string filter)
    {
        Filter = Enum.Parse<FindingFilter>(filter);
        Page = PanelPage.Problems;
    }

    /// <summary>The few values worth a glance: the ones the person pinned, or else the most strained right now.</summary>
    public ObservableCollection<MonitorTile> Featured { get; } = [];

    [ObservableProperty] private string _featuredTitle = "";

    [RelayCommand]
    private void TogglePin(MonitorTile tile)
    {
        if (!_pinned.Remove(tile.Key)) _pinned.Add(tile.Key);
        _config.Update(f => f.Ui[PinnedKey] = string.Join(';', _pinned));
        Refresh();
    }
    [ObservableProperty] private bool _expertMode;
    [ObservableProperty] private FindingFilter _filter = FindingFilter.Open;
    [ObservableProperty] private int _openCount;
    [ObservableProperty] private int _ignoredCount;
    [ObservableProperty] private int _resolvedCount;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _subline = "";
    [ObservableProperty] private string _statusLevel = "ok";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CheckLabel))] private bool _isChecking;
    [ObservableProperty] private int _historyDays = 7;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _historySummary = "";

    public string CheckLabel => IsChecking ? L.T("CONTROLLO IN CORSO", "CHECKING") : L.T("CONTROLLA ORA", "CHECK NOW");

    private DateTimeOffset Now => _clock.GetLocalNow();

    partial void OnPageChanged(PanelPage value)
    {
        Notice = "";
        if (value != PanelPage.View)
            foreach (var v in Views) v.IsSelected = false;
        UpdateNavigation();
        Refresh();
    }

    partial void OnFilterChanged(FindingFilter value)
    {
        Notice = "";
        Refresh();
    }
    partial void OnHistoryDaysChanged(int value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();

    partial void OnExpertModeChanged(bool value)
    {
        _config.Update(f => f.Ui[ModeKey] = value ? "expert" : "simple");
        if (!value) Page = PanelPage.Problems; // The other pages exist only in expert mode.
    }

    /// <summary>Re-reads what the current page shows. Cheap enough to call every few seconds.</summary>
    public void Refresh()
    {
        var all = _store.GetFindings(includeClosed: true);
        var open = all.Where(f => f.Status == FindingStatus.Open).ToList();
        OpenCount = open.Count;
        IgnoredCount = all.Count(f => f.Status == FindingStatus.Ignored);
        ResolvedCount = all.Count(f => f.Status == FindingStatus.Resolved);

        Headline = open.Count switch
        {
            0 => L.T("Tutto a posto", "All good"),
            1 => L.T("1 problema aperto", "1 open problem"),
            _ => L.T($"{open.Count} problemi aperti", $"{open.Count} open problems"),
        };
        StatusLevel = open.Count == 0 ? "ok" : Display.Level(open.Max(f => f.Severity));
        Subline = DescribeAgent();

        var status = Filter switch
        {
            FindingFilter.Ignored => FindingStatus.Ignored,
            FindingFilter.Resolved => FindingStatus.Resolved,
            _ => FindingStatus.Open,
        };
        var shown = Filter == FindingFilter.History ? [] : all.Where(f => f.Status == status).ToList();
        EmptyText = shown.Count > 0 ? "" : Filter switch
        {
            FindingFilter.History => "",
            FindingFilter.Ignored => L.T("Non stai ignorando nessun problema.", "You are not ignoring any problem."),
            FindingFilter.Resolved => L.T("Nessun problema si è ancora risolto da solo.", "No problem has solved itself yet."),
            _ => L.T("Nessun problema aperto.", "No open problems."),
        };
        // Rebuilding the list resets scrolling, so it is done only when something visible changed.
        var signature = string.Join('|', shown.Select(f => $"{f.Id}:{f.Severity}:{f.Title}:{f.Detail}:{FixLabel(f)}:{f.LastSeen.ToUnixTimeSeconds() / 60}"));
        if (signature != _findingsShown || Findings.Count != shown.Count)
        {
            _findingsShown = signature;
            Findings.Clear();
            foreach (var f in shown) Findings.Add(new FindingItem(f, ModuleName(f.Module), Now, FixLabel(f)));
            // Grouped by area, in a fixed order: performance first, reminders last, anything unknown after.
            FindingSections.Clear();
            foreach (var g in Findings.GroupBy(i => i.Area)
                         .OrderBy(g => FindingArea.Order.ToList().IndexOf(g.Key) is var i && i >= 0 ? i : int.MaxValue)
                         .ThenBy(g => g.Key))
                FindingSections.Add(new FindingSection(g.Key, [.. g]));
        }

        // The history lives inside the Problems page: it is where problems come from.
        if (Page == PanelPage.History || (Page == PanelPage.Problems && Filter == FindingFilter.History)) RefreshHistory();
        if (Page == PanelPage.Actions) RefreshActionLog();
        RefreshActivities();
        if (Page == PanelPage.View && SelectedView is { } tab)
        {
            if (tab.IsForm)
            {
                if (tab.Form is null && Modules.FirstOrDefault(m => m.Id == tab.ModuleId) is { } item)
                    tab.OpenForm(item, () => [.. _readLive().Select(e => new LiveValue(e.Key, e.Label, e.Group, e.Value, e.Unit, e.Stress, e.Missing, e.MissingFix))]);
                else tab.RefreshPreview(); // The live values moved.
            }
            if (tab.UsesButtonsPage) tab.LoadButtons(_runner.SettingsOf(tab.Module));
            if (!tab.IsButtons && !tab.IsForm) tab.Load(_store.QueryEvents(Now.AddDays(-tab.View.Days), Now, tab.ModuleId, tab.View.EventKind, limit: 500));
        }
        if (Page == PanelPage.Report) RefreshReport();
        if (Page == PanelPage.Intro) RefreshIntro();
        if (Page == PanelPage.Monitor)
        {
            RefreshHome(open);
            RefreshMonitor();
        }
        if (Page == PanelPage.Modules)
            foreach (var m in Modules) m.RefreshStatus(Now);
    }

    private void RefreshMonitor()
    {
        // A value with no label is meant for other parts of OptLife (which process is the game), not for people.
        var all = _readLive().Where(e => e.Label.Length > 0).ToList();
        // A value this PC cannot read is not a tile with a made-up number: it is a notice with why and, when there is one, the button that helps.
        var notices = all.Where(e => e.IsMissing).GroupBy(e => e.Missing!)
            .Select(g => new MonitorNotice(string.Join(", ", g.Select(e => $"{e.Group} · {e.Label}")), g.Key, g.First().Module, g.First().MissingFix))
            .ToList();
        if (!notices.Select(n => n.Signature).SequenceEqual(MonitorNotices.Select(n => n.Signature)))
        {
            MonitorNotices.Clear();
            foreach (var n in notices) MonitorNotices.Add(n);
        }
        var live = all.Where(e => !e.IsMissing).ToList();
        MonitorEmptyText = live.Count > 0 || notices.Count > 0
            ? ""
            : L.T("Nessun valore dal vivo: l'agente non è attivo oppure il modulo Monitoraggio è spento.", "No live values: the agent is not running or the Monitoring module is off.");

        // Tiles are rebuilt only when the set of sensors changes; otherwise just their numbers move.
        if (!live.Select(e => e.Key).ToHashSet().SetEquals(_tiles.Keys))
        {
            _tiles.Clear();
            MonitorGroups.Clear();
            foreach (var group in live.GroupBy(e => e.Group))
            {
                var tiles = group.Select(e => _tiles[e.Key] = new MonitorTile(e.Key, e.Label, e.Unit, group.Key)).ToList();
                MonitorGroups.Add(new MonitorGroup(group.Key, tiles));
            }
            _chartsLoadedAt = DateTimeOffset.MinValue;
            _featuredShown = "";
        }
        foreach (var e in live) _tiles[e.Key].Update(LiveEntry.FormatNumber(e.Value, e.Unit), e.Stress);
        foreach (var tile in _tiles.Values) tile.IsPinned = _pinned.Contains(tile.Key);

        // Featured: what the person pinned, in their order; with nothing pinned, the most strained right now.
        // Rebuilt only when the selection changes, so the tiles do not jump around while their numbers move.
        var pinned = _pinned.Where(_tiles.ContainsKey).Select(k => _tiles[k]).ToList();
        var chosen = pinned.Count > 0
            ? pinned
            : [.. _tiles.Values.Where(t => t.Stress is not null).OrderByDescending(t => t.Stress >= 0.8 ? t.Stress : 0).ThenBy(t => t.Key).Take(FeaturedCount)];
        // Among the calm ones the order is fixed (by name), so only a value crossing into "strained" reshuffles them.
        FeaturedTitle = pinned.Count > 0 ? L.T("IN EVIDENZA · SCELTI DA TE", "FEATURED · CHOSEN BY YOU") : chosen.Any(t => t.Level != "ok") ? L.T("IN EVIDENZA · I PIÙ SOTTO SFORZO", "FEATURED · MOST STRAINED") : L.T("IN EVIDENZA", "FEATURED");
        var selection = string.Join('|', chosen.Select(t => t.Key));
        if (selection != _featuredShown)
        {
            _featuredShown = selection;
            Featured.Clear();
            foreach (var tile in chosen) Featured.Add(tile);
        }

        // The little charts show the last hour, one point per minute: no need to reload them every tick.
        if (Now - _chartsLoadedAt < TimeSpan.FromSeconds(30)) return;
        _chartsLoadedAt = Now;
        foreach (var tile in _tiles.Values)
            tile.History = [.. _store.QuerySamples(tile.Key, Now.AddHours(-1), Now).Select(s => s.Value)];
    }

    private void RefreshHistory()
    {
        var events = _store.QueryEvents(Now.AddDays(-HistoryDays), Now, limit: 2000);
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var shown = events
            .Where(e => words.All(w =>
                e.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                e.Kind.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                e.Data.Values.Any(v => v.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        HistorySummary = shown.Count switch
        {
            0 => L.T("Nessun evento nel periodo.", "No events in the period."),
            1 => "1 evento",
            _ => $"{shown.Count} eventi",
        };
        var signature = $"{HistoryDays}|{Search}|{shown.Count}|{shown.FirstOrDefault()?.Id}";
        if (signature == _eventsShown) return;
        _eventsShown = signature;
        Events.Clear();
        foreach (var e in shown) Events.Add(new EventItem(e, ModuleName(e.Module)));
    }

    /// <summary>The module and action behind a problem's fix, when both still exist.</summary>
    private (IModule Module, ModuleAction Action)? FindFix(StoredFinding f)
    {
        if (f.Fix is null || _modules.FirstOrDefault(m => m.Info.Id == f.Module) is not { } module) return null;
        return module.Actions.FirstOrDefault(a => a.Id == f.Fix.ActionId) is { } action ? (module, action) : null;
    }

    /// <summary>The button a problem gets: none when it has no fix, or when the person set that action to "never".</summary>
    private string? FixLabel(StoredFinding f) =>
        FindFix(f) is { } fix && _runner.PermissionOf(fix.Module, fix.Action) != Permission.Never ? f.Fix!.Label : null;

    public ObservableCollection<ReportLine> ReportLines { get; } = [];
    public ObservableCollection<Core.Reporting.ReportFigure> ReportFigures { get; } = [];

    [ObservableProperty] private int _reportDays = 7;

    partial void OnReportDaysChanged(int value) => Refresh();

    [RelayCommand]
    private void SetReportDays(string days) => ReportDays = int.Parse(days);

    private void RefreshReport()
    {
        var report = Core.Reporting.Report.Build(_store, ModuleName,
            (module, action) => _modules.FirstOrDefault(m => m.Info.Id == module)?.Actions.FirstOrDefault(a => a.Id == action)?.Name ?? action,
            Now.AddDays(-ReportDays), Now);
        var signature = report.ToText();
        if (signature == _reportShown) return;
        _reportShown = signature;
        ReportFigures.Clear();
        foreach (var figure in report.Figures) ReportFigures.Add(figure);
        ReportLines.Clear();
        foreach (var section in report.Sections)
        {
            ReportLines.Add(new ReportLine(section.Title.ToUpper(Display.Lang), IsHeader: true));
            foreach (var line in section.Lines) ReportLines.Add(new ReportLine(line, IsHeader: false));
        }
    }

    private void RefreshActionLog()
    {
        var entries = _store.GetActions(200);
        ActionLogEmptyText = entries.Count > 0 ? "" : L.T("Nessuna azione eseguita finora. Qui compare tutto ciò che viene cambiato sul PC, da te o da OptLife.", "No actions run so far. Everything changed on the PC, by you or by OptLife, shows up here.");
        if (entries.Count == ActionLog.Count && (entries.Count == 0 || entries[0].Time.ToString("o") + entries[0].Detail == _actionsShown)) return;
        _actionsShown = entries.Count == 0 ? "" : entries[0].Time.ToString("o") + entries[0].Detail;
        ActionLog.Clear();
        foreach (var e in entries)
        {
            var module = _modules.FirstOrDefault(m => m.Info.Id == e.Module);
            ActionLog.Add(new ActionLogItem(e, ModuleName(e.Module), module?.Actions.FirstOrDefault(a => a.Id == e.Action)?.Name ?? e.Action));
        }
    }

    private string DescribeAgent()
    {
        if (!_agentRunning()) return L.T("L'agente non è attivo: i controlli non girano da soli.", "The agent is not running: checks do not run by themselves.");
        var last = _modules
            .Where(_runner.IsEnabled)
            .SelectMany(m => m.Tasks.Select(t => _runner.ReadStatus(m, t).LastRun))
            .Max();
        return last is null
            ? L.T("Agente attivo · nessun controllo ancora eseguito", "Agent running · no check run yet")
            : L.T("Agente attivo · ultimo controllo ", "Agent running · last check ") + Display.When(last.Value, Now);
    }

    private string ModuleName(string id) =>
        id == ModuleRunner.CoreModuleId ? "OptLife" : _modules.FirstOrDefault(m => m.Info.Id == id)?.Info.Name ?? id;

    [RelayCommand]
    private void Show(string page) => Page = Enum.Parse<PanelPage>(page);

    [RelayCommand]
    private void SetFilter(string filter) => Filter = Enum.Parse<FindingFilter>(filter);

    [RelayCommand]
    private void SetMode(string mode) => ExpertMode = mode == "expert";

    [RelayCommand]
    private void SetHistoryDays(string days) => HistoryDays = int.Parse(days);

    [RelayCommand]
    private void Ignore(FindingItem item)
    {
        _store.SetFindingStatus(item.Id, FindingStatus.Ignored);
        Refresh();
    }

    [RelayCommand]
    private void Solve(FindingItem item)
    {
        _store.SolveFinding(item.Id, Now);
        Refresh();
    }

    [RelayCommand]
    private void Reopen(FindingItem item)
    {
        _store.SetFindingStatus(item.Id, FindingStatus.Open);
        Refresh();
    }

    /// <summary>
    /// The "put it right" button of a problem. Pressing it is the person's own decision, so an action
    /// set to "ask first" goes ahead; afterwards the module looks again, and the problem leaves the
    /// list only if it is really gone.
    /// </summary>
    [RelayCommand]
    private async Task FixAsync(FindingItem item)
    {
        var finding = _store.GetFindings(includeClosed: true).FirstOrDefault(f => f.Id == item.Id);
        if (finding is null || FindFix(finding) is not { } fix || IsChecking) return;
        IsChecking = true;
        var ok = false;
        try
        {
            var result = await Task.Run(() => _runner.RunActionAsync(fix.Module, fix.Action, "pannello", confirmed: true, finding.Fix!.Argument, CancellationToken.None));
            (Notice, NoticeIsError) = ((result.Ok ? L.T("Fatto. ", "Done. ") : L.T("Non riuscito. ", "Failed. ")) + result.Detail, !result.Ok);
            ok = result.Ok;
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
        // The module looks again behind, and the panel stays free meanwhile: a full look can take minutes (the Cleanup weighs whole
        // drives when asked), and on 23 Nov 2026 every other button stayed dead until it ended.
        if (ok) Rechecking = RecheckAsync(fix.Module);
    }

    /// <summary>The look that follows a fix, running behind; the tests wait for it.</summary>
    internal Task Rechecking { get; private set; } = Task.CompletedTask;

    private async Task RecheckAsync(IModule module)
    {
        foreach (var task in module.Tasks.Where(t => !t.Continuous))
            await Task.Run(() => _runner.RunTaskAsync(module, task, CancellationToken.None, onDemand: true));
        Refresh();
    }

    /// <summary>
    /// A button's answer (the list to tick, what it did) is written at the top of the page: the window scrolls there, or a button
    /// pressed far down the page looks like it did nothing (on 24 Nov the Hub's SEE AND APPLY did, and APPLY was pressed instead).
    /// </summary>
    public event Action? PageTopNeeded;

    /// <summary>
    /// A button of a module's page. Pressing it is the confirmation; an action that needs a pick first
    /// opens its list instead, and runs only from <see cref="ConfirmChoicesCommand"/>.
    /// </summary>
    [RelayCommand]
    private async Task PressAsync(ButtonItem item)
    {
        try { await PressInnerAsync(item); }
        finally { PageTopNeeded?.Invoke(); }
    }

    private async Task PressInnerAsync(ButtonItem item)
    {
        if (SelectedView is not { } tab || IsChecking) return;
        if (tab.Module.Actions.FirstOrDefault(a => a.Id == item.Button.ActionId) is not { } action)
        {
            (tab.Notice, tab.NoticeIsError) = (L.T("Questa azione non esiste più.", "This action no longer exists."), true);
            return;
        }
        if (_runner.PermissionOf(tab.Module, action) == Permission.Never)
        {
            (tab.Notice, tab.NoticeIsError) = (L.T($"\"{action.Name}\" è su \"mai\" in Impostazioni.", $"\"{action.Name}\" is set to \"never\" in Settings."), true);
            return;
        }
        if (action.Choices is not null)
        {
            IsChecking = true;
            string? error = null;
            var choices = await Task.Run(() => _runner.ChoicesOf(tab.Module, action, out error));
            IsChecking = false;
            if (choices.Count == 0)
            {
                (tab.Notice, tab.NoticeIsError) = (error is null ? L.T("Niente da scegliere: non c'è nulla su cui agire.", "Nothing to choose: there is nothing to act on.") : L.T("Non riesco a leggere l'elenco: ", "Cannot read the list: ") + error, error is not null);
                return;
            }
            tab.Pending = item;
            tab.ChoicesTitle = action.Name;
            tab.ChoicesHint = action.Description;
            tab.ChoicesConfirm = action.Name.ToUpper(L.Culture);
            tab.Choices = [.. choices.Select(c => new ChoiceItem(c))];
            tab.Notice = "";
            return;
        }
        await RunButtonAsync(tab, action, item.Button.Argument);
    }

    [RelayCommand]
    private async Task ConfirmChoicesAsync()
    {
        if (SelectedView is not { Pending: { } item } tab || IsChecking) return;
        var picked = string.Join(';', tab.Choices.Where(c => c.IsChecked).Select(c => c.Id));
        (tab.Choices, tab.Pending) = ([], null);
        if (tab.Module.Actions.FirstOrDefault(a => a.Id == item.Button.ActionId) is not { } action) return;
        if (picked.Length == 0)
        {
            (tab.Notice, tab.NoticeIsError) = (L.T("Nessuna voce spuntata: non ho fatto niente.", "Nothing ticked: I did nothing."), false);
            return;
        }
        await RunButtonAsync(tab, action, picked);
    }

    [RelayCommand]
    private void CancelChoices()
    {
        if (SelectedView is not { } tab) return;
        (tab.Choices, tab.Pending, tab.Notice) = ([], null, "");
    }

    private async Task RunButtonAsync(ViewTab tab, ModuleAction action, string? argument)
    {
        IsChecking = true;
        tab.Notice = L.T("In corso…", "In progress…");
        try
        {
            var result = await Task.Run(() => _runner.RunActionAsync(tab.Module, action, "pannello", confirmed: true, argument, CancellationToken.None));
            (tab.Notice, tab.NoticeIsError) = ((result.Ok ? L.T("Fatto. ", "Done. ") : L.T("Non riuscito. ", "Failed. ")) + result.Detail, !result.Ok);
        }
        finally
        {
            IsChecking = false;
            // The module rewrote its page after the action: show it now, not at the next tick.
            Refresh();
        }
    }

    /// <summary>"Check now" on a module's page: that module only, here and now, then the page is read again.</summary>
    [RelayCommand]
    private async Task RecheckViewAsync()
    {
        if (SelectedView is not { } tab || IsChecking) return;
        IsChecking = true;
        tab.Notice = L.T("Controllo in corso…", "Checking…");
        try
        {
            var ok = true;
            foreach (var task in tab.Module.Tasks.Where(t => !t.Continuous))
                ok &= await Task.Run(() => _runner.RunTaskAsync(tab.Module, task, CancellationToken.None, onDemand: true));
            (tab.Notice, tab.NoticeIsError) = ok ? (L.T("Controllato ora.", "Checked just now."), false) : (L.T("Il controllo non è riuscito: guarda la pagina Moduli.", "The check failed: see the Modules page."), true);
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            if (_requestRunNow())
            {
                await Task.Delay(AgentGrace);
            }
            else
            {
                // No agent to ask: run the checks here, off the interface thread.
                foreach (var module in _modules.Where(_runner.IsEnabled))
                    foreach (var task in module.Tasks.Where(t => !t.Continuous))
                        await Task.Run(() => _runner.RunTaskAsync(module, task, CancellationToken.None, onDemand: true));
            }
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
    }
}
