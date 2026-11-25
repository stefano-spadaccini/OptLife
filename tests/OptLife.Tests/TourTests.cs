using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Tools;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

/// <summary>A module that takes part in the guided tour: a page with words, two questions, one suggestion only the PC can tell.</summary>
internal sealed class GuidedModule : IModule
{
    public ModuleInfo Info { get; } = new("guided", "Guidato", "Un modulo nella visita.");

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Choice("mode", "Modo", "", "avvisa", "da solo").Guided("Come vuoi fare?"),
        SettingDefinition.Text("speaker", "Uscita", "", "").Guided("Quale uscita?"),
        SettingDefinition.List("other", "Altro", "").Guided("Mai chiesta: è un elenco."),
    ];

    public IReadOnlyList<ModuleView> Views { get; } =
    [
        new ModuleView("page", "Pagina guidata", "kind", [], Menu: ViewMenu.Status, Layout: ViewLayout.Checklist, Guide: "A cosa serve questa pagina."),
        new ModuleView("other", "Pagina senza visita", "kind", [], Menu: ViewMenu.Status, Layout: ViewLayout.Checklist),
    ];
}

public sealed class TourTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private PanelViewModel Panel() =>
        new(_box.Store, _box.Config, _box.Runner, [new GuidedModule(), new ToolsModule()], () => false, () => false, _box.Clock, () => []);

    [Fact]
    public void The_first_start_opens_the_tour_with_the_suggested_answers_already_in_place_and_finish_saves_them()
    {
        _box.Store.SetState("guided", "suggest.speaker", "Cuffie (USB)");
        var vm = Panel();
        Assert.True(vm.ShowWelcome);
        Assert.Equal(TourKind.Look, vm.TourStepNow!.Kind);

        var page = Assert.Single(vm.TourSteps, s => s.Title == "Pagina guidata");
        Assert.DoesNotContain(vm.TourSteps, s => s.Title == "Pagina senza visita");
        Assert.Equal("PageArea", page.Highlight);
        Assert.Equal(["mode", "speaker"], page.Fields.Select(f => f.Key)); // Not the list.
        Assert.Equal("avvisa", page.Fields[0].Value);         // The default.
        Assert.Equal("Cuffie (USB)", page.Fields[1].Value);   // What the module suggests.

        // Walking to the page opens it behind the dimming.
        while (vm.TourStepNow != page) vm.TourNextCommand.Execute(null);
        Assert.Equal(PanelPage.View, vm.Page);
        page.Fields[0].Value = "da solo";

        // Summary: "Change" goes back to the step; then finish.
        while (!vm.TourIsLast) vm.TourNextCommand.Execute(null);
        Assert.Equal(TourKind.Summary, vm.TourStepNow!.Kind);
        Assert.Contains(vm.TourAnswered, s => s == page);
        vm.TourChangeCommand.Execute(page);
        Assert.Equal(page, vm.TourStepNow);
        while (!vm.TourIsLast) vm.TourNextCommand.Execute(null);
        vm.TourNextCommand.Execute(null); // Finish.

        Assert.False(vm.ShowWelcome);
        var saved = _box.Config.GetModule("guided").Settings;
        Assert.Equal("da solo", saved["mode"].GetString());
        Assert.Equal("Cuffie (USB)", saved["speaker"].GetString());
        Assert.False(Panel().ShowWelcome); // Once.
    }

    [Fact]
    public void Skipping_saves_nothing_a_choice_made_before_is_kept_and_the_tour_can_be_taken_again()
    {
        _box.Config.UpdateModule("guided", m => m.Settings["mode"] = JsonSerializer.SerializeToElement("da solo"));
        var vm = Panel();
        vm.TourSkipCommand.Execute(null);
        Assert.False(vm.ShowWelcome);
        Assert.False(_box.Config.GetModule("guided").Settings.ContainsKey("speaker"));

        vm.StartTourCommand.Execute(null);
        Assert.True(vm.ShowWelcome);
        Assert.Equal("da solo", vm.TourSteps.Single(s => s.Title == "Pagina guidata").Fields[0].Value); // The person's own choice, not the default.
    }

    [Fact]
    public void A_switched_off_module_is_not_in_the_tour_and_a_language_change_resumes_at_the_same_step()
    {
        _box.Config.UpdateModule("guided", m => m.Enabled = false);
        var vm = Panel();
        Assert.DoesNotContain(vm.TourSteps, s => s.Title == "Pagina guidata");
        vm.TourNextCommand.Execute(null);
        vm.TourNextCommand.Execute(null);
        Assert.Equal(2, Panel().TourIndex); // The panel reopens in the new language where it was.
    }
}

