using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Config;
using OptLife.Core.Reporting;
using OptLife.Modules.Games;
using OptLife.Modules.Reminders;
using OptLife.Panel.ViewModels;

namespace OptLife.Tests;

public class RemindersTests
{
    private static readonly DateTime Today = new(2026, 11, 16);

    private readonly Sandbox _box = new();
    private readonly Dictionary<string, DateTimeOffset> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly RemindersModule _module;

    public RemindersTests() => _module = new RemindersModule(file => _files.TryGetValue(file, out var t) ? t : null);

    private void Set(string key, params string[] lines) =>
        _box.Config.UpdateModule("reminders", c => c.Settings[key] = JsonSerializer.SerializeToElement(lines));

    private async Task Check() =>
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None));

    [Fact]
    public void A_deadline_is_silent_until_its_notice_then_counts_down_then_says_it_is_past()
    {
        Assert.Null(RemindersModule.Deadline("Token PSN | 14/01/2027 | 10", Today));
        Assert.Equal("Token PSN: scade tra 9 giorni", RemindersModule.Deadline("Token PSN | 25/11/2026 | 10", Today)!.Title);
        Assert.Equal("Token PSN: scade domani", RemindersModule.Deadline("Token PSN | 17/11/2026 | 10", Today)!.Title);
        Assert.Equal("Token PSN: scade oggi", RemindersModule.Deadline("Token PSN | 16/11/2026", Today)!.Title);

        var past = RemindersModule.Deadline("Token PSN | 2026-11-01 | 10", Today)!;
        Assert.Equal("Token PSN: scaduto da 15 giorni", past.Title);
        Assert.Equal(Severity.Medium, past.Severity);
    }

    [Fact]
    public void A_date_that_cannot_be_read_is_said_instead_of_being_ignored()
    {
        Assert.Equal("Scadenza \"Abbonamento\": non capisco la data", RemindersModule.Deadline("Abbonamento | fine mese | 5", Today)!.Title);
        Assert.Null(RemindersModule.Deadline("", Today));
    }

    [Fact]
    public async Task A_watched_file_that_changes_becomes_a_reminder_that_stays_until_dismissed()
    {
        const string manifest = @"E:\SteamLibrary\steamapps\appmanifest_1422450.acf";
        Set("files", $"Deadlock | {manifest} | Si è aggiornato: controlla che le mod funzionino ancora.");
        _files[manifest] = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        await Check();
        Assert.Empty(_box.Store.GetFindings()); // The version of today is simply the one to compare with.

        _files[manifest] = new DateTimeOffset(2026, 11, 15, 18, 30, 0, TimeSpan.Zero);
        await Check();
        await Check();

        var reminder = Assert.Single(_box.Store.GetFindings());
        Assert.Equal("Deadlock è cambiato il 15 novembre", reminder.Title);
        Assert.StartsWith("Si è aggiornato: controlla che le mod funzionino ancora.", reminder.Advice);

        _box.Store.SetFindingStatus(reminder.Id, FindingStatus.Ignored);
        await Check();
        Assert.Empty(_box.Store.GetFindings());

        _files[manifest] = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero); // Another update: a new reminder.
        await Check();
        Assert.Equal("Deadlock è cambiato il 20 novembre", Assert.Single(_box.Store.GetFindings()).Title);
    }

    [Fact]
    public async Task A_watched_file_that_is_not_there_says_nothing()
    {
        Set("files", @"Gioco | Q:\non\esiste.acf | Controlla le mod.");

        await Check();

        Assert.Empty(_box.Store.GetFindings());
    }
}

