using OptLife.Core.Appearance;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OptLife.Panel.ViewModels;

/// <summary>One page reachable from the side bar: a page of the panel, or one of a module's pages.</summary>
/// <param name="key">Stable name of the page, to open it again next time ("view:games/sessions", "report").</param>
public sealed partial class NavItem(string key, string label, Action go, Func<bool> isCurrent) : ObservableObject
{
    public string Key => key;
    public string Label => label;
    public bool IsCurrent => isCurrent();
    public void Notify() => OnPropertyChanged(nameof(IsCurrent));

    [RelayCommand]
    private void Go() => go();
}

/// <summary>
/// An entry of the side bar. With one page it opens that page; with more, the pages show as tabs at the top of
/// the content, so pages that belong together (everything about the programs, the history) share one place.
/// </summary>
/// <param name="icon">A Segoe Fluent Icons glyph.</param>
/// <param name="heading">A heading drawn above this entry ("The PC"), or "" for none.</param>
/// <param name="bottom">Kept at the bottom of the bar, with Settings.</param>
public sealed partial class NavGroup(string label, IReadOnlyList<NavItem> items, string icon = NavGroup.DefaultIcon, string heading = "", bool bottom = false) : ObservableObject
{
    public const string DefaultIcon = "\uEA86"; // A puzzle piece: a module's page with a place of its own.

    public string Label => items.Count > 1 ? label : items[0].Label;
    public IReadOnlyList<NavItem> Items => items;
    public bool HasMenu => items.Count > 1;
    public string Icon => icon;
    public string Heading => heading;
    public bool HasHeading => heading.Length > 0;
    public bool IsBottom => bottom;

    /// <summary>"Ctrl+3", shown in the tooltip; set when the bar is built.</summary>
    public string Shortcut { get; set; } = "";
    public string ToolTipText => Shortcut.Length > 0 ? $"{Label} ({Shortcut})" : Label;

    /// <summary>Kept for compatibility with older layouts: the side bar has no drop-downs.</summary>
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isSelected;

    /// <summary>The page of the group in view, or the last one seen: the side bar entry goes back there.</summary>
    private NavItem? _last;

    public string Current => HasMenu && items.FirstOrDefault(i => i.IsCurrent) is { } c ? c.Label : "";

    public void Update()
    {
        foreach (var i in items) i.Notify();
        if (items.FirstOrDefault(i => i.IsCurrent) is { } current) _last = current;
        IsSelected = items.Any(i => i.IsCurrent);
        OnPropertyChanged(nameof(Current));
    }

    [RelayCommand]
    private void Activate() => (_last ?? items[0]).GoCommand.Execute(null);

    [RelayCommand]
    private void Pick(NavItem item)
    {
        IsOpen = false;
        item.GoCommand.Execute(null);
    }
}

/// <summary>One thing the search found: where it is, what it says, and how to get there.</summary>
public sealed partial class SearchResult(string where, string text, string detail, Action go)
{
    public string Where => where.ToUpper(Display.Lang);
    public string Text => text;
    public string Detail => detail;
    public bool HasDetail => detail.Length > 0;
    public void Go() => go();
}

