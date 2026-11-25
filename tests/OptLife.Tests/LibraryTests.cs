using System.Text;
using OptLife.Core;
using OptLife.Modules.Library;

namespace OptLife.Tests;

internal sealed class FakeLibraryEnvironment : ILibraryEnvironment
{
    public Screen Screen { get; set; } = new(2560, 1440, 144);
    public double Vram { get; set; } = 8;
    public string DocumentsFolder { get; set; } = "";
    /// <summary>url → content; anything else is "not there".</summary>
    public Dictionary<string, byte[]> Web { get; } = [];
    public List<string> Asked { get; } = [];
    public List<(string Exe, string Args, string Folder)> Launched { get; } = [];
    public List<string> Recycled { get; } = [];

    public Task<bool> Download(string url, string path, CancellationToken ct)
    {
        Asked.Add(url);
        if (!Web.TryGetValue(url, out var bytes)) return Task.FromResult(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return Task.FromResult(true);
    }

    public Screen PrimaryScreen() => Screen;
    public double VramGb() => Vram;
    public string Documents() => DocumentsFolder;
    public string AppDataFolder { get; set; } = "";
    public string AppData() => AppDataFolder;
    public string ProgramDataFolder { get; set; } = Path.Combine(Path.GetTempPath(), "optlife-tests", "no-programdata");
    public string ProgramData() => ProgramDataFolder;
    public string? Steam { get; set; }
    public string? SteamFolder() => Steam;
    public List<UninstallEntry> Programs { get; } = [];
    public IReadOnlyList<UninstallEntry> Uninstall() => Programs;
    public List<string> Drives { get; } = [];
    public IReadOnlyList<string> FixedDrives() => Drives;
    public List<string> Packages { get; } = [];
    public IReadOnlyList<string> PackageFullNames() => Packages;
    public string Explorer() => @"C:\Windows\explorer.exe";
    public ActionResult Launch(string exe, string arguments, string folder)
    {
        Launched.Add((exe, arguments, folder));
        return new ActionResult(true, "");
    }
    public ActionResult Open(string folder) => new(true, "");
    public bool Recycle(string path)
    {
        Recycled.Add(path);
        File.Delete(path);
        return true;
    }
}

public sealed class LibraryTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optlife-tests", "library-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _box.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Reading discs ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("BOOT2 = cdrom0:\\SLES_820.09;1\r\nVER = 1.00\r\nVMODE = PAL", "SLES_820.09", "SLES-82009")]
    [InlineData("BOOT2=cdrom0:\\SKATE\\SKATE2.ELF;1", "SKATE\\SKATE2.ELF", "SKATE2ELF")]
    public void The_started_program_and_serial_come_from_system_cnf(string cnf, string boot, string serial)
    {
        Assert.Equal(boot, DiscReader.BootPath(cnf));
        Assert.Equal(serial, DiscReader.SerialFrom(boot));
    }

    [Fact]
    public void The_crc_is_the_xor_of_the_words() =>
        Assert.Equal(0x01020304u ^ 0x10203040u, DiscReader.ElfCrc([0x04, 0x03, 0x02, 0x01, 0x40, 0x30, 0x20, 0x10, 0xFF]));

    [Fact]
    public void A_small_iso_is_read_without_unpacking()
    {
        var elf = new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 };
        var iso = Iso(("SYSTEM.CNF", Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLUS_123.45;1\n")), ("SLUS_123.45", elf));
        var disc = DiscReader.ReadPs2(new MemoryStream(iso));
        Assert.NotNull(disc);
        Assert.Equal("SLUS-12345", disc.Serial);
        Assert.Equal("00000003", disc.Crc);
        Assert.Null(DiscReader.ReadPs2(new MemoryStream(new byte[40000])));
    }

    /// <summary>The real disc on this PC, when there is one: PCSX2's own log says SLES-82009 with CRC 093E7D52.</summary>
    [Fact]
    public void A_real_ps2_disc_gives_what_pcsx2_says()
    {
        var path = Environment.GetEnvironmentVariable("OPTLIFE_LIBRARY_ISO");
        if (string.IsNullOrEmpty(path)) return;
        Assert.True(File.Exists(path), path);
        using var s = File.OpenRead(path);
        var disc = DiscReader.ReadPs2(s);
        Assert.Equal("SLES-82009", disc?.Serial);
        Assert.Equal("093E7D52", disc?.Crc);
    }

    [Fact]
    public void A_param_sfo_gives_id_title_and_category()
    {
        var info = DiscReader.ReadSfo(Sfo(("CATEGORY", "DG"), ("TITLE", "METAL GEAR SOLID 4"), ("TITLE_ID", "BLES00246")));
        Assert.Equal(new SfoInfo("BLES00246", "METAL GEAR SOLID 4", "DG"), info);
        Assert.Null(DiscReader.ReadSfo([1, 2, 3]));
    }

    // ── Names and covers ─────────────────────────────────────────────────────

    private const string Dat = """
        clrmamepro (
        	name "Sony - PlayStation 2"
        )

        game (
        	name "Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It)"
        	serial "SLES-82009"
        	rom ( name "Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It).iso" size 1 crc 0 serial "SLES-82009" )
        )
        game (
        	name "Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It) (Rev 1)"
        	serial "SLES-82009"
        )
        """;

    [Fact]
    public void The_list_names_a_game_by_serial_preferring_the_files_own_name()
    {
        var map = LibraryRules.ParseDat(new StringReader(Dat));
        Assert.Equal(2, map["SLES-82009"].Count);
        Assert.Equal("Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It) (Rev 1)",
            LibraryRules.DatName(map, "SLES-82009", "Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It) (Rev 1).iso"));
        Assert.Equal("Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It)", LibraryRules.DatName(map, "SLES-82009", "mgs2.iso"));
        Assert.Null(LibraryRules.DatName(map, "SLUS-00000", "x.iso"));
        Assert.Equal("Metal Gear Solid 2 - Substance", LibraryRules.CleanTitle("Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It)"));
        Assert.Equal("BLES-00246", LibraryRules.DashedId("BLES00246"));
    }

    [Fact]
    public void Covers_are_tried_from_the_exact_name_to_other_regions()
    {
        var names = LibraryRules.CoverNames("Metal Gear Solid 4 - Guns of the Patriots (Europe, Australia) (En,Fr,De,Es,It) (v01.01)");
        Assert.Equal("Metal Gear Solid 4 - Guns of the Patriots (Europe, Australia) (En,Fr,De,Es,It) (v01.01)", names[0]);
        Assert.Contains("Metal Gear Solid 4 - Guns of the Patriots (Europe, Australia)", names);
        Assert.Contains("Metal Gear Solid 4 - Guns of the Patriots (USA)", names);
        Assert.Equal("Tom _ Jerry", LibraryRules.SafeName("Tom & Jerry"));
        Assert.Equal("https://raw.githubusercontent.com/libretro-thumbnails/Sony_-_PlayStation_2/master/Named_Boxarts/Ape%20Escape%20%28USA%29.png",
            LibraryRules.CoverUrl(LibraryRules.Ps2, "Ape Escape (USA)"));
    }

    // ── Presets ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1440, GpuLevel.High, Preset.Balanced, 3, 150)]
    [InlineData(1440, GpuLevel.High, Preset.Quality, 4, 200)]
    [InlineData(1440, GpuLevel.High, Preset.Performance, 2, 100)]
    [InlineData(1080, GpuLevel.High, Preset.Balanced, 2, 100)]
    [InlineData(1080, GpuLevel.High, Preset.Quality, 3, 150)]
    [InlineData(2160, GpuLevel.VeryHigh, Preset.Quality, 5, 300)]
    [InlineData(2160, GpuLevel.Low, Preset.Quality, 2, 100)]
    public void Presets_follow_the_screen_and_the_card(int height, GpuLevel level, Preset preset, int pcsx2, int rpcs3)
    {
        var screen = new Screen(height * 16 / 9, height, 144);
        Assert.Equal(pcsx2, LibraryRules.Pcsx2Scale(screen, level, preset));
        Assert.Equal(rpcs3, LibraryRules.Rpcs3Scale(screen, level, preset));
    }

    [Fact]
    public void Sync_follows_the_screens_hz_and_choices_read_in_both_languages()
    {
        Assert.True(LibraryRules.Vsync(new Screen(1920, 1080, 60)));
        Assert.False(LibraryRules.Vsync(new Screen(2560, 1440, 144)));
        Assert.Equal(Preset.Quality, LibraryRules.FromWord("Quality"));
        Assert.Equal(Preset.Performance, LibraryRules.FromWord("Prestazioni"));
        Assert.Equal(Preset.Balanced, LibraryRules.FromWord("anything"));
    }

    // ── Writing only our keys ────────────────────────────────────────────────

    [Fact]
    public void Ini_values_are_set_in_place_and_the_rest_is_kept()
    {
        var before = "[EmuCore]\r\nEnableCheats = true\r\n\r\n[EmuCore/GS]\r\nupscale_multiplier = 1\r\nAspectRatio = 16:9\r\n";
        var after = LibraryRules.SetIni(before, [new("EmuCore/GS", "upscale_multiplier", "3"), new("EmuCore/GS", "VsyncEnable", "false")]);
        Assert.Contains("EnableCheats = true", after);
        Assert.Contains("AspectRatio = 16:9", after);
        Assert.Equal("3", LibraryRules.GetIni(after, "EmuCore/GS", "upscale_multiplier"));
        Assert.Equal("false", LibraryRules.GetIni(after, "EmuCore/GS", "VsyncEnable"));
        Assert.DoesNotContain("\n\n\n", after.Replace("\r", ""));
        Assert.Contains("\r\n", after);
        var fresh = LibraryRules.SetIni("", [new("EmuCore/GS", "upscale_multiplier", "3")]);
        Assert.Equal("[EmuCore/GS]\nupscale_multiplier = 3\n", fresh);
    }

    [Fact]
    public void Yaml_values_touch_only_the_settings_level()
    {
        var before = "Core:\n  PPU Threads: 2\nVideo:\n  Renderer: OpenGL\n  Resolution Scale: 100\n  Vulkan:\n    Adapter: \"\"\n  Frame limit: 60\nAudio:\n  Renderer: Cubeb\n";
        var values = new ConfigValue[] { new("Video", "Renderer", "Vulkan"), new("Video", "Resolution Scale", "150"), new("Video", "Shader Mode", "Async Shader Recompiler") };
        var after = LibraryRules.SetYaml(before, values);
        Assert.Contains("  PPU Threads: 2", after);
        Assert.Contains("Audio:\n  Renderer: Cubeb", after);       // Another block's Renderer stays.
        Assert.Contains("    Adapter: \"\"", after);
        Assert.Equal("Vulkan", LibraryRules.GetYaml(after, "Video", "Renderer"));
        Assert.Equal("Async Shader Recompiler", LibraryRules.GetYaml(after, "Video", "Shader Mode"));
        Assert.True(LibraryRules.Holds(after, values, yaml: true));
        Assert.False(LibraryRules.Holds(before, values, yaml: true));
        Assert.Equal("Video:\n  Renderer: Vulkan\n", LibraryRules.SetYaml("", [new("Video", "Renderer", "Vulkan")]));
    }

    [Fact]
    public void Rpcs3_remembers_where_its_disc_games_are() =>
        Assert.Equal([("BLES00246", @"E:\Games\PS3\Metal gear 4")], LibraryRules.Rpcs3GamesYml("BLES00246: E:/Games/PS3/Metal gear 4/\nbad line\n"));

    // ── The module, on files in a folder ─────────────────────────────────────

    private (LibraryModule Module, FakeLibraryEnvironment Env, string Pcsx2, string Rpcs3) Setup()
    {
        var ps2 = Directory.CreateDirectory(Path.Combine(_root, "Games", "PS2")).FullName;
        var ps3 = Directory.CreateDirectory(Path.Combine(_root, "Games", "PS3")).FullName;
        File.WriteAllBytes(Path.Combine(ps2, "Metal Gear Solid 2 - Substance (Europe) (En,Fr,De,Es,It).iso"),
            Iso(("SYSTEM.CNF", Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLES_820.09;1\n")), ("SLES_820.09", [0x52, 0x7D, 0x3E, 0x09])));
        File.WriteAllBytes(Path.Combine(ps2, "Other.chd"), [1, 2, 3]);
        var game = Directory.CreateDirectory(Path.Combine(ps3, "Metal gear 4", "PS3_GAME", "USRDIR")).FullName;
        File.WriteAllBytes(Path.Combine(game, "EBOOT.BIN"), [0]);
        File.WriteAllBytes(Path.Combine(ps3, "Metal gear 4", "PS3_GAME", "PARAM.SFO"), Sfo(("CATEGORY", "DG"), ("TITLE", "METAL GEAR SOLID 4"), ("TITLE_ID", "BLES00246")));
        File.WriteAllBytes(Path.Combine(ps3, "Metal gear 4", "PS3_GAME", "ICON0.PNG"), [0x89, 0x50]);

        var pcsx2 = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Emu", "PCSX2")).FullName, "pcsx2-qt.exe");
        File.WriteAllBytes(pcsx2, [0]);
        File.WriteAllText(Path.Combine(_root, "Emu", "PCSX2", "portable.txt"), "");
        var rpcs3Dir = Directory.CreateDirectory(Path.Combine(_root, "Emu", "RPCS3")).FullName;
        var rpcs3 = Path.Combine(rpcs3Dir, "rpcs3.exe");
        File.WriteAllBytes(rpcs3, [0]);
        // A patch installed in RPCS3's disk: not a game of its own.
        var patch = Directory.CreateDirectory(Path.Combine(rpcs3Dir, "dev_hdd0", "game", "BLES00246", "USRDIR")).FullName;
        File.WriteAllBytes(Path.Combine(patch, "EBOOT.BIN"), [0]);
        File.WriteAllBytes(Path.Combine(rpcs3Dir, "dev_hdd0", "game", "BLES00246", "PARAM.SFO"), Sfo(("CATEGORY", "GP"), ("TITLE", "MGS4 update"), ("TITLE_ID", "BLES00246")));

        _box.Store.InsertEvents("emulation", [new NewEvent(_box.Clock.GetUtcNow(), "emulators_found", Severity.Info, "found", "emulators_found", Data: new Dictionary<string, string>
        {
            ["games.ps2"] = ps2, ["games.ps3"] = ps3, ["exe.ps2.pcsx2"] = pcsx2, ["exe.ps3.rpcs3"] = rpcs3,
        })]);

        var env = new FakeLibraryEnvironment { DocumentsFolder = Path.Combine(_root, "Documents") };
        env.Web[LibraryRules.DatUrl(LibraryRules.Ps2)] = Encoding.UTF8.GetBytes(Dat);
        env.Web[LibraryRules.CoverUrl(LibraryRules.Ps2, "Metal Gear Solid 2 - Substance (USA)")] = [0x89, 0x50, 0x4E, 0x47];
        return (new LibraryModule(env), env, pcsx2, rpcs3);
    }

    private IReadOnlyList<CheckItem> Board() =>
        CheckItem.FromData(_box.Store.QueryEvents(_box.Clock.GetUtcNow().AddDays(-1), _box.Clock.GetUtcNow().AddDays(1), "library", LibraryModule.BoardKind).MaxBy(e => e.Time)!.Data);

    private Task<ActionResult> Do(LibraryModule module, string action, string argument) =>
        _box.Runner.RunActionAsync(module, module.Actions.Single(a => a.Id == action), "test", true, argument, CancellationToken.None);

    [Fact]
    public async Task Games_are_found_named_and_given_covers()
    {
        var (module, env, _, _) = Setup();
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true));
        var board = Board();
        Assert.Equal(3, board.Count);
        var mgs2 = board.Single(i => i.Label == "Metal Gear Solid 2 - Substance");
        Assert.Equal("PlayStation 2", mgs2.Group);
        Assert.Contains("SLES-82009", mgs2.Value);
        Assert.EndsWith("Metal Gear Solid 2 - Substance (USA).png", mgs2.Image);
        Assert.Equal(LibraryModule.Play, mgs2.Buttons![0].ActionId);
        Assert.Contains("Other", board.Select(i => i.Label));
        var mgs4 = board.Single(i => i.Group == "PlayStation 3");
        Assert.Equal("METAL GEAR SOLID 4", mgs4.Label);
        Assert.EndsWith("ICON0.PNG", mgs4.Image);   // No box art online: the game's own picture.

        // A second look reads nothing again and asks the internet nothing more for the same games.
        var asked = env.Asked.Count;
        _box.Clock.Now = _box.Clock.Now.AddHours(1);
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true));
        Assert.Equal(asked, env.Asked.Count);
    }

    [Fact]
    public async Task Play_writes_the_presets_values_and_starts_full_screen_as_the_person()
    {
        var (module, env, pcsx2, _) = Setup();
        await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true);
        var result = await Do(module, LibraryModule.Play, "ps2:SLES-82009");
        Assert.True(result.Ok, result.Detail);
        var ini = Path.Combine(Path.GetDirectoryName(pcsx2)!, "gamesettings", "SLES-82009_093E7D52.ini");
        Assert.Equal("3", LibraryRules.GetIni(File.ReadAllText(ini), "EmuCore/GS", "upscale_multiplier"));
        var (exe, args, _) = env.Launched.Single();
        Assert.Equal(pcsx2, exe);
        Assert.Contains("-fullscreen", args);
        Assert.EndsWith(".iso\"", args);
        Assert.Contains(_box.Store.QueryEvents(_box.Clock.GetUtcNow().AddDays(-1), _box.Clock.GetUtcNow().AddDays(1), "library", LibraryModule.Launched),
            e => e.Data["game"] == "ps2:SLES-82009");

        // Choosing Quality for this game rewrites it; undo removes the file OptLife created (to the Recycle Bin).
        Assert.True((await Do(module, LibraryModule.SetPreset, "ps2:SLES-82009|quality")).Ok);
        Assert.Equal("4", LibraryRules.GetIni(File.ReadAllText(ini), "EmuCore/GS", "upscale_multiplier"));
        Assert.True((await Do(module, LibraryModule.UndoSettings, "ps2:SLES-82009")).Ok);
        Assert.False(File.Exists(ini));
        Assert.Contains(ini, env.Recycled);
    }

    [Fact]
    public async Task A_settings_file_the_person_wrote_is_left_alone_until_a_preset_is_chosen()
    {
        var (module, env, _, rpcs3) = Setup();
        var custom = Path.Combine(Path.GetDirectoryName(rpcs3)!, "config", "custom_configs", "config_BLES00246.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        const string mine = "Core:\n  PPU Threads: 2\nVideo:\n  Renderer: Vulkan\n  Resolution Scale: 200\n";
        File.WriteAllText(custom, mine);
        await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true);
        Assert.Contains("your settings", Board().Single(i => i.Group == "PlayStation 3").Value.Replace("impostazioni tue", "your settings"));

        Assert.True((await Do(module, LibraryModule.Play, "ps3:BLES00246")).Ok);
        Assert.Equal(mine, File.ReadAllText(custom));                     // Untouched.
        Assert.Contains("--no-gui --fullscreen", env.Launched.Single().Args);

        Assert.True((await Do(module, LibraryModule.SetPreset, "ps3:BLES00246|balanced")).Ok);
        var written = File.ReadAllText(custom);
        Assert.Equal("150", LibraryRules.GetYaml(written, "Video", "Resolution Scale"));
        Assert.Contains("  PPU Threads: 2", written);                      // The rest stays the person's.

        Assert.True((await Do(module, LibraryModule.UndoSettings, "ps3:BLES00246")).Ok);
        Assert.Equal(mine, File.ReadAllText(custom));                     // Back from the copy.
    }

    [Fact]
    public async Task Without_the_emulator_play_says_where_to_install_it()
    {
        var (module, _, pcsx2, _) = Setup();
        File.Delete(pcsx2);
        await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true);
        Assert.Equal(CheckStatus.Watch, Board().Single(i => i.Label == "Metal Gear Solid 2 - Substance").Status);
        var result = await Do(module, LibraryModule.Play, "ps2:SLES-82009");
        Assert.False(result.Ok);
        Assert.Contains("Emulazione", result.Detail);
    }

    // ── Piece 2: PS1, PSP, GameCube / Wii ────────────────────────────────────

    [Fact]
    public void Ps1_reads_boot_not_boot2_and_cue_sheets_name_their_track()
    {
        Assert.Equal("SLES_013.70", DiscReader.BootPath("BOOT = cdrom:\\SLES_013.70;1\r\nTCB = 4", "BOOT"));
        Assert.Null(DiscReader.BootPath("BOOT2 = cdrom0:\\SLES_820.09;1", "BOOT"));
        Assert.Equal("SLES-01370", DiscReader.ReadPs1Serial(new MemoryStream(Iso(("SYSTEM.CNF", Encoding.ASCII.GetBytes("BOOT = cdrom:\\SLES_013.70;1\n"))))));
        Assert.Equal("Metal Gear Solid (Europe) (Disc 1).bin", DiscReader.CueFirstFile("FILE \"Metal Gear Solid (Europe) (Disc 1).bin\" BINARY\n  TRACK 01 MODE2/2352"));
        Assert.Equal(2, LibraryRules.DiscNumber("Metal Gear Solid (Europe) (Disc 2).cue"));
        Assert.Equal(0, LibraryRules.DiscNumber("Crash Bandicoot (Europe).cue"));
    }

    [Fact]
    public void Psp_and_nintendo_discs_name_themselves()
    {
        var psp = DiscReader.ReadPsp(new MemoryStream(IsoInFolder("PSP_GAME", "PARAM.SFO", Sfo(("DISC_ID", "ULES00151"), ("TITLE", "Lumines")))));
        Assert.Equal("ULES00151", psp?.TitleId);
        Assert.Equal("Lumines", psp?.Title);
        Assert.Equal(new NintendoDisc("GALE01", "Super Smash Bros Melee", false), DiscReader.ReadNintendo(new MemoryStream(GameCube("GALE01", "Super Smash Bros Melee"))));
        Assert.Equal("GALP01", DiscReader.ReadNintendo(new MemoryStream(GameCube("GALP01", "Melee", rvz: true)))?.Id);
        Assert.Null(DiscReader.ReadNintendo(new MemoryStream(new byte[0x400])));
        Assert.Equal("https://art.gametdb.com/wii/cover/EN/GALP01.png", LibraryRules.GameTdbUrls("GALP01")[0]);
        Assert.Equal("https://art.gametdb.com/wii/cover/US/GALE01.png", LibraryRules.GameTdbUrls("GALE01")[0]);
    }

    [Theory]
    [InlineData(EmuKind.DuckStation, Preset.Quality, 6)]
    [InlineData(EmuKind.DuckStation, Preset.Balanced, 5)]
    [InlineData(EmuKind.Ppsspp, Preset.Quality, 6)]
    [InlineData(EmuKind.Dolphin, Preset.Quality, 3)]
    [InlineData(EmuKind.Dolphin, Preset.Balanced, 2)]
    [InlineData(EmuKind.Dolphin, Preset.Performance, 1)]
    public void Every_emulator_scales_from_its_consoles_own_picture(EmuKind kind, Preset preset, int expected) =>
        Assert.Equal(expected, LibraryRules.Scale(kind, new Screen(2560, 1440, 144), GpuLevel.High, preset));

    [Theory]
    [InlineData(EmuKind.DuckStation, "-batch -fullscreen -- \"g.cue\"")]
    [InlineData(EmuKind.Ppsspp, "--fullscreen \"g.cue\"")]
    [InlineData(EmuKind.Dolphin, "-b -C Dolphin.Display.Fullscreen=True -e \"g.cue\"")]
    public void Each_emulator_opens_the_game_full_screen(EmuKind kind, string expected) =>
        Assert.Equal(expected, LibraryRules.Arguments(kind, "g.cue", fullscreen: true));

    [Fact]
    public async Task Ps1_discs_are_one_card_and_dolphin_games_get_their_gametdb_cover()
    {
        var ps1 = Directory.CreateDirectory(Path.Combine(_root, "G", "PS1", "Metal Gear Solid (Europe) (2 Discs)")).FullName;
        foreach (var disc in new[] { 1, 2 })
        {
            var dir = Directory.CreateDirectory(Path.Combine(ps1, $"Metal Gear Solid (Europe) (Disc {disc})")).FullName;
            var bin = $"Metal Gear Solid (Europe) (Disc {disc}).bin";
            File.WriteAllBytes(Path.Combine(dir, bin), Iso(("SYSTEM.CNF", Encoding.ASCII.GetBytes($"BOOT = cdrom:\\SLES_{(disc == 1 ? "013.70" : "113.70")};1\n"))));
            File.WriteAllText(Path.Combine(dir, $"Metal Gear Solid (Europe) (Disc {disc}).cue"), $"FILE \"{bin}\" BINARY\n  TRACK 01 MODE2/2352\n");
        }
        var gc = Directory.CreateDirectory(Path.Combine(_root, "G", "GC-Wii")).FullName;
        File.WriteAllBytes(Path.Combine(gc, "melee.rvz"), GameCube("GALE01", "Super Smash Bros Melee", rvz: true));
        var duck = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "E", "DuckStation")).FullName, "duckstation-qt-x64-ReleaseLTCG.exe");
        File.WriteAllBytes(duck, [0]);
        var dolphinDir = Directory.CreateDirectory(Path.Combine(_root, "E", "Dolphin")).FullName;
        var dolphin = Path.Combine(dolphinDir, "Dolphin.exe");
        File.WriteAllBytes(dolphin, [0]);
        File.WriteAllText(Path.Combine(dolphinDir, "portable.txt"), "");
        _box.Store.InsertEvents("emulation", [new NewEvent(_box.Clock.GetUtcNow(), "emulators_found", Severity.Info, "found", "emulators_found", Data: new Dictionary<string, string>
        {
            ["games.ps1"] = Path.Combine(_root, "G", "PS1"), ["games.gc-wii"] = gc, ["exe.ps1.duckstation"] = duck, ["exe.gc-wii.dolphin"] = dolphin,
        })]);
        var env = new FakeLibraryEnvironment { DocumentsFolder = Path.Combine(_root, "Documents") };
        env.Web["https://art.gametdb.com/wii/cover/US/GALE01.png"] = [0x89, 0x50];
        var module = new LibraryModule(env);

        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true));
        var board = Board();
        Assert.Equal(2, board.Count);
        var mgs = board.Single(i => i.Group == "PlayStation");
        Assert.Equal("Metal Gear Solid", mgs.Label);
        Assert.Contains("2 dischi", mgs.Value);
        Assert.Contains("SLES-01370", mgs.Value);
        var melee = board.Single(i => i.Group == "GameCube / Wii");
        Assert.Equal("Super Smash Bros Melee", melee.Label);
        Assert.EndsWith("GALE01.png", melee.Image);

        Assert.True((await Do(module, LibraryModule.Play, "ps1:SLES-01370")).Ok);
        Assert.Equal("5", LibraryRules.GetIni(File.ReadAllText(Path.Combine(_root, "Documents", "DuckStation", "gamesettings", "SLES-01370.ini")), "GPU", "ResolutionScale"));
        Assert.EndsWith("(Disc 1).cue\"", env.Launched.Last().Args);

        Assert.True((await Do(module, LibraryModule.Play, "gc-wii:GALE01")).Ok);
        Assert.Equal("2", LibraryRules.GetIni(File.ReadAllText(Path.Combine(dolphinDir, "User", "GameSettings", "GALE01.ini")), "Video_Settings", "InternalResolution"));
        Assert.Contains("-e \"", env.Launched.Last().Args);
    }

    // ── Pieces 3 and 4: Switch, Wii U, PS4, 3DS, DS, retro, arcade, Xbox, Vita ──

    [Fact]
    public void Cartridges_and_packages_name_themselves()
    {
        // NSP: a PFS0 with a ticket named after the rights id.
        var names = Encoding.ASCII.GetBytes("abc.nca\00100F2C0115B6000000000000000004.tik\0");
        var nsp = new MemoryStream();
        nsp.Write(Encoding.ASCII.GetBytes("PFS0"));
        nsp.Write(BitConverter.GetBytes(2));
        nsp.Write(BitConverter.GetBytes(names.Length));
        nsp.Write(new byte[4]);
        nsp.Write(new byte[2 * 24]);
        nsp.Write(names);
        Assert.Equal("0100F2C0115B6000", DiscReader.SwitchTitleId(nsp));

        var cci = new byte[0x4200];
        Encoding.ASCII.GetBytes("NCCH").CopyTo(cci, 0x4100);
        Encoding.ASCII.GetBytes("CTR-P-AMKE").CopyTo(cci, 0x4150);
        Assert.Equal("AMKE", DiscReader.N3dsCode(new MemoryStream(cci)));

        var nds = new byte[0x200];
        Encoding.ASCII.GetBytes("MARIOKART DS").CopyTo(nds, 0);
        Encoding.ASCII.GetBytes("AMCE").CopyTo(nds, 0x0C);
        Assert.Equal(("AMCE", "MARIOKART DS"), DiscReader.DsHeader(new MemoryStream(nds))!.Value);

        Assert.Equal(("AMKP01", "Mario Kart 8"), LibraryRules.WiiUMeta(
            "<menu><product_code type=\"string\">WUP-P-AMKP</product_code><company_code type=\"string\">0001</company_code><longname_en type=\"string\">Mario Kart 8</longname_en></menu>")!.Value);
        Assert.Equal("https://art.gametdb.com/3ds/cover/US/AMKE.jpg", LibraryRules.GameTdbUrls("AMKE", "3ds", "jpg")[0]);
    }

    [Fact]
    public void Retroarch_needs_an_installed_core_and_mame_takes_the_set_name()
    {
        Assert.Null(LibraryRules.RetroArchArguments(@"C:\RA\cores", ["mesen", "nestopia"], @"C:\R\Zelda.nes", true, _ => false));
        Assert.Equal("-f -L \"C:\\RA\\cores\\nestopia_libretro.dll\" \"C:\\R\\Zelda.nes\"",
            LibraryRules.RetroArchArguments(@"C:\RA\cores", ["mesen", "nestopia"], @"C:\R\Zelda.nes", true, p => p.Contains("nestopia")));
        Assert.Equal("-rompath \"C:\\Arcade\" sf2", LibraryRules.MameArguments(@"C:\Arcade\sf2.zip", true));
        Assert.Equal("-F -r PCSE00001", LibraryRules.Arguments(EmuKind.Vita3K, "PCSE00001", true));
        Assert.False(LibraryRules.HasPresets(EmuKind.Ryujinx));
        Assert.True(LibraryRules.HasPresets(EmuKind.Dolphin));
        Assert.Equal(LibraryRules.Consoles.Count, LibraryRules.Consoles.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task Ps4_folders_and_retro_roms_join_without_presets()
    {
        var ps4 = Directory.CreateDirectory(Path.Combine(_root, "G", "PS4", "Games", "CUSA01113", "sce_sys")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(ps4, "eboot.bin"), [0]);
        File.WriteAllBytes(Path.Combine(ps4, "sce_sys", "param.sfo"), Sfo(("CATEGORY", "gd"), ("TITLE", "Gravity Rush Remastered"), ("TITLE_ID", "CUSA01113")));
        var patch = Directory.CreateDirectory(Path.Combine(_root, "G", "PS4", "Games", "CUSA01113-patch", "sce_sys")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(patch, "eboot.bin"), [0]);
        File.WriteAllBytes(Path.Combine(patch, "sce_sys", "param.sfo"), Sfo(("CATEGORY", "gp"), ("TITLE", "Patch"), ("TITLE_ID", "CUSA01113")));
        var retro = Directory.CreateDirectory(Path.Combine(_root, "G", "Retro")).FullName;
        File.WriteAllBytes(Path.Combine(retro, "Super Mario Bros. (World).nes"), [0]);
        var shad = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "E", "shad")).FullName, "shadPS4.exe");
        File.WriteAllBytes(shad, [0]);
        _box.Store.InsertEvents("emulation", [new NewEvent(_box.Clock.GetUtcNow(), "emulators_found", Severity.Info, "found", "emulators_found", Data: new Dictionary<string, string>
        {
            ["games.ps4"] = Path.Combine(_root, "G", "PS4"), ["games.retro"] = retro, ["exe.ps4.shadps4"] = shad,
        })]);
        var env = new FakeLibraryEnvironment();
        var module = new LibraryModule(env);
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true));
        var board = Board();
        var gravity = board.Single(i => i.Group == "PlayStation 4");
        Assert.Equal("Gravity Rush Remastered", gravity.Label);
        Assert.DoesNotContain(gravity.Buttons!, b => b.ActionId == LibraryModule.SetPreset);
        var mario = board.Single(i => i.Group == "NES");
        Assert.Equal(CheckStatus.Watch, mario.Status);   // RetroArch is not there.
        Assert.Contains(env.Asked, u => u.Contains("Nintendo_-_Nintendo_Entertainment_System") && u.Contains("Super%20Mario%20Bros.%20%28World%29"));

        Assert.True((await Do(module, LibraryModule.Play, "ps4:CUSA01113")).Ok);
        Assert.Equal($"-g \"{Path.Combine(ps4, "eboot.bin")}\" --fullscreen true", env.Launched.Single().Args);
    }

    [Fact]
    public void Repacks_find_their_game_and_steam_names_it()
    {
        Assert.True(PcRules.SkipFolder("_CommonRedist"));
        Assert.True(PcRules.SkipFolder("Bonus Content"));
        Assert.False(PcRules.SkipFolder("FF1"));
        var bundle = PcRules.Bundle(@"E:\R\FF", [(@"E:\R\FF\unins000.exe", 1), (@"E:\R\FF\FF1\FINAL FANTASY.exe", 9), (@"E:\R\FF\FF2\FINAL FANTASY II.exe", 9)]);
        Assert.Equal([(@"E:\R\FF\FF1", @"E:\R\FF\FF1\FINAL FANTASY.exe"), (@"E:\R\FF\FF2", @"E:\R\FF\FF2\FINAL FANTASY II.exe")], bundle);
        Assert.Single(PcRules.Bundle(@"E:\R\Disco", [(@"E:\R\Disco\Dead as Disco\Pagoda.exe", 9), (@"E:\R\Disco\Dead as Disco\Pagoda\Binaries\Win64\Pagoda-Win64-Shipping.exe", 90)]));
        Assert.Equal(@"E:\R\Disco\Dead as Disco\Pagoda.exe",
            PcRules.MainExe([(@"E:\R\Disco\Dead as Disco\Pagoda.exe", 9), (@"E:\R\Disco\Dead as Disco\Pagoda\Binaries\Win64\Pagoda-Win64-Shipping.exe", 90)]));

        const string answer = """{"total":2,"items":[{"type":"app","name":"METAL GEAR SOLID V: GROUND ZEROES","id":311340},{"type":"app","name":"METAL GEAR SOLID V: THE PHANTOM PAIN","id":287700}]}""";
        Assert.Equal("287700", PcRules.SteamMatch(answer, "Metal Gear Solid V - The Phantom Pain"));
        Assert.Null(PcRules.SteamMatch(answer, "Pokemon Pokopia"));
        Assert.Contains("term=Metal%20Gear%20Solid%20V%20The%20Phantom%20Pain", PcRules.SteamSearchUrl("Metal Gear Solid V - The Phantom Pain"));
        Assert.Equal("3404260", PcRules.SteamAppOf(PcRules.SteamCoverUrl("3404260")));
        Assert.Equal("https://shared.akamai.steamstatic.com/a/header.jpg", PcRules.SteamHeader("""{"header_image":"https://shared.akamai.steamstatic.com/a/header.jpg"}"""));
    }

    [Fact]
    public void Steam_says_when_a_game_was_last_played_and_names_match_without_signs()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793713410), PcRules.SteamLastPlayed("\"AppState\"\n{\n\t\"appid\"\t\t\"1\"\n\t\"LastPlayed\"\t\t\"1793713410\"\n}"));
        Assert.Null(PcRules.SteamLastPlayed("\"AppState\"\n{\n\t\"LastPlayed\"\t\t\"0\"\n}"));
        Assert.True(PcRules.SameGame("Rocket League®", "RocketLeague"));
        Assert.False(PcRules.SameGame("Hollow Knight", "Hollow Knight: Silksong"));
    }

    // ── PC games ─────────────────────────────────────────────────────────────

    [Fact]
    public void Launcher_files_are_read()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n\t}\n}";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"E:\SteamLibrary"], PcRules.SteamLibraries(vdf));
        Assert.Equal(("1030300", "Hollow Knight: Silksong", "Hollow Knight Silksong"),
            PcRules.SteamApp("\"AppState\"\n{\n\t\"appid\"\t\t\"1030300\"\n\t\"name\"\t\t\"Hollow Knight: Silksong\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Hollow Knight Silksong\"\n}")!.Value);
        Assert.Null(PcRules.SteamApp("\"appid\" \"1\"\n\"name\" \"x\"\n\"StateFlags\" \"2\""));   // Still downloading.
        Assert.True(PcRules.LooksLikeTool("Steamworks Common Redistributables"));

        var epic = PcRules.EpicGame("""{"DisplayName":"Rocket League®","AppName":"Sugar","CatalogNamespace":"9773","CatalogItemId":"5301","InstallLocation":"E:\\racinggame","AppCategories":["public","games","applications"]}""");
        Assert.Equal("pc:epic:Sugar", epic?.Id);
        Assert.Equal("com.epicgames.launcher://apps/9773%3A5301%3ASugar?action=launch&silent=true", epic?.Args);
        Assert.Null(PcRules.EpicGame("""{"DisplayName":"Unreal Engine","AppName":"UE","AppCategories":["engines"]}"""));
        var cache = Convert.ToBase64String(Encoding.UTF8.GetBytes("""[{"id":"5301","keyImages":[{"type":"DieselGameBox","url":"https://cdn/wide"},{"type":"DieselGameBoxTall","url":"https://cdn/tall"}]}]"""));
        Assert.Equal("https://cdn/tall", PcRules.EpicCovers(cache)["5301"]);

        var xbox = PcRules.XboxConfig("""<Game><Identity Name="Microsoft.4297127D64EC6" /><ExecutableList><Executable Name="Minecraft.exe" /></ExecutableList><ShellVisuals DefaultDisplayName="Minecraft Launcher" Square150x150Logo="GraphicsLogo.png" /></Game>""");
        Assert.Equal(("Microsoft.4297127D64EC6", "Minecraft Launcher", "GraphicsLogo.png", "Game"), xbox!.Value);
        Assert.Equal("Microsoft.4297127D64EC6_8wekyb3d8bbwe", PcRules.FamilyName("Microsoft.4297127D64EC6", ["Other_1_x64__abc", "Microsoft.4297127D64EC6_1.0.0.0_x64__8wekyb3d8bbwe"]));
        Assert.Equal(@"C:\G\game.exe", PcRules.MainExe([(@"C:\G\unins000.exe", 9000), (@"C:\G\game.exe", 100), (@"C:\G\bin\helper.exe", 99999)]));
    }

    [Fact]
    public void Battle_net_riot_and_ea_games_come_from_the_installed_programs()
    {
        var games = PcRules.FromUninstall(
        [
            new("Battle.net", "Battle.net", "Blizzard Entertainment", @"C:\Program Files (x86)\Battle.net", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --uid=battle.net", ""),
            new("Overwatch", "Overwatch", "Blizzard Entertainment", @"E:\shootergame", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=prometheus --displayname=\"Overwatch\"", @"E:\shootergame\_retail_\Overwatch.exe"),
            new("Riot Game league_of_legends.live", "League of Legends", "Riot Games, Inc", "C:/Riot Games/League of Legends", "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=league_of_legends --uninstall-patchline=live", ""),
            new("Riot Game Riot_Client.", "Riot Client", "Riot Games, Inc", "C:/Riot Games/Riot Client/", "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=Riot_Client --uninstall-patchline=", ""),
        ]).ToList();
        Assert.Equal(2, games.Count);
        var ow = games.Single(g => g.Launcher == PcRules.BattleNet);
        Assert.Equal(@"C:\Program Files (x86)\Battle.net\Battle.net.exe", ow.Exe);
        Assert.Equal("--exec=\"launch Pro\"", ow.Args);
        var lol = games.Single(g => g.Launcher == PcRules.Riot);
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", lol.Exe);
        Assert.Equal("--launch-product=league_of_legends --launch-patchline=live", lol.Args);
    }

    [Fact]
    public async Task Pc_games_join_the_library_and_start_through_their_launcher()
    {
        var steam = Directory.CreateDirectory(Path.Combine(_root, "Steam", "steamapps")).Parent!.FullName;
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace("\\", "\\\\")}\"\n\t}}\n}}");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1030300.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1030300\"\n\t\"name\"\t\t\"Hollow Knight: Silksong\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Silksong\"\n}");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_228980.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"228980\"\n\t\"name\"\t\t\"Steamworks Common Redistributables\"\n\t\"StateFlags\"\t\t\"4\"\n}");
        var cover = Path.Combine(Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", "1030300", "abc")).FullName, "library_capsule.jpg");
        File.WriteAllBytes(cover, [1]);
        var repack = Directory.CreateDirectory(Path.Combine(_root, "Repack", "Some Game")).FullName;
        File.WriteAllBytes(Path.Combine(repack, "SomeGame.exe"), new byte[50]);
        File.WriteAllBytes(Path.Combine(repack, "unins000.exe"), new byte[500]);
        _box.Config.UpdateModule("library", m => m.Settings["pcFolders"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { Path.Combine(_root, "Repack") }));

        var env = new FakeLibraryEnvironment { Steam = steam };
        var module = new LibraryModule(env);
        Assert.True(await _box.Runner.RunTaskAsync(module, module.Tasks.Single(), CancellationToken.None, onDemand: true));
        var board = Board();
        var silksong = board.Single(i => i.Label == "Hollow Knight: Silksong");
        Assert.Equal("Steam", silksong.Group);
        Assert.Equal("PC", silksong.Kind);
        Assert.Equal(cover, silksong.Image);
        Assert.DoesNotContain(board, i => i.Label.Contains("Redistributables"));
        Assert.Contains(board, i => i.Label == "Some Game" && i.Group == PcRules.Show(PcRules.Folder));

        Assert.True((await Do(module, LibraryModule.Play, "pc:steam:1030300")).Ok);
        Assert.Equal((@"C:\Windows\explorer.exe", "\"steam://rungameid/1030300\""), (env.Launched[0].Exe, env.Launched[0].Args));
        var folderGame = board.Single(i => i.Label == "Some Game").Buttons![0].Argument!;
        Assert.True((await Do(module, LibraryModule.Play, folderGame)).Ok);
        Assert.EndsWith("SomeGame.exe", env.Launched[1].Exe);

        // Removed by hand: gone from the page, back with "bring back".
        Assert.True((await Do(module, LibraryModule.Hide, "pc:steam:1030300")).Ok);
        Assert.DoesNotContain(Board(), i => i.Label == "Hollow Knight: Silksong");
        Assert.True((await Do(module, LibraryModule.Unhide, "")).Ok);
        Assert.Contains(Board(), i => i.Label == "Hollow Knight: Silksong");
    }

    // ── Test images ──────────────────────────────────────────────────────────

    /// <summary>A minimal ISO 9660 image: volume descriptor at sector 16, root directory at 18, files from 19.</summary>
    private static byte[] Iso(params (string Name, byte[] Data)[] files)
    {
        const int sector = 2048;
        var fileSectors = files.Select(f => Math.Max(1, (f.Data.Length + sector - 1) / sector)).ToArray();
        var image = new byte[(19 + fileSectors.Sum()) * sector];
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, 16 * sector + 1);
        image[16 * sector] = 1;
        WriteRecord(image, 16 * sector + 156, 18, sector, dir: true, "\0");
        var at = 18 * sector;
        var lba = 19;
        for (var i = 0; i < files.Length; i++)
        {
            at += WriteRecord(image, at, lba, files[i].Data.Length, dir: false, files[i].Name + ";1");
            files[i].Data.CopyTo(image, lba * sector);
            lba += fileSectors[i];
        }
        return image;
    }

    /// <summary>An ISO with one folder holding one file: root at 18, the folder at 19, the file from 20.</summary>
    private static byte[] IsoInFolder(string folder, string name, byte[] data)
    {
        const int sector = 2048;
        var image = new byte[(20 + Math.Max(1, (data.Length + sector - 1) / sector)) * sector];
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, 16 * sector + 1);
        image[16 * sector] = 1;
        WriteRecord(image, 16 * sector + 156, 18, sector, dir: true, "\0");
        WriteRecord(image, 18 * sector, 19, sector, dir: true, folder);
        WriteRecord(image, 19 * sector, 20, data.Length, dir: false, name + ";1");
        data.CopyTo(image, 20 * sector);
        return image;
    }

    /// <summary>A GameCube disc header (or the start of an RVZ, whose header copy sits at 0x58).</summary>
    private static byte[] GameCube(string id, string title, bool rvz = false)
    {
        var header = new byte[0x400];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        BitConverter.GetBytes(0x3D9F33C2u).CopyTo(header, 0x1C);
        Encoding.ASCII.GetBytes(title).CopyTo(header, 0x20);
        if (!rvz) return header;
        var file = new byte[0x58 + header.Length];
        Encoding.ASCII.GetBytes("RVZ\u0001").CopyTo(file, 0);
        header.CopyTo(file, 0x58);
        return file;
    }

    private static int WriteRecord(byte[] image, int at, int lba, int size, bool dir, string name)
    {
        var length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        image[at] = (byte)length;
        BitConverter.GetBytes(lba).CopyTo(image, at + 2);
        BitConverter.GetBytes(size).CopyTo(image, at + 10);
        image[at + 25] = (byte)(dir ? 2 : 0);
        image[at + 32] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(image, at + 33);
        return length;
    }

    private static byte[] Sfo(params (string Key, string Value)[] entries)
    {
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var table = new MemoryStream();
        foreach (var (key, value) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            table.Write(BitConverter.GetBytes((ushort)keys.Length));
            table.Write(BitConverter.GetBytes((ushort)0x0204));
            table.Write(BitConverter.GetBytes((uint)bytes.Length));
            table.Write(BitConverter.GetBytes((uint)bytes.Length));
            table.Write(BitConverter.GetBytes((uint)data.Length));
            keys.Write(Encoding.ASCII.GetBytes(key + "\0"));
            data.Write(bytes);
        }
        var keyStart = 20 + (int)table.Length;
        var dataStart = keyStart + (int)keys.Length;
        var head = new MemoryStream();
        head.Write([0, (byte)'P', (byte)'S', (byte)'F', 1, 1, 0, 0]);
        head.Write(BitConverter.GetBytes((uint)keyStart));
        head.Write(BitConverter.GetBytes((uint)dataStart));
        head.Write(BitConverter.GetBytes((uint)entries.Length));
        head.Write(table.ToArray());
        head.Write(keys.ToArray());
        head.Write(data.ToArray());
        return head.ToArray();
    }
}