public class ReportTests
{
    [Fact]
    public void The_report_tells_the_period_in_words_from_the_history_alone()
    {
        using var box = new Sandbox();
        var now = box.Clock.Now;
        var week = now.AddDays(-7);

        box.Store.SyncFindings("monitor", "ram", [new NewFinding("ram", Severity.High, "Memoria quasi piena per 80 minuti", "d", "Chiudi i programmi pesanti.")], now.AddDays(-2));
        box.Store.SyncFindings("setup", "s", [new NewFinding("old", Severity.Low, "Un problema vecchio", "d")], now.AddDays(-40));
        box.Store.SyncFindings("errors", "e", [new NewFinding("svc", Severity.Low, "Un servizio dava errore", "d")], now.AddDays(-3));
        box.Store.SyncFindings("errors", "e", [], now.AddDays(-1)); // Went away during the week.

        box.Store.InsertEvents("games",
        [
            new NewEvent(now.AddDays(-1), "game_session", Severity.Info, "RocketLeague: 1 h 9 min, 143 FPS medi", "game|racinggame"),
            new NewEvent(now.AddDays(-3), "game_session", Severity.Info, "RocketLeague: 40 min, 141 FPS medi", "game|racinggame"),
            new NewEvent(now.AddDays(-20), "game_session", Severity.Info, "Fuori dal periodo", "game|altro"),
        ]);
        box.Store.InsertEvents("errors", [new NewEvent(now.AddDays(-2), "app_crash", Severity.Medium, "Wallpaper Engine si è chiuso", "crash|wallpaper")]);
        box.Store.LogAction(now.AddDays(-1), "setup", "set-refresh", "pannello", new ActionResult(true, "Schermo portato da 60 a 144 Hz.", "Schermo avanzato → 60 Hz"));

        var report = Report.Build(box.Store, id => id == "games" ? "Giochi" : id == "errors" ? "Errori" : id, (_, action) => action == "set-refresh" ? "Porta lo schermo alla frequenza massima" : action, week, now);
        var text = report.ToText();

        Assert.StartsWith("RAPPORTO OPTLIFE · dal 8 novembre al 15 novembre 2026", text);
        Assert.Contains("Aperti adesso: 2 (1 alto, 1 basso).", text);
        Assert.Contains("Comparsi nel periodo: 2.", text);            // The old one is not "new this week".
        Assert.Contains("+ [ALTO] Memoria quasi piena per 80 minuti", text);
        Assert.Contains("Risolti nel periodo: 1.", text);
        Assert.Contains("✓ Un servizio dava errore", text);
        // The more serious module first; the same game twice is one line with a count; what is outside the period is not there.
        Assert.True(text.IndexOf("Errori: 1 evento.") < text.IndexOf("Giochi: 2 eventi."));
        Assert.Contains("RocketLeague: 1 h 9 min, 143 FPS medi  (×2)", text);
        Assert.DoesNotContain("Fuori dal periodo", text);
        Assert.Contains("Porta lo schermo alla frequenza massima · fatto — Schermo portato da 60 a 144 Hz.", text);
        Assert.Contains("[ALTO] Memoria quasi piena per 80 minuti → Chiudi i programmi pesanti.", text);
    }

    [Fact]
    public void A_quiet_period_says_so()
    {
        using var box = new Sandbox();

        var text = Report.Build(box.Store, id => id, (_, a) => a, box.Clock.Now.AddDays(-7), box.Clock.Now).ToText();

        Assert.Contains("Aperti adesso: nessuno.", text);
        Assert.Contains("Niente di registrato.", text);
        Assert.Contains("Nessuna: sul PC non è stato cambiato niente.", text);
        Assert.Contains("Niente di urgente.", text);
    }

    [Fact]
    public void The_panel_shows_the_report_for_seven_or_thirty_days()
    {
        using var box = new Sandbox();
        box.Store.InsertEvents("fake", [new NewEvent(box.Clock.Now.AddDays(-12), "k", Severity.Low, "Successo due settimane fa")]);
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [new FakeModule()], () => false, () => false, box.Clock);

        vm.ShowCommand.Execute("Report");
        Assert.Contains(vm.ReportLines, l => l is { IsHeader: true, Text: "COSA È SUCCESSO" });
        Assert.DoesNotContain(vm.ReportLines, l => l.Text.Contains("Successo due settimane fa"));

        vm.SetReportDaysCommand.Execute("30");
        Assert.Contains(vm.ReportLines, l => l.Text.Contains("Successo due settimane fa"));
        Assert.Contains(vm.ReportLines, l => l.Text == "Finto: 1 evento."); // The module's name, not its id.
    }
}

