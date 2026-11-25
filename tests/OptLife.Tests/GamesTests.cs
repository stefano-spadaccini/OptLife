using System.IO.MemoryMappedFiles;
using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Games;
using OptLife.Modules.Games.Windows;

namespace OptLife.Tests;

/// <summary>A scripted PC: a game that runs for a set time, readings and key presses decided by the test.</summary>
internal sealed class FakeGameEnvironment(FakeClock clock) : IGameEnvironment, ISessionSource
{
    private DateTimeOffset? _startedAt;

    public GameProcess? Game { get; set; } = new(4242, "RocketLeague", @"E:\racinggame\RocketLeague.exe");
    public TimeSpan RunsFor { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Seconds into the session at which the lag key is pressed.</summary>
    public List<int> MarkAt { get; } = [];

    /// <summary>Readings as a function of the second of the session.</summary>
    public Func<int, Dictionary<string, double>> Reading { get; set; } = _ => new() { ["fps"] = 100 };

    public int Beeps { get; private set; }
    public bool SourceDisposed { get; private set; }
    public CancellationTokenSource? CancelAfter { get; set; }
    public TimeSpan CancelAt { get; set; }

    private int Second => (int)(clock.Now - _startedAt!.Value).TotalSeconds;

    public GameProcess? FindGame(IReadOnlyList<string> folders, IReadOnlyList<string> names, IReadOnlyList<string> ignored) => Game;

    /// <summary>Which game's window is in front, as a function of the second of the session; null = none or the recorded one.</summary>
    public Func<int, GameProcess?> Front { get; set; } = _ => null;

    public GameProcess? ForegroundGame(IReadOnlyList<string> folders, IReadOnlyList<string> names, IReadOnlyList<string> ignored) =>
        _startedAt is null ? null : Front(Second);

    public bool IsRunning(GameProcess game) => clock.Now - _startedAt!.Value < RunsFor;

    public ServerHint? LastServerHint { get; private set; }

    public ISessionSource OpenSource(GameProcess game, ServerHint server)
    {
        LastServerHint = server;
        _startedAt = clock.Now;
        return this;
    }

    // Held for the first half of the marked second, like a real key tap.
    public bool IsFunctionKeyDown(int number) =>
        _startedAt is not null && MarkAt.Contains(Second) && (clock.Now - _startedAt.Value).Milliseconds < 500;

    public void Beep() => Beeps++;

    public Task Delay(TimeSpan time, CancellationToken ct)
    {
        clock.Now += time;
        if (CancelAfter is not null && clock.Now - _startedAt!.Value >= CancelAt) CancelAfter.Cancel();
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, double> Read() => Reading(Second);

    public void Dispose() => SourceDisposed = true;
}

public class GamesModuleTests
{
    private static async Task Watch(Sandbox box, GamesModule module) =>
        Assert.True(await box.Runner.RunTaskAsync(module, module.Tasks[0], CancellationToken.None),
            box.Runner.ReadStatus(module, module.Tasks[0]).LastError);

    private static IReadOnlyList<StoredEvent> Events(Sandbox box, string kind) =>
        box.Store.QueryEvents(box.Clock.Now.AddDays(-1), box.Clock.Now.AddDays(1), kind: kind);

    [Fact]
    public async Task A_session_becomes_one_line_in_the_history_with_its_frame_rate()
    {
        using var box = new Sandbox();
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromMinutes(47) };
        env.Reading = second => new() { ["fps"] = second < 60 ? 60 : 144 };

        await Watch(box, new GamesModule(env));

        var session = Assert.Single(Events(box, GamesRules.SessionKind));
        Assert.StartsWith("RocketLeague: 47 min, 142 FPS medi (minimo sostenuto 60)", session.Title);
        Assert.Equal("RocketLeague", session.Data["game"]);
        Assert.Equal("0", session.Data["marks"]);
        Assert.True(File.Exists(session.Data["file"]));
        Assert.Equal(47 * 60 + 2, File.ReadAllLines(session.Data["file"]).Length); // header + one row per second
        Assert.True(env.SourceDisposed);
        // The frame rate also gets its per-minute history, like any other sensor.
        Assert.Equal(47, box.Store.QuerySamples("game.fps", box.Clock.Now.AddHours(-2), box.Clock.Now).Count);
    }

    [Fact]
    public async Task A_program_that_closes_right_away_leaves_no_trace()
    {
        using var box = new Sandbox();
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromSeconds(20) };

        await Watch(box, new GamesModule(env));

        Assert.Empty(Events(box, GamesRules.SessionKind));
    }

