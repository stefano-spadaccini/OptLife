using System.Globalization;
using System.Text.RegularExpressions;

namespace OptLife.Modules.Scheduler;

public enum TriggerKind { Daily, Weekly, Every, After, Startup }

/// <summary>One "when" of a job: a time of day (on some days of the week), a fixed gap, after another job, or once per Windows start.</summary>
public sealed record Trigger(TriggerKind Kind, TimeSpan At = default, IReadOnlyList<DayOfWeek>? Days = null, TimeSpan Every = default, string? After = null)
{
    public string Describe()
    {
        var at = $"{(int)At.TotalHours:00}:{At.Minutes:00}";
        return Kind switch
        {
            TriggerKind.Daily => L.T($"ogni giorno alle {at}", $"every day at {at}"),
            TriggerKind.Weekly => $"{string.Join(", ", Days!.Select(d => L.Culture.DateTimeFormat.GetAbbreviatedDayName(d)))} {at}",
            TriggerKind.Every => L.T($"ogni {SchedulerRules.Duration(Every)}", $"every {SchedulerRules.Duration(Every)}"),
            TriggerKind.After => L.T($"dopo «{After}»", $"after “{After}”"),
            _ => L.T("all'avvio di Windows", "at Windows start"),
        };
    }
}

/// <param name="IdleMinutes">For a program: close it once it has moved almost no data for this long (0 = start it and leave it).</param>
/// <param name="MaxMinutes">How long to wait at most: a script still running is left alone, a program is closed.</param>
/// <param name="Problem">What could not be read in the line, to tell the person; the job does not run until it is fixed.</param>
/// <param name="Waits">Waited for and judged by its exit code like a script, though it is a program (robocopy, Defender's scanner).</param>
/// <param name="OkBelow">Exit codes below this mean it worked (robocopy says 1-7 for "copied, all fine").</param>
public sealed record Job(string Name, IReadOnlyList<Trigger> Triggers, string File, string Arguments, int IdleMinutes, int MaxMinutes, bool Admin, string? Problem = null,
    bool Waits = false, int OkBelow = 1)
{
    private static readonly string[] ScriptExtensions = [".ps1", ".cmd", ".bat", ".vbs"];

    public bool IsScript => Waits || ScriptExtensions.Contains(Path.GetExtension(File), StringComparer.OrdinalIgnoreCase);

    /// <summary>A program watched until it goes quiet and then closed (a sync client), as opposed to one just started.</summary>
    public bool ClosesWhenIdle => !IsScript && IdleMinutes > 0;
}

public enum Outcome { Ok, Warn, Fail }

/// <summary>What the module remembers of a job between rounds and across restarts.</summary>
/// <param name="Since">When the job was first seen: nothing scheduled before that counts as missed.</param>
/// <param name="Running">True while a run is going on; still true after OptLife stopped in the middle of one.</param>
public sealed record JobState(DateTimeOffset Since, DateTimeOffset? LastStart = null, DateTimeOffset? LastEnd = null, Outcome? Last = null,
    string? Detail = null, bool Running = false, int? Pid = null);