public class ProfileTests
{
    private static readonly FakeModule Module = new()
    {
        Settings =
        [
            SettingDefinition.Int("days", "Giorni", "", 7, 1, 30),
            SettingDefinition.Bool("on", "Acceso", "", true),
            SettingDefinition.Choice("corner", "Angolo", "", "alto", "basso"),
            SettingDefinition.List("folders", "Cartelle", ""),
            SettingDefinition.Text("path", "Percorso", "", ""),
        ],
        Actions = [new ModuleAction("fix", "Sistema", "", Permission.Ask, (_, _, _) => Task.FromResult(new ActionResult(true, "ok")))],
    };

    [Fact]
    public void A_profile_carries_thresholds_switches_and_permissions_but_nothing_personal()
    {
        using var mine = new Sandbox();
        mine.Config.UpdateModule("fake", c =>
        {
            c.Enabled = false;
            c.Settings["days"] = JsonSerializer.SerializeToElement(21);
            c.Settings["corner"] = JsonSerializer.SerializeToElement("basso");
            c.Settings["folders"] = JsonSerializer.SerializeToElement(new[] { @"C:\Users\alex\Documents\Segreto" });
            c.Settings["path"] = JsonSerializer.SerializeToElement(@"E:\Backups");
            c.Permissions["fix"] = Permission.Auto;
        });
        mine.Config.Update(f => f.Ui["mode"] = "expert");

        var profile = Profile.Export(mine.Config, [Module]);

        Assert.DoesNotContain("alex", profile);
        Assert.DoesNotContain("Backups", profile);
        Assert.DoesNotContain("expert", profile);

        using var theirs = new Sandbox();
        theirs.Config.UpdateModule("fake", c => c.Settings["folders"] = JsonSerializer.SerializeToElement(new[] { @"D:\Le mie cose" }));

        var applied = Profile.Import(theirs.Config, [Module], profile);

        var settings = theirs.Runner.SettingsOf(Module);
        Assert.Equal(4, applied); // enabled, days, corner, the permission
        Assert.Equal(21, settings.GetInt("days"));
        Assert.Equal("basso", settings.GetChoice("corner"));
        Assert.False(theirs.Runner.IsEnabled(Module));
        Assert.Equal(Permission.Auto, theirs.Config.GetModule("fake").Permissions["fix"]);
        Assert.Equal([@"D:\Le mie cose"], settings.GetList("folders")); // Their own list is still theirs.
    }

    [Fact]
    public void A_profile_cannot_smuggle_lists_in_and_a_file_that_is_not_one_is_refused()
    {
        using var box = new Sandbox();
        const string crafted = """{ "modules": { "fake": { "enabled": true, "settings": { "folders": ["C:\\Windows"], "days": 3 } } } }""";

        Profile.Import(box.Config, [Module], crafted);

        Assert.Empty(box.Runner.SettingsOf(Module).GetList("folders"));
        Assert.Equal(3, box.Runner.SettingsOf(Module).GetInt("days"));
        Assert.Throws<FormatException>(() => Profile.Import(box.Config, [Module], "questo non è un profilo"));
    }
}

public class ModuleViewTests
{
    private static readonly FakeModule WithView = new()
    {
        Info = new ModuleInfo("games", "Giochi", ""),
        Views =
        [
            new ModuleView("sessions", "Sessioni", "game_session",
            [
                new ViewColumn("Quando", "@time", ColumnKind.When, 104),
                new ViewColumn("Gioco", "game"),
                new ViewColumn("Durata", "seconds", ColumnKind.Duration),
                new ViewColumn("FPS medi", "fps_avg", ColumnKind.Number),
                new ViewColumn("Causa", "cause", ColumnKind.Text, 0),
            ], EmptyText: "Nessuna sessione registrata."),
        ],
    };

    [Fact]
    public void A_module_gets_a_page_of_its_own_that_the_panel_fills_without_knowing_it()
    {
        using var box = new Sandbox();
        var now = box.Clock.Now;
        box.Store.InsertEvents("games",
        [
            new NewEvent(now.AddHours(-2), "game_session", Severity.Info, "t", Data: new Dictionary<string, string>
                { ["game"] = "RocketLeague", ["seconds"] = "4143", ["fps_avg"] = "142.8", ["cause"] = "durante gli scatti: Windows che rilegge memoria dal disco" }),
            new NewEvent(now.AddDays(-1), "game_session", Severity.Info, "t", Data: new Dictionary<string, string> { ["game"] = "Deadlock", ["seconds"] = "1500" }),
            new NewEvent(now.AddHours(-1), "lag_mark", Severity.Info, "not a session"),
        ]);
        box.Store.InsertEvents("altro", [new NewEvent(now, "game_session", Severity.Info, "another module's event of the same kind")]);
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [WithView, new FakeModule()], () => false, () => false, box.Clock);

