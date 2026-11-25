using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Core.Storage;

namespace OptLife.Panel.ViewModels;

internal static class Display
{
    public static CultureInfo Lang => L.Culture;

    /// <summary>The colour family a severity is drawn with; the theme decides the actual colours.</summary>
    public static string Level(Severity s) => s switch
    {
        Severity.High => "high",
        Severity.Medium => "medium",
        Severity.Low => "low",
        _ => "info",
    };

    public static string Label(Severity s) => s switch
    {
        // Words that say what to do, not a scale to decode.
        Severity.High => L.T("IMPORTANTE", "IMPORTANT"),
        Severity.Medium => L.T("DA SISTEMARE", "TO FIX"),
        Severity.Low => L.T("DA GUARDARE", "WORTH A LOOK"),
        _ => L.T("DA SAPERE", "GOOD TO KNOW"),
    };

    public static string When(DateTimeOffset t, DateTimeOffset now)
    {
        var local = t.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date) return L.T("oggi ", "today ") + local.ToString("HH:mm", Lang);
        if (local.Date == now.ToLocalTime().Date.AddDays(-1)) return L.T("ieri ", "yesterday ") + local.ToString("HH:mm", Lang);
        return local.ToString("d MMM HH:mm", Lang);
    }
}

/// <summary>A button of a choice an action stopped at ("Tieni quello della Hub"): the module's action with its argument.</summary>
public sealed record NeedButton(string Module, FindingFix Fix, long? FindingId)
{
    public string Label => Fix.Label.ToUpper(Display.Lang);
}

/// <summary>
/// What pressing a button did, kept by the panel apart from the cards (which are rebuilt at every refresh): working, done, nothing
/// to do, failed, or a choice with its buttons. Every press ends with one of these, also the ones that take no time.
/// </summary>
public sealed partial class ActionFeedback : ObservableObject
{
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _text = "";

    /// <summary>The button is off while its action runs: pressed three times, it would run three times.</summary>
    public bool Idle => !Busy;
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(Idle));

    /// <summary>The colour family of the icon: "good" done, "info" nothing to do, "medium" failed or waiting for a choice.</summary>
    [ObservableProperty] private string _level = "info";

    public System.Collections.ObjectModel.ObservableCollection<NeedButton> Needs { get; } = [];
    public bool HasNeeds => Needs.Count > 0;

    public ActionFeedback() => Needs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNeeds));

    public static ActionFeedback Working(string what) => new() { Busy = true, Text = L.T($"In corso: {what}…", $"Working: {what}…") };

    /// <summary>The result in words that say which of the four it is.</summary>
    public void Show(ActionResult result, string module, long? findingId)
    {
        Busy = false;
        (Text, Level) = result.Outcome switch
        {
            ActionOutcome.Done => (L.T("Fatto. ", "Done. ") + result.Detail, "good"),
            ActionOutcome.Nothing => (L.T("Niente da fare: ", "Nothing to do: ") + result.Detail, "info"),
            ActionOutcome.Choice => (L.T("Serve una tua scelta. ", "Your choice is needed. ") + result.Detail, "medium"),
            _ => (L.T("Non riuscito. ", "Failed. ") + result.Detail, "medium"),
        };
        Needs.Clear();
        foreach (var fix in result.Needs ?? []) Needs.Add(new NeedButton(module, fix, findingId));
    }
}

/// <param name="fixLabel">What the "put it right" button says; null when the problem has no fix or the person forbade it.</param>
/// <param name="feedback">What its button did last, when it was pressed in this panel.</param>
public sealed partial class FindingItem(StoredFinding f, string moduleName, DateTimeOffset now, string? fixLabel = null, ActionFeedback? feedback = null) : ObservableObject
{
    public long Id => f.Id;
    public string ModuleId => f.Module;
    public ActionFeedback? Feedback => feedback;
    public bool HasFeedback => feedback is not null;

    /// <summary>The section it is listed under; problems stored before areas existed fall under their module's name.</summary>
    public string Area => f.Area ?? moduleName;
    public FindingFix? Fix => f.Fix;
    public bool HasFix => fixLabel is not null && IsOpen;
    public string? FixLabel => fixLabel?.ToUpper(Display.Lang);
    public string Title => f.Title;
    public string Detail => f.Detail;

    /// <summary>
    /// A long detail (rule names, update titles, numbers) is technical: its first sentence stays in sight, the rest opens under
    /// "Details" so what happened and what to do come first.
    /// </summary>
    public string DetailShort => HasMore ? FirstSentence(f.Detail) : f.Detail;
    public bool HasMore => f.Detail.Length > 160 && FirstSentence(f.Detail).Length < f.Detail.Length;
    [ObservableProperty] private bool _showDetail;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleDetail() => ShowDetail = !ShowDetail;

    private static string FirstSentence(string text) => text.IndexOf(". ", StringComparison.Ordinal) is var i and > 0 and < 160 ? text[..(i + 1)] : text.Length > 140 ? text[..140].TrimEnd() + "…" : text;
    public string? Advice => f.Advice;
    public bool HasAdvice => !string.IsNullOrWhiteSpace(f.Advice);
    public string Level => Display.Level(f.Severity);
    public string SeverityLabel => Display.Label(f.Severity);
    public string AccessibleName => $"{SeverityLabel}: {Title}";
    public bool IsOpen => f.Status == FindingStatus.Open;

    /// <summary>"Risolto": the cause is fixed; it closes now and comes back only if it happens again.</summary>
    public bool CanSolve => IsOpen && f.CanBeSolved;
    public bool IsIgnored => f.Status == FindingStatus.Ignored;
    public string Footer => $"{moduleName} · {L.T("visto", "seen")} {Display.When(f.LastSeen, now)} · {L.T("dal", "since")} {f.FirstSeen.ToLocalTime().ToString("d MMM", Display.Lang)}";

    /// <summary>
    /// The problem as text to paste into a chat with Claude: everything on the card plus what is needed to find it
    /// again (number, module, key) and the command that reads its history.
    /// </summary>
    public string ForClipboard()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"[OptLife] {L.T("Problema", "Problem")} #{f.Id} · {SeverityLabel} · {Area} · {f.Status}");
        text.AppendLine(f.Title);
        text.AppendLine(f.Detail);
        if (HasAdvice) text.AppendLine(L.T("Consiglio: ", "Advice: ") + f.Advice);
        text.AppendLine($"{L.T("Modulo", "Module")}: {moduleName} ({f.Module}) · {L.T("chiave", "key")}: {f.Key}");
        text.AppendLine($"{L.T("Primo", "First")}: {f.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm} · {L.T("ultimo", "last")}: {f.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm}");
        text.Append($"{L.T("Per leggerlo", "To read it")}: optlife findings --all --json · optlife events --since 7d");
        return text.ToString();
    }
}

public sealed class EventItem(StoredEvent e, string moduleName)
{
    public string Time => e.Time.ToLocalTime().ToString("dd/MM HH:mm", Display.Lang);
    public string Title => e.Title;
    public string Level => Display.Level(e.Severity);
    public string LevelWord => Display.Label(e.Severity);
    public string Source => $"{moduleName} · {e.Kind}";
}

/// <summary>One line of the weekly report: a section title, or something that happened.</summary>
public sealed record ReportLine(string Text, bool IsHeader);

/// <param name="Width">In pixels; NaN lets the cell take what it needs.</param>
public sealed record ViewCell(string Text, double Width, bool Right)
{
    /// <summary>Numbers line up on the right, words on the left.</summary>
    public string Align => Right ? "Right" : "Left";
}

