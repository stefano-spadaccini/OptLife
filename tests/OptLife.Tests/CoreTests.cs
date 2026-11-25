using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Hosting;
using OptLife.Core.Storage;

namespace OptLife.Tests;

/// <summary>A throwaway data folder with its own database, config and adjustable clock.</summary>
public sealed class Sandbox : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optlife-tests", Guid.NewGuid().ToString("N"));

    public Sandbox()
    {
        Directory.CreateDirectory(_dir);
        Store = new Store(Path.Combine(_dir, "test.db"), pooling: false);
        Config = new ConfigStore(Path.Combine(_dir, "config.json"));
        Runner = new ModuleRunner(Store, Config, Clock);
    }

    public Store Store { get; }
    public ConfigStore Config { get; }
    public FakeClock Clock { get; } = new();
    public ModuleRunner Runner { get; }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public sealed class FakeModule : IModule
{
    public ModuleInfo Info { get; init; } = new("fake", "Finto", "Modulo di prova");
    public IReadOnlyList<SettingDefinition> Settings { get; init; } = [];
    public IReadOnlyList<ModuleTask> Tasks { get; init; } = [];
    public IReadOnlyList<ModuleAction> Actions { get; init; } = [];
    public IReadOnlyList<ModuleView> Views { get; init; } = [];

    public static ModuleTask Task(Action<IModuleContext> body) =>
        new("t", "Attività", TimeSpan.FromMinutes(1), (ctx, _) => { body(ctx); return System.Threading.Tasks.Task.CompletedTask; });
}

public class StoreTests
{
    [Fact]
    public void Events_with_the_same_uid_are_stored_once()
    {
        using var box = new Sandbox();
        var e = new NewEvent(box.Clock.Now, "k", Severity.Low, "titolo", "g", Uid: "abc");

        Assert.Equal(1, box.Store.InsertEvents("m", [e, e]));
        Assert.Equal(0, box.Store.InsertEvents("m", [e]));
        Assert.Equal(1, box.Store.InsertEvents("other", [e])); // uid is per module
        Assert.Single(box.Store.QueryEvents(box.Clock.Now.AddHours(-1), box.Clock.Now.AddHours(1), module: "m"));
    }

    [Fact]
    public void Event_data_round_trips()
    {
        using var box = new Sandbox();
        box.Store.InsertEvents("m", [new NewEvent(box.Clock.Now, "k", Severity.High, "t", Data: new Dictionary<string, string> { ["exe"] = "gioco.exe" })]);

        var stored = Assert.Single(box.Store.QueryEvents(box.Clock.Now.AddHours(-1), box.Clock.Now.AddHours(1)));
        Assert.Equal("gioco.exe", stored.Data["exe"]);
        Assert.Equal(Severity.High, stored.Severity);
    }

    [Fact]
    public void A_finding_no_longer_reported_is_resolved_and_reopens_if_it_comes_back()
    {
        using var box = new Sandbox();
        var t0 = box.Clock.Now;
        var f = new NewFinding("k", Severity.Medium, "titolo", "dettaglio");

        box.Store.SyncFindings("m", "s", [f], t0);
        Assert.Single(box.Store.GetFindings());

        box.Store.SyncFindings("m", "s", [], t0.AddHours(1));
        Assert.Empty(box.Store.GetFindings());
        Assert.Equal(FindingStatus.Resolved, Assert.Single(box.Store.GetFindings(includeClosed: true)).Status);

        box.Store.SyncFindings("m", "s", [f], t0.AddHours(2));
        var reopened = Assert.Single(box.Store.GetFindings());
        Assert.Equal(t0.AddHours(2), reopened.FirstSeen);
    }