/// <summary>The rules of the scheduler, with no machine behind them: reading the list, telling when a job is due, telling when a program has gone quiet.</summary>
public static class SchedulerRules
{
    private static readonly Regex AtTime = new(@"^(?:(?<days>.+?)\s+)?(?<h>\d{1,2})[:.](?<m>\d{2})$", RegexOptions.CultureInvariant);
    private static readonly Regex EveryGap = new(@"^(?:every|ogni)\s+(?<n>\d+)\s*(?<u>h|ore|ora|hours?|m|min|minuti|minutes?)$", RegexOptions.CultureInvariant);
    private static readonly string[] StartupWords = ["startup", "start", "avvio", "all'avvio", "at startup", "login", "logon", "accesso"];
    private static readonly string[] DailyWords = ["daily", "every day", "ogni giorno", "giornaliero", "sempre"];
    private static readonly string[] Yes = ["sì", "si", "yes", "y", "s", "true", "1"];

    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.Ordinal)
    {
        ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday, ["thu"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday, ["sun"] = DayOfWeek.Sunday,
        ["lun"] = DayOfWeek.Monday, ["mar"] = DayOfWeek.Tuesday, ["mer"] = DayOfWeek.Wednesday, ["gio"] = DayOfWeek.Thursday,
        ["ven"] = DayOfWeek.Friday, ["sab"] = DayOfWeek.Saturday, ["dom"] = DayOfWeek.Sunday,
    };

    /// <summary>"name | when | file | arguments | close when idle (min) | max (min) | admin": the first line with a name wins.</summary>
    public static IReadOnlyList<Job> Parse(IEnumerable<string> lines)
    {
        var jobs = new List<Job>();
        foreach (var line in lines)
        {
            var p = line.Split('|', StringSplitOptions.TrimEntries);
            if (p.Length < 3 || p[0].Length == 0 || p[2].Length == 0) continue;
            if (jobs.Any(j => j.Name.Equals(p[0], StringComparison.OrdinalIgnoreCase))) continue;
            var triggers = ParseWhen(p[1], out var bad);
            var idle = p.Length > 4 && int.TryParse(p[4], out var i) && i > 0 ? i : 0;
            var job = new Job(p[0], triggers, p[2], p.Length > 3 ? p[3] : "", idle, 0, p.Length > 6 && Yes.Contains(p[6].ToLowerInvariant()));
            var max = p.Length > 5 && int.TryParse(p[5], out var m) && m > 0 ? m : job.IsScript ? 60 : 120;
            var problem = bad is not null
                ? L.T($"non capisco quando: \"{bad}\"", $"I cannot read when: \"{bad}\"")
                : triggers.Count == 0 ? L.T("manca quando farlo", "it does not say when") : null;
            jobs.Add(job with { MaxMinutes = Math.Max(max, idle), Problem = problem });
        }
        return jobs;
    }

    // ── Ready-made jobs ──────────────────────────────────────────────────────

    public static string DefenderJob => L.T("Scansione completa Defender", "Defender full scan");
    public static string BackupJob => L.T("Copia di sicurezza", "Backup copy");
    public static string SyncJob => L.T("Sincronizzazione", "Sync");
    public static string ScriptJob => L.T("Script da riapplicare", "Script to reapply");

    /// <summary>
    /// The ready-made jobs switched on in the settings, for anyone: a monthly full Defender scan, a copy of a folder that never
    /// deletes anything at the destination, a sync program closed once it is done, a script run again and again. A job the person
    /// wrote with the same name wins. A switched-on one without its file is listed with the problem, and does not run.
    /// </summary>
    public static IReadOnlyList<Job> Templates(ISettings s)
    {
        var jobs = new List<Job>();
        Job Make(string name, string when, string file, string args, int idle, int max, bool admin, bool waits = false, int okBelow = 1, string? missing = null)
        {
            var triggers = ParseWhen(when, out var bad);
            var problem = missing ?? (bad is not null ? L.T($"non capisco quando: \"{bad}\"", $"I cannot read when: \"{bad}\"")
                : triggers.Count == 0 ? L.T("manca quando farlo", "it does not say when") : null);
            return new Job(name, triggers, file, args, idle, Math.Max(max, idle), admin, problem, waits, okBelow);
        }
        if (s.GetBool("tplDefender"))
            jobs.Add(Make(DefenderJob, "every 720h", @"%ProgramFiles%\Windows Defender\MpCmdRun.exe", "-Scan -ScanType 2", 0, 240, admin: true, waits: true));
        if (s.GetBool("tplBackup"))
        {
            var from = s.GetText("tplBackupFrom").Trim().Trim('"').TrimEnd('\\');
            var to = s.GetText("tplBackupTo").Trim().Trim('"').TrimEnd('\\');
            var leaf = from.Length > 0 ? Path.GetFileName(from) : "";
            // robocopy /E /XO: copies what is new or newer, never deletes at the destination (no /MIR, no /PURGE). 0-7 = it worked.
            jobs.Add(Make(BackupJob, s.GetText("tplBackupWhen"), @"%SystemRoot%\System32\robocopy.exe",
                $"\"{from}\" \"{Path.Combine(to, leaf)}\" /E /XO /XJ /R:1 /W:5 /NP /NJH", 0, 240, admin: false, waits: true, okBelow: 8,
                missing: from.Length == 0 || to.Length == 0 ? L.T("mancano la cartella da copiare o quella dove copiare", "the folder to copy or the destination is missing") : null));
        }
        if (s.GetBool("tplSync"))
        {
            var program = s.GetText("tplSyncProgram").Trim().Trim('"');
            jobs.Add(Make(SyncJob, s.GetText("tplSyncWhen"), program.Length > 0 ? program : "?", s.GetText("tplSyncArguments").Trim(),
                Math.Max(1, s.GetInt("tplSyncIdle")), 120, admin: false,
                missing: program.Length == 0 ? L.T("manca il programma di sincronizzazione", "the sync program is missing") : null));
        }
        if (s.GetBool("tplScript"))
        {
            var script = s.GetText("tplScriptFile").Trim().Trim('"');
            jobs.Add(Make(ScriptJob, s.GetText("tplScriptWhen"), script.Length > 0 ? script : "?.ps1", "", 0, 10, s.GetBool("tplScriptAdmin"),
                missing: script.Length == 0 ? L.T("manca lo script", "the script is missing") : null));
        }
        return jobs;
    }

    /// <summary>The person's own jobs, then the ready-made ones switched on (unless one of theirs has the same name).</summary>
    public static IReadOnlyList<Job> All(ISettings s)
    {
        var own = Parse(s.GetList("jobs"));
        return [.. own, .. Templates(s).Where(t => !own.Any(j => j.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>Several "when"s joined by '+': "14:00", "sun 19:00", "mon, thu 08:30", "every 4h", "after Backup", "startup".</summary>
    public static IReadOnlyList<Trigger> ParseWhen(string text, out string? bad)
    {
        bad = null;
        var list = new List<Trigger>();
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = Regex.Replace(raw.ToLowerInvariant(), @"\s+", " ");
            if (StartupWords.Contains(part))
            {
                list.Add(new Trigger(TriggerKind.Startup));
                continue;
            }
            if (part.StartsWith("after ", StringComparison.Ordinal) || part.StartsWith("dopo ", StringComparison.Ordinal))
            {
                list.Add(new Trigger(TriggerKind.After, After: raw[(raw.IndexOf(' ') + 1)..].Trim()));
                continue;
            }
            if (EveryGap.Match(part) is { Success: true } e)
            {
                var n = int.Parse(e.Groups["n"].Value, CultureInfo.InvariantCulture);
                var gap = e.Groups["u"].Value.StartsWith('m') ? TimeSpan.FromMinutes(n) : TimeSpan.FromHours(n);
                if (gap >= TimeSpan.FromMinutes(5))
                {
                    list.Add(new Trigger(TriggerKind.Every, Every: gap));
                    continue;
                }
            }
            if (AtTime.Match(part) is { Success: true } t
                && int.Parse(t.Groups["h"].Value, CultureInfo.InvariantCulture) is var h and < 24
                && int.Parse(t.Groups["m"].Value, CultureInfo.InvariantCulture) is var min and < 60)
            {
                var at = new TimeSpan(h, min, 0);
                var days = t.Groups["days"].Value.Trim();
                if (days.Length == 0 || DailyWords.Contains(days))
                {
                    list.Add(new Trigger(TriggerKind.Daily, at));
                    continue;
                }
                var parsed = days.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Length >= 3 && DayNames.TryGetValue(d[..3], out var day) ? day : (DayOfWeek?)null).ToList();
                if (parsed.All(d => d is not null))
                {
                    list.Add(new Trigger(TriggerKind.Weekly, at, [.. parsed.Select(d => d!.Value).Distinct().Order()]));
                    continue;
                }
            }
            bad ??= raw;
        }
        return list;
    }

    /// <summary>The most recent moment, not after now, that a timed trigger fell on.</summary>
    public static DateTimeOffset? LastScheduled(Trigger t, DateTimeOffset now)
    {
        if (t.Kind is not (TriggerKind.Daily or TriggerKind.Weekly)) return null;
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(-d);
            var at = new DateTimeOffset(day + t.At, now.Offset);
            if (at <= now && (t.Kind == TriggerKind.Daily || t.Days!.Contains(day.DayOfWeek))) return at;
        }
        return null;
    }

    /// <summary>The next moment after now that a timed trigger falls on.</summary>
    public static DateTimeOffset? NextScheduled(Trigger t, DateTimeOffset now)
    {
        if (t.Kind is not (TriggerKind.Daily or TriggerKind.Weekly)) return null;
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(d);
            var at = new DateTimeOffset(day + t.At, now.Offset);
            if (at > now && (t.Kind == TriggerKind.Daily || t.Days!.Contains(day.DayOfWeek))) return at;
        }
        return null;
    }

    /// <summary>
    /// Why the job should run now, or null. A moment missed while the PC was off is made up once, at the next
    /// chance; nothing before the job was first seen counts.
    /// </summary>
    /// <param name="finished">When the job with that name last finished, for "after".</param>
    public static string? Reason(Job job, JobState state, DateTimeOffset now, DateTimeOffset boot, Func<string, DateTimeOffset?> finished)
    {
        var reference = state.LastStart ?? state.Since;
        foreach (var t in job.Triggers)
        {
            switch (t.Kind)
            {
                case TriggerKind.Daily or TriggerKind.Weekly when LastScheduled(t, now) is { } at && at > reference:
                    return now - at < TimeSpan.FromMinutes(2)
                        ? L.T($"in programma alle {at:HH:mm}", $"scheduled at {at:HH:mm}")
                        : L.T($"recupero: era in programma {Moment(at, now)}", $"catching up: it was due {Moment(at, now)}");
                case TriggerKind.Every when now - reference >= t.Every:
                    return t.Describe();
                case TriggerKind.Startup when reference < boot:
                    return t.Describe();
                case TriggerKind.After when finished(t.After!) is { } f && f > reference:
                    return t.Describe();
            }
        }
        return null;
    }

    /// <summary>When it will run next, in words, for the page.</summary>
    public static string Next(Job job, JobState state, DateTimeOffset now)
    {
        var times = job.Triggers.Select(t => t.Kind switch
        {
            TriggerKind.Every => (state.LastStart ?? state.Since) + t.Every,
            _ => NextScheduled(t, now),
        }).OfType<DateTimeOffset>().ToList();
        var words = job.Triggers.Where(t => t.Kind is TriggerKind.After or TriggerKind.Startup).Select(t => t.Describe()).ToList();
        if (times.Count > 0) words.Insert(0, Moment(times.Min() < now ? now : times.Min(), now));
        return string.Join(L.T(" e ", " and "), words);
    }

    /// <summary>
    /// True when the program has moved almost no data in the last <paramref name="idleMinutes"/> minutes (and has been
    /// watched at least that long).
    /// </summary>
    /// <param name="samples">Data moved since the start, in bytes, as read over time (oldest first).</param>
    public static bool IsIdle(IReadOnlyList<(DateTimeOffset At, long Bytes)> samples, DateTimeOffset now, int idleMinutes, int kbPerSecond)
    {
        if (samples.Count == 0 || idleMinutes <= 0) return false;
        var from = now - TimeSpan.FromMinutes(idleMinutes);
        if (samples[0].At > from) return false; // Not watched long enough yet.
        var before = samples.Last(s => s.At <= from);
        var moved = samples[^1].Bytes - before.Bytes;
        return moved <= (long)kbPerSecond * 1024 * idleMinutes * 60;
    }

    public static string Duration(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{Math.Max(0, (int)span.TotalSeconds)} s"
        : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min"
        : span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", L.Culture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", L.Culture) + " MB";

    /// <summary>"oggi 14:00", "ieri 19:00", "dom 29 nov 19:00".</summary>
    public static string Moment(DateTimeOffset t, DateTimeOffset now)
    {
        var day = (t.Date - now.Date).Days switch
        {
            0 => L.T("oggi", "today"),
            -1 => L.T("ieri", "yesterday"),
            1 => L.T("domani", "tomorrow"),
            _ => t.ToString("ddd d MMM", L.Culture),
        };
        return $"{day} {t:HH:mm}";
    }
}
