using OptLife.Core.Appearance;
using OptLife.Core;
using OptLife.Modules.Tools;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

public class ThemeCatalogTests
{
    [Fact]
    public void Every_base_gives_every_token()
    {
        var tokens = ThemeCatalog.Resolve("black", ThemeCatalog.Neutral, null, null).Keys.ToHashSet();
        foreach (var b in ThemeCatalog.Bases)
            Assert.Equal(tokens, ThemeCatalog.Resolve(b.Id, "blue", null, null).Keys.ToHashSet());
        Assert.Contains("Accent", tokens);
        Assert.Contains("Text.OnAccent", tokens);
    }

    [Fact]
    public void Neutral_accent_is_the_text_colour_and_unknown_choices_fall_back()
    {
        Assert.Equal("#F4F4F4", ThemeCatalog.Resolve("black", ThemeCatalog.Neutral, null, null)["Accent"]);
        Assert.Equal("#141414", ThemeCatalog.Resolve("light", ThemeCatalog.Neutral, null, null)["Accent"]);
        Assert.Equal("#000000", ThemeCatalog.Resolve("nope", "nope", null, null)["Surface.Base"]);
    }

    [Fact]
    public void Custom_and_windows_colours_are_used_and_kept_readable()
    {
        Assert.Equal("#3B82F6", ThemeCatalog.Resolve("black", ThemeCatalog.Custom, "3b82f6", null)["Accent"]);
        Assert.Equal("#3B82F6", ThemeCatalog.Resolve("black", ThemeCatalog.Windows, null, "#3B82F6")["Accent"]);
        // A dark brown Windows accent on a black background is lightened until it can be seen.
        var accent = ThemeCatalog.Resolve("black", ThemeCatalog.Windows, null, "#2F2621")["Accent"];
        Assert.True(ThemeCatalog.Contrast(accent, "#000000") >= 3);
        // A bad custom colour falls back to neutral.
        Assert.Equal("#F4F4F4", ThemeCatalog.Resolve("black", ThemeCatalog.Custom, "zz", null)["Accent"]);
    }

    [Fact]
    public void Text_on_the_accent_is_black_or_white_by_brightness()
    {
        Assert.Equal("#000000", ThemeCatalog.Resolve("black", "green", null, null)["Text.OnAccent"]);
        Assert.Equal("#FFFFFF", ThemeCatalog.Resolve("light", ThemeCatalog.Custom, "#5B21B6", null)["Text.OnAccent"]);
    }