public sealed class PickTests
{
    private static readonly SettingOption[] Devices =
    [
        new("", "Nessun controllo"),
        new("Altoparlanti (USB Audio DAC)", "Altoparlanti (USB Audio DAC)"),
        new("Altoparlanti (Realtek(R) Audio)", "Altoparlanti (Realtek(R) Audio)"),
    ];

    [Fact]
    public void A_name_written_by_hand_shows_as_the_device_it_names_and_stays_stored_as_it_was()
    {
        var into = new System.Collections.ObjectModel.ObservableCollection<SettingOption>();
        Picks.Fill(into, Devices, "DAC");
        Assert.Equal(3, into.Count);
        Assert.Equal(new SettingOption("DAC", "Altoparlanti (USB Audio DAC)"), into[1]);

        Picks.Fill(into, Devices, "Cuffie vecchie"); // Matches nothing: kept, and said to be written by hand.
        Assert.Equal(4, into.Count);
        Assert.Contains("Cuffie vecchie", into[3].Label);

        Picks.Fill(into, Devices, @"F:\Giochi"); // A folder picked with Browse: shown as it is.
        Assert.Equal(@"F:\Giochi", into[3].Label);
    }

    [Fact]
    public void A_ticked_list_keeps_what_matches_nothing_and_saves_the_full_names_of_the_ticked_ones()
    {
        var def = SettingDefinition.List("allowed", "Ammessi", "").PickFrom();
        var settings = new FakeSettings(new Dictionary<string, object> { ["allowed"] = new[] { "DAC", "Cuffie BT" } });
        var item = new SettingItem(def, settings, _ => [.. Devices.Skip(1)]);
        Assert.True(item.IsPickList);
        Assert.False(item.IsListTable);
        Assert.Equal([true, false], item.PickChecks.Select(c => c.IsChecked));
        Assert.Equal("Cuffie BT", item.ExtraText);
        Assert.False(item.IsDirty);

        item.PickChecks[1].IsChecked = true;
        Assert.True(item.IsDirty);
        Assert.Equal(["Altoparlanti (USB Audio DAC)", "Altoparlanti (Realtek(R) Audio)", "Cuffie BT"],
            item.TextValue.Split(Environment.NewLine));
    }

    private sealed class FakeSettings(Dictionary<string, object> values) : ISettings
    {
        public bool GetBool(string key) => values.TryGetValue(key, out var v) && v is true;
        public int GetInt(string key) => values.TryGetValue(key, out var v) ? (int)v : 0;
        public string GetText(string key) => values.TryGetValue(key, out var v) ? (string)v : "";
        public IReadOnlyList<string> GetList(string key) => values.TryGetValue(key, out var v) ? (string[])v : [];
        public string GetChoice(string key) => GetText(key);
    }
}

public sealed class TourSummaryTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void The_summary_says_every_answer_in_full_one_line_each()
    {
        var vm = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [new GuidedModule()], () => false, () => false, _box.Clock, () => []);
        var page = vm.TourSteps.Single(s => s.Title == "Pagina guidata");
        Assert.Equal(["Modo: avvisa", "Uscita: (nessuno)"], page.Answers.Split(Environment.NewLine));
        var notices = vm.TourSteps.Single(s => s.Kind == TourKind.Notices);
        Assert.All(notices.Answers.Split(Environment.NewLine), line => Assert.Matches(@"^.+: (sì|no)$", line));
        Assert.StartsWith("Lingua: ", vm.TourLookSummary);
    }
}

