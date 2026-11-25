using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Updates;

namespace OptLife.Tests;

internal sealed class FakeUpdatesEnvironment : IUpdatesEnvironment
{
    public IReadOnlyList<AppUpdate>? Apps { get; set; } = [];
    public IReadOnlyList<WindowsUpdateItem>? Windows { get; set; } = [];
    public bool Preview { get; set; }
    public GraphicsDriver? Card { get; set; }
    public Dictionary<string, InstalledProgram> Programs { get; } = [];
    public BiosInfo? BiosInfo { get; set; }
    public List<string> Upgraded { get; } = [];
    public List<string> Opened { get; } = [];
    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<AppUpdate>?> WingetUpgrades(CancellationToken ct)
    {
        ListCalls++;
        return Task.FromResult(Apps);
    }

    /// <summary>winget's answer for an id (none: 0, updated); the fake list loses the program when it is updated.</summary>
    public Dictionary<string, int> Codes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Hangs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StaysListed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Idle { get; set; }
    public List<(string Id, AppScope Scope)> Scopes { get; } = [];

    public Task<UpgradeAttempt> WingetUpgrade(string id, AppScope scope, TimeSpan limit, CancellationToken ct)
    {
        Upgraded.Add(id);
        if (Brings.TryGetValue(id, out var extra)) Installed.Add(extra);
        Scopes.Add((id, scope));
        if (Hangs.Contains(id)) return Task.FromResult(new UpgradeAttempt(null, true, null));
        var code = Codes.GetValueOrDefault(id);
        if (code == 0 && !StaysListed.Contains(id) && Apps is { } apps) Apps = [.. apps.Where(a => !a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))];
        return Task.FromResult(new UpgradeAttempt(code, false, null));
    }

    public AppScope ScopeOf(AppUpdate app) => app.Id.StartsWith("User.") ? AppScope.User : AppScope.Machine;
    public int IdleMinutes() => Idle;

    public Dictionary<string, List<string>> Deps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Installed { get; } = [];
    /// <summary>Programs an update installs along with itself (what winget did with iTunes).</summary>
    public Dictionary<string, string> Brings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<IReadOnlyList<string>?> PackageDependencies(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(Deps.TryGetValue(id, out var d) ? d : []);
    public IReadOnlyList<string> InstalledNames() => [.. Installed];

    public Task<IReadOnlyList<WindowsUpdateItem>?> WindowsUpdates(CancellationToken ct) => Task.FromResult(Windows);
    public bool PreviewUpdatesOn() => Preview;
    public GraphicsDriver? Graphics() => Card;
    public InstalledProgram? Program(string namePart) => Programs.GetValueOrDefault(namePart);
    public BiosInfo? Bios() => BiosInfo;
    public ActionResult Open(string target) { Opened.Add(target); return new ActionResult(true, ""); }
}

