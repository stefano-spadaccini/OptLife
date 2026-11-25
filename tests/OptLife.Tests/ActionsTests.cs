using System.Text.Json;
using Microsoft.Data.Sqlite;
using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Core.Storage;
using OptLife.Modules.Setup;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

/// <summary>Problems that carry their own fix: stored, run with the person's permission, and written down.</summary>
public class FixTests
{
    private const string Slow = "Lo schermo A va a 60 Hz ma può arrivare a 165 Hz";
    private static readonly FindingFix ToMax = new(SetupModule.SetRefresh, "Porta a 165 Hz", "A");

    private readonly Sandbox _box = new();
    private readonly FakePcProbe _pc = new();
    private readonly SetupModule _module;

    public FixTests()
    {
        _module = new SetupModule(_pc);
        _pc.Set("display.A.mode", "2560×1440 a 165 Hz");
        // Applying the fix really changes what the next reading finds, like on a real PC.
        _pc.OnApply = (action, _) =>
        {
            if (action == SetupModule.SetRefresh) _pc.Set("display.A.mode", "2560×1440 a 165 Hz");
            return new ActionResult(true, "Schermo A portato da 60 a 165 Hz.", "Schermo avanzato → 60 Hz");
        };
    }

    private void BreakIt() => _pc.Set("display.A.mode", "2560×1440 a 60 Hz", problem: Slow, fix: ToMax);

