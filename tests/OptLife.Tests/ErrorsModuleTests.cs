using OptLife.Core;
using OptLife.Modules.Errors;

namespace OptLife.Tests;

internal static class Raw
{
    private static long _record;
    public static readonly DateTimeOffset T0 = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public static RawEvent Event(string provider, int id, int level = 2, string log = "System", DateTimeOffset? at = null, string? message = null, params string[] properties) =>
        new(log, provider, id, level, at ?? T0, Interlocked.Increment(ref _record), properties, message);

    /// <summary>Property layout copied from a real "Application Error" record on the target PC.</summary>
    public static RawEvent Crash(string exe, string module, string code = "c0000409", DateTimeOffset? at = null) =>
        Event("Application Error", 1000, log: "Application", at: at, properties:
        [
            exe, "2.8.0.42", "6a42830c", module, "2.8.0.42", "6a42830c", code, "00000000002c9819", "7368",
            "134382576887839669", $@"C:\Giochi\{exe}", $@"C:\Giochi\{module}", "e8901a23-8dc3-46b6-953d-b3bbe6db128c", "", "",
        ]);
}

public class EventClassifierTests
{
    private static NewEvent? Classify(RawEvent e, params string[] ignored) => EventClassifier.Classify(e, ignored);

    [Fact]
    public void A_program_crash_keeps_exe_module_and_code_and_groups_by_program()
    {
        var e = Classify(Raw.Crash("Deadlock.exe", "rendersystemdx11.dll", "0xC0000005"))!;

        Assert.Equal(ErrorKinds.AppCrash, e.Kind);
        Assert.Equal("crash|deadlock.exe", e.GroupKey);
        Assert.Equal("rendersystemdx11.dll", e.Data!["faultModule"]);
        Assert.Equal("c0000005", e.Data["code"]);
        Assert.Contains("Deadlock.exe", e.Title);
    }

    [Fact]
    public void A_hang_is_its_own_kind()
    {
        var e = Classify(Raw.Event("Application Hang", 1002, log: "Application", properties:
            ["Overwatch.exe", "2.25.0.23016", "9648", "134358512256916341", "4294967295", @"E:\shootergame\_retail_\Overwatch.exe"]))!;

        Assert.Equal(ErrorKinds.AppHang, e.Kind);
        Assert.Equal("hang|overwatch.exe", e.GroupKey);
    }

    [Theory]
    [InlineData(7000, new[] { "MioServizio", "%%2" })]
    [InlineData(7034, new[] { "MioServizio", "3" })]
    [InlineData(7009, new[] { "30000", "MioServizio" })] // timeout events put the name second
    [InlineData(7011, new[] { "30000", "MioServizio" })]
    public void Service_failures_group_by_service_whatever_the_event_layout(int id, string[] properties)
    {
        var e = Classify(Raw.Event("Service Control Manager", id, properties: properties))!;

        Assert.Equal(ErrorKinds.Service, e.Kind);
        Assert.Equal("service|mioservizio", e.GroupKey);
    }

    [Fact]
    public void Unexpected_shutdown_is_reported_once_not_twice()
    {
        Assert.Equal(ErrorKinds.UnexpectedShutdown, Classify(Raw.Event("Microsoft-Windows-Kernel-Power", 41, level: 1))!.Kind);
        Assert.Null(Classify(Raw.Event("EventLog", 6008)));
    }

    [Fact]
    public void Blue_screen_keeps_only_the_stop_code()
    {
        var e = Classify(Raw.Event("Microsoft-Windows-WER-SystemErrorReporting", 1001,
            properties: ["0x0000009f (0x0000000000000003, 0xffff, 0xffff, 0xffff)", @"C:\WINDOWS\MEMORY.DMP"]))!;

        Assert.Equal(ErrorKinds.BlueScreen, e.Kind);
        Assert.Equal(Severity.High, e.Severity);
        Assert.Equal("0x0000009f", e.Data!["code"]);
    }

    [Fact]
    public void A_driver_reset_counts_even_though_windows_logs_it_as_a_warning()
    {
        var e = Classify(Raw.Event("Display", 4101, level: 3, properties: ["nvlddmkm"]))!;
        Assert.Equal(ErrorKinds.Gpu, e.Kind);
    }

    [Fact]
    public void Informational_records_of_watched_providers_are_not_problems()
    {
        Assert.Null(Classify(Raw.Event("disk", 98, level: 4)));
        Assert.Null(Classify(Raw.Event("Display", 1, level: 4)));
        Assert.Equal(ErrorKinds.Disk, Classify(Raw.Event("disk", 153, level: 3))!.Kind);
    }

    [Fact]
    public void Error_reports_matter_only_when_they_are_kernel_level()
    {
        Assert.Null(Classify(Raw.Event("Windows Error Reporting", 1001, level: 4, log: "Application",
            properties: ["123", "4", "APPCRASH", "Non disponibile", "0", "gioco.exe"])));

        var gpu = Classify(Raw.Event("Windows Error Reporting", 1001, level: 4, log: "Application",
            properties: ["", "0", "LiveKernelEvent", "Non disponibile", "0", "141", "ffff", "ffff"]))!;
        Assert.Equal(ErrorKinds.Gpu, gpu.Kind);
    }