public sealed class UpdatesTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly FakeUpdatesEnvironment _env = new();
    private readonly UpdatesModule _module;

    public UpdatesTests()
    {
        _module = new UpdatesModule(_env);
        _box.Clock.Now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    }

    public void Dispose() => _box.Dispose();

    private async Task Round(bool onDemand = false) => Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None, onDemand));

    /// <summary>The agent's side: what is queued gets updated.</summary>
    private async Task Queue() => Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[1], CancellationToken.None));

    private IReadOnlyList<StoredFinding> Open() => [.. _box.Store.GetFindings().Where(f => f.Module == "updates" && f.Status == FindingStatus.Open)];

    private IReadOnlyList<CheckItem> Board() =>
        CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "updates", kind: UpdatesModule.BoardKind).MaxBy(e => e.Time)!.Data);

    private const string Sample = """
        Name                                             Id                                     Version         Available
        -----------------------------------------------------------------------------------------------------------------------
        Battle.net                                       Blizzard.BattleNet                     Unknown         2.0.0.4232
        Git                                              Git.Git                                2.56.0.4        2.56.1
        HWiNFO® 64                                       REALiX.HWiNFO                          8.54            8.60
        Ubisoft Connect                                  Ubisoft.Connect                        < 175.2.0.13423 175.2.0.13423
        Unity 6000.3.2f1                                 Unity.Unity.6000                       6000.3.2f1      6000.6.10f1
        29 upgrades available.

        The following packages have an upgrade available, but require explicit targeting for upgrade:
        Name    Id        Version Available
        ---------------------------------------
        Pinned  Pin.Ned   1.0     2.0
        """;

    [Fact]
    public void The_winget_table_is_read_by_its_columns()
    {
        var apps = UpdatesRules.ParseWinget("\r  - \r  \\ \r" + Sample.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Equal(5, apps.Count);
        Assert.Equal(new AppUpdate("HWiNFO® 64", "REALiX.HWiNFO", "8.54", "8.60"), apps[2]);
        Assert.Equal("< 175.2.0.13423", apps[3].Installed);
        Assert.False(apps[0].KnownVersion);
        Assert.False(apps[3].KnownVersion);
        Assert.True(apps[1].KnownVersion);
        Assert.DoesNotContain(apps, a => a.Id == "Pin.Ned");
        Assert.Empty(UpdatesRules.ParseWinget("No installed package found matching input criteria."));
    }

    [Theory]
    [InlineData("32.0.16.2124", "621.24")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.5222", "552.22")]
    public void Nvidia_numbers_come_from_windows_numbers(string windows, string nvidia) =>
        Assert.Equal(nvidia, UpdatesRules.NvidiaVersion(windows));

    [Fact]
    public async Task Programs_to_update_are_one_problem_with_an_update_all_button_and_the_excluded_ones_stay_out()
    {
        _env.Apps = UpdatesRules.ParseWinget(Sample);
        _box.Config.UpdateModule("updates", m => m.Settings["exclude"] = JsonSerializer.SerializeToElement(new[] { "Unity.Unity.6000" }));
        await Round();

        var f = Assert.Single(Open(), f => f.Key.StartsWith("apps|"));
        Assert.StartsWith("2 ", f.Title); // Git and HWiNFO: not the unknown versions, not Unity.
        Assert.Equal("Git.Git;REALiX.HWiNFO", f.Fix!.Argument);

        var board = Board();
        Assert.Equal(CheckStatus.Watch, board.Single(i => i.Label == "Git").Status);
        Assert.Equal(CheckStatus.Info, board.Single(i => i.Label == "Unity 6000.3.2f1").Status);
        Assert.Equal(CheckStatus.Info, board.Single(i => i.Label == "Battle.net").Status);

        // The pick list offers only what OptLife really updates, all ticked: never the excluded or self-updating ones.
        var choices = _box.Runner.ChoicesOf(_module, _module.Actions.Single(a => a.Id == UpdatesRules.Upgrade), out _);
        Assert.Equal(["Git.Git", "REALiX.HWiNFO"], choices.Select(c => c.Id).Order());
        Assert.All(choices, c => Assert.True(c.Checked));

        var one = _module.Actions.Single(a => a.Id == UpdatesRules.UpgradeOne);
        var result = await _box.Runner.RunActionAsync(_module, one, "test", confirmed: true, f.Fix.Argument, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Empty(_env.Upgraded); // Only queued: the agent does it.
        await Queue();
        Assert.Equal(["Git.Git", "REALiX.HWiNFO"], _env.Upgraded);
        Assert.DoesNotContain(Open(), x => x.Key.StartsWith("apps|"));
        Assert.Equal(2, _box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "updates", kind: "app_updated").Count);
    }

    [Fact]
    public async Task Winget_is_asked_at_most_every_few_hours_unless_the_person_asks()
    {
        await Round();
        _box.Clock.Now += TimeSpan.FromHours(1);
        await Round();
        Assert.Equal(1, _env.ListCalls);
        await Round(onDemand: true);
        Assert.Equal(2, _env.ListCalls);
        _box.Clock.Now += TimeSpan.FromHours(13);
        await Round();
        Assert.Equal(3, _env.ListCalls);
    }

    [Fact]
    public async Task Automatic_mode_updates_only_what_is_safe_to()
    {
        _env.Apps = UpdatesRules.ParseWinget(Sample);
        _box.Config.UpdateModule("updates", m =>
        {
            m.Settings["updates"] = JsonSerializer.SerializeToElement(_module.Settings.Single(s => s.Key == "updates").Options![1]);
            m.Settings["exclude"] = JsonSerializer.SerializeToElement(new[] { "HWiNFO", "Unity" });
        });
        _env.Idle = 60; // Away from the PC long enough.
        await Round();
        await Queue();
        Assert.Equal(["Git.Git"], _env.Upgraded);
        Assert.DoesNotContain(Open(), x => x.Key.StartsWith("apps|"));
    }

    [Fact]
    public async Task Preview_updates_on_is_a_problem_that_opens_windows_update()
    {
        _env.Preview = true;
        await Round();
        var f = Assert.Single(Open(), f => f.Key == "preview");
        Assert.Contains("anteprima", f.Advice);
        Assert.Equal(UpdatesRules.OpenSite, f.Fix!.ActionId);
        Assert.Equal(CheckStatus.Wrong, Board()[0].Status);

        var open = _module.Actions.Single(a => a.Id == UpdatesRules.OpenSite);
        Assert.True((await _box.Runner.RunActionAsync(_module, open, "test", true, f.Fix.Argument, CancellationToken.None)).Ok);
        Assert.False((await _box.Runner.RunActionAsync(_module, open, "test", true, "C:\\Windows\\System32\\cmd.exe", CancellationToken.None)).Ok);
        Assert.Equal([UpdatesRules.WindowsUpdatePage], _env.Opened);
    }

    [Fact]
    public async Task Drivers_and_bios_are_only_read_with_a_link_and_old_ones_are_flagged()
    {
        _env.Card = new GraphicsDriver("NVIDIA", "NVIDIA GeForce RTX 4070", "32.0.16.2124", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        _env.Programs["AMD Chipset Software"] = new InstalledProgram("AMD Chipset Software", "7.06.02.123", new DateTimeOffset(2025, 6, 20, 0, 0, 0, TimeSpan.Zero));
        _env.BiosInfo = new BiosInfo("ASUSTeK COMPUTER INC.", "ROG STRIX X570-F GAMING", "3607", new DateTimeOffset(2024, 3, 18, 0, 0, 0, TimeSpan.Zero));
        _env.Windows = [new WindowsUpdateItem("Intel - System - 1.2.3", Driver: true, Optional: true)];
        await Round();

        var board = Board();
        var gpu = board.Single(i => i.Label.StartsWith("Driver video"));
        Assert.StartsWith("621.24", gpu.Value);
        Assert.Equal(CheckStatus.Ok, gpu.Status);
        Assert.Equal(CheckStatus.Watch, board.Single(i => i.Label == "AMD chipset").Status);
        var bios = board.Single(i => i.Label.StartsWith("BIOS"));
        Assert.Equal(CheckStatus.Watch, bios.Status);
        Assert.StartsWith("https://rog.asus.com/motherboards/rog-strix/", bios.Buttons![0].Argument);   // The board's own BIOS page.
        Assert.DoesNotContain(bios.Buttons, b => b.ActionId is not (UpdatesRules.OpenSite or UpdatesRules.RestartBios)); // A link, and the restart into it: never an install.

        var open = Open();
        Assert.Contains(open, f => f.Key.StartsWith("driver|amd chipset|"));
        Assert.DoesNotContain(open, f => f.Key.Contains("bios", StringComparison.OrdinalIgnoreCase)); // The BIOS is never a problem.
        Assert.Contains(open, f => f.Key.StartsWith("windows|") && f.Fix!.Argument == UpdatesRules.OptionalPage);
    }

    [Fact]
    public async Task Emulator_updates_come_from_the_history_when_the_emulation_page_is_there()
    {
        await Round();
        Assert.DoesNotContain(Board(), i => i.Group == UpdatesRules.EmulatorsGroup);

        _box.Store.InsertEvents("emulation", [new NewEvent(_box.Clock.Now, "emulation_board", Severity.Info, "board", "emulation_board",
            Data: CheckItem.ToData([
                new CheckItem("PS3", "RPCS3", "Aggiornamento disponibile: 0.0.43", CheckStatus.Watch),
                new CheckItem("PS2", "PCSX2", "Installato · 2.8.0", CheckStatus.Ok),
            ]))]);
        _box.Clock.Now += TimeSpan.FromMinutes(30);
        await Round();
        var emu = Assert.Single(Board(), i => i.Group == UpdatesRules.EmulatorsGroup);
        Assert.Equal("RPCS3 · PS3", emu.Label);
    }

    [Fact]
    public async Task A_run_says_app_by_app_how_it_went_and_one_failure_does_not_make_it_all_failed()
    {
        _env.Apps =
        [
            new AppUpdate("Git", "Git.Git", "1", "2"), new AppUpdate("Epic Online Services", "EpicGames.EpicOnlineServices", "4.2", "4.3"),
            new AppUpdate("Unity Hub", "Unity.UnityHub", "3.2", "4.0"), new AppUpdate("Tool", "User.Tool", "1", "2"),
        ];
        _env.Codes["EpicGames.EpicOnlineServices"] = unchecked((int)0x8A15008E);
        _env.Hangs.Add("Unity.UnityHub");
        await Round();
        await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == UpdatesRules.Upgrade), "pannello", true,
            "Git.Git;EpicGames.EpicOnlineServices;Unity.UnityHub;User.Tool", CancellationToken.None);
        await Queue();

        var run = _box.Store.GetActivities().Single(a => a.Title.Contains('4'));
        Assert.Equal(ActivityState.Failed, run.State);
        Assert.StartsWith("Riusciti: 2 · Non riusciti: 1 · Da controllare: 1.", run.Detail);
        Assert.Contains("8A15008E", run.Detail);
        Assert.Contains("finestra", run.Detail); // The installer that waited on a window.
        Assert.Contains(("User.Tool", AppScope.User), _env.Scopes); // Installed for the person: run as the person.
        Assert.Contains(("Git.Git", AppScope.Machine), _env.Scopes); // For the whole PC: by the agent, no UAC.

        var last = Board().Where(i => i.Group == UpdatesRules.LastRunGroup).ToList();
        Assert.Equal("DA CONTROLLARE", last.Single(i => i.Label == "Unity Hub").StatusText);
        Assert.Equal("NON RIUSCITO", last.Single(i => i.Label == "Epic Online Services").StatusText);
    }

    [Fact]
    public async Task Programs_that_update_themselves_or_stay_listed_are_put_aside_and_never_offered_again()
    {
        _env.Apps = [new AppUpdate("Logitech G HUB", "Logitech.GHUB", "2026.6", "2026.7"), new AppUpdate("Thing", "Maker.Thing", "1", "2")];
        _env.StaysListed.Add("Maker.Thing"); // winget says done, the version does not change.
        await Round();
        Assert.Equal(UpdatesRules.SelfGroup, Board().Single(i => i.Label == "Logitech G HUB").Group);
        await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == UpdatesRules.UpgradeOne), "pannello", true, "Maker.Thing", CancellationToken.None);
        await Queue();
        // First time: said plainly that it did not update, not put aside yet.
        Assert.Equal("NON RIUSCITO", Board().Single(i => i.Label == "Thing" && i.Group == UpdatesRules.LastRunGroup).StatusText);
        Assert.Equal(ActivityState.Failed, _box.Store.GetActivities()[0].State);
        await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == UpdatesRules.UpgradeOne), "pannello", true, "Maker.Thing", CancellationToken.None);
        await Queue();
        Assert.Equal(UpdatesRules.SelfGroup, Board().Single(i => i.Label == "Thing" && i.Group != UpdatesRules.LastRunGroup).Group);
        Assert.DoesNotContain(Open(), f => f.Key.StartsWith("apps|"));
    }

    [Fact]
    public async Task Nothing_starts_by_itself_while_the_pc_is_in_use()
    {
        _env.Apps = [new AppUpdate("Git", "Git.Git", "1", "2")];
        _box.Config.UpdateModule("updates", m => m.Settings["updates"] = JsonSerializer.SerializeToElement(_module.Settings.Single(s => s.Key == "updates").Options![1]));
        _env.Idle = 2;
        await Round();
        await Queue();
        Assert.Empty(_env.Upgraded);
    }

    [Theory]
    [InlineData(0, false, UpdatesRules.Outcome.Done)]
    [InlineData(unchecked((int)0x8A150109), false, UpdatesRules.Outcome.Reboot)]
    [InlineData(unchecked((int)0x8A150101), false, UpdatesRules.Outcome.Attention)]
    [InlineData(unchecked((int)0x8A150006), false, UpdatesRules.Outcome.Failed)]
    [InlineData(null, true, UpdatesRules.Outcome.Attention)]
    public void Winget_answers_become_words(int? code, bool timedOut, UpdatesRules.Outcome expected) =>
        Assert.Equal(expected, UpdatesRules.Explain(code, timedOut, 15).Outcome);

    /// <summary>23 Nov 2026, 01:38: "Updated: 2" in 3 seconds, and winget still listed both on the old version.</summary>
    [Fact]
    public async Task Winget_saying_done_is_checked_against_the_list_afterwards()
    {
        _env.Apps = [new AppUpdate("Epic Online Services", "EpicGames.EpicOnlineServices", "4.2.1", "4.3.1")];
        _env.Codes["EpicGames.EpicOnlineServices"] = unchecked((int)0x8A15002B); // "Nothing to update", as winget answered.
        _env.StaysListed.Add("EpicGames.EpicOnlineServices");
        await Round();
        await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == UpdatesRules.UpgradeOne), "pannello", true, "EpicGames.EpicOnlineServices", CancellationToken.None);
        await Queue();
        var run = _box.Store.GetActivities()[0];
        Assert.Equal(ActivityState.Failed, run.State);
        Assert.StartsWith("Riusciti: 0 · Non riusciti: 1", run.Detail);
        Assert.Contains("4.2.1", run.Detail);
        Assert.Empty(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "updates", "app_updated"));
        var line = Board().Single(i => i.Group == UpdatesRules.LastRunGroup && i.Label == "Epic Online Services");
        Assert.Contains("8A15002B", line.Value); // The technical detail is there, below the words.
    }

    [Fact]
    public void Package_dependencies_are_read_from_winget_show()
    {
        const string show = "Installer:\n  Installer Type: nullsoft\n  Dependencies: \n    - Package Dependencies: \n        Apple.iTunes\n        Microsoft.VCRedist.2015+.x64\nOther: x\n";
        Assert.Equal(["Apple.iTunes", "Microsoft.VCRedist.2015+.x64"], Dependencies.Parse(show));
        Assert.Equal(["Apple.iTunes"], Dependencies.Missing(Dependencies.Parse(show), ["Git", "7-Zip"])); // Microsoft runtimes do not count.
        Assert.Empty(Dependencies.Missing(["Apple.iTunes"], ["iTunes"]));
    }

    /// <summary>23 Nov 2026, 00:17: updating iloader installed iTunes, which the user had removed on purpose.</summary>
    [Fact]
    public async Task An_update_that_would_install_another_program_is_not_done_and_is_announced_on_the_page()
    {
        _env.Apps = [new AppUpdate("iloader", "nabdev.iloader", "2.4.1", "2.4.2"), new AppUpdate("Git", "Git.Git", "1", "2")];
        _env.Deps["nabdev.iloader"] = ["Apple.iTunes"];
        _env.Brings["Git.Git"] = "Something Extra";
        await Round();
        var row = Board().Single(i => i.Label == "iloader");
        Assert.Contains("iTunes", row.Advice);
        Assert.DoesNotContain("nabdev.iloader", Open().Single(f => f.Key.StartsWith("apps|")).Fix!.Argument); // Not in "Update all".

        await _box.Runner.RunActionAsync(_module, _module.Actions.Single(a => a.Id == UpdatesRules.UpgradeOne), "pannello", true, "nabdev.iloader;Git.Git", CancellationToken.None);
        await Queue();
        Assert.Equal(["Git.Git"], _env.Upgraded); // iloader not touched.
        var run = _box.Store.GetActivities()[0].Detail!;
        Assert.Contains("iTunes", run);
        Assert.Contains("Something Extra", run); // What Git's update brought along is said.
    }
}

