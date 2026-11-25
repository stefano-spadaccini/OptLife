using System.Text;
using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Modules.Setup;
using OptLife.Modules.Setup.Windows;

namespace OptLife.Tests;

/// <summary>A PC whose settings, parts and versions are whatever the test says.</summary>
internal sealed class FakePcProbe : IPcProbe
{
    public List<Reading> Settings { get; } = [];
    public Dictionary<string, string> Hardware { get; } = new() { ["Scheda video"] = "RTX 3080", ["Memoria"] = "2 × 8 GB DDR4" };
    public Dictionary<string, string> Versions { get; } = new() { ["Driver della scheda video"] = "566.36", ["Windows"] = "26H2 build 26300.9818" };

    public IReadOnlyList<Reading> ReadSettings() => [.. Settings];
    public IReadOnlyDictionary<string, string> ReadHardware() => new Dictionary<string, string>(Hardware);
    public IReadOnlyDictionary<string, string> ReadVersions() => new Dictionary<string, string>(Versions);

    /// <summary>What was asked of the machine, and what the machine does about it.</summary>
    public List<(string Action, string? Argument)> Applied { get; } = [];
    public Func<string, string?, ActionResult> OnApply { get; set; } = (_, _) => new ActionResult(true, "Fatto.", "Come prima.");

    public ActionResult Apply(string action, string? argument)
    {
        Applied.Add((action, argument));
        return OnApply(action, argument);
    }

    public void Set(string key, string value, string? problem = null, string? note = null, FindingFix? fix = null)
    {
        Settings.RemoveAll(r => r.Key == key);
        Settings.Add(new Reading(key, Label(key), value, problem, "Rimettilo com'era.", Severity.Medium, note, fix));
    }

    private static string Label(string key) => key switch
    {
        "power.plan" => "Piano energetico",
        "display.A.mode" => "Schermo A",
        _ => key,
    };
}

public class SetupModuleTests
{
    private readonly Sandbox _box = new();
    private readonly FakePcProbe _pc = new();
    private readonly SetupModule _module;

    public SetupModuleTests()
    {
        _module = new SetupModule(_pc);
        _pc.Set("power.plan", "Prestazioni elevate");
        _pc.Set("display.A.mode", "2560×1440 a 165 Hz");
    }