    [Fact]
    public void An_ignored_finding_stays_ignored_while_it_keeps_being_reported()
    {
        using var box = new Sandbox();
        var f = new NewFinding("k", Severity.Info, "rumore", "dettaglio");
        box.Store.SyncFindings("m", "s", [f], box.Clock.Now);
        var id = Assert.Single(box.Store.GetFindings()).Id;

        Assert.True(box.Store.SetFindingStatus(id, FindingStatus.Ignored));
        box.Store.SyncFindings("m", "s", [f with { Title = "rumore, ancora" }], box.Clock.Now.AddHours(1));

        Assert.Empty(box.Store.GetFindings());
        var kept = Assert.Single(box.Store.GetFindings(includeClosed: true));
        Assert.Equal(FindingStatus.Ignored, kept.Status);
        Assert.Equal("rumore, ancora", kept.Title);
    }

    [Fact]
    public void Syncing_one_scope_leaves_other_scopes_and_modules_alone()
    {
        using var box = new Sandbox();
        var f = new NewFinding("k", Severity.Low, "t", "d");
        box.Store.SyncFindings("m", "a", [f], box.Clock.Now);
        box.Store.SyncFindings("m", "b", [f], box.Clock.Now);
        box.Store.SyncFindings("other", "a", [f], box.Clock.Now);

        box.Store.SyncFindings("m", "a", [], box.Clock.Now);

        Assert.Equal(2, box.Store.GetFindings().Count);
    }

    [Fact]
    public void Samples_are_returned_in_time_order_within_the_range()
    {
        using var box = new Sandbox();
        var t = box.Clock.Now;
        box.Store.InsertSample("m", "cpu.temp", 60, t.AddMinutes(2));
        box.Store.InsertSample("m", "cpu.temp", 50, t.AddMinutes(1));
        box.Store.InsertSample("m", "cpu.temp", 99, t.AddDays(1));
        box.Store.InsertSample("m", "gpu.temp", 70, t.AddMinutes(1));

        var points = box.Store.QuerySamples("cpu.temp", t, t.AddHours(1));

        Assert.Equal([50, 60], points.Select(p => p.Value));
    }
}

public class ModuleRunnerTests
{
    [Fact]
    public async Task A_throwing_task_is_contained_and_recorded()
    {
        using var box = new Sandbox();
        var task = FakeModule.Task(_ => throw new InvalidOperationException("rotto"));
        var module = new FakeModule { Tasks = [task] };

        Assert.False(await box.Runner.RunTaskAsync(module, task, CancellationToken.None));

        var status = box.Runner.ReadStatus(module, task);
        Assert.Equal(1, status.Failures);
        Assert.Contains("rotto", status.LastError);
        Assert.Empty(box.Store.GetFindings()); // one failure is not yet a problem worth showing
    }

    [Fact]
    public async Task Repeated_failures_raise_a_core_finding_that_clears_on_success()
    {
        using var box = new Sandbox();
        var broken = true;
        var task = FakeModule.Task(_ => { if (broken) throw new InvalidOperationException("rotto"); });
        var module = new FakeModule { Tasks = [task] };

        for (var i = 0; i < 3; i++) await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        var finding = Assert.Single(box.Store.GetFindings());
        Assert.Equal(ModuleRunner.CoreModuleId, finding.Module);

        broken = false;
        Assert.True(await box.Runner.RunTaskAsync(module, task, CancellationToken.None));
        Assert.Empty(box.Store.GetFindings());
        Assert.Equal(0, box.Runner.ReadStatus(module, task).Failures);
    }

    [Fact]
    public async Task What_a_module_writes_is_tagged_with_its_own_id()
    {
        using var box = new Sandbox();
        var task = FakeModule.Task(ctx =>
        {
            ctx.RecordEvents([new NewEvent(ctx.Now, "k", Severity.Low, "t")]);
            ctx.SyncFindings("s", [new NewFinding("k", Severity.Low, "t", "d")]);
            ctx.SetState("cursor", "42");
        });
        var module = new FakeModule { Tasks = [task] };

        Assert.True(await box.Runner.RunTaskAsync(module, task, CancellationToken.None));

        Assert.Equal("fake", Assert.Single(box.Store.QueryEvents(box.Clock.Now.AddHours(-1), box.Clock.Now.AddHours(1))).Module);
        Assert.Equal("fake", Assert.Single(box.Store.GetFindings()).Module);
        Assert.Equal("42", box.Store.GetState("fake", "cursor"));
    }

