using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Modules.Monitor;
using OptLife.Modules.Monitor.Sensors;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

internal sealed class FakeSensor : ISensor
{
    public List<LiveValue> Values { get; } = [];

    public void Set(string key, double value, string group = "Memoria", string unit = "%")
    {
        Values.RemoveAll(v => v.Key == key);
        Values.Add(new LiveValue(key, key, group, value, unit));
    }

    public ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct) => new([.. Values]);
}

public class LiveBoardTests
{
    private static string UniqueName() => $@"Local\OptLife.Test.{Guid.NewGuid():N}";

    [Fact]
    public void What_the_agent_publishes_another_reader_sees_tagged_by_module()
    {
        var name = UniqueName();
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        using var board = new LiveBoard(name);

        board.Publish("monitor", [new LiveValue("gpu.temp", "Temperatura", "Scheda video", 61, "°C")], now);
        board.Publish("other", [new LiveValue("x", "X", "G", 1.5, "GB")], now);

        var read = LiveBoard.Read(now.AddSeconds(2), name);
        Assert.Equal(2, read.Count);
        var gpu = read.Single(e => e.Key == "gpu.temp");
        Assert.Equal(("monitor", "Temperatura", "Scheda video", 61d, "°C"), (gpu.Module, gpu.Label, gpu.Group, gpu.Value, gpu.Unit));
    }

    [Fact]
    public void Publishing_again_replaces_that_modules_values()
    {
        var name = UniqueName();
        var now = DateTimeOffset.UtcNow;
        using var board = new LiveBoard(name);
        board.Publish("monitor", [new LiveValue("a", "A", "G", 1, "%"), new LiveValue("b", "B", "G", 2, "%")], now);

        board.Publish("monitor", [new LiveValue("a", "A", "G", 9, "%")], now);

        Assert.Equal(9, Assert.Single(LiveBoard.Read(now, name)).Value);
    }

    [Fact]
    public void A_module_that_stopped_publishing_fades_out()
    {
        var name = UniqueName();
        var now = DateTimeOffset.UtcNow;
        using var board = new LiveBoard(name);
        board.Publish("monitor", [new LiveValue("a", "A", "G", 1, "%")], now);

        Assert.Single(LiveBoard.Read(now.AddSeconds(29), name));
        Assert.Empty(LiveBoard.Read(now.AddSeconds(31), name));
    }

    [Fact]
    public void With_no_agent_there_is_simply_nothing_to_read()
    {
        Assert.Empty(LiveBoard.Read(DateTimeOffset.UtcNow, UniqueName()));
    }

    [Fact]
    public async Task A_module_reaches_the_board_through_its_context()
    {
        using var box = new Sandbox();
        var name = UniqueName();
        using var board = new LiveBoard(name);
        var runner = new ModuleRunner(box.Store, box.Config, box.Clock, board);
        var task = FakeModule.Task(ctx => ctx.PublishLive([new LiveValue("k", "K", "G", 42, "ms")]));
        var module = new FakeModule { Tasks = [task] };

        Assert.True(await runner.RunTaskAsync(module, task, CancellationToken.None));

        var entry = Assert.Single(LiveBoard.Read(box.Clock.Now, name));
        Assert.Equal(("fake", 42d), (entry.Module, entry.Value));
    }

    [Theory]
    [InlineData(61.4, "°C", "61")]
    [InlineData(11.26, "GB", "11,3")]
    [InlineData(0.4, "%", "0")]
    public void Numbers_are_shown_the_way_people_read_them(double value, string unit, string expected) =>
        Assert.Equal(expected, LiveEntry.FormatNumber(value, unit));
}

public class FastTaskBookkeepingTests
{
    [Fact]
    public async Task A_task_running_every_few_seconds_does_not_rewrite_its_state_each_time()
    {
        using var box = new Sandbox();
        var task = FakeModule.Task(_ => { });
        var module = new FakeModule { Tasks = [task] };
        var t0 = box.Clock.Now;

        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        box.Clock.Now = t0.AddSeconds(2);
        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        Assert.Equal(t0, box.Runner.ReadStatus(module, task).LastRun);

        box.Clock.Now = t0.AddSeconds(31);
        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        Assert.Equal(t0.AddSeconds(31), box.Runner.ReadStatus(module, task).LastRun);
    }

