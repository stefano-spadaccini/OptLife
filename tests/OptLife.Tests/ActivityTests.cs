using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Modules.Updates;

namespace OptLife.Tests;

public sealed class ActivityTests : IDisposable
{
    private readonly Sandbox _box = new();
    public void Dispose() => _box.Dispose();

    private static StoredActivity Act(long id, ActivityState state, DateTimeOffset start, DateTimeOffset? end = null, string actor = "person") =>
        new(id, "m", $"Job {id}", actor, start, end ?? start, end, state, null, null, null, "detail", null);

    [Fact]
    public async Task Every_action_is_an_activity_with_its_progress_and_outcome()
    {
        var env = new FakeUpdatesEnvironment { Apps = [new AppUpdate("Git", "Git.Git", "1", "2"), new AppUpdate("GOG GALAXY", "GOG.Galaxy", "1", "2")] };
        var module = new UpdatesModule(env);
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks[0], CancellationToken.None));
        var action = module.Actions.Single(a => a.Id == UpdatesRules.UpgradeOne);
        await _box.Runner.RunActionAsync(module, action, "pannello", confirmed: true, "Git.Git;GOG.Galaxy", CancellationToken.None);
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks[1], CancellationToken.None)); // The agent takes the queue.

        var a = _box.Store.GetActivities()[0]; // The run itself (the button only queued it).
        Assert.Equal((ActivityState.Done, "person", "updates"), (a.State, a.Actor, a.Module));
        Assert.Equal((2, 2), (a.Done, a.Total));
        Assert.Contains("GOG GALAXY", a.Progress);
        Assert.StartsWith("Riusciti: 2", a.Detail);
    }

    [Fact]
    public async Task A_refused_action_is_not_an_activity_and_a_failing_one_is_marked_failed()
    {
        var module = new FakeModule
        {
            Actions =
            [
                new ModuleAction("ask", "Chiede", "", Permission.Ask, (_, _, _) => Task.FromResult(new ActionResult(true, ""))),
                new ModuleAction("boom", "Esplode", "", Permission.Auto, (_, _, _) => throw new InvalidOperationException("rotto")),
            ],
        };
        await _box.Runner.RunActionAsync(module, module.Actions[0], "cli", confirmed: false, null, CancellationToken.None);
        Assert.Empty(_box.Store.GetActivities());
        await _box.Runner.RunActionAsync(module, module.Actions[1], "cli", confirmed: true, null, CancellationToken.None);
        var a = _box.Store.GetActivities()[0]; // The run itself (the button only queued it).
        Assert.Equal(ActivityState.Failed, a.State);
        Assert.Contains("rotto", a.Detail);
    }

    [Fact]
    public void The_estimate_is_how_long_the_same_thing_took_last_time()
    {
        var t = _box.Clock.Now;
        var first = _box.Store.BeginActivity("m", "Scan", "scheduler", t);
        _box.Store.EndActivity(first, true, "ok", t.AddMinutes(45));
        _box.Store.BeginActivity("m", "Scan", "person", t.AddDays(1));
        Assert.Equal(TimeSpan.FromMinutes(45), _box.Store.GetActivities()[0].Estimate);
    }

    [Fact]
    public void Pop_ups_come_for_long_things_only_once_and_never_during_a_game()
    {
        var notices = new ActivityNotices();
        var settings = new NotificationSettings();
        var t0 = new DateTimeOffset(2026, 11, 22, 23, 0, 0, TimeSpan.Zero);
        Assert.Empty(notices.Next([Act(1, ActivityState.Done, t0.AddHours(-1), t0.AddMinutes(-30))], settings, t0, false)); // The past is not news.

        var running = Act(2, ActivityState.Running, t0);
        Assert.Empty(notices.Next([running], settings, t0.AddSeconds(1), false)); // Too soon to say.
        Assert.Single(notices.Next([running], settings, t0.AddSeconds(5), false), n => !n.Ended);
        Assert.Empty(notices.Next([running], settings, t0.AddSeconds(7), false)); // Said once.

        // Ends during a game: nothing now, said when the game is over.
        var done = Act(2, ActivityState.Done, t0, t0.AddMinutes(20));
        Assert.Empty(notices.Next([done], settings, t0.AddMinutes(21), playing: true));
        Assert.Single(notices.Next([done], settings, t0.AddMinutes(40), false), n => n.Ended && n.State == ActivityState.Done);

        // A quick success stays quiet; a quick failure does not.
        Assert.Empty(notices.Next([Act(3, ActivityState.Done, t0, t0.AddSeconds(2))], settings, t0.AddSeconds(3), false));
        Assert.Single(notices.Next([Act(4, ActivityState.Failed, t0, t0.AddSeconds(2))], settings, t0.AddSeconds(3), false));

        // Scheduler jobs only when asked for.
        Assert.Empty(notices.Next([Act(5, ActivityState.Running, t0, actor: "scheduler")], settings, t0.AddMinutes(1), false));
        settings.Scheduler = true;
        Assert.Single(notices.Next([Act(6, ActivityState.Running, t0, actor: "scheduler")], settings, t0.AddMinutes(1), false));
        settings.Popup = false;
        Assert.Empty(notices.Next([Act(7, ActivityState.Running, t0)], settings, t0.AddMinutes(1), false));
    }

    [Fact]
    public void An_activity_whose_program_is_gone_reads_as_interrupted()
    {
        // Written as if by another process that no longer exists: the pid of a process that just ended.
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        var id = _box.Store.BeginActivity("m", "Lost", "person", _box.Clock.Now);
        _box.Store.SetActivityOwner(id, p.Id);
        Assert.Equal(ActivityState.Interrupted, _box.Store.GetActivities().Single().State);
    }
}

public sealed class PanelStaysFreeTests : IDisposable
{
    private readonly Sandbox _box = new();
    public void Dispose() => _box.Dispose();

    /// <summary>
    /// 23 Nov 2026: after "Remove it from start-up" the panel re-ran the whole Cleanup check (weighing all of E:\ during a Defender
    /// scan) and every other button stayed dead until it ended. The action ends the wait; the new look goes on behind.
    /// </summary>
    [Fact]
    public async Task After_a_fix_the_panel_is_free_while_the_module_looks_again_behind()
    {
        var gate = new TaskCompletionSource();
        var fixedIt = false;
        var module = new FakeModule
        {
            Tasks =
            [
                new ModuleTask("t", "Controllo", TimeSpan.FromMinutes(1), async (ctx, _) =>
                {
                    if (fixedIt) await gate.Task; // The slow look that follows the fix.
                    ctx.SyncFindings("s", fixedIt ? [] : [new NewFinding("k", Severity.Low, "Problema", "", Fix: new FindingFix("fix", "Sistema"))]);
                }),
            ],
            Actions = [new ModuleAction("fix", "Sistema", "", Permission.Ask, (_, _, _) => { fixedIt = true; return Task.FromResult(new ActionResult(true, "ok")); })],
        };
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks[0], CancellationToken.None));
        var vm = new OptLife.Panel.ViewModels.PanelViewModel(_box.Store, _box.Config, _box.Runner, [module], () => true, () => true, _box.Clock) { AgentGrace = TimeSpan.Zero };

        var fix = vm.FixCommand.ExecuteAsync(vm.Findings.Single());
        Assert.Same(fix, await Task.WhenAny(fix, Task.Delay(5000))); // Without the change this waited for the whole look.
        Assert.False(vm.IsChecking);
        Assert.True(vm.FixCommand.CanExecute(vm.Findings.Single()));

        gate.SetResult();
        await vm.Rechecking;
        Assert.Empty(vm.Findings);
    }
}