    [Fact]
    public async Task A_launcher_left_open_gives_way_to_the_game_played_in_front_of_it()
    {
        using var box = new Sandbox();
        var launcher = new GameProcess(1, "QualcheLauncher", @"E:\Games\Qualche\QualcheLauncher.exe");
        var game = new GameProcess(2, "Overwatch", @"E:\shootergame\Overwatch.exe");
        var env = new FakeGameEnvironment(box.Clock) { Game = launcher, RunsFor = TimeSpan.FromHours(4) };
        // The real game takes the screen 100 seconds in, with a short alt-tab away at 200-209 that must not matter.
        env.Front = second => second >= 100 ? game : null;
        var module = new GamesModule(env);

        await Watch(box, module);

        var first = Assert.Single(Events(box, GamesRules.SessionKind));
        Assert.Equal("QualcheLauncher", first.Data["game"]);
        Assert.InRange(int.Parse(first.Data["seconds"]), 120, 135); // Three checks ten seconds apart after second 100.

        env.Game = game;
        env.RunsFor = TimeSpan.FromMinutes(10);
        env.Front = _ => null;
        await Watch(box, module);

        Assert.Contains(Events(box, GamesRules.SessionKind), e => e.Data["game"] == "Overwatch" && e.Data["seconds"] == "601");
    }

    [Fact]
    public async Task A_brief_look_at_another_game_window_does_not_end_the_session()
    {
        using var box = new Sandbox();
        var other = new GameProcess(2, "AltroGioco", @"E:\Games\Altro\AltroGioco.exe");
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromMinutes(5) };
        env.Front = second => second is >= 100 and < 115 ? other : null; // 15 seconds: fewer than three checks in a row.

        await Watch(box, new GamesModule(env));

