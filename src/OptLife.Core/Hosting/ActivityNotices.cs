using OptLife.Core.Config;

namespace OptLife.Core.Hosting;

/// <summary>A pop-up to show: an activity started, or ended (well or not).</summary>
public sealed record ActivityNotice(long Id, bool Ended, ActivityState State, string Title, string Text);

/// <summary>
/// Decides which activities deserve the agent's pop-up, so that a long job is announced when it starts and when it ends, and the
/// small things (opening a site, a quick switch) stay quiet. Kept apart from the window so it can be tested.
/// </summary>
public sealed class ActivityNotices
{
    /// <summary>Running at least this long before "started" is worth saying.</summary>
    public static readonly TimeSpan StartAfter = TimeSpan.FromSeconds(3);

    /// <summary>Lasting at least this long before "done" is worth saying (a failure is always said).</summary>
    public static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(10);

    private readonly HashSet<long> _started = [];
    private readonly HashSet<long> _ended = [];
    private bool _baseline;

    /// <param name="playing">A game is running: nothing is shown now, it is all said once the game is over.</param>
    public IReadOnlyList<ActivityNotice> Next(IReadOnlyList<StoredActivity> activities, NotificationSettings settings, DateTimeOffset now, bool playing)
    {
        // What was there before the agent started was never "new": no pop-ups for the past.
        if (!_baseline)
        {
            _baseline = true;
            foreach (var a in activities)
            {
                _started.Add(a.Id);
                if (a.State != ActivityState.Running) _ended.Add(a.Id);
            }
            return [];
        }
        if (!settings.Popup || playing) return [];

        var list = new List<ActivityNotice>();
        foreach (var a in activities.OrderBy(a => a.Id))
        {
            if (a.Actor == "scheduler" && !settings.Scheduler) continue;
            var elapsed = a.Elapsed(now);
            if (a.State == ActivityState.Running)
            {
                if (elapsed < StartAfter || !_started.Add(a.Id)) continue;
                var guess = a.Estimate is { } e ? L.T($" Di solito ci mette {Duration(e)}.", $" It usually takes {Duration(e)}.") : "";
                list.Add(new ActivityNotice(a.Id, false, a.State, L.T($"Iniziato: {a.Title}", $"Started: {a.Title}"),
                    (a.Progress is { } p ? Step(a, p) : L.T("In corso.", "In progress.")) + guess));
                continue;
            }
            if (_ended.Contains(a.Id)) continue;
            _ended.Add(a.Id);
            _started.Add(a.Id);
            if (a.State == ActivityState.Done && elapsed < EndAfter) continue;
            var title = a.State switch
            {
                ActivityState.Done => L.T($"Finito: {a.Title}", $"Done: {a.Title}"),
                ActivityState.Failed => L.T($"Non riuscito: {a.Title}", $"Failed: {a.Title}"),
                _ => L.T($"Interrotto: {a.Title}", $"Stopped: {a.Title}"),
            };
            list.Add(new ActivityNotice(a.Id, true, a.State, title, L.T($"In {Duration(elapsed)}. ", $"In {Duration(elapsed)}. ") + (a.Detail ?? "")));
        }
        return list;
    }

    public static string Step(StoredActivity a, string text) =>
        a.Done is { } d && a.Total is { } t ? $"{text} ({d}/{t})" : text;

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? L.T($"{(int)t.TotalSeconds} s", $"{(int)t.TotalSeconds} s")
        : t.TotalMinutes < 60 ? L.T($"{(int)t.TotalMinutes} min", $"{(int)t.TotalMinutes} min")
        : L.T($"{(int)t.TotalHours} h {t.Minutes} min", $"{(int)t.TotalHours} h {t.Minutes} min");
}
