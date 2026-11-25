using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Startup;

namespace OptLife.Tests;

internal sealed class FakeStartupEnvironment : IStartupEnvironment
{
    public List<StartupItem> Items { get; } = [];
    public List<string> Switched { get; } = [];

    public IReadOnlyList<StartupItem> Read() => [.. Items];

    public ActionResult SetEnabled(StartupItem item, bool enabled)
    {
        Switched.Add($"{item.Id}={(enabled ? "on" : "off")}");
        var i = Items.FindIndex(x => x.Id == item.Id);
        Items[i] = Items[i] with { Enabled = enabled };
        return new ActionResult(true, "ok");
    }
}

public sealed class StartupTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeStartupEnvironment _env = new();
    private readonly StartupModule _module;

    public StartupTests()
    {
        _module = new StartupModule(_env);
        _box.Config.UpdateModule("startup", m =>
        {
            m.Settings["allowedStartup"] = JsonSerializer.SerializeToElement(new[] { "Wallpaper Engine", "Everything", "FluentFlyout", "TranslucentTB" });
            m.Settings["blockedStartup"] = JsonSerializer.SerializeToElement(new[] { "Spotify | Spotify → Settings → Startup → No", "Steam", "Edge Update", "Proton VPN" });
        });
    }

    public void Dispose() => _box.Dispose();

    private static StartupItem Run(string name, string command, bool on = true) =>
        new($"run:HKCU:{name}", name, Path.GetFileName(command.Trim('"').Split('"')[0]), command, StartupSource.Registry, on);

    private async Task Round() => Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));

    private IReadOnlyList<StoredFinding> Open() => [.. _box.Store.GetFindings().Where(f => f.Module == "startup" && f.Status == FindingStatus.Open)];

    [Fact]
    public void Names_match_without_spaces_and_never_on_the_folder()
    {
        var wallpaper = Run("Wallpaper Engine", "\"E:\\SteamLibrary\\steamapps\\common\\wallpaper_engine\\wallpaper64.exe\" -autostart");
        var (verdict, _) = StartupRules.Judge(wallpaper, ["WallpaperEngine"], [new BlockRule("Steam", null)]);
        Assert.Equal(Verdict.Allowed, verdict);
        // A Steam library in the path is not Steam.
        var other = Run("Something", "\"E:\\SteamLibrary\\tool.exe\"");
        Assert.Equal(Verdict.Other, StartupRules.Judge(other, [], [new BlockRule("Steam", null)]).Verdict);
        var proton = new StartupItem("task:\\ProtonVPN", "ProtonVPN", "ProtonVPN.exe", "", StartupSource.Task, true);
        Assert.Equal(Verdict.Blocked, StartupRules.Judge(proton, [], [new BlockRule("Proton VPN", null)]).Verdict);
        var edge = new StartupItem("task:\\MicrosoftEdgeUpdateTaskMachineCore", "MicrosoftEdgeUpdateTaskMachineCore", "", "", StartupSource.Task, true);
        Assert.Equal(Verdict.Blocked, StartupRules.Judge(edge, [], [new BlockRule("Edge Update", null)]).Verdict);
    }

    [Fact]
    public async Task A_blocked_program_that_is_on_is_a_problem_with_its_own_advice_and_a_switch()
    {
        _env.Items.Add(Run("Spotify", "\"C:\\Users\\x\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart"));
        _env.Items.Add(Run("Everything", "\"C:\\Program Files\\Everything\\Everything.exe\" -startup"));
        await Round();

        var f = Assert.Single(Open(), f => f.Key.StartsWith("blocked|"));
        Assert.Contains("Spotify", f.Title);
        Assert.Contains("Settings → Startup", f.Advice);
        Assert.Equal(StartupRules.Disable, f.Fix!.ActionId);

        var action = _module.Actions.Single(a => a.Id == StartupRules.Disable);
        var result = await _box.Runner.RunActionAsync(_module, action, "test", confirmed: true, f.Fix.Argument, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Contains("enable-startup", result.Undo);
        Assert.Equal(["run:HKCU:Spotify=off"], _env.Switched);
        Assert.DoesNotContain(Open(), x => x.Key.StartsWith("blocked|"));
    }

    [Fact]
    public async Task A_blocked_program_that_switches_itself_back_on_says_so_and_goes_in_the_history()
    {
        _env.Items.Add(Run("Spotify", "Spotify.exe", on: false));
        await Round();
        Assert.DoesNotContain(Open(), f => f.Key.StartsWith("blocked|"));

        _env.Items[0] = _env.Items[0] with { Enabled = true };
        _box.Clock.Now += TimeSpan.FromMinutes(10);
        await Round();

        var f = Assert.Single(Open(), f => f.Key.StartsWith("blocked|"));
        Assert.Contains("riacceso da solo", f.Title);
        Assert.Equal(Severity.Medium, f.Severity);
        var events = _box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "startup", kind: "startup_enabled");
        Assert.Single(events);
    }

    [Fact]
    public async Task A_wanted_program_off_or_missing_is_reported()
    {
        _env.Items.Add(Run("Everything", "Everything.exe", on: false));
        _env.Items.Add(new StartupItem("store:unchihugo.FluentFlyoutApp_x\\FluentFlyoutStartup", "FluentFlyoutApp", "unchihugo.FluentFlyoutApp_x FluentFlyoutStartup", "", StartupSource.Store, true));
        await Round();

        var open = Open();
        Assert.Contains(open, f => f.Key == "allowed|run:hkcu:everything" && f.Fix!.ActionId == StartupRules.Enable);
        Assert.Contains(open, f => f.Key == "missing|wallpaperengine");
        Assert.Contains(open, f => f.Key == "missing|translucenttb");
        Assert.DoesNotContain(open, f => f.Key.Contains("fluentflyout"));
    }

    [Fact]
    public async Task New_entries_go_in_the_history_after_the_first_look_and_the_board_lists_everything()
    {
        _env.Items.Add(Run("Everything", "Everything.exe"));
        await Round();
        Assert.Empty(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "startup", kind: "startup_added"));

        _env.Items.Add(Run("Discord", "Discord.exe"));
        _box.Clock.Now += TimeSpan.FromMinutes(10);
        await Round();
        var added = Assert.Single(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "startup", kind: "startup_added"));
        Assert.Contains("Discord", added.Title);

        var board = _box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "startup", kind: StartupModule.BoardKind).MaxBy(e => e.Time)!;
        var items = CheckItem.FromData(board.Data);
        Assert.Equal(CheckStatus.Ok, items.Single(i => i.Label == "Everything").Status);
        Assert.Equal(CheckStatus.Ok, items.Single(i => i.Label == "Discord").Status); // The colour is the state: on.
        Assert.Equal("", items.Single(i => i.Label == "Discord").StatusText); // No "OK" or "TO FIX": the value says On or Off.
        Assert.Equal(StartupRules.Disable, items.Single(i => i.Label == "Discord").Buttons![0].ActionId);
        Assert.Equal(CheckStatus.Watch, items.Single(i => i.Label == "Wallpaper Engine").Status);
    }

    [Fact]
    public void The_switch_value_is_what_task_manager_writes()
    {
        var off = StartupRules.Approved(false, new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(12, off.Length);
        Assert.Equal(3, off[0]);
        Assert.False(StartupRules.IsApproved(off));
        Assert.True(StartupRules.IsApproved(StartupRules.Approved(true, DateTimeOffset.UtcNow)));
        Assert.True(StartupRules.IsApproved([6, 0, 0, 0]));
        Assert.True(StartupRules.IsApproved(null));
        Assert.True(StartupRules.IsStoreEnabled(2));
        Assert.False(StartupRules.IsStoreEnabled(1));
        Assert.Equal("Spotify", StartupRules.NameOf("run:HKCU:Spotify"));
        Assert.Equal("SpotifyStartupTask", StartupRules.NameOf("store:SpotifyAB.SpotifyMusic_x\\SpotifyStartupTask"));
    }

    [Fact]
    public void Entries_named_with_guids_get_a_readable_name_and_say_what_they_are()
    {
        var edge = new StartupItem(@"task:\MicrosoftEdgeUpdateTaskUserS-1-5-21-449190848-3642930408-738954857-1001Core{C56EA737-AF64-40CE-86D7-2BC91250F240}",
            "MicrosoftEdgeUpdateTaskUserS-1-5-21-449190848-3642930408-738954857-1001Core{C56EA737-AF64-40CE-86D7-2BC91250F240}", "x MicrosoftEdgeUpdate.exe",
            @"C:\x\MicrosoftEdgeUpdate.exe /c", StartupSource.Task, true, Program: "Microsoft Edge Update", Publisher: "Microsoft Corporation");
        Assert.Equal("Microsoft Edge Update", edge.Display);
        Assert.Contains("Microsoft Corporation", edge.What);
        var guid = new StartupItem(@"task:\{D2665A0E-3169-4C56-8EB6-AC90063A0FD8}", "{D2665A0E-3169-4C56-8EB6-AC90063A0FD8}", "{D2665A0E-3169-4C56-8EB6-AC90063A0FD8} XRD Manager.exe",
            @"C:\Temp\XRD Manager.exe /x", StartupSource.Task, false, Missing: true);
        Assert.Equal("XRD Manager", guid.Display);
        Assert.Contains("XRD Manager.exe", guid.What);
        var plain = new StartupItem("run:HKCU:Everything", "Everything", "Everything.exe", "Everything.exe", StartupSource.Registry, true, Program: "Everything");
        Assert.Equal("Everything", plain.Display);
    }

    [Fact]
    public void Every_entry_is_coloured_by_its_state_for_anyone()
    {
        var on = new StartupItem("run:HKCU:Tool", "Tool", "Tool.exe", "Tool.exe", StartupSource.Registry, true);
        var gone = new StartupItem("run:HKCU:Old", "Old", "Old.exe", "Old.exe", StartupSource.Registry, true, Missing: true);
        var off = new StartupItem("run:HKCU:Quiet", "Quiet", "Quiet.exe", "Quiet.exe", StartupSource.Registry, false);
        var board = StartupRules.Board([on, gone, off], [], []);
        Assert.Equal(CheckStatus.Ok, board.Single(i => i.Label == "Tool").Status);
        Assert.Contains("non c'è più", board.Single(i => i.Label == "Old").Value);
        Assert.Equal(CheckStatus.Wrong, board.Single(i => i.Label == "Quiet").Status); // Off is red, for anyone.
        Assert.Equal("Programmi all'avvio", board[0].Group); // No lists: no "other", just the programs.
    }

    [Fact]
    public void Programs_and_scheduled_tasks_are_in_separate_sections_and_the_difference_from_the_lists_is_written()
    {
        var gpu = new StartupItem(@"task:\GPU efficienza", "GPU efficienza", "GPU efficienza powershell.exe", "powershell.exe -File gpu.ps1", StartupSource.Task, false);
        var spotify = new StartupItem("run:HKCU:Spotify", "Spotify", "Spotify.exe", "Spotify.exe", StartupSource.Registry, false);
        var board = StartupRules.Board([gpu, spotify], ["GPU efficienza"], [new BlockRule("Spotify", null)]);
        var g = board.Single(i => i.Label == "GPU efficienza");
        Assert.Equal((StartupRules.TasksMustStart, CheckStatus.Wrong), (g.Group, g.Status));
        Assert.Contains("dovrebbe partire", g.Value); // The difference from the lists is written, not coloured.
        var s = board.Single(i => i.Label == "Spotify");
        Assert.Equal((StartupRules.MustNot, CheckStatus.Wrong), (s.Group, s.Status)); // Off is red even when that is what the list wants.
        Assert.True(board.ToList().IndexOf(s) < board.ToList().IndexOf(g)); // Programs before tasks.
    }
    [Fact]
    public void A_wanted_program_started_by_its_service_is_on_not_missing()
    {
        StartupService[] services = [new("Wallpaper Engine Service", "Wallpaper Engine Service", @"C:\WE\wallpaperservice64.exe", "wallpaperservice64 wallpaper64 launcher")];
        var board = StartupRules.Board([], ["Wallpaper Engine", "Nothing"], [], services);
        var we = board.Single(i => i.Label == "Wallpaper Engine");
        Assert.Equal(CheckStatus.Ok, we.Status);
        Assert.StartsWith(StartupRules.ViaServiceText, we.Value);
        Assert.Equal(CheckStatus.Watch, board.Single(i => i.Label == "Nothing").Status);
        var missing = StartupRules.Findings([], ["Wallpaper Engine", "Nothing", "wallpaper64"], [], new HashSet<string>(), services).ToList();
        Assert.Equal(["missing|nothing"], missing.Select(m => m.Key)); // Also the program the service starts, beside it.
    }
}