    [Fact]
    public void Anything_else_at_error_level_becomes_a_generic_system_error()
    {
        var e = Classify(Raw.Event("VSS", 13, message: "Informazioni sul servizio Copia Shadow del volume"))!;

        Assert.Equal(ErrorKinds.Other, e.Kind);
        Assert.Equal("evt|vss|13", e.GroupKey);
        Assert.Equal("Informazioni sul servizio Copia Shadow del volume", e.Title);
        Assert.Null(Classify(Raw.Event("VSS", 13, level: 3)));
    }

    [Fact]
    public void Ignored_origins_are_dropped_by_provider_or_by_provider_and_id()
    {
        var dcom = Raw.Event("Microsoft-Windows-DistributedCOM", 10016);

        Assert.NotNull(Classify(dcom));
        Assert.Null(Classify(dcom, "Microsoft-Windows-DistributedCOM:10016"));
        Assert.Null(Classify(dcom, "microsoft-windows-distributedcom"));
        Assert.NotNull(Classify(dcom, "Microsoft-Windows-DistributedCOM:10010"));
    }

    [Fact]
    public void The_same_record_always_gets_the_same_identity()
    {
        var raw = Raw.Crash("a.exe", "a.exe");
        Assert.Equal(Classify(raw)!.Uid, Classify(raw)!.Uid);
        Assert.NotEqual(Classify(raw)!.Uid, Classify(Raw.Crash("a.exe", "a.exe"))!.Uid);
    }
}

public class ErrorsModuleTests
{
    private sealed class FakeSource : IEventSource
    {
        public List<RawEvent> Events { get; } = [];
        public List<(string Log, DateTimeOffset Since)> Reads { get; } = [];

        public IEnumerable<RawEvent> Read(string log, DateTimeOffset since)
        {
            Reads.Add((log, since));
            return Events.Where(e => e.Log == log && e.Time >= since);
        }
    }

    private static async Task Scan(Sandbox box, ErrorsModule module) =>
        Assert.True(await box.Runner.RunTaskAsync(module, module.Tasks[0], CancellationToken.None));

    [Fact]
    public async Task One_crash_is_history_two_of_the_same_program_are_a_problem()
    {
        using var box = new Sandbox();
        var source = new FakeSource();
        var module = new ErrorsModule(source);
        var now = box.Clock.Now;

        source.Events.Add(Raw.Crash("Deadlock.exe", "rendersystemdx11.dll", "c0000005", now.AddDays(-2)));
        await Scan(box, module);
        Assert.Empty(box.Store.GetFindings());
        Assert.Single(box.Store.QueryEvents(now.AddDays(-30), now));

        source.Events.Add(Raw.Crash("Deadlock.exe", "rendersystemdx11.dll", "c0000005", now.AddHours(-1)));
        await Scan(box, module);

        var finding = Assert.Single(box.Store.GetFindings());
        Assert.Contains("Deadlock.exe è andato in crash 2 volte", finding.Title);
        Assert.Contains("Sempre nello stesso punto", finding.Detail);
        Assert.Contains("accesso a memoria non valida", finding.Detail);
        Assert.Contains("grafica", finding.Advice);
    }

    [Fact]
    public async Task Scanning_again_does_not_duplicate_and_resumes_from_the_newest_record()
    {
        using var box = new Sandbox();
        var source = new FakeSource();
        var module = new ErrorsModule(source);
        var now = box.Clock.Now;
        var crashTime = now.AddHours(-3);
        source.Events.Add(Raw.Crash("a.exe", "a.exe", at: crashTime));

        await Scan(box, module);
        await Scan(box, module);

        Assert.Single(box.Store.QueryEvents(now.AddDays(-30), now));
        Assert.Equal(now.AddDays(-14), source.Reads.First(r => r.Log == "Application").Since); // first run: lookback
        Assert.Equal(crashTime, source.Reads.Last(r => r.Log == "Application").Since);         // then: from the newest seen
    }

    [Fact]
    public async Task A_problem_disappears_by_itself_when_it_stops_happening()
    {
        using var box = new Sandbox();
        var source = new FakeSource();
        var module = new ErrorsModule(source);
        source.Events.Add(Raw.Event("Microsoft-Windows-Kernel-Power", 41, level: 1, at: box.Clock.Now.AddDays(-6)));

        await Scan(box, module);
        Assert.Single(box.Store.GetFindings());

        box.Clock.Now = box.Clock.Now.AddDays(3); // the shutdown is now older than the 7-day window
        await Scan(box, module);
        Assert.Empty(box.Store.GetFindings());
    }