    private async Task Check(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None),
                _box.Runner.ReadStatus(_module, _module.Tasks[0]).LastError);
            _box.Clock.Now += TimeSpan.FromMinutes(10);
        }
    }

    private IReadOnlyList<StoredFinding> Open() => _box.Store.GetFindings();

    private IReadOnlyList<StoredEvent> Events(string kind) =>
        _box.Store.QueryEvents(_box.Clock.Now.AddDays(-30), _box.Clock.Now.AddDays(1), kind: kind);

    /// <summary>24 Nov 2026: after the BIOS update to 3645 SVM was off; the line was new, so there was no earlier value to compare with.</summary>
    [Fact]
    public async Task A_bios_setting_first_seen_off_right_after_a_bios_update_is_reported_as_reset()
    {
        _pc.Versions["BIOS"] = "3607";
        await Check();
        _pc.Versions["BIOS"] = "3645";
        await Check();
        _pc.Settings.Add(new Reading("bios.virtualization", "Virtualizzazione del processore", "spenta", Good: false, Advice: "SVM Mode nel BIOS."));
        await Check();

        var finding = Assert.Single(Open(), f => f.Key.StartsWith("bios.virtualization", StringComparison.Ordinal));
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains("aggiornamento del BIOS", finding.Title);

        // Turned back on in the BIOS: as it should be, so nothing to warn about (it was reported as a factory reset).
        _pc.Settings[^1] = new Reading("bios.virtualization", "Virtualizzazione del processore", "accesa", Good: true);
        await Check(3);
        Assert.DoesNotContain(Open(), f => f.Key.StartsWith("bios.virtualization", StringComparison.Ordinal) && f.Status == FindingStatus.Open);
    }

    [Fact]
    public async Task A_pc_that_is_set_up_well_has_no_problems_and_one_line_in_the_history()
    {
        await Check(3);

        Assert.Empty(Open());
        var line = Assert.Single(Events("settings_check"));
        Assert.Equal("Impostazioni del PC: 2 voci controllate, tutte a posto", line.Title);
        Assert.Equal("Piano energetico = Prestazioni elevate", line.Data["power.plan"]);
    }

    [Fact]
    public async Task Something_wrong_in_itself_is_reported_from_the_first_check()
    {
        _pc.Set("display.A.mode", "2560×1440 a 60 Hz", problem: "Lo schermo A va a 60 Hz ma può arrivare a 165 Hz");

        await Check();

        var finding = Assert.Single(Open());
        Assert.Equal("Lo schermo A va a 60 Hz ma può arrivare a 165 Hz", finding.Title);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal("Impostazioni del PC: 2 voci controllate, 1 da sistemare", Events("settings_check")[0].Title);
    }

    [Fact]
    public async Task A_setting_that_changes_is_reported_only_once_it_has_been_seen_twice()
    {
        await Check();
        _pc.Set("power.plan", "Bilanciato");

        await Check();
        Assert.Empty(Open()); // Could be a passing state.

        await Check();
        var finding = Assert.Single(Open());
        Assert.Equal("Piano energetico è cambiato: ora è \"Bilanciato\"", finding.Title);
        Assert.Contains("Prima era \"Prestazioni elevate\"", finding.Detail);
        Assert.Equal("Piano energetico: da \"Prestazioni elevate\" a \"Bilanciato\"", Assert.Single(Events("setting_changed")).Title);
    }

    [Fact]
    public async Task A_change_that_lasts_one_check_is_never_reported()
    {
        await Check();
        _pc.Set("display.A.mode", "1920×1080 a 60 Hz");
        await Check();
        _pc.Set("display.A.mode", "2560×1440 a 165 Hz");

        await Check(3);

        Assert.Empty(Open());
        Assert.Empty(Events("setting_changed"));
    }

    [Fact]
    public async Task Putting_it_back_closes_the_problem()
    {
        await Check();
        _pc.Set("power.plan", "Bilanciato");
        await Check(2);
        Assert.Single(Open());

        _pc.Set("power.plan", "Prestazioni elevate");
        await Check(2);

        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_change_right_after_an_update_names_the_update()
    {
        await Check();
        _pc.Versions["Driver della scheda video"] = "617.42";
        _pc.Set("display.A.mode", "2560×1440 a 60 Hz", problem: "Lo schermo A va a 60 Hz ma può arrivare a 165 Hz");

        await Check(2);

        Assert.Equal("Driver della scheda video: aggiornamento da 566.36 a 617.42", Assert.Single(Events("system_update")).Title);
        var finding = Assert.Single(Open());
        Assert.Contains("Prima era \"2560×1440 a 165 Hz\"", finding.Detail);
        Assert.Contains("Poco prima", finding.Detail);
        Assert.Contains("Driver della scheda video: aggiornamento da 566.36 a 617.42", finding.Detail);
    }

    [Fact]
    public async Task An_update_from_last_month_is_not_blamed()
    {
        await Check();
        _pc.Versions["Windows"] = "26H2 build 26300.9900";
        await Check();
        _box.Clock.Now += TimeSpan.FromDays(20);
        _pc.Set("power.plan", "Bilanciato");

        await Check(2);

        Assert.DoesNotContain("Poco prima", Assert.Single(Open()).Detail);
    }

    [Fact]
    public async Task Accepting_a_change_by_ignoring_it_holds_until_it_changes_again()
    {
        await Check();
        _pc.Set("power.plan", "Bilanciato");
        await Check(2);
        _box.Store.SetFindingStatus(Assert.Single(Open()).Id, FindingStatus.Ignored);

        await Check(3);
        Assert.Empty(Open());

        _pc.Set("power.plan", "Risparmio di energia");
        await Check(2);
        Assert.Equal("Piano energetico è cambiato: ora è \"Risparmio di energia\"", Assert.Single(Open()).Title);
    }

    [Fact]
    public async Task New_hardware_is_recorded_and_becomes_the_new_normal()
    {
        _pc.Set("ram.speed", "3200 MHz");
        await Check();
        _pc.Hardware["Memoria"] = "2 × 16 GB DDR4";
        _pc.Set("ram.speed", "3600 MHz");

        await Check(3);

        Assert.Equal("Hardware cambiato · Memoria: 2 × 16 GB DDR4 (prima: 2 × 8 GB DDR4)", Assert.Single(Events("hardware_change")).Title);
        Assert.Empty(Open()); // The speed of the new modules is not "a setting that changed".

        // From here on the new state is the reference like any other.
        _pc.Set("ram.speed", "2666 MHz", problem: "La memoria va a 2666 MHz ma è fatta per 3600 MHz");
        await Check(2);
        Assert.Contains("Prima era \"3600 MHz\"", Assert.Single(Open()).Detail);
    }

    [Fact]
    public async Task A_pinned_value_is_enforced_even_when_it_was_already_wrong_at_the_first_check()
    {
        _pc.Set("gpu.power_limit", "290 W");
        _box.Config.UpdateModule("setup", c => c.Settings["expected"] = JsonSerializer.SerializeToElement(new[] { "gpu.power_limit = 250 W" }));

        await Check();

        var finding = Assert.Single(Open());
        Assert.Equal("gpu.power_limit: dovrebbe essere \"250 W\" ma è \"290 W\"", finding.Title);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains("1 da sistemare", Events("settings_check")[0].Title);

        _pc.Set("gpu.power_limit", "250 W");
        await Check(2);
        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_screen_that_is_switched_off_takes_its_problem_away_and_brings_it_back()
    {
        _pc.Set("display.B.mode", "3840×2160 a 30 Hz", problem: "Lo schermo B va a 30 Hz ma può arrivare a 60 Hz");
        await Check();
        Assert.Single(Open());

        var b = _pc.Settings.Single(r => r.Key == "display.B.mode");
        _pc.Settings.Remove(b);
        await Check();
        Assert.Empty(Open());

        _pc.Settings.Add(b);
        await Check();
        Assert.Single(Open());
    }

    [Fact]
    public async Task Debatable_advice_is_information_that_can_be_switched_off()
    {
        _pc.Set("windows.memory_integrity", "accesa", note: "Può costare qualche frame.");

        await Check();
        var note = Assert.Single(Open());
        Assert.Equal(Severity.Info, note.Severity);
        Assert.Equal("Può costare qualche frame.", note.Detail);

        _box.Config.UpdateModule("setup", c => c.Settings["showAdvice"] = JsonSerializer.SerializeToElement(false));
        await Check();
        Assert.Empty(Open());
    }

    [Fact]
    public async Task An_excluded_entry_is_not_checked_at_all()
    {
        _pc.Set("windows.gamemode", "spenta", problem: "La Modalità gioco di Windows è spenta");
        _box.Config.UpdateModule("setup", c => c.Settings["ignored"] = JsonSerializer.SerializeToElement(new[] { "windows.gamemode" }));

        await Check();

        Assert.Empty(Open());
        Assert.False(Events("settings_check")[0].Data.ContainsKey("windows.gamemode"));
    }

    [Fact]
    public async Task Nothing_is_checked_while_a_game_is_running()
    {
        using var board = new LiveBoard($@"Local\OptLife.Test.{Guid.NewGuid():N}");
        var runner = new ModuleRunner(_box.Store, _box.Config, _box.Clock, board);
        board.Publish("games", [new LiveValue("game.minutes", "In gioco da", "Gioco", 3, "min")], _box.Clock.Now);
        _pc.Set("display.A.mode", "1280×720 a 60 Hz", problem: "Lo schermo A va a 60 Hz ma può arrivare a 165 Hz"); // The game's own mode.

        Assert.True(await runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None));

        Assert.Empty(Open());
        Assert.Empty(Events("settings_check"));
    }
}