public sealed class StartupPanelTests : IDisposable
{
    private readonly Sandbox _box = new();
    public void Dispose() => _box.Dispose();

    [Fact]
    public async Task A_line_changes_colour_right_after_its_button_is_pressed()
    {
        var env = new FakeStartupEnvironment();
        env.Items.Add(new StartupItem("run:HKCU:Discord", "Discord", "Discord.exe", "Discord.exe", StartupSource.Registry, true));
        var module = new StartupModule(env);
        _box.Config.UpdateModule("startup", m => m.Settings["blockedStartup"] = JsonSerializer.SerializeToElement(new[] { "Discord" }));
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None));

        var vm = new OptLife.Panel.ViewModels.PanelViewModel(_box.Store, _box.Config, _box.Runner, [module], () => true, () => true, _box.Clock) { AgentGrace = TimeSpan.Zero };
        vm.ShowViewCommand.Execute(vm.Views.Single());
        var row = vm.Views.Single().CheckGroups.SelectMany(g => g.Rows).Single(r => r.Label == "Discord");
        Assert.Equal("good", row.Level); // On.

        _box.Clock.Now += TimeSpan.FromSeconds(30);
        await vm.PressCommand.ExecuteAsync(row.Buttons.Single());
        var after = vm.Views.Single().CheckGroups.SelectMany(g => g.Rows).Single(r => r.Label == "Discord");
        Assert.Equal(("high", "Spento"), (after.Level, after.ValueHead.Split(" · ")[0])); // Off, right after the button.
    }
}