    [Fact]
    public async Task A_failure_is_recorded_at_once_even_between_two_refreshes()
    {
        using var box = new Sandbox();
        var broken = false;
        var task = FakeModule.Task(_ => { if (broken) throw new InvalidOperationException("rotto"); });
        var module = new FakeModule { Tasks = [task] };

        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        broken = true;
        box.Clock.Now = box.Clock.Now.AddSeconds(2);
        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        Assert.Contains("rotto", box.Runner.ReadStatus(module, task).LastError);

        broken = false;
        box.Clock.Now = box.Clock.Now.AddSeconds(2);
        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        Assert.Null(box.Runner.ReadStatus(module, task).LastError);
    }
}

public class HistoryPruningTests
{
    [Fact]
    public void Old_measurements_events_and_resolved_problems_are_dropped_and_recent_ones_kept()
    {
        using var box = new Sandbox();
        var now = box.Clock.Now;
        box.Store.InsertSamples("m", [new("a", 1), new("b", 2)], now.AddDays(-40));
        box.Store.InsertSample("m", "a", 3, now.AddDays(-1));
        box.Store.InsertEvents("m", [new NewEvent(now.AddDays(-200), "k", Severity.Low, "vecchio"), new NewEvent(now.AddDays(-2), "k", Severity.Low, "recente")]);
        box.Store.SyncFindings("m", "s", [new NewFinding("gone", Severity.Low, "t", "d")], now.AddDays(-300));
        box.Store.SyncFindings("m", "s", [new NewFinding("open", Severity.Low, "t", "d")], now.AddDays(-299));

        var removed = box.Store.Prune(samplesBefore: now.AddDays(-30), eventsBefore: now.AddDays(-180));

        Assert.Equal(4, removed);
        Assert.Equal(3, Assert.Single(box.Store.QuerySamples("a", now.AddDays(-400), now)).Value);
        Assert.Equal("recente", Assert.Single(box.Store.QueryEvents(now.AddDays(-400), now)).Title);
        Assert.Equal("open", Assert.Single(box.Store.GetFindings(includeClosed: true)).Key); // open problems are never pruned
    }

    [Fact]
    public void Readings_that_are_not_numbers_are_not_stored()
    {
        using var box = new Sandbox();
        box.Store.InsertSamples("m", [new("a", double.NaN), new("a", 5)], box.Clock.Now);
        Assert.Equal(5, Assert.Single(box.Store.QuerySamples("a", box.Clock.Now.AddHours(-1), box.Clock.Now.AddHours(1))).Value);
    }
}

public class MonitorModuleTests
{
    private static async Task Sample(Sandbox box, MonitorModule module) =>
        Assert.True(await box.Runner.RunTaskAsync(module, module.Tasks[0], CancellationToken.None),
            box.Runner.ReadStatus(module, module.Tasks[0]).LastError);

    private static void Set(Sandbox box, string key, int value) =>
        box.Config.UpdateModule("monitor", c => c.Settings[key] = JsonSerializer.SerializeToElement(value));

    [Fact]
    public async Task Readings_within_a_minute_are_stored_once_as_their_average()
    {
        using var box = new Sandbox();
        var sensor = new FakeSensor();
        var module = new MonitorModule([sensor], () => "");
        var t0 = box.Clock.Now; // 12:00:00

        sensor.Set("gpu.temp", 60);
        await Sample(box, module);
        box.Clock.Now = t0.AddSeconds(30);
        sensor.Set("gpu.temp", 70);
        await Sample(box, module);
        Assert.Empty(box.Store.QuerySamples("gpu.temp", t0.AddHours(-1), t0.AddHours(1))); // the minute is not over yet

        box.Clock.Now = t0.AddSeconds(61);
        sensor.Set("gpu.temp", 99);
        await Sample(box, module);

        var stored = Assert.Single(box.Store.QuerySamples("gpu.temp", t0.AddHours(-1), t0.AddHours(1)));
        Assert.Equal(65, stored.Value);
        Assert.Equal(t0, stored.Time);
    }

