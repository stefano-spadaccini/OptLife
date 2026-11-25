using System.Reflection;
using OptLife.Core.Config;
using OptLife.Core.Storage;

namespace OptLife.Core.Hosting;

/// <summary>Everything the agent and the CLI share: where data lives, the history, the config, the modules.</summary>
public sealed class OptLifeRuntime
{
    public const string AgentMutexName = @"Local\OptLife.Agent";

    public required string DataDir { get; init; }
    public required Store Store { get; init; }
    public required ConfigStore Config { get; init; }
    public required ModuleRunner Runner { get; init; }
    public required IReadOnlyList<IModule> Modules { get; init; }
    public required IReadOnlyList<string> LoadErrors { get; init; }

    /// <summary>
    /// Where history and settings live. OPTLIFE_DATA when set; installed under Program Files (by the installer), the machine's
    /// own %ProgramData%\OptLife, because Program Files is read-only for the panel and the command line, which run as the user,
    /// and the agent is one per PC; anywhere else (a portable copy, a folder of one's own), a "dati" folder beside the program.
    /// </summary>
    public static string ResolveDataDir() =>
        Environment.GetEnvironmentVariable("OPTLIFE_DATA") is { Length: > 0 } custom
            ? custom
            : InProgramFiles(AppContext.BaseDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OptLife")
                : Path.Combine(AppContext.BaseDirectory, "dati");

    /// <summary>True for a folder inside Program Files (either of them).</summary>
    public static bool InProgramFiles(string folder)
    {
        var path = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0)
            .Any(p => path.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="hostLiveBoard">True only for the agent: it is the one process that publishes live values.</param>
    public static OptLifeRuntime Create(string? dataDir = null, string? modulesDir = null, bool hostLiveBoard = false)
    {
        dataDir ??= ResolveDataDir();
        Directory.CreateDirectory(dataDir);
        var store = new Store(Path.Combine(dataDir, "optlife.db"));
        var config = new ConfigStore(Path.Combine(dataDir, "config.json"));
        // Before the modules are created: their names and settings are texts too.
        L.Use(config.Load().Language);
        var (found, errors) = DiscoverModules(modulesDir ?? AppContext.BaseDirectory);
        // The PC organisation is part of the core (it writes other modules' settings), shown like a module page.
        var modules = new List<IModule>(found);
        modules.Add(new Layout.LayoutModule(config, () => modules));
        // OptLife's own updates, from its GitHub releases: part of the core too.
        modules.Add(new SelfUpdate.SelfUpdateModule());
        return new OptLifeRuntime
        {
            DataDir = dataDir,
            Store = store,
            Config = config,
            Runner = new ModuleRunner(store, config, TimeProvider.System, hostLiveBoard ? new LiveBoard() : null, readShared: !hostLiveBoard),
            Modules = modules,
            LoadErrors = errors,
        };
    }

    /// <summary>
    /// Finds modules by file name (OptLife.Modules.*.dll). Nothing in the core names a module,
    /// which is what makes them removable: no DLL, no module.
    /// </summary>
    public static (IReadOnlyList<IModule> Modules, IReadOnlyList<string> Errors) DiscoverModules(string directory)
    {
        var modules = new List<IModule>();
        var errors = new List<string>();
        foreach (var dll in Directory.EnumerateFiles(directory, "OptLife.Modules.*.dll").Order())
        {
            try
            {
                var types = Assembly.LoadFrom(dll).GetTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IModule).IsAssignableFrom(t));
                foreach (var type in types)
                {
                    var module = (IModule)Activator.CreateInstance(type)!;
                    if (modules.Any(m => m.Info.Id == module.Info.Id))
                        errors.Add(L.T($"{Path.GetFileName(dll)}: id \"{module.Info.Id}\" già usato da un altro modulo.", $"{Path.GetFileName(dll)}: id \"{module.Info.Id}\" already used by another module."));
                    else
                        modules.Add(module);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(dll)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return (modules, errors);
    }

    /// <summary>Deletes history older than the retention set in the config. Meant to run a few times a day.</summary>
    public int PruneHistory()
    {
        var config = Config.Load();
        var now = DateTimeOffset.Now;
        return Store.Prune(
            now.AddDays(-Math.Max(1, config.SampleRetentionDays)),
            now.AddDays(-Math.Max(1, config.EventRetentionDays)));
    }

    public const string AgentStopEventName = @"Local\OptLife.Agent.Stop";

    public static bool IsAgentRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(AgentMutexName, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // There, but made by an older agent that only administrators may open.
        }
    }

    public const string AgentRunNowEventName = @"Local\OptLife.Agent.RunNow";
    public const string PanelMutexName = @"Local\OptLife.Panel";
    public const string PanelShowEventName = @"Local\OptLife.Panel.Show";

    /// <summary>Asks a running agent to finish and exit. Returns false when no agent is running.</summary>
    public static bool RequestAgentStop() => Signal(AgentStopEventName);

    public const string AgentOpenPanelEventName = @"Local\OptLife.Agent.OpenPanel";

    /// <summary>Asks a running agent to open the panel with its own rights (administrator). False when no agent is running.</summary>
    public static bool RequestOpenPanel() => Signal(AgentOpenPanelEventName);

    /// <summary>Asks a running agent to run every check right away. Returns false when no agent is running.</summary>
    public static bool RequestAgentRunNow() => Signal(AgentRunNowEventName);

    /// <summary>Sets a named event owned by another OptLife process; false when that process is not there.</summary>
    public static bool Signal(string eventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var handle)) return false;
            using (handle) handle.Set();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // Made by an older agent that only administrators may signal.
        }
    }
}
