using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Layout;
using OptLife.Modules.Cleanup;
using OptLife.Modules.Emulation;

namespace OptLife.Tests;

internal sealed class FakeLayoutEnvironment : ILayoutEnvironment
{
    public List<string> DriveList { get; } = [@"C:\", @"E:\"];
    public Dictionary<string, List<string>> Top { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FolderFacts> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Reference> Refs { get; } = [];
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Created { get; } = [];
    public List<string> Moved { get; } = [];
    public Dictionary<string, List<string>> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long Free { get; set; } = 1L << 40;

    public IReadOnlyList<string> Drives() => DriveList;
    public IReadOnlyList<string> TopFolders(string root) => Top.TryGetValue(root, out var l) ? [.. l] : [];
    public FolderFacts Inspect(string path, bool measure) => Facts.TryGetValue(path, out var f) ? f : new FolderFacts(path, Launcher.None, false, false, 0);
    public IReadOnlyList<Reference> References() => Refs;
    public bool Exists(string path) => Folders.Contains(LayoutPaths.Expand(path).TrimEnd('\\'));
    public ActionResult CreateFolder(string path) { Created.Add(LayoutPaths.Expand(path)); Folders.Add(LayoutPaths.Expand(path).TrimEnd('\\')); return new ActionResult(true, path); }
    public long FreeBytes(string path) => Free;
    public bool SameVolume(string a, string b) => a[..2].Equals(b[..2], StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> RunningUnder(string path) => Running.TryGetValue(path, out var l) ? l : [];

    public ActionResult Move(string from, string to)
    {
        Moved.Add($"{from} → {to}");
        var root = Path.GetPathRoot(from)!;
        if (Top.TryGetValue(root, out var top)) top.Remove(from);
        Folders.Remove(from);
        Folders.Add(to);
        return new ActionResult(true, "");
    }

    public int FixShortcuts(string from, string to) => 2;
    public HashSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ProcessRunning(string name) => Processes.Contains(name);
    public List<(string, string)> Epic { get; } = [];
    public int FixEpic(string from, string to, string backupFolder) { Epic.Add((from, to)); return 1; }
    public long Total { get; set; }
    public long TotalBytes(string path) => Total;

    /// <summary>The folders directly inside a path, from <see cref="Folders"/>.</summary>
    public IReadOnlyList<string> Subfolders(string path) =>
        [.. Folders.Where(f => Path.GetDirectoryName(f) is { } parent && LayoutPaths.Same(parent, path)).Select(Path.GetFileName).OfType<string>()];
}

public sealed class LayoutTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeLayoutEnvironment _env = new();
    private readonly LayoutModule _layout;
    private readonly IModule[] _modules;

    public LayoutTests()
    {
        var list = new List<IModule> { new CleanupModule(), new EmulationModule() };
        _layout = new LayoutModule(_box.Config, () => list, _env);
        list.Add(_layout);
        _modules = [.. list];
        // These tests are about the Hub: the way of keeping the Desktop that has one.
        Set("layout", "desktop", DesktopPreset.NameOf(DesktopPreset.Hub));
    }

    public void Dispose() => _box.Dispose();

    private void Set(string module, string key, object value) =>
        _box.Config.UpdateModule(module, m => m.Settings[key] = JsonSerializer.SerializeToElement(value));

    private async Task Round() => Assert.True(await _box.Runner.RunTaskAsync(_layout, _layout.Tasks.Single(), CancellationToken.None, onDemand: true));

    private IReadOnlyList<CheckItem> Board() =>
        CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), LayoutModule.Id, LayoutModule.BoardKind).MaxBy(e => e.Id)!.Data);

    private Task<ActionResult> Run(string action, string? argument) =>
        _box.Runner.RunActionAsync(_layout, _layout.Actions.Single(a => a.Id == action), "test", true, argument, CancellationToken.None);

    [Fact]
    public void The_layout_that_ships_is_generic()
    {
        var json = LayoutTemplate.Default.ToJson();
        Assert.DoesNotContain("alex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"E:\\", json);
        Assert.NotEmpty(LayoutTemplate.Default.Places);
        Assert.Contains(LayoutTemplate.Default.Rules, r => r.Module == "cleanup" && r.Key == "tidy");
    }

    /// <summary>The organisation already on a real PC, written in its settings: the page must be all green and APPLY change nothing.</summary>
    [Fact]
    public async Task An_existing_organisation_is_recognised_and_nothing_is_proposed()
    {
        Set("layout", "games", @"E:\Games");
        Set("layout", "backups", @"E:\Backups");
        Set("layout", "hubSections", new[] { "0 Da sistemare", "1 Giochi", "2 Emulatori" });
        Set("cleanup", "tidy", new[] { @"%USERPROFILE%\Desktop | Hub, desktop.ini | %USERPROFILE%\Desktop\Hub\0 Da sistemare", @"C:\Users\Public\Desktop | desktop.ini | %USERPROFILE%\Desktop\Hub\0 Da sistemare" });
        Set("cleanup", "watch", new[] { @"C:\ | Games go in E:\Games, portable programs in C:\Software.", @"E:\ | Non-Steam games go in E:\Games\Repack." });
        Set("cleanup", "shortcuts", new[] { @"%USERPROFILE%\Desktop\Hub" });
        Set("cleanup", "heavy", new[] { @"%USERPROFILE%\Downloads | 1 | 60" });
        Set("emulation", "games", @"E:\Games\Emulazione");
        foreach (var p in new[] { @"E:\Games", @"E:\Games\Repack", @"E:\Games\Emulazione", @"C:\Software", @"C:\Software\Consoles", @"E:\Backups" })
            _env.Folders.Add(p);
        foreach (var s in new[] { "Hub", @"Hub\0 Da sistemare", @"Hub\1 Giochi", @"Hub\2 Emulatori" })
            _env.Folders.Add(LayoutPaths.Expand($@"%USERPROFILE%\Desktop\{s}"));
        _env.Top[@"E:\"] = [@"E:\Games", @"E:\Backups"];
        _env.Top[@"C:\"] = [@"C:\Software", @"C:\Windows", @"C:\Users"];

        await Round();
        var board = Board();
        Assert.DoesNotContain(board, i => i.Status is CheckStatus.Wrong or CheckStatus.Watch);
        var result = await Run(LayoutModule.Apply, null);
        Assert.Contains("già tutte a posto", result.Detail);
        Assert.Empty(_env.Created);
    }

    /// <summary>On 24 Nov the Hub's button looked dead and APPLY was pressed instead: the row must carry the Hub's own action, on top.</summary>
    [Fact]
    public async Task A_waiting_hub_reorganisation_is_on_top_with_its_own_button_updates_the_settings_and_undoes()
    {
        var hub = LayoutPaths.Expand(Path.Combine("%USERPROFILE%", "Desktop", "Hub"));
        _env.Folders.Add(hub);
        _env.Folders.Add(Path.Combine(hub, "0 Da sistemare"));
        _env.Folders.Add(Path.Combine(hub, "1 Giochi"));
        Set("layout", "hubSections", new[] { "0 To sort", "1 Games" });
        await Round();
        var row = Board()[0];
        Assert.Equal(CheckStatus.Watch, row.Status);
        Assert.Equal(LayoutModule.HubApply, Assert.Single(row.Buttons!).ActionId);
        Assert.Contains("VEDI E APPLICA", (await Run(LayoutModule.Apply, null)).Detail);

        var choices = _box.Runner.ChoicesOf(_layout, _layout.Actions.Single(a => a.Id == LayoutModule.HubApply), out _);
        var done = await Run(LayoutModule.HubApply, string.Join(';', choices.Where(c => c.Checked).Select(c => c.Id)));
        Assert.True(done.Ok, done.Detail);
        Assert.Contains(Path.Combine(hub, "1 Games"), _env.Folders);
        Assert.Equal(["0 To sort", "1 Games"], _box.Runner.SettingsOf(_layout).GetList("hubSections"));
        Assert.DoesNotContain(Board(), i => i.Buttons?.Any(b => b.ActionId == LayoutModule.HubApply) == true);

        var undo = Board().Single(i => i.Buttons?.Any(b => b.ActionId == LayoutModule.HubUndo) == true).Buttons!.Single();
        Assert.True((await Run(LayoutModule.HubUndo, undo.Argument)).Ok);
        Assert.Contains(Path.Combine(hub, "1 Giochi"), _env.Folders);
        Assert.DoesNotContain(Path.Combine(hub, "1 Games"), _env.Folders);
        Assert.Equal(["0 To sort", "1 Games"], _box.Runner.SettingsOf(_layout).GetList("hubSections")); // The choice as it was before.
    }

    [Fact]
    public async Task On_a_new_pc_apply_creates_the_folders_and_adds_the_rules_once_and_never_overrides_a_choice()
    {
        Set("emulation", "emulators", @"D:\MyEmus"); // The person chose: the layout leaves it.
        await Round();
        Assert.Contains(Board(), i => i.Status == CheckStatus.Wrong);

        var result = await Run(LayoutModule.Apply, null);
        Assert.True(result.Ok);
        Assert.Contains(@"E:\Giochi\Repack", _env.Created); // Games on the second drive by default ("Giochi" is the default name in Italian).
        Assert.Contains(_env.Created, c => c.EndsWith(@"Desktop\Hub"));
        var cleanup = _box.Runner.SettingsOf(_modules[0]);
        Assert.Contains(cleanup.GetList("tidy"), l => l.StartsWith(@"%USERPROFILE%\Desktop | Hub"));
        Assert.Equal(@"E:\Giochi\Emulazione", _box.Runner.SettingsOf(_modules[1]).GetText("games"));
        Assert.Equal(@"D:\MyEmus", _box.Runner.SettingsOf(_modules[1]).GetText("emulators"));
        Assert.Contains(Board(), i => i.Value.Contains(@"D:\MyEmus") && i.Status == CheckStatus.Info);

        var again = await Run(LayoutModule.Apply, null);
        Assert.Contains("già tutte a posto", again.Detail);
        Assert.Single(_box.Runner.SettingsOf(_modules[0]).GetList("tidy"), l => l.StartsWith(@"%USERPROFILE%\Desktop | Hub"));
    }

    [Fact]
    public void Folders_outside_are_judged_safe_or_risky_with_the_reason()
    {
        var choices = new LayoutChoices(@"E:\Games", @"C:\Software", @"C:\Software\Consoles", "", "Hub", ["0"]);
        var refs = new List<Reference>
        {
            new(RefKind.Installed, "Riot Client", @"C:\Riot Games\Riot Client\RiotClientServices.exe"),
            new(RefKind.Service, "Sync service", @"E:\SyncTool\svc.exe"),
            new(RefKind.Startup, "Widget", @"C:\Widget\widget.exe"),
        };
        Assert.Equal(Risk.Launcher, LayoutScan.Judge(new FolderFacts(@"E:\SteamLibrary", Launcher.Steam, false, false, 0), refs, choices).Risk);
        // An Epic game moves with help (OptLife updates Epic's records); a Riot one only with guided steps.
        var epic = LayoutScan.Judge(new FolderFacts(@"E:\racinggame", Launcher.Epic, true, true, 0), refs, choices);
        Assert.Equal((Risk.Assisted, @"E:\Games\racinggame"), (epic.Risk, epic.Destination));
        Assert.Contains("Epic", epic.Why);
        Assert.Equal(Risk.Risky, LayoutScan.Judge(new FolderFacts(@"C:\Riot Games\Valorant", Launcher.Riot, true, true, 0), refs, choices).Risk);
        Assert.Equal(Risk.Risky, LayoutScan.Judge(new FolderFacts(@"C:\Riot Games", Launcher.None, true, false, 0), refs, choices).Risk);
        Assert.Contains("servizio", LayoutScan.Judge(new FolderFacts(@"E:\SyncTool", Launcher.None, true, false, 0), refs, choices).What);
        Assert.Equal(Risk.Risky, LayoutScan.Judge(new FolderFacts(@"C:\Widget", Launcher.None, true, false, 0), refs, choices).Risk);
        Assert.Equal(Risk.Data, LayoutScan.Judge(new FolderFacts(@"E:\UNITY", Launcher.None, false, false, 0), refs, choices).Risk);
        var game = LayoutScan.Judge(new FolderFacts(@"C:\Elden Ring", Launcher.None, true, true, 50L << 30), refs, choices);
        Assert.Equal((Risk.Safe, @"E:\Games\Repack\Elden Ring"), (game.Risk, game.Destination));
        var tool = LayoutScan.Judge(new FolderFacts(@"E:\HWiNFO", Launcher.None, true, false, 20 << 20), refs, choices);
        Assert.Equal((Risk.Safe, @"C:\Software\HWiNFO"), (tool.Risk, tool.Destination));
    }

    [Fact]
    public async Task A_safe_folder_moves_with_its_settings_and_the_move_can_be_undone_while_a_risky_one_never_moves()
    {
        Set("layout", "games", @"E:\Games");
        _box.Config.UpdateModule("scheduler", m => m.Settings["jobs"] = JsonSerializer.SerializeToElement(new[] { @"Tool | 14:00 | E:\HWiNFO\HWiNFO64.exe" }));
        _env.Top[@"E:\"] = [@"E:\HWiNFO", @"E:\Valorant"];
        _env.Facts[@"E:\HWiNFO"] = new FolderFacts(@"E:\HWiNFO", Launcher.None, true, false, 20 << 20);
        _env.Facts[@"E:\Valorant"] = new FolderFacts(@"E:\Valorant", Launcher.Riot, true, true, 30L << 30);
        await Round();
        var choices = _box.Runner.ChoicesOf(_layout, _layout.Actions.Single(a => a.Id == LayoutModule.Move), out _);
        var choice = Assert.Single(choices); // Only the safe one is offered, and not ticked…
        Assert.False(choice.Checked);
        Assert.Contains("MB", choice.Detail);   // …with what the move means: size, time, room left.

        var result = await Run(LayoutModule.Move, @"E:\HWiNFO;E:\Valorant");
        Assert.False(result.Ok); // The Riot game was refused…
        Assert.Contains("Valorant", result.Detail);
        Assert.Equal([@"E:\HWiNFO → C:\Software\HWiNFO"], _env.Moved); // …the portable program moved.
        Assert.Contains(_box.Config.GetModule("scheduler").Settings["jobs"].EnumerateArray(), e => e.GetString()!.Contains(@"C:\Software\HWiNFO\HWiNFO64.exe"));

        var move = Board().Single(i => i.Buttons?.Any(b => b.ActionId == LayoutModule.MoveBack) == true).Buttons!.Single(b => b.ActionId == LayoutModule.MoveBack);
        _env.Top[@"C:\"] = [@"C:\Software\HWiNFO"];
        _env.Folders.Remove(@"E:\HWiNFO");
        _env.Folders.Remove(@"C:\Software\HWiNFO");
        Assert.True((await Run(LayoutModule.MoveBack, move.Argument)).Ok);
        Assert.Contains(_box.Config.GetModule("scheduler").Settings["jobs"].EnumerateArray(), e => e.GetString()!.Contains(@"E:\HWiNFO\HWiNFO64.exe"));
    }

    [Fact]
    public async Task An_epic_game_moves_with_its_records_once_epic_is_closed_and_a_nearly_full_drive_asks_twice()
    {
        Set("layout", "games", @"C:\Giochi");
        _env.Top[@"E:\"] = [@"E:\racinggame"];
        _env.Facts[@"E:\racinggame"] = new FolderFacts(@"E:\racinggame", Launcher.Epic, true, true, 30L << 30);
        _env.Processes.Add("EpicGamesLauncher");
        await Round();
        Assert.Contains("EpicGamesLauncher", (await Run(LayoutModule.Move, @"E:\racinggame")).Detail);
        _env.Processes.Clear();

        // C: would be left with 5 GB of 500: not blocked, but a second, explicit confirmation is asked.
        (_env.Free, _env.Total) = (35L << 30, 500L << 30);
        var first = await Run(LayoutModule.Move, @"E:\racinggame");
        Assert.False(first.Ok);
        Assert.Contains("quasi pieno", first.Detail);
        Assert.Empty(_env.Moved);
        var anyway = Board().Single(i => i.Label == @"E:\racinggame").Buttons!.First();
        Assert.Equal("!" + @"E:\racinggame", anyway.Argument);
        Assert.True((await Run(LayoutModule.Move, anyway.Argument)).Ok);
        Assert.Equal([@"E:\racinggame → C:\Giochi\racinggame"], _env.Moved);
        Assert.Equal([(@"E:\racinggame", @"C:\Giochi\racinggame")], _env.Epic);
    }

    [Fact]
    public void Epic_records_are_rebased_in_their_json_form()
    {
        const string item = """{"InstallLocation": "E:\\racinggame", "ManifestLocation": "E:\\racinggame/.egstore", "Other": "E:\\racinggame2"}""";
        var updated = LayoutEpic.Rebase(item, @"E:\racinggame", @"C:\Giochi\racinggame");
        Assert.Contains(@"""InstallLocation"": ""C:\\Giochi\\racinggame""", updated);
        Assert.Contains(@"C:\\Giochi\\racinggame/.egstore", updated);
        Assert.Contains(@"E:\\racinggame2", updated);   // Another folder that only starts the same way is left alone.
    }

    [Fact]
    public async Task A_move_waits_for_the_program_to_close_and_for_room_on_the_drive()
    {
        Set("layout", "games", @"E:\Games");
        _env.Top[@"E:\"] = [@"E:\Tool"];
        _env.Facts[@"E:\Tool"] = new FolderFacts(@"E:\Tool", Launcher.None, true, false, 10L << 30);
        _env.Running[@"E:\Tool"] = ["tool"];
        Assert.Contains("in uso", (await Run(LayoutModule.Move, @"E:\Tool")).Detail);
        _env.Running.Clear();
        _env.Free = 1L << 30;
        Assert.Contains("spazio", (await Run(LayoutModule.Move, @"E:\Tool")).Detail);
        Assert.Empty(_env.Moved);
    }

    [Fact]
    public async Task Export_makes_the_layout_general_and_import_adapts_it_to_the_drives_here()
    {
        Set("layout", "games", @"E:\Games");
        Set("cleanup", "heavy", new[] { @"%USERPROFILE%\Downloads | 1 | 60", @"E:\Games\Download repack | 20 | 30", @"F:\Other | 1 | 1" });
        var json = _layout.Export(_box.Runner.SettingsOf(_layout), _layout.DataDirectory);
        Assert.Contains(@"{games}\\Download repack", json);
        Assert.DoesNotContain(@"F:\\Other", json); // A folder of this PC only is not passed on.

        _env.DriveList.Remove(@"E:\");
        var notes = _layout.Import(json, _layout.DataDirectory);
        Assert.Contains(notes, n => n.Contains("E:"));
        Assert.Equal("", _box.Runner.SettingsOf(_layout).GetText("games")); // Back to the automatic choice.
        await Run(LayoutModule.Apply, null);
        Assert.Contains(_box.Runner.SettingsOf(_modules[0]).GetList("heavy"), l => l.StartsWith(@"C:\Giochi\Download repack"));
    }
}

public sealed class LayoutHubTests
{
    [Fact]
    public void A_setting_naming_the_profile_as_a_variable_follows_a_move_and_keeps_the_variable()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var line = @"%USERPROFILE%\Desktop | Hub, desktop.ini | %USERPROFILE%\Desktop\Hub\0 Da sistemare";
        var moved = LayoutPaths.RebaseText(line, Path.Combine(profile, @"Desktop\Hub\0 Da sistemare"), Path.Combine(profile, @"Desktop\Hub\0 To sort"));
        Assert.Equal(@"%USERPROFILE%\Desktop | Hub, desktop.ini | %USERPROFILE%\Desktop\Hub\0 To sort", moved);
        Assert.Equal(@"X:\Other\0 Da sistemare", LayoutPaths.RebaseText(@"X:\Other\0 Da sistemare", Path.Combine(profile, @"Desktop\Hub\0 Da sistemare"), @"C:\New"));
    }

    [Fact]
    public void Sections_are_renamed_by_number_and_every_step_has_its_reverse()
    {
        var ops = LayoutHub.ByNumber(@"C:\U\Desktop\Hub", ["0 Da sistemare", "1 Giochi", "8 Altro"], ["0 To sort", "1 Games", "2 Entertainment"]);
        Assert.Equal(
        [
            new HubOp("folder", @"C:\U\Desktop\Hub\0 Da sistemare", @"C:\U\Desktop\Hub\0 To sort"),
            new HubOp("folder", @"C:\U\Desktop\Hub\1 Giochi", @"C:\U\Desktop\Hub\1 Games"),
            new HubOp("create", @"C:\U\Desktop\Hub\2 Entertainment"),
        ], ops);
        Assert.Equal(new HubOp("folder", @"B", @"A"), LayoutHub.Reverse(new HubOp("folder", "A", "B")));
        Assert.Equal("remove", LayoutHub.Reverse(new HubOp("create", "X")).Kind);
        Assert.Contains("1 Games", LayoutHub.Describe(ops[1], @"C:\U\Desktop\Hub"));
    }

    [Fact]
    public void Texts_naming_an_old_folder_are_listed_not_rewritten()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "optlife-tests", "hub-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "GUIDA.txt"), @"Apri Desktop\Hub\3 Manga - Suwayomi\Suwayomi Launcher.lnk");
            File.WriteAllText(Path.Combine(dir, "other.txt"), "nothing");
            var found = LayoutHub.MentionsOutside([dir], [@"C:\U\Desktop\Hub\3 Manga - Suwayomi"]);
            var (file, mentions) = Assert.Single(found);
            Assert.EndsWith("GUIDA.txt", file);
            Assert.Equal("3 Manga - Suwayomi", mentions);
            Assert.Contains("3 Manga", File.ReadAllText(Path.Combine(dir, "GUIDA.txt")));   // Untouched.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