    [Fact]
    public async Task The_first_source_to_answer_for_a_value_wins()
    {
        using var box = new Sandbox();
        var driver = new FakeSensor();
        var generic = new FakeSensor();
        driver.Set("gpu.temp", 60);
        generic.Set("gpu.temp", 10);
        generic.Set("cpu.temp", 50);
        var module = new MonitorModule([driver, generic], () => "");
        var t0 = box.Clock.Now;

        await Sample(box, module);
        box.Clock.Now = t0.AddSeconds(61);
        await Sample(box, module);

        Assert.Equal(60, box.Store.QuerySamples("gpu.temp", t0.AddHours(-1), t0.AddHours(1))[0].Value);
        Assert.Equal(50, box.Store.QuerySamples("cpu.temp", t0.AddHours(-1), t0.AddHours(1))[0].Value);
    }

    [Fact]
    public async Task Memory_full_for_long_enough_becomes_a_problem_that_names_who_was_using_it()
    {
        using var box = new Sandbox();
        Set(box, "ramMinutes", 3);
        var sensor = new FakeSensor();
        sensor.Set("ram.used_pct", 94);
        var module = new MonitorModule([sensor], () => "chrome (3,1 GB), Discord (1,2 GB)");
        var t0 = box.Clock.Now;

        for (var minute = 0; minute <= 2; minute++)
        {
            box.Clock.Now = t0.AddMinutes(minute);
            await Sample(box, module);
        }
        Assert.Empty(box.Store.GetFindings()); // two full minutes so far

        box.Clock.Now = t0.AddMinutes(3);
        await Sample(box, module);

        var finding = Assert.Single(box.Store.GetFindings());
        Assert.Contains("3 minuti", finding.Title);
        Assert.Contains("picco 94%", finding.Detail);
        Assert.Contains("chrome (3,1 GB)", finding.Detail);
        // One line in the history for the whole episode, not one per minute.
        Assert.Single(box.Store.QueryEvents(t0.AddHours(-1), t0.AddHours(1), kind: "ram_pressure"));
    }

    [Fact]
    public async Task A_full_disk_is_reported_and_clears_when_space_comes_back()
    {
        using var box = new Sandbox();
        var sensor = new FakeSensor();
        sensor.Set("disk.C.free_pct", 4, "Dischi");
        sensor.Set("disk.C.free_gb", 18, "Dischi", "GB");
        var module = new MonitorModule([sensor], () => "");
        var t0 = box.Clock.Now;

        await Sample(box, module);
        box.Clock.Now = t0.AddMinutes(1);
        await Sample(box, module);
        var finding = Assert.Single(box.Store.GetFindings());
        Assert.Equal("Disco C: quasi pieno", finding.Title);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains("4% (18 GB)", finding.Detail);

        sensor.Set("disk.C.free_pct", 40, "Dischi");
        box.Clock.Now = t0.AddMinutes(2);
        await Sample(box, module);
        Assert.Empty(box.Store.GetFindings());
    }
}

