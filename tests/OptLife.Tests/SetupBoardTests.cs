using OptLife.Core;
using OptLife.Modules.Setup;

namespace OptLife.Tests;

/// <summary>The Setup page and the areas problems are grouped under.</summary>
public class SetupBoardTests
{
    private readonly Sandbox _box = new();
    private readonly FakePcProbe _pc = new();
    private readonly SetupModule _module;

    public SetupBoardTests() => _module = new SetupModule(_pc);

    private async Task Check()
    {
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None),
            _box.Runner.ReadStatus(_module, _module.Tasks[0]).LastError);
        _box.Clock.Now += TimeSpan.FromMinutes(10);
    }

    private IReadOnlyList<CheckItem> Board()
    {
        var latest = _box.Store.QueryEvents(_box.Clock.Now.AddDays(-30), _box.Clock.Now.AddDays(1), kind: SetupModule.BoardKind).MaxBy(e => e.Time);
        Assert.NotNull(latest);
        return CheckItem.FromData(latest.Data);
    }

    [Fact]
    public async Task Each_setting_shows_how_it_stands_and_what_it_should_be()
    {
        _pc.Settings.AddRange(
        [
            new Reading("display.A.mode", "Schermo A", "2560×1440 a 60 Hz", Problem: "Lo schermo A va a 60 Hz ma può arrivare a 165 Hz",
                Severity: Severity.Medium, Good: false, Expected: "165 Hz, la massima"),
            new Reading("power.plan", "Piano energetico", "Bilanciato", Good: true, Expected: "Bilanciato o Prestazioni elevate"),
            new Reading("windows.mouse_accel", "Accelerazione del mouse", "accesa", Advice: "Opzioni puntatore.", Good: false, Expected: "spenta"),
            new Reading("windows.vrr", "Frequenza variabile di Windows", "predefinita"),
        ]);

        await Check();
        var board = Board();

        var screen = board.Single(i => i.Label == "Schermo A");
        Assert.Equal((CheckStatus.Wrong, "Schermi", "165 Hz, la massima"), (screen.Status, screen.Group, screen.Expected));
        Assert.Equal(CheckStatus.Ok, board.Single(i => i.Label == "Piano energetico").Status);
        Assert.Equal(CheckStatus.Watch, board.Single(i => i.Label == "Accelerazione del mouse").Status);
        Assert.Equal(CheckStatus.Info, board.Single(i => i.Label == "Frequenza variabile di Windows").Status);
        // The parts of the PC come last, as information.
        Assert.Equal(("Componenti e versioni", CheckStatus.Info), (board[^1].Group, board[^1].Status));
        Assert.Contains(board, i => i.Label == "Scheda video" && i.Value == "RTX 3080");
    }

    [Fact]
    public async Task A_value_the_person_pinned_is_right_when_it_matches_and_wrong_when_it_does_not()
    {
        _box.Config.UpdateModule("setup", c => c.Settings["expected"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "gpu.power_limit=250 W" }));
        _pc.Settings.Add(new Reading("gpu.power_limit", "Limite di potenza della scheda video", "250 W"));
        await Check();
        var pinned = Board().Single(i => i.Label.StartsWith("Limite"));
        Assert.Equal((CheckStatus.Ok, "250 W", "Scheda video"), (pinned.Status, pinned.Expected, pinned.Group));

        _pc.Settings[0] = _pc.Settings[0] with { Value = "290 W" };
        await Check();
        await Check(); // A new value counts once seen twice.
        Assert.Equal(CheckStatus.Wrong, Board().Single(i => i.Label.StartsWith("Limite")).Status);
    }

    [Fact]
    public async Task The_page_gets_a_new_line_only_when_something_on_it_changed()
    {
        _pc.Settings.Add(new Reading("power.plan", "Piano energetico", "Bilanciato", Good: true));
        await Check();
        await Check();
        await Check();

        Assert.Single(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now, kind: SetupModule.BoardKind));
    }

    [Fact]
    public void A_checklist_reads_back_in_the_order_it_was_written()
    {
        var items = Enumerable.Range(0, 12).Select(i => new CheckItem("G", $"voce {i}", "x", CheckStatus.Ok)).ToList();

        var back = CheckItem.FromData(CheckItem.ToData(items));

        Assert.Equal(items, back);
    }

    [Fact]
    public async Task The_panel_shows_the_setup_page_as_a_coloured_checklist()
    {
        _pc.Settings.AddRange(
        [
            new Reading("power.plan", "Piano energetico", "Risparmio di energia", Problem: "Il piano energetico è su Risparmio di energia",
                Severity: Severity.Medium, Good: false, Expected: "Bilanciato o Prestazioni elevate"),
            new Reading("windows.gamemode", "Modalità gioco di Windows", "accesa", Good: true, Expected: "accesa"),
        ]);
        await Check();

        var vm = new OptLife.Panel.ViewModels.PanelViewModel(_box.Store, _box.Config, _box.Runner, [_module], () => false, () => false, _box.Clock);
        var tab = Assert.Single(vm.Views);
        vm.ShowViewCommand.Execute(tab);

        Assert.Equal("SETUP", tab.Title);
        Assert.True(tab.IsChecklist);
        Assert.StartsWith("1 a posto · 1 da sistemare", tab.CheckSummary);
        Assert.Equal("high", tab.CheckLevel);
        var energy = tab.CheckGroups.Single(g => g.Name == "Energia").Rows.Single();
        Assert.Equal(("high", "DA SISTEMARE", "dovrebbe: Bilanciato o Prestazioni elevate"), (energy.Level, energy.StatusLabel, energy.Expected));
        var game = tab.CheckGroups.Single(g => g.Name == "Windows").Rows.Single();
        Assert.Equal(("good", ""), (game.Level, game.Expected)); // Already right: nothing to compare with.

        // The problem it raised is listed under its own area.
        vm.ShowCommand.Execute("Problems");
        Assert.Equal(FindingArea.Setup, Assert.Single(vm.FindingSections).Name);
    }

    [Fact]
    public void Problems_are_grouped_by_area_with_performance_first()
    {
        var now = _box.Clock.Now;
        _box.Store.SyncFindings("errors", "s", [new NewFinding("crash", Severity.Medium, "Un programma va in crash", "…")], now, FindingArea.Software);
        _box.Store.SyncFindings("monitor", "s", [new NewFinding("ram", Severity.Medium, "Memoria quasi piena", "…")], now, FindingArea.Hardware);
        _box.Store.SyncFindings("old", "s", [new NewFinding("x", Severity.Low, "Problema di prima delle aree", "…")], now);

        var vm = new OptLife.Panel.ViewModels.PanelViewModel(_box.Store, _box.Config, _box.Runner, [], () => false, () => false, _box.Clock);
        vm.ShowCommand.Execute("Problems");

        Assert.Equal([FindingArea.Hardware, FindingArea.Software, "old"], vm.FindingSections.Select(s => s.Name));
        Assert.Equal("HARDWARE E PRESTAZIONI  1", vm.FindingSections[0].Header);
    }

    [Fact]
    public void A_problem_falls_under_its_own_area_or_else_its_modules()
    {
        var now = DateTimeOffset.Now;
        _box.Store.SyncFindings("cleanup", "s",
            [new NewFinding("a", Severity.Low, "Desktop in disordine", "…"),
             new NewFinding("b", Severity.Low, "Nuovo programma all'avvio", "…", Area: FindingArea.Software)],
            now, FindingArea.Tidy);

        var stored = _box.Store.GetFindings().ToDictionary(f => f.Key, f => f.Area);
        Assert.Equal(FindingArea.Tidy, stored["a"]);
        Assert.Equal(FindingArea.Software, stored["b"]);
    }
}
