using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Core.Storage;

namespace OptLife.Panel.ViewModels;

/// <summary>
/// The frame around every page: its title, the short line under it and the full help behind "?", the tabs of a side-bar
/// entry with more than one page, the Home summary, the bell, the compact side bar, the zoom, and the last page seen.
/// </summary>
public sealed partial class PanelViewModel
{
    private const string NavPageKey = "nav.page", NavCompactKey = "nav.compact", ZoomKey = "zoom";

    /// <summary>The side-bar entry the page belongs to (none for Home, Problems and Settings).</summary>
    public NavGroup? CurrentGroup => NavGroups.FirstOrDefault(g => g.IsSelected);

    /// <summary>Several pages under one entry: their tabs are drawn at the top of the content.</summary>
    public bool ShowTabs => CurrentGroup?.HasMenu == true;

    public string PageTitle => Page switch
    {
        PanelPage.Monitor => L.T("Home", "Home"),
        PanelPage.Problems => L.T("Problemi", "Problems"),
        PanelPage.Modules => L.T("Impostazioni", "Settings"),
        _ => CurrentGroup?.HasMenu == true ? CurrentGroup.Label : CurrentGroup?.Label ?? SelectedView?.View.Title ?? "",
    };

    /// <summary>The page's own words in full: what it is for, how to read it. Opened by "?" or F1.</summary>
    public string PageHelp => Page switch
    {
        PanelPage.Monitor => L.T("Com'è il PC adesso: lo stato generale, i problemi più importanti, cosa sta facendo OptLife e i valori dal vivo (si aggiornano ogni 2 secondi; la linea sotto ogni valore è l'ultima ora). " +
                                 "Fissa i valori che vuoi tenere in cima: compare il pulsante quando passi sopra un valore o ci arrivi con Tab.",
                                 "How the PC is right now: the overall state, the most important problems, what OptLife is doing and the live values (updated every 2 seconds; the line under each value is the last hour). " +
                                 "Pin the values you want to keep on top: the button shows when you hover a value or reach it with Tab."),
        PanelPage.Problems => L.T("Ciò che OptLife ha trovato da sistemare, diviso per area. Ogni problema dice cosa è successo e cosa fare; dove può, OptLife lo sistema con il pulsante. " +
                                  "RISOLTO chiude un problema di cui hai tolto la causa (si riapre solo se succede di nuovo). In \"Altro\" trovi Ignora e Copia (per incollarlo a Claude).",
                                  "What OptLife found to fix, by area. Each problem says what happened and what to do; where it can, OptLife fixes it with the button. " +
                                  "SOLVED closes a problem whose cause you removed (it reopens only if it happens again). \"More\" holds Ignore and Copy (to paste it to Claude)."),
        PanelPage.Modules => L.T("Lingua, aspetto e avvisi del pannello; poi ogni modulo con il suo interruttore, cosa può fare da solo e le sue regole. I moduli si aprono con un clic sul nome.",
                                 "Language, look and notices of the panel; then every module with its switch, what it may do by itself and its rules. Modules open with a click on their name."),
        PanelPage.Activity => L.T("Tutto ciò che OptLife sta facendo o ha fatto: aggiornamenti, installazioni, spostamenti, scansioni, lavori del Pianificatore. In cima quelle in corso, con quanto manca quando si sa. " +
                                  "L'avviso sopra l'orologio e il suono si scelgono in Impostazioni → Avvisi.",
                                  "Everything OptLife is doing or did: updates, installs, moves, scans, Scheduler jobs. Running ones on top, with how much is left when known. " +
                                  "The notice above the clock and the sound are chosen in Settings → Notices."),
        PanelPage.Intro => IntroText,
        PanelPage.Report => L.T("Il periodo in parole, costruito dallo storico: problemi comparsi e risolti, cosa è successo modulo per modulo, cosa è stato fatto e cosa resta da fare.",
                                "The period in words, built from the history: problems that appeared and were solved, what happened module by module, what was done and what is left to do."),
        PanelPage.Actions => L.T("Ogni cambiamento fatto al PC da OptLife, da te o dalla riga di comando, con l'esito e, dove si può, come tornare indietro.",
                                 "Every change made to the PC by OptLife, by you or from the command line, with the outcome and, where possible, how to undo it."),
        PanelPage.History => L.T("Tutto ciò che i moduli hanno registrato: errori di Windows, aggiornamenti, cambi di impostazioni, sessioni di gioco. Cerca per programma, tipo o codice.",
                                 "Everything the modules recorded: Windows errors, updates, setting changes, game sessions. Search by program, type or code."),
        _ => SelectedView?.Description ?? "",
    };

