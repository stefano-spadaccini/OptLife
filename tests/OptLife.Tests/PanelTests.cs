using OptLife.Core;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

public class PanelViewModelTests
{
    private static PanelViewModel Panel(Sandbox box, IModule[]? modules = null, bool agent = true, Func<bool>? runNow = null) =>
        new(box.Store, box.Config, box.Runner, modules ?? [new FakeModule()], () => agent, runNow ?? (() => agent), box.Clock)
        {
            AgentGrace = TimeSpan.Zero,
        };

    private static void Report(Sandbox box, params NewFinding[] findings) =>
        box.Store.SyncFindings("fake", "s", findings, box.Clock.Now);

    [Fact]
    public void With_nothing_open_the_panel_says_so()
    {
        using var box = new Sandbox();
        var vm = Panel(box);

        Assert.Equal("Tutto a posto", vm.Headline);
        Assert.Equal("ok", vm.StatusLevel);
        Assert.Equal("Nessun problema aperto.", vm.EmptyText);
        Assert.Empty(vm.Findings);
    }

    [Fact]
    public void The_overall_colour_follows_the_most_serious_open_problem()
    {
        using var box = new Sandbox();
        Report(box,
            new NewFinding("a", Severity.Low, "piccolo", "d"),
            new NewFinding("b", Severity.High, "grave", "d", "fai così"));
        var vm = Panel(box);

        Assert.Equal("2 problemi aperti", vm.Headline);
        Assert.Equal("high", vm.StatusLevel);
        Assert.Equal("grave", vm.Findings[0].Title); // most serious first
        Assert.Equal("IMPORTANTE", vm.Findings[0].SeverityLabel);
        Assert.True(vm.Findings[0].HasAdvice);
        Assert.False(vm.Findings[1].HasAdvice);
        Assert.Contains("Finto", vm.Findings[0].Footer); // the module's name, not its id
    }

    [Fact]
    public void Ignoring_moves_a_problem_out_of_open_and_it_can_be_brought_back()
    {
        using var box = new Sandbox();
        Report(box, new NewFinding("a", Severity.Medium, "rumore", "d"));
        var vm = Panel(box);

        vm.IgnoreCommand.Execute(vm.Findings[0]);

        Assert.Equal((0, 1), (vm.OpenCount, vm.IgnoredCount));
        Assert.Equal("Tutto a posto", vm.Headline);
        Assert.Empty(vm.Findings);

        vm.SetFilterCommand.Execute("Ignored");
        var ignored = Assert.Single(vm.Findings);
        Assert.True(ignored.IsIgnored);

        vm.ReopenCommand.Execute(ignored);
        Assert.Equal((1, 0), (vm.OpenCount, vm.IgnoredCount));
        Assert.Equal("Non stai ignorando nessun problema.", vm.EmptyText);
    }

    [Fact]
    public void Resolved_problems_stay_reachable()
    {
        using var box = new Sandbox();
        Report(box, new NewFinding("a", Severity.Medium, "passato", "d"));
        Report(box);
        var vm = Panel(box);

        Assert.Equal(1, vm.ResolvedCount);
        vm.SetFilterCommand.Execute("Resolved");
        Assert.Equal("passato", Assert.Single(vm.Findings).Title);
    }

    [Fact]
    public async Task Without_the_agent_the_panel_says_checks_do_not_run_and_checks_by_itself()
    {
        using var box = new Sandbox();
        var ran = 0;
        var task = FakeModule.Task(_ => ran++);
        var vm = Panel(box, [new FakeModule { Tasks = [task] }], agent: false);

        Assert.Contains("non è attivo", vm.Subline);
        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(1, ran);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task With_the_agent_running_the_panel_asks_it_instead_of_checking_twice()
    {
        using var box = new Sandbox();
        var ran = 0;
        var asked = 0;
        var task = FakeModule.Task(_ => ran++);
        var vm = Panel(box, [new FakeModule { Tasks = [task] }], runNow: () => { asked++; return true; });

        await vm.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal((1, 0), (asked, ran));
    }

    [Fact]
    public void The_panel_always_shows_everything_and_opens_on_the_overview()
    {
        using var box = new Sandbox();
        box.Config.Update(f => f.Ui["mode"] = "simple"); // A choice saved by an older version no longer matters.

        var vm = Panel(box);

        Assert.True(vm.ExpertMode);
        Assert.Equal(PanelPage.Monitor, vm.Page);
        Assert.False(vm.InProblems);

        vm.ShowProblemsCommand.Execute("Resolved");
        Assert.Equal((PanelPage.Problems, FindingFilter.Resolved), (vm.Page, vm.Filter));
        vm.ShowCommand.Execute("History");
        Assert.False(vm.InProblems); // The history is a page of its own now, under History in the side bar.
    }

    [Fact]
    public void History_can_be_narrowed_by_period_and_by_words_including_details()
    {
        using var box = new Sandbox();
        var now = box.Clock.Now;
        box.Store.InsertEvents("fake",
        [
            new NewEvent(now.AddHours(-2), "app_crash", Severity.Medium, "Deadlock.exe si è chiuso", Data: new Dictionary<string, string> { ["code"] = "c0000005" }),
            new NewEvent(now.AddDays(-3), "service_error", Severity.Low, "Il servizio X non è partito"),
            new NewEvent(now.AddDays(-20), "app_crash", Severity.Medium, "Vecchio.exe si è chiuso"),
        ]);
        var vm = Panel(box);
        vm.ShowCommand.Execute("History");

        Assert.Equal(2, vm.Events.Count);
        Assert.Equal("2 eventi", vm.HistorySummary);

        vm.SetHistoryDaysCommand.Execute("30");
        Assert.Equal(3, vm.Events.Count);

        vm.Search = "c0000005";
        Assert.Equal("Deadlock.exe si è chiuso", Assert.Single(vm.Events).Title);

        vm.Search = "crash vecchio";
        Assert.Equal("Vecchio.exe si è chiuso", Assert.Single(vm.Events).Title);

        vm.Search = "niente di simile";
        Assert.Equal("Nessun evento nel periodo.", vm.HistorySummary);
    }
}

public class ModuleItemTests
{
    private static readonly SettingDefinition[] FakeSettings =
    [
        SettingDefinition.Int("days", "Giorni", "Quanti giorni.", 7, 1, 30),
        SettingDefinition.Bool("on", "Acceso", "Sì o no.", true),
        SettingDefinition.List("names", "Nomi", "Elenco.", "a"),
    ];