        var tab = Assert.Single(vm.Views);
        Assert.Equal("SESSIONI", tab.Title);
        Assert.Equal(["QUANDO", "GIOCO", "DURATA", "FPS MEDI", "CAUSA"], tab.Header.Select(h => h.Text));

        vm.ShowViewCommand.Execute(tab);

        Assert.Equal(PanelPage.View, vm.Page);
        Assert.True(tab.IsSelected);
        Assert.Equal(2, tab.Rows.Count);
        static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("dd/MM HH:mm"); // Shown in this PC's own time.
        Assert.Equal([Local(now.AddHours(-2)), "RocketLeague", "1 h 9 min", "143", "durante gli scatti: Windows che rilegge memoria dal disco"], tab.Rows[0].Select(c => c.Text));
        // What a session did not record is a dash for numbers and nothing for words.
        Assert.Equal([Local(now.AddDays(-1)), "Deadlock", "25 min", "—", ""], tab.Rows[1].Select(c => c.Text));
        Assert.True(tab.Rows[0][2].Right);
        Assert.True(double.IsNaN(tab.Rows[0][4].Width)); // The last column takes what it needs.

        vm.ShowCommand.Execute("Problems");
        Assert.False(tab.IsSelected);
    }

    [Fact]
    public void An_empty_page_says_what_will_appear_there()
    {
        using var box = new Sandbox();
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [WithView], () => false, () => false, box.Clock);

        vm.ShowViewCommand.Execute(vm.Views[0]);

        Assert.Empty(vm.Views[0].Rows);
        Assert.Equal("Nessuna sessione registrata.", vm.Views[0].EmptyText);
    }

    [Fact]
    public void The_games_module_declares_its_sessions_page_and_names_a_cause_only_when_there_is_one()
    {
        var view = Assert.Single(new GamesModule().Views);
        Assert.Equal(GamesRules.SessionKind, view.EventKind);
        Assert.Contains(view.Columns, c => c.Key == "cause");

        static SessionSummary Summary(params MetricVerdict[] hitches) => new(3600, true, 144, 120, 0, [], hitches.Length == 0 ? 0 : hitches[0].Marks, hitches);

        Assert.Equal("durante gli scatti: Windows che rilegge memoria dal disco",
            GamesRules.Cause(Summary(new MetricVerdict("pages_in", 0, 0.05, 11, 16), new MetricVerdict("cpu_tot", 0, 0.05, 5, 16))));
        Assert.Null(GamesRules.Cause(Summary(new MetricVerdict("pages_in", 0, 0.05, 2, 16))));   // Rarely there: not a cause.
        Assert.Null(GamesRules.Cause(Summary(new MetricVerdict("pages_in", 0, 0.05, 2, 2))));    // Two stutters prove nothing.
        Assert.Null(GamesRules.Cause(Summary()));

        var marked = new SessionSummary(3600, true, 144, 120, 9, [new MetricVerdict("ping_gw_max", 0, 0.05, 8, 9)], 16, [new MetricVerdict("pages_in", 0, 0.05, 11, 16)]);
        Assert.Equal("prima dei lag: il ping verso il router che sale", GamesRules.Cause(marked)); // What the person felt comes first.
    }
}