/// <summary>A page a module asked for: its events of one kind as a table. The panel knows nothing else about it.</summary>
public sealed partial class ViewTab(IModule module, ModuleView view) : ObservableObject
{
    public string ModuleId => module.Info.Id;
    public ModuleView View => view;
    public string Title => view.Title.ToUpper(Display.Lang);
    public string Description => view.Description;
    public IReadOnlyList<ViewCell> Header { get; } = [.. view.Columns.Select(c => new ViewCell(c.Label.ToUpper(Display.Lang), WidthOf(c), IsRight(c)))];

    [ObservableProperty] private IReadOnlyList<IReadOnlyList<ViewCell>> _rows = [];
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isSelected;

    public bool IsTable => view.Layout == ViewLayout.Table;
    public bool IsChecklist => view.Layout == ViewLayout.Checklist;
    public bool IsButtons => view.Layout == ViewLayout.Buttons;
    public bool IsForm => view.Layout == ViewLayout.Form;
    public bool IsGallery => view.Layout == ViewLayout.Gallery;

    // ── A gallery: cards with a picture, a search and a filter by group ──

    private IReadOnlyList<CheckItem> _galleryItems = [];
    private string _galleryShown = "";

    /// <summary>The groups to filter by, "all" first; the chosen one is <see cref="GalleryFilter"/> ("" = all).</summary>
    [ObservableProperty] private IReadOnlyList<GalleryFilterItem> _galleryFilters = [];
    [ObservableProperty] private string _galleryFilter = "";
    [ObservableProperty] private string _gallerySearch = "";
    [ObservableProperty] private IReadOnlyList<GallerySection> _gallerySections = [];
    [ObservableProperty] private string _galleryCount = "";

    partial void OnGalleryFilterChanged(string value) => ShowGallery();
    partial void OnGalleryFiltersChanged(IReadOnlyList<GalleryFilterItem> value) => OnPropertyChanged(nameof(HasGalleryFilters));
    public bool HasGalleryFilters => GalleryFilters.Count > 0;
    partial void OnGallerySearchChanged(string value) => ShowGallery();

    /// <summary>How the grid is ordered: "name", "used" (last played first), "added" (last installed first).</summary>
    [ObservableProperty] private string _gallerySort = "added";
    partial void OnGallerySortChanged(string value) => ShowGallery();
    public bool CanSortGallery => _galleryItems.Any(i => i.Used is not null || i.Added is not null);

    [RelayCommand]
    private void SortGallery(string how) => GallerySort = how;

    /// <summary>The cards after filter and search, in the module's order (the full-screen view builds its rows from them).</summary>
    [ObservableProperty] private IReadOnlyList<CheckItem> _shownItems = [];

    /// <summary>The filter as a drop-down choice.</summary>
    [ObservableProperty] private GalleryFilterItem? _galleryFilterChoice;
    partial void OnGalleryFilterChoiceChanged(GalleryFilterItem? value)
    {
        if (value is not null && value.Key != GalleryFilter) GalleryFilter = value.Key;
    }

    [RelayCommand]
    private void FilterGallery(string group) => GalleryFilter = group;

    private void LoadGallery(StoredEvent? latest)
    {
        var signature = latest is null ? "" : $"{latest.Id}";
        if (signature == _galleryShown) return;
        _galleryShown = signature;
        _galleryItems = latest is null ? [] : CheckItem.FromData(latest.Data);
        var groups = _galleryItems.Select(i => i.Group).Distinct().ToList();
        // Families first (PC, Consoles) when the cards have more than one; then each group (a launcher, a console).
        var kinds = _galleryItems.Select(i => i.Kind).OfType<string>().Distinct().ToList();
        _galleryKinds = kinds.Count > 1 ? kinds : [];
        if (GalleryFilter.Length > 0 && !groups.Contains(GalleryFilter) && !_galleryKinds.Any(k => KindKey(k) == GalleryFilter)) GalleryFilter = "";
        _galleryGroups = groups;
        OnPropertyChanged(nameof(CanSortGallery));
        ShowGallery();
        if (latest is not null && _galleryItems.Count == 0) EmptyText = view.EmptyText;
    }

    private List<string> _galleryGroups = [];
    private List<string> _galleryKinds = [];
    private static string KindKey(string kind) => "kind:" + kind;

    private bool InFilter(CheckItem i) =>
        GalleryFilter.Length == 0 || i.Group == GalleryFilter || (i.Kind is { } k && KindKey(k) == GalleryFilter);