    [Fact]
    public async Task A_module_cannot_overwrite_the_core_bookkeeping()
    {
        using var box = new Sandbox();
        var task = FakeModule.Task(ctx => ctx.SetState("_task:t:failures", "0"));
        var module = new FakeModule { Tasks = [task] };

        Assert.False(await box.Runner.RunTaskAsync(module, task, CancellationToken.None));
        Assert.Contains("reserved", box.Runner.ReadStatus(module, task).LastError);
    }

    [Fact]
    public void A_module_is_enabled_until_the_config_says_otherwise()
    {
        using var box = new Sandbox();
        var module = new FakeModule();

        Assert.True(box.Runner.IsEnabled(module));
        box.Config.UpdateModule("fake", c => c.Enabled = false);
        Assert.False(box.Runner.IsEnabled(module));
    }

    [Theory]
    [InlineData(Permission.Auto, false, true)]
    [InlineData(Permission.Ask, false, false)]
    [InlineData(Permission.Ask, true, true)]
    [InlineData(Permission.Never, true, false)]
    public async Task Actions_obey_their_permission_and_are_always_logged(Permission permission, bool confirmed, bool shouldRun)
    {
        using var box = new Sandbox();
        var ran = false;
        var action = new ModuleAction("a", "Azione", "desc", permission, (_, _, _) =>
        {
            ran = true;
            return Task.FromResult(new ActionResult(true, "fatto", Undo: "annulla così"));
        });
        var module = new FakeModule { Actions = [action] };

        var result = await box.Runner.RunActionAsync(module, action, "utente", confirmed, null, CancellationToken.None);

        Assert.Equal(shouldRun, ran);
        Assert.Equal(shouldRun, result.Ok);
        var logged = Assert.Single(box.Store.GetActions());
        Assert.Equal(shouldRun, logged.Ok);
        Assert.Equal("utente", logged.Actor);
    }

    [Fact]
    public async Task The_user_can_tighten_an_action_permission_from_the_config()
    {
        using var box = new Sandbox();
        var ran = false;
        var action = new ModuleAction("a", "Azione", "desc", Permission.Auto, (_, _, _) =>
        {
            ran = true;
            return Task.FromResult(new ActionResult(true, "fatto"));
        });
        var module = new FakeModule { Actions = [action] };
        box.Config.UpdateModule("fake", c => c.Permissions["a"] = Permission.Never);

        await box.Runner.RunActionAsync(module, action, "auto", confirmed: false, null, CancellationToken.None);

        Assert.False(ran);
    }

    [Fact]
    public async Task A_throwing_action_is_contained_and_logged_as_failed()
    {
        using var box = new Sandbox();
        var action = new ModuleAction("a", "Azione", "desc", Permission.Auto, (_, _, _) => throw new IOException("disco pieno"));
        var module = new FakeModule { Actions = [action] };

        var result = await box.Runner.RunActionAsync(module, action, "auto", false, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("disco pieno", Assert.Single(box.Store.GetActions()).Detail);
    }
}

public class SettingsTests
{
    private static readonly SettingDefinition[] Definitions =
    [
        SettingDefinition.Int("days", "Giorni", "", 7, 1, 30),
        SettingDefinition.Bool("on", "Acceso", "", true),
        SettingDefinition.List("names", "Nomi", "", "a", "b"),
        SettingDefinition.Text("path", "Percorso", "", @"C:\x"),
    ];

