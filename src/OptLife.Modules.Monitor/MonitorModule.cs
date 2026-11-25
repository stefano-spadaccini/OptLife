using System.Diagnostics;
using System.Globalization;
using OptLife.Core;
using OptLife.Modules.Monitor.Sensors;

namespace OptLife.Modules.Monitor;

/// <summary>
/// Reads memory, processor, graphics card, network and disks every couple of seconds.
/// The current values go to the live board; once a minute their average goes to the history,
/// and the last 24 hours are checked for memory pressure, overheating, unstable connection, full disks.
/// </summary>
public sealed class MonitorModule : IModule
{
    private static readonly TimeSpan EventCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly ISensor[] _sensors;
    private readonly Func<string> _topMemoryConsumers;
    private readonly Func<AfterburnerState>? _afterburner;
    private readonly Dictionary<string, (double Sum, int Count)> _minute = [];
    private readonly Dictionary<string, DateTimeOffset> _lastEvent = [];
    private DateTimeOffset? _minuteStart;

    public MonitorModule() : this(
        [new MemorySensor(), new CpuLoadSensor(), new NvidiaSensor(), new AfterburnerSensor(), new NetworkSensor(), new DiskSensor()],
        DescribeTopMemoryConsumers, AfterburnerOnPc.State)
    { }

    /// <param name="afterburner">Where MSI Afterburner stands; null = do not say what is missing (the tests of the readings).</param>
    internal MonitorModule(ISensor[] sensors, Func<string> topMemoryConsumers, Func<AfterburnerState>? afterburner = null)
    {
        _sensors = sensors;
        _topMemoryConsumers = topMemoryConsumers;
        _afterburner = afterburner;
    }

    public IReadOnlyList<ModuleAction> Actions =>
    [
        new ModuleAction(MissingValues.OpenSite, L.T("Apri il sito di MSI Afterburner", "Open the MSI Afterburner site"),
            L.T("Apre nel browser la pagina ufficiale di MSI Afterburner, da cui si scarica. OptLife non scarica niente.", "Opens MSI Afterburner's official page in the browser, where it is downloaded. OptLife downloads nothing."),
            Permission.Ask, (_, argument, _) => Task.FromResult(
                // Only MSI's own page: the argument comes with the button, and nothing else may be opened from here.
                argument != MissingValues.AfterburnerPage
                    ? new ActionResult(false, L.T("Indirizzo non ammesso.", "Address not allowed."))
                    : Protection.StartAsUser("explorer.exe", $"\"{MissingValues.AfterburnerPage}\"", Environment.SystemDirectory) is { Ok: true }
                        ? new ActionResult(true, L.T("Pagina aperta nel browser.", "Page opened in the browser."))
                        : new ActionResult(false, L.T("Non riesco ad aprire il browser: l'indirizzo è ", "Cannot open the browser: the address is ") + MissingValues.AfterburnerPage))),
        new ModuleAction(MissingValues.StartAfterburner, L.T("Avvia MSI Afterburner", "Start MSI Afterburner"),
            L.T("Avvia MSI Afterburner come dal suo collegamento (Windows può chiedere il permesso). Da lì OptLife legge la temperatura del processore.", "Starts MSI Afterburner as its shortcut does (Windows may ask for permission). OptLife reads the processor temperature from it."),
            Permission.Ask, (_, _, _) =>
            {
                if (AfterburnerOnPc.Exe() is not { } exe) return Task.FromResult(new ActionResult(false, L.T("MSI Afterburner non è installato.", "MSI Afterburner is not installed.")));
                // Through Explorer, as a double-click: Afterburner asks for its own rights.
                var started = Protection.StartAsUser("explorer.exe", $"\"{exe}\"", Path.GetDirectoryName(exe)!);
                return Task.FromResult(started.Ok
                    ? new ActionResult(true, L.T("Afterburner avviato: tra qualche secondo compare la temperatura.", "Afterburner started: the temperature appears in a few seconds."))
                    : started);
            }),
    ];