    public static TheoryData<string, bool, string> EveryTheme()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var light in new[] { false, true })
            foreach (var b in ThemeCatalog.BasesFor(light))
                foreach (var a in new[] { ThemeCatalog.Neutral, "blue", "violet", "green", "orange", "pink", "#FFFF00", "#101010", "#808080" })
                    data.Add(b.Id, light, a);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTheme))]
    public void Every_text_reads_on_every_surface_and_on_the_accent(string baseId, bool windowsLight, string accent)
    {
        var custom = accent.StartsWith('#');
        var c = ThemeCatalog.Resolve(baseId, custom ? ThemeCatalog.Custom : accent, custom ? accent : null, null, windowsLight);
        foreach (var text in ThemeCatalog.TextTokens)
            foreach (var surface in ThemeCatalog.Surfaces)
            {
                var under = ThemeCatalog.Flatten(c[surface], c["Surface.Solid"]);
                Assert.True(ThemeCatalog.Contrast(c[text], under) >= 4.5, $"{baseId}/{accent}: {text} {c[text]} on {surface} {under}");
            }
        foreach (var fill in new[] { "Accent.Fill", "Accent.Hover", "Accent.Pressed" })
            Assert.True(ThemeCatalog.Contrast(c["Text.OnAccent"], c[fill]) >= 4.5, $"{baseId}/{accent}: text on {fill}");
        // Lines and button edges need 3:1 against the window.
        Assert.True(ThemeCatalog.Contrast(c["Accent"], c["Surface.Solid"]) >= 3, $"{baseId}/{accent}: accent");
    }

    [Fact]
    public void The_windows_base_follows_light_and_dark_and_new_installs_start_on_it()
    {
        Assert.Equal(ThemeCatalog.System, ThemeCatalog.DefaultBase);
        Assert.Equal(ThemeCatalog.Windows, ThemeCatalog.DefaultAccent);
        Assert.True(ThemeCatalog.BaseOf(ThemeCatalog.System, windowsLight: true).IsLight);
        Assert.False(ThemeCatalog.BaseOf(ThemeCatalog.System, windowsLight: false).IsLight);
        Assert.True(ThemeCatalog.BaseOf(ThemeCatalog.System).Mica);
        // See-through layers are laid on the solid colour to judge them.
        Assert.Equal("#2B2B2B", ThemeCatalog.Flatten("#0DFFFFFF", "#202020"));
    }

    [Fact]
    public void High_contrast_uses_only_the_colours_windows_gives()
    {
        var c = ThemeCatalog.HighContrast("#000000", "#FFFFFF", "#1AEBFF", "#000000", "#3FF23F", "#FFFF00");
        Assert.Equal(ThemeCatalog.Resolve("black", ThemeCatalog.Neutral, null, null).Keys.ToHashSet(), c.Keys.ToHashSet());
        Assert.All(c.Values, v => Assert.Contains(v, new[] { "#000000", "#FFFFFF", "#1AEBFF", "#3FF23F", "#FFFF00" }));
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("12ab34", "#12AB34")]
    [InlineData("#12ab3", null)]
    [InlineData("hello!", null)]
    public void Colours_are_normalised(string input, string? expected) => Assert.Equal(expected, ThemeCatalog.Normalize(input));

    [Fact]
    public void Windows_keeps_its_accent_as_abgr() => Assert.Equal("#0078D4", ThemeCatalog.FromAbgr(unchecked((int)0xFFD47800)));
}

