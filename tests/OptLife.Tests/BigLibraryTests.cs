using OptLife.Core;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

public sealed class BigLibraryTests
{
    private sealed class GalleryModule : IModule
    {
        public ModuleInfo Info { get; } = new("library", "Library", "");
    }

    private static readonly ModuleView View = new("library", "Library", "library_board", [], Layout: ViewLayout.Gallery);
    private static readonly DateTimeOffset Day = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static CheckItem Game(string group, string kind, string title, int added, int? played, params string[] buttons) =>
        new(group, title, $"{group} line", CheckStatus.Info, Kind: kind, Buttons: [.. buttons.Select(b => new CheckButton(b, title, b.ToUpperInvariant()))],
            Used: played is { } p ? Day.AddDays(p) : null, Added: Day.AddDays(added));

    private static (BigLibraryViewModel Vm, List<string> Pressed) Make()
    {
        var tab = new ViewTab(new GalleryModule(), View);
        var items = new List<CheckItem>
        {
            Game("Steam", "PC", "Balatro", 3, 1, "play", "hide"),
            Game("Steam", "PC", "Hades II", 2, null, "play", "hide"),
            Game("Steam", "PC", "Undertale", 1, null, "play"),
            Game("PlayStation 2", "Console", "Metal Gear Solid 2", 4, null, "play", "preset", "open-folder"),
        };
        for (var i = 0; i < 6; i++) items.Add(Game("Epic Games", "PC", $"Game {i}", -i, null, "play"));
        tab.Load([new StoredEvent(1, DateTimeOffset.UnixEpoch, "library", "library_board", Severity.Info, "Library", null, CheckItem.ToData(items))]);
        var pressed = new List<string>();
        var vm = new BigLibraryViewModel(tab, b => { pressed.Add($"{b.Button.ActionId}:{b.Button.Argument}"); return Task.CompletedTask; });
        return (vm, pressed);
    }

    [Fact]
    public async Task One_grid_of_every_game_with_the_last_played_on_top_and_a_plays()
    {
        var (vm, pressed) = Make();
        // Recently played, then every game by date added in rows of six: not split by launcher.
        Assert.Equal(["Recently played", "All games", ""], vm.Rows.Select(r => r.Name.Replace("Giocati di recente", "Recently played").Replace("Tutti i giochi", "All games")));
        Assert.Equal(["Metal Gear Solid 2", "Balatro", "Hades II", "Undertale", "Game 0"], vm.Rows[1].Cards.Select(c => c.Card.Title));
        Assert.Equal("Balatro", vm.Selected!.Card.Title);
        await vm.Handle(BigInput.Down);
        Assert.Equal("Metal Gear Solid 2", vm.Selected!.Card.Title);
        await vm.Handle(BigInput.Right);
        await vm.Handle(BigInput.Down);   // Same column in the next row.
        Assert.Equal("Game 2", vm.Selected!.Card.Title);
        Assert.Equal(BigOutcome.Launched, await vm.Handle(BigInput.Accept));
        Assert.Equal(["play:Game 2"], pressed);
    }

    [Fact]
    public async Task Start_opens_the_games_menu_and_b_never_leaves_by_itself()
    {
        var (vm, pressed) = Make();
        await vm.Handle(BigInput.Down);
        await vm.Handle(BigInput.Menu);
        Assert.True(vm.MenuOpen);
        Assert.Equal(["PRESET", "OPEN-FOLDER"], vm.Menu.Select(m => m.Text));
        await vm.Handle(BigInput.Down);
        await vm.Handle(BigInput.Accept);
        Assert.False(vm.MenuOpen);
        Assert.Equal(["open-folder:Metal Gear Solid 2"], pressed);
        Assert.Equal(BigOutcome.None, await vm.Handle(BigInput.Back));   // B alone does not leave…
        Assert.Equal(BigOutcome.None, await vm.Handle(BigInput.Exit));   // …leaving asks first…
        Assert.True(vm.ExitOpen);
        Assert.Equal(BigOutcome.None, await vm.Handle(BigInput.Back));   // …and B stays.
        Assert.False(vm.ExitOpen);
        await vm.Handle(BigInput.Exit);
        Assert.Equal(BigOutcome.Close, await vm.Handle(BigInput.Accept));
    }

    [Fact]
    public async Task Select_opens_order_and_filters_and_y_types_with_the_on_screen_keyboard()
    {
        var (vm, _) = Make();
        await vm.Handle(BigInput.Filters);
        Assert.True(vm.FiltersOpen);
        Assert.True(vm.Filters[0].IsHeading);
        // Down to "Show: Consoles" and apply.
        while (!(vm.Filters[vm.FilterIndex].Value is (string k, string v) && k == "filter" && v.StartsWith("kind:Console", StringComparison.Ordinal)))
            await vm.Handle(BigInput.Down);
        await vm.Handle(BigInput.Accept);
        await vm.Handle(BigInput.Back);
        Assert.False(vm.FiltersOpen);
        Assert.Equal(["Metal Gear Solid 2"], vm.Rows.SelectMany(r => r.Cards).Select(c => c.Card.Title).Distinct());

        vm.Tab.GalleryFilter = "";
        await vm.Handle(BigInput.Search);
        Assert.True(vm.KeyboardOpen);
        vm.Type("H");
        vm.Type("A");
        vm.Type("D");
        Assert.Equal("had", vm.Tab.GallerySearch);
        Assert.Equal(["Hades II"], vm.Rows.SelectMany(r => r.Cards).Select(c => c.Card.Title));
        await vm.Handle(BigInput.Menu);   // X deletes.
        Assert.Equal("ha", vm.Tab.GallerySearch);
        await vm.Handle(BigInput.Back);
        Assert.False(vm.KeyboardOpen);
        await vm.Handle(BigInput.Back);   // B on the grid clears the search.
        Assert.Equal("", vm.Tab.GallerySearch);
    }