    private void ShowGallery()
    {
        GalleryFilters = _galleryGroups.Count > 1
            ? [new GalleryFilterItem("", L.T("Tutti", "All"), GalleryFilter.Length == 0),
               .. _galleryKinds.Select(k => new GalleryFilterItem(KindKey(k), k, KindKey(k) == GalleryFilter)),
               .. _galleryGroups.Select(g => new GalleryFilterItem(g, g, g == GalleryFilter))]
            : [];
        var words = PanelViewModel.Words(GallerySearch);
        bool Matches(CheckItem i) => words.Length == 0 || PanelViewModel.Matches(words, $"{i.Label} {i.Value} {i.Kind}");
        var shown = _galleryItems.Where(i => InFilter(i) && Matches(i)).ToList();
        ShownItems = shown;
        // One grid, as a store library does: by name, by last used, or by last added; groups stay as filters.
        IEnumerable<CheckItem> ordered = GallerySort switch
        {
            "used" => shown.OrderByDescending(i => i.Used ?? DateTimeOffset.MinValue).ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase),
            "added" => shown.OrderByDescending(i => i.Added ?? DateTimeOffset.MinValue).ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase),
        };
        GallerySections = shown.Count == 0 ? [] : [new GallerySection("", [.. ordered.Select(i => new GalleryCard(i))])];
        GalleryFilterChoice = GalleryFilters.FirstOrDefault(f => f.IsChosen);
        var distinct = shown.Count;
        GalleryCount = words.Length > 0 || GalleryFilter.Length > 0
            ? L.T($"{distinct} di {_galleryItems.Count}", $"{distinct} of {_galleryItems.Count}")
            : L.T($"{distinct} in tutto", $"{distinct} in all");
    }

    // ── A Form page: the module's settings, and the live picture its module draws of them ──

    /// <summary>The module's settings on this page (the same ones as in Settings); set when the page opens.</summary>
    [ObservableProperty] private ModuleItem? _form;
    private Func<IReadOnlyList<LiveValue>> _live = () => [];

    partial void OnFormChanged(ModuleItem? oldValue, ModuleItem? newValue)
    {
        if (oldValue is not null) oldValue.Edited -= RefreshPreview;
        if (newValue is not null) newValue.Edited += RefreshPreview;
    }

    public void OpenForm(ModuleItem item, Func<IReadOnlyList<LiveValue>> live)
    {
        _live = live;
        Form = item;
        RefreshPreview();
    }

    [ObservableProperty] private byte[]? _previewPng;
    [ObservableProperty] private double _previewLeft;
    [ObservableProperty] private double _previewTop;
    [ObservableProperty] private int _screenWidth = 1920;
    [ObservableProperty] private int _screenHeight = 1080;
    [ObservableProperty] private string _previewText = "";
    public bool HasPreview => PreviewPng is not null;
    partial void OnPreviewPngChanged(byte[]? value) => OnPropertyChanged(nameof(HasPreview));
    private ViewPreview? _preview;

    /// <summary>Draws the picture again: a setting changed on screen, or the live values moved.</summary>
    public void RefreshPreview()
    {
        if (view.Preview is null || Form is null) return;
        try
        {
            _preview = view.Preview(Form.CurrentSettings(), _live());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _preview = null;
            PreviewText = L.T("Anteprima non disponibile: ", "Preview not available: ") + ex.Message;
        }
        if (_preview is not { } p)
        {
            PreviewPng = null;
            return;
        }
        (ScreenWidth, ScreenHeight) = (p.ScreenWidth, p.ScreenHeight);
        (PreviewLeft, PreviewTop) = (p.X * p.ScreenWidth, p.Y * p.ScreenHeight);
        PreviewText = p.Description;
        PreviewNotice = p.Notice ?? "";
        PreviewPng = p.Png;
    }

    /// <summary>What in the picture cannot work on this PC, and why (a value it cannot read); empty when all is well.</summary>
    [ObservableProperty] private string _previewNotice = "";

    /// <summary>The picture dragged (or moved with the arrows) to this spot, as fractions of the screen: the module's position settings follow.</summary>
    public void MovePreview(double x, double y)
    {
        if (_preview is not { DragX: { } dragX, DragY: { } dragY } p || Form is null) return;
        foreach (var (key, value) in p.OnDrag ?? new Dictionary<string, string>()) Form.Set(key, value);
        Form.Set(dragX, Math.Round(Math.Clamp(x, 0, 1) * 100).ToString(CultureInfo.InvariantCulture));
        Form.Set(dragY, Math.Round(Math.Clamp(y, 0, 1) * 100).ToString(CultureInfo.InvariantCulture));
        RefreshPreview();
    }

    public bool CanDrag => _preview?.DragX is not null;
    public IModule Module => module;

    /// <summary>For a Buttons page: the buttons, grouped, as the module's settings make them now.</summary>
    [ObservableProperty] private IReadOnlyList<ButtonGroup> _buttonGroups = [];

    /// <summary>What the last button did.</summary>
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private bool _noticeIsError;

    /// <summary>The last button's outcome, with the buttons of a choice when it stopped at one.</summary>
    [ObservableProperty] private ActionFeedback? _feedback;

    /// <summary>The list to tick before an action runs; empty when none is open.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChoices))] private IReadOnlyList<ChoiceItem> _choices = [];
    public IReadOnlyList<ChoiceGroup> ChoiceGroups { get; private set; } = [];
    partial void OnChoicesChanged(IReadOnlyList<ChoiceItem> value)
    {
        ChoiceGroups = ChoiceGroup.Of(value);
        OnPropertyChanged(nameof(ChoiceGroups));
    }
    [ObservableProperty] private string _choicesTitle = "";
    /// <summary>What the action does with the ticked ones (its description), and the confirm button: both come from the action.</summary>
    [ObservableProperty] private string _choicesHint = "";
    [ObservableProperty] private string _choicesConfirm = "";
    public bool HasChoices => Choices.Count > 0;

    /// <summary>The button whose list is open, to run once the person confirms.</summary>
    public ButtonItem? Pending { get; set; }

    private string _buttonsShown = "";

    public void LoadButtons(ISettings settings)
    {
        var buttons = view.Buttons?.Invoke(settings) ?? [];
        var signature = string.Join('\n', buttons);
        if (signature == _buttonsShown) return;
        _buttonsShown = signature;
        ButtonGroups = [.. buttons.GroupBy(b => b.Group).Select(g => new ButtonGroup(g.Key.ToUpper(Display.Lang), [.. g.Select(b => new ButtonItem(b))]))];
        if (IsButtons) EmptyText = buttons.Count > 0 ? "" : view.EmptyText;
    }

    /// <summary>The buttons page holds the list to tick; a checklist uses it too, with its buttons above the lines.</summary>
    public bool UsesButtonsPage => IsButtons || IsChecklist || IsGallery;

    // ── Checklist order: as the module wrote it, by kind, or most important first ──
    private IReadOnlyList<CheckItem> _checkItems = [];
    private readonly Dictionary<string, bool> _openGroups = [];
    public bool GroupsCanClose => view.GroupsStartClosed;
    [ObservableProperty] private string _checkSort = "order";
    public bool CanSortChecks => _checkItems.Any(i => i.Kind is not null);
    partial void OnCheckSortChanged(string value) => GroupChecks();

    [RelayCommand]
    private void SortChecks(string how) => CheckSort = how;

    private void GroupChecks()
    {
        static int Rank(CheckStatus s) => s switch { CheckStatus.Wrong => 0, CheckStatus.Watch => 1, CheckStatus.Info => 2, _ => 3 };
        static string RankName(CheckStatus s) => s switch
        {
            CheckStatus.Wrong => L.T("Da sistemare", "To fix"),
            CheckStatus.Watch => L.T("Da guardare", "Worth a look"),
            CheckStatus.Info => L.T("Facoltative e informazioni", "Optional and information"),
            _ => L.T("A posto", "Done"),
        };
        var groups = CheckSort switch
        {
            "kind" => _checkItems.GroupBy(i => i.Kind ?? i.Group),
            "importance" => _checkItems.OrderBy(i => Rank(i.Status)).GroupBy(i => RankName(i.Status)),
            _ => _checkItems.GroupBy(i => i.Group),
        };
        CheckGroups = [.. groups.Select(g => new CheckGroup(g.Key, [.. g.Select(i => new CheckRow(i))],
            _openGroups.TryGetValue(g.Key, out var open) ? open : !view.GroupsStartClosed,
            (n, v) => _openGroups[n] = v))];
        OnPropertyChanged(nameof(CanSortChecks));
    }

    /// <summary>For a checklist: its lines, grouped as the module grouped them.</summary>
    [ObservableProperty] private IReadOnlyList<CheckGroup> _checkGroups = [];

    /// <summary>For a checklist: how many lines stand where, and when it was last looked at.</summary>
    [ObservableProperty] private string _checkSummary = "";
    [ObservableProperty] private string _checkLevel = "info";

    public void Load(IReadOnlyList<StoredEvent> events)
    {
        EmptyText = events.Count > 0 ? "" : view.EmptyText.Length > 0 ? view.EmptyText : L.T("Ancora niente da mostrare.", "Nothing to show yet.");
        if (IsChecklist)
        {
            LoadChecklist(events.MaxBy(e => e.Time));
            return;
        }
        if (IsGallery)
        {
            LoadGallery(events.MaxBy(e => e.Time));
            return;
        }
        Rows =[.. events.Select(e => (IReadOnlyList<ViewCell>)[.. view.Columns.Select(c => new ViewCell(Format(c, e), WidthOf(c), IsRight(c)))])];
    }

    private string _checksShown = "";

    private void LoadChecklist(StoredEvent? latest)
    {
        var signature = latest is null ? "" : $"{latest.Id}";
        if (signature == _checksShown) return; // Same list: leave it, so scrolling does not jump.
        _checksShown = signature;
        if (latest is null)
        {
            (CheckGroups, CheckSummary) = ([], "");
            return;
        }
        var items = CheckItem.FromData(latest.Data);
        _checkItems = items;
        GroupChecks();
        // Lines that show a state (StatusText given: on/off) are counted as on and off, not as right and wrong.
        var states = items.Where(i => i.StatusText is not null && i.ValueStatus is not null).ToList();
        var judged = items.Except(states).ToList();
        int Count(CheckStatus s) => judged.Count(i => i.Status == s);
        var parts = new List<string>();
        if (states.Count > 0)
        {
            parts.Add(L.T($"{states.Count(i => i.Status == CheckStatus.Ok)} accesi", $"{states.Count(i => i.Status == CheckStatus.Ok)} on"));
            parts.Add(L.T($"{states.Count(i => i.Status == CheckStatus.Wrong)} spenti", $"{states.Count(i => i.Status == CheckStatus.Wrong)} off"));
        }
        if (states.Count == 0 || Count(CheckStatus.Ok) > 0) parts.Add(L.T($"{Count(CheckStatus.Ok)} a posto", $"{Count(CheckStatus.Ok)} fine"));
        if (Count(CheckStatus.Watch) > 0) parts.Add(L.T($"{Count(CheckStatus.Watch)} da guardare", $"{Count(CheckStatus.Watch)} worth a look"));
        if (Count(CheckStatus.Wrong) > 0) parts.Add(L.T($"{Count(CheckStatus.Wrong)} da sistemare", $"{Count(CheckStatus.Wrong)} to fix"));
        CheckSummary = $"{string.Join(" · ", parts)} · {L.T("aggiornato", "updated")} {latest.Time.ToLocalTime().ToString("d MMM HH:mm", Display.Lang)}";
        CheckLevel = Count(CheckStatus.Wrong) > 0 ? "high" : Count(CheckStatus.Watch) > 0 ? "medium" : states.Count > 0 && judged.Count == 0 ? "info" : "good";
    }

    private static double WidthOf(ViewColumn c) => c.Width > 0 ? c.Width : double.NaN;
    private static bool IsRight(ViewColumn c) => c.Kind is ColumnKind.Number or ColumnKind.Duration;

    private static string Format(ViewColumn c, StoredEvent e)
    {
        var raw = c.Key switch
        {
            "@time" => e.Time.ToLocalTime().ToString("dd/MM HH:mm", Display.Lang),
            "@title" => e.Title,
            _ => e.Data.GetValueOrDefault(c.Key, ""),
        };
        if (raw.Length == 0) return c.Kind == ColumnKind.Text ? "" : "—";
        if (c.Show is not null) raw = c.Show(raw);
        if (c.Kind is ColumnKind.Number or ColumnKind.Duration && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            if (c.Kind == ColumnKind.Number) return n.ToString("0", Display.Lang);
            var seconds = (int)n;
            return seconds < 3600 ? $"{seconds / 60} min" : $"{seconds / 3600} h {seconds % 3600 / 60} min";
        }
        return raw;
    }
}