public class MonitorRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private static List<SamplePoint> Minutes(params double[] values) =>
        [.. values.Select((v, i) => new SamplePoint(T0.AddMinutes(i), v))];

    [Fact]
    public void Short_spikes_of_memory_are_not_a_problem()
    {
        Assert.Null(MonitorRules.Memory(Minutes(95, 50, 96, 50), threshold: 90, minMinutes: 3, topConsumers: null));
    }

    [Fact]
    public void A_hot_card_is_reported_with_its_peak()
    {
        var finding = MonitorRules.Temperature("gpu.temp", "Scheda video", Minutes(70, 86, 88, 91, 70), 85, 3, "consiglio")!;

        Assert.Equal("Scheda video sopra 85 °C per 3 minuti nelle ultime 24 ore", finding.Title);
        Assert.Contains("Picco 91 °C", finding.Detail);
        Assert.Null(MonitorRules.Temperature("gpu.temp", "Scheda video", Minutes(70, 86, 88), 85, 3, "consiglio"));
    }

    [Fact]
    public void Loss_that_starts_at_the_router_is_blamed_on_the_home_network()
    {
        var router = Minutes(0, 20, 30, 25, 10, 12, 0);
        var internet = Minutes(0, 22, 35, 30, 11, 14, 0);

        var finding = MonitorRules.Network(router, internet, lossThreshold: 5, minMinutes: 5)!;

        Assert.Contains("5 minuti", finding.Title);
        Assert.Contains("in casa", finding.Detail);
        Assert.Contains("Wi-Fi", finding.Advice);
    }

    [Fact]
    public void Loss_with_a_clean_router_is_blamed_on_the_line()
    {
        var router = Minutes(0, 0, 0, 0, 0, 0);
        var internet = Minutes(10, 12, 9, 30, 8, 7);

        var finding = MonitorRules.Network(router, internet, 5, 5)!;

        Assert.Contains("oltre", finding.Detail);
        Assert.Contains("provider", finding.Advice);
    }

    [Fact]
    public void A_few_bad_minutes_of_connection_are_not_reported()
    {
        Assert.Null(MonitorRules.Network(Minutes(0, 50, 0, 0), Minutes(0, 50, 0, 0), 5, 5));
    }
}

public class SensorTests
{
    private static ModuleSettings Hosts(params string[] lines) =>
        new([SettingDefinition.List(NetworkSensor.HostsSetting, "", "", lines)], new Dictionary<string, JsonElement>());

    [Fact]
    public async Task Latency_is_the_last_answer_and_loss_is_the_share_of_unanswered_pings()
    {
        var answers = new Queue<long?>([12, null, 20, null]);
        var sensor = new NetworkSensor((_, _) => Task.FromResult(answers.Dequeue()), () => "192.168.1.1");
        var settings = Hosts("router=gateway");

        IReadOnlyList<LiveValue> values = [];
        for (var i = 0; i < 4; i++) values = await sensor.ReadAsync(settings, CancellationToken.None);

        Assert.Equal(20, values.Single(v => v.Key == "net.router.ms").Value); // last one that answered
        Assert.Equal(50, values.Single(v => v.Key == "net.router.loss").Value);
        Assert.Equal("Router · latenza", values.Single(v => v.Key == "net.router.ms").Label);
    }

    [Fact]
    public async Task The_word_gateway_is_replaced_by_the_router_address_and_other_hosts_are_pinged_as_written()
    {
        var pinged = new List<string>();
        var sensor = new NetworkSensor((address, _) => { lock (pinged) pinged.Add(address); return Task.FromResult<long?>(1); }, () => "10.0.0.1");

        await sensor.ReadAsync(Hosts("router=gateway", "internet=1.1.1.1", "NAS di casa = 192.168.1.50"), CancellationToken.None);

        Assert.Equal(["1.1.1.1", "10.0.0.1", "192.168.1.50"], pinged.Order());
    }

    [Fact]
    public async Task With_no_network_there_is_loss_but_no_made_up_latency()
    {
        var sensor = new NetworkSensor((_, _) => Task.FromResult<long?>(null), () => null);

        var values = await sensor.ReadAsync(Hosts("router=gateway"), CancellationToken.None);

        Assert.Equal(100, Assert.Single(values).Value);
    }

    [Fact]
    public void Host_lines_that_make_no_sense_are_ignored()
    {
        var hosts = NetworkSensor.ParseHosts(["router=gateway", "senza uguale", "=1.2.3.4", "vuoto=", "router=doppione"]);
        Assert.Equal([("router", "gateway")], hosts);
    }