    /// <summary>The first sentence of the help, under the title; the rest stays behind "?".</summary>
    public string PageSubtitle => FirstSentence(PageHelp);
    public bool HasHelp => PageHelp.Length > 0;

    /// <summary>A checklist page: its help also explains the colours and words of the lines.</summary>
    public bool HelpHasLegend => Page is PanelPage.View && SelectedView?.IsChecklist == true || Page is PanelPage.Monitor or PanelPage.Problems;

    [ObservableProperty] private bool _helpOpen;

    [RelayCommand]
    private void ToggleHelp() => HelpOpen = !HelpOpen;

    internal static string FirstSentence(string text)
    {
        var at = text.IndexOf(". ", StringComparison.Ordinal);
        return at > 0 && at < 200 ? text[..(at + 1)] : text;
    }

    /// <summary>Called whenever the page changes: the frame follows, and the page is remembered for next time.</summary>
    private void UpdateFrame()
    {
        HelpOpen = false;
        OnPropertyChanged(nameof(CurrentGroup));
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageHelp));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(HasHelp));
        OnPropertyChanged(nameof(HelpHasLegend));
        OnPropertyChanged(nameof(RefreshLabel));
        OnPropertyChanged(nameof(ShowRefresh));
        if (!_restoring) RememberPage();
    }

    // ── Last page ────────────────────────────────────────────────────────────

    private bool _restoring = true;
    private string _pageSaved = "";

    private string PageKey => Page switch
    {
        PanelPage.Monitor => "home",
        PanelPage.Problems => "problems",
        PanelPage.Modules => "settings",
        _ => NavGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.IsCurrent)?.Key ?? "home",
    };

    private void RememberPage()
    {
        var key = PageKey;
        if (key == _pageSaved) return;
        _pageSaved = key;
        _config.Update(f => f.Ui[NavPageKey] = key);
    }

    /// <summary>Opens the page seen last time; Home when there is none or it no longer exists.</summary>
    private void RestorePage()
    {
        var key = _config.Load().Ui.GetValueOrDefault(NavPageKey) ?? "home";
        _pageSaved = key;
        switch (key)
        {
            case "problems": Page = PanelPage.Problems; break;
            case "settings": Page = PanelPage.Modules; break;
            case "home": break;
            default:
                NavGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.Key == key)?.GoCommand.Execute(null);
                break;
        }
        _restoring = false;
    }

    /// <summary>
    /// The page header's button, the same place on every page (F5 too): it checks again where a check makes sense (a checklist,
    /// a gallery, Problems), and reads the history again elsewhere. Home has its own big button.
    /// </summary>
    public bool ShowRefresh => Page is not (PanelPage.Monitor or PanelPage.Intro);

    public string RefreshLabel => Page == PanelPage.View && SelectedView is { IsChecklist: true } or { IsGallery: true } || Page == PanelPage.Problems
        ? L.T("Controlla ora", "Check now") : L.T("Aggiorna", "Refresh");

    /// <summary>F5: a checklist checks its module again, Home and Problems check everything, other pages read the history again.</summary>
    [RelayCommand]
    private async Task RefreshPage()
    {
        if (Page == PanelPage.View && SelectedView is { IsChecklist: true } or { IsGallery: true }) await RecheckViewCommand.ExecuteAsync(null);
        else if (Page is PanelPage.Monitor or PanelPage.Problems) await CheckNowCommand.ExecuteAsync(null);
        else Refresh();
    }

    // ── Side bar ─────────────────────────────────────────────────────────────

    /// <summary>Only the icons: more room for the page. Remembered.</summary>
    [ObservableProperty] private bool _navCompact;

    partial void OnNavCompactChanged(bool value)
    {
        _config.Update(f => f.Ui[NavCompactKey] = value ? "1" : "0");
        NotifyBar();
    }

    /// <summary>The window is too narrow for the names: the bar shows only its icons, without changing the person's choice.</summary>
    [ObservableProperty] private bool _navNarrow;

    partial void OnNavNarrowChanged(bool value) => NotifyBar();

    /// <summary>Only the icons, by choice (☰) or because the window is narrow.</summary>
    public bool NavIconsOnly => NavCompact || NavNarrow;

    /// <summary>The number of problems: a badge beside the name, or a dot on the icon when the bar is narrow.</summary>
    public bool ShowCountBadge => OpenCount > 0 && !NavIconsOnly;
    public bool ShowCountDot => OpenCount > 0 && NavIconsOnly;

    partial void OnOpenCountChanged(int value) => NotifyBar();

    private void NotifyBar()
    {
        OnPropertyChanged(nameof(NavIconsOnly));
        OnPropertyChanged(nameof(ShowCountBadge));
        OnPropertyChanged(nameof(ShowCountDot));
    }

    [RelayCommand]
    private void ToggleNav() => NavCompact = !NavCompact;

    /// <summary>Ctrl+1 Home, Ctrl+2 Problems, Ctrl+3… the side-bar entries from the top, then History.</summary>
    [RelayCommand]
    private void GoToEntry(string number)
    {
        if (!int.TryParse(number, out var n)) return;
        if (n == 1) { Page = PanelPage.Monitor; return; }
        if (n == 2) { Show(PanelPage.Problems, FindingFilter.Open); return; }
        var entries = TopGroups.Concat(NavGroups.Where(g => g.IsBottom)).ToList();
        if (n - 3 < entries.Count) entries[n - 3].ActivateCommand.Execute(null);
    }

    // ── Zoom (on top of Windows' own text size) ──────────────────────────────

    /// <summary>How big the panel is drawn, 0.8 to 2; Ctrl+plus, Ctrl+minus, Ctrl+0. Remembered.</summary>
    [ObservableProperty] private double _zoom = 1;

    partial void OnZoomChanged(double value) => _config.Update(f => f.Ui[ZoomKey] = value.ToString("0.##", CultureInfo.InvariantCulture));

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    [RelayCommand]
    private void ZoomBy(string step)
    {
        var s = double.Parse(step, CultureInfo.InvariantCulture);
        Zoom = s == 0 ? 1 : Math.Clamp(Math.Round(Zoom + s, 2), 0.8, 2);
        OnPropertyChanged(nameof(ZoomText));
    }

    private void LoadShell()
    {
        var ui = _config.Load().Ui;
#pragma warning disable MVVMTK0034 // Read back without writing it again.
        _navCompact = ui.GetValueOrDefault(NavCompactKey) == "1";
        _zoom = double.TryParse(ui.GetValueOrDefault(ZoomKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ? Math.Clamp(z, 0.8, 2) : 1;
#pragma warning restore MVVMTK0034
    }

    // ── Home ─────────────────────────────────────────────────────────────────

    /// <summary>The few open problems that matter most, for Home; all of them are one click away.</summary>
    public ObservableCollection<FindingItem> TopProblems { get; } = [];
    private string _topShown = "";
    public bool HasMoreProblems => OpenCount > TopProblems.Count;
    public string MoreProblemsText => L.T($"Vedi tutti i {OpenCount} problemi", $"See all {OpenCount} problems");

    private void RefreshHome(IReadOnlyList<StoredFinding> open)
    {
        var top = open.OrderByDescending(f => f.Severity).ThenByDescending(f => f.LastSeen).Take(3).ToList();
        var signature = string.Join('|', top.Select(f => $"{f.Id}:{f.Title}:{FixLabel(f)}"));
        if (signature != _topShown)
        {
            _topShown = signature;
            TopProblems.Clear();
            foreach (var f in top) TopProblems.Add(new FindingItem(f, ModuleName(f.Module), Now, FixLabel(f)));
        }
        OnPropertyChanged(nameof(HasMoreProblems));
        OnPropertyChanged(nameof(MoreProblemsText));
    }

    // ── Bell ─────────────────────────────────────────────────────────────────

    /// <summary>What is running now, for the bell's list and for Home.</summary>
    public ObservableCollection<ActivityItem> Running { get; } = [];
    private string _runningShown = "";

    [ObservableProperty] private bool _bellOpen;

    [RelayCommand]
    private void ToggleBell() => BellOpen = !BellOpen;

    [RelayCommand]
    private void OpenActivity()
    {
        BellOpen = false;
        Page = PanelPage.Activity;
    }

    public string BellText => RunningCount switch
    {
        0 => L.T("Nessuna attività in corso", "Nothing running"),
        _ => RunningText,
    };

    private void RefreshRunning(IReadOnlyList<StoredActivity> list)
    {
        var running = list.Where(a => a.State == ActivityState.Running).ToList();
        var signature = string.Join('|', running.Select(a => $"{a.Id}:{a.Progress}:{a.Done}")) + (running.Count > 0 ? Now.ToUnixTimeSeconds() / 10 : 0);
        OnPropertyChanged(nameof(BellText));
        if (signature == _runningShown) return;
        _runningShown = signature;
        Running.Clear();
        foreach (var a in running.OrderByDescending(a => a.Id)) Running.Add(new ActivityItem(a, ModuleName(a.Module), Now));
    }
}