/// <summary>A heading of a checklist and its lines; it can be closed so a long page shows only what matters.</summary>
public sealed partial class CheckGroup(string name, IReadOnlyList<CheckRow> rows, bool expanded, Action<string, bool> remember) : ObservableObject
{
    public string Name => name;
    public IReadOnlyList<CheckRow> Rows => rows;

    /// <summary>The worst line, for the dot beside a closed heading.</summary>
    public string Level => rows.Any(r => r.Level == "high") ? "high" : rows.Any(r => r.Level == "medium") ? "medium" : rows.Any(r => r.Level == "good") ? "good" : "info";
    public string Summary => string.Join(" · ", rows.Where(r => r.Level != "info").Select(r => $"{r.Label}: {r.ValueHead}").Take(2));

    /// <summary>The worst line in words, for a closed heading and for screen readers.</summary>
    public string LevelWord => Level switch
    {
        "high" => L.T("qualcosa da sistemare", "something to fix"),
        "medium" => L.T("qualcosa da guardare", "something worth a look"),
        "good" => L.T("a posto", "fine"),
        _ => "",
    };
    public string AccessibleName => $"{name}, {rows.Count} {L.T("righe", "lines")}, {LevelWord}".TrimEnd(',', ' ');

    [ObservableProperty] private bool _isExpanded = expanded;
    partial void OnIsExpandedChanged(bool value) => remember(name, value);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

public sealed record ButtonGroup(string Name, IReadOnlyList<ButtonItem> Buttons);

/// <summary>One button of a module's Buttons page.</summary>
public sealed class ButtonItem(ViewButton button)
{
    public ViewButton Button => button;
    public string Label => button.Label;
    public string Description => button.Description;
    public bool HasDescription => button.Description.Length > 0;
    public string ButtonText => button.ButtonText ?? L.T("AVVIA", "RUN");
}

/// <summary>One thing to tick before an action runs.</summary>
public sealed partial class ChoiceItem(ActionChoice choice) : ObservableObject
{
    public string Id => choice.Id;
    public string Label => choice.Label;
    public string Detail => choice.Detail;
    public string Category => choice.Category ?? "";

    /// <summary>The program the picture comes from; the window turns it into an icon, or shows <see cref="Monogram"/>.</summary>
    public string? Icon => choice.Icon;
    public string Monogram => choice.Label.FirstOrDefault(char.IsLetterOrDigit) is var c && c != default ? char.ToUpperInvariant(c).ToString() : "?";

    [ObservableProperty] private bool _isChecked = choice.Checked;
}

/// <summary>A heading of the list to tick, with a box that ticks or unticks all of it.</summary>
public sealed partial class ChoiceGroup : ObservableObject
{
    private bool _syncing;

    public ChoiceGroup(string name, IReadOnlyList<ChoiceItem> items)
    {
        Name = name;
        Items = items;
        foreach (var i in items)
            i.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChoiceItem.IsChecked)) Sync(); };
        Sync();
    }

    public string Name { get; }
    public IReadOnlyList<ChoiceItem> Items { get; }
    public string Header => $"{Name.ToUpper(Display.Lang)}  {Items.Count(i => i.IsChecked)}/{Items.Count}";
    public bool HasName => Name.Length > 0;

    /// <summary>True when all are ticked, false when none, null in between (the box shows a dash).</summary>
    [ObservableProperty] private bool? _allChecked;

    partial void OnAllCheckedChanged(bool? value)
    {
        if (_syncing || value is null) return;
        foreach (var i in Items) i.IsChecked = value.Value;
    }

    private void Sync()
    {
        _syncing = true;
        var n = Items.Count(i => i.IsChecked);
        AllChecked = n == Items.Count ? true : n == 0 ? false : null;
        _syncing = false;
        OnPropertyChanged(nameof(Header));
    }

    /// <summary>The list as headings, in the order the module gave (each heading where its first item was).</summary>
    public static IReadOnlyList<ChoiceGroup> Of(IReadOnlyList<ChoiceItem> items) =>
        [.. items.GroupBy(i => i.Category).Select(g => new ChoiceGroup(g.Key, [.. g]))];
}

/// <summary>One line of a checklist page, with its colour.</summary>
public sealed partial class CheckRow(CheckItem item) : ObservableObject
{
    /// <summary>The second, technical line (publisher, command, path) opens with the row, not by default.</summary>
    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowValueMore));

    /// <summary>A short second line (what an account is, what a program does) is always in sight; a long technical one opens on request.</summary>
    public bool ValueMoreIsShort => ValueMore.Length is > 0 and <= 140 && !ValueMore.Contains('\n');
    public bool ShowValueMore => HasValueMore && (ValueMoreIsShort || IsExpanded);
    public bool HasMoreToggle => HasValueMore && !ValueMoreIsShort;

    public string AccessibleName => $"{Label}: {ValueHead}. {StatusLabel}".Trim(' ', '.');

    /// <summary>What can be done about the line, as buttons of the page (pressing is the confirmation).</summary>
    public IReadOnlyList<ButtonItem> Buttons { get; } = [.. (item.Buttons ?? []).Select(b => new ButtonItem(new ViewButton("", item.Label, "", b.ActionId, b.Argument, b.Text)))];
    public bool HasButtons => Buttons.Count > 0;
    public string Label => item.Label;
    public string Value => item.Value;
    public string? Advice => item.Advice;

    /// <summary>Where to change it matters only when it is not right yet: on a green line it is noise.</summary>
    public bool HasAdvice => item.Status != CheckStatus.Ok && !string.IsNullOrWhiteSpace(item.Advice);

    /// <summary>"dovrebbe: 144 Hz, la massima", shown only when the value is not already the right one.</summary>
    public string Expected => item.Expected is null || item.Status == CheckStatus.Ok ? "" : L.T("dovrebbe: ", "should be: ") + item.Expected;
    public bool HasExpected => Expected.Length > 0;

    public string Level => LevelOf(item.Status);

    /// <summary>The first line of the value, in colour; what follows ("what it is, who made it") is a quieter second line.</summary>
    public string ValueHead => item.Value.Split('\n')[0];
    public string ValueMore => item.Value.Contains('\n') ? item.Value[(item.Value.IndexOf('\n') + 1)..] : "";
    public bool HasValueMore => ValueMore.Length > 0;

    /// <summary>The value's own colour (On green, Off red) when the module gives one; otherwise the line's.</summary>
    public string ValueLevel => item.ValueStatus is { } v ? LevelOf(v) : Level;

    private static string LevelOf(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "good",
        CheckStatus.Watch => "medium",
        CheckStatus.Wrong => "high",
        _ => "info",
    };

    public string StatusLabel => item.StatusText ?? item.Status switch
    {
        CheckStatus.Ok => L.T("A POSTO", "OK"),
        CheckStatus.Watch => L.T("DA GUARDARE", "TO CHECK"),
        CheckStatus.Wrong => L.T("DA SISTEMARE", "TO FIX"),
        _ => "",
    };

    /// <summary>A line that is only information says so with its grey bar; a word "INFO" beside it added nothing.</summary>
    public bool HasStatus => StatusLabel.Length > 0;
}