public sealed class ChipsetPageTests
{
    private const string Generic = "https://www.amd.com/en/support/download/drivers.html";

    /// <summary>Addresses checked on 24 Nov 2026: each answers with that chipset's (or processor's) own page.</summary>
    [Theory]
    [InlineData("ROG STRIX X570-F GAMING", "AMD Ryzen 7 5800X 6-Core Processor", "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am4/x570.html")]
    [InlineData("MAG X670E TOMAHAWK WIFI", null, "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/x670e.html")]
    [InlineData("B650M AORUS ELITE AX", null, "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/b650.html")]
    [InlineData("X570S AERO G", null, "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am4/x570.html")]
    [InlineData("TRX50 AERO D", null, "https://www.amd.com/en/support/downloads/drivers.html/chipsets/str5/trx50.html")]
    // A laptop: no desktop chipset in the board's name, the processor's own page.
    [InlineData("8A4F", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "https://www.amd.com/en/support/downloads/drivers.html/processors/ryzen/ryzen-7000-series/amd-ryzen-7-7840hs.html")]
    [InlineData("Unknown board", "Some processor", Generic)]
    public void The_amd_chipset_button_opens_the_page_of_this_pc(string board, string? cpu, string expected) =>
        Assert.Equal(expected, UpdatesRules.DriverSite(Generic, new BiosInfo("Maker", board, "1", null, cpu)));

