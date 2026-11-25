using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OptLife.Core;
using OptLife.Core.Hosting;

namespace OptLife.Panel.ViewModels;

/// <summary>
/// The Activity page: what OptLife is doing right now (with how far it has got and a guess of the end) and what it did lately,
/// whoever started it; plus the switches of the agent's pop-up. The count of running ones is always in sight at the top.
/// </summary>
public sealed partial class PanelViewModel
{
    public ObservableCollection<ActivityItem> Activities { get; } = [];

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private string _runningText = "";
    [ObservableProperty] private string _activityEmptyText = "";
    private string _activitiesShown = "";

    public bool NotifyPopup
    {
        get => _config.Load().Notifications.Popup;
        set { _config.Update(f => f.Notifications.Popup = value); OnPropertyChanged(); }
    }

    public bool NotifySound
    {
        get => _config.Load().Notifications.Sound;
        set { _config.Update(f => f.Notifications.Sound = value); OnPropertyChanged(); }
    }

    public bool NotifyScheduler
    {
        get => _config.Load().Notifications.Scheduler;
        set { _config.Update(f => f.Notifications.Scheduler = value); OnPropertyChanged(); }
    }

    /// <summary>Called at every refresh: the count for the top of the window, and the page when it is shown.</summary>
    private void RefreshActivities()
    {
        IReadOnlyList<StoredActivity> list;
        try
        {
            list = _store.GetActivities(60);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return;
        }
        RunningCount = list.Count(a => a.State == ActivityState.Running);
        RunningText = RunningCount switch
        {
            0 => "",
            1 => L.T("1 attività in corso", "1 activity running"),
            var n => L.T($"{n} attività in corso", $"{n} activities running"),
        };
        RefreshRunning(list);
        if (Page != PanelPage.Activity) return;
        ActivityEmptyText = list.Count > 0 ? "" : L.T("Nessuna attività finora. Qui compare ciò che OptLife sta facendo o ha fatto: aggiornamenti, installazioni, scansioni, lavori del Pianificatore.",
            "No activity so far. What OptLife is doing or did shows up here: updates, installs, scans, Scheduler jobs.");
        // Rebuilt when something moved; running ones every time, for their elapsed time.
        var signature = string.Join('|', list.Select(a => $"{a.Id}:{a.State}:{a.Progress}:{a.Done}")) + (RunningCount > 0 ? Now.ToUnixTimeSeconds() / 10 : 0);
        if (signature == _activitiesShown) return;
        _activitiesShown = signature;
        Activities.Clear();
        foreach (var a in list.OrderBy(a => a.State == ActivityState.Running ? 0 : 1).ThenByDescending(a => a.Id))
            Activities.Add(new ActivityItem(a, ModuleName(a.Module), Now));
    }
}

/// <summary>One activity, as a line of the page.</summary>
public sealed class ActivityItem(StoredActivity a, string moduleName, DateTimeOffset now)
{
    public string Title => a.Title;

    public string Source => $"{moduleName} · " + a.Actor switch
    {
        "person" => L.T("avviata da te", "started by you"),
        "scheduler" => L.T("dal Pianificatore", "by the Scheduler"),
        _ => L.T("da sola", "by itself"),
    };

    public bool Running => a.State == ActivityState.Running;

    public string Level => a.State switch
    {
        ActivityState.Running => "info",
        ActivityState.Done => "good",
        ActivityState.Failed => "high",
        _ => "medium",
    };

    public string Outcome => a.State switch
    {
        ActivityState.Running => L.T("IN CORSO", "RUNNING"),
        ActivityState.Done => L.T("FATTO", "DONE"),
        ActivityState.Failed => L.T("NON RIUSCITO", "FAILED"),
        _ => L.T("INTERROTTO", "STOPPED"),
    };

    /// <summary>"Updating GOG Galaxy (7/22)".</summary>
    public string Step => a.Progress is { } p && Running ? ActivityNotices.Step(a, p) : "";
    public bool HasStep => Step.Length > 0;

    public double Fraction => a.Done is { } d && a.Total is > 0 ? (double)d / a.Total.Value : 0;
    public bool HasFraction => Running && a.Total is > 0;

    /// <summary>When it started and how long it has been going (with the usual length), or when it ended and how long it took.</summary>
    public string When
    {
        get
        {
            var start = a.Start.ToLocalTime().ToString("d MMM HH:mm", L.Culture);
            if (Running)
                return L.T($"Iniziata {start} · da {ActivityNotices.Duration(a.Elapsed(now))}", $"Started {start} · for {ActivityNotices.Duration(a.Elapsed(now))}")
                       + (a.Estimate is { } e ? L.T($" · di solito ci mette {ActivityNotices.Duration(e)}", $" · usually takes {ActivityNotices.Duration(e)}") : "");
            var end = (a.End ?? a.Updated).ToLocalTime().ToString("HH:mm", L.Culture);
            return a.State == ActivityState.Interrupted
                ? L.T($"Iniziata {start}: il programma che la faceva si è chiuso prima della fine", $"Started {start}: the program doing it closed before the end")
                : L.T($"{start} → {end}, in {ActivityNotices.Duration(a.Elapsed(now))}", $"{start} → {end}, in {ActivityNotices.Duration(a.Elapsed(now))}");
        }
    }

    public string Detail => Running ? "" : a.Detail ?? "";
    public bool HasDetail => Detail.Length > 0;
}
