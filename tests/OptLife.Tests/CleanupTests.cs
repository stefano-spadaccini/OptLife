using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Modules.Cleanup;
using OptLife.Modules.Cleanup.Windows;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

/// <summary>A PC made of dictionaries: what is in each folder, what starts with Windows, and a record of what was asked.</summary>
internal sealed class FakeCleanupEnvironment : ICleanupEnvironment
{
    public Dictionary<string, List<string>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DiskItem>> Weights { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DiskItem>> Rebuildable { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StartupEntry> StartupEntries { get; } = [];
    public List<string> Programs { get; } = [];

    public List<string> Recycled { get; } = [];
    public List<string> Opened { get; } = [];
    public int Weighings { get; private set; }

    public IReadOnlyList<string>? List(string folder) => Folders.GetValueOrDefault(folder);

    public IReadOnlyList<DiskItem> Weigh(string folder)
    {
        Weighings++;
        return Weights.GetValueOrDefault(folder) ?? [];
    }

    public IReadOnlyList<DiskItem> Find(string root, IReadOnlyList<string> names, string? marker) => Rebuildable.GetValueOrDefault(root) ?? [];
    public IReadOnlyList<string> BrokenLinks(string folder) => Broken.GetValueOrDefault(folder) ?? [];
    public IReadOnlyList<StartupEntry> Startup() => [.. StartupEntries];
    public IReadOnlyList<string> InstalledPrograms() => [.. Programs];

    public ActionResult Move(string folder, IReadOnlyList<string> names, string destination)
    {
        Folders[folder].RemoveAll(names.Contains);
        if (!Folders.TryGetValue(destination, out var there)) Folders[destination] = there = [];
        there.AddRange(names);
        var inbox = Path.GetFileName(destination);
        if (destination.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !Folders[folder].Contains(inbox)) Folders[folder].Add(inbox);
        return new ActionResult(true, $"{names.Count} elementi spostati in {destination}.", $"Rimettili da {destination} in {folder}");
    }

    public ActionResult Recycle(IReadOnlyList<string> paths)
    {
        Recycled.AddRange(paths);
        foreach (var list in Rebuildable.Values) list.RemoveAll(i => paths.Contains(i.Path));
        foreach (var list in Links.Values) list.RemoveAll(s => paths.Contains(s.Path));
        foreach (var list in BrokenIn.Values) list.RemoveAll(paths.Contains);
        return new ActionResult(true, $"{paths.Count} cartelle spostate nel Cestino.", "Ripristinale dal Cestino");
    }

    public ActionResult RemoveStartup(string id)
    {
        StartupEntries.RemoveAll(e => e.Id == id);
        return new ActionResult(true, "Tolto dall'avvio.", "reg add …");
    }

    /// <summary>Shortcuts by folder, and the Hub's sections by folder.</summary>
    public Dictionary<string, List<OptLife.Modules.Cleanup.Shortcut>> Links { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Sections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<OptLife.Modules.Cleanup.Shortcut> Shortcuts(string folder) => Links.TryGetValue(folder, out var l) ? [.. l] : [];
    public IReadOnlyList<string> Subfolders(string folder) => Sections.TryGetValue(folder, out var l) ? l : [];
    public Dictionary<string, List<string>> BrokenIn { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BrokenShortcuts(string folder, bool recursive) => BrokenIn.TryGetValue(folder, out var l) ? [.. l] : [];

    public ActionResult MoveFile(string path, string toFolder, string? newName = null)
    {
        var from = Links.Values.FirstOrDefault(l => l.Any(s => s.Path == path));
        if (from is null) return new ActionResult(false, "non c'è più");
        var s = from.First(x => x.Path == path);
        from.Remove(s);
        var to = Path.Combine(toFolder, newName ?? Path.GetFileName(path));
        if (!Links.TryGetValue(toFolder, out var there)) Links[toFolder] = there = [];
        there.Add(s with { Path = to });
        return new ActionResult(true, to);
    }

    public ActionResult Open(string folder)
    {
        Opened.Add(folder);
        return new ActionResult(true, $"Aperta {folder}.");
    }

    public ActionResult MoveBack(IReadOnlyList<MovedItem> items)
    {
        foreach (var i in items)
        {
            var name = Path.GetFileName(i.From);
            if (Folders.TryGetValue(Path.GetDirectoryName(i.To)!, out var there) && there.Remove(name))
                (Folders.TryGetValue(Path.GetDirectoryName(i.From)!, out var back) ? back : Folders[Path.GetDirectoryName(i.From)!] = []).Add(name);
        }
        return new ActionResult(true, $"Rimessi: {items.Count}.");
    }
}

public class CleanupModuleTests
{
    private const string Desktop = @"C:\Users\io\Desktop", Inbox = @"C:\Users\io\Desktop\Hub\0 Da sistemare", Downloads = @"C:\Users\io\Downloads", Projects = @"C:\Progetti";
    private readonly Sandbox _box = new();
    private readonly FakeCleanupEnvironment _pc = new();
    private readonly CleanupModule _module;

    public CleanupModuleTests()
    {
        _module = new CleanupModule(_pc);
        _pc.Folders[Desktop] = ["Hub", "desktop.ini"];
        _pc.StartupEntries.Add(new StartupEntry("HKCU:Everything", "Everything", @"C:\Program Files\Everything\Everything.exe -startup", true));
    }

    private DateTimeOffset Now => _box.Clock.Now;

    private void Set(string key, object value) =>
        _box.Config.UpdateModule("cleanup", c => c.Settings[key] = JsonSerializer.SerializeToElement(value));

    private async Task Check(TimeSpan? later = null, bool asked = false)
    {
        _box.Clock.Now += later ?? TimeSpan.Zero;
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None, asked),
            _box.Runner.ReadStatus(_module, _module.Tasks[0]).LastError);
    }

    private IReadOnlyList<StoredFinding> Open() => _box.Store.GetFindings();

    private Task<ActionResult> Do(string action, string? argument, string actor = "pannello") =>
        _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == action), actor, confirmed: true, argument, CancellationToken.None);