    private static ModuleItem Item(Sandbox box, FakeModule? module = null) =>
        new(module ?? new FakeModule { Settings = FakeSettings }, box.Config, box.Runner, box.Clock.Now);

    private static SettingItem Field(ModuleItem item, string key) => item.Settings.Single(s => s.Key == key);

    [Fact]
    public void The_menu_is_built_from_what_the_module_declares()
    {
        using var box = new Sandbox();
        var item = Item(box);

        Assert.Equal(["days", "on", "names"], item.Settings.Select(s => s.Key));
        Assert.Equal("7", Field(item, "days").TextValue);
        Assert.True(Field(item, "days").IsLine);
        Assert.True(Field(item, "on").IsBool && Field(item, "on").BoolValue);
        Assert.True(Field(item, "names").IsList);
        Assert.Contains("Da 1 a 30", Field(item, "days").Description);
        Assert.False(item.IsDirty);
        Assert.False(item.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Saving_writes_every_changed_field_to_the_config_the_module_reads()
    {
        using var box = new Sandbox();
        var module = new FakeModule { Settings = FakeSettings };
        var item = Item(box, module);

        Field(item, "days").TextValue = "12";
        Field(item, "on").BoolValue = false;
        Field(item, "names").TextValue = "uno\r\ndue\n";
        Assert.True(item.SaveCommand.CanExecute(null));
        item.SaveCommand.Execute(null);

        var stored = box.Runner.SettingsOf(module);
        Assert.Equal(12, stored.GetInt("days"));
        Assert.False(stored.GetBool("on"));
        Assert.Equal(["uno", "due"], stored.GetList("names"));
        Assert.False(item.MessageIsError);
        Assert.False(item.IsDirty);
    }

    [Fact]
    public void One_invalid_field_blocks_the_whole_save_and_says_which()
    {
        using var box = new Sandbox();
        var module = new FakeModule { Settings = FakeSettings };
        var item = Item(box, module);

        Field(item, "on").BoolValue = false;
        Field(item, "days").TextValue = "999";
        item.SaveCommand.Execute(null);

        Assert.True(item.MessageIsError);
        Assert.Contains("Giorni", item.Message);
        Assert.True(box.Runner.SettingsOf(module).GetBool("on")); // nothing was written
        Assert.True(item.IsDirty);
    }

    [Fact]
    public void Defaults_can_be_restored()
    {
        using var box = new Sandbox();
        var module = new FakeModule { Settings = FakeSettings };
        var item = Item(box, module);
        Field(item, "days").TextValue = "20";
        item.SaveCommand.Execute(null);

        item.ResetDefaultsCommand.Execute(null);

        Assert.Equal(7, box.Runner.SettingsOf(module).GetInt("days"));
        Assert.Equal("7", Field(item, "days").TextValue);
    }

    [Fact]
    public async Task The_switch_turns_the_module_off_for_real_and_the_state_line_follows()
    {
        using var box = new Sandbox();
        var task = FakeModule.Task(_ => throw new InvalidOperationException("rotto"));
        var module = new FakeModule { Tasks = [task] };
        var item = Item(box, module);
        Assert.Contains("in attesa", item.StatusText);

        await box.Runner.RunTaskAsync(module, task, CancellationToken.None);
        item.RefreshStatus(box.Clock.Now);
        Assert.Equal("high", item.StatusLevel);
        Assert.Contains("rotto", item.StatusText);

        item.Enabled = false;
        item.RefreshStatus(box.Clock.Now);
        Assert.False(box.Runner.IsEnabled(module));
        Assert.StartsWith("Spento", item.StatusText);
    }
}