    private static ModuleSettings With(string json) =>
        new(Definitions, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    [Fact]
    public void Missing_values_fall_back_to_the_declared_default()
    {
        var s = With("{}");
        Assert.Equal(7, s.GetInt("days"));
        Assert.True(s.GetBool("on"));
        Assert.Equal(["a", "b"], s.GetList("names"));
        Assert.Equal(@"C:\x", s.GetText("path"));
    }

    [Fact]
    public void Stored_values_win_and_numbers_are_clamped_to_the_allowed_range()
    {
        var s = With("""{ "days": 400, "on": false, "names": ["z"], "path": "D:\\y" }""");
        Assert.Equal(30, s.GetInt("days"));
        Assert.False(s.GetBool("on"));
        Assert.Equal(["z"], s.GetList("names"));
        Assert.Equal(@"D:\y", s.GetText("path"));
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_treated_as_missing()
    {
        var s = With("""{ "days": "tanti", "on": 3, "names": "a" }""");
        Assert.Equal(7, s.GetInt("days"));
        Assert.True(s.GetBool("on"));
        Assert.Equal(["a", "b"], s.GetList("names"));
    }

    [Fact]
    public void Asking_for_an_undeclared_setting_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => With("{}").GetInt("nope"));
        Assert.Throws<ArgumentException>(() => With("{}").GetBool("days"));
    }

    [Fact]
    public void Text_typed_by_a_person_is_parsed_per_kind_and_rejected_when_out_of_range()
    {
        Assert.Equal(12, ModuleSettings.Parse(Definitions[0], "12").GetInt32());
        Assert.True(ModuleSettings.Parse(Definitions[1], "sì").GetBoolean());
        Assert.Equal(2, ModuleSettings.Parse(Definitions[2], "uno; due").GetArrayLength());
        Assert.Throws<FormatException>(() => ModuleSettings.Parse(Definitions[0], "99"));
        Assert.Throws<FormatException>(() => ModuleSettings.Parse(Definitions[1], "forse"));
    }

    [Fact]
    public void A_broken_config_file_falls_back_to_defaults_instead_of_crashing()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.Config.Path, "{ questo non è json");

        Assert.True(box.Config.GetModule("fake").Enabled);
    }
}

public class DiscoveryTests
{
    [Fact]
    public void Modules_are_found_by_file_name_without_the_core_naming_them()
    {
        var (modules, errors) = OptLifeRuntime.DiscoverModules(AppContext.BaseDirectory);

        Assert.Empty(errors);
        Assert.Contains(modules, m => m.Info.Id == "errors");
        Assert.Equal(modules.Count, modules.Select(m => m.Info.Id).Distinct().Count());
    }

    [Fact]
    public void A_folder_with_no_modules_is_a_valid_installation()
    {
        var empty = Directory.CreateTempSubdirectory("optlife-empty").FullName;
        var (modules, errors) = OptLifeRuntime.DiscoverModules(empty);
        Assert.Empty(modules);
        Assert.Empty(errors);
    }
}

public class SharedObjectsTests
{
    [Fact]
    public void Objects_made_with_explicit_rights_are_found_again_by_name()
    {
        var name = $@"Local\OptLife.Test.{Guid.NewGuid():N}";

        Assert.True(OptLife.Core.Hosting.SharedObjects.Mutex(name + ".M"));
        Assert.False(OptLife.Core.Hosting.SharedObjects.Mutex(name + ".M")); // Second time: it already exists.
        Assert.True(Mutex.TryOpenExisting(name + ".M", out var mutex));
        mutex!.Dispose();

        Assert.True(OptLife.Core.Hosting.SharedObjects.Event(name + ".E", manualReset: false));
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".E");
        Assert.True(OptLife.Core.Hosting.OptLifeRuntime.Signal(name + ".E"));
        Assert.True(signal.WaitOne(0));

        using var board = new OptLife.Core.Hosting.LiveBoard(name + ".Live");
        board.Publish("m", [new OptLife.Core.LiveValue("x.y", "X", "G", 42, "%")], DateTimeOffset.Now);
        Assert.Equal(42, Assert.Single(OptLife.Core.Hosting.LiveBoard.Read(DateTimeOffset.Now, name + ".Live")).Value);
    }
}