/// <summary>Problems of one area ("Hardware e prestazioni"), so a crash does not sit next to a full memory.</summary>
public sealed record FindingSection(string Name, IReadOnlyList<FindingItem> Items)
{
    public string Header => $"{Name.ToUpper(Display.Lang)}  {Items.Count}";
}

/// <summary>One thing that was done to the PC, by the person, by OptLife on its own, or from the command line.</summary>
public sealed class ActionLogItem(ActionLogEntry e, string moduleName, string actionName)
{
    public string Time => e.Time.ToLocalTime().ToString("dd/MM HH:mm", Display.Lang);
    public string Title => actionName;
    public string Detail => e.Detail;
    public string Level => e.Outcome switch { ActionOutcome.Done => "ok", ActionOutcome.Nothing => "info", _ => "medium" };
    public string Outcome => e.Outcome switch
    {
        ActionOutcome.Done => L.T("FATTO", "DONE"),
        ActionOutcome.Nothing => L.T("NIENTE DA FARE", "NOTHING TO DO"),
        ActionOutcome.Choice => L.T("SERVE UNA SCELTA", "CHOICE NEEDED"),
        _ => L.T("NON ESEGUITA", "NOT DONE"),
    };
    public string Source => $"{moduleName} · " + e.Actor switch
    {
        ModuleRunner.AutoActor => "da solo",
        "pannello" => "da te",
        "cli" => L.T("da riga di comando", "from the command line"),
        var other => other,
    };
    public string? Undo => e.Undo;
    public bool HasUndo => !string.IsNullOrWhiteSpace(e.Undo);
}

/// <summary>One action of a module with the say the person has over it: run by itself, ask first, or never.</summary>
public sealed partial class ActionItem : ObservableObject
{
    // A property, not a field: the words must follow the language chosen at start-up. Plain sentences, not system terms.
    private static (Permission Value, string Text)[] Choices =>
        [(Permission.Auto, L.T("Fallo da solo", "Do it by itself")), (Permission.Ask, L.T("Chiedimi prima", "Ask me first")), (Permission.Never, L.T("Mai", "Never"))];

    private readonly IModule _module;
    private readonly ModuleAction _action;
    private readonly ConfigStore _config;

    public ActionItem(IModule module, ModuleAction action, ConfigStore config, ModuleRunner runner)
    {
        _module = module;
        _action = action;
        _config = config;
#pragma warning disable MVVMTK0034 // Initial value: assigning the property would write it back to the config.
        _allowed = Choices.First(c => c.Value == runner.PermissionOf(module, action)).Text;
#pragma warning restore MVVMTK0034
    }

    public string Name => _action.Name;
    public string Description => _action.Description;
    /// <summary>An action that removes something is not offered "da solo" at all.</summary>
    public IReadOnlyList<string> Options => [.. Choices.Where(c => !((_action.NeverAuto || _action.Choices is not null) && c.Value == Permission.Auto)).Select(c => c.Text)];

    /// <summary>The say the person has over this action, in the words shown ("da solo", "chiedi prima", "mai").</summary>
    [ObservableProperty]
    private string? _allowed;

    partial void OnAllowedChanged(string? value)
    {
        if (value is null) return; // The list losing its selection for a moment is not a choice.
        var permission = Choices.First(c => c.Text == value).Value;
        _config.UpdateModule(_module.Info.Id, c => c.Permissions[_action.Id] = permission);
    }
}

/// <summary>One setting of a module, as an editable field. The kind decides which editor the panel shows.</summary>
/// <summary>One field of a row of a list setting.</summary>
/// <param name="picks">What can be picked for this field (a live value, a game, a colour); empty: typed.</param>
public sealed partial class ListCell(Action changed, string value, IReadOnlyList<SettingOption>? picks = null) : ObservableObject, IPickTarget
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PickedValue))] private string _value = value;
    partial void OnValueChanged(string value) => changed();

    public ObservableCollection<SettingOption> Picks { get; } = Filled(picks, value);
    public bool IsPick => Picks.Count > 0;
    public bool IsTyped => !IsPick;

    public string PickedValue
    {
        get => Value;
        set => Value = value ?? "";
    }

    private static ObservableCollection<SettingOption> Filled(IReadOnlyList<SettingOption>? picks, string value)
    {
        var list = new ObservableCollection<SettingOption>();
        // A value written by hand that is not among what OptLife knows: said plainly, it will not show.
        if (picks is { Count: > 0 }) ViewModels.Picks.Fill(list, picks, value, v => L.T($"{v} — OptLife non conosce questo valore: non comparirà", $"{v} — OptLife does not know this value: it will not show"));
        return list;
    }
}

/// <summary>One entry of a list setting, split into its fields.</summary>
public sealed partial class ListRow(SettingItem owner, IReadOnlyList<ListCell> cells) : ObservableObject
{
    public IReadOnlyList<ListCell> Cells => cells;
    public SettingItem Owner => owner;

    /// <summary>Its name in the simple view's order line.</summary>
    public string Title => owner.TitleOf(this);
    public void Refresh() => OnPropertyChanged(nameof(Title));

    [RelayCommand]
    private void Remove() => owner.RemoveRow(this);

    /// <summary>Up or down one place: in a list where the order is what is shown (the overlay's values).</summary>
    [RelayCommand]
    private void MoveUp() => owner.MoveRow(this, -1);

    [RelayCommand]
    private void MoveDown() => owner.MoveRow(this, 1);

    [RelayCommand]
    private void Duplicate() => owner.DuplicateRow(this);
}

public sealed partial class SettingItem : ObservableObject, IPickTarget
{
    private readonly Func<string, IReadOnlyList<SettingOption>> _optionsOf;
    private IReadOnlyList<SettingOption> Options_() => _optionsOf(_def.Key);
    private readonly SettingDefinition _def;
    private string _savedText = "";
    private bool _savedBool;
    private bool _fromRows;

    /// <summary>The heading shown above this setting when a group starts here; empty otherwise.</summary>
    public string GroupHeader { get; init; } = "";
    public bool HasGroupHeader => GroupHeader.Length > 0;

    /// <param name="options">What the module found on this PC for this setting (devices, drives); read again at every load.</param>
    /// <param name="options">What the module wrote that can be picked: by the setting's key, and by "key.N" for the field N of a table.</param>
    public SettingItem(SettingDefinition def, ISettings current, Func<string, IReadOnlyList<SettingOption>>? options = null)
    {
        _def = def;
        _optionsOf = options ?? (_ => []);
        Load(current);
    }

