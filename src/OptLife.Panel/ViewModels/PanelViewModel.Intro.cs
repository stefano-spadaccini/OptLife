using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptLife.Core;

namespace OptLife.Panel.ViewModels;

/// <summary>One page of a side-bar entry, as a card of its overview: what it is for, how it stands now, a button to open it.</summary>
public sealed partial class IntroCard(ViewTab tab, Action<ViewTab> open) : ObservableObject
{
    public ViewTab Tab => tab;
    public string Title => tab.View.Title;

    /// <summary>What the page is for: its tour words, or else the first sentence of its description.</summary>
    public string Guide => tab.View.Guide ?? PanelViewModel.FirstSentence(tab.View.Description);

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _level = "info";
    public bool HasSummary => Summary.Length > 0;
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

    public string OpenText => L.T($"Apri {Title}", $"Open {Title}");

    [RelayCommand]
    private void Open() => open(tab);
}

/// <summary>
/// The overview of a side-bar entry with several pages (<see cref="ViewMenu.Intro"/>): how the pages work together, then one
/// card per page. It is the entry's first tab, and the guided tour's single step for the whole entry.
/// </summary>
public sealed partial class PanelViewModel
{
    [ObservableProperty] private string _introMenu = "";
    public ObservableCollection<IntroCard> IntroCards { get; } = [];
    public string IntroText => ViewMenu.Intro(IntroMenu) ?? "";

    private void ShowIntro(string menu)
    {
        IntroMenu = menu;
        IntroCards.Clear();
        foreach (var tab in Views.Where(v => (v.View.Menu ?? v.View.Title) == menu).OrderBy(v => v.View.Order))
            IntroCards.Add(new IntroCard(tab, ShowView));
        OnPropertyChanged(nameof(IntroText));
        if (Page == PanelPage.Intro) Refresh();
        else Page = PanelPage.Intro;
        UpdateNavigation();
    }

    /// <summary>How each page stands: a checklist's own summary of its latest round.</summary>
    private void RefreshIntro()
    {
        foreach (var card in IntroCards)
        {
            if (!card.Tab.IsChecklist) continue;
            card.Tab.Load(_store.QueryEvents(Now.AddDays(-card.Tab.View.Days), Now, card.Tab.ModuleId, card.Tab.View.EventKind, limit: 1));
            (card.Summary, card.Level) = (card.Tab.CheckSummary, card.Tab.CheckLevel);
        }
    }
}