public sealed class NavigationAndSearchTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private PanelViewModel Panel() =>
        new(_box.Store, _box.Config, _box.Runner, [new ToolsModule()], () => false, () => false, _box.Clock, () => []);

    [Fact]
    public void Module_pages_go_under_their_menu_and_the_history_has_its_own()
    {
        var vm = Panel();
        var tools = vm.NavGroups.First();
        Assert.False(tools.HasMenu); // One page: a plain tab.
        var history = vm.NavGroups.Last();
        Assert.True(history.HasMenu);
        Assert.Equal(4, history.Items.Count); // Activity, Report, Actions taken, Recorded events.

        history.PickCommand.Execute(history.Items[0]);
        Assert.Equal(PanelPage.Activity, vm.Page);
        Assert.True(history.IsSelected);
        Assert.False(tools.IsSelected);

        tools.ActivateCommand.Execute(null);
        Assert.Equal(PanelPage.View, vm.Page);
        Assert.True(tools.IsSelected);
        Assert.False(history.IsSelected);
    }

    [Fact]
    public void The_theme_choice_is_saved_and_applied()
    {
        var vm = Panel();
        (string? Base, string? Accent, string? Custom) applied = default;
        vm.ApplyTheme = (b, a, c) => applied = (b, a, c);
        vm.SetThemeBaseCommand.Execute("light");
        vm.SetThemeAccentCommand.Execute(ThemeCatalog.Custom);
        vm.CustomAccent = "#12345";      // Half typed: not saved.
        Assert.NotEqual("", vm.CustomAccentError);
        vm.CustomAccent = "#123456";
        Assert.Equal(("light", ThemeCatalog.Custom, "#123456"), applied);
        Assert.Equal("light", Panel().ThemeBase);
        Assert.Equal("#123456", Panel().CustomAccent);
    }

    [Fact]
    public void The_side_bar_has_icons_shortcuts_a_pc_heading_and_reopens_the_last_page()
    {
        var vm = Panel();
        var tools = vm.NavGroups.First();
        Assert.Equal("", tools.Icon);
        Assert.Equal("Ctrl+3", tools.Shortcut);
        Assert.False(tools.HasHeading); // Tools is not about the PC.
        Assert.True(vm.HistoryGroup!.IsBottom);
        Assert.DoesNotContain(vm.HistoryGroup, vm.TopGroups);

        vm.GoToEntryCommand.Execute("4"); // The history comes after the top entries.
        Assert.Equal(PanelPage.Activity, vm.Page);
        Assert.True(vm.ShowTabs);
        Assert.Equal("Storico", vm.PageTitle);
        Assert.NotEqual("", vm.PageSubtitle);

        vm.HistoryGroup.PickCommand.Execute(vm.HistoryGroup.Items[1]);
        Assert.Equal(PanelPage.Report, Panel().Page); // A new panel opens where the last one was.
        vm.GoToEntryCommand.Execute("1");
        Assert.Equal(PanelPage.Monitor, Panel().Page);

        // Back to the group: the page of it seen last, not the first one.
        vm.HistoryGroup.PickCommand.Execute(vm.HistoryGroup.Items[2]);
        vm.GoToEntryCommand.Execute("2");
        vm.HistoryGroup.ActivateCommand.Execute(null);
        Assert.Equal(PanelPage.Actions, vm.Page);
    }

    [Fact]
    public void Compact_bar_and_zoom_are_remembered_and_the_zoom_stays_in_range()
    {
        var vm = Panel();
        vm.ToggleNavCommand.Execute(null);
        for (var i = 0; i < 20; i++) vm.ZoomByCommand.Execute("0.1");
        var again = Panel();
        Assert.True(again.NavCompact);
        Assert.Equal(2, again.Zoom);
        again.ZoomByCommand.Execute("0");
        Assert.Equal(1, Panel().Zoom);
    }

    [Theory]
    [InlineData("SCEGLI E AGGIORNA", "Scegli e aggiorna")]
    [InlineData("PULIZIA DRIVER (DDU)", "Pulizia driver (DDU)")]
    [InlineData("APRI IL BIOS", "Apri il BIOS")]
    [InlineData("HARDWARE E PRESTAZIONI  1", "Hardware e prestazioni  1")]
    [InlineData("IN EVIDENZA · SCELTI DA TE", "In evidenza · scelti da te")]
    [InlineData("WI-FI E RETE", "Wi-Fi e rete")]
    [InlineData("Già scritto bene", "Già scritto bene")]
    [InlineData("OK", "OK")]
    public void Capitals_become_sentence_case_keeping_acronyms(string text, string expected) =>
        Assert.Equal(expected, OptLife.Panel.TextCase.Sentence(text));

    [Fact]
    public void The_search_finds_pages_settings_and_buttons_ignoring_accents()
    {
        var vm = Panel();
        vm.GlobalQuery = "impostazioni";
        Assert.True(vm.SearchOpen);
        Assert.Contains(vm.SearchResults, r => r.Text == "Impostazioni");

        vm.GlobalQuery = "chiudi background";
        var close = Assert.Single(vm.SearchResults, r => r.Where.StartsWith("STRUMENTI") && r.Text.StartsWith("Chiudi i programmi"));
        vm.OpenResultCommand.Execute(close);
        Assert.Equal(PanelPage.View, vm.Page);
        Assert.Equal("", vm.GlobalQuery);

        vm.GlobalQuery = "mai chiudere";
        vm.OpenResultCommand.Execute(vm.SearchResults.First());
        Assert.Equal(PanelPage.Modules, vm.Page);
        Assert.Equal("tools", vm.FocusedModule);

        Assert.True(PanelViewModel.Matches(["perche"], "Perché succede"));
        vm.GlobalQuery = "zzzz nessuna";
        Assert.Empty(vm.SearchResults);
        Assert.NotEqual("", vm.SearchEmptyText);
    }
}