    private async Task Check(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None),
                _box.Runner.ReadStatus(_module, _module.Tasks[0]).LastError);
            _box.Clock.Now += TimeSpan.FromMinutes(10);
        }
    }

    private void Allow(Permission permission) =>
        _box.Config.UpdateModule("setup", c => c.Permissions[SetupModule.SetRefresh] = permission);

    private ModuleAction Action(string id) => _module.Actions.Single(a => a.Id == id);

    [Fact]
    public async Task A_problem_remembers_the_action_that_would_fix_it()
    {
        BreakIt();
        await Check();

        var finding = Assert.Single(_box.Store.GetFindings());
        Assert.Equal(ToMax, finding.Fix);
        Assert.Empty(_pc.Applied); // By default it asks first: nothing happens on its own.
    }

    [Fact]
    public async Task Asked_to_it_fixes_writes_it_down_and_the_problem_goes_at_the_very_next_check()
    {
        BreakIt();
        await Check();

        var result = await _box.Runner.RunActionAsync(_module, Action(SetupModule.SetRefresh), "pannello", confirmed: true, "A", CancellationToken.None);
        await Check(); // One check, not two: after its own action the module believes what it finds.

        Assert.True(result.Ok);
        Assert.Equal([(SetupModule.SetRefresh, "A")], _pc.Applied);
        Assert.Empty(_box.Store.GetFindings());
        var logged = Assert.Single(_box.Store.GetActions());
        Assert.Equal(("setup", SetupModule.SetRefresh, "pannello", true), (logged.Module, logged.Action, logged.Actor, logged.Ok));
        Assert.Equal("Schermo avanzato → 60 Hz", logged.Undo);
    }

    [Fact]
    public async Task Without_a_yes_an_action_that_asks_first_does_nothing_and_says_so()
    {
        var result = await _box.Runner.RunActionAsync(_module, Action(SetupModule.SetRefresh), "cli", confirmed: false, "A", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("conferma", result.Detail);
        Assert.Empty(_pc.Applied);
        Assert.False(Assert.Single(_box.Store.GetActions()).Ok); // The refusal is written down too.
    }

    [Fact]
    public async Task Set_to_never_it_is_refused_even_with_a_yes()
    {
        Allow(Permission.Never);

        var result = await _box.Runner.RunActionAsync(_module, Action(SetupModule.SetRefresh), "pannello", confirmed: true, "A", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(_pc.Applied);
    }

    [Fact]
    public async Task Set_to_run_by_itself_it_fixes_the_problem_as_soon_as_it_appears()
    {
        Allow(Permission.Auto);
        await Check();
        BreakIt();

        await Check(2); // Seen twice, so it is real: fixed in the same round.
        await Check();

        Assert.Equal([(SetupModule.SetRefresh, "A")], _pc.Applied);
        Assert.Equal(ModuleRunner.AutoActor, Assert.Single(_box.Store.GetActions()).Actor);
        Assert.Empty(_box.Store.GetFindings());
    }

    [Fact]
    public async Task A_fix_that_does_not_hold_is_not_repeated_in_circles()
    {
        Allow(Permission.Auto);
        _pc.OnApply = (_, _) => new ActionResult(true, "Fatto (ma non tiene)."); // The screen stays at 60.
        BreakIt();

        await Check(4); // 40 minutes with the problem still there.
        Assert.Single(_pc.Applied);

        // Later the same problem goes away and comes back: by then an hour has passed, so it may try again.
        _pc.Set("display.A.mode", "2560×1440 a 165 Hz");
        await Check(4);
        BreakIt();
        await Check(2);
        Assert.Equal(2, _pc.Applied.Count);
    }

    [Fact]
    public async Task A_failed_action_leaves_the_problem_open()
    {
        _pc.OnApply = (_, _) => new ActionResult(false, "Windows ha rifiutato il cambio di frequenza (codice -2).");
        BreakIt();
        await Check();

        var result = await _box.Runner.RunActionAsync(_module, Action(SetupModule.SetRefresh), "pannello", confirmed: true, "A", CancellationToken.None);
        await Check();

        Assert.False(result.Ok);
        Assert.Single(_box.Store.GetFindings());
    }

    // ── The panel ───────────────────────────────────────────────────────────

    private PanelViewModel Panel() =>
        new(_box.Store, _box.Config, _box.Runner, [_module], () => false, () => false, _box.Clock) { AgentGrace = TimeSpan.Zero };

    [Fact]
    public async Task The_panel_puts_a_button_on_the_problem_and_pressing_it_settles_it()
    {
        BreakIt();
        await Check();
        var vm = Panel();
        var item = Assert.Single(vm.Findings);
        Assert.True(item.HasFix);
        Assert.Equal("PORTA A 165 HZ", item.FixLabel);

        await vm.FixCommand.ExecuteAsync(item);
        await vm.Rechecking; // The look that follows runs behind.

        Assert.Equal("Fatto. Schermo A portato da 60 a 165 Hz.", vm.Notice);
        Assert.False(vm.NoticeIsError);
        Assert.Empty(vm.Findings);
        Assert.Equal("Tutto a posto", vm.Headline);

        vm.ShowCommand.Execute("Actions");
        var logged = Assert.Single(vm.ActionLog);
        Assert.Equal("Porta lo schermo alla frequenza massima", logged.Title);
        Assert.Equal("Impostazioni del PC · da te", logged.Source);
        Assert.Equal("FATTO", logged.Outcome);
        Assert.True(logged.HasUndo);
        Assert.Equal("", vm.ActionLogEmptyText);
    }

    [Fact]
    public async Task When_the_fix_fails_the_panel_says_why_and_keeps_the_problem()
    {
        _pc.OnApply = (_, _) => new ActionResult(false, "Windows ha rifiutato il cambio di frequenza (codice -2).");
        BreakIt();
        await Check();
        var vm = Panel();

        await vm.FixCommand.ExecuteAsync(vm.Findings[0]);
        await vm.Rechecking;

        Assert.Equal("Non riuscito. Windows ha rifiutato il cambio di frequenza (codice -2).", vm.Notice);
        Assert.True(vm.NoticeIsError);
        Assert.Single(vm.Findings);
    }

    /// <summary>25 Nov 2026: "Sort them" pressed three times answered "ok" each time and moved nothing; the problem stayed open.</summary>
    [Fact]
    public async Task An_action_that_stops_at_a_choice_says_so_on_the_card_with_the_buttons_and_one_of_them_settles_it()
    {
        var calls = 0;
        _pc.OnApply = (action, argument) =>
        {
            if (++calls == 1) return ActionResult.Choose("C'è già un'impostazione uguale.", [new FindingFix(SetupModule.SetRefresh, "Usa la nuova", "A!")]);
            _pc.Set("display.A.mode", "2560×1440 a 165 Hz");
            return new ActionResult(true, $"Fatto con {argument}.");
        };
        BreakIt();
        await Check();
        var vm = Panel();

        await vm.FixCommand.ExecuteAsync(vm.Findings.Single());
        await vm.Rechecking;
        var card = vm.Findings.Single(); // Still open: nothing changed.
        Assert.True(card.HasFeedback);
        Assert.Equal("medium", card.Feedback!.Level);
        Assert.StartsWith("Serve una tua scelta.", card.Feedback.Text);
        var way = Assert.Single(card.Feedback.Needs);
        Assert.Equal("USA LA NUOVA", way.Label);
        var logged = _box.Store.GetActions().First();
        Assert.Equal(ActionOutcome.Choice, logged.Outcome);
        Assert.False(logged.Ok); // Never written down as done.

        await vm.NeedCommand.ExecuteAsync(way);
        await vm.Rechecking;
        Assert.Equal([(SetupModule.SetRefresh, "A"), (SetupModule.SetRefresh, "A!")], _pc.Applied);
        Assert.Empty(vm.Findings); // Looked at again at once, not at the agent's round.
    }

    [Fact]
    public async Task Nothing_left_to_do_is_said_plainly_and_the_problem_closes_at_once_if_it_is_gone()
    {
        _pc.OnApply = (_, _) =>
        {
            _pc.Set("display.A.mode", "2560×1440 a 165 Hz"); // Put right by hand meanwhile.
            return ActionResult.Nothing("lo schermo è già al massimo.");
        };
        BreakIt();
        await Check();
        var vm = Panel();

        var item = vm.Findings.Single();
        var press = vm.FixCommand.ExecuteAsync(item);
        await press;
        Assert.StartsWith("Niente da fare: lo schermo è già al massimo.", vm.Notice);
        Assert.False(vm.NoticeIsError);
        await vm.Rechecking;
        Assert.Empty(vm.Findings);
        Assert.Equal(ActionOutcome.Nothing, _box.Store.GetActions().First().Outcome);

        vm.ShowCommand.Execute("Actions");
        Assert.Equal("NIENTE DA FARE", vm.ActionLog.First().Outcome);
    }

    [Fact]
    public void Outcomes_are_four_and_never_confused()
    {
        Assert.Equal(ActionOutcome.Done, new ActionResult(true, "x").Outcome);
        Assert.Equal(ActionOutcome.Failed, new ActionResult(false, "x").Outcome);
        Assert.Equal(ActionOutcome.Nothing, ActionResult.Nothing("x").Outcome);
        Assert.True(ActionResult.Nothing("x").Ok);
        var choice = ActionResult.Choose("x", [new FindingFix("a", "A")]);
        Assert.Equal(ActionOutcome.Choice, choice.Outcome);
        Assert.False(choice.Ok);
        Assert.Equal(ActionOutcome.Choice, (choice with { Undo = "u" }).Outcome);
    }

    [Fact]
    public async Task An_action_set_to_never_has_no_button()
    {
        Allow(Permission.Never);
        BreakIt();
        await Check();

        Assert.False(Assert.Single(Panel().Findings).HasFix);
    }

    [Fact]
    public void Each_action_of_a_module_has_its_permission_and_changing_it_is_saved()
    {
        var vm = Panel();
        var actions = Assert.Single(vm.Modules).Actions;
        Assert.Equal(5, actions.Count); // Refresh rate, power plan, game mode, equalizer, DNS.
        var refresh = actions.Single(a => a.Name == "Porta lo schermo alla frequenza massima");
        Assert.Equal("Chiedimi prima", refresh.Allowed);
        Assert.Equal(["Fallo da solo", "Chiedimi prima", "Mai"], refresh.Options);

        refresh.Allowed = "Fallo da solo";
        Assert.Equal(Permission.Auto, _box.Config.GetModule("setup").Permissions[SetupModule.SetRefresh]);

        refresh.Allowed = null; // The list control dropping its selection for a moment changes nothing.
        Assert.Equal(Permission.Auto, _box.Config.GetModule("setup").Permissions[SetupModule.SetRefresh]);
        Assert.Equal("Fallo da solo", Assert.Single(Panel().Modules).Actions.Single(a => a.Name == refresh.Name).Allowed);
    }

    [Fact]
    public void With_nothing_done_yet_the_actions_page_explains_what_it_is_for()
    {
        var vm = Panel();
        vm.ShowCommand.Execute("Actions");

        Assert.Empty(vm.ActionLog);
        Assert.Contains("Nessuna azione eseguita finora", vm.ActionLogEmptyText);
    }
}

public class FindingFixStorageTests
{
    [Fact]
    public void A_history_created_before_fixes_existed_gains_the_column_and_keeps_its_problems()
    {
        var dir = Path.Combine(Path.GetTempPath(), "optlife-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "old.db");
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE findings(
                        id INTEGER PRIMARY KEY, module TEXT NOT NULL, scope TEXT NOT NULL, key TEXT NOT NULL,
                        severity INTEGER NOT NULL, title TEXT NOT NULL, detail TEXT NOT NULL, advice TEXT,
                        status TEXT NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                        UNIQUE(module, scope, key));
                    INSERT INTO findings(module, scope, key, severity, title, detail, status, first_seen, last_seen)
                    VALUES('errors', 's', 'old', 1, 'Un problema di prima', 'd', 'Open', 1, 1);
                    """;
                cmd.ExecuteNonQuery();
            }

            var store = new Store(path, pooling: false);
            var again = new Store(path, pooling: false); // Opening it twice must not try to add the column twice.

            var old = Assert.Single(store.GetFindings());
            Assert.Equal("Un problema di prima", old.Title);
            Assert.Null(old.Fix);

            var appeared = again.SyncFindings("setup", "s", [new NewFinding("k", Severity.Low, "t", "d", Fix: new FindingFix("a", "Fallo", "x"))], DateTimeOffset.Now);
            Assert.Single(appeared);
            Assert.Equal(new FindingFix("a", "Fallo", "x"), store.GetFindings().Single(f => f.Key == "k").Fix);
            Assert.Empty(again.SyncFindings("setup", "s", [new NewFinding("k", Severity.Low, "t", "d")], DateTimeOffset.Now)); // Still open: not "appeared" again.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

public class AudioRulesTests
{
    [Fact]
    public void The_equalizer_left_behind_on_an_old_entry_of_the_same_device_is_a_problem_with_a_fix()
    {
        var readings = SetupRules.Audio("Altoparlanti (USB Audio DAC)", "Microfono (USB Microphone)", equalizerOnOutput: false, equalizerOnTwin: true).ToList();

        Assert.Equal(["audio.output", "audio.input", "audio.eq"], readings.Select(r => r.Key));
        var eq = readings[2];
        Assert.Equal("non attivo", eq.Value);
        Assert.Equal("L'equalizzatore non è più attivo su Altoparlanti (USB Audio DAC)", eq.Problem);
        Assert.Equal(SetupModule.OpenEqualizer, eq.Fix!.ActionId);
    }

    [Fact]
    public void An_output_that_never_had_the_equalizer_is_not_a_problem()
    {
        var eq = SetupRules.Audio("Altoparlanti (USB Microphone)", null, equalizerOnOutput: false, equalizerOnTwin: false).Last();

        Assert.Equal("non attivo", eq.Value);
        Assert.Null(eq.Problem);
        Assert.Null(eq.Fix);
    }

    [Fact]
    public void Without_equalizer_apo_installed_there_is_no_equalizer_entry_at_all()
    {
        var keys = SetupRules.Audio("Altoparlanti", "Microfono", equalizerOnOutput: null, equalizerOnTwin: false).Select(r => r.Key);

        Assert.Equal(["audio.output", "audio.input"], keys);
        Assert.Empty(SetupRules.Audio(null, null, null, false));
    }

    [Fact]
    public void Rule_problems_come_with_the_matching_fix()
    {
        var slow = SetupRules.Display(new DisplayInfo("GSM5B7F", "LG ULTRAGEAR", 2560, 1440, 60, 144, null)).First();
        Assert.Equal(new FindingFix(SetupModule.SetRefresh, "Porta a 144 Hz", "GSM5B7F"), slow.Fix);
        Assert.Null(SetupRules.Display(new DisplayInfo("GSM5B7F", "LG ULTRAGEAR", 2560, 1440, 144, 144, null)).First().Fix);

        Assert.Equal(SetupRules.Balanced, SetupRules.PowerPlan(SetupRules.PowerSaver, "Risparmio di energia").Fix!.Argument);
        Assert.Equal(SetupModule.EnableGameMode, SetupRules.GameMode(0).Fix!.ActionId);
        Assert.Null(SetupRules.GameMode(1).Fix);
    }
}