public class SetupRulesTests
{
    [Fact]
    public void A_screen_below_its_top_rate_is_a_problem_and_one_hertz_of_rounding_is_not()
    {
        var slow = SetupRules.Display(new DisplayInfo("GSM5B7F", "LG ULTRAGEAR", 2560, 1440, 60, 165, Hdr: false)).ToList();
        Assert.Equal("Lo schermo LG ULTRAGEAR va a 60 Hz ma può arrivare a 165 Hz", slow[0].Problem);
        Assert.Equal("2560×1440 a 60 Hz", slow[0].Value);
        Assert.Equal("display.GSM5B7F.mode", slow[0].Key);
        Assert.Equal("spento", slow[1].Value);

        var fine = SetupRules.Display(new DisplayInfo("GSM5B7F", "LG ULTRAGEAR", 2560, 1440, 164, 165, Hdr: null)).ToList();
        Assert.Null(Assert.Single(fine).Problem); // And no HDR entry for a screen that has none.
    }

    [Fact]
    public void Memory_slower_than_it_was_built_for_is_a_problem()
    {
        var kit = new MemoryModule(8192, "DDR4", 3200, 2133, "Crucial", "BL8G32C16U4BL.M8FE");
        var reading = Assert.Single(SetupRules.Memory([kit, kit]));
        Assert.Equal("La memoria va a 2133 MHz ma è fatta per 3200 MHz", reading.Problem);

        var ok = Assert.Single(SetupRules.Memory([kit with { ConfiguredMhz = 3200 }]));
        Assert.Null(ok.Problem);
        Assert.Null(ok.Note);
        Assert.Equal("3200 MHz", ok.Value);
    }

