using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Layout;
using OptLife.Modules.Cleanup;
using OptLife.Modules.Emulation;
using OptLife.Modules.Monitor;
using OptLife.Modules.Monitor.Sensors;
using OptLife.Modules.Overlay;
using OptLife.Modules.Setup;
using OptLife.Modules.Tools;
using OptLife.Modules.Wifi;

namespace OptLife.Tests;

/// <summary>OptLife on other people's PCs: laptops, no Afterburner, no NVIDIA, another way of keeping the Desktop, known tools, a cable.</summary>
public sealed class PortabilityTests : IDisposable
{
    private readonly Sandbox _box = new();
    public void Dispose() => _box.Dispose();

    private void Set(string module, string key, object value) =>
        _box.Config.UpdateModule(module, m => m.Settings[key] = JsonSerializer.SerializeToElement(value));

    // ── Laptops ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_laptop_gets_no_desktop_advice_and_a_desktop_with_a_ups_is_still_a_desktop()
    {
        Reading[] readings = [SetupRules.PowerPlan(SetupRules.PowerSaver, "Risparmio di energia"), SetupRules.GpuPowerLimit(80)];

        var laptop = SetupRules.ForDevice(readings, chassis: 10, battery: true);
        Assert.Equal("portatile", laptop.Single(r => r.Key == "power.device").Value);
        var plan = laptop.Single(r => r.Key == "power.plan");
        Assert.Null(plan.Problem); // Power saver on battery is right.
        Assert.Null(plan.Fix);
        Assert.Null(plan.Good);
        Assert.Contains("produttore", laptop.Single(r => r.Key == "gpu.power_limit").Advice);

        var desktop = SetupRules.ForDevice(readings, chassis: 3, battery: true); // A UPS reports a battery.
        Assert.Equal("fisso", desktop.Single(r => r.Key == "power.device").Value);
        Assert.NotNull(desktop.Single(r => r.Key == "power.plan").Problem);

        Assert.True(SetupRules.IsLaptop(null, battery: true)); // Case unknown: the battery decides.
        Assert.False(SetupRules.IsLaptop(null, battery: false));
    }

    // ── Values this PC cannot read ───────────────────────────────────────────

    [Fact]
    public void Without_afterburner_the_processor_temperature_is_not_available_with_why_and_the_official_page()
    {
        var missing = MissingValues.For(new HashSet<string> { "cpu.load", "gpu.temp", "gpu.load" }, AfterburnerState.NotInstalled);
        var cpu = Assert.Single(missing);
        Assert.Equal("cpu.temp", cpu.Key);
        Assert.True(cpu.IsMissing);
        Assert.Equal(0, cpu.Value);
        Assert.Contains("MSI Afterburner", cpu.Missing);
        Assert.Equal(MissingValues.AfterburnerPage, cpu.MissingFix!.Argument);

        Assert.Equal(MissingValues.StartAfterburner, MissingValues.For(new HashSet<string> { "gpu.temp" }, AfterburnerState.Closed).Single().MissingFix!.ActionId);
        Assert.Null(MissingValues.For(new HashSet<string> { "gpu.temp" }, AfterburnerState.Running).Single().MissingFix); // Open: what to tick, said in words.
        Assert.Empty(MissingValues.For(new HashSet<string> { "cpu.temp", "gpu.temp" }, AfterburnerState.Running)); // Found: nothing to say.

        // No NVIDIA card and no Afterburner: the graphics card says so too, never a 0.
        Assert.Contains(MissingValues.For(new HashSet<string> { "cpu.load" }, AfterburnerState.NotInstalled), v => v.Key == "gpu.temp" && v.IsMissing);
    }

    [Fact]
    public void The_overlay_shows_na_for_a_value_this_pc_cannot_read_and_says_why_under_the_preview()
    {
        IReadOnlyList<LiveValue> live = [new("cpu.load", "Carico", "Processore", 40, "%"), .. MissingValues.For(new HashSet<string> { "cpu.load", "gpu.temp" }, AfterburnerState.NotInstalled)];
        var rows = OverlayLayout.Rows(["cpu.temp=CPU", "cpu.load=LOAD"], live);
        Assert.Equal("n/d", rows.Single(r => r.Label == "CPU").Value);
        Assert.Equal("40%", rows.Single(r => r.Label == "LOAD").Value);
        Assert.StartsWith("CPU: Per la temperatura del processore installa MSI Afterburner", OverlayLayout.Unavailable(["cpu.temp=CPU", "cpu.load=LOAD"], live));
        Assert.Null(OverlayLayout.Unavailable(["cpu.load=LOAD"], live));
    }

