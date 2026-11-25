using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Modules.Scheduler;

namespace OptLife.Tests;

internal sealed class FakeJobProcess(int pid, int exitAfter, int code) : IJobProcess
{
    private int _looks;
    public int Pid => pid;
    // Ends after being looked at a few times, so a run takes some (fake) time.
    public bool HasExited => _looks++ >= exitAfter;
    public int? ExitCode => code;
}

internal sealed class FakeSchedulerEnvironment : ISchedulerEnvironment
{
    public DateTimeOffset BootTime { get; set; } = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Started { get; } = [];
    public List<int> Closed { get; } = [];
    public int ExitAfter { get; set; } = 3;
    public int ExitCode { get; set; }

    /// <summary>What the program's processes have moved so far; empty = not running.</summary>
    public Dictionary<int, long> Running { get; } = [];

    /// <summary>Called at every look, to change what the program does over time.</summary>
    public Action<FakeSchedulerEnvironment>? OnProbe { get; set; }

    public bool Exists(string file) => Files.Contains(file);

    /// <summary>Files someone who is not an administrator could change (the reason), for the jobs that run as administrator.</summary>
    public Dictionary<string, string> UnsafeFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Unsafe(Job job) => UnsafeFiles.GetValueOrDefault(job.File);

    public (IJobProcess? Process, string? Error) Start(Job job)
    {
        Started.Add(job.Name);
        var p = new FakeJobProcess(100 + Started.Count, ExitAfter, ExitCode);
        if (!job.IsScript) Running[p.Pid] = 0;
        return (p, null);
    }

    public IReadOnlyDictionary<int, long> Probe(Job job, int? rootPid)
    {
        OnProbe?.Invoke(this);
        return new Dictionary<int, long>(Running);
    }

    /// <summary>The process found again after a restart (null: it is gone).</summary>
    public IJobProcess? Attached { get; set; }
    public IJobProcess? Attach(int pid, DateTimeOffset startedBy) => Attached;

    public void Close(IReadOnlyCollection<int> pids)
    {
        Closed.AddRange(pids);
        foreach (var pid in pids) Running.Remove(pid);
    }
}