    // ── Picked from a list instead of typed ──

    /// <summary>A text picked from a drop-down (with Browse for a folder), when the module wrote what can be picked.</summary>
    public bool IsPick => _def.Kind == SettingKind.Text && _def.Pick != SettingPick.None && (Picks.Count > 0 || _def.Pick == SettingPick.Folder);
    public bool IsFolder => _def.Pick == SettingPick.Folder;

    /// <summary>A list ticked from what the module found (devices), with the entries written by hand kept below.</summary>
    public bool IsPickList => _def.Kind == SettingKind.List && _def.Pick == SettingPick.List && _def.Columns is null && PickChecks.Count > 0;
    public bool IsListTable => IsList && !IsPickList;

    public ObservableCollection<SettingOption> Picks { get; } = [];
    public ObservableCollection<PickCheck> PickChecks { get; } = [];

    /// <summary>For a ticked list: the entries that match nothing found (a device not plugged in now), one per line.</summary>
    [ObservableProperty] private string _extraText = "";
    private bool _buildingChecks;

    public string PickedValue
    {
        get => TextValue;
        set => TextValue = value ?? "";
    }

    private void BuildPicks()
    {
        var options = Options_();
        if (_def.Kind == SettingKind.Text)
        {
            ViewModels.Picks.Fill(Picks, options, TextValue);
            OnPropertyChanged(nameof(IsPick));
            OnPropertyChanged(nameof(PickedValue));
            return;
        }
        if (_def.Kind != SettingKind.List || _def.Pick != SettingPick.List || _def.Columns is not null) return;
        _buildingChecks = true;
        var entries = TextValue.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        PickChecks.Clear();
        foreach (var o in options) PickChecks.Add(new PickCheck(o, entries.Any(e => Picks_Matches(o, e)), ChecksChanged));
        ExtraText = string.Join(Environment.NewLine, entries.Where(e => !options.Any(o => Picks_Matches(o, e))));
        _buildingChecks = false;
        OnPropertyChanged(nameof(IsPickList));
        OnPropertyChanged(nameof(IsListTable));
    }

    private static bool Picks_Matches(SettingOption o, string e) => ViewModels.Picks.Matches(o, e);

    partial void OnExtraTextChanged(string value) => ChecksChanged();

    /// <summary>The ticks and the hand-written lines back into the list that is saved.</summary>
    private void ChecksChanged()
    {
        if (_buildingChecks) return;
        var lines = PickChecks.Where(c => c.IsChecked).Select(c => c.Option.Value)
            .Concat(ExtraText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0));
        _fromRows = true;
        TextValue = string.Join(Environment.NewLine, lines);
        _fromRows = false;
    }

    // ── A list shown as a table: one row per entry, one box per field ──

    /// <summary>The column names; a plain list has one column with no name.</summary>
    public IReadOnlyList<string> Columns => _def.Columns is { Count: > 0 } c ? [.. c.Select(n => n.ToUpper(Display.Lang))] : [""];
    public bool HasColumnNames => _def.Columns is { Count: > 0 };
    public System.Collections.ObjectModel.ObservableCollection<ListRow> Rows { get; } = [];

    /// <summary>Shown as the text it is stored as, for pasting many lines at once.</summary>
    [ObservableProperty] private bool _asText;

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(NewRow(""));
        RowsChanged();
    }

    public void MoveRow(ListRow row, int by)
    {
        var at = Rows.IndexOf(row);
        var to = at + by;
        if (at < 0 || to < 0 || to >= Rows.Count) return;
        Rows.Move(at, to);
        RowsChanged();
    }

    public void RemoveRow(ListRow row)
    {
        Rows.Remove(row);
        RowsChanged();
    }

    /// <summary>A copy of the row right under it (a preset for one more game).</summary>
    public void DuplicateRow(ListRow row)
    {
        var at = Rows.IndexOf(row);
        if (at < 0) return;
        Rows.Insert(at + 1, NewRow(string.Join(_def.Separator, row.Cells.Select(c => c.Value))));
        RowsChanged();
    }

    /// <summary>A whole entry added from elsewhere on the card ("Save as preset" writes into the presets table).</summary>
    public void AddLine(string line)
    {
        Rows.Add(NewRow(line));
        RowsChanged();
    }

    // ── "Save as preset": the chosen values under a name, into the module's presets table ──

    public bool CanSavePreset => _def.PresetTable is not null;
    [ObservableProperty] private string _presetName = "";

    /// <summary>Set by the module card: where a new preset goes (the sibling table setting).</summary>
    public Action<string>? SavePresetTo { get; set; }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Replace('|', ' ').Trim();
        var keys = Rows.Select(TitleKey).Where(k => k.Length > 0).ToList();
        if (name.Length == 0 || keys.Count == 0 || SavePresetTo is null) return;
        SavePresetTo($"{name} | {string.Join(",", keys)} | ");
        if (!Presets.Any(p => p.Label.Equals(name, StringComparison.CurrentCultureIgnoreCase))) Presets.Add(new SettingOption(string.Join(",", keys), name));
        PresetName = "";
    }

    private ListRow NewRow(string line)
    {
        var n = Columns.Count;
        var parts = line.Split(_def.Separator);
        // Fields beyond the columns stay in the last one, so nothing written by hand is lost.
        var cells = Enumerable.Range(0, n)
            .Select(i => i < n - 1 ? (i < parts.Length ? parts[i] : "") : string.Join(_def.Separator, parts.Skip(n - 1)))
            .Select((v, i) => new ListCell(RowsChanged, v.Trim(), _def.Pick == SettingPick.List && _def.Columns is not null ? (i == 0 ? Options_() : _optionsOf($"{_def.Key}.{i}")) : null))
            .ToList();
        return new ListRow(this, cells);
    }

    private void BuildRows(string text)
    {
        Rows.Clear();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            Rows.Add(NewRow(line));
    }

    /// <summary>The table back into the text that is saved: empty rows are dropped, fields joined with the separator.</summary>
    private void RowsChanged()
    {
        var joint = _def.Separator == '|' ? " | " : _def.Separator.ToString();
        var lines = Rows.Select(r => string.Join(joint, r.Cells.Select(c => c.Value.Trim())).TrimEnd(' ', '|'))
            .Where(l => l.Replace(joint.Trim(), "").Trim().Length > 0);
        _fromRows = true;
        TextValue = string.Join(Environment.NewLine, lines);
        _fromRows = false;
        SyncChips();
    }

    partial void OnTextValueChanged(string value)
    {
        // Edited as text: the table follows.
        if (IsList && !_fromRows)
        {
            BuildRows(value);
            SyncChips();
        }
        OnPropertyChanged(nameof(PickedValue));
    }

    public string Key => _def.Key;
    public SettingDefinition Definition => _def;
    public string Label => _def.Label;
    public string Description => _def.Kind switch
    {
        SettingKind.Int => _def.Description + L.T($" Da {_def.Min} a {_def.Max}.", $" From {_def.Min} to {_def.Max}."),
        SettingKind.List => _def.Description,
        _ => _def.Description,
    };
    public bool IsBool => _def.Kind == SettingKind.Bool;
    public bool IsLine => _def.Kind is SettingKind.Int || _def.Kind == SettingKind.Text && !IsPick;
    public bool IsList => _def.Kind == SettingKind.List;
    public bool IsChoice => _def.Kind == SettingKind.Choice;
    public IReadOnlyList<string> Options => _def.Options ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _boolValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _textValue = "";

    public bool IsDirty => IsBool ? BoolValue != _savedBool : TextValue != _savedText;

    public void Load(ISettings current)
    {
        switch (_def.Kind)
        {
            case SettingKind.Bool: _savedBool = current.GetBool(Key); break;
            case SettingKind.Int: _savedText = current.GetInt(Key).ToString(CultureInfo.InvariantCulture); break;
            case SettingKind.Text: _savedText = current.GetText(Key); break;
            case SettingKind.Choice: _savedText = current.GetChoice(Key); break;
            default: _savedText = string.Join(Environment.NewLine, current.GetList(Key)); break;
        }
        BoolValue = _savedBool;
        TextValue = _savedText;
        BuildPicks();
        BuildBasic();
        SyncChips();
        OnPropertyChanged(nameof(IsLine));
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>The value as it must be stored, or the reason it cannot be accepted.</summary>
    public bool TryRead(out JsonElement value, out string? error)
    {
        try
        {
            var text = _def.Kind switch
            {
                SettingKind.Bool => BoolValue ? "true" : "false",
                SettingKind.List => string.Join(';', TextValue.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)),
                _ => TextValue.Trim(),
            };
            value = ModuleSettings.Parse(_def, text);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            value = default;
            error = $"{Label}: {ex.Message}";
            return false;
        }
    }
}