public class IgnoredFindingTests
{
    [Fact]
    public void An_ignored_problem_that_goes_quiet_and_comes_back_stays_ignored()
    {
        var box = new Sandbox();
        var now = DateTimeOffset.Now;
        var noise = new OptLife.Core.NewFinding("vss", OptLife.Core.Severity.Info, "Errore ricorrente di VSS", "…");
        box.Store.SyncFindings("errors", "recurring", [noise], now);
        var id = Assert.Single(box.Store.GetFindings()).Id;
        box.Store.SetFindingStatus(id, OptLife.Core.FindingStatus.Ignored);

        box.Store.SyncFindings("errors", "recurring", [], now.AddDays(8));      // A quiet week.
        box.Store.SyncFindings("errors", "recurring", [noise], now.AddDays(9)); // And back again.

        Assert.Empty(box.Store.GetFindings());
        Assert.Equal(OptLife.Core.FindingStatus.Ignored, box.Store.GetFindings(includeClosed: true).Single().Status);
    }
}

public class SolvedFindingTests
{
    private static OptLife.Core.NewFinding Gupdate(DateTimeOffset latest) =>
        new("service|gupdate", OptLife.Core.Severity.Low, "Il servizio Google Update dà errore di continuo", "…", LatestAt: latest);

    [Fact]
    public void A_problem_marked_solved_stays_solved_while_only_old_occurrences_are_in_the_window()
    {
        var box = new Sandbox();
        var now = new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.FromHours(1));
        var lastFailure = now.AddDays(-3); // Fixed on 17/11: the old failures are still inside the 7 days.
        box.Store.SyncFindings("errors", "recurring", [Gupdate(lastFailure)], now);
        var id = Assert.Single(box.Store.GetFindings()).Id;

        Assert.True(box.Store.SolveFinding(id, now));
        box.Store.SyncFindings("errors", "recurring", [Gupdate(lastFailure)], now.AddHours(1));
        Assert.Empty(box.Store.GetFindings());

        // It happens again after it was marked solved: it reopens, so it is never hidden for good.
        var appeared = box.Store.SyncFindings("errors", "recurring", [Gupdate(now.AddHours(2))], now.AddHours(2));
        Assert.Single(appeared);
        Assert.Single(box.Store.GetFindings());
    }

    [Fact]
    public void Only_a_problem_that_says_when_it_last_happened_can_be_marked_solved()
    {
        var box = new Sandbox();
        var now = DateTimeOffset.Now;
        box.Store.SyncFindings("monitor", "s", [new OptLife.Core.NewFinding("ram", OptLife.Core.Severity.Medium, "Memoria quasi piena", "…")], now);
        var finding = Assert.Single(box.Store.GetFindings());

        Assert.False(finding.CanBeSolved);
        Assert.False(box.Store.SolveFinding(finding.Id, now));
        Assert.Single(box.Store.GetFindings());
    }

    [Fact]
    public void The_panel_offers_solved_only_where_it_means_something()
    {
        var box = new Sandbox();
        var now = box.Clock.GetLocalNow();
        box.Store.SyncFindings("errors", "recurring", [Gupdate(now.AddDays(-3))], now);
        box.Store.SyncFindings("monitor", "s", [new OptLife.Core.NewFinding("ram", OptLife.Core.Severity.Medium, "Memoria quasi piena", "…")], now);
        var vm = new OptLife.Panel.ViewModels.PanelViewModel(box.Store, box.Config, box.Runner, [], () => false, () => false, box.Clock);
        vm.ShowCommand.Execute("Problems");

        var service = vm.Findings.Single(f => f.Title.Contains("Google"));
        Assert.True(service.CanSolve);
        Assert.False(vm.Findings.Single(f => f.Title.Contains("Memoria")).CanSolve);

        vm.SolveCommand.Execute(service);
        Assert.Equal("Memoria quasi piena", Assert.Single(vm.Findings).Title);
    }
}