    private DiskItem Item(string path, double gb, int daysOld) => new(path, (long)(gb * 1024 * 1024 * 1024), Now.AddDays(-daysOld));

    [Fact]
    public async Task With_no_rules_it_has_nothing_to_say()
    {
        await Check();

        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_desktop_that_must_hold_only_the_hub_reports_what_else_is_on_it_and_can_move_it_aside()
    {
        Set("tidy", new[] { $"{Desktop} | Hub, desktop.ini | {Inbox}" });
        _pc.Folders[Desktop].AddRange(["Nuovo gioco.lnk", "setup.exe"]);

        await Check();
        var finding = Assert.Single(Open());
        Assert.Equal(@"In C:\Users\io\Desktop ci sono 2 elementi fuori posto", finding.Title);
        Assert.Contains("Nuovo gioco.lnk, setup.exe", finding.Detail);
        Assert.Equal(new FindingFix(CleanupModule.MoveLoose, "Spostali", Desktop), finding.Fix);

        var result = await Do(CleanupModule.MoveLoose, Desktop);
        await Check();

        Assert.True(result.Ok);
        Assert.Equal(["Nuovo gioco.lnk", "setup.exe"], _pc.Folders[Inbox]);
        Assert.Empty(Open());
        Assert.NotNull(Assert.Single(_box.Store.GetActions()).Undo);
    }

    [Fact]
    public async Task What_was_already_there_at_first_sight_is_normal_and_only_newcomers_are_reported_one_by_one()
    {
        Set("watch", new[] { @"C:\ | I giochi vanno in E:\Games, i programmi portable in C:\Software." });
        _pc.Folders[@"C:\"] = ["Program Files", "Users", "Windows", "Riot Games"];
        await Check();
        Assert.Empty(Open());

        _pc.Folders[@"C:\"].AddRange(["GiocoNuovo", "temp123"]);
        await Check(TimeSpan.FromMinutes(30));

        Assert.Equal(["Nuovo in C:\\: GiocoNuovo", "Nuovo in C:\\: temp123"], Open().Select(f => f.Title).Order());
        Assert.Contains(@"E:\Games", Open()[0].Advice);

        // Accepting one leaves the other; removing the other closes it.
        _box.Store.SetFindingStatus(Open().Single(f => f.Title.EndsWith("GiocoNuovo")).Id, FindingStatus.Ignored);
        _pc.Folders[@"C:\"].Remove("temp123");
        await Check(TimeSpan.FromMinutes(30));
        Assert.Empty(Open());
    }

    [Fact]
    public async Task Broken_shortcuts_are_counted_and_named()
    {
        Set("shortcuts", new[] { @"C:\Users\io\Desktop\Hub" });
        _pc.Broken[@"C:\Users\io\Desktop\Hub"] = [@"1 Giochi\Vecchio gioco.lnk", @"5 Sistema\Tool.lnk"];

        await Check();

        var finding = Assert.Single(Open());
        Assert.Equal("2 collegamenti rotti in Hub", finding.Title);
        Assert.Contains(@"1 Giochi\Vecchio gioco.lnk", finding.Detail);
    }

    [Fact]
    public async Task Downloads_that_are_heavy_or_long_forgotten_are_listed_for_the_person_to_decide()
    {
        Set("heavy", new[] { $"{Downloads} | 1 | 60" });
        _pc.Weights[Downloads] =
        [
            Item($@"{Downloads}\gioco.iso", 12.4, 3),          // heavy
            Item($@"{Downloads}\driver-vecchio.zip", 0.3, 200), // light but forgotten
            Item($@"{Downloads}\foto.jpg", 0.004, 300),         // too small to matter
            Item($@"{Downloads}\recente.zip", 0.5, 2),          // neither
        ];

        await Check();

        var finding = Assert.Single(Open());
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Downloads: 2 elementi da valutare, 13 GB", finding.Title);
        Assert.Contains("gioco.iso (12 GB, fermo da 3 giorni)", finding.Detail);
        Assert.DoesNotContain("foto.jpg", finding.Detail);

        Assert.True((await Do(CleanupModule.OpenFolder, Downloads)).Ok);
        Assert.Equal([Downloads], _pc.Opened);
    }

    [Fact]
    public async Task Folders_that_rebuild_themselves_are_offered_to_the_bin_only_when_stale_and_worth_it()
    {
        Set("rebuildable", new[] { $"Compilati .NET | {Projects} | bin, obj | 30 | *.csproj" });
        _pc.Rebuildable[Projects] =
        [
            Item($@"{Projects}\Vecchio\bin", 3.2, 120),
            Item($@"{Projects}\Vecchio\obj", 1.1, 120),
            Item($@"{Projects}\InCorso\bin", 2.0, 1), // Being worked on: left alone.
        ];

        await Check();
        var finding = Assert.Single(Open());
        Assert.Equal("Compilati .NET: si possono liberare 4,3 GB", finding.Title);
        Assert.DoesNotContain("InCorso", finding.Detail);

        var result = await Do(CleanupModule.Recycle, "Compilati .NET");
        await Check(asked: true);

        Assert.True(result.Ok);
        Assert.Equal([$@"{Projects}\Vecchio\bin", $@"{Projects}\Vecchio\obj"], _pc.Recycled);
        Assert.Empty(Open());
        Assert.Equal("Ripristinale dal Cestino", _box.Store.GetActions()[0].Undo);
    }

    [Fact]
    public async Task Little_to_gain_is_not_worth_a_notice()
    {
        Set("rebuildable", new[] { $"Compilati .NET | {Projects} | bin, obj | 30" });
        _pc.Rebuildable[Projects] = [Item($@"{Projects}\Vecchio\bin", 0.4, 120)];

        await Check();

        Assert.Empty(Open());
    }

    [Fact]
    public async Task Sending_to_the_bin_can_never_be_set_to_run_by_itself()
    {
        _box.Config.UpdateModule("cleanup", c =>
        {
            c.Permissions[CleanupModule.Recycle] = Permission.Auto;        // Even written by hand in the file.
            c.Permissions[CleanupModule.RemoveStartup] = Permission.Auto;
        });
        Set("rebuildable", new[] { $"Compilati .NET | {Projects} | bin, obj | 30" });
        _pc.Rebuildable[Projects] = [Item($@"{Projects}\Vecchio\bin", 9, 120)];

        await Check();

        Assert.Empty(_pc.Recycled);
        Assert.Single(Open());
        Assert.Equal(Permission.Ask, _box.Runner.PermissionOf(_module, _module.Actions.Single(a => a.Id == CleanupModule.Recycle)));

        var vm = new PanelViewModel(_box.Store, _box.Config, _box.Runner, [_module], () => false, () => false, _box.Clock);
        var actions = Assert.Single(vm.Modules).Actions;
        Assert.Equal(["Chiedimi prima", "Mai"], actions.Single(a => a.Name.StartsWith("Sposta nel Cestino")).Options);
        Assert.Equal(["Fallo da solo", "Chiedimi prima", "Mai"], actions.Single(a => a.Name.StartsWith("Sposta ciò")).Options);
    }

    [Fact]
    public async Task A_program_that_adds_itself_to_startup_is_noticed_and_can_be_taken_out()
    {
        await Check(); // First look: Everything is simply what starts today.
        Assert.Empty(Open());

        _pc.StartupEntries.Add(new StartupEntry("HKCU:Discord", "Discord", @"C:\Users\io\AppData\Local\Discord\Update.exe --processStart Discord.exe", true));
        _pc.StartupEntries.Add(new StartupEntry("HKLM:Driver Helper", "Driver Helper", @"C:\Program Files\X\helper.exe", false));
        await Check(TimeSpan.FromMinutes(30));

        var discord = Open().Single(f => f.Title == "Nuovo programma all'avvio: Discord");
        Assert.Equal(CleanupModule.RemoveStartup, discord.Fix!.ActionId);
        // For all users: cannot be removed from here, so there is no button, only the way to do it.
        var helper = Open().Single(f => f.Title == "Nuovo programma all'avvio: Driver Helper");
        Assert.Null(helper.Fix);
        Assert.Contains("amministratore", helper.Advice);

        await Do(CleanupModule.RemoveStartup, "HKCU:Discord");
        await Check(TimeSpan.FromMinutes(30));
        Assert.DoesNotContain(Open(), f => f.Title.Contains("Discord"));
    }

    [Fact]
    public async Task A_program_listed_as_unwanted_is_removed_by_itself_every_time_it_comes_back_when_allowed()
    {
        Set("unwantedStartup", new[] { "BlueStacks Services" });
        _box.Config.UpdateModule("cleanup", c => c.Permissions[CleanupModule.RemoveUnwanted] = Permission.Auto);
        await Check();

        _pc.StartupEntries.Add(new StartupEntry("HKCU:electron.app.BlueStacks Services", "electron.app.BlueStacks Services", "BlueStacksServices.exe", true));
        await Check(TimeSpan.FromMinutes(30));

        Assert.DoesNotContain(_pc.StartupEntries, e => e.Name.Contains("BlueStacks"));
        var logged = Assert.Single(_box.Store.GetActions());
        Assert.Equal((CleanupModule.RemoveUnwanted, ModuleRunner.AutoActor, true), (logged.Action, logged.Actor, logged.Ok));

        // The automatic action is only for what was listed: it refuses anything else, whoever asks.
        var other = await Do(CleanupModule.RemoveUnwanted, "HKCU:Everything", actor: "cli");
        Assert.False(other.Ok);
        Assert.Contains(_pc.StartupEntries, e => e.Name == "Everything");
    }

    [Fact]
    public async Task Programs_installed_and_removed_are_written_in_the_history_without_being_problems()
    {
        _pc.Programs.AddRange(["Steam", "Discord"]);
        await Check();

        _pc.Programs.Remove("Discord");
        _pc.Programs.Add("Hades II");
        await Check(TimeSpan.FromMinutes(30));
        await Check(TimeSpan.FromMinutes(30));

        var titles = _box.Store.QueryEvents(Now.AddDays(-1), Now.AddDays(1)).Select(e => e.Title).ToList();
        Assert.Equal(["Programma disinstallato: Discord", "Programma installato: Hades II"], titles.Order());
        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_folder_that_grew_a_lot_in_a_month_is_reported_once_there_is_a_month_to_compare()
    {
        Set("map", new[] { @"E:\" });
        _pc.Weights[@"E:\"] = [Item(@"E:\SteamLibrary", 400, 1), Item(@"E:\Games", 150, 1), Item(@"E:\piccola", 0.2, 1)];
        await Check();
        Assert.Empty(Open()); // Nothing to compare with yet.

        var map = Assert.Single(_box.Store.QueryEvents(Now.AddDays(-1), Now.AddDays(1), kind: "space_map"));
        Assert.Equal("Spazio occupato: SteamLibrary 400 GB, Games 150 GB", map.Title);
        Assert.False(map.Data.ContainsKey(@"E:\piccola"));

        _pc.Weights[@"E:\"] = [Item(@"E:\SteamLibrary", 465, 1), Item(@"E:\Games", 152, 1)];
        await Check(TimeSpan.FromDays(20));

        var finding = Assert.Single(Open());
        Assert.Equal(@"E:\SteamLibrary è cresciuta di 65 GB in 20 giorni", finding.Title);
        Assert.Equal("Era 400 GB, ora è 465 GB.", finding.Detail);
    }

    [Fact]
    public async Task Heavy_work_is_spaced_out_but_done_at_once_when_asked()
    {
        Set("heavy", new[] { $"{Downloads} | 1 | 60" });
        await Check();
        await Check(TimeSpan.FromMinutes(30));
        await Check(TimeSpan.FromMinutes(30));
        Assert.Equal(1, _pc.Weighings);

        await Check(TimeSpan.FromMinutes(30), asked: true);
        Assert.Equal(2, _pc.Weighings);

        await Check(TimeSpan.FromHours(12));
        Assert.Equal(3, _pc.Weighings);
    }

    [Fact]
    public async Task Nothing_is_touched_while_a_game_is_running()
    {
        using var board = new LiveBoard($@"Local\OptLife.Test.{Guid.NewGuid():N}");
        var runner = new ModuleRunner(_box.Store, _box.Config, _box.Clock, board);
        board.Publish("games", [new LiveValue("game.minutes", "In gioco da", "Gioco", 3, "min")], Now);
        Set("heavy", new[] { $"{Downloads} | 1 | 60" });

        Assert.True(await runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None));

        Assert.Equal(0, _pc.Weighings);
    }

    [Fact]
    public void Rules_written_wrong_are_skipped_rather_than_guessed()
    {
        Assert.Null(TidyRule.Parse(""));
        Assert.Null(RebuildRule.Parse(@"Compilati | C:\Progetti | bin"));
        Assert.Null(RebuildRule.Parse(@"Compilati | C:\Progetti | bin | presto"));
        var rule = RebuildRule.Parse(@" Unity | C:\Users\io | Library, Temp | 30 | ProjectSettings ")!;
        Assert.Equal(("Unity", "ProjectSettings", 2), (rule.Name, rule.Marker, rule.Folders.Count));
        Assert.Null(TidyRule.Parse(@"C:\Desktop | Hub")!.MoveTo);
    }
}

public class WindowsCleanupEnvironmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optlife-tests", Guid.NewGuid().ToString("N"));
    private readonly WindowsCleanupEnvironment _env = new();

    public WindowsCleanupEnvironmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, int bytes = 10, DateTime? modified = null)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modified is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    [Fact]
    public void A_folder_is_listed_and_weighed_with_everything_inside_it()
    {
        Write(@"a\uno.bin", 1000);
        Write(@"a\dentro\due.bin", 2500);
        Write("sciolto.txt", 40);

        Assert.Equal(["a", "sciolto.txt"], _env.List(_dir));
        Assert.Null(_env.List(Path.Combine(_dir, "manca")));

        var weighed = _env.Weigh(_dir).ToDictionary(i => i.Name, i => i.Bytes);
        Assert.Equal(3500, weighed["a"]);
        Assert.Equal(40, weighed["sciolto.txt"]);
    }

    [Fact]
    public void Rebuildable_folders_are_found_only_next_to_their_marker_and_never_inside_one_another()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(@"Progetto\Progetto.csproj");
        Write(@"Progetto\bin\Debug\app.dll", 5000, old);
        Write(@"Progetto\bin\Debug\obj\interno.txt", 10, old); // Inside a match: part of it, not a second one.
        Write(@"Progetto\obj\cache.bin", 700, old);
        Write(@"Documenti\bin\importante.docx", 900); // A folder that only happens to be called "bin".

        var found = _env.Find(_dir, ["bin", "obj"], "*.csproj");

        Assert.Equal([@"Progetto\bin", @"Progetto\obj"], found.Select(i => Path.GetRelativePath(_dir, i.Path)).Order());
        Assert.Equal(5010, found.Single(i => i.Path.EndsWith("bin")).Bytes);
        Assert.Equal(old, found.Single(i => i.Path.EndsWith("obj")).Modified.UtcDateTime);
        // Without a marker every folder with that name counts: which is why the marker exists.
        Assert.Equal(3, _env.Find(_dir, ["bin", "obj"], null).Count);
    }

    [Fact]
    public void Moving_aside_never_overwrites_and_says_where_things_went()
    {
        Write("gioco.lnk");
        Write(@"cartella\dentro.txt");
        var inbox = Path.Combine(_dir, "Hub", "0 Da sistemare");
        Write(@"Hub\0 Da sistemare\gioco.lnk"); // Already one with that name there.

        var result = _env.Move(_dir, ["gioco.lnk", "cartella", "non-esiste"], inbox);

        Assert.True(result.Ok);
        Assert.StartsWith("2 elementi spostati", result.Detail);
        Assert.True(File.Exists(Path.Combine(inbox, "gioco.lnk")));
        Assert.True(File.Exists(Path.Combine(inbox, "gioco (2).lnk")));
        Assert.True(File.Exists(Path.Combine(inbox, "cartella", "dentro.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "gioco.lnk")));
        Assert.Contains(inbox, result.Undo);
    }

    [Fact]
    public void The_real_startup_list_and_program_list_can_be_read()
    {
        var startup = _env.Startup();
        var programs = _env.InstalledPrograms();

        Assert.Equal(startup.Count, startup.Select(e => e.Id).Distinct().Count());
        Assert.All(startup, e => Assert.Contains(':', e.Id));
        Assert.NotEmpty(programs);
        Assert.Equal(programs.Count, programs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(_env.BrokenLinks(Path.Combine(_dir, "nessuna-cartella")));
    }

    /// <summary>The Recycle Bin for real, on a throwaway folder. It leaves one small item in the bin, so it runs only when asked: OPTLIFE_E2E=1.</summary>
    [Fact]
    public void A_folder_sent_to_the_bin_is_gone_from_its_place()
    {
        if (Environment.GetEnvironmentVariable("OPTLIFE_E2E") != "1") return;
        Write(@"optlife-prova-cestino\file.txt", 100);
        var folder = Path.Combine(_dir, "optlife-prova-cestino");

        var result = _env.Recycle([folder, Path.Combine(_dir, "mai-esistita")]);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("1 cartella spostata nel Cestino.", result.Detail);
        Assert.False(Directory.Exists(folder));
    }
}

public sealed class HubSortTests : IDisposable
{
    private const string Hub = @"C:\U\Desktop\Hub", Inbox = @"C:\U\Desktop\Hub\0 Da sistemare";
    private readonly Sandbox _box = new();
    private readonly FakeCleanupEnvironment _pc = new();
    private readonly CleanupModule _module;
    private static readonly string[] Sections = ["0 Da sistemare", "1 Giochi", "2 Emulatori", "5 Sistema e manutenzione"];

    public HubSortTests()
    {
        _module = new CleanupModule(_pc);
        _box.Config.UpdateModule("cleanup", m => m.Settings["tidy"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { $@"C:\U\Desktop | Hub | {Inbox}" }));
        _pc.Sections[Hub] = [.. Sections];
        _pc.Links[Inbox] =
        [
            new(@$"{Inbox}\Steam.lnk", "Steam", @"C:\Program Files (x86)\Steam\steam.exe"),
            new(@$"{Inbox}\RetroArch.lnk", "RetroArch", @"E:\SteamLibrary\steamapps\common\RetroArch\retroarch.exe"),
            new(@$"{Inbox}\Mystery.lnk", "Mystery", @"C:\Tools\thing.exe"),
        ];
    }

    public void Dispose() => _box.Dispose();

    private static IReadOnlyList<SortRule> Rules => [.. HubSort.Defaults.Select(SortRule.Parse).OfType<SortRule>()];

    [Fact]
    public void The_section_comes_from_what_the_shortcut_opens_and_is_sure_only_when_one_fits()
    {
        var sections = Sections.Skip(1).ToList();
        var steam = HubSort.Propose(new Shortcut("s", "Steam", @"C:\Steam\steam.exe"), sections, Rules);
        Assert.Equal(("1 Giochi", true), (steam.Section, steam.Sure));
        var retro = HubSort.Propose(new Shortcut("r", "RetroArch", @"E:\SteamLibrary\steamapps\common\RetroArch\retroarch.exe"), sections, Rules);
        Assert.Equal(("2 Emulatori", false), (retro.Section, retro.Sure)); // Emulator first, but it also lives in a Steam library.
        var blender = HubSort.Propose(new Shortcut("b", "Blender", @"E:SteamLibrarysteamappsmmonBlenderblender.exe"), [.. sections, "4 Sviluppo e progetti"], Rules);
        Assert.Equal("4 Sviluppo e progetti", blender.Section); // A tool before a Steam folder.
        var manga = HubSort.Propose(new Shortcut("w", "Suwayomi Launcher", @"C:Suwayomiauncher.exe"), [.. sections, "3 Manga - Suwayomi"], Rules);
        Assert.Equal(("3 Manga - Suwayomi", true), (manga.Section, manga.Sure)); // A section named after the program wins.
        Assert.Null(HubSort.Propose(new Shortcut("m", "Mystery", @"C:\Tools\thing.exe"), sections, Rules).Section);
        // Sections the Hub does not have are never proposed.
        Assert.Null(HubSort.Propose(new Shortcut("d", "Discord", @"C:\Discord\Update.exe"), sections, Rules).Section);
    }

    [Fact]
    public void Store_entertainment_apps_go_to_entertainment_by_name_or_package()
    {
        string[] sections = ["1 Games", "2 Entertainment", "5 Monitoring and utilities"];
        var tv = HubSort.Propose(new Shortcut("a", "TV", @"C:\Windows\explorer.exe shell:AppsFolder\AppleInc.AppleTV_nzyj5cx40ttqa!App"), sections, Rules);
        Assert.Equal(("2 Entertainment", true), (tv.Section, tv.Sure));
        Assert.Equal("2 Entertainment", HubSort.Propose(new Shortcut("c", "Crunchyroll", ""), sections, Rules).Section);
        Assert.Equal("2 Entertainment", HubSort.Propose(new Shortcut("y", "YouTube", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge_proxy.exe --app-id=abc"), sections, Rules).Section);
    }

    [Fact]
    public async Task Asking_by_default_a_finding_and_a_page_move_and_undo_one_shortcut()
    {
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Equal(3, _pc.Links[Inbox].Count); // Nothing moved by itself.
        var f = Assert.Single(_box.Store.GetFindings(), x => x.Key.StartsWith("hub|"));
        Assert.Equal(CleanupModule.SortHub, f.Fix!.ActionId);

        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "cleanup", CleanupModule.HubKind).MaxBy(e => e.Id)!.Data);
        var steam = board.Single(i => i.Label == "Steam");
        var one = _module.Actions.Single(a => a.Id == CleanupModule.SortOne);
        Assert.True((await _box.Runner.RunActionAsync(_module, one, "test", true, steam.Buttons!.Single().Argument, CancellationToken.None)).Ok);
        Assert.Single(_pc.Links[$@"{Hub}\1 Giochi"]);

        board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "cleanup", CleanupModule.HubKind).MaxBy(e => e.Id)!.Data);
        var undo = board.Single(i => i.Buttons?.Any(b => b.ActionId == CleanupModule.SortUndo) == true).Buttons!.Single();
        Assert.True((await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == CleanupModule.SortUndo), "test", true, undo.Argument, CancellationToken.None)).Ok);
        Assert.Equal(3, _pc.Links[Inbox].Count);
    }

    [Fact]
    public async Task Picking_another_section_and_automatic_mode_moving_only_the_sure_ones()
    {
        var pick = _module.Actions.Single(a => a.Id == CleanupModule.SortPick);
        var choices = _box.Runner.ChoicesOf(_module, pick, out _);
        Assert.Contains(choices, c => c.Category == "Mystery" && !c.Checked);
        Assert.Contains(choices, c => c.Id.EndsWith("Steam.lnk|1 Giochi") && c.Checked);
        await _box.Runner.RunActionAsync(_module, pick, "test", true, $@"{Inbox}\Mystery.lnk|5 Sistema e manutenzione", CancellationToken.None);
        Assert.Single(_pc.Links[$@"{Hub}\5 Sistema e manutenzione"]);

        _box.Config.UpdateModule("cleanup", m => m.Settings["hubSortMode"] = System.Text.Json.JsonSerializer.SerializeToElement(_module.Settings.Single(s => s.Key == "hubSortMode").Options![1]));
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Single(_pc.Links[$@"{Hub}\1 Giochi"]); // Steam: sure.
        Assert.Contains(_pc.Links[Inbox], s => s.Name == "RetroArch"); // Two sections possible: left for the person.
    }

    [Fact]
    public async Task Clean_the_desktop_moves_what_is_loose_and_suggests_at_once_with_an_activity()
    {
        _pc.Folders[@"C:\U\Desktop"] = ["Hub", "Nuovo gioco.lnk", "foto.png"];
        var clean = _module.Actions.Single(a => a.Id == CleanupModule.CleanDesktop);
        var result = await _box.Runner.RunActionAsync(_module, clean, "pannello", true, null, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.DoesNotContain(_pc.Folders[@"C:\U\Desktop"], n => n is "Nuovo gioco.lnk" or "foto.png");
        Assert.Contains("2", result.Detail);
        Assert.Contains("3", result.Detail); // The three shortcuts already waiting are counted with their suggestions.
        Assert.Equal(ActivityState.Done, _box.Store.GetActivities().Single().State);
        Assert.Contains(_module.Views, v => v.Menu == ViewMenu.Tools && v.Buttons!(_box.Runner.SettingsOf(_module)).Any(b => b.ActionId == CleanupModule.CleanDesktop));
    }

    [Fact]
    public async Task A_namesake_can_keep_the_hub_one_or_be_replaced_and_a_remembered_choice_applies_by_itself_next_time()
    {
        // An update recreates its own "RetroArch" shortcut next to the person's, which opens something else.
        _pc.Links[$@"{Hub}\2 Emulatori"] = [new(@$"{Hub}\2 Emulatori\RetroArch.lnk", "RetroArch", @"C:\Mio\Avvia RetroArch.vbs")];
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        var keepHub = _module.Actions.Single(a => a.Id == CleanupModule.TwinKeepHub);
        var useNew = _module.Actions.Single(a => a.Id == CleanupModule.TwinUseNew);
        Assert.True(keepHub.NeverAuto && useNew.NeverAuto);

        // Use the new one: the Hub's goes to the bin, the new one takes its place.
        Assert.True((await _box.Runner.RunActionAsync(_module, useNew, "test", true, @$"{Inbox}\RetroArch.lnk|2 Emulatori", CancellationToken.None)).Ok);
        Assert.Contains(@$"{Hub}\2 Emulatori\RetroArch.lnk", _pc.Recycled);
        Assert.Contains(_pc.Links[$@"{Hub}\2 Emulatori"], s => s.Path.EndsWith(@"2 Emulatori\RetroArch.lnk") && s.Target.Contains("retroarch.exe", StringComparison.OrdinalIgnoreCase));

        // It comes back again: this time choose "keep the Hub one" and remember it.
        _pc.Links[Inbox].Add(new(@$"{Inbox}\RetroArch.lnk", "RetroArch", @"C:\Emu\Nuovo\retroarch.exe"));
        var decide = _module.Actions.Single(a => a.Id == CleanupModule.TwinDecide);
        var choices = _box.Runner.ChoicesOf(_module, decide, out _);
        Assert.Equal(4, choices.Count);
        Assert.True(choices.Single(c => c.Id.EndsWith("|both")).Checked); // Nothing deleted unless ticked.
        var key = @$"{Inbox}\RetroArch.lnk|2 Emulatori";
        Assert.True((await _box.Runner.RunActionAsync(_module, decide, "test", true, $"{key}|hub;{key}|remember", CancellationToken.None)).Ok);
        Assert.Contains(@$"{Inbox}\RetroArch.lnk", _pc.Recycled);

        // The next update: handled by itself, as an activity, and the rule can be forgotten.
        _pc.Recycled.Clear();
        _pc.Links[Inbox].Add(new(@$"{Inbox}\RetroArch.lnk", "RetroArch", @"C:\Emu\Nuovo\retroarch.exe"));
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Equal([@$"{Inbox}\RetroArch.lnk"], _pc.Recycled);
        Assert.Contains(_box.Store.GetActivities(), a => a.Title.Contains("RetroArch") && a.State == ActivityState.Done);
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "cleanup", CleanupModule.HubKind).MaxBy(e => e.Id)!.Data);
        var rule = board.Single(i => i.Group == "Scelte ricordate");
        Assert.True((await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == CleanupModule.TwinForget), "test", true, rule.Buttons!.Single().Argument, CancellationToken.None)).Ok);
        board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "cleanup", CleanupModule.HubKind).MaxBy(e => e.Id)!.Data);
        Assert.DoesNotContain(board, i => i.Group == "Scelte ricordate");
    }

    [Fact]
    public async Task A_duplicate_in_the_section_goes_to_the_bin_and_a_namesake_is_kept_with_another_name()
    {
        // The games section already has Steam (same program) and a different "RetroArch".
        _pc.Links[$@"{Hub}\1 Giochi"] = [new(@$"{Hub}\1 Giochi\Steam.lnk", "Steam", @"C:\Program Files (x86)\Steam\steam.exe")];
        _pc.Links[$@"{Hub}\2 Emulatori"] = [new(@$"{Hub}\2 Emulatori\RetroArch.lnk", "RetroArch", @"C:\Emu\RetroArch\retroarch.exe")];
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "cleanup", CleanupModule.HubKind).MaxBy(e => e.Id)!.Data);