public class ChoiceCategoryTests
{
    private static AppGroup App(string name, string? description = null, string? path = null, long mb = 100)
    {
        var p = new OptLife.Modules.Tools.ProcessInfo(1, 0, name, path ?? $@"C:\Program Files\{name}\{name}.exe", mb * 1024 * 1024, true, description);
        return new AppGroup(p, [p]);
    }

    private static readonly string[] Categories =
    [
        "Launcher | steam, epicgames",
        "Luci e colori | signalrgb, displaycal",
        "Chat | whatsapp, signal",
    ];

    [Fact]
    public void Programs_get_the_first_category_with_a_matching_word()
    {
        Assert.Equal("Launcher", AppRules.Categorize(App("steam"), Categories));
        Assert.Equal("Luci e colori", AppRules.Categorize(App("SignalRgb"), Categories)); // Not "Chat", which is later.
        Assert.Equal("Chat", AppRules.Categorize(App("WhatsApp.Root", "WhatsApp"), Categories));
        Assert.Equal("Altro", AppRules.Categorize(App("pippo"), Categories));
    }

    [Fact]
    public void The_list_follows_the_category_order_heaviest_first_with_icons()
    {
        var list = AppRules.Grouped([(App("pippo", mb: 900), true), (App("whatsapp", mb: 50), true), (App("steam", mb: 100), false), (App("epicgameslauncher", mb: 300), false)], Categories);
        Assert.Equal(["epicgameslauncher", "steam", "whatsapp", "pippo"], list.Select(c => c.Id));
        Assert.Equal(["Launcher", "Launcher", "Chat", "Altro"], list.Select(c => c.Category));
        Assert.EndsWith("steam.exe", list[1].Icon);
    }

    [Fact]
    public void A_group_box_ticks_everything_and_shows_a_dash_in_between()
    {
        var items = new[] { new ChoiceItem(new ActionChoice("a", "A", "", true, "Audio")), new ChoiceItem(new ActionChoice("b", "B", "", false, "Audio")), new ChoiceItem(new ActionChoice("c", "C", "", true, "Chat")) };
        var groups = ChoiceGroup.Of(items);
        Assert.Equal(["Audio", "Chat"], groups.Select(g => g.Name));
        Assert.Null(groups[0].AllChecked);
        groups[0].AllChecked = true;
        Assert.True(items[1].IsChecked);
        items[0].IsChecked = false;
        items[1].IsChecked = false;
        Assert.False(groups[0].AllChecked);
        Assert.Equal("B", items[1].Monogram);
    }
}

public class ListTableTests
{
    private static SettingItem Item(SettingDefinition def) =>
        new(def, new OptLife.Core.Config.ModuleSettings([def], new Dictionary<string, System.Text.Json.JsonElement>()));

    [Fact]
    public void A_list_with_columns_becomes_rows_of_fields_and_back()
    {
        var item = Item(SettingDefinition.Table("copies", ["Nome", "Cartella", "Giorni"], "Copie", "", "Foto | E:/Foto | 7", "Solo nome"));
        Assert.Equal(["NOME", "CARTELLA", "GIORNI"], item.Columns);
        Assert.Equal(2, item.Rows.Count);
        Assert.Equal(["Foto", "E:/Foto", "7"], item.Rows[0].Cells.Select(c => c.Value));
        Assert.Equal(["Solo nome", "", ""], item.Rows[1].Cells.Select(c => c.Value));

        item.Rows[1].Cells[2].Value = "3";
        item.AddRowCommand.Execute(null);                    // An empty row is not saved.
        item.Rows[0].RemoveCommand.Execute(null);
        Assert.Equal("Solo nome |  | 3", item.TextValue);
        Assert.True(item.IsDirty);
    }

