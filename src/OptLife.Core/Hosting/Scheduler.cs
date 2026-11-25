namespace OptLife.Core.Hosting;

/// <summary>Keeps every task of every enabled module running on its own interval, independently of the others.</summary>
public sealed class Scheduler(ModuleRunner runner, IReadOnlyList<IModule> modules, Action<string> log)
{
    private CancellationTokenSource _wake = new();

    /// <summary>How many times "check now" was asked: a task busy when it came runs again as soon as it ends.</summary>
    private int _asked;

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(
            from module in modules
            from task in module.Tasks
            // Task.Run: a task that blocks must not delay the start of the others.
            select Task.Run(() => LoopAsync(module, task, ct), CancellationToken.None));

    /// <summary>
    /// Cuts every wait short, so all tasks run a round right away ("check now"). A task in the middle of a round is not woken (it is not
    /// waiting): it runs once more when that round ends — on 25 Nov 2026 a check pressed while the Cleanup was weighing a drive was lost.
    /// </summary>
    public void TriggerNow()
    {
        Interlocked.Increment(ref _asked);
        Interlocked.Exchange(ref _wake, new CancellationTokenSource()).Cancel();
    }

    private async Task LoopAsync(IModule module, ModuleTask task, CancellationToken ct)
    {
        var name = $"{module.Info.Id}/{task.Id}";
        var asked = false; // This round was started by "check now" rather than by the clock.
        while (!ct.IsCancellationRequested)
        {
            var askedBefore = Volatile.Read(ref _asked);
            try
            {
                // Checked every round, so switching a module on or off takes effect without a restart.
                if (runner.IsEnabled(module))
                {
                    var ok = await runner.RunTaskAsync(module, task, ct, onDemand: asked);
                    if (!ok) log(L.T($"{name}: fallita ({runner.ReadStatus(module, task).LastError})", $"{name}: failed ({runner.ReadStatus(module, task).LastError})"));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Only the core's own bookkeeping can land here (e.g. the database is busy): try again next round.
                log(L.T($"{name}: giro saltato ({ex.GetType().Name}: {ex.Message})", $"{name}: round skipped ({ex.GetType().Name}: {ex.Message})"));
            }

            // "Check now" came while this round was running: run again at once, as asked (continuous tasks run all the time anyway).
            if (Volatile.Read(ref _asked) != askedBefore && !task.Continuous)
            {
                asked = true;
                continue;
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, Volatile.Read(ref _wake).Token);
            asked = false;
            try
            {
                await Task.Delay(task.Interval, wait.Token);
            }
            catch (OperationCanceledException)
            {
                // Either shutting down (the loop condition ends it) or woken by TriggerNow (run again, as asked).
                asked = !ct.IsCancellationRequested;
            }
        }
    }
}