/// <summary>A module as the panel shows it: switch, state, and the settings it declared.</summary>
public sealed partial class ModuleItem : ObservableObject
{
    private readonly IModule _module;
    private readonly ConfigStore _config;
    private readonly ModuleRunner _runner;

    /// <param name="options">Reads what the module wrote that can be picked for a setting (its state "options.&lt;key&gt;").</param>
    public ModuleItem(IModule module, ConfigStore config, ModuleRunner runner, DateTimeOffset now, Func<string, IReadOnlyList<SettingOption>>? options = null)
    {
        _module = module;
        _config = config;
        _runner = runner;
#pragma warning disable MVVMTK0034 // Initial value: assigning the property would write it back to the config.
        _enabled = runner.IsEnabled(module);
#pragma warning restore MVVMTK0034
        var current = runner.SettingsOf(module);
        Actions = [.. module.Actions.Select(a => new ActionItem(module, a, config, runner))];
        // A heading where a group of settings starts ("When to update"), so a long list reads in parts.
        Settings = [.. module.Settings.Select((d, i) => new SettingItem(d, current, options) { GroupHeader = d.Group is { } g && (i == 0 || module.Settings[i - 1].Group != g) ? g : "" })];
        // "Save as preset" on a table writes into the table the module named for it.
        foreach (var s in Settings.Where(s => s.Definition.PresetTable is not null))
            if (Settings.FirstOrDefault(t => t.Key == s.Definition.PresetTable) is { } table)
                s.SavePresetTo = line => table.AddLine(line);
        foreach (var s in Settings)
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SettingItem.TextValue) or nameof(SettingItem.BoolValue)) Edited?.Invoke();
                if (e.PropertyName != nameof(SettingItem.IsDirty)) return;
                OnPropertyChanged(nameof(IsDirty));
                SaveCommand.NotifyCanExecuteChanged();
            };
        RefreshStatus(now);
#pragma warning disable MVVMTK0034 // Read back without writing it again.
        _showAdvanced = config.Load().Ui.GetValueOrDefault("advanced." + module.Info.Id) == "1";
