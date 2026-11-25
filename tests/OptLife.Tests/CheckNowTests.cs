using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

/// <summary>"Check now" — one behaviour from Home and from Problems: it really waits, says how far it got, and how it went.</summary>
public sealed class CheckNowTests : IDisposable
{
    private readonly Sandbox _box = new();
    public void Dispose() => _box.Dispose();

    /// <summary>25 Nov 2026: "check now" pressed while a module was in the middle of a round was lost for that module.</summary>
    [Fact]
    public async Task A_check_asked_while_a_task_is_busy_runs_it_again_when_it_ends()
    {
        var runs = 0;
        var busy = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var task = new ModuleTask("t", "Lenta", TimeSpan.FromHours(1), async (_, _) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                busy.SetResult();
                await release.Task;
            }
        });
        var module = new FakeModule { Tasks = [task] };
        var scheduler = new Scheduler(_box.Runner, [module], _ => { });
        using var cts = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cts.Token);

        await busy.Task;            // First round running…
        scheduler.TriggerNow();     // …"check now" arrives…
        release.SetResult();        // …the round ends.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref runs) < 2 && DateTime.UtcNow < until) await Task.Delay(20);
        cts.Cancel();
        await loop;

        Assert.Equal(2, runs); // Run again at once, not an hour later.
    }

    [Fact]
    public async Task Check_now_says_how_many_modules_looked_and_which_problems_closed_or_are_new()
    {
        var round = 0;
        var task = FakeModule.Task(ctx =>
        {
            round++;
            ctx.SyncFindings("s", round == 1 ? [new NewFinding("old", Severity.Low, "vecchio", "d")] : [new NewFinding("new", Severity.Low, "nuovo", "d"), new NewFinding("other", Severity.Low, "altro", "d")]);
        });
        var module = new FakeModule { Tasks = [task] };
        await _box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        var vm = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [module], () => false, () => false, _box.Clock) { AgentGrace = TimeSpan.Zero };

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal("Controllato 1 modulo: 1 problema chiuso, 2 nuovi.", vm.Notice);
        Assert.Equal(vm.Notice, vm.LastCheck);
        Assert.False(vm.IsChecking);
        Assert.Equal("", vm.CheckProgress);
        Assert.Equal(2, vm.Findings.Count);
    }

    [Fact]
    public async Task With_the_agent_it_waits_for_the_modules_to_end_and_says_so_if_some_are_still_at_work()
    {
        var task = FakeModule.Task(_ => { });
        var module = new FakeModule { Tasks = [task] };
        // The agent was asked, but nothing ran within the wait: said, not "done".
        var vm = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [module], () => true, () => true, _box.Clock) { AgentGrace = TimeSpan.Zero };
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.StartsWith("Controllati 0 moduli su 1", vm.Notice);

        // The agent ran it: everything is checked.
        var after = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [module], () => true, () =>
        {
            _box.Clock.Now += TimeSpan.FromSeconds(2);
            _ = _box.Runner.RunTaskAsync(module, task, CancellationToken.None, onDemand: true).Result;
            return true;
        }, _box.Clock) { AgentGrace = TimeSpan.FromSeconds(5), AgentPoll = TimeSpan.FromMilliseconds(10) };
        await after.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal("Controllato 1 modulo: 0 problemi chiusi, 0 nuovi.", after.Notice);
    }

    [Fact]
    public async Task A_second_press_while_one_runs_is_answered_not_silent()
    {
        var gate = new TaskCompletionSource();
        var task = new ModuleTask("t", "Lenta", TimeSpan.FromMinutes(1), async (_, _) => await gate.Task);
        var module = new FakeModule { Tasks = [task] };
        var vm = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [module], () => false, () => false, _box.Clock) { AgentGrace = TimeSpan.Zero };

        var first = vm.CheckNowCommand.ExecuteAsync(null);
        Assert.True(vm.IsChecking);
        Assert.Equal("In corso…", vm.RefreshLabel);
        Assert.Equal("IN CORSO…", vm.CheckLabel);
        await vm.CheckNowCommand.ExecuteAsync(null); // A second press, as F5 or the header button would.
        Assert.StartsWith("Un controllo o un'azione è già in corso", vm.Notice);
        gate.SetResult();
        await first;
        Assert.StartsWith("Controllato 1 modulo", vm.Notice);
    }
}