    [Fact]
    public void A_page_the_person_chose_is_kept_and_intel_goes_to_its_detection_tool()
    {
        var pc = new BiosInfo("ASUS", "ROG STRIX X570-F GAMING", "1", null);
        Assert.Equal("https://example.org/mine", UpdatesRules.DriverSite("https://example.org/mine", pc));
        Assert.Equal(UpdatesRules.IntelDetect, UpdatesRules.DriverSite("https://www.intel.com/content/www/us/en/download-center/home.html", pc));
    }
}

public sealed class BiosPageTests
{
    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG STRIX X570-F GAMING", "https://rog.asus.com/motherboards/rog-strix/rog-strix-x570-f-gaming-model/helpdesk_bios/")]
    [InlineData("ASUSTeK COMPUTER INC.", "PRIME B650-PLUS", "https://www.asus.com/supportonly/PRIME%20B650-PLUS/helpdesk_bios/")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B550 TOMAHAWK (MS-7C91)", "https://www.msi.com/Motherboard/MAG-B550-TOMAHAWK/support#bios")]
    [InlineData("Gigabyte Technology Co., Ltd.", "B550 AORUS ELITE", "https://www.gigabyte.com/Search?kw=B550%20AORUS%20ELITE")]
    [InlineData("ASRock", "B550 Steel Legend", "https://www.asrock.com/mb/AMD/B550%20Steel%20Legend/index.asp#BIOS")]
    [InlineData("ASRock", "Z790 Pro RS", "https://www.asrock.com/mb/Intel/Z790%20Pro%20RS/index.asp#BIOS")]
    [InlineData("Biostar", "B550MH", "https://duckduckgo.com/?q=Biostar%20B550MH%20BIOS")]
    public void The_bios_page_is_the_boards_own(string maker, string board, string expected) =>
        Assert.Equal(expected, OptLife.Modules.Updates.UpdatesRules.BiosPage(maker, board));
}
