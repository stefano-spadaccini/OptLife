using OptLife.Core;
using OptLife.Modules.Space;
using OptLife.Modules.Wifi;

namespace OptLife.Tests;

internal sealed class FakeSpaceEnvironment : ISpaceEnvironment
{
    public Dictionary<string, List<TempItem>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BinInfo> Bins { get; } = [];
    public List<string> Recycled { get; } = [];
    public List<string> Opened { get; } = [];

    public IReadOnlyList<TempItem>? Temp(string folder) => Folders.TryGetValue(folder, out var l) ? [.. l] : null;
    public IReadOnlyList<BinInfo> RecycleBins() => Bins;

    public ActionResult Recycle(IReadOnlyList<string> paths)
    {
        Recycled.AddRange(paths);
        foreach (var l in Folders.Values) l.RemoveAll(i => paths.Contains(i.Path));
        return new ActionResult(true, "ok");
    }

    public ActionResult Open(string target) { Opened.Add(target); return new ActionResult(true, ""); }
}

public sealed class SpaceTests : IDisposable
{
    private const long Mb = 1L << 20, Gb = 1L << 30;
    private readonly Sandbox _box = new();
    private readonly FakeSpaceEnvironment _env = new();
    private readonly SpaceModule _module;

    public SpaceTests()
    {
        _module = new SpaceModule(_env);
        var now = _box.Clock.Now;
        _env.Folders["%TEMP%"] =
        [
            new TempItem(@"T\is-ABC.tmp", 800 * Mb, now.AddDays(-20)),
            new TempItem(@"T\fresh.log", 900 * Mb, now.AddHours(-1)),
        ];
        _env.Bins.Add(new BinInfo("C:", 12 * Gb, 340, 1000 * Gb));
        _env.Bins.Add(new BinInfo("E:", 1 * Gb, 3, 2000 * Gb));
    }

    public void Dispose() => _box.Dispose();

    private async Task Round() => Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
    private IReadOnlyList<StoredFinding> Open() => [.. _box.Store.GetFindings().Where(f => f.Module == "space" && f.Status == FindingStatus.Open)];

    [Fact]
    public async Task Old_leftovers_and_a_full_bin_are_reported_and_recent_files_are_never_touched()
    {
        await Round();
        var open = Open();
        var temp = open.Single(f => f.Key == "temp|%temp%");
        Assert.Contains("800 MB", temp.Title);
        Assert.Contains(open, f => f.Key == "bin|c:" && f.Fix!.Argument == SpaceModule.BinTarget);
        Assert.DoesNotContain(open, f => f.Key == "bin|e:");

        var recycle = _module.Actions.Single(a => a.Id == SpaceModule.RecycleTemp);
        Assert.True(recycle.NeverAuto);
        Assert.True((await _box.Runner.RunActionAsync(_module, recycle, "test", true, temp.Fix!.Argument, CancellationToken.None)).Ok);
        Assert.Equal([@"T\is-ABC.tmp"], _env.Recycled); // The file written an hour ago stays.
        Assert.DoesNotContain(Open(), f => f.Key.StartsWith("temp|"));
    }

    [Fact]
    public async Task Only_the_listed_folders_and_the_bin_can_be_opened_or_emptied()
    {
        var open = _module.Actions.Single(a => a.Id == SpaceModule.Open);
        Assert.True((await _box.Runner.RunActionAsync(_module, open, "test", true, SpaceModule.BinTarget, CancellationToken.None)).Ok);
        Assert.False((await _box.Runner.RunActionAsync(_module, open, "test", true, @"C:\Windows", CancellationToken.None)).Ok);
        var recycle = _module.Actions.Single(a => a.Id == SpaceModule.RecycleTemp);
        Assert.False((await _box.Runner.RunActionAsync(_module, recycle, "test", true, @"C:\Users", CancellationToken.None)).Ok);
        Assert.Empty(_env.Recycled);
    }
}

internal sealed class FakeWifiEnvironment : IWifiEnvironment
{
    public string? Output { get; set; }
    public string? Interfaces() => Output;
    public WifiAdapter? Card { get; set; }
    public WifiAdapter? Adapter() => Card;
    public ActionResult Open(string target) => new(true, "");
    public List<WiredLink> Cables { get; } = [];
    public IReadOnlyList<WiredLink> Wired() => [.. Cables];
}