        Assert.Equal("301", Assert.Single(Events(box, GamesRules.SessionKind)).Data["seconds"]);
    }

    [Fact]
    public async Task With_no_game_running_nothing_happens()
    {
        using var box = new Sandbox();
        var env = new FakeGameEnvironment(box.Clock) { Game = null };

        await Watch(box, new GamesModule(env));

        Assert.Empty(Events(box, GamesRules.SessionKind));
    }

    [Fact]
    public async Task A_game_without_frame_data_is_still_recorded_and_says_so()
    {
        using var box = new Sandbox();
        var env = new FakeGameEnvironment(box.Clock) { Reading = _ => new() { ["cpu_tot"] = 40 } };

        await Watch(box, new GamesModule(env));

        Assert.Contains("frame non disponibili", Assert.Single(Events(box, GamesRules.SessionKind)).Title);
    }

    [Fact]
    public async Task Each_press_of_the_key_marks_one_lag_and_confirms_with_a_sound()
    {
        using var box = new Sandbox();
        var env = new FakeGameEnvironment(box.Clock);
        env.MarkAt.AddRange([30, 31, 90]); // 30-31: held across two seconds would be one press; here two taps
        var start = box.Clock.Now;

        await Watch(box, new GamesModule(env));

        Assert.Equal("3", Assert.Single(Events(box, GamesRules.SessionKind)).Data["marks"]);
        Assert.Equal(3, env.Beeps);
        var marks = Events(box, "lag_mark");
        Assert.Equal([start.AddSeconds(30), start.AddSeconds(31), start.AddSeconds(90)], marks.Select(m => m.Time).Order());
    }

    [Fact]
    public async Task The_sound_can_be_turned_off()
    {
        using var box = new Sandbox();
        box.Config.UpdateModule("games", c => c.Settings["markSound"] = JsonSerializer.SerializeToElement(false));
        var env = new FakeGameEnvironment(box.Clock);
        env.MarkAt.Add(30);

        await Watch(box, new GamesModule(env));

        Assert.Equal(0, env.Beeps);
        Assert.Equal("1", Assert.Single(Events(box, GamesRules.SessionKind)).Data["marks"]);
    }

    [Fact]
    public async Task What_keeps_happening_right_before_the_marked_lags_is_named_as_the_suspect()
    {
        using var box = new Sandbox();
        box.Config.UpdateModule("games", c => c.Settings["minMarks"] = JsonSerializer.SerializeToElement(5));
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromMinutes(20) };
        int[] lags = [100, 250, 400, 550, 700, 850, 1000];
        env.MarkAt.AddRange(lags);
        // Windows re-reads memory from disk in the two seconds before every lag; the network stays flat.
        env.Reading = second => new()
        {
            ["fps"] = 120,
            ["pages_in"] = lags.Any(l => second >= l - 2 && second < l) ? 9000 : 20,
            ["ping_gw"] = 2,
            ["avail_mb"] = 3000,
        };

        await Watch(box, new GamesModule(env));

        var finding = Assert.Single(box.Store.GetFindings());
        Assert.Equal("Lag in RocketLeague: l'indiziato è Windows che rilegge memoria dal disco", finding.Title);
        Assert.Contains("7 lag segnati", finding.Detail);
        Assert.Contains("100%", finding.Detail);
        Assert.Contains("RAM", finding.Advice);
    }

    [Fact]
    public async Task Too_few_marked_lags_draw_no_conclusion()
    {
        using var box = new Sandbox();
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromMinutes(10) };
        env.MarkAt.AddRange([100, 300]);
        env.Reading = second => new() { ["pages_in"] = second is 98 or 99 or 298 or 299 ? 9000 : 20 };

        await Watch(box, new GamesModule(env));

        Assert.Empty(box.Store.GetFindings());
    }

    [Fact]
    public async Task Enough_marked_lags_with_nothing_unusual_before_them_say_so()
    {
        using var box = new Sandbox();
        box.Config.UpdateModule("games", c => c.Settings["minMarks"] = JsonSerializer.SerializeToElement(3));
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromMinutes(10) };
        env.MarkAt.AddRange([100, 200, 300, 400]);
        env.Reading = _ => new() { ["fps"] = 120, ["pages_in"] = 20, ["ping_gw"] = 2 };

        await Watch(box, new GamesModule(env));

        var finding = Assert.Single(box.Store.GetFindings());
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("nessuna causa evidente", finding.Title);
    }

    [Fact]
    public async Task If_optlife_closes_mid_game_what_was_recorded_is_kept()
    {
        using var box = new Sandbox();
        using var cts = new CancellationTokenSource();
        var env = new FakeGameEnvironment(box.Clock) { RunsFor = TimeSpan.FromHours(3), CancelAfter = cts, CancelAt = TimeSpan.FromMinutes(5) };
        var module = new GamesModule(env);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => box.Runner.RunTaskAsync(module, module.Tasks[0], cts.Token));

        Assert.StartsWith("RocketLeague: 5 min", Assert.Single(Events(box, GamesRules.SessionKind)).Title);
    }

    [Fact]
    public async Task Only_the_newest_detailed_recordings_are_kept_on_disk()
    {
        using var box = new Sandbox();
        box.Config.UpdateModule("games", c => c.Settings["keepSessions"] = JsonSerializer.SerializeToElement(2));
        var module = new GamesModule(new FakeGameEnvironment(box.Clock));

        for (var i = 0; i < 4; i++)
        {
            await Watch(box, module);
            box.Clock.Now = box.Clock.Now.AddMinutes(10);
        }

        var sessions = Events(box, GamesRules.SessionKind);
        Assert.Equal(4, sessions.Count);
        Assert.Equal(2, sessions.Count(s => File.Exists(s.Data["file"])));
        Assert.True(File.Exists(sessions.MaxBy(s => s.Time)!.Data["file"]));
    }
}

