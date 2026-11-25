using System.IO;
using System.Windows;
using System.Windows.Threading;
using OptLife.Core.Hosting;
using OptLife.Panel.ViewModels;

namespace OptLife.Panel;

public partial class App : Application
{

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Developer aid: "--snapshot file.png [--page History|Modules] [--filter Ignored|Resolved] [--expert]"
        // draws the panel into an image and exits, so its look can be checked without a person at the screen.
        var snapshot = Option(e.Args, "--snapshot");

        // Restarted to change language: let the old window close first, or this one would find it and give way.
        if (int.TryParse(Option(e.Args, "--after"), out var previous))
        {
            try { System.Diagnostics.Process.GetProcessById(previous).WaitForExit(10_000); }
            catch (ArgumentException) { /* Already gone. */ }
        }

        // An installed copy keeps its data for administrators only: a panel started without those rights (the Start menu) asks the
        // agent for one that has them, the one its tray icon opens; with no agent running, Windows asks for the rights.
        if (snapshot is null && Protection.InstalledMode && !Protection.IsElevated() && Environment.GetEnvironmentVariable("OPTLIFE_DATA") is not { Length: > 0 })
        {
            if (!OptLifeRuntime.RequestOpenPanel())
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', e.Args.Select(a => $"\"{a}\"")))
                        { UseShellExecute = true, Verb = "runas" })?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(L.T("OptLife si apre con i permessi di amministratore: le sue impostazioni decidono cosa fa l'agente, e le cambia solo un amministratore.",
                        "OptLife opens with administrator permission: its settings decide what the agent does, and only an administrator changes them."), "OptLife",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            Shutdown();
            return;
        }

        if (snapshot is null)
        {
            // Opened from the agent's icon the panel is administrator too; opened from a shortcut it is not:
            // either one must find the other, so the lock and the signal carry explicit rights.
            var isFirst = SharedObjects.Mutex(OptLifeRuntime.PanelMutexName, initiallyOwned: true);
            if (isFirst) SharedObjects.Event(OptLifeRuntime.PanelShowEventName, manualReset: false);
            if (!isFirst)
            {
                // Already open: bring that window forward instead of opening a second one.
                OptLifeRuntime.Signal(OptLifeRuntime.PanelShowEventName);
                Shutdown();
                return;
            }
        }

        var runtime = OptLifeRuntime.Create();
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText(Path.Combine(runtime.DataDir, "panel.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {args.Exception}{Environment.NewLine}");
            MessageBox.Show(L.T("OptLife ha incontrato un errore e si chiude.", "OptLife hit an error and is closing.") + $"\n\n{args.Exception.Message}", "OptLife", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        var viewModel = new PanelViewModel(
            runtime.Store, runtime.Config, runtime.Runner, runtime.Modules,
            OptLifeRuntime.IsAgentRunning, OptLifeRuntime.RequestAgentRunNow, TimeProvider.System);
        viewModel.Restart = () =>
        {
            // A new panel waits for this one to close (it holds the single-instance lock), then opens in the new language.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"--after {Environment.ProcessId}") { UseShellExecute = false });
            Shutdown();
        };
        if (snapshot is not null && !e.Args.Contains("--welcome")) viewModel.ShowWelcome = false;
        // "--welcome [--tour-step 3]": the guided tour, at that step, to look at it.
        if (snapshot is not null && e.Args.Contains("--welcome"))
        {
            viewModel.StartTourCommand.Execute(null);
            if (int.TryParse(Option(e.Args, "--tour-step"), out var step) && step < viewModel.TourSteps.Count) viewModel.TourChangeCommand.Execute(viewModel.TourSteps[step]);
        }
        viewModel.ApplyTheme = ThemeManager.Apply;
        viewModel.CopyText = text => { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { /* Clipboard busy: the next press works. */ } };
        viewModel.WindowsAccent = ThemeManager.WindowsAccent;
        viewModel.WindowsLight = ThemeManager.WindowsLight;
        viewModel.ApplyCurrentTheme();
        // The panel follows Windows: its accent colour, light or dark mode, transparency, high contrast.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(viewModel.ApplyCurrentTheme);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(viewModel.ApplyCurrentTheme);
        };
        var window = new MainWindow(viewModel);
        MainWindow = window;

        BigLibraryWindow MakeBig(ViewTab tab) =>
            new(new BigLibraryViewModel(tab, item => viewModel.PressCommand.ExecuteAsync(item)), viewModel.GameRunning) { Refresh = viewModel.Refresh };
        viewModel.OpenBig = tab =>
        {
            var big = MakeBig(tab);
            // Leaving the full-screen view shows the normal panel.
            big.Closed += (_, _) => { if (!window.IsVisible) { window.Show(); window.Activate(); } };
            big.Show();
            big.Activate();
        };

        // "--big": the library full screen, for the sofa (a shortcut, or a non-Steam game in Steam's Big Picture).
        if ((e.Args.Contains("--big") || snapshot is null && viewModel.OpenBigAtStart && !e.Args.Contains("--normal")) && viewModel.ShowGallery() is { } gallery)
        {
            if (snapshot is not null)
            {
                // "--big-open filters|keyboard|menu|exit": the snapshot with that layer open.
                BigInput[] open = Option(e.Args, "--big-open") switch { "filters" => [BigInput.Filters], "keyboard" => [BigInput.Search], "menu" => [BigInput.Menu], "exit" => [BigInput.Exit], _ => [] };
                var bigWindow = MakeBig(gallery);
                if (Option(e.Args, "--big-select") is { } title) ((BigLibraryViewModel)bigWindow.DataContext).ChooseTitle(title);
                bigWindow.SnapshotTo(snapshot, open);
                Shutdown();
                return;
            }
            viewModel.OpenBig(gallery);
            ListenForShowRequests(window);
            return;
        }

        if (snapshot is not null)
        {
            if (e.Args.Contains("--expert")) viewModel.ExpertMode = true;
            if (Enum.TryParse<FindingFilter>(Option(e.Args, "--filter"), ignoreCase: true, out var filter)) viewModel.Filter = filter;
            if (Enum.TryParse<PanelPage>(Option(e.Args, "--page"), ignoreCase: true, out var page)) viewModel.Page = page;
            if (filter == FindingFilter.History) viewModel.Page = PanelPage.History; // The history is a page of its own behind that tab.
            // "--view sessions" opens a module's own page, by the id the module gave it.
            if (Option(e.Args, "--view") is { } viewId && viewModel.Views.FirstOrDefault(v => v.View.Id.Equals(viewId, StringComparison.OrdinalIgnoreCase)) is { } tab)
                viewModel.ShowViewCommand.Execute(tab);
            // A tall window shows what would otherwise need scrolling (e.g. the settings of the last module).
            if (int.TryParse(Option(e.Args, "--height"), out var height)) window.Height = height;
            // A narrow window, to check that nothing overlaps ("--width 700"); "--zoom 1.5" as if the text were bigger.
            if (int.TryParse(Option(e.Args, "--width"), out var width)) window.Width = width;
            if (double.TryParse(Option(e.Args, "--zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom)) viewModel.Zoom = zoom;
            window.SnapshotTo(snapshot);
            return;
        }

        window.Show();
        ListenForShowRequests(window);
    }

    private static void ListenForShowRequests(MainWindow window)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, OptLifeRuntime.PanelShowEventName);
        new Thread(() =>
        {
            while (show.WaitOne())
                window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, window.BringToFront);
        })
        { IsBackground = true, Name = "OptLife show" }.Start();
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