    [Fact]
    public async Task The_afterburner_page_button_opens_only_msis_own_page()
    {
        var monitor = new MonitorModule([], () => "");
        var open = monitor.Actions.Single(a => a.Id == MissingValues.OpenSite);
        Assert.False((await _box.Runner.RunActionAsync(monitor, open, "test", true, "https://example.com/afterburner.exe", CancellationToken.None)).Ok);
    }

    // ── The Desktop: patterns, a crowded one, moves undone ───────────────────

    [Fact]
    public void Allowed_names_take_patterns_and_a_crowded_desktop_is_only_a_warning()
    {
        Assert.True(CleanupRules.Matches("Gioco.LNK", "*.lnk"));
        Assert.False(CleanupRules.Matches("foto.jpg", "*.lnk"));
        Assert.True(CleanupRules.Matches("desktop.ini", "desktop.ini"));
        var rule = TidyRule.Parse(@"C:\D | *.lnk, *.url, desktop.ini | C:\Docs\Dal Desktop")!;
        Assert.Equal(["foto.jpg", "Cartella"], CleanupRules.Loose(rule, ["a.lnk", "b.url", "foto.jpg", "desktop.ini", "Cartella"]));

        Assert.Null(CleanupRules.Crowded(@"C:\D", 3, ["a", "b", "c", "desktop.ini"]));
        var crowded = CleanupRules.Crowded(@"C:\D", 3, ["a", "b", "c", "d"])!;
        Assert.Null(crowded.Fix); // Nothing moves.
        Assert.Contains("4 elementi", crowded.Title);
    }