public sealed class SimpleViewTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private ModuleItem Overlay()
    {
        var module = new OptLife.Modules.Overlay.OverlayModule();
        IReadOnlyList<SettingOption> Options(string key) => key switch
        {
            "items" => [new("game.fps", "FPS"), new("game.ping_max", "Ping"), new("gpu.temp", "GPU")],
            "items.basic" => OptLife.Modules.Overlay.OverlayModule.Common,
            "items.presets" => OptLife.Modules.Overlay.OverlayModule.Presets,
            _ => [],
        };
        return new ModuleItem(module, _box.Config, _box.Runner, _box.Clock.Now, Options);
    }

    [Fact]
    public void A_card_opened_earlier_never_writes_back_a_setting_saved_elsewhere()
    {
        // Two cards of the same module: the overlay editor and Settings → Overlay, both open.
        var editor = Overlay();
        var settings = Overlay();
        var items = editor.Settings.Single(s => s.Key == "items");
        items.PresetName = "Mine";
        items.SavePresetCommand.Execute(null);
        editor.SaveCommand.Execute(null);
        Assert.Single(_box.Config.GetModule("overlay").Settings["myPresets"].EnumerateArray());

        // The other card, still holding the old (empty) list, saves something else: the preset stays.
        settings.Settings.Single(s => s.Key == "scale").TextValue = "120";
        settings.SaveCommand.Execute(null);
        var saved = _box.Config.GetModule("overlay").Settings;
        Assert.Single(saved["myPresets"].EnumerateArray());
        Assert.Equal(120, saved["scale"].GetInt32());
        // And a copy of the file as it was is kept before each save.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(_box.Config.Path)!, "config-copies")));
    }

    [Fact]
    public void The_simple_view_ticks_common_values_applies_ready_sets_and_names_unknown_ones()
    {
        var item = Overlay();
        var items = item.Settings.Single(s => s.Key == "items");
        Assert.True(item.HasAdvanced);
        Assert.True(items.ShowBasic);
        Assert.False(items.ShowTable);
        Assert.False(item.Settings.Single(s => s.Key == "font").Shown); // Advanced only.

        var competitive = items.Presets.First();
        items.ApplyPresetCommand.Execute(competitive);
        Assert.Equal(["game.fps", "game.ping_max"], items.Rows.Select(r => r.Cells[0].Value));
        var chips = items.BasicGroups.SelectMany(g => g.Chips).ToList();
        Assert.True(chips.Single(c => c.Key == "game.fps").IsChecked);
        Assert.False(chips.Single(c => c.Key == "gpu.temp").IsChecked);

        chips.Single(c => c.Key == "gpu.temp").IsChecked = true; // Its ready line goes at the end, with its limit.
        Assert.Equal("gpu.temp | GPU | >85", items.Rows.Last().Cells.Select(c => c.Value).Aggregate((a, b) => $"{a} | {b}").TrimEnd(' ', '|'));
        items.MoveRowTo(items.Rows.Last(), 0);
        Assert.Equal("gpu.temp", items.Rows[0].Cells[0].Value);

        // The chosen values saved as a preset of one's own: a row of "My presets", among the ready sets too; a copy for one more game.
        var mine = item.Settings.Single(s => s.Key == "myPresets");
        Assert.True(mine.Shown);
        Assert.True(items.CanSavePreset);
        items.PresetName = "Mine";
        items.SavePresetCommand.Execute(null);
        Assert.Equal("Mine | gpu.temp,game.fps,game.ping_max", mine.TextValue);
        Assert.Contains(items.Presets, p => p.Label == "Mine");
        mine.DuplicateRow(mine.Rows[0]);
        Assert.Equal(2, mine.Rows.Count);

        items.TextValue += Environment.NewLine + "game.mystery | ??? | | ";
        Assert.Contains("non conosce", items.Rows.Last().Title);

        item.ShowAdvanced = true;
        Assert.True(items.ShowTable);
        Assert.True(item.Settings.Single(s => s.Key == "font").Shown);
        Assert.True(Overlay().ShowAdvanced); // Remembered.
    }
}

/// <summary>Two pages in "Order and space": the entry opens on an overview, and the tour has one step for it.</summary>
internal sealed class OrderPagesModule : IModule
{
    public ModuleInfo Info { get; } = new("orderpages", "Ordine", "Due pagine in Ordine e spazio.");

    public IReadOnlyList<SettingDefinition> Settings { get; } = [SettingDefinition.Bool("tidy", "Ordina", "", true).Guided("Ordinare?")];

    public IReadOnlyList<ModuleView> Views { get; } =
    [
        new ModuleView("first", "Prima", "k1", [], Menu: ViewMenu.Order, Layout: ViewLayout.Checklist, Guide: "La prima pagina."),
        new ModuleView("second", "Seconda", "k2", [], Menu: ViewMenu.Order, Layout: ViewLayout.Checklist, Description: "La seconda pagina. Altro."),
    ];
}

public sealed class IntroTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void An_entry_with_several_pages_opens_on_its_overview_and_the_tour_takes_it_in_one_step()
    {
        var vm = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [new OrderPagesModule()], () => false, () => false, _box.Clock, () => []);
        var order = vm.NavGroups.Single(g => g.Label == ViewMenu.Order);
        Assert.Equal(["Panoramica", "Prima", "Seconda"], order.Items.Select(i => i.Label));

        vm.TourSkipCommand.Execute(null);
        order.ActivateCommand.Execute(null);
        Assert.Equal(PanelPage.Intro, vm.Page);
        Assert.Equal(ViewMenu.Intro(ViewMenu.Order), vm.PageHelp);
        Assert.Equal(["La prima pagina.", "La seconda pagina."], vm.IntroCards.Select(c => c.Guide));
        vm.IntroCards[1].OpenCommand.Execute(null);
        Assert.Equal(PanelPage.View, vm.Page);

        vm.StartTourCommand.Execute(null);
        var step = Assert.Single(vm.TourSteps, s => s.Title == ViewMenu.Order);
        Assert.Equal(["tidy"], step.Fields.Select(f => f.Key));
        Assert.DoesNotContain(vm.TourSteps, s => s.Title is "Prima" or "Seconda");
    }
}