public class NetworkLagAndStressTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_connection_that_misbehaves_while_frames_stay_perfect_is_counted_as_network_lag()
    {
        var recorder = new SessionRecorder(Start);
        int[] spikes = [100, 101, 102, 300, 500];   // 100-102 is one lag, not three.
        for (var s = 0; s < 600; s++)
            recorder.Add(Start.AddSeconds(s), new Dictionary<string, double>
            {
                ["fps"] = 144,
                ["ping_gw"] = spikes.Contains(s) ? 220 : 3,
                ["ping_ext"] = spikes.Contains(s) ? 240 : 9,
            });
        recorder.Add(Start.AddSeconds(400.5), new Dictionary<string, double> { ["ping_ext_lost"] = 1 }); // Lost beyond the router.

        var summary = recorder.Summarize();

        Assert.Equal(0, summary.Hitches);  // Frames never moved: no stutter.
        Assert.Equal(4, summary.NetLags);
        Assert.Equal("router", summary.NetWhere);
        Assert.Equal(144, summary.FpsMax);
        Assert.Equal("rete: il tratto fino al router (Wi-Fi)", GamesRules.Cause(summary));
    }

    [Fact]
    public void A_steady_connection_has_no_network_lag()
    {
        var recorder = new SessionRecorder(Start);
        for (var s = 0; s < 600; s++)
            recorder.Add(Start.AddSeconds(s), new Dictionary<string, double> { ["fps"] = 144, ["ping_gw"] = 2 + s % 4, ["ping_ext"] = 8 + s % 6 });

        Assert.Equal(0, recorder.Summarize().NetLags);
    }

    [Fact]
    public void Each_monitored_value_knows_how_close_it_is_to_its_limit()
    {
        var settings = new OptLife.Core.Config.ModuleSettings(new OptLife.Modules.Monitor.MonitorModule().Settings, new Dictionary<string, JsonElement>());
        double? Stress(string key, double value) => OptLife.Modules.Monitor.MonitorModule.Stress(new LiveValue(key, "", "", value, ""), settings);

        Assert.Equal(1, Stress("ram.used_pct", 90));          // the RAM threshold is 90
        Assert.Equal(0.5, Stress("gpu.temp", 42.5));          // the GPU limit is 85
        Assert.Equal(2, Stress("disk.C.free_pct", 5));        // 5% free against a minimum of 10
        Assert.Null(Stress("gpu.clock", 1800));               // a clock speed has no limit
    }

    [Fact]
    public void The_overview_features_the_most_strained_values_or_the_pinned_ones()
    {
        using var box = new Sandbox();
        var live = new List<OptLife.Core.Hosting.LiveEntry>
        {
            new("monitor", "ram.used_pct", "RAM occupata", "Memoria", 95, "%", 1.06),
            new("monitor", "gpu.temp", "Temperatura", "Scheda video", 70, "°C", 0.82),
            new("monitor", "cpu.temp", "Temperatura", "Processore", 50, "°C", 0.55),
            new("monitor", "gpu.clock", "Frequenza", "Scheda video", 1800, "MHz", null),
        };
        var vm = new PanelViewModel(box.Store, box.Config, box.Runner, [new FakeModule()], () => true, () => true, box.Clock, () => live);

        Assert.Equal(["ram.used_pct", "gpu.temp", "cpu.temp"], vm.Featured.Select(t => t.Key));
        Assert.Equal(["high", "medium", "ok"], vm.Featured.Select(t => t.Level));
        Assert.Equal("IN EVIDENZA · I PIÙ SOTTO SFORZO", vm.FeaturedTitle);
        Assert.DoesNotContain(vm.Featured, t => t.Key == "gpu.clock"); // No limit: never "strained".

        vm.TogglePinCommand.Execute(vm.MonitorGroups.SelectMany(g => g.Tiles).Single(t => t.Key == "gpu.clock"));

        Assert.Equal(["gpu.clock"], vm.Featured.Select(t => t.Key));
        Assert.Equal("IN EVIDENZA · SCELTI DA TE", vm.FeaturedTitle);
        Assert.Equal("gpu.clock", box.Config.Load().Ui["pinned"]); // Remembered for next time.
    }
}