    [Fact]
    public async Task What_is_moved_off_the_desktop_can_be_put_back()
    {
        const string desktop = @"C:\Users\io\Desktop", inbox = @"C:\Users\io\Documents\Dal Desktop";
        var pc = new FakeCleanupEnvironment();
        pc.Folders[desktop] = ["Gioco.lnk", "foto.jpg", "desktop.ini"];
        var cleanup = new CleanupModule(pc);
        Set("cleanup", "tidy", new[] { $"{desktop} | *.lnk, *.url, desktop.ini | {inbox}" });
        Assert.True(await _box.Runner.RunTaskAsync(cleanup, cleanup.Tasks[0], CancellationToken.None));
        var finding = Assert.Single(_box.Store.GetFindings());
        Assert.Contains("foto.jpg", finding.Detail); // The whole list, before pressing.

        var moved = await _box.Runner.RunActionAsync(cleanup, cleanup.Actions.Single(a => a.Id == CleanupModule.MoveLoose), "test", true, desktop, CancellationToken.None);
        Assert.True(moved.Ok);
        Assert.DoesNotContain("foto.jpg", pc.Folders[desktop]);
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "cleanup", CleanupModule.HubKind).MaxBy(e => e.Id)!.Data);
        var undo = board.SelectMany(i => i.Buttons ?? []).Single(b => b.ActionId == CleanupModule.LooseUndo);

        Assert.True((await _box.Runner.RunActionAsync(cleanup, cleanup.Actions.Single(a => a.Id == CleanupModule.LooseUndo), "test", true, undo.Argument, CancellationToken.None)).Ok);
        Assert.Contains("foto.jpg", pc.Folders[desktop]);
        Assert.DoesNotContain("foto.jpg", pc.Folders[inbox]);
    }

    /// <summary>24 Nov 2026: shortcuts to Store apps made in the Hub (IDList of shell:AppsFolder only) were reported as broken.</summary>
    [Fact]
    public void A_store_app_shortcut_is_broken_only_when_the_app_is_uninstalled()
    {
        var installed = new HashSet<string> { "4DF9E0F8.Netflix_mcm4njqhnhss8", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm" };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\Google\Chrome\Application\chrome.exe" };
        bool Broken(string target, string arguments, string? item) =>
            ShortcutCheck.IsBroken(target, arguments, item, installed.Contains, files.Contains, d => d.StartsWith("C:", StringComparison.OrdinalIgnoreCase));

        Assert.False(Broken("", "", "4DF9E0F8.Netflix_mcm4njqhnhss8!Netflix.App"));                      // IDList only, installed.
        Assert.True(Broken("", "", "AppleInc.AppleTVWin_nzyj5cx40ttqa!App"));                             // IDList only, uninstalled.
        Assert.False(Broken(@"C:\Windows\explorer.exe", @"shell:AppsFolder\5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", null));
        Assert.True(Broken(@"C:\Windows\explorer.exe", @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude", null));
        Assert.False(Broken("", "", "Microsoft.Windows.Explorer"));                                       // Not a package: no judgement.
        Assert.False(Broken("", "", null));                                                               // Unreadable: not broken.
        Assert.False(Broken("https://music.apple.com/", "", null));                                       // A web address.
        Assert.False(Broken(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "--app=https://music.apple.com/", null)); // A web app.
        Assert.True(Broken(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "--app-id=abc", null)); // Edge removed: really broken.
        Assert.False(Broken(@"F:\Games\game.exe", "", null));                                             // A drive not plugged in.
        Assert.False(Broken(@"\\nas\share\tool.exe", "", null));                                          // The network.
    }

    // ── Ways of keeping the Desktop ──────────────────────────────────────────

    private (LayoutModule Layout, IModule[] Modules) Layout()
    {
        var list = new List<IModule> { new CleanupModule(), new EmulationModule() };
        var layout = new LayoutModule(_box.Config, () => list, new FakeLayoutEnvironment());
        list.Add(layout);
        return (layout, [.. list]);
    }

    private async Task<ActionResult> Apply(LayoutModule layout)
    {
        Assert.True(await _box.Runner.RunTaskAsync(layout, layout.Tasks.Single(), CancellationToken.None, onDemand: true));
        return await _box.Runner.RunActionAsync(layout, layout.Actions.Single(a => a.Id == LayoutModule.Apply), "test", true, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_new_pc_starts_with_warnings_only_and_nothing_that_moves()
    {
        var (layout, modules) = Layout();
        Assert.True((await Apply(layout)).Ok);
        Assert.Equal(DesktopPreset.NameOf(DesktopPreset.Report), _box.Runner.SettingsOf(layout).GetChoice("desktop"));
        var cleanup = _box.Runner.SettingsOf(modules[0]);
        Assert.Empty(cleanup.GetList("tidy"));
        Assert.Contains(cleanup.GetList("crowded"), l => l.StartsWith(@"%USERPROFILE%\Desktop | 30"));
        Assert.Contains(@"%USERPROFILE%\Desktop", cleanup.GetList("shortcuts"));
    }

    [Fact]
    public async Task A_pc_with_the_hub_already_is_hub_by_category_and_stays_as_it_is()
    {
        var hubLine = @"%USERPROFILE%\Desktop | Hub, desktop.ini | %USERPROFILE%\Desktop\Hub\0 Da sistemare";
        Set("cleanup", "tidy", new[] { hubLine });
        var (layout, modules) = Layout();
        await Apply(layout);
        Assert.Equal(DesktopPreset.NameOf(DesktopPreset.Hub), _box.Runner.SettingsOf(layout).GetChoice("desktop"));
        Assert.Contains(hubLine, _box.Runner.SettingsOf(modules[0]).GetList("tidy"));
        Assert.Empty(_box.Runner.SettingsOf(modules[0]).GetList("crowded"));
    }

    [Fact]
    public async Task Changing_from_hub_to_clean_desktop_shows_what_changes_and_apply_replaces_the_hubs_rules()
    {
        Set("layout", "desktop", DesktopPreset.NameOf(DesktopPreset.Hub));
        var (layout, modules) = Layout();
        await Apply(layout);
        var cleanup = _box.Runner.SettingsOf(modules[0]);
        Assert.Contains(cleanup.GetList("tidy"), l => l.Contains("| Hub,"));

        Set("layout", "desktop", DesktopPreset.NameOf(DesktopPreset.Clean));
        Assert.True(await _box.Runner.RunTaskAsync(layout, layout.Tasks.Single(), CancellationToken.None, onDemand: true));
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), LayoutModule.Id, LayoutModule.BoardKind).MaxBy(e => e.Id)!.Data);
        Assert.Contains(board, i => i.Value.StartsWith("Da cambiare"));
        Assert.Contains(board, i => i.Value.StartsWith("Del modo di prima"));
        Assert.Equal(1, _box.Runner.SettingsOf(modules[0]).GetList("tidy").Count(l => l.Contains("| Hub,"))); // Nothing changed before APPLY.

        await _box.Runner.RunActionAsync(layout, layout.Actions.Single(a => a.Id == LayoutModule.Apply), "test", true, null, CancellationToken.None);
        cleanup = _box.Runner.SettingsOf(modules[0]);
        var tidy = cleanup.GetList("tidy");
        Assert.Contains(tidy, l => l.StartsWith(@"%USERPROFILE%\Desktop | *.lnk, *.url, desktop.ini |"));
        Assert.DoesNotContain(tidy, l => l.Contains("| Hub,"));
        Assert.DoesNotContain(tidy, l => l.StartsWith(@"C:\Users\Public\Desktop")); // The Hub's own line went.
        Assert.Contains(@"%USERPROFILE%\Desktop", cleanup.GetList("shortcuts"));
    }

    // ── Known tools ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Known_tools_show_only_where_they_are_and_never_twice()
    {
        var env = new FakeToolsEnvironment();
        var tools = new ToolsModule(env);
        var hwinfo = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\HWiNFO64\HWiNFO64.EXE");
        var ddu = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Display Driver Uninstaller\Display Driver Uninstaller.exe");
        IReadOnlyList<ViewButton> Buttons() => tools.Views.Single().Buttons!(_box.Runner.SettingsOf(tools));

        Assert.DoesNotContain(Buttons(), b => b.ActionId is ToolsModule.RunKnown or ToolsModule.ClearShaderCache);

        env.Files.Add(hwinfo);
        env.Files.Add(ddu);
        env.Caches.Add(new ShaderCache(@"%LOCALAPPDATA%\NVIDIA\DXCache", 300L << 20, 40));
        // The person has DDU among their own tools already: not a second button.
        Set("tools", "tools", new[] { @"Driver | DDU | D:\Utility\DDU\Display Driver Uninstaller.exe | | yes | mine" });
        Set("tools", "hiddenKnown", new[] { "crystaldiskinfo" });
        var buttons = Buttons();
        Assert.Contains(buttons, b => b.ActionId == ToolsModule.RunKnown && b.Argument == "hwinfo");
        Assert.DoesNotContain(buttons, b => b.ActionId == ToolsModule.RunKnown && b.Argument == "ddu");
        Assert.Contains(buttons, b => b.ActionId == ToolsModule.ClearShaderCache);

        var run = tools.Actions.Single(a => a.Id == ToolsModule.RunKnown);
        Assert.True((await _box.Runner.RunActionAsync(tools, run, "test", true, "hwinfo", CancellationToken.None)).Ok);
        Assert.Equal((hwinfo, false), env.Known.Single());

        // Only the driver's own cache folders, whatever is passed.
        var clear = tools.Actions.Single(a => a.Id == ToolsModule.ClearShaderCache);
        await _box.Runner.RunActionAsync(tools, clear, "test", true, @"C:\Windows;%LOCALAPPDATA%\NVIDIA\DXCache", CancellationToken.None);
        Assert.Equal([@"%LOCALAPPDATA%\NVIDIA\DXCache"], env.Emptied);
        Assert.True(clear.NeverAuto);
    }

    // ── The cable ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_gigabit_card_at_100_mbps_and_damaged_packets_are_reported_after_two_checks()
    {
        Assert.True(WiredRules.Gigabit("Realtek PCIe GbE Family Controller"));
        Assert.True(WiredRules.Gigabit("Intel(R) Ethernet Controller I225-V"));
        Assert.False(WiredRules.Gigabit("Generic USB Ethernet"));
        Assert.Equal("2,5 Gbps", WiredRules.Speed(2500));

        var env = new FakeWifiEnvironment();
        var network = new WifiModule(env);
        async Task Round(long errors)
        {
            env.Cables.Clear();
            env.Cables.Add(new WiredLink("{1}", "Ethernet", "Realtek PCIe GbE Family Controller", true, true, 100, errors));
            Assert.True(await _box.Runner.RunTaskAsync(network, network.Tasks.Single(), CancellationToken.None));
            _box.Clock.Now += TimeSpan.FromMinutes(5);
        }
        await Round(0);
        Assert.Empty(_box.Store.GetFindings()); // One reading is not enough.
        await Round(500);
        await Round(1000);
        var open = _box.Store.GetFindings();
        Assert.Contains(open, f => f.Key == "wired.slow|{1}" && f.Severity == Severity.Medium);
        Assert.Contains(open, f => f.Key == "wired.errors|{1}");
        var board = CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.Now.AddDays(-1), _box.Clock.Now.AddDays(1), "wifi", WifiModule.BoardKind).MaxBy(e => e.Id)!.Data);
        Assert.Contains(board, i => i.Value.StartsWith("il cavo"));
        Assert.DoesNotContain(board, i => i.Group == "Rete e segnale"); // No Wi-Fi card: nothing about Wi-Fi.

        // The cable comes out three times in a day: worth a look.
        for (var i = 0; i < 3; i++)
        {
            env.Cables.Clear();
            env.Cables.Add(new WiredLink("{1}", "Ethernet", "Realtek PCIe GbE Family Controller", false, false, 0, 0));
            Assert.True(await _box.Runner.RunTaskAsync(network, network.Tasks.Single(), CancellationToken.None));
            await Round(1000);
        }
        Assert.Contains(_box.Store.GetFindings(), f => f.Key == "wired.drops");
    }
}
