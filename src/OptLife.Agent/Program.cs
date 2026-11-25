using OptLife.Core.Hosting;

namespace OptLife.Agent;

/// <summary>
/// The background half of OptLife: one instance, no window. It runs the modules on their schedule
/// and owns the notification-area icon, so the panel itself only exists while it is open.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // An installation is copying the program's files: the 5-minute task must not start the agent in the middle of it
        // (on 24 Nov it did twice, and the copy failed on a locked file). The mark is ignored once it is old: a stopped install.
        var installing = Path.Combine(AppContext.BaseDirectory, "installing.lock");
        if (File.Exists(installing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(installing) < TimeSpan.FromMinutes(10)) return;

        // Shared objects carry explicit rights: the agent runs as administrator, panel and command line may not.
        if (!SharedObjects.Mutex(OptLifeRuntime.AgentMutexName, initiallyOwned: true)) return;

        ApplicationConfiguration.Initialize();
        var runtime = OptLifeRuntime.Create(hostLiveBoard: true);
        var log = new FileLog(Path.Combine(runtime.DataDir, "agent.log"));
        log.Write($"{L.T("Avvio", "Start")}{(SharedObjects.IsElevated() ? L.T(" come amministratore", " as administrator") : "")}. {L.T("Moduli", "Modules")}: {string.Join(", ", runtime.Modules.Select(m => m.Info.Id))}");
        foreach (var error in runtime.LoadErrors) log.Write(L.T("Modulo non caricato: ", "Module not loaded: ") + error);
        // An installed copy: only administrators may change the data (the settings decide what the agent runs). Checked at every start.
        if (OptLife.Core.Protection.InstalledMode && SharedObjects.IsElevated() && !OptLife.Core.Protection.IsHardened(runtime.DataDir))
        {
            try
            {
                OptLife.Core.Protection.Harden(runtime.DataDir);
                log.Write(L.T("Cartella dei dati protetta: la cambiano solo gli amministratori.", "Data folder protected: only administrators change it."));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                log.Write(L.T($"Cartella dei dati non protetta: {ex.Message}", $"Data folder not protected: {ex.Message}"));
            }
        }

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(runtime.Runner, runtime.Modules, log.Write);
        var work = Task.Run(() => scheduler.RunAsync(cts.Token));
        _ = Task.Run(async () =>
        {
            // Housekeeping: shortly after start, then four times a day.
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), cts.Token);
                while (true)
                {
                    try
                    {
                        var removed = runtime.PruneHistory();
                        if (removed > 0) log.Write(L.T($"Storico: rimosse {removed} righe vecchie.", $"History: removed {removed} old rows."));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.Write(L.T($"Storico: pulizia saltata ({ex.Message})", $"History: cleanup skipped ({ex.Message})"));
                    }
                    await Task.Delay(TimeSpan.FromHours(6), cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // The language chosen in the panel applies without a restart: every text the agent writes is built when written.
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    try
                    {
                        var wanted = L.Resolve(runtime.Config.Load().Language, System.Globalization.CultureInfo.CurrentUICulture);
                        if (wanted == L.Code) continue;
                        L.Use(wanted);
                        log.Write(L.T("Lingua cambiata: italiano.", "Language changed: English."));
                        scheduler.TriggerNow(); // Problems and pages rewritten in the new language right away.
                    }
                    catch (IOException)
                    {
                        // The config is being written: the next look reads it.
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        SharedObjects.Event(OptLifeRuntime.AgentStopEventName, manualReset: true);
        SharedObjects.Event(OptLifeRuntime.AgentRunNowEventName, manualReset: false);
        SharedObjects.Event(OptLifeRuntime.AgentOpenPanelEventName, manualReset: false);
        using var openPanel = new EventWaitHandle(false, EventResetMode.AutoReset, OptLifeRuntime.AgentOpenPanelEventName);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, OptLifeRuntime.AgentStopEventName);
        using var runNow = new EventWaitHandle(false, EventResetMode.AutoReset, OptLifeRuntime.AgentRunNowEventName);
        var signals = new Thread(() =>
        {
            while (true)
            {
                var which = WaitHandle.WaitAny([stop, runNow, openPanel]);
                if (which == 0) break;
                // A panel started without administrator rights asks for one that has them (the one the tray icon opens).
                if (which == 2) TrayIcon.OpenPanel();
                else scheduler.TriggerNow();
            }
            Application.Exit();
        })
        { IsBackground = true, Name = "OptLife signals" };
        signals.Start();

        using (var tray = new TrayIcon(runtime.Store, scheduler.TriggerNow, onExit: Application.Exit))
        using (new ActivityWatcher(runtime.Store, runtime.Config, TrayIcon.OpenPanel))
        {
            Application.Run();
        }

        cts.Cancel();
        work.Wait(TimeSpan.FromSeconds(5));
        log.Write(L.T("Arresto.", "Stop."));
    }
}

internal sealed class FileLog(string path)
{
    private readonly Lock _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never be the reason the agent stops.
            }
        }
    }
}