    [Fact]
    public void A_playstation_controller_reads_like_an_xbox_one()
    {
        var b = new byte[14];
        b[1] = 1; b[9] = 1; // Cross and Options.
        var (buttons, x, y) = GamepadMapper.FromSony(b, 2, 0.5, 0.0); // Cross up-right, stick pushed up.
        Assert.Equal(GamepadMapper.A | GamepadMapper.Start | GamepadMapper.DpadUp | GamepadMapper.DpadRight, (int)buttons);
        Assert.Equal(0, x);
        Assert.True(y > 30000);
        Assert.Equal(GamepadMapper.X, (int)GamepadMapper.FromSony([1], 0, 0.5, 0.5).Buttons); // Square.
    }

    [Fact]
    public void The_controller_counts_a_press_once_repeats_a_held_direction_and_select_is_short_or_held()
    {
        var pad = new GamepadMapper();
        Assert.Equal([BigInput.Accept], pad.Step(GamepadMapper.A, 0, 0, 0));
        Assert.Empty(pad.Step(GamepadMapper.A, 0, 0, 16));                         // Still held: not again.
        Assert.Equal([BigInput.Right], pad.Step(0, 30000, 0, 32));                 // The stick acts as the cross.
        Assert.Empty(pad.Step(0, 30000, 0, 200));
        Assert.Equal([BigInput.Right], pad.Step(0, 30000, 0, 400));                // Repeats after a moment…
        Assert.Empty(pad.Step(0, 30000, 0, 450));
        Assert.Equal([BigInput.Right], pad.Step(0, 30000, 0, 500));                // …then often.
        Assert.Empty(pad.Step(0, 5000, 0, 520));                                    // Inside the dead zone: nothing.
        Assert.Equal([BigInput.Menu], pad.Step(GamepadMapper.Start, 0, 0, 540));   // Start: the game's menu.
        Assert.Empty(pad.Step(0, 0, 0, 560));
        // Select pressed briefly: filters, on release.
        Assert.Empty(pad.Step(GamepadMapper.Back, 0, 0, 600));
        Assert.Equal([BigInput.Filters], pad.Step(0, 0, 0, 800));
        // Select held for a second: leave (once), and no filters on release.
        Assert.Empty(pad.Step(GamepadMapper.Back, 0, 0, 1000));
        Assert.Equal([BigInput.Exit], pad.Step(GamepadMapper.Back, 0, 0, 2100));
        Assert.Empty(pad.Step(GamepadMapper.Back, 0, 0, 2500));
        Assert.Empty(pad.Step(0, 0, 0, 2600));
    }
}

public sealed class CoverMatchTests
{
    [Fact]
    public void Steam_art_comes_only_from_the_games_own_folder()
    {
        var cache = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "optlife-tests", "lc-" + Guid.NewGuid().ToString("N"), "appcache", "librarycache")).FullName;
        try
        {
            // Balatro keeps its box art directly in its folder; Ori's hero is in another game's folder.
            var balatro = Directory.CreateDirectory(Path.Combine(cache, "2379780")).FullName;
            File.WriteAllBytes(Path.Combine(balatro, "library_600x900.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(balatro, "library_hero.jpg"), [1]);
            var ori = Directory.CreateDirectory(Path.Combine(cache, "1057090", "abc")).FullName;
            File.WriteAllBytes(Path.Combine(ori, "library_hero.jpg"), [1]);
            var card = new GalleryCard(new CheckItem("Steam", "Balatro", "", CheckStatus.Info, Image: Path.Combine(balatro, "library_600x900.jpg")));
            Assert.Equal(Path.Combine(balatro, "library_hero.jpg"), card.Hero);
            // An old-layout box art right in librarycache has no folder of its own: no hero is looked for elsewhere.
            File.WriteAllBytes(Path.Combine(cache, "42_library_600x900.jpg"), [1]);
            var old = new GalleryCard(new CheckItem("Steam", "Old", "", CheckStatus.Info, Image: Path.Combine(cache, "42_library_600x900.jpg")));
            Assert.Equal(Path.Combine(cache, "42_library_600x900.jpg"), old.Hero);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(cache)!)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("METAL GEAR SOLID DELTA", "2417610")]          // "Δ" read as "delta"; the longer Steam name starts with the title.
    [InlineData("Metal Gear Solid V - Ground Zeroes", "311340")]
    [InlineData("Metal Gear Solid", null)]                       // Too short a match for any of these: no cover rather than a wrong one.
    [InlineData("Pokemon Pokopia", null)]
    public void A_cover_found_by_name_must_be_the_same_game(string title, string? expected)
    {
        const string answer = """{"items":[{"name":"METAL GEAR SOLID Δ: SNAKE EATER","id":2417610},{"name":"METAL GEAR SOLID V: GROUND ZEROES","id":311340},{"name":"METAL GEAR SOLID 2: Sons of Liberty - Master Collection Version","id":2131640}]}""";
        Assert.Equal(expected, OptLife.Modules.Library.PcRules.SteamMatch(answer, title));
    }
}