public sealed class WifiTests : IDisposable
{
    private const string LocationButton = "ms-settings:privacy-location";
    private readonly Sandbox _box = new();
    private readonly FakeWifiEnvironment _env = new();
    private readonly WifiModule _module;

    public WifiTests() => _module = new WifiModule(_env);
    public void Dispose() => _box.Dispose();

    private static string Netsh(string band, int channel, int rx, int signal) => $"""

        There is 1 interface on the system:

            Name                   : Wi-Fi
            Description            : Intel(R) Wi-Fi 6 AX200 160MHz
            State                  : connected
            SSID                   : Casa
            AP BSSID               : aa:bb:cc:dd:ee:ff
            Band                   : {band}
            Channel                : {channel}
            Radio type             : 802.11ax
            Receive rate (Mbps)    : {rx}
            Transmit rate (Mbps)   : {rx - 100}
            Signal                 : {signal}%
            Profile                : Casa

        """;

    [Fact]
    public void Netsh_is_read_by_the_shape_of_its_values()
    {
        var link = WifiRules.Parse(Netsh("5 GHz", 36, 1201, 96).Replace("Receive rate", "Velocità ricezione").Replace("Transmit rate", "Velocità trasmissione").Replace("Signal", "Segnale"));
        Assert.Equal(new WifiLink("Casa", 5, 36, "802.11ax", 1201, 1101, 96), link);
        Assert.Null(WifiRules.Parse("There is 1 interface on the system:\n    State : disconnected\n"));
        Assert.True(WifiRules.NeedsLocation("Network shell commands need location permission to access WLAN information."));
        // Older Windows without the Band line: told from the channel.
        Assert.Equal(2.4, WifiRules.Parse(Netsh("x", 6, 144, 70).Replace("    Band                   : x\n", ""))!.BandGHz);
    }

    [Fact]
    public async Task A_drop_to_the_lower_band_two_readings_in_a_row_is_reported()
    {
        // An hour and more of normal readings: 5 GHz, around 1200 Mbps.
        for (var i = 0; i < 14; i++)
        {
            _env.Output = Netsh("5 GHz", 36, 1201, 90);
            Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
            _box.Clock.Now += TimeSpan.FromMinutes(5);
        }
        Assert.Empty(_box.Store.GetFindings());

        _env.Output = Netsh("2.4 GHz", 6, 144, 90);
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Empty(_box.Store.GetFindings()); // One reading is not enough.
        _box.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        var open = _box.Store.GetFindings().Where(f => f.Module == "wifi").ToList();
        Assert.Contains(open, f => f.Key == "band" && f.Severity == Severity.Medium);
        Assert.Contains(open, f => f.Key == "speed");
        Assert.Single(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "wifi", "wifi_band_changed"));
    }

    [Fact]
    public async Task Without_location_windows_says_nothing_and_the_page_explains_why()
    {
        _env.Output = "Network shell commands need location permission to access WLAN information. Turn on Location services...";
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        Assert.Empty(_box.Store.GetFindings());
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "wifi", WifiModule.BoardKind).Single().Data);
        var row = board.Single(i => i.Group == "Rete e segnale");
        Assert.Contains("Posizione", row.Value); // Said in words, with the button.
        Assert.Equal(LocationButton, row.Buttons!.Single().Argument);
    }
}

public sealed class WifiWithoutLocationTests : IDisposable
{
    private readonly Sandbox _box = new();
    public void Dispose() => _box.Dispose();

    [Fact]
    public async Task Without_location_the_link_speed_still_shows()
    {
        var env = new FakeWifiEnvironment { Output = "Network shell commands need location permission to access WLAN information.", Card = new WifiAdapter("Intel(R) Wi-Fi 6 AX200 160MHz", 287) };
        var module = new WifiModule(env);
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None));
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "wifi", WifiModule.BoardKind).Single().Data);
        Assert.Contains(board, i => i.Value.StartsWith("287 Mbps"));
        Assert.Contains(board, i => i.Value.StartsWith("Nascosti da Windows"));
        Assert.Contains(board, i => i.Label.StartsWith("Intel") && i.Value.StartsWith("Wi-Fi 6"));
        Assert.Contains(board, i => i.Value.StartsWith("Ancora da imparare"));
        Assert.DoesNotContain(board, i => i.Value.Contains("?")); // Nothing unexplained.
        Assert.Single(_box.Store.QuerySamples("wifi.rx_mbps", _box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1)));
    }
}
