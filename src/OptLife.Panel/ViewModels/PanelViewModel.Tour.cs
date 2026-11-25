using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptLife.Core;

namespace OptLife.Panel.ViewModels;

/// <summary>What a step of the tour shows besides its words: the look, a few settings, the notices, or the summary.</summary>
public enum TourKind { Look, Plain, Fields, Notices, Summary }

/// <summary>One question of the tour, with the suggested answer already in place.</summary>
public sealed partial class TourField : ObservableObject, IPickTarget
{
    public TourField(string? moduleId, string key, string question, SettingKind kind, string value, IReadOnlyList<string>? options = null,
        SettingPick pick = SettingPick.None, IReadOnlyList<SettingOption>? picks = null, string? name = null)
    {
        Name = name ?? question;
        Pick = pick;
        if (pick != SettingPick.None) ViewModels.Picks.Fill(Picks, picks ?? [], value);
        ModuleId = moduleId;
        Key = key;
        Question = question;
        Kind = kind;
        Options = options ?? [];
        _value = value;
    }

    /// <summary>The module the setting belongs to; null for the panel's own (the notices).</summary>
    public string? ModuleId { get; }
    public string Key { get; }
    public string Question { get; }
    public SettingKind Kind { get; }
    public IReadOnlyList<string> Options { get; }
    public bool IsBool => Kind == SettingKind.Bool;
    public bool IsChoice => Kind == SettingKind.Choice;
    /// <summary>The setting's own short name, for the summary ("Default output").</summary>
    public string Name { get; }

    public SettingPick Pick { get; }
    public ObservableCollection<SettingOption> Picks { get; } = [];

    /// <summary>Picked from what the module found (devices, drives), or a folder with Browse; typed only when there is no list.</summary>
    public bool IsPick => Kind == SettingKind.Text && Pick != SettingPick.None && (Picks.Count > 0 || Pick == SettingPick.Folder);
    public bool IsFolder => Pick == SettingPick.Folder;
    public bool IsText => Kind == SettingKind.Text && !IsPick;

    public string PickedValue
    {
        get => Value;
        set => Value = value ?? "";
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BoolValue), nameof(Shown), nameof(PickedValue))] private string _value;

    public bool BoolValue
    {
        get => Value == "true";
        set => Value = value ? "true" : "false";
    }

    /// <summary>The answer in words, for the summary.</summary>
    public string Shown => Kind switch
    {
        _ when IsFolder && Value.Trim().Length > 0 => Value,
        SettingKind.Bool => BoolValue ? L.T("sì", "yes") : L.T("no", "no"),
        _ when Picks.FirstOrDefault(p => p.Value.Equals(Value, StringComparison.OrdinalIgnoreCase)) is { } picked => picked.Label,
        _ when Value.Trim().Length == 0 => L.T("(nessuno)", "(none)"),
        _ => Value,
    };
}

/// <summary>A step of the guided tour: where it goes, what it lights up, what it says and asks.</summary>
public sealed partial class TourStep(TourKind kind, string title, string text, string highlight, Action? go, IReadOnlyList<TourField> fields) : ObservableObject
{
    public TourKind Kind => kind;
    public string Title => title;
    public string Text => text;

    /// <summary>The part of the window shown through the dimming: "SideBar", "ProblemsEntry", "PageArea", "Bell", or "" for none.</summary>
    public string Highlight => highlight;
    public Action? Go => go;
    public IReadOnlyList<TourField> Fields => fields;
    public bool HasFields => fields.Count > 0;

    /// <summary>For the summary: the answers given on this step, in one line.</summary>
    public string Answers => string.Join(Environment.NewLine, fields.Select(f => $"{f.Name}: {f.Shown}"));
    public void Refresh() => OnPropertyChanged(nameof(Answers));

    /// <summary>Set when the tour is built: the summary's "Change" button goes back here.</summary>
    public int Index { get; set; }
}

/// <summary>
/// The guided tour at the first start (it replaces the old welcome card): language and look, then page by page what each part
/// is for, with the suggested choice already in place; at the end a summary with "Change" beside every answer. It can be
/// skipped, and run again from Settings. The pages and questions come from the modules (<see cref="ModuleView.Guide"/>,
/// <see cref="SettingDefinition.Guide"/>): a module that is not there, or is switched off, is simply not in it.
/// </summary>
public sealed partial class PanelViewModel
{
    private const string TourStepKey = "tour.step";