    [Fact]
    public void Memory_at_base_speed_that_claims_nothing_more_gets_a_remark_not_a_problem()
    {
        var basic = Assert.Single(SetupRules.Memory([new MemoryModule(8192, "DDR4", 2133, 2133, "", "")]));
        Assert.Null(basic.Problem);
        Assert.Contains("velocità di base", basic.Note);
    }

    [Fact]
    public void Only_the_power_saver_plan_is_wrong_in_itself()
    {
        Assert.NotNull(SetupRules.PowerPlan(SetupRules.PowerSaver, "Risparmio di energia").Problem);
        Assert.Null(SetupRules.PowerPlan("381b4222-f694-41f0-9685-ff5bb260df2e", "Bilanciato").Problem);
    }

    [Fact]
    public void Windows_switches_are_read_with_their_defaults()
    {
        Assert.Equal("accesa", SetupRules.GameMode(null).Value); // Never written: on by default.
        Assert.NotNull(SetupRules.GameMode(0).Problem);
        Assert.Equal("predefinita", SetupRules.HardwareScheduling(null).Value);
        Assert.Equal("accesa", SetupRules.HardwareScheduling(2).Value);
        Assert.NotNull(SetupRules.MemoryIntegrity(1).Note);
        Assert.Null(SetupRules.MemoryIntegrity(0).Note);
        Assert.Equal("spenta", SetupRules.MouseAcceleration("0").Value);
        Assert.Equal("accesa", SetupRules.MouseAcceleration("1").Value);

        var dx = SetupRules.DirectX("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;").ToList();
        Assert.Equal(["accesa", "spenta"], dx.Select(r => r.Value));
        Assert.All(SetupRules.DirectX(null), r => Assert.Equal("predefinita", r.Value));
    }

    [Fact]
    public void Memory_is_described_by_what_is_installed_not_by_how_fast_it_runs()
    {
        var a = new MemoryModule(8192, "DDR4", 3200, 3200, "Crucial", "BL8G32C16U4BL.M8FE");
        Assert.Equal("2 × 8 GB DDR4 BL8G32C16U4BL.M8FE", SetupRules.DescribeMemory([a, a with { ConfiguredMhz = 2133 }]));
        Assert.Null(SetupRules.DescribeMemory([]));
    }
}