    [Fact]
    public async Task Thresholds_come_from_the_settings()
    {
        using var box = new Sandbox();
        var source = new FakeSource();
        var module = new ErrorsModule(source);
        source.Events.Add(Raw.Crash("a.exe", "a.exe", at: box.Clock.Now.AddHours(-1)));
        box.Config.UpdateModule("errors", c => c.Settings["crashThreshold"] = System.Text.Json.JsonSerializer.SerializeToElement(1));

        await Scan(box, module);

        Assert.Single(box.Store.GetFindings());
    }

    [Fact]
    public async Task Generic_noise_shows_up_only_past_the_recurring_threshold_and_as_info()
    {
        using var box = new Sandbox();
        var source = new FakeSource();
        var module = new ErrorsModule(source);
        for (var i = 0; i < 9; i++)
            source.Events.Add(Raw.Event("VSS", 13, at: box.Clock.Now.AddHours(-i - 1), message: "errore vss"));

        await Scan(box, module);
        Assert.Empty(box.Store.GetFindings());

        source.Events.Add(Raw.Event("VSS", 13, at: box.Clock.Now.AddMinutes(-5), message: "errore vss"));
        await Scan(box, module);
        Assert.Equal(Severity.Info, Assert.Single(box.Store.GetFindings()).Severity);
    }
}

public class ErrorRulesTests
{
    private static StoredEvent Stored(RawEvent raw, long id)
    {
        var e = EventClassifier.Classify(raw, [])!;
        return new StoredEvent(id, e.Time, "errors", e.Kind, e.Severity, e.Title, e.GroupKey, e.Data!);
    }

    [Fact]
    public void Crashes_scattered_across_components_point_away_from_the_program()
    {
        var events = new[] { "a.dll", "b.dll", "c.dll", "d.dll" }
            .Select((module, i) => Stored(Raw.Crash("gioco.exe", module, "c0000005", Raw.T0.AddHours(i)), i))
            .ToList();

        var finding = Assert.Single(ErrorRules.Evaluate(events, 7, crashThreshold: 2, recurringThreshold: 10));

        Assert.Contains("4 volte", finding.Title);
        Assert.Contains("1 volte su 4", finding.Detail);
        Assert.Contains("memoria o di sistema", finding.Advice);
    }

    [Fact]
    public void Many_repetitions_raise_the_severity()
    {
        var events = Enumerable.Range(0, 5)
            .Select(i => Stored(Raw.Crash("gioco.exe", "gioco.exe", at: Raw.T0.AddHours(i)), i))
            .ToList();

        var finding = Assert.Single(ErrorRules.Evaluate(events, 7, 2, 10));

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains("programma stesso", finding.Advice);
    }

    [Fact]
    public void Errors_written_while_windows_shuts_down_are_not_problems()
    {
        const string vss = "Volume Shadow Copy Service error: Unexpected error calling routine CoCreateInstance. hr = 0x8007045b, È in corso l'arresto del sistema.";
        Assert.Null(EventClassifier.Classify(Raw.Event("VSS", 8193, log: "Application", message: vss), []));
        Assert.NotNull(EventClassifier.Classify(Raw.Event("VSS", 8193, log: "Application", message: "hr = 0x80070005"), []));

        // Events stored before the rule existed are left out of the evaluation as well.
        var old = Enumerable.Range(0, 12)
            .Select(i => new StoredEvent(i, Raw.T0.AddHours(i), "errors", ErrorKinds.Other, Severity.Low, "VSS", "evt|vss|8193",
                new Dictionary<string, string> { ["message"] = vss }))
            .ToList();
        Assert.Empty(ErrorRules.Evaluate(old, 7, 2, 10));
    }

    [Fact]
    public void A_service_failing_a_couple_of_times_is_not_reported()
    {
        var twice = Enumerable.Range(0, 2)
            .Select(i => Stored(Raw.Event("Service Control Manager", 7034, at: Raw.T0.AddHours(i), properties: ["Svc", "1"]), i))
            .ToList();
        Assert.Empty(ErrorRules.Evaluate(twice, 7, 2, 10));

        var third = twice.Append(Stored(Raw.Event("Service Control Manager", 7000, at: Raw.T0.AddHours(5), properties: ["Svc", "%%2"]), 9)).ToList();
        Assert.Contains("Svc", Assert.Single(ErrorRules.Evaluate(third, 7, 2, 10)).Title);
    }

    [Fact]
    public void The_records_windows_writes_for_one_service_failure_count_as_one()
    {
        // Each failed start produces a "timed out" and a "did not start" record a moment apart.
        var events = Enumerable.Range(0, 3).SelectMany(i => new[]
        {
            Stored(Raw.Event("Service Control Manager", 7009, at: Raw.T0.AddHours(i), properties: ["30000", "Svc"]), i * 2),
            Stored(Raw.Event("Service Control Manager", 7000, at: Raw.T0.AddHours(i).AddSeconds(1), properties: ["Svc", "%%1053"]), i * 2 + 1),
        }).ToList();

        Assert.Contains("3 volte", Assert.Single(ErrorRules.Evaluate(events, 7, 2, 10)).Title);
    }
}