/// <summary>A choice of the Appearance section (a base, an accent), with the colour to show beside its name.</summary>
public sealed partial class ThemeOption(string id, string name, string? swatch) : ObservableObject
{
    public string Id => id;
    public string Name => name;
    public string Swatch => swatch ?? "#808080";
    public bool HasSwatch => swatch is not null;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PanelViewModel
{
    public const string ThemeBaseKey = "theme.base", ThemeAccentKey = "theme.accent", ThemeCustomKey = "theme.custom", BigAtStartKey = "start.big";

    /// <summary>The panel opens on the full-screen library (for a PC used from the sofa); leaving it shows the normal panel.</summary>
    [ObservableProperty] private bool _openBigAtStart;

    partial void OnOpenBigAtStartChanged(bool value)
    {
        if (_loadingTheme) return;
        _config.Update(f => f.Ui[BigAtStartKey] = value ? "1" : "0");
    }

    // ── Side bar ─────────────────────────────────────────────────────────────

    /// <summary>The entries between Problems and Settings: module pages by section, then the history.</summary>
    public ObservableCollection<NavGroup> NavGroups { get; } = [];

    /// <summary>The entries in the middle of the bar; the history sits at the bottom with Settings.</summary>
    public IEnumerable<NavGroup> TopGroups => NavGroups.Where(g => !g.IsBottom);
    public NavGroup? HistoryGroup => NavGroups.FirstOrDefault(g => g.IsBottom);

    /// <summary>The sections of the bar in their order, with their icon; the first four sit under "The PC".</summary>
    private static (string Menu, string Icon, bool Pc)[] Sections =>
    [
        (ViewMenu.Status, "\uE9D9", true),     // Diagnostic
        (ViewMenu.Programs, "\uE71D", true),   // All apps
        (ViewMenu.Security, "\uEA18", true),   // Shield
        (ViewMenu.Order, "\uEDA2", true),      // Hard drive
        (ViewMenu.Pc, "\uE7F4", true),         // Monitor
        (ViewMenu.Games, "\uE7FC", false),     // Game controller
        (ViewMenu.Tools, "\uE90F", false),     // Repair
    ];

    private void BuildNavigation()
    {
        NavGroups.Clear();
        var sections = Sections;
        var menus = Views.GroupBy(v => v.View.Menu ?? v.View.Title)
            .OrderBy(g => Array.FindIndex(sections, s => s.Menu == g.Key) is var i && i >= 0 ? i : sections.Length)
            .ToList();
        var pcHeadingDone = false;
        foreach (var menu in menus)
        {
            var section = sections.FirstOrDefault(s => s.Menu == menu.Key);
            var heading = section.Pc && !pcHeadingDone ? L.T("Il PC", "The PC") : "";
            pcHeadingDone |= section.Pc;
            // An entry with several pages and words of its own opens on its overview.
            var key = menu.Key;
            List<NavItem> intro = menu.Count() > 1 && ViewMenu.Intro(key) is not null
                ? [new NavItem($"intro:{key}", L.T("Panoramica", "Overview"), () => ShowIntro(key), () => Page == PanelPage.Intro && IntroMenu == key)]
                : [];
            NavGroups.Add(new NavGroup(menu.Key,
                [.. intro, .. menu.OrderBy(tab => tab.View.Order).Select(tab => new NavItem($"view:{tab.ModuleId}/{tab.View.Id}", tab.View.Title, () => ShowView(tab), () => Page == PanelPage.View && SelectedView == tab))],
                section.Icon ?? NavGroup.DefaultIcon, heading));
        }
        NavGroups.Add(new NavGroup(L.T("Storico", "History"),
        [
            new NavItem("activity", L.T("Attività", "Activity"), () => Page = PanelPage.Activity, () => Page == PanelPage.Activity),
            new NavItem("report", L.T("Rapporto", "Report"), () => Page = PanelPage.Report, () => Page == PanelPage.Report),
            new NavItem("actions", L.T("Azioni eseguite", "Actions taken"), () => Page = PanelPage.Actions, () => Page == PanelPage.Actions),
            new NavItem("events", L.T("Eventi registrati", "Recorded events"), () => Page = PanelPage.History, () => Page == PanelPage.History),
        ], "\uE81C", bottom: true));
        // Ctrl+1 Home, Ctrl+2 Problems, then the bar from the top.
        var n = 3;
        foreach (var g in TopGroups.Concat(NavGroups.Where(g => g.IsBottom)))
            g.Shortcut = n <= 9 ? $"Ctrl+{n++}" : "";
        OnPropertyChanged(nameof(TopGroups));
        OnPropertyChanged(nameof(HistoryGroup));
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        foreach (var g in NavGroups) g.Update();
        UpdateFrame();
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    /// <summary>Puts a theme on screen (base, accent, custom colour); set by the application, absent in tests.</summary>
    public Action<string?, string?, string?>? ApplyTheme { get; set; }

    /// <summary>The Windows accent colour, to show it beside "Windows colour"; set by the application.</summary>
    public Func<string?> WindowsAccent { get; set; } = () => null;

    /// <summary>Whether Windows is in light mode, for the colour shown beside the Windows base; set by the application.</summary>
    public Func<bool> WindowsLight { get; set; } = () => false;

    public IReadOnlyList<ThemeOption> ThemeBases { get; private set; } = [];
    public IReadOnlyList<ThemeOption> ThemeAccents { get; private set; } = [];

    [ObservableProperty] private string _themeBase = "black";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustomAccent))] private string _themeAccent = ThemeCatalog.Neutral;
    [ObservableProperty] private string _customAccent = "#3B82F6";
    [ObservableProperty] private string _customAccentError = "";

    public bool IsCustomAccent => ThemeAccent == ThemeCatalog.Custom;

    private bool _loadingTheme;

    private void LoadAppearance()
    {
        var ui = _config.Load().Ui;
        _loadingTheme = true;
        // Nothing chosen yet: the look of the Windows system apps. A choice made before stays as it was.
        ThemeBase = ui.TryGetValue(ThemeBaseKey, out var savedBase) ? ThemeCatalog.BaseOf(savedBase).Id : ThemeCatalog.DefaultBase;
        ThemeAccent = ThemeCatalog.Accents.Any(a => a.Id == ui.GetValueOrDefault(ThemeAccentKey)) ? ui[ThemeAccentKey]
            : ui.ContainsKey(ThemeBaseKey) ? ThemeCatalog.Neutral : ThemeCatalog.DefaultAccent;
        CustomAccent = ThemeCatalog.Normalize(ui.GetValueOrDefault(ThemeCustomKey)) ?? "#3B82F6";
        OpenBigAtStart = ui.GetValueOrDefault(BigAtStartKey) == "1";
        _loadingTheme = false;
        BuildThemeOptions();
    }

    /// <summary>Called by the application once the theme can be applied (and when Windows changes its colour).</summary>
    public void ApplyCurrentTheme()
    {
        BuildThemeOptions();
        ApplyTheme?.Invoke(ThemeBase, ThemeAccent, CustomAccent);
    }

    private void BuildThemeOptions()
    {
        ThemeBases = [.. ThemeCatalog.BasesFor(WindowsLight()).Select(b => new ThemeOption(b.Id, b.Name, b.Colors["Surface.Solid"]) { IsSelected = b.Id == ThemeBase })];
        var baseText = ThemeCatalog.BaseOf(ThemeBase, WindowsLight()).Colors["Text.Primary"];
        ThemeAccents = [.. ThemeCatalog.Accents.Select(a => new ThemeOption(a.Id, a.Name,
            a.Id switch { ThemeCatalog.Neutral => baseText, ThemeCatalog.Windows => WindowsAccent(), ThemeCatalog.Custom => ThemeCatalog.Normalize(CustomAccent), _ => a.Hex })
            { IsSelected = a.Id == ThemeAccent })];
        OnPropertyChanged(nameof(ThemeBases));
        OnPropertyChanged(nameof(ThemeAccents));
    }

    [RelayCommand]
    private void SetThemeBase(string id) => ThemeBase = id;

    [RelayCommand]
    private void SetThemeAccent(string id) => ThemeAccent = id;

    partial void OnThemeBaseChanged(string value) => SaveTheme();
    partial void OnThemeAccentChanged(string value) => SaveTheme();

    partial void OnCustomAccentChanged(string value)
    {
        if (_loadingTheme) return;
        // Typed one character at a time: only a complete colour is saved and applied.
        if (ThemeCatalog.Normalize(value) is null)
        {
            CustomAccentError = L.T("Scrivi un colore come #3B82F6", "Write a colour like #3B82F6");
            return;
        }
        CustomAccentError = "";
        SaveTheme();
    }

    private void SaveTheme()
    {
        if (_loadingTheme) return;
        _config.Update(f =>
        {
            f.Ui[ThemeBaseKey] = ThemeBase;
            f.Ui[ThemeAccentKey] = ThemeAccent;
            if (ThemeCatalog.Normalize(CustomAccent) is { } hex) f.Ui[ThemeCustomKey] = hex;
        });
        ApplyCurrentTheme();
    }

    // ── Language and first start ─────────────────────────────────────────────

    public const string WelcomeKey = "welcome";

    public bool IsItalian => !L.IsEnglish;
    public bool IsEnglish => L.IsEnglish;

    /// <summary>Opens the panel again (its texts are read when the window is built); set by the application.</summary>
    public Action? Restart { get; set; }

    /// <summary>The first time the panel opens: a few questions (language, look) before anything else.</summary>
    [ObservableProperty] private bool _showWelcome;

    [RelayCommand]
    private void SetLanguage(string code)
    {
        if (code == L.Code) return;
        // Saved as a choice, not "auto": the person picked it. The agent follows within a few seconds.
        _config.Update(f => f.Language = code);
        Restart?.Invoke();
    }


    // ── Copy a problem ───────────────────────────────────────────────────────

    /// <summary>Puts text on the clipboard; set by the application (no clipboard in tests).</summary>
    public Action<string>? CopyText { get; set; }

    [RelayCommand]
    private void CopyFinding(FindingItem item)
    {
        CopyText?.Invoke(item.ForClipboard());
        (Notice, NoticeIsError) = (L.T($"Problema #{item.Id} copiato: incollalo a Claude con Ctrl+V.", $"Problem #{item.Id} copied: paste it to Claude with Ctrl+V."), false);
    }

    // ── Search (Ctrl+F) ──────────────────────────────────────────────────────

    [ObservableProperty] private string _globalQuery = "";
    [ObservableProperty] private bool _searchOpen;
    public ObservableCollection<SearchResult> SearchResults { get; } = [];
    [ObservableProperty] private string _searchEmptyText = "";

    /// <summary>The module whose settings the search asked to show; the window scrolls to it.</summary>
    [ObservableProperty] private string? _focusedModule;

    partial void OnGlobalQueryChanged(string value)
    {
        SearchResults.Clear();
        var words = Words(value);
        if (words.Length == 0 || value.Trim().Length < 2)
        {
            SearchOpen = false;
            return;
        }
        // Found in the name before found in the description; the order of SearchEverything breaks ties.
        var found = SearchEverything()
            .Select((r, i) => (r, i, Score: Matches(words, r.Text) ? 0 : Matches(words, r.Text + " " + r.Where) ? 1 : Matches(words, r.Text + " " + r.Detail + " " + r.Where) ? 2 : -1))
            .Where(x => x.Score >= 0)
            .OrderBy(x => x.Score).ThenBy(x => x.i)
            .Take(40);
        foreach (var (r, _, _) in found) SearchResults.Add(r);
        SearchEmptyText = SearchResults.Count == 0 ? L.T("Niente di trovato.", "Nothing found.") : "";
        SearchOpen = true;
    }

    [RelayCommand]
    private void OpenResult(SearchResult result)
    {
        SearchOpen = false;
        GlobalQuery = "";
        result.Go();
    }

    /// <summary>Everything a person may look for, in the order results are shown: places first, then content.</summary>
    private IEnumerable<SearchResult> SearchEverything()
    {
        var pages = L.T("Pagine", "Pages");
        yield return new SearchResult(pages, L.T("Panoramica", "Overview"), L.T("Valori dal vivo", "Live values"), () => Page = PanelPage.Monitor);
        yield return new SearchResult(pages, L.T("Problemi", "Problems"), "", () => Show(PanelPage.Problems, FindingFilter.Open));
        foreach (var g in NavGroups)
            foreach (var i in g.Items)
                yield return new SearchResult(pages, i.Label, g.HasMenu ? g.Label.Replace("▾", "").Trim() : "", () => i.GoCommand.Execute(null));
        yield return new SearchResult(pages, L.T("Impostazioni", "Settings"), L.T("Moduli, permessi, aspetto", "Modules, permissions, appearance"), () => Page = PanelPage.Modules);
        yield return new SearchResult(pages, L.T("Lingua, aspetto e temi", "Language, appearance and themes"), L.T("Italiano, inglese, nero, grafite, chiaro, colore d'accento", "Italian, English, black, graphite, light, accent colour"),
            () => GoToSettings(null));

        var problems = L.T("Problemi", "Problems");
        foreach (var f in _store.GetFindings(includeClosed: true).OrderBy(f => f.Status))
        {
            var state = f.Status switch { FindingStatus.Open => FindingFilter.Open, FindingStatus.Ignored => FindingFilter.Ignored, _ => FindingFilter.Resolved };
            var label = state switch { FindingFilter.Open => L.T("aperto", "open"), FindingFilter.Ignored => L.T("ignorato", "ignored"), _ => L.T("risolto", "solved") };
            yield return new SearchResult($"{problems} · {label}", f.Title, f.Detail, () => Show(PanelPage.Problems, state));
        }

        foreach (var tab in Views)
        {
            var where = tab.View.Title;
            if (tab.View.Layout is ViewLayout.Checklist or ViewLayout.Gallery)
            {
                var latest = _store.QueryEvents(Now.AddDays(-tab.View.Days), Now, tab.ModuleId, tab.View.EventKind, limit: 1).FirstOrDefault();
                if (latest is not null)
                    foreach (var item in CheckItem.FromData(latest.Data))
                        yield return new SearchResult(where, item.Label, $"{item.Value} · {item.Group}", () => ShowView(tab));
            }
            if (tab.View.Layout == ViewLayout.Buttons && tab.View.Buttons is { } buttons)
                foreach (var b in buttons(_runner.SettingsOf(tab.Module)))
                    yield return new SearchResult(where, b.Label, b.Description, () => ShowView(tab));
        }

        var settings = L.T("Impostazioni", "Settings");
        foreach (var m in Modules)
        {
            yield return new SearchResult(settings, m.Name, m.Description, () => GoToSettings(m.Id));
            foreach (var s in m.Settings)
                yield return new SearchResult($"{settings} · {m.Name}", s.Label, s.Description, () => GoToSettings(m.Id));
            foreach (var a in m.Actions)
                yield return new SearchResult($"{settings} · {m.Name}", a.Name, a.Description, () => GoToSettings(m.Id));
        }

        var live = L.T("Panoramica", "Overview");
        foreach (var v in _readLive().Where(v => v.Label.Length > 0))
            yield return new SearchResult($"{live} · {v.Group}", v.Label, v.IsMissing ? v.Missing! : $"{LiveValue.FormatNumber(v.Value, v.Unit)} {v.Unit}".Trim(), () => Page = PanelPage.Monitor);
    }

    private void Show(PanelPage page, FindingFilter filter)
    {
        Filter = filter;
        Page = page;
    }

    [RelayCommand]
    private void OpenSettingsOf(string? moduleId) => GoToSettings(moduleId);

    private void GoToSettings(string? moduleId)
    {
        Page = PanelPage.Modules;
        if (Modules.FirstOrDefault(m => m.Id == moduleId) is { } module) module.IsExpanded = true;
        FocusedModule = null; // The same module twice in a row must still scroll.
        FocusedModule = moduleId ?? "";
    }

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    internal static string[] Words(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Every word in the text, ignoring case and accents ("perche" finds "perché"). A short word (up to 3 letters)
    /// must start a word, or "ram" would find every "programma".
    /// </summary>
    internal static bool Matches(string[] words, string text) => words.All(w => Contains(text, w, mustStartWord: w.Length <= 3));

    private static bool Contains(string text, string word, bool mustStartWord)
    {
        const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        for (var at = Compare.IndexOf(text, word, Loose); at >= 0; at = Compare.IndexOf(text, word, at + 1, Loose))
        {
            if (!mustStartWord || at == 0 || !char.IsLetterOrDigit(text[at - 1])) return true;
            if (at + 1 >= text.Length) break;
        }
        return false;
    }
}
