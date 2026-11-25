using System.Text.Json;
using OptLife.Core;
using OptLife.Modules.Audio;

namespace OptLife.Tests;

internal sealed class FakeAudioEnvironment : IAudioEnvironment
{
    public List<AudioDevice> List { get; } = [];
    public string?[] Output { get; } = new string?[3];
    public string?[] Input { get; } = new string?[3];
    public List<string> Done { get; } = [];

    public IReadOnlyList<AudioDevice> Devices() => [.. List];
    public AudioDefaults Defaults() => new([.. Output], [.. Input]);

    public ActionResult SetVisible(string id, bool visible)
    {
        Done.Add($"{(visible ? "show" : "hide")} {id}");
        var i = List.FindIndex(d => d.Id == id);
        List[i] = List[i] with { On = visible };
        return new ActionResult(true, "ok");
    }

    public ActionResult SetDefault(string id)
    {
        Done.Add($"default {id}");
        var flow = List.Single(d => d.Id == id).Flow;
        for (var r = 0; r < 3; r++) (flow == Flow.Output ? Output : Input)[r] = id;
        return new ActionResult(true, "ok");
    }
}

public sealed class AudioTests : IDisposable
{
    private const string DAC = "{0.0.0.00000000}.{dac}", Realtek = "{0.0.0.00000000}.{realtek}", Digital = "{0.0.0.00000000}.{digital}",
        UsbMic = "{0.0.1.00000000}.{USB}", Webcam = "{0.0.1.00000000}.{webcam}", FiiO = "{0.0.0.00000000}.{fiio}";

    private readonly Sandbox _box = new();
    private readonly FakeAudioEnvironment _env = new();
    private readonly AudioModule _module;

    public AudioTests()
    {
        _module = new AudioModule(_env);
        _env.List.AddRange([
            new AudioDevice(DAC, Flow.Output, "Altoparlanti (USB Audio DAC)", true),
            new AudioDevice(Realtek, Flow.Output, "Altoparlanti (Realtek(R) Audio)", true),
            new AudioDevice(Digital, Flow.Output, "Realtek Digital Output (Realtek(R) Audio)", false),
            new AudioDevice(UsbMic, Flow.Input, "Microfono (USB Microphone)", true),
            new AudioDevice(Webcam, Flow.Input, "Microfono (GENERAL WEBCAM)", false),
        ]);
        for (var r = 0; r < 3; r++) (_env.Output[r], _env.Input[r]) = (DAC, UsbMic);
        _box.Config.UpdateModule("audio", m =>
        {
            m.Settings["allowed"] = JsonSerializer.SerializeToElement(new[] { "DAC", "Altoparlanti (Realtek", "Microfono (USB" });
            m.Settings["defaultOutput"] = JsonSerializer.SerializeToElement("DAC");
            m.Settings["defaultInput"] = JsonSerializer.SerializeToElement("USB");
        });
    }

    public void Dispose() => _box.Dispose();