    [Fact]
    public void Afterburners_shared_memory_gives_processor_readings_and_skips_empty_slots()
    {
        const int header = 32, entry = 1324, data = 1300;
        (string Name, float Value)[] entries = [("CPU temperature", 48.5f), ("GPU temperature", 39f), ("CPU clock", float.MaxValue), ("Framerate", 144f)];
        using var map = MemoryMappedFile.CreateNew(null, header + entries.Length * entry);
        using var view = map.CreateViewAccessor();
        view.Write(0, 0x4D41484Du);
        view.Write(8, (uint)header);
        view.Write(12, (uint)entries.Length);
        view.Write(16, (uint)entry);
        for (var i = 0; i < entries.Length; i++)
        {
            var name = Encoding.ASCII.GetBytes(entries[i].Name);
            view.WriteArray(header + i * entry, name, 0, name.Length);
            view.Write(header + i * entry + data, entries[i].Value);
        }

        var values = AfterburnerSensor.Parse(view);

        Assert.Equal(["cpu.temp", "gpu.temp"], values.Select(v => v.Key));
        Assert.Equal(48.5, values[0].Value);
    }

    [Fact]
    public void Memory_that_is_not_afterburners_is_ignored()
    {
        using var map = MemoryMappedFile.CreateNew(null, 4096);
        using var view = map.CreateViewAccessor();
        Assert.Empty(AfterburnerSensor.Parse(view));
        Assert.Empty(AfterburnerSensor.Read($"OptLife.Test.Missing.{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task The_built_in_sensors_work_on_this_machine_without_special_rights()
    {
        var none = new ModuleSettings([], new Dictionary<string, JsonElement>());
        var memory = await new MemorySensor().ReadAsync(none, CancellationToken.None);
        Assert.InRange(memory.Single(v => v.Key == "ram.used_pct").Value, 1, 100);

        var cpu = new CpuLoadSensor();
        await cpu.ReadAsync(none, CancellationToken.None); // the first reading only sets the baseline
        await Task.Delay(120);
        Assert.InRange(Assert.Single(await cpu.ReadAsync(none, CancellationToken.None)).Value, 0, 100);

        var disks = await new DiskSensor().ReadAsync(none, CancellationToken.None);
        Assert.Contains(disks, v => v.Key.EndsWith(".free_pct") && v.Value is > 0 and <= 100);
    }
}

public class MonitorPageTests
{
    private static PanelViewModel Panel(Sandbox box, Func<IReadOnlyList<LiveEntry>> live)
    {
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [new FakeModule()], () => true, () => true, box.Clock, live);
        vm.ShowCommand.Execute("Monitor");
        return vm;
    }

    private static LiveEntry Entry(string key, string group, double value, string unit = "%") =>
        new("monitor", key, key, group, value, unit);

    [Fact]
    public void Live_values_are_grouped_and_follow_the_sensors_without_rebuilding_the_page()
    {
        using var box = new Sandbox();
        var live = new List<LiveEntry> { Entry("ram.used_pct", "Memoria", 71.6), Entry("gpu.temp", "Scheda video", 60, "°C") };
        var vm = Panel(box, () => live);

        Assert.Equal(["Memoria", "Scheda video"], vm.MonitorGroups.Select(g => g.Name));
        var tile = vm.MonitorGroups[0].Tiles[0];
        Assert.Equal("72", tile.ValueText);

        live[0] = Entry("ram.used_pct", "Memoria", 80);
        vm.Refresh();

        Assert.Same(tile, vm.MonitorGroups[0].Tiles[0]);
        Assert.Equal("80", tile.ValueText);
    }

    [Fact]
    public void Each_tile_carries_the_last_hour_of_its_own_history()
    {
        using var box = new Sandbox();
        var now = box.Clock.Now;
        box.Store.InsertSample("monitor", "gpu.temp", 55, now.AddMinutes(-90));
        box.Store.InsertSample("monitor", "gpu.temp", 60, now.AddMinutes(-30));
        box.Store.InsertSample("monitor", "gpu.temp", 62, now.AddMinutes(-1));

        var vm = Panel(box, () => [Entry("gpu.temp", "Scheda video", 63, "°C")]);

        Assert.Equal([60, 62], vm.MonitorGroups[0].Tiles[0].History);
    }

    [Fact]
    public void Without_live_values_the_page_says_why()
    {
        using var box = new Sandbox();
        var vm = Panel(box, () => []);

        Assert.Empty(vm.MonitorGroups);
        Assert.Contains("agente non è attivo", vm.MonitorEmptyText);
    }
}
