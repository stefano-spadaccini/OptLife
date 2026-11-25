using System.Globalization;
using OptLife.Core;
using OptLife.Modules.FirstSetup;

namespace OptLife.Tests;

public class FirstSetupTests
{
    private static readonly HashSet<Use> Everything = [Use.Music, Use.Games, Use.Study];

    [Fact]
    public void Drivers_follow_the_hardware_and_come_first()
    {
        var amdNvidia = Catalog.For(new Hardware(Vendor.Amd, [Vendor.Nvidia]), Everything, []);
        Assert.Equal(Catalog.WindowsUpdate, amdNvidia[0].Id);
        Assert.Contains(amdNvidia, a => a.Id == "amd-chipset");
        Assert.Contains(amdNvidia, a => a.Id == "nvidia-app");
        Assert.DoesNotContain(amdNvidia, a => a.Id is "intel-dsa" or "amd-adrenalin");
        Assert.True(amdNvidia.Select(a => a.Step).SequenceEqual(amdNvidia.Select(a => a.Step).Order()));

        var intelRadeon = Catalog.For(new Hardware(Vendor.Intel, [Vendor.Amd]), Everything, []);
        Assert.Contains(intelRadeon, a => a.Id == "intel-dsa");
        Assert.Contains(intelRadeon, a => a.Id == "amd-adrenalin");
        Assert.DoesNotContain(intelRadeon, a => a.Id is "amd-chipset" or "nvidia-app");
    }

    [Fact]
    public void Uses_switched_off_and_skipped_apps_leave_the_list()
    {
        var list = Catalog.For(new Hardware(Vendor.Amd, [Vendor.Nvidia]), new HashSet<Use> { Use.Music }, ["chrome"]);
        Assert.Contains(list, a => a.Id == "latencymon");
        // Every section stays, but an area not chosen recommends nothing.
        Assert.All(list.Where(a => a.Use is Use.Games or Use.Study), a => Assert.Equal(Tier.Optional, a.Tier));
        Assert.Equal(Tier.Recommended, list.Single(a => a.Id == "reaper").Tier);
        Assert.DoesNotContain(list, a => a.Id == "chrome");
        Assert.Contains(list, a => a.Id == "7zip");
    }

    [Fact]
    public void Three_tiers_and_the_tools_a_use_needs_become_recommended_with_it()
    {
        var none = Catalog.For(new Hardware(Vendor.Amd, [Vendor.Nvidia]), new HashSet<Use>(), []);
        Assert.Equal(Tier.Optional, none.Single(a => a.Id == "rtss").Tier);
        Assert.Equal(Tier.Optional, none.Single(a => a.Id == "latencymon").Tier);
        var games = Catalog.For(new Hardware(Vendor.Amd, [Vendor.Nvidia]), new HashSet<Use> { Use.Games }, []);
        Assert.Equal(Tier.Recommended, games.Single(a => a.Id == "rtss").Tier);
        // One browser and one password manager proposed; the alternatives are not ticked.
        Assert.Equal(["firefox"], none.Where(a => Catalog.KindOf(a) == "Browser" && a.Essential).Select(a => a.Id));
        Assert.Equal(Tier.Recommended, none.Single(a => a.Id == "bitwarden").Tier);
        Assert.Equal(Tier.Optional, none.Single(a => a.Id == "proton-pass").Tier);
        Assert.Equal(Tier.Essential, none.Single(a => a.Id == "7zip").Tier);
        Assert.Equal(Tier.Optional, none.Single(a => a.Id == "fluentflyout").Tier);
    }

    [Fact]
    public void Installed_programs_are_recognised_by_name()
    {
        var steam = Catalog.All.Single(a => a.Id == "steam");
        Assert.True(Catalog.IsInstalled(steam, ["Steam"]));
        Assert.False(Catalog.IsInstalled(steam, ["Steamworks Common Redistributables"]));
        var zip = Catalog.All.Single(a => a.Id == "7zip");
        Assert.True(Catalog.IsInstalled(zip, ["7-Zip 26.06 (x64)"]));
        var dsa = Catalog.All.Single(a => a.Id == "intel-dsa");
        Assert.True(Catalog.IsInstalled(dsa, ["Intel® Driver & Support Assistant"]));
    }

    [Fact]
    public void The_board_shows_installed_missing_and_optional()
    {
        var hw = new Hardware(Vendor.Amd, [Vendor.Nvidia]);
        var apps = Catalog.For(hw, Everything, []);
        var board = FirstSetupModule.Board(apps, hw, ["Steam", "Spotify"]);
        Assert.Equal(CheckStatus.Ok, board.Single(i => i.Label == "Steam").Status);
        var discord = board.Single(i => i.Label == "Discord");
        Assert.Equal(CheckStatus.Info, discord.Status);   // Recommended and missing: said in words, no warning colour.
        Assert.Equal("CONSIGLIATA", discord.StatusText);
        Assert.Equal(CheckStatus.Info, board.Single(i => i.Label == "Epic Games Launcher").Status);
        Assert.Contains("AMD", board[0].Value);
        Assert.Contains("NVIDIA", board[0].Value);
    }

    [Fact]
    public void Every_app_has_an_official_page_and_a_unique_id()
    {
        Assert.Equal(Catalog.All.Count, Catalog.All.Select(a => a.Id).Distinct().Count());
        Assert.All(Catalog.All, a => Assert.True(a.Url.StartsWith("https://") || a.Url.StartsWith("ms-settings:")));
        Assert.All(Catalog.All, a => Assert.InRange(a.Step, 0, Catalog.Steps.Length - 1));
    }
}

public class LanguageTests
{
    [Theory]
    [InlineData("auto", "it-IT", "it")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData(null, "it-IT", "it")]
    [InlineData("en", "it-IT", "en")]
    [InlineData("it", "en-US", "it")]
    public void The_language_follows_windows_unless_forced(string? setting, string windows, string expected) =>
        Assert.Equal(expected, L.Resolve(setting, CultureInfo.GetCultureInfo(windows)));
}