public class SmbiosTests
{
    /// <summary>Builds one structure the way firmware stores it: fixed part, then its strings, then a closing zero.</summary>
    private static byte[] Structure(byte type, byte[] body, params string[] strings)
    {
        var bytes = new List<byte> { type, (byte)(body.Length + 4), 0, 0 };
        bytes.AddRange(body);
        foreach (var s in strings)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(s));
            bytes.Add(0);
        }
        bytes.Add(0);
        if (strings.Length == 0) bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Table(params byte[][] structures)
    {
        var table = structures.SelectMany(s => s).ToArray();
        return [0, 3, 3, 0, .. BitConverter.GetBytes((uint)table.Length), .. table];
    }

    private static byte[] MemoryDevice(int sizeMb, byte kind, int rated, int configured)
    {
        var body = new byte[0x22 - 4];
        void Word(int offset, int value) => BitConverter.TryWriteBytes(body.AsSpan(offset - 4), (ushort)value);
        Word(0x0C, sizeMb);
        body[0x12 - 4] = kind;
        Word(0x15, rated);
        body[0x17 - 4] = 1; // manufacturer = first string
        body[0x1A - 4] = 2; // part number = second string
        Word(0x20, configured);
        return body;
    }

    [Fact]
    public void Memory_modules_board_and_bios_are_read_from_the_firmware_table()
    {
        var bios = new byte[0x14];
        bios[0x05 - 4] = 2; // version = second string
        var board = new byte[0x0B];
        board[0x04 - 4] = 1;
        board[0x05 - 4] = 2;

        var table = Smbios.Parse(Table(
            Structure(0, bios, "American Megatrends Inc.", "3607", "03/18/2024"),
            Structure(2, board, "ASUSTeK COMPUTER INC.", "ROG STRIX X570-F GAMING"),
            Structure(17, MemoryDevice(8192, 0x1A, 3200, 3200), "Crucial Technology", "BL8G32C16U4BL.M8FE  "),
            Structure(17, MemoryDevice(0, 0x1A, 0, 0)), // empty slot, no strings
            Structure(17, MemoryDevice(8192, 0x1A, 3200, 2133), "Crucial Technology", "BL8G32C16U4BL.M8FE"),
            Structure(127, [])));

        Assert.Equal("3607", Smbios.BiosVersion(table));
        Assert.Equal("ASUSTeK COMPUTER INC. ROG STRIX X570-F GAMING", Smbios.Board(table));
        var modules = Smbios.Memory(table);
        Assert.Equal(2, modules.Count);
        Assert.Equal(new MemoryModule(8192, "DDR4", 3200, 3200, "Crucial Technology", "BL8G32C16U4BL.M8FE"), modules[0]);
        Assert.Equal(2133, modules[1].ConfiguredMhz);
    }

    [Fact]
    public void A_truncated_or_empty_table_gives_nothing_instead_of_failing()
    {
        Assert.Empty(Smbios.Parse([]));
        Assert.Empty(Smbios.Parse([0, 3, 3, 0, 200, 0, 0, 0, 17, 40, 0]));
    }
}

public class WindowsPcProbeTests
{
    /// <summary>The real PC: whatever it is, the probe must answer without failing and with sensible shapes.</summary>
    [Fact]
    public void The_real_machine_can_be_read()
    {
        var probe = new WindowsPcProbe();

        var settings = probe.ReadSettings();
        var hardware = probe.ReadHardware();
        var versions = probe.ReadVersions();

        Assert.Contains(settings, r => r.Key == "power.plan" && r.Value.Length > 0);
        Assert.Contains(settings, r => r.Key == "windows.gamemode");
        Assert.Equal(settings.Count, settings.Select(r => r.Key).Distinct().Count());
        Assert.True(hardware.ContainsKey("Processore"));
        Assert.StartsWith("2", versions["Windows"][..1]); // "24H2", "25H2", "26H2"...

        // Set OPTLIFE_SETUP_DUMP to a file to see what was found on this PC.
        if (Environment.GetEnvironmentVariable("OPTLIFE_SETUP_DUMP") is { Length: > 0 } file)
            File.WriteAllLines(file,
            [
                .. settings.Select(r => $"{r.Key,-34} {r.Label}: {r.Value}{(r.Problem is null ? "" : $"   !! {r.Problem}")}{(r.Note is null ? "" : "   (nota)")}"),
                "", .. hardware.Select(h => $"{h.Key}: {h.Value}"),
                "", .. versions.Select(v => $"{v.Key}: {v.Value}"),
            ]);
    }
}

public sealed class FirmwareRulesTests
{
    [Fact]
    public void Bios_settings_are_judged_and_resizable_bar_is_advised_only_where_the_card_supports_it()
    {
        var all = SetupRules.Firmware(new FirmwareFacts("3607", true, false, 2, true, "NVIDIA GeForce RTX 3080", 256, 8192, 4, 4, 8, 16))
            .ToDictionary(r => r.Key);
        Assert.Equal(true, all["bios.mode"].Good);
        Assert.Equal(false, all["bios.secure_boot"].Good);
        Assert.Equal(true, all["bios.tpm"].Good);
        Assert.Equal(false, all["bios.rebar"].Good); // An RTX 30 supports it: off is worth a look.
        Assert.Equal(false, all["bios.pcie"].Good);  // 8 lanes of 16.
        Assert.Contains("8", all["bios.pcie"].Advice);
        var old = SetupRules.Firmware(new FirmwareFacts(GpuName: "NVIDIA GeForce GTX 1060", Bar1Mb: 256, VramMb: 6144)).Single();
        Assert.Null(old.Good); // Not supported: only how it stands.
        Assert.Empty(SetupRules.Firmware(new FirmwareFacts()));
    }
}