    private async Task Round()
    {
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks.Single(), CancellationToken.None));
        _box.Clock.Now += TimeSpan.FromMinutes(5);
    }

    private IReadOnlyList<StoredFinding> Open() => [.. _box.Store.GetFindings().Where(f => f.Module == "audio" && f.Status == FindingStatus.Open)];

    private IReadOnlyList<CheckItem> Board() =>
        CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "audio", kind: AudioModule.BoardKind).MaxBy(e => e.Id)!.Data);

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(0x10000001, false, false)]
    [InlineData(8, true, true)]
    [InlineData(0x10000008, false, true)]
    public void Device_state_is_read_like_sound_settings(int state, bool on, bool unplugged) =>
        Assert.Equal((on, unplugged), AudioRules.FromState(state));

    [Theory]
    [InlineData(4)]
    [InlineData(0x20000004)]
    [InlineData(0x10000004)]
    public void Old_copies_of_hardware_no_longer_there_are_not_devices(int state) => Assert.Null(AudioRules.FromState(state));

    [Fact]
    public async Task As_it_should_be_there_is_nothing_to_report_and_the_page_is_green()
    {
        await Round();
        Assert.Empty(Open());
        Assert.DoesNotContain(Board(), i => i.Status == CheckStatus.Watch || i.StatusText is null && i.Status == CheckStatus.Wrong); // Red only for devices that are off: a state, not a fault.
    }

    [Fact]
    public async Task A_new_dac_is_reported_as_new_with_a_button_to_turn_it_off()
    {
        await Round();
        _env.List.Add(new AudioDevice(FiiO, Flow.Output, "Altoparlanti (FiiO K11)", true));
        await Round();

        var f = Assert.Single(Open());
        Assert.Contains("FiiO K11", f.Title);
        Assert.StartsWith("Nuovo", f.Title);
        Assert.Equal(AudioRules.Hide, f.Fix!.ActionId);
        Assert.Single(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "audio", kind: "audio_device_added"));

        var hide = _module.Actions.Single(a => a.Id == AudioRules.Hide);
        var result = await _box.Runner.RunActionAsync(_module, hide, "test", true, f.Fix.Argument, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Contains("show-device", result.Undo);
        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_monitor_output_made_again_by_windows_is_explained_and_can_be_turned_off_by_itself()
    {
        _env.List.Add(new AudioDevice("{0.0.0.00000000}.{old-lg}", Flow.Output, "LG ULTRAGEAR (NVIDIA High Definition Audio)", false));
        await Round();
        // After a restart Windows makes a new entry for the same monitor, switched on.
        _env.List.Add(new AudioDevice("{0.0.0.00000000}.{new-lg}", Flow.Output, "LG ULTRAGEAR (NVIDIA High Definition Audio)", true));
        await Round();
        var f = Assert.Single(Open());
        Assert.Contains("ricreato", f.Detail);

        _box.Config.UpdateModule("audio", m => m.Settings["newDevices"] = JsonSerializer.SerializeToElement("spegnilo da solo"));
        _env.List.Add(new AudioDevice("{0.0.0.00000000}.{other}", Flow.Output, "LG ULTRAFINE (NVIDIA High Definition Audio)", true));
        await Round();
        Assert.Contains("hide {0.0.0.00000000}.{other}", _env.Done);
        Assert.Contains(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "audio", kind: "audio_device_disabled"),
            e => e.Title.StartsWith("Spento da solo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_that_comes_back_on_says_so()
    {
        await Round();
        var i = _env.List.FindIndex(d => d.Id == Webcam);
        _env.List[i] = _env.List[i] with { On = true };
        await Round();
        var f = Assert.Single(Open());
        Assert.Contains("lasciato acceso come hai scelto", f.Title); // The same entry back on: the person switched it on.
        Assert.Equal(Severity.Info, f.Severity);
    }

    /// <summary>24 Nov 2026, 19:42-19:44: a monitor's sound turned off by itself, switched back on by hand, turned off again, twice.</summary>
    [Fact]
    public async Task A_device_switched_back_on_by_hand_is_never_turned_off_again()
    {
        _box.Config.UpdateModule("audio", m => m.Settings["newDevices"] = JsonSerializer.SerializeToElement("spegnilo da solo"));
        await Round();
        const string monitor = "{0.0.0.00000000}.{monitor}";
        _env.List.Add(new AudioDevice(monitor, Flow.Output, "MONITOR (NVIDIA High Definition Audio)", true));
        await Round();
        Assert.Contains($"hide {monitor}", _env.Done); // New and not allowed: off by itself.

        // Switched back on by the person (the page's TURN ON, or Windows' Sound settings): same entry.
        var show = _module.Actions.Single(a => a.Id == AudioRules.Show);
        Assert.True((await _box.Runner.RunActionAsync(_module, show, "test", true, monitor, CancellationToken.None)).Ok);
        _env.Done.Clear();
        await Round();
        await Round();
        Assert.DoesNotContain($"hide {monitor}", _env.Done);
        Assert.True(_env.List.Single(d => d.Id == monitor).On);
        Assert.Single(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), module: "audio", kind: "audio_device_kept"));
        Assert.Equal(Severity.Info, Open().Single(f => f.Key.Contains("monitor")).Severity);

        // A new entry Windows makes for it later is new again: turned off.
        const string again = "{0.0.0.00000000}.{monitor-2}";
        _env.List.Add(new AudioDevice(again, Flow.Output, "MONITOR 2 (NVIDIA High Definition Audio)", true));
        await Round();
        Assert.Contains($"hide {again}", _env.Done);
    }

    [Fact]
    public async Task A_default_moved_for_one_role_is_set_back_for_all()
    {
        _env.Output[2] = Realtek; // Calls went to the speakers.
        await Round();
        var f = Assert.Single(Open());
        Assert.Equal("default|output", f.Key);
        Assert.Contains(AudioRules.RoleName(2), f.Detail);
        Assert.Equal(DAC, f.Fix!.Argument);
        Assert.Equal(CheckStatus.Wrong, Board()[0].Status);

        var set = _module.Actions.Single(a => a.Id == AudioRules.SetDefault);
        Assert.True((await _box.Runner.RunActionAsync(_module, set, "test", true, f.Fix.Argument, CancellationToken.None)).Ok);
        Assert.Empty(Open());
        Assert.Equal(CheckStatus.Ok, Board()[0].Status);
    }

    [Fact]
    public async Task An_allowed_device_switched_off_is_reported_and_an_allowed_one_missing_is_marked()
    {
        var i = _env.List.FindIndex(d => d.Id == Realtek);
        _env.List[i] = _env.List[i] with { On = false };
        _box.Config.UpdateModule("audio", m => m.Settings["allowed"] = JsonSerializer.SerializeToElement(new[] { "DAC", "Altoparlanti (Realtek", "Microfono (USB", "FiiO" }));
        await Round();
        var f = Assert.Single(Open());
        Assert.Equal(AudioRules.Show, f.Fix!.ActionId);
        Assert.Equal(CheckStatus.Watch, Board().Single(x => x.Label == "FiiO").Status);
    }

    [Fact]
    public void With_no_settings_nothing_is_judged()
    {
        var x = new AudioRules.Inputs(_env.Devices(), _env.Defaults(), [], null, null, new HashSet<string>(), new HashSet<string>());
        Assert.Empty(AudioRules.Findings(x));
        Assert.DoesNotContain(AudioRules.Board(x), i => i.StatusText is null); // Only states: no defaults judged, nothing called wrong.
    }
}