    public ObservableCollection<TourStep> TourSteps { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TourStepNow), nameof(TourCounter), nameof(TourIsFirst), nameof(TourIsLast), nameof(TourHighlight))]
    private int _tourIndex;

    public TourStep? TourStepNow => TourIndex >= 0 && TourIndex < TourSteps.Count ? TourSteps[TourIndex] : null;
    public string TourCounter => L.T($"{TourIndex + 1} di {TourSteps.Count}", $"{TourIndex + 1} of {TourSteps.Count}");
    public bool TourIsFirst => TourIndex == 0;
    public bool TourIsLast => TourIndex == TourSteps.Count - 1;
    public string TourHighlight => ShowWelcome ? TourStepNow?.Highlight ?? "" : "";

    /// <summary>The look chosen on the first step, for the summary.</summary>
    public string TourLookSummary => string.Join(Environment.NewLine,
        L.T("Lingua: ", "Language: ") + (L.IsEnglish ? "English" : "Italiano"),
        L.T("Sfondo: ", "Background: ") + ThemeBases.FirstOrDefault(b => b.IsSelected)?.Name,
        L.T("Colore d'accento: ", "Accent colour: ") + ThemeAccents.FirstOrDefault(a => a.IsSelected)?.Name);

    /// <summary>The summary's steps: those that asked something.</summary>
    public IEnumerable<TourStep> TourAnswered => TourSteps.Where(s => s.HasFields);

    partial void OnShowWelcomeChanged(bool value) => OnPropertyChanged(nameof(TourHighlight));

    /// <summary>Builds the tour and opens it at the given step (0, or where a language change left it).</summary>
    private void BeginTour(int at)
    {
        TourSteps.Clear();
        TourSteps.Add(new TourStep(TourKind.Look, L.T("Benvenuto in OptLife", "Welcome to OptLife"),
            L.T("OptLife tiene d'occhio il PC e ti dice cosa sistemare. Qualche scelta, già pronta con quella consigliata: le cambi quando vuoi in Impostazioni.",
                "OptLife keeps an eye on the PC and tells you what to fix. A few choices, with the suggested one already in place: you can change them any time in Settings."),
            "", null, []));
        TourSteps.Add(new TourStep(TourKind.Plain, L.T("La barra a sinistra", "The bar on the left"),
            L.T("Home è com'è il PC adesso. Sotto, le parti del PC, i giochi e gli strumenti; in fondo lo storico e le impostazioni. ☰ la stringe alle sole icone.",
                "Home is how the PC is right now. Below, the parts of the PC, games and tools; at the bottom history and settings. ☰ shrinks it to its icons."),
            "SideBar", () => Page = PanelPage.Monitor, []));
        TourSteps.Add(new TourStep(TourKind.Plain, L.T("I problemi", "The problems"),
            L.T("Ciò che OptLife trova da sistemare, con il pulsante per farlo quando può. Il numero accanto dice quanti sono aperti.",
                "What OptLife finds to fix, with the button to do it when it can. The number beside it says how many are open."),
            "ProblemsEntry", () => Show(PanelPage.Problems, FindingFilter.Open), []));

        // The modules' pages, in the side bar's order; each module's questions on its first guided page.
        var asked = new HashSet<string>();
        foreach (var item in TopGroups.SelectMany(g => g.Items))
        {
            // An entry with an overview is one step: its words, and the questions of all its pages.
            if (item.Key.StartsWith("intro:", StringComparison.Ordinal))
            {
                var menu = item.Key["intro:".Length..];
                var tabs = Views.Where(v => (v.View.Menu ?? v.View.Title) == menu && _runner.IsEnabled(v.Module)).ToList();
                var introFields = tabs.Where(t => asked.Add(t.ModuleId)).SelectMany(t => FieldsOf(t.Module)).ToList();
                TourSteps.Add(new TourStep(introFields.Count > 0 ? TourKind.Fields : TourKind.Plain, menu, ViewMenu.Intro(menu) ?? "", "PageArea", () => ShowIntro(menu), introFields));
                continue;
            }
            if (Views.FirstOrDefault(v => $"view:{v.ModuleId}/{v.View.Id}" == item.Key) is { } inIntro && ViewMenu.Intro(inIntro.View.Menu ?? "") is not null
                && TopGroups.Any(g => g.Items.Any(i => i.Key == $"intro:{inIntro.View.Menu}"))) continue; // Already in the overview's step.
            if (Views.FirstOrDefault(v => $"view:{v.ModuleId}/{v.View.Id}" == item.Key) is not { View.Guide: { } guide } tab) continue;
            if (!_runner.IsEnabled(tab.Module)) continue;
            var fields = asked.Add(tab.ModuleId) ? FieldsOf(tab.Module) : [];
            TourSteps.Add(new TourStep(fields.Count > 0 ? TourKind.Fields : TourKind.Plain, tab.View.Title, guide, "PageArea", () => ShowView(tab), fields));
        }

        TourSteps.Add(new TourStep(TourKind.Notices, L.T("Gli avvisi", "The notices"),
            L.T("Quando una cosa lunga inizia o finisce, OptLife mostra un piccolo avviso sopra l'orologio (mai una notifica di Windows); durante un gioco aspetta. La campanella in alto dice cosa sta facendo.",
                "When something long starts or ends, OptLife shows a small notice above the clock (never a Windows notification); during a game it waits. The bell at the top says what it is doing."),
            "Bell", () => Page = PanelPage.Monitor,
            [
                new TourField(null, "notify.popup", L.T("Avviso sopra l'orologio", "Notice above the clock"), SettingKind.Bool, NotifyPopup ? "true" : "false", name: L.T("Avviso sopra l'orologio", "Notice above the clock")),
                new TourField(null, "notify.sound", L.T("Suono alla fine", "Sound at the end"), SettingKind.Bool, NotifySound ? "true" : "false", name: L.T("Suono alla fine", "Sound at the end")),
                new TourField(null, "notify.scheduler", L.T("Anche per i lavori del Pianificatore partiti da soli", "Also for Scheduler jobs that start by themselves"), SettingKind.Bool, NotifyScheduler ? "true" : "false", name: L.T("Avvisi per i lavori del Pianificatore", "Notices for Scheduler jobs")),
            ]));
        TourSteps.Add(new TourStep(TourKind.Summary, L.T("Riepilogo", "Summary"),
            L.T("Ecco le scelte. \"Cambia\" torna a quel passo; Fine le salva. Puoi rifare la visita da Impostazioni.",
                "Here are the choices. \"Change\" goes back to that step; Finish saves them. You can take the tour again from Settings."),
            "", () => Page = PanelPage.Monitor, []));
        for (var i = 0; i < TourSteps.Count; i++) TourSteps[i].Index = i;
        OnPropertyChanged(nameof(TourAnswered));
        ShowWelcome = true;
        GoToTourStep(Math.Clamp(at, 0, TourSteps.Count - 1));
    }

    /// <summary>The guided settings of a module, each with its answer ready: what the person chose, else the module's suggestion, else the default.</summary>
    private List<TourField> FieldsOf(IModule module)
    {
        var chosen = _config.GetModule(module.Info.Id).Settings;
        var current = _runner.SettingsOf(module);
        var list = new List<TourField>();
        foreach (var d in module.Settings.Where(d => d.Guide is not null && d.Kind is SettingKind.Bool or SettingKind.Choice or SettingKind.Text))
        {
            string value;
            if (chosen.ContainsKey(d.Key))
                value = d.Kind switch { SettingKind.Bool => current.GetBool(d.Key) ? "true" : "false", SettingKind.Choice => current.GetChoice(d.Key), _ => current.GetText(d.Key) };
            else
                value = _store.GetState(module.Info.Id, "suggest." + d.Key) is { Length: > 0 } suggested ? suggested
                    : d.Kind == SettingKind.Bool ? (d.Default is true ? "true" : "false") : d.Default?.ToString() ?? "";
            var picks = SettingOption.Read(_store.GetState(module.Info.Id, SettingOption.StateKey(d.Key)));
            list.Add(new TourField(module.Info.Id, d.Key, d.Guide!, d.Kind, value, d.Options, d.Pick, picks, d.Label));
        }
        return list;
    }

    private void GoToTourStep(int index)
    {
        TourIndex = index;
        TourStepNow?.Go?.Invoke();
        foreach (var s in TourSteps) s.Refresh();
        OnPropertyChanged(nameof(TourLookSummary));
        _config.Update(f => f.Ui[TourStepKey] = index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnPropertyChanged(nameof(TourHighlight));
    }

    [RelayCommand]
    private void TourNext()
    {
        if (TourIsLast) FinishWelcome();
        else GoToTourStep(TourIndex + 1);
    }

    [RelayCommand]
    private void TourBack()
    {
        if (!TourIsFirst) GoToTourStep(TourIndex - 1);
    }

    /// <summary>"Change" in the summary: back to that step.</summary>
    [RelayCommand]
    private void TourChange(TourStep step) => GoToTourStep(step.Index);

    /// <summary>Skip: nothing more is asked or saved (the look already chosen stays); the tour can be taken again from Settings.</summary>
    [RelayCommand]
    private void TourSkip() => EndTour(apply: false);

    [RelayCommand]
    private void StartTour() => BeginTour(0);

    /// <summary>Finish: every answer is saved, then Home.</summary>
    [RelayCommand]
    private void FinishWelcome() => EndTour(apply: true);

    private void EndTour(bool apply)
    {
        if (apply)
        {
            foreach (var field in TourSteps.SelectMany(s => s.Fields))
            {
                switch (field.Key)
                {
                    case "notify.popup": NotifyPopup = field.BoolValue; continue;
                    case "notify.sound": NotifySound = field.BoolValue; continue;
                    case "notify.scheduler": NotifyScheduler = field.BoolValue; continue;
                }
                if (field.ModuleId is not { } id) continue;
                var element = field.IsBool ? JsonSerializer.SerializeToElement(field.BoolValue) : JsonSerializer.SerializeToElement(field.Value.Trim());
                _config.UpdateModule(id, m => m.Settings[field.Key] = element);
            }
            foreach (var m in Modules) m.Reload();
        }
        _config.Update(f =>
        {
            f.Ui[WelcomeKey] = "1";
            f.Ui.Remove(TourStepKey);
        });
        ShowWelcome = false;
        Page = PanelPage.Monitor;
    }

    /// <summary>At start: the tour the first time, or again where a language change (which reopens the panel) left it.</summary>
    private void ResumeTour()
    {
        var ui = _config.Load().Ui;
        if (ui.ContainsKey(WelcomeKey)) return;
        BeginTour(int.TryParse(ui.GetValueOrDefault(TourStepKey), out var at) ? at : 0);
    }
}