public class SessionRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_metric_that_spikes_before_every_mark_stands_out_and_a_flat_one_does_not()
    {
        var recorder = new SessionRecorder(Start);
        int[] marks = [50, 150, 250, 350, 450];
        for (var s = 0; s < 600; s++)
        {
            var spike = marks.Any(m => s == m - 1);
            recorder.Add(Start.AddSeconds(s), new Dictionary<string, double> { ["ping_gw"] = spike ? 180 : 3, ["cpu_tot"] = 35 });
        }
        foreach (var m in marks) recorder.Mark(Start.AddSeconds(m));

        var summary = recorder.Summarize();

        var ping = summary.Verdicts.Single(v => v.Metric == "ping_gw_max");
        Assert.Equal(5, ping.Hits);
        Assert.Equal(1.0, ping.HitRate);
        Assert.True(ping.Lift >= 10, $"lift {ping.Lift}");
        Assert.Equal("ping_gw_max", summary.Verdicts[0].Metric); // strongest suspect first
        Assert.Equal(0, summary.Verdicts.Single(v => v.Metric == "cpu_tot").Hits);
    }

    [Fact]
    public void Each_second_keeps_its_worst_reading_and_inverted_metrics_point_the_right_way()
    {
        var recorder = new SessionRecorder(Start);
        recorder.Add(Start.AddMilliseconds(100), new Dictionary<string, double> { ["ping_ext"] = 20, ["avail_mb"] = 4000, ["gpu_util"] = 98 });
        recorder.Add(Start.AddMilliseconds(600), new Dictionary<string, double> { ["ping_ext"] = 90, ["avail_mb"] = 500, ["gpu_util"] = 40, ["ping_ext_lost"] = 1 });
        var path = Path.Combine(Path.GetTempPath(), $"optlife-rec-{Guid.NewGuid():N}.csv");
        try
        {
            recorder.WriteCsv(path);
            var header = File.ReadAllLines(path)[0].Split(',');
            var row = File.ReadAllLines(path)[1].Split(',');
            string Cell(string column) => row[Array.IndexOf(header, column)];

            Assert.Equal("90", Cell("ping_ext_max"));
            Assert.Equal("1", Cell("ping_ext_loss"));
            Assert.Equal("99500", Cell("ram_low"));  // less memory available = higher = worse
            Assert.Equal("60", Cell("gpu_idle"));    // the card waiting = higher = worse
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Frame_rate_is_averaged_per_second_and_the_low_figure_ignores_seconds_without_frames()
    {
        var recorder = new SessionRecorder(Start);
        for (var s = 0; s < 200; s++)
        {
            if (s is >= 50 and < 60) continue; // alt-tabbed: no frames reported
            recorder.Add(Start.AddSeconds(s), new Dictionary<string, double> { ["fps"] = s < 2 ? 30 : 100 });
            recorder.Add(Start.AddSeconds(s).AddMilliseconds(500), new Dictionary<string, double> { ["fps"] = s < 2 ? 50 : 100 });
        }

        var summary = recorder.Summarize();

        Assert.True(summary.HasFrames);
        Assert.Equal(40, recorder.FpsAt(0));
        Assert.Equal(0, recorder.FpsAt(55));
        Assert.Equal(40, summary.FpsLow);
        Assert.InRange(summary.FpsAverage, 98, 100);
        Assert.Empty(summary.Verdicts); // no marks, nothing to judge
    }

    /// <summary>Ten minutes at 144 FPS with the given seconds slowed down, and paging at the given seconds.</summary>
    private static SessionRecorder Stuttering(int[] slow, int[] paging, Func<int, double>? fps = null)
    {
        var recorder = new SessionRecorder(Start);
        for (var s = 0; s < 600; s++)
            recorder.Add(Start.AddSeconds(s), new Dictionary<string, double>
            {
                ["fps"] = slow.Contains(s) ? 95 : fps?.Invoke(s) ?? 144,
                ["pages_in"] = paging.Contains(s) ? 40_000 : 50,
                ["ping_ext"] = 9,
            });
        return recorder;
    }

    [Fact]
    public void Sudden_falls_of_the_frame_rate_are_found_without_any_key_and_judged_like_marked_lags()
    {
        int[] slow = [100, 200, 201, 202, 300, 400, 500];
        var summary = Stuttering(slow, paging: [99, 199, 299, 399, 499, 550]).Summarize();

        Assert.Equal(5, summary.Hitches); // 200-202 is one stutter, not three.
        Assert.Equal(0, summary.Marks);
        var paging = summary.HitchVerdicts[0];
        Assert.Equal("pages_in", paging.Metric);
        Assert.Equal(5, paging.Hits);
        Assert.True(paging.Lift >= 10, $"lift {paging.Lift}");
        Assert.Equal(0, summary.HitchVerdicts.Single(v => v.Metric == "ping_ext_max").Hits);
        Assert.DoesNotContain(summary.HitchVerdicts, v => v.Metric == "frame_ms");
    }

    [Fact]
    public void A_game_that_switches_to_a_frame_limit_is_not_a_stutter()
    {
        // From second 300 the game runs at 60 (a menu, a cutscene) and stays there.
        var summary = Stuttering([], [], fps: s => s >= 300 ? 60 : 144).Summarize();

        Assert.Equal(0, summary.Hitches);
    }

    [Fact]
    public void A_frame_limit_reached_through_a_half_second_still_is_not_a_stutter()
    {
        // As Overwatch on the kill cam, 17/11/2026: 130, 87, 60, 60, 60… then back to 144 a while later.
        var summary = Stuttering([], [], fps: s => s switch
        {
            300 => 87,
            >= 301 and < 320 => 60,
            _ => 144,
        }).Summarize();

        Assert.Equal(0, summary.Hitches);
    }

    [Fact]
    public void A_ragged_fall_that_climbs_back_is_a_stutter()
    {
        // As Overwatch, 17/11/2026: 101, 64, 66, 145.
        var summary = Stuttering([], [], fps: s => s switch { 300 => 64, 301 => 66, _ => 144 }).Summarize();

        Assert.Equal(1, summary.Hitches);
    }

    [Fact]
    public void A_recorded_session_can_be_read_back_and_judged_again()
    {
        var original = Stuttering([100, 300], paging: [99, 299]);
        original.Mark(Start.AddSeconds(303));
        var file = Path.Combine(Path.GetTempPath(), $"optlife-{Guid.NewGuid():N}.csv");
        try
        {
            original.WriteCsv(file);
            var again = SessionRecorder.FromCsv(file, Start).Summarize();
            var first = original.Summarize();

            Assert.Equal(first.Seconds, again.Seconds);
            Assert.Equal(first.Hitches, again.Hitches);
            Assert.Equal(1, again.Marks);
            Assert.Equal(first.FpsAverage, again.FpsAverage, 1);
            Assert.Equal(first.HitchVerdicts[0], again.HitchVerdicts[0]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Judges a session already on disk: set OPTLIFE_SESSION_CSV to its file and OPTLIFE_SESSION_OUT to
    /// where the result should be written.
    /// </summary>
    [Fact]
    public void A_real_recording_can_be_analysed_on_request()
    {
        if (Environment.GetEnvironmentVariable("OPTLIFE_SESSION_CSV") is not { Length: > 0 } csv) return;
        var summary = SessionRecorder.FromCsv(csv, Start).Summarize();
        File.WriteAllLines(Environment.GetEnvironmentVariable("OPTLIFE_SESSION_OUT")!,
        [
            $"secondi {summary.Seconds}, fps medi {summary.FpsAverage:0.#}, scatti {summary.Hitches}, lag segnati {summary.Marks}",
            .. summary.HitchVerdicts.Select(v => $"{v.Metric,-15} presenti in {v.Hits}/{v.Marks} scatti ({v.HitRate:P0}), di solito {v.BaseRate:P1}, rapporto {v.Lift:0.0}"),
        ]);
    }
}

public class GamesRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 21, 0, 0, TimeSpan.Zero);

    private static StoredEvent Stuttery(int day, int hitches, params (string Metric, int Hits, double Base)[] verdicts)
    {
        var data = new Dictionary<string, string> { ["game"] = "RocketLeague", ["seconds"] = "3600", ["marks"] = "0", ["hitches"] = hitches.ToString() };
        foreach (var (metric, hits, baseRate) in verdicts)
            data[$"h.{metric}"] = $"{hits};{baseRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return new StoredEvent(day, T0.AddDays(day), "games", GamesRules.SessionKind, Severity.Info, "t", "game|racinggame", data);
    }

    [Fact]
    public void Stutters_with_a_common_cause_are_reported_even_if_no_lag_was_ever_marked()
    {
        var finding = GamesRules.Stutter("RocketLeague",
            [Stuttery(1, 20, ("pages_in", 17, 0.06), ("ping_gw_max", 2, 0.05)), Stuttery(2, 10, ("pages_in", 8, 0.04))], minHitches: 15, days: 7);

        Assert.NotNull(finding);
        Assert.Equal("Scatti in RocketLeague: l'indiziato è Windows che rilegge memoria dal disco", finding.Title);
        Assert.StartsWith("30 scatti (cali improvvisi dei frame) negli ultimi 7 giorni.", finding.Detail);
        Assert.Contains("83%", finding.Detail);
        Assert.Contains("RAM", finding.Advice);
        Assert.Null(GamesRules.Lag("RocketLeague", [Stuttery(1, 20, ("pages_in", 17, 0.06))], minMarks: 8, days: 7)); // No marks: nothing to say about lags.
    }

    [Fact]
    public void A_few_stutters_or_stutters_with_no_clear_cause_are_not_worth_a_problem()
    {
        Assert.Null(GamesRules.Stutter("RocketLeague", [Stuttery(1, 6, ("pages_in", 6, 0.05))], minHitches: 15, days: 7));
        Assert.Null(GamesRules.Stutter("RocketLeague", [Stuttery(1, 40, ("pages_in", 3, 0.05), ("cpu_tot", 2, 0.05))], minHitches: 15, days: 7));
    }

    private static StoredEvent Session(int day, double fps, int minutes = 30, int marks = 0, params (string Metric, int Hits, double Base)[] verdicts)
    {
        var data = new Dictionary<string, string>
        {
            ["game"] = "Deadlock",
            ["seconds"] = (minutes * 60).ToString(),
            ["marks"] = marks.ToString(),
            ["fps_avg"] = fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (metric, hits, baseRate) in verdicts)
            data[$"m.{metric}"] = $"{hits};{baseRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return new StoredEvent(day, T0.AddDays(day), "games", GamesRules.SessionKind, Severity.Info, "t", "game|deadlock", data);
    }

    [Fact]
    public void A_session_clearly_slower_than_usual_is_reported()
    {
        var finding = GamesRules.FpsDrop("Deadlock", [Session(1, 140), Session(2, 150), Session(3, 145), Session(4, 100)])!;

        Assert.Equal("Deadlock: FPS calati del 31% rispetto al solito", finding.Title);
        Assert.Contains("100 FPS medi", finding.Detail);
        Assert.Contains("145", finding.Detail);
    }

    [Fact]
    public void Normal_variation_and_too_little_history_are_not_reported()
    {
        Assert.Null(GamesRules.FpsDrop("Deadlock", [Session(1, 140), Session(2, 150), Session(3, 145), Session(4, 130)]));
        Assert.Null(GamesRules.FpsDrop("Deadlock", [Session(1, 140), Session(2, 60)]));
        // Short sessions and sessions without frame data do not count as "usual".
        Assert.Null(GamesRules.FpsDrop("Deadlock", [Session(1, 140, minutes: 3), Session(2, 0), Session(3, 150), Session(4, 145), Session(5, 60)]));
    }

    [Fact]
    public void Marks_from_several_sessions_add_up_to_one_verdict()
    {
        StoredEvent[] sessions =
        [
            Session(1, 140, marks: 4, verdicts: [("ping_gw_max", 3, 0.05), ("cpu_tot", 0, 0.05)]),
            Session(2, 140, marks: 5, verdicts: [("ping_gw_max", 4, 0.05), ("cpu_tot", 1, 0.05)]),
            Session(3, 140, marks: 0),
        ];

        var finding = GamesRules.Lag("Deadlock", sessions, minMarks: 8, days: 7)!;

        Assert.Equal("Lag in Deadlock: l'indiziato è il ping verso il router che sale", finding.Title);
        Assert.Contains("9 lag segnati", finding.Detail);
        Assert.Contains("78%", finding.Detail);
        Assert.Contains("Wi-Fi", finding.Advice);
        Assert.Null(GamesRules.Lag("Deadlock", sessions, minMarks: 10, days: 7));
    }
}

public class RtssTests
{
    private static MemoryMappedViewAccessor Build(MemoryMappedFile map, params (uint Pid, uint Start, uint End, uint Frames, uint FrameUs)[] apps)
    {
        const int offset = 64, entry = 300;
        var view = map.CreateViewAccessor();
        view.Write(0, 0x52545353u);
        view.Write(8, (uint)entry);
        view.Write(12, (uint)offset);
        view.Write(16, (uint)apps.Length);
        for (var i = 0; i < apps.Length; i++)
        {
            var at = offset + i * entry;
            view.Write(at, apps[i].Pid);
            view.Write(at + 268, apps[i].Start);
            view.Write(at + 272, apps[i].End);
            view.Write(at + 276, apps[i].Frames);
            view.Write(at + 280, apps[i].FrameUs);
        }
        return view;
    }

    [Fact]
    public void Frames_are_read_for_the_right_program()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        using var view = Build(map, (111, 10_000, 11_000, 60, 16_600), (222, 10_000, 10_500, 72, 6_900));

        var frames = Rtss.Parse(view, pid: 222, tickNow: 10_600)!.Value;

        Assert.Equal(144, frames.Fps);
        Assert.Equal(6.9, frames.FrameMs);
    }

    [Fact]
    public void A_program_rtss_does_not_follow_or_that_stopped_drawing_has_no_frames()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        using var view = Build(map, (111, 10_000, 11_000, 60, 16_600));

        Assert.Null(Rtss.Parse(view, pid: 999, tickNow: 11_100));   // not hooked
        Assert.Null(Rtss.Parse(view, pid: 111, tickNow: 20_000));   // last update 9 seconds ago
        Assert.Null(Rtss.Read(1, $"OptLife.Test.Missing.{Guid.NewGuid():N}")); // RTSS not running
    }
}

