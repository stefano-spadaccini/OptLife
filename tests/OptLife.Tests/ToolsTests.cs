using OptLife.Core;
using OptLife.Modules.Tools;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

/// <summary>A PC whose running programs are whatever the test says; closing and launching are only written down.</summary>
internal sealed class FakeToolsEnvironment : IToolsEnvironment
{
    public List<ProcessInfo> Running { get; } = [];
    public List<string> Closed { get; } = [];
    public List<Tool> Launched { get; } = [];

    public IReadOnlyList<ProcessInfo> Processes() => [.. Running];

    public ActionResult Close(IReadOnlyList<AppGroup> apps)
    {
        Closed.AddRange(apps.Select(a => a.Root.Name));
        return new ActionResult(true, $"Chiusi {apps.Count}.");
    }

    public ActionResult Launch(Tool tool)
    {
        Launched.Add(tool);
        return new ActionResult(true, $"Avviato: {tool.Name}.");
    }

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FileExists(string path) => Files.Contains(path);
    public List<(string File, bool Admin)> Known { get; } = [];
    public ActionResult LaunchKnown(string file, bool admin) { Known.Add((file, admin)); return new ActionResult(true, file); }
    public List<ShaderCache> Caches { get; } = [];
    public IReadOnlyList<ShaderCache> ShaderCaches() => [.. Caches];
    public List<string> Emptied { get; } = [];
    public ActionResult RecycleContents(IReadOnlyList<string> folders) { Emptied.AddRange(folders); return new ActionResult(true, ""); }
}

public class ToolsTests
{
    private const long Mb = 1024 * 1024;
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ProcessInfo P(int pid, int parent, string name, string? path, long mb, bool window = false, string? description = null) =>
        new(pid, parent, name, path, mb * Mb, window, description);