public class GameServerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_server_in_a_cloud_datacenter_is_measured_through_that_datacenter()
    {
        var aws = OptLife.Modules.Games.Windows.ServerProbe.ToTarget("18.157.3.199", _ => "ec2-18-157-3-199.eu-central-1.compute.amazonaws.com");
        Assert.Equal(new OptLife.Modules.Games.Windows.ServerTarget("ec2.eu-central-1.amazonaws.com", 443, "Amazon eu-central-1"), aws);

        var plain = OptLife.Modules.Games.Windows.ServerProbe.ToTarget("203.0.113.9", _ => null);
        Assert.Equal(new OptLife.Modules.Games.Windows.ServerTarget("203.0.113.9", 0, "203.0.113.9"), plain); // An ICMP ping to the address itself.
    }

    [Fact]
    public void Lag_that_starts_beyond_the_home_line_is_put_on_the_way_to_the_server()
    {
        var recorder = new SessionRecorder(Start);
        int[] spikes = [100, 250, 400];
        for (var s = 0; s < 600; s++)
            recorder.Add(Start.AddSeconds(s), new Dictionary<string, double>
            {
                ["fps"] = 144, ["ping_gw"] = 2, ["ping_ext"] = 8,               // home and line are steady
                ["ping_srv"] = spikes.Contains(s) ? 260 : 20,                     // the route to the server is not
            });

        var summary = recorder.Summarize();

        Assert.Equal(3, summary.NetLags);
        Assert.Equal("server", summary.NetWhere);
        Assert.Equal("rete: il percorso verso il server del gioco", GamesRules.Cause(summary));
    }

    [Fact]
    public void The_settings_say_where_to_read_each_games_server()
    {
        var settings = new OptLife.Core.Config.ModuleSettings(new GamesModule().Settings, new Dictionary<string, JsonElement>());
        var rocket = GamesModule.ServerFor(settings, new GameProcess(1, "RocketLeague", @"E:\racinggame\RocketLeague.exe"));

        Assert.EndsWith(@"Rocket League\TAGame\Logs\Launch.log", rocket.Log);
        Assert.Null(rocket.Host);
        Assert.Equal(ServerHint.None, GamesModule.ServerFor(settings, new GameProcess(2, "Overwatch", "")));

        var custom = new OptLife.Core.Config.ModuleSettings(new GamesModule().Settings, new Dictionary<string, JsonElement>
        {
            ["servers"] = JsonSerializer.SerializeToElement(new[] { "Overwatch | | 137.221.0.1:443" }),
        });
        Assert.Equal(new ServerHint(null, "137.221.0.1:443"), GamesModule.ServerFor(custom, new GameProcess(2, "Overwatch", "")));
    }

    [Fact]
    public void A_joined_server_is_read_from_the_end_of_the_games_log()
    {
        var log = Path.Combine(Path.GetTempPath(), $"optlife-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(log,
            [
                "[10.00] DevNet: Browse: 127.0.0.1:7777/MENU_Main_p",
                "[3598.53] DevNet: Browse: 203.0.113.9:9083/MENU_Main_p?Name=Player02",
                "[3600.43] JoinGame: GotoJoinGameState JoiningNewServer -> None",
            ]);
            using var probe = new OptLife.Modules.Games.Windows.ServerProbe(log, null);

            probe.Poll();

            Assert.Equal("203.0.113.9", probe.Target!.Shown);
        }
        finally
        {
            File.Delete(log);
        }
    }
}

public class GameServerEndToEndTests
{
    /// <summary>Reads Rocket League's real log on this PC and measures the server it names. Runs only when asked: OPTLIFE_E2E=1.</summary>
    [Fact]
    public async Task The_server_of_the_last_rocket_league_match_is_found_and_measured()
    {
        if (Environment.GetEnvironmentVariable("OPTLIFE_E2E") != "1") return;
        var log = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Documents\My Games\Rocket League\TAGame\Logs\Launch.log");
        if (!File.Exists(log)) return;
        using var probe = new OptLife.Modules.Games.Windows.ServerProbe(log, null);

        var answers = new List<double?>();
        for (var i = 0; i < 12 && answers.Count < 5; i++)
        {
            var (answered, ms) = probe.Poll();
            if (answered) answers.Add(ms);
            await Task.Delay(300);
        }

        Assert.NotNull(probe.Target);
        Assert.NotEmpty(answers);
        Assert.Contains(answers, a => a is > 1 and < 300);
        if (Environment.GetEnvironmentVariable("OPTLIFE_SESSION_OUT") is { Length: > 0 } file)
            File.WriteAllText(file, $"{probe.Target!.Shown} -> {probe.Target.Host}:{probe.Target.Port}\n" + string.Join(", ", answers.Select(a => a is null ? "perso" : $"{a:0} ms")));
    }
}