public class WindowsGameEnvironmentTests
{
    [Fact]
    public async Task The_real_counters_answer_on_this_machine_without_special_rights()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var game = new GameProcess(me.Id, me.ProcessName, me.MainModule?.FileName ?? "");
        var env = new WindowsGameEnvironment();

        using var source = env.OpenSource(game, ServerHint.None);
        source.Read();
        await Task.Delay(400);
        var reading = source.Read();

        Assert.InRange(reading["cpu_tot"], 0, 100 * Environment.ProcessorCount);
        Assert.InRange(reading["cpu_core_max"], 0, 400);
        Assert.True(reading["avail_mb"] > 0);
        Assert.Contains("pages_in", reading.Keys);
        Assert.Contains("disk_ms", reading.Keys);
        Assert.True(env.IsRunning(game));
        Assert.False(env.IsRunning(game with { Pid = 0x7FFFFFF0 }));
        Assert.False(env.IsFunctionKeyDown(12));
    }

    [Fact]
    public void A_program_is_a_game_only_if_it_matches_a_folder_or_a_name_and_is_not_excluded()
    {
        var env = new WindowsGameEnvironment();

        // Nothing on this PC runs from a folder with this name.
        Assert.Null(env.FindGame([@"\cartella-che-non-esiste-optlife\"], [], []));
        // A name that matches but is excluded is not a game either.
        Assert.Null(env.FindGame([], ["explorer"], ["explorer"]));
    }
}

public class GamesEndToEndTests
{
    /// <summary>
    /// A real session on the real machine, with a harmless Windows program standing in for the game.
    /// It takes over a minute and opens a window, so it runs only when asked: set OPTLIFE_E2E=1.
    /// </summary>
    [Fact]
    public async Task A_real_windowed_program_is_found_recorded_and_summarised()
    {
        if (Environment.GetEnvironmentVariable("OPTLIFE_E2E") != "1") return;

        using var box = new Sandbox();
        var live = $@"Local\OptLife.Test.{Guid.NewGuid():N}";
        using var board = new OptLife.Core.Hosting.LiveBoard(live);
        var runner = new OptLife.Core.Hosting.ModuleRunner(box.Store, box.Config, TimeProvider.System, board);
        box.Config.UpdateModule("games", c =>
        {
            c.Settings["gameFolders"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
            c.Settings["gameNames"] = JsonSerializer.SerializeToElement(new[] { "charmap" });
        });
        var module = new GamesModule();

        using var standIn = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("charmap.exe")
        {
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized,
            UseShellExecute = true,
        })!;
        try
        {
            standIn.WaitForInputIdle(5000);
            await Task.Delay(1500);
            var session = runner.RunTaskAsync(module, module.Tasks[0], CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(8));
            var during = OptLife.Core.Hosting.LiveBoard.Read(DateTimeOffset.Now, live);
            Assert.Contains(during, e => e.Key == "game.minutes" && e.Label.StartsWith("charmap"));

            await Task.Delay(TimeSpan.FromSeconds(60));
            standIn.Kill();
            Assert.True(await session, runner.ReadStatus(module, module.Tasks[0]).LastError);
        }
        finally
        {
            if (!standIn.HasExited) standIn.Kill();
        }

        var recorded = Assert.Single(box.Store.QueryEvents(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1), kind: GamesRules.SessionKind));
        Assert.StartsWith("charmap: 1 min", recorded.Title);
        var rows = File.ReadAllLines(recorded.Data["file"]);
        var header = rows[0].Split(',');
        // Network and system were really measured: some second has a router ping and a processor load.
        Assert.Contains(rows.Skip(1), r => double.Parse(r.Split(',')[Array.IndexOf(header, "cpu_tot")], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Contains(rows.Skip(1), r => double.Parse(r.Split(',')[Array.IndexOf(header, "ping_gw_max")], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.DoesNotContain(OptLife.Core.Hosting.LiveBoard.Read(DateTimeOffset.Now, live), e => e.Module == "games");
    }
}