    [Fact]
    public void Extra_fields_stay_in_the_last_column_and_pairs_use_their_separator()
    {
        var table = Item(SettingDefinition.Table("t", ["A", "B"], "T", "", "a | b | c"));
        Assert.Equal(["a", "b | c"], table.Rows[0].Cells.Select(c => c.Value));

        var pairs = Item(SettingDefinition.Pairs("p", ["Valore", "Etichetta"], "P", "", "game.fps=FPS"));
        Assert.Equal(["game.fps", "FPS"], pairs.Rows[0].Cells.Select(c => c.Value));
        pairs.Rows[0].Cells[1].Value = "FPS<100";
        Assert.Equal("game.fps=FPS<100", pairs.TextValue);

        // Pasted as text: the table follows.
        pairs.TextValue = "a=b" + Environment.NewLine + "c=d";
        Assert.Equal(2, pairs.Rows.Count);
        Assert.False(Item(SettingDefinition.List("plain", "L", "", "x")).HasColumnNames);
    }
}

public sealed class WelcomeLanguageCopyTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private PanelViewModel Panel() =>
        new(_box.Store, _box.Config, _box.Runner, [new ToolsModule()], () => false, () => false, _box.Clock, () => []);

    [Fact]
    public void The_welcome_shows_once_and_the_language_is_saved_before_restarting()
    {
        var vm = Panel();
        Assert.True(vm.ShowWelcome);
        var restarted = false;
        vm.Restart = () => restarted = true;
        vm.SetLanguageCommand.Execute(L.Code); // Same language: nothing to do.
        Assert.False(restarted);
        vm.SetLanguageCommand.Execute(L.IsEnglish ? L.Italian : L.English);
        Assert.True(restarted);
        Assert.Equal(L.IsEnglish ? L.Italian : L.English, _box.Config.Load().Language);
        vm.FinishWelcomeCommand.Execute(null);
        Assert.False(Panel().ShowWelcome);
    }

    [Fact]
    public void A_problem_is_copied_with_what_is_needed_to_find_it()
    {
        _box.Store.SyncFindings("tools", "s", [new NewFinding("k1", Severity.Medium, "Titolo", "Dettaglio", "Consiglio")], _box.Clock.GetUtcNow(), "Area");
        var vm = Panel();
        string? copied = null;
        vm.CopyText = t => copied = t;
        vm.ShowCommand.Execute("Problems");
        vm.CopyFindingCommand.Execute(vm.Findings.Single());
        Assert.Contains("Titolo", copied);
        Assert.Contains("Dettaglio", copied);
        Assert.Contains("k1", copied);
        Assert.Contains("optlife findings", copied);
        Assert.NotEqual("", vm.Notice);
    }

    [Fact]
    public void A_checklist_can_be_ordered_by_kind_or_importance()
    {
        var items = new[]
        {
            new CheckItem("1 · A", "Steam", "ok", CheckStatus.Ok, Kind: "Launcher"),
            new CheckItem("1 · A", "Reaper", "no", CheckStatus.Info, Kind: "DAW"),
            new CheckItem("2 · B", "Bitwarden", "no", CheckStatus.Wrong, Kind: "Passwords", Buttons: [new CheckButton("install-one", "bitwarden", "INSTALL")]),
        };
        _box.Store.InsertEvents("tools", [new NewEvent(_box.Clock.GetUtcNow(), "board", Severity.Info, "b", Data: CheckItem.ToData(items))]);
        var tab = new ViewTab(new ToolsModule(), new ModuleView("v", "V", "board", [], Layout: ViewLayout.Checklist));
        tab.Load(_box.Store.QueryEvents(_box.Clock.GetUtcNow().AddDays(-1), _box.Clock.GetUtcNow().AddDays(1), "tools", "board"));
        Assert.True(tab.CanSortChecks);
        Assert.Equal(["1 · A", "2 · B"], tab.CheckGroups.Select(g => g.Name));
        tab.SortChecksCommand.Execute("kind");
        Assert.Equal(["Launcher", "DAW", "Passwords"], tab.CheckGroups.Select(g => g.Name));
        tab.SortChecksCommand.Execute("importance");
        Assert.Equal("Bitwarden", tab.CheckGroups[0].Rows[0].Label);
        Assert.Equal("INSTALL", tab.CheckGroups[0].Rows[0].Buttons.Single().ButtonText);
    }
}