    /// <summary>An evening on a gaming PC: a game started by Steam, Discord, Spotify, Chrome, Claude, drivers and Windows.</summary>
    private static List<ProcessInfo> Evening() =>
    [
        P(1, 0, "explorer", $@"{Win}\explorer.exe", 150, true),
        P(2, 0, "svchost", $@"{Win}\System32\svchost.exe", 80),
        P(10, 1, "steam", @"C:\Program Files (x86)\Steam\steam.exe", 190, description: "Steam"),
        P(11, 10, "steamwebhelper", @"C:\Program Files (x86)\Steam\bin\cef\steamwebhelper.exe", 900),
        P(12, 10, "RocketLeague", @"E:\racinggame\Binaries\Win64\RocketLeague.exe", 4800, true),
        P(20, 1, "Discord", @"C:\Users\x\AppData\Local\Discord\app-1\Discord.exe", 700, true),
        P(30, 1, "Spotify", @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe", 300),
        P(40, 1, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 400, true, "Google Chrome"),
        P(41, 40, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 900),
        P(50, 1, "claude", @"C:\Users\x\AppData\Local\Claude\claude.exe", 1100, true, "Claude"),
        P(60, 2, "lghub_agent", @"C:\Program Files\LGHUB\lghub_agent.exe", 120),
        P(70, 1, "MSIAfterburner", null, 60), // Runs as administrator: its path cannot be read.
        P(80, 1, "TinyHelper", @"C:\Program Files\Tiny\TinyHelper.exe", 5),
        P(90, 2, "SearchHost", $@"{Win}\SystemApps\SearchHost.exe", 200),
    ];

    private static readonly string[] Protect = ["discord", "spotify", "lghub", "optlife"];
    private static readonly string[] Folders = [@"E:\racinggame", @"steamapps\common"];
    private static readonly string[] Uncheck = ["steam", "claude"];

    [Fact]
    public void Only_the_programs_that_do_not_serve_the_game_are_offered_biggest_first()
    {
        var offered = AppRules.Candidates(Evening(), Protect, Folders, Uncheck, minMb: 30);

        // Chrome and its children are one program; Claude is offered but not ticked.
        Assert.Equal(["chrome", "claude"], offered.Select(o => o.App.Root.Name));
        Assert.Equal(1300 * Mb, offered[0].App.Bytes);
        Assert.True(offered[0].Checked);
        Assert.False(offered[1].Checked);
    }

    [Fact]
    public void A_launcher_that_started_the_game_is_never_offered()
    {
        // Steam started Rocket League, which is in a game folder: Steam stays, even though "steam" is only on the untick list.
        var offered = AppRules.Candidates(Evening(), Protect, Folders, Uncheck, minMb: 30);
        Assert.DoesNotContain(offered, o => o.App.Root.Name == "steam");

        // Without the game running, Steam is offered, but not ticked.
        var noGame = Evening().Where(p => p.Name != "RocketLeague").ToList();
        var steam = Assert.Single(AppRules.Candidates(noGame, Protect, Folders, Uncheck, minMb: 30), o => o.App.Root.Name == "steam");
        Assert.False(steam.Checked);
        Assert.Equal(1090 * Mb, steam.App.Bytes);
    }

    [Fact]
    public void Windows_and_programs_that_cannot_be_read_are_never_offered()
    {
        var offered = AppRules.Candidates(Evening(), [], [], [], minMb: 0).Select(o => o.App.Root.Name).ToList();

        Assert.DoesNotContain("explorer", offered);
        Assert.DoesNotContain("svchost", offered);
        Assert.DoesNotContain("SearchHost", offered);
        Assert.DoesNotContain("MSIAfterburner", offered);
        Assert.Contains("TinyHelper", offered); // With no minimum, even the small ones.
    }

    /// <summary>17/11/2026: Overwatch, in E:\shootergame, was offered for closing because that folder was in no list.</summary>
    private static List<ProcessInfo> OverwatchEvening() =>
    [
        P(1, 0, "explorer", $@"{Win}\explorer.exe", 150, true),
        P(20, 1, "Battle.net", @"C:\Program Files (x86)\Battle.net\Battle.net.exe", 300, description: "Battle.net"),
        P(21, 20, "Overwatch", @"E:\shootergame\_retail_\Overwatch.exe", 7100),
        P(40, 1, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 400, true),
    ];

    [Fact]
    public void The_game_being_played_is_never_offered_even_in_a_folder_no_list_knows()
    {
        var offered = AppRules.Candidates(OverwatchEvening(), Protect, Folders, [], minMb: 30, gamePids: [21]);

        Assert.Equal(["chrome"], offered.Select(o => o.App.Root.Name)); // Neither Overwatch nor Battle.net, which started it.
    }

    [Fact]
    public void A_window_covering_the_screen_is_protected_even_when_no_game_is_known()
    {
        var processes = OverwatchEvening();
        processes[2] = processes[2] with { Fullscreen = true };

        var offered = AppRules.Candidates(processes, Protect, Folders, [], minMb: 30);

        Assert.Equal(["chrome"], offered.Select(o => o.App.Root.Name));
    }

    [Fact]
    public async Task The_module_reads_which_process_is_the_game_from_the_live_values()
    {
        var box = new Sandbox();
        var board = new OptLife.Core.Hosting.LiveBoard($@"Local\OptLife.Test.{Guid.NewGuid():N}");
        var runner = new OptLife.Core.Hosting.ModuleRunner(box.Store, box.Config, box.Clock, board);
        var env = new FakeToolsEnvironment();
        env.Running.AddRange(OverwatchEvening());
        var module = new ToolsModule(env);
        board.Publish("games", [new LiveValue(ToolsModule.GamePidKey, "", "Gioco", 21, "")], box.Clock.GetLocalNow());

        var close = module.Actions.Single(a => a.Id == ToolsModule.CloseApps);
        var offered = runner.ChoicesOf(module, close, out var error);
        Assert.Null(error);
        Assert.Equal(["chrome"], offered.Select(c => c.Id));

        // Even an id sent by hand is refused when it is the game: the list is read again at that moment.
        await runner.RunActionAsync(module, close, "test", confirmed: true, "Overwatch;Battle.net", CancellationToken.None);
        Assert.Empty(env.Closed);
        board.Dispose();
    }

    [Fact]
    public void A_protected_name_on_a_helper_does_not_protect_the_whole_program()
    {
        // Steam's crash reporter is called "crashhandler": protecting that name must not hide Steam.
        var noGame = Evening().Where(p => p.Name != "RocketLeague").ToList();
        noGame.Add(P(13, 10, "crashhandler", @"C:\Program Files (x86)\Steam\crashhandler.exe", 1));

        var offered = AppRules.Candidates(noGame, ["crashhandler", .. Protect], Folders, Uncheck, minMb: 30);

        Assert.Contains(offered, o => o.App.Root.Name == "steam");
    }

    [Fact]
    public void The_default_game_folders_do_not_hide_the_launchers_next_to_them()
    {
        var module = new ToolsModule(new FakeToolsEnvironment());
        var folders = (string[])module.Settings.Single(s => s.Key == "protectFolders").Default;
        List<ProcessInfo> launchers =
        [
            P(1, 0, "EpicGamesLauncher", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", 300),
            P(2, 0, "RiotClientServices", @"C:\Riot Games\Riot Client\RiotClientServices.exe", 200),
            P(3, 0, "Fortnite", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient.exe", 3000),
            P(4, 0, "League of Legends", @"C:\Riot Games\League of Legends\Game\League of Legends.exe", 2000),
        ];

        var offered = AppRules.Candidates(launchers, [], folders, [], minMb: 30).Select(o => o.App.Root.Name).ToList();

        Assert.Equal(["EpicGamesLauncher", "RiotClientServices"], offered); // The games are not offered, their launchers are.
    }

    [Fact]
    public void Copies_of_the_same_program_started_apart_are_one_line()
    {
        List<ProcessInfo> builds =
        [
            P(100, 2, "dotnet", @"C:\Program Files\dotnet\dotnet.exe", 80),
            P(101, 2, "dotnet", @"C:\Program Files\dotnet\dotnet.exe", 70),
            P(102, 101, "conhost", @"C:\Tools\conhost.exe", 5),
        ];

        var line = Assert.Single(AppRules.Candidates(builds, [], [], [], minMb: 30));

        Assert.Equal(("dotnet", 3, 155 * Mb), (line.App.Root.Name, line.App.Members.Count, line.App.Bytes));
    }

    [Fact]
    public void A_choice_says_how_much_memory_and_whether_a_window_is_open()
    {
        var chrome = AppRules.Candidates(Evening(), Protect, Folders, Uncheck, minMb: 30)[0];

        var choice = AppRules.ToChoice(chrome.App, chrome.Checked);

        Assert.Equal(("chrome", "Google Chrome"), (choice.Id, choice.Label));
        Assert.Equal("1,3 GB · 2 processi · finestra aperta: chiusa come col pulsante X", choice.Detail);
    }

    [Fact]
    public void Tools_are_read_from_the_settings_lines()
    {
        var tools = AppRules.ParseTools(
        [
            @"Scheda video | Modalità efficienza | C:\Tools\gpu-modalita.cmd | efficienza | sì | Tetto 1905 MHz e 250 W",
            @" | Backup ora | ""E:\Backups\Aggiorna backup.cmd""",
            "riga senza file",
        ]);

        Assert.Equal(2, tools.Count);
        Assert.Equal(new Tool("Scheda video", "Modalità efficienza", @"C:\Tools\gpu-modalita.cmd", "efficienza", true, "Tetto 1905 MHz e 250 W"), tools[0]);
        Assert.Equal(new Tool("Strumenti", "Backup ora", @"E:\Backups\Aggiorna backup.cmd", "", false, ""), tools[1]);
    }

    [Fact]
    public async Task From_the_panel_the_list_opens_first_and_only_the_ticked_ones_are_closed()
    {
        var box = new Sandbox();
        var env = new FakeToolsEnvironment();
        env.Running.AddRange(Evening());
        var module = new ToolsModule(env);
        box.Config.UpdateModule("tools", c =>
        {
            c.Settings["protect"] = System.Text.Json.JsonSerializer.SerializeToElement(Protect);
            c.Settings["protectFolders"] = System.Text.Json.JsonSerializer.SerializeToElement(Folders);
            c.Settings["uncheck"] = System.Text.Json.JsonSerializer.SerializeToElement(Uncheck);
        });
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [module], () => false, () => false, box.Clock);
        var tab = Assert.Single(vm.Views);
        vm.ShowViewCommand.Execute(tab);

        var close = tab.ButtonGroups.SelectMany(g => g.Buttons).Single(b => b.Button.ActionId == ToolsModule.CloseApps);
        await vm.PressCommand.ExecuteAsync(close);

        Assert.Empty(env.Closed); // Nothing happens before the person confirms.
        Assert.Equal(["Google Chrome", "Claude"], tab.Choices.Select(c => c.Label));
        tab.Choices[0].IsChecked = false;
        tab.Choices[1].IsChecked = true; // Ticked by hand.
        await vm.ConfirmChoicesCommand.ExecuteAsync(null);

        Assert.Equal(["claude"], env.Closed);
        Assert.False(tab.HasChoices);
        Assert.StartsWith("Fatto.", tab.Notice);
        Assert.Contains(box.Store.GetActions(10), a => a.Action == ToolsModule.CloseApps && a.Ok);
    }

    [Fact]
    public async Task A_tool_button_launches_what_the_settings_say()
    {
        var box = new Sandbox();
        var env = new FakeToolsEnvironment();
        var module = new ToolsModule(env);
        box.Config.UpdateModule("tools", c => c.Settings["tools"] = System.Text.Json.JsonSerializer.SerializeToElement(new[]
        {
            @"Scheda video | Modalità potenza | C:\Tools\gpu-modalita.cmd | potenza | sì | Più frequenza, più calore",
        }));
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [module], () => false, () => false, box.Clock);
        var tab = Assert.Single(vm.Views);
        vm.ShowViewCommand.Execute(tab);

        Assert.Equal(["PRIMA DI GIOCARE", "SCHEDA VIDEO"], tab.ButtonGroups.Select(g => g.Name).Where(n => n != "PC")); // "PC" (restart into the BIOS) only on UEFI.
        var gpu = tab.ButtonGroups[1].Buttons.Single();
        Assert.Equal("Più frequenza, più calore (amministratore)", gpu.Description);
        await vm.PressCommand.ExecuteAsync(gpu);

        Assert.Equal("potenza", Assert.Single(env.Launched).Arguments);
        Assert.Equal("Fatto. Avviato: Modalità potenza.", tab.Notice);
    }

    [Fact]
    public void An_action_with_a_list_to_tick_can_never_run_by_itself()
    {
        var box = new Sandbox();
        var module = new ToolsModule(new FakeToolsEnvironment());
        var close = module.Actions.Single(a => a.Id == ToolsModule.CloseApps);
        box.Config.UpdateModule("tools", c => c.Permissions[ToolsModule.CloseApps] = Permission.Auto);

        Assert.Equal(Permission.Ask, box.Runner.PermissionOf(module, close));
    }
}