    public ModuleInfo Info { get; } = new(
        "monitor", L.T("Monitoraggio", "Monitoring"),
        L.T("Tiene d'occhio memoria, processore, scheda video, rete e dischi, e segnala quando restano a lungo oltre i limiti.", "Keeps an eye on memory, processor, graphics card, network and disks, and reports when they stay beyond their limits for long."), FindingArea.Hardware);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Int("ramThreshold", L.T("Soglia memoria piena (%)", "Full memory threshold (%)"),
            L.T("Sopra questa percentuale di RAM occupata il minuto conta come \"memoria quasi piena\".", "Above this percentage of RAM in use the minute counts as \"memory almost full\"."), 90, 50, 99),
        SettingDefinition.Int("ramMinutes", L.T("Minuti di memoria piena prima di segnalare", "Minutes of full memory before reporting"),
            L.T("Quanti minuti sopra soglia, nelle ultime 24 ore, fanno comparire il problema.", "How many minutes above the threshold, in the last 24 hours, make the problem appear."), 10, 1, 600),
        SettingDefinition.Int("gpuTempLimit", L.T("Limite temperatura scheda video (°C)", "Graphics card temperature limit (°C)"),
            L.T("Media al minuto oltre la quale la scheda video è considerata troppo calda.", "Per-minute average above which the graphics card counts as too hot."), 85, 60, 100),
        SettingDefinition.Int("cpuTempLimit", L.T("Limite temperatura processore (°C)", "Processor temperature limit (°C)"),
            L.T("Media al minuto oltre la quale il processore è considerato troppo caldo. Richiede MSI Afterburner attivo.", "Per-minute average above which the processor counts as too hot. Needs MSI Afterburner running."), 90, 60, 105),
        SettingDefinition.Int("lossThreshold", L.T("Soglia pacchetti persi (%)", "Packet loss threshold (%)"),
            L.T("Un minuto con almeno questa perdita di pacchetti conta come connessione instabile.", "A minute with at least this packet loss counts as an unstable connection."), 5, 1, 50),
        SettingDefinition.Int("diskFreeMin", L.T("Spazio libero minimo su disco (%)", "Minimum free disk space (%)"),
            L.T("Sotto questa percentuale un disco viene segnalato come quasi pieno.", "Below this percentage a disk is reported as almost full."), 10, 1, 50),
        SettingDefinition.Pairs(NetworkSensor.HostsSetting, [L.T("Nome", "Name"), L.T("Indirizzo", "Address")], L.T("Indirizzi da controllare", "Addresses to check"),
            L.T("Scritti come nome=indirizzo. \"gateway\" indica il router, qualunque sia il suo indirizzo.", "Written as name=address. \"gateway\" means the router, whatever its address."),
            "router=gateway", "internet=1.1.1.1"),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("sample", L.T("Lettura dei sensori", "Reading the sensors"), TimeSpan.FromSeconds(2), SampleAsync, Continuous: true),
    ];

    private async Task SampleAsync(IModuleContext ctx, CancellationToken ct)
    {
        // Sensors are asked in order and the first to answer for a key wins, so a dedicated
        // source (the graphics driver) takes precedence over a generic one (Afterburner).
        var values = new Dictionary<string, LiveValue>();
        foreach (var sensor in _sensors)
            foreach (var v in await sensor.ReadAsync(ctx.Settings, ct))
                if (double.IsFinite(v.Value) && !v.IsMissing) values.TryAdd(v.Key, v with { Stress = Stress(v, ctx.Settings) });
        // What could not be read is said, with why, and is never recorded or averaged.
        IReadOnlyList<LiveValue> missing = _afterburner is null ? [] : MissingValues.For(values.Keys.ToHashSet(), _afterburner());
        ctx.PublishLive([.. values.Values, .. missing]);

        var minute = Truncate(ctx.Now);
        if (_minuteStart is { } start && minute > start)
        {
            var averages = _minute.ToDictionary(m => m.Key, m => m.Value.Sum / m.Value.Count);
            _minute.Clear();
            ctx.RecordSamples(averages, start);
            Evaluate(ctx, averages, values.Values);
        }
        _minuteStart = minute;

        foreach (var v in values.Values)
        {
            var (sum, count) = _minute.GetValueOrDefault(v.Key);
            _minute[v.Key] = (sum + v.Value, count + 1);
        }
    }

    /// <summary>
    /// How close a reading is to the limit this module holds it to (1 = at the limit), using the same
    /// thresholds the person set for the problems. Readings with no limit of their own get none.
    /// </summary>
    internal static double? Stress(LiveValue v, ISettings s)
    {
        static double Ratio(double value, double limit) => limit <= 0 ? 0 : Math.Max(0, value / limit);
        return v.Key switch
        {
            "ram.used_pct" => Ratio(v.Value, s.GetInt("ramThreshold")),
            "ram.commit_pct" => Ratio(v.Value, 95),
            "gpu.temp" => Ratio(v.Value, s.GetInt("gpuTempLimit")),
            "cpu.temp" => Ratio(v.Value, s.GetInt("cpuTempLimit")),
            "gpu.vram_pct" => Ratio(v.Value, 95),
            // A busy processor or card is doing its job: only close to flat out is worth a colour.
            "cpu.load" or "gpu.load" => Ratio(v.Value, 98),
            _ when v.Key.StartsWith("net.") && v.Key.EndsWith(".loss") => Ratio(v.Value, s.GetInt("lossThreshold")),
            _ when v.Key.StartsWith("net.") && v.Key.EndsWith(".ms") => Ratio(v.Value, 100),
            // The less free space, the more strained: at the minimum it is 1.
            _ when v.Key.StartsWith("disk.") && v.Key.EndsWith(".free_pct") => v.Value <= 0 ? 2 : s.GetInt("diskFreeMin") / v.Value,
            _ => null,
        };
    }

    private void Evaluate(IModuleContext ctx, Dictionary<string, double> minute, IReadOnlyCollection<LiveValue> current)
    {
        var s = ctx.Settings;
        var now = ctx.Now;
        IReadOnlyList<SamplePoint> Last24h(string metric) => ctx.History.Samples(metric, now - Day, now);

        // ── Events: one line in the history when a limit is crossed (not one per minute) ──
        var ramLimit = s.GetInt("ramThreshold");
        if (minute.TryGetValue("ram.used_pct", out var ram) && ram >= ramLimit && Due("ram_pressure", now))
        {
            ctx.RecordEvents([new NewEvent(now, "ram_pressure", Severity.Medium,
                L.T("Memoria quasi piena", "Memory almost full") + $" ({ram.ToString("0", CultureInfo.InvariantCulture)}%)", "ram_pressure",
                Data: new Dictionary<string, string> { ["top"] = _topMemoryConsumers() })]);
        }
        foreach (var (metric, kind, what, limit) in new[]
                 {
                     ("gpu.temp", "gpu_hot", L.T("Scheda video", "Graphics card"), s.GetInt("gpuTempLimit")),
                     ("cpu.temp", "cpu_hot", L.T("Processore", "Processor"), s.GetInt("cpuTempLimit")),
                 })
        {
            if (minute.TryGetValue(metric, out var t) && t >= limit && Due(kind, now))
                ctx.RecordEvents([new NewEvent(now, kind, Severity.Medium, $"{what} {L.T("a", "at")} {t.ToString("0", CultureInfo.InvariantCulture)} °C", kind)]);
        }
        var lossLimit = s.GetInt("lossThreshold");
        var lossy = minute.Where(m => m.Key.StartsWith("net.") && m.Key.EndsWith(".loss") && m.Value >= lossLimit).ToList();
        if (lossy.Count > 0 && Due("net_unstable", now))
        {
            ctx.RecordEvents([new NewEvent(now, "net_unstable", Severity.Low,
                L.T("Connessione instabile: ", "Unstable connection: ") + string.Join(", ", lossy.Select(l => $"{l.Key.Split('.')[1]} {l.Value.ToString("0", CultureInfo.InvariantCulture)}% {L.T("persi", "lost")}")),
                "net_unstable")]);
        }

        // ── Problems: judged on the whole last day, so they also clear by themselves ──
        var lastPressure = ctx.History.Events(now - Day, now, ctx.ModuleId, "ram_pressure").FirstOrDefault();
        ctx.SyncFindings("ram", ListOf(MonitorRules.Memory(
            Last24h("ram.used_pct"), ramLimit, s.GetInt("ramMinutes"), lastPressure?.Data.GetValueOrDefault("top"))));

        ctx.SyncFindings("temp", ListOf(
            MonitorRules.Temperature("gpu.temp", L.T("Scheda video", "Graphics card"), Last24h("gpu.temp"), s.GetInt("gpuTempLimit"), 3,
                L.T("Controlla polvere e curva delle ventole; se capita in un solo gioco, limitare gli FPS abbassa molto il calore.", "Check dust and the fan curve; if it happens in one game only, capping the FPS lowers the heat a lot.")),
            MonitorRules.Temperature("cpu.temp", L.T("Processore", "Processor"), Last24h("cpu.temp"), s.GetInt("cpuTempLimit"), 3,
                L.T("Controlla dissipatore, pasta termica e ricircolo d'aria nel case.", "Check the cooler, thermal paste and airflow in the case."))));

        ctx.SyncFindings("net", ListOf(MonitorRules.Network(
            Last24h("net.router.loss"), Last24h("net.internet.loss"), lossLimit, 5)));

        ctx.SyncFindings("disk", MonitorRules.Disks(current, s.GetInt("diskFreeMin")));
    }

    private bool Due(string kind, DateTimeOffset now)
    {
        if (_lastEvent.TryGetValue(kind, out var last) && now - last < EventCooldown) return false;
        _lastEvent[kind] = now;
        return true;
    }

    private static List<NewFinding> ListOf(params NewFinding?[] findings) => [.. findings.OfType<NewFinding>()];

    private static DateTimeOffset Truncate(DateTimeOffset t) => t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute));

    /// <summary>The five programs holding the most memory right now, e.g. "chrome (3,1 GB), Discord (1,2 GB)".</summary>
    private static string DescribeTopMemoryConsumers()
    {
        var byName = new Dictionary<string, long>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    byName[process.ProcessName] = byName.GetValueOrDefault(process.ProcessName) + process.WorkingSet64;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process ended while we were looking, or is protected: not ours to count.
                }
            }
        }
        var it = L.Culture;
        return string.Join(", ", byName
            .OrderByDescending(p => p.Value)
            .Take(5)
            .Select(p => $"{p.Key} ({(p.Value / (1024d * 1024 * 1024)).ToString("0.0", it)} GB)"));
    }
}
