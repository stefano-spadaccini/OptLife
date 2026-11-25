using System.Text.RegularExpressions;
using OptLife.Core;
using OptLife.Modules.Emulation;

namespace OptLife.Tests;

internal sealed class FakeEmulationEnvironment : IEmulationEnvironment
{
    public Dictionary<string, string> Exes { get; } = new(StringComparer.OrdinalIgnoreCase);   // pattern → path
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Release> Releases { get; } = [];
    public List<string> Downloads { get; } = [];
    public List<string> Opened { get; } = [];
    public List<string> Folders { get; } = [];

    public string? FindFile(string folder, string pattern) => Exes.GetValueOrDefault(pattern);
    public bool Exists(string pathPattern) => Files.Contains(pathPattern);
    public IReadOnlyList<string> InstalledNames() => [];
    public string? Version(string exe) => "2.8.0";
    public Task<Release?> Latest(string repo, string assetPattern, CancellationToken ct) => Task.FromResult(Releases.GetValueOrDefault(repo));
    public Task<ActionResult> DownloadInto(string url, string folder, CancellationToken ct)
    {
        Downloads.Add($"{url} → {folder}");
        return Task.FromResult(new ActionResult(true, ""));
    }
    public Task<ActionResult> Winget(string id, CancellationToken ct) => Task.FromResult(new ActionResult(true, ""));
    public ActionResult Open(string target) { Opened.Add(target); return new ActionResult(true, ""); }
    public void CreateFolder(string path) => Folders.Add(path);
}

public sealed class EmulationTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void The_catalogue_is_complete_and_official()
    {
        var all = EmuCatalog.All;
        Assert.True(all.Count >= 18);
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        var emulators = all.SelectMany(c => c.Emulators).ToList();
        Assert.Equal(emulators.Count, emulators.Select(e => e.Id).Distinct().Count());
        Assert.All(all, c => Assert.NotEmpty(c.Emulators));
        Assert.All(emulators, e => Assert.StartsWith("https://", e.Source.Site));
        Assert.All(emulators.Where(e => e.Source.GitHub is not null), e => _ = new Regex(e.Source.Asset!));
        // Console files: only official makers' pages or guides, never a download of the files themselves.
        Assert.All(all.SelectMany(c => c.Files), f => Assert.True(f.Official is null || f.Official.StartsWith("https://www.playstation.com/")));
        Assert.Null(EmuCatalog.Find("ryujinx")!.Value.Emulator.Source.GitHub);
    }

    [Theory]
    [InlineData("v2.8.2", "2.8.0", null, true)]
    [InlineData("v2.8.2", "2.8.2", null, false)]
    [InlineData("build-abc", "0.0.37", null, false)]   // A commit build says nothing by itself...
    [InlineData("build-abc", "0.0.37", "build-old", true)] // ...but against what OptLife installed it does.
    [InlineData("v2.8.2", null, "v2.8.2", false)]
    public void Updates_are_found_only_when_they_can_be_told(string latest, string? installed, string? tag, bool expected) =>
        Assert.Equal(expected, EmulationModule.IsNewer(latest, installed, tag));

    [Fact]
    public async Task The_page_shows_emulators_updates_and_missing_console_files_and_installs_in_place()
    {
        var env = new FakeEmulationEnvironment();
        env.Exes["pcsx2*.exe"] = @"C:\Software\Consoles\PlayStation\PCSX2\pcsx2-qt.exe";
        env.Releases["PCSX2/pcsx2"] = new Release("v2.8.2", "https://example/pcsx2.7z");
        var module = new EmulationModule(env);
        _box.Config.UpdateModule("emulation", m => m.Settings["games"] = System.Text.Json.JsonSerializer.SerializeToElement(@"E:\Games\Emulazione"));
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true));

        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.GetUtcNow().AddDays(-1), _box.Clock.GetUtcNow().AddDays(1), "emulation", EmulationModule.BoardKind).Single().Data);
        var pcsx2 = board.Single(i => i.Label == "PCSX2");
        Assert.Equal(CheckStatus.Watch, pcsx2.Status);
        Assert.Contains(pcsx2.Buttons!, b => b.ActionId == EmulationModule.InstallOne);
        var bios = board.Single(i => i.Label == "BIOS PS2");
        Assert.Equal(CheckStatus.Wrong, bios.Status); // PCSX2 is there, its BIOS is not.
        Assert.Equal(CheckStatus.Info, board.Single(i => i.Label == "DuckStation").Status);
        Assert.Contains(board.Single(i => i.Label == "Firmware PS3").Buttons!, b => b.ActionId == EmulationModule.OpenOfficial);

        var result = await _box.Runner.RunActionAsync(module, module.Actions.Single(a => a.Id == EmulationModule.InstallOne), "test", true, "pcsx2", CancellationToken.None);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(@"https://example/pcsx2.7z → C:\Software\Consoles\PlayStation\PCSX2", Assert.Single(env.Downloads));
        Assert.Contains(@"E:\Games\Emulazione\PS2", env.Folders);
        Assert.Contains(@"E:\Games\Emulazione\BIOS\PS2", env.Folders);
    }
}

public class DnsRuleTests
{
    [Fact]
    public void The_dns_is_judged_against_the_chosen_servers_with_a_fix()
    {
        var reading = OptLife.Modules.Setup.SetupRules.Dns("Wi-Fi", ["1.1.1.1", "8.8.8.8"]);
        Assert.Same(reading, OptLife.Modules.Setup.SetupRules.JudgeDns(reading, []));          // Nothing chosen: no judgement.
        var right = OptLife.Modules.Setup.SetupRules.JudgeDns(reading, ["1.1.1.1", "8.8.8.8"]);
        Assert.True(right.Good);
        Assert.Null(right.Problem);
        var wrong = OptLife.Modules.Setup.SetupRules.JudgeDns(OptLife.Modules.Setup.SetupRules.Dns("Wi-Fi", []), ["1.1.1.1", "8.8.8.8"]);
        Assert.False(wrong.Good);
        Assert.NotNull(wrong.Problem);
        Assert.Equal("Wi-Fi|1.1.1.1,8.8.8.8", wrong.Fix!.Argument);
    }
}