#pragma warning restore MVVMTK0034
        ApplyAdvanced();
    }

    public string Id => _module.Info.Id;
    public string Name => _module.Info.Name;
    public string Description => _module.Info.Description;
    public IReadOnlyList<SettingItem> Settings { get; }

    /// <summary>The settings as they are on screen now, saved or not: what a module's preview draws.</summary>
    public ISettings CurrentSettings()
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (var s in Settings)
            if (s.TryRead(out var value, out _)) values[s.Key] = value;
        return new ModuleSettings(_module.Settings, values);
    }

    /// <summary>Changes one setting on screen (not saved yet), as typing in it would: the preview's dragging does it.</summary>
    public void Set(string key, string text)
    {
        if (Settings.FirstOrDefault(s => s.Key == key) is not { } item) return;
        if (item.IsBool) item.BoolValue = text == "true";
        else item.TextValue = text;
    }

    /// <summary>
    /// "Advanced": the settings marked advanced, and the whole tables instead of their simple view. Off by default, remembered per module.
    /// </summary>
    [ObservableProperty] private bool _showAdvanced;

    public bool HasAdvanced => Settings.Any(s => s.IsAdvanced || s.HasBasic);

    partial void OnShowAdvancedChanged(bool value)
    {
        _config.Update(f => f.Ui["advanced." + Id] = value ? "1" : "0");
        ApplyAdvanced();
    }

    private void ApplyAdvanced()
    {
        foreach (var s in Settings)
        {
            s.Shown = !s.IsAdvanced || ShowAdvanced;
            s.ShowBasic = s.HasBasic && !ShowAdvanced;
        }
    }

    /// <summary>Fired when any setting changes on screen, saved or not.</summary>
    public event Action? Edited;
    public bool HasSettings => Settings.Count > 0;
    public IReadOnlyList<ActionItem> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public bool IsDirty => Settings.Any(s => s.IsDirty);

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Settings and permissions shown; closed at first, so the page is a short list of modules.</summary>
    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>How much there is inside, on the closed card ("8 settings · 2 actions").</summary>
    public string Contents => string.Join(" · ", new[]
    {
        Settings.Count == 0 ? null : L.T(Settings.Count == 1 ? "1 impostazione" : $"{Settings.Count} impostazioni", Settings.Count == 1 ? "1 setting" : $"{Settings.Count} settings"),
        Actions.Count == 0 ? null : L.T(Actions.Count == 1 ? "1 azione" : $"{Actions.Count} azioni", Actions.Count == 1 ? "1 action" : $"{Actions.Count} actions"),
    }.OfType<string>());

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _statusLevel = "ok";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _messageIsError;

    partial void OnEnabledChanged(bool value)
    {
        _config.UpdateModule(Id, c => c.Enabled = value);
        Message = "";
    }

    public void RefreshStatus(DateTimeOffset now)
    {
        var statuses = _module.Tasks.Select(t => _runner.ReadStatus(_module, t)).ToList();
        var failing = statuses.FirstOrDefault(s => s.LastError is not null);
        var last = statuses.Max(s => s.LastRun);
        if (!Enabled)
            (StatusText, StatusLevel) = (L.T("Spento: non raccoglie e non controlla nulla.", "Off: collects and checks nothing."), "info");
        else if (failing is not null)
            (StatusText, StatusLevel) = (L.T("In errore: ", "Failing: ") + failing.LastError, "high");
        else if (last is null)
            (StatusText, StatusLevel) = (L.T("Acceso, in attesa del primo controllo.", "On, waiting for the first check."), "info");
        else
            (StatusText, StatusLevel) = (L.T("Funziona. Ultimo controllo ", "Working. Last check ") + Display.When(last.Value, now) + ".", "ok");
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        // Only what changed on this card is written: a card opened earlier must never write back its old copy of a setting
        // changed elsewhere since (on 24 Nov a save from Settings → Overlay emptied the presets made in the overlay editor).
        var values = new Dictionary<string, JsonElement>();
        foreach (var s in Settings.Where(s => s.IsDirty))
        {
            if (!s.TryRead(out var value, out var error))
            {
                (Message, MessageIsError) = (error!, true);
                return; // Nothing is written unless every field is valid.
            }
            values[s.Key] = value;
        }
        _config.KeepCopy();
        _config.UpdateModule(Id, c =>
        {
            foreach (var (key, value) in values) c.Settings[key] = value;
        });
        Reload(L.T("Salvato. Vale dal prossimo controllo.", "Saved. It applies from the next check."));
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _config.KeepCopy();
        _config.UpdateModule(Id, c => c.Settings.Clear());
        Reload(L.T("Ripristinati i valori predefiniti.", "Default values restored."));
    }

    /// <summary>Reads the settings again from the file (the guided tour wrote some).</summary>
    public void Reload() => Reload("");

    private void Reload(string message)
    {
        var current = _runner.SettingsOf(_module);
        foreach (var s in Settings) s.Load(current);
        ApplyAdvanced();
        (Message, MessageIsError) = (message, false);
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One live measurement on the Monitor page: its current number and the last hour as a small chart.</summary>
/// <summary>Values this PC cannot read, with why and the button that helps (opens the official page of the program that reads them).</summary>
public sealed partial class MonitorNotice(string values, string why, string module, FindingFix? fix) : ObservableObject
{
    public string Values => values;
    public string Why => why;
    public string Module => module;
    public FindingFix? Fix => fix;
    public bool HasFix => fix is not null;
    public string ButtonText => fix?.Label ?? "";
    public string Signature => $"{values}|{why}|{fix?.ActionId}|{fix?.Argument}";

    /// <summary>What the button did ("Pagina aperta nel browser").</summary>
    [ObservableProperty] private string _result = "";
}

public sealed partial class MonitorTile(string key, string label, string unit, string group = "") : ObservableObject
{
    public string Key => key;
    public string Label => label;
    public string Unit => unit;

    /// <summary>Where the value comes from ("Scheda video"), shown when the tile stands alone among the featured ones.</summary>
    public string Group => group;
    public string FullLabel => group.Length > 0 ? $"{group} · {label}" : label;

    /// <summary>How close to its limit, as the module reported it; null when the value has none.</summary>
    public double? Stress { get; private set; }

    /// <summary>The colour family: "high" beyond the limit, "medium" close to it, "ok" within, "info" when there is no limit.</summary>
    [ObservableProperty]
    private string _level = "info";

    /// <summary>Chosen by the person to stay among the featured ones.</summary>
    [ObservableProperty]
    private bool _isPinned;

    public void Update(string valueText, double? stress)
    {
        ValueText = valueText;
        Stress = stress;
        Level = stress switch { null => "info", >= 1 => "high", >= 0.8 => "medium", _ => "ok" };
        OnPropertyChanged(nameof(LevelWord));
        OnPropertyChanged(nameof(IsStrained));
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>The colour in words, beside its icon: a state is never told by colour alone.</summary>
    public string LevelWord => Level switch
    {
        "high" => L.T("oltre il limite", "over the limit"),
        "medium" => L.T("vicino al limite", "near the limit"),
        _ => "",
    };
    public bool IsStrained => Level is "high" or "medium";

    /// <summary>What a screen reader says for the tile.</summary>
    public string AccessibleName => $"{FullLabel}: {ValueText} {Unit} {LevelWord}".Trim();

    /// <summary>The name other parts use for the value (the overlay list): in the tooltip, not on the tile.</summary>
    public string KeyHint => L.T($"Nome del valore: {key}. La linea sotto è l'ultima ora.", $"Value name: {key}. The line below is the last hour.");

    [ObservableProperty]
    private string _valueText = "";

    [ObservableProperty]
    private IReadOnlyList<double> _history = [];
}

public sealed record MonitorGroup(string Name, IReadOnlyList<MonitorTile> Tiles);

/// <summary>A filter of a gallery: one of its groups, or all ("" as key).</summary>
public sealed record GalleryFilterItem(string Key, string Label, bool IsChosen);

/// <summary>A heading of a gallery and its cards.</summary>
public sealed record GallerySection(string Name, IReadOnlyList<GalleryCard> Cards)
{
    public string AccessibleName => $"{Name}, {Cards.Count}";
}

/// <summary>
/// One card of a gallery: picture (or the initial when there is none), title, one line, and the first of its buttons in sight;
/// the others are in its menu.
/// </summary>
public sealed class GalleryCard(CheckItem item)
{
    private readonly IReadOnlyList<ButtonItem> _buttons = [.. (item.Buttons ?? []).Select(b => new ButtonItem(new ViewButton("", item.Label, "", b.ActionId, b.Argument, b.Text)))];

    public string Title => item.Label;
    public string Line => item.Value.Split('\n')[0];
    public string Advice => item.Advice ?? "";
    public bool HasAdvice => Advice.Length > 0;
    public string? Image => item.Image;
    /// <summary>Only pictures: a program file given by mistake shows the initial, not an empty frame.</summary>
    public bool HasImage => item.Image is { } i && System.IO.Path.GetExtension(i).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico";
    public string? Picture => HasImage ? item.Image : null;

    /// <summary>
    /// The big picture for the full-screen view: Steam's wide hero art when the box art comes from its cache
    /// (librarycache\&lt;id&gt;\…), otherwise the box art itself.
    /// </summary>
    public string? Hero => _hero ??= FindHero() ?? Picture ?? "";
    private string? _hero;
    private string? FindHero() => item.Wide is { Length: > 0 } wide && System.IO.File.Exists(wide) ? wide : SteamArt("library_hero.jpg", "library_header.jpg", "header.jpg");

    /// <summary>The game's logo Steam keeps beside its art (a transparent picture of the title), when there is one.</summary>
    public string? Logo => _logo ??= SteamArt("logo.png") ?? "";
    private string? _logo;
    public bool HasLogo => !string.IsNullOrEmpty(Logo);

    /// <summary>
    /// The game's own folder in Steam's cache: librarycache\<id>\… with <id> all digits. A box art directly in librarycache
    /// (old layout) has no folder of its own: nothing is looked for, so another game's art can never be taken.
    /// </summary>
    private string? SteamArt(params string[] names)
    {
        if (Picture is not { } p || !p.Contains(@"\librarycache\", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            for (var dir = System.IO.Path.GetDirectoryName(p); dir is not null; dir = System.IO.Path.GetDirectoryName(dir))
            {
                var parent = System.IO.Path.GetDirectoryName(dir);
                if (parent is null || !System.IO.Path.GetFileName(parent).Equals("librarycache", StringComparison.OrdinalIgnoreCase)) continue;
                if (!System.IO.Path.GetFileName(dir).All(char.IsDigit)) return null;
                foreach (var name in names)
                    if (System.IO.Directory.EnumerateFiles(dir, name, System.IO.SearchOption.AllDirectories).FirstOrDefault() is { } hit) return hit;
                return null;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Extra facts for the full-screen view ("Played: 12 h", "Average FPS: 141").</summary>
    public IReadOnlyList<string> Details => item.Details ?? [];
    public bool HasDetails => Details.Count > 0;
    public string Initial => item.Label.Trim().Length > 0 ? item.Label.Trim()[..1].ToUpper(Display.Lang) : "?";
    public string Level => item.Status switch { CheckStatus.Ok => "good", CheckStatus.Watch => "medium", CheckStatus.Wrong => "high", _ => "info" };
    public bool Warns => item.Status is CheckStatus.Watch or CheckStatus.Wrong;
    public ButtonItem? Primary => _buttons.FirstOrDefault();
    public bool HasPrimary => Primary is not null;
    public IReadOnlyList<ButtonItem> More => [.. _buttons.Skip(1)];
    public bool HasMore => _buttons.Count > 1;
    public string MoreName => L.T($"Altre azioni per {item.Label}", $"More actions for {item.Label}");
    public string PrimaryName => Primary is null ? "" : $"{Primary.ButtonText} {item.Label}";
    public string AccessibleName => $"{item.Label}. {Line}. {Advice}".Trim(' ', '.');
}