public sealed class SchedulerTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeSchedulerEnvironment _env = new();
    private readonly SchedulerModule _module;

    public SchedulerTests()
    {
        // Waiting between two looks moves the fake clock instead of the real one.
        _module = new SchedulerModule(_env, (span, _) =>
        {
            _box.Clock.Now += span;
            return Task.CompletedTask;
        });
        _box.Clock.Now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero); // A Sunday.
    }

    public void Dispose() => _box.Dispose();

    private void Jobs(params string[] lines) =>
        _box.Config.UpdateModule("scheduler", m => m.Settings["jobs"] = System.Text.Json.JsonSerializer.SerializeToElement(lines));

    private async Task Round()
    {
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        await _module.WhenDone();
    }

    private IReadOnlyList<StoredEvent> Runs() =>
        _box.Store.QueryEvents(_box.Clock.Now.AddDays(-30), _box.Clock.Now.AddDays(1), "scheduler", SchedulerModule.RunKind);

    [Fact]
    public void The_when_column_reads_both_languages_and_says_what_it_cannot_read()
    {
        var t = SchedulerRules.ParseWhen("14:00 + dom 19:00 + mon, thu 08:30 + ogni 4h + dopo Backup settimanale + avvio", out var bad);
        Assert.Null(bad);
        Assert.Equal([TriggerKind.Daily, TriggerKind.Weekly, TriggerKind.Weekly, TriggerKind.Every, TriggerKind.After, TriggerKind.Startup], t.Select(x => x.Kind));
        Assert.Equal([DayOfWeek.Sunday], t[1].Days);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], t[2].Days);
        Assert.Equal(TimeSpan.FromHours(4), t[3].Every);
        Assert.Equal("Backup settimanale", t[4].After);

        SchedulerRules.ParseWhen("14:00 + quando capita", out bad);
        Assert.Equal("quando capita", bad);
        var job = Assert.Single(SchedulerRules.Parse(["Sync | 25:00 | C:\\a.exe"]));
        Assert.NotNull(job.Problem);
    }

    [Fact]
    public void A_moment_missed_while_the_pc_was_off_is_made_up_once()
    {
        var job = SchedulerRules.Parse(["Backup | sun 19:00 | E:\\b.ps1"]).Single();
        var since = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 11, 22, 18, 59, 0, TimeSpan.Zero);
        Assert.Null(SchedulerRules.Reason(job, new JobState(since), sunday, since, _ => null));
        // Off at 19:00, on again on Monday morning: it runs then.
        var monday = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        Assert.NotNull(SchedulerRules.Reason(job, new JobState(since), monday, since, _ => null));
        Assert.Null(SchedulerRules.Reason(job, new JobState(since, LastStart: monday), monday.AddHours(3), since, _ => null));
        // Nothing before the job existed counts.
        Assert.Null(SchedulerRules.Reason(job, new JobState(monday), monday.AddHours(1), since, _ => null));
    }

    [Fact]
    public async Task A_script_runs_at_its_time_and_the_job_after_it_follows()
    {
        _env.Files.Add(@"E:\backup.ps1");
        _env.Files.Add(@"C:\NAS\nas.exe");
        Jobs(@"Backup | sun 19:00 | E:\backup.ps1", @"NAS sync | after Backup | C:\NAS\nas.exe | --autostart | 10 | 120");
        await Round(); // First look: nothing due yet.
        Assert.Empty(_env.Started);

        _box.Clock.Now = new DateTimeOffset(2026, 11, 22, 19, 0, 30, TimeSpan.Zero);
        await Round();
        Assert.Equal(["Backup"], _env.Started);
        Assert.Equal("ok", Runs().Single().Data["result"]);

        // The sync starts after it, moves data for a while, then goes quiet and is closed.
        var looks = 0;
        _env.OnProbe = e =>
        {
            looks++;
            foreach (var pid in e.Running.Keys.ToList()) e.Running[pid] += looks < 10 ? 50L << 20 : 1024;
        };
        await Round();
        Assert.Equal(["Backup", "NAS sync"], _env.Started);
        Assert.NotEmpty(_env.Closed);
        var sync = Runs().First(r => r.Data["job"] == "NAS sync");
        Assert.Equal("ok", sync.Data["result"]);
        Assert.Contains("MB", sync.Data["detail"]);
        Assert.InRange(long.Parse(sync.Data["seconds"]), 12 * 60, 20 * 60);
    }

    [Fact]
    public async Task Nothing_starts_during_a_game_unless_asked_and_a_failure_is_a_problem()
    {
        _env.Files.Add(@"E:\backup.ps1");
        Jobs(@"Backup | 13:00 | E:\backup.ps1");
        await Round();
        using var live = new LiveBoard($@"Local\OptLife.Test.{Guid.NewGuid():N}");
        var runner = new OptLife.Core.Hosting.ModuleRunner(_box.Store, _box.Config, _box.Clock, live);

        _box.Clock.Now = new DateTimeOffset(2026, 11, 22, 13, 0, 10, TimeSpan.Zero);
        live.Publish("games", [new LiveValue("game.fps", "FPS", "Game", 144, "")], _box.Clock.Now);
        Assert.True(await runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Empty(_env.Started);
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "scheduler", SchedulerModule.BoardKind).MaxBy(e => e.Id)!.Data);
        Assert.Equal(CheckStatus.Watch, board.Single().Status);

        // RUN NOW starts it anyway; this time the script fails.
        var asked = await runner.RunActionAsync(_module, _module.Actions.Single(), "test", true, "backup", CancellationToken.None);
        Assert.True(asked.Ok, asked.Detail);
        live.Publish("games", [new LiveValue("game.fps", "FPS", "Game", 144, "")], _box.Clock.Now);
        _env.ExitCode = 2;
        Assert.True(await runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        await _module.WhenDone();
        await Round();
        var finding = Assert.Single(_box.Store.GetFindings(), f => f.Module == "scheduler");
        Assert.Equal(SchedulerModule.RunNow, finding.Fix!.ActionId);
        Assert.Contains("2", finding.Detail);
    }

    [Fact]
    public async Task A_run_cut_short_by_a_restart_is_picked_up_and_the_program_still_closed()
    {
        _env.Files.Add(@"C:\NAS\nas.exe");
        Jobs(@"NAS sync | 13:00 | C:\NAS\nas.exe | | 10 | 60");
        await Round();
        // OptLife stopped while the sync was running: its state says "running" and the program is still open.
        var restarted = new SchedulerModule(_env, (span, _) => { _box.Clock.Now += span; return Task.CompletedTask; });
        _box.Store.SetState("scheduler", "job:nas sync", System.Text.Json.JsonSerializer.Serialize(new JobState(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddMinutes(-5), Running: true, Pid: 500)));
        _env.Running[500] = 0;
        Assert.True(await _box.Runner.RunTaskAsync(restarted, restarted.Tasks.Single(), CancellationToken.None));
        await restarted.WhenDone();
        Assert.Empty(_env.Started);
        Assert.Equal([500], _env.Closed);
        Assert.Equal("ok", Runs().Single().Data["result"]);
    }

    private void Set(string key, object value) =>
        _box.Config.UpdateModule("scheduler", m => m.Settings[key] = System.Text.Json.JsonSerializer.SerializeToElement(value));

    [Fact]
    public void Ready_made_jobs_are_all_off_for_anyone_and_need_their_files()
    {
        var settings = _box.Runner.SettingsOf(_module);
        Assert.Empty(SchedulerRules.Templates(settings));

        Set("tplDefender", true);
        Set("tplBackup", true);
        Set("tplSync", true);
        var jobs = SchedulerRules.Templates(_box.Runner.SettingsOf(_module));
        var defender = jobs.Single(j => j.Name == SchedulerRules.DefenderJob);
        Assert.True(defender.Admin && defender.IsScript && defender.Problem is null);
        Assert.NotNull(jobs.Single(j => j.Name == SchedulerRules.BackupJob).Problem); // No folders written yet.
        Assert.NotNull(jobs.Single(j => j.Name == SchedulerRules.SyncJob).Problem);    // No program written yet.
    }

    [Fact]
    public async Task The_ready_made_backup_never_deletes_and_counts_robocopy_successes_as_such()
    {
        Set("tplBackup", true);
        Set("tplBackupFrom", @"C:\Users\x\Documents");
        Set("tplBackupTo", @"E:\Copie");
        Set("tplBackupWhen", "sun 19:00");
        var job = SchedulerRules.All(_box.Runner.SettingsOf(_module)).Single();
        Assert.Contains("/E /XO", job.Arguments);
        Assert.DoesNotContain("/MIR", job.Arguments);
        Assert.DoesNotContain("/PURGE", job.Arguments);
        Assert.Contains(@"""E:\Copie\Documents""", job.Arguments);

        _env.Files.Add(job.File);
        _env.ExitCode = 3; // robocopy: files copied, some extra at the destination. It worked.
        await Round();
        _box.Clock.Now = new DateTimeOffset(2026, 11, 22, 19, 0, 30, TimeSpan.Zero);
        await Round();
        Assert.Equal([SchedulerRules.BackupJob], _env.Started);
        Assert.Equal("ok", Runs().Single().Data["result"]);
    }

    [Fact]
    public void A_job_written_by_the_person_wins_over_a_ready_made_one_with_the_same_name()
    {
        Set("tplDefender", true);
        Jobs($@"{SchedulerRules.DefenderJob} | every 168h | C:\mine.ps1");
        var job = Assert.Single(SchedulerRules.All(_box.Runner.SettingsOf(_module)));
        Assert.Equal(@"C:\mine.ps1", job.File);
    }

    /// <summary>A job run as administrator from a file a user could change would give that user full rights: it is not started.</summary>
    [Fact]
    public async Task An_administrator_job_whose_file_a_user_could_change_is_not_started()
    {
        _env.Files.Add(@"C:UsersPublicscan.cmd");
        _env.UnsafeFiles[@"C:UsersPublicscan.cmd"] = "Users can change it";
        Jobs(@"Scan | 13:00 | C:UsersPublicscan.cmd | | | 240 | yes");
        await Round();
        var asked = await _box.Runner.RunActionAsync(_module, _module.Actions.Single(), "test", true, "scan", CancellationToken.None);
        Assert.True(asked.Ok, asked.Detail);
        await Round();
        Assert.Empty(_env.Started);
        Assert.Contains("Users can change it", Runs().Single().Data["detail"]);
    }

    [Fact]
    public async Task A_script_still_running_when_optlife_restarts_is_followed_to_its_end()
    {
        _env.Files.Add(@"C:\scan.cmd");
        Jobs(@"Scan | every 720h | C:\scan.cmd | | | 240 | yes");
        // As OptLife left it when it closed: the scan started 10 minutes ago, process 4242.
        var started = _box.Clock.Now.AddMinutes(-10);
        _box.Store.SetState("scheduler", "job:scan", System.Text.Json.JsonSerializer.Serialize(new JobState(started.AddDays(-1), LastStart: started, Running: true, Pid: 4242)));
        _env.Attached = new FakeJobProcess(4242, exitAfter: 3, code: 0);

        await Round();
        var run = Runs().Single();
        Assert.Equal("ok", run.Data["result"]);
        Assert.Empty(_env.Started); // Not started again: the same run was waited for.
        Assert.Contains(_box.Store.GetActivities(), a => a.Title == "Scan" && a.State == ActivityState.Done);
    }

    [Fact]
    public async Task A_script_that_is_gone_after_a_restart_is_still_recorded_as_interrupted()
    {
        _env.Files.Add(@"C:\scan.cmd");
        Jobs(@"Scan | every 720h | C:\scan.cmd");
        _box.Store.SetState("scheduler", "job:scan", System.Text.Json.JsonSerializer.Serialize(new JobState(_box.Clock.Now.AddDays(-1), LastStart: _box.Clock.Now.AddMinutes(-10), Running: true, Pid: 4242)));
        await Round();
        Assert.Equal("warn", Runs().Single().Data["result"]);
    }
}