        var steam = board.Single(i => i.Label == "Steam").Buttons!.Single();
        Assert.Equal(CleanupModule.DropDuplicate, steam.ActionId);
        var drop = _module.Actions.Single(a => a.Id == CleanupModule.DropDuplicate);
        Assert.True(drop.NeverAuto);
        Assert.True((await _box.Runner.RunActionAsync(_module, drop, "test", true, steam.Argument, CancellationToken.None)).Ok);
        Assert.Contains(@$"{Inbox}\Steam.lnk", _pc.Recycled);

        var retroButtons = board.Single(i => i.Label == "RetroArch").Buttons!;
        Assert.Equal([CleanupModule.TwinKeepHub, CleanupModule.TwinUseNew, CleanupModule.SortRename, CleanupModule.TwinDecide], retroButtons.Select(b => b.ActionId));
        var retro = retroButtons.Single(b => b.ActionId == CleanupModule.SortRename);
        // Never to the bin when the two open different things.
        Assert.False((await _box.Runner.RunActionAsync(_module, drop, "test", true, retro.Argument, CancellationToken.None)).Ok);
        Assert.True((await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == CleanupModule.SortRename), "test", true, retro.Argument, CancellationToken.None)).Ok);
        Assert.Contains(_pc.Links[$@"{Hub}\2 Emulatori"], s => s.Path.EndsWith("RetroArch (2).lnk"));
    }

    [Fact]
    public async Task Shortcuts_that_open_nothing_are_offered_for_the_bin_and_go_by_themselves_when_asked_to()
    {
        _pc.BrokenIn[Hub] = [@$"{Hub}\1 Giochi\Old Game.lnk", @$"{Hub}\8 Chat\EarTrumpet.lnk"];
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        var f = Assert.Single(_box.Store.GetFindings(), x => x.Key.StartsWith("broken|"));
        Assert.Contains("EarTrumpet", f.Detail);
        Assert.Empty(_pc.Recycled); // "Ask" by default: only offered.
        Assert.True(_module.Actions.Single(a => a.Id == CleanupModule.RecycleBroken).NeverAuto);

        _box.Config.UpdateModule("cleanup", m => m.Settings["brokenMode"] = System.Text.Json.JsonSerializer.SerializeToElement(_module.Settings.Single(s => s.Key == "brokenMode").Options![1]));
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Equal(2, _pc.Recycled.Count);
        Assert.DoesNotContain(_box.Store.GetFindings(), x => x.Key.StartsWith("broken|") && x.Status == FindingStatus.Open);
        var a = _box.Store.GetActivities().Single();
        Assert.Equal(("auto", ActivityState.Done), (a.Actor, a.State)); // In Activity, as done by itself (not "by the Scheduler").
    }
}
