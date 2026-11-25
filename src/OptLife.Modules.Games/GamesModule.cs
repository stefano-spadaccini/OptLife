using System.Globalization;
using OptLife.Core;
using OptLife.Modules.Games.Windows;

namespace OptLife.Modules.Games;

public sealed record GameProcess(int Pid, string Name, string Path);

/// <summary>Everything the module needs from the machine, so the session logic can be tested without one.</summary>
public interface IGameEnvironment
{
    GameProcess? FindGame(IReadOnlyList<string> folders, IReadOnlyList<string> names, IReadOnlyList<string> ignored);

    /// <summary>The program whose window is in front right now, if it counts as a game; null otherwise.</summary>
    GameProcess? ForegroundGame(IReadOnlyList<string> folders, IReadOnlyList<string> names, IReadOnlyList<string> ignored);
    bool IsRunning(GameProcess game);
    ISessionSource OpenSource(GameProcess game, ServerHint server);
    bool IsFunctionKeyDown(int number);
    void Beep();
    Task Delay(TimeSpan time, CancellationToken ct);
}

/// <summary>Where to learn the address of the game's server: its log, or a host the person wrote. Both may be missing.</summary>
public sealed record ServerHint(string? Log, string? Host)
{
    public static readonly ServerHint None = new(null, null);
}

/// <summary>The measurements of one instant during a session, by the names SessionRecorder understands.</summary>
public interface ISessionSource : IDisposable
{
    IReadOnlyDictionary<string, double> Read();

    /// <summary>The server being measured, as a person reads it ("Amazon eu-central-1"); null when none is known.</summary>
    string? Server => null;
}

/// <summary>
/// Notices when a game is running and records the session second by second: network, processor,
/// memory, disk, graphics card and frames. A function key marks "I just felt a lag"; when the game
/// closes, each measurement is judged by how often it misbehaved right before those marks.
/// </summary>
public sealed class GamesModule : IModule
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);
    private const int StepsPerReading = 5;   // 4 readings per second
    private const int StepsPerSecond = 20;
    private const int StepsPerFrontCheck = 200; // every 10 seconds
    private const int FrontChecksToSwitch = 3;
    private const int RecentReadings = 20; // five seconds
    private const int MinimumSeconds = 60;
    private const int MinimumHitches = 15;
    private const int LagDays = 7;
    private const int HistoryDays = 30;

    private readonly IGameEnvironment _env;
    private DateTimeOffset _evaluatedAt = DateTimeOffset.MinValue;

    public GamesModule() : this(new WindowsGameEnvironment()) { }

    internal GamesModule(IGameEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(
        "games", L.T("Giochi", "Games"),
        L.T("Registra ogni sessione di gioco (frame, rete, processore, memoria) e, dai lag che segni con un tasto, ricava cosa li precede.", "Records every game session (frames, network, processor, memory) and, from the lags you mark with a key, works out what comes before them."), FindingArea.Hardware);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.List("gameFolders", L.T("Cartelle dei giochi", "Game folders"),
            L.T("Un programma con una finestra che parte da una di queste cartelle è considerato un gioco. Basta una parte del percorso.", "A program with a window started from one of these folders counts as a game. Part of the path is enough."),
            @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\Riot Games\", @"\GOG Galaxy\Games\"),
        SettingDefinition.List("gameNames", L.T("Giochi per nome", "Games by name"),
            L.T("Nomi di programmi da considerare giochi anche se installati altrove (senza .exe).", "Names of programs to treat as games even if installed elsewhere (without .exe).")),
        SettingDefinition.List("ignored", L.T("Programmi da non considerare giochi", "Programs not to treat as games"),
            L.T("Parti di nome da escludere: launcher, strumenti e programmi che vivono nelle cartelle dei giochi.", "Parts of names to leave out: launchers, tools and programs that live in the game folders."),
            "wallpaper", "CrashReport", "CrashHandler", "EasyAntiCheat", "BEService", "EOSOverlay", "Launcher",
            "Redist", "Setup", "Installer", "UnrealCEFSubProcess", "steamwebhelper", "Overlay", "Riot Client", "RiotClient"),
        SettingDefinition.Table("servers", [L.T("Gioco", "Game"), L.T("Registro del gioco", "Game log"), L.T("Indirizzo", "Address")], L.T("Server del gioco da misurare", "Game servers to measure"),
            L.T("Scritti come: gioco | registro del gioco | indirizzo. Per misurare anche il tratto da internet al server del gioco. " +
            "Dal registro si legge il server della partita (funziona con i giochi che lo scrivono, come Rocket League); " +
            "in alternativa scrivi un indirizzo fisso (con :porta per misurare il tempo di connessione invece del ping). " +
            "Un server che non risponde al ping viene misurato attraverso il suo datacenter, quando si riconosce.", "Written as: game | game log | address. To also measure the stretch from the internet to the game server. The log gives the server of the match (works with games that write it, like Rocket League); otherwise write a fixed address (with :port to time the connection instead of pinging). A server that does not answer ping is measured through its datacenter, when it is recognised."),
            @"RocketLeague | %USERPROFILE%\Documents\My Games\Rocket League\TAGame\Logs\Launch.log |"),
        SettingDefinition.Int("markKey", L.T("Tasto funzione per segnare un lag", "Function key to mark a lag"),
            L.T("Il numero del tasto: 9 significa F9. Premilo subito dopo aver sentito un lag.", "The key number: 9 means F9. Press it right after you feel a lag."), 9, 1, 12),
        SettingDefinition.Bool("markSound", L.T("Suono di conferma", "Confirmation sound"),
            L.T("Un breve suono conferma che il lag è stato segnato.", "A short sound confirms the lag was marked."), true),
        SettingDefinition.Int("minMarks", L.T("Lag segnati prima di trarre conclusioni", "Marked lags before drawing conclusions"),
            L.T("Con meno lag segnati di così, in 7 giorni, il risultato sarebbe poco affidabile e non viene mostrato.", "With fewer marked lags than this in 7 days the result would be unreliable and is not shown."), 8, 3, 100),
        SettingDefinition.Int("keepSessions", L.T("Registrazioni dettagliate da conservare", "Detailed recordings to keep"),
            L.T("Quante sessioni tenere secondo per secondo su disco; le più vecchie vengono cancellate.", "How many sessions to keep second by second on disk; the oldest are deleted."), 30, 1, 500),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("watch", L.T("Attesa di un gioco", "Waiting for a game"), TimeSpan.FromSeconds(5), WatchAsync, Continuous: true),
    ];

    public IReadOnlyList<ModuleView> Views { get; } =
    [
        new ModuleView("sessions", L.T("Sessioni", "Sessions"), GamesRules.SessionKind,
        [
            new ViewColumn(L.T("Quando", "When"), "@time", ColumnKind.When, 104),
            new ViewColumn(L.T("Gioco", "Game"), "game", ColumnKind.Text, 190),
            new ViewColumn(L.T("Durata", "Length"), "seconds", ColumnKind.Duration, 84),
            new ViewColumn("FPS min", "fps_low", ColumnKind.Number, 62),
            new ViewColumn(L.T("Medi", "Avg"), "fps_avg", ColumnKind.Number, 48),
            new ViewColumn("Max", "fps_max", ColumnKind.Number, 44),
            new ViewColumn(L.T("Scatti", "Stutters"), "hitches", ColumnKind.Number, 54),
            new ViewColumn(L.T("Rete", "Network"), "net_lags", ColumnKind.Number, 44),
            new ViewColumn("F9", "marks", ColumnKind.Number, 34),
            new ViewColumn(L.T("Causa più probabile", "Most likely cause"), "cause", ColumnKind.Text, 0, GamesRules.InCurrentLanguage),
        ],
        EmptyText: L.T("Nessuna sessione registrata. Compaiono qui dopo ogni partita di almeno un minuto.", "No sessions recorded. They show up here after every match of at least a minute."),
        Menu: ViewMenu.Games, Order: 9,
        Description: L.T("FPS min è il minimo sostenuto: l'1% dei secondi peggiori sta sotto. " +
            "Scatti: i frame calano di colpo sotto l'80% del solito, cioè l'immagine si blocca (colpa del PC). " +
            "Rete: un pacchetto perso o un ping molto sopra il solito verso il router o internet, cioè la macchina si sposta mentre i frame restano alti; " +
            "è il lag che il gioco segnala con i suoi indicatori. Dove il gioco dice a quale server è collegato (Rocket League), si misura anche il tratto fino al suo datacenter, " +
            "e la causa dice in quale dei tre tratti nasce il lag: Wi-Fi, linea di casa o percorso verso il server. " +
            "F9: i lag che hai segnato tu.", "FPS min is the sustained minimum: the worst 1% of seconds are below it. Stutters: frames suddenly drop below 80% of the usual, i.e. the picture freezes (the PC's fault). Network: a lost packet or a ping far above the usual to the router or the internet, i.e. the car moves while frames stay high; it is the lag the game shows with its indicators. Where the game says which server it is on (Rocket League), the stretch to its datacenter is measured too, and the cause says in which of the three stretches the lag starts: Wi-Fi, home line or route to the server. F9: the lags you marked.")),
    ];

    private async Task WatchAsync(IModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings;
        var game = _env.FindGame(s.GetList("gameFolders"), s.GetList("gameNames"), s.GetList("ignored"));
        if (game is not null)
        {
            await RecordAsync(ctx, game, ct);
        }
        else if (ctx.Now - _evaluatedAt > TimeSpan.FromHours(1))
        {
            Evaluate(ctx); // Conclusions age out by themselves even when nobody plays.
        }
    }

    /// <summary>The line of the "servers" setting that names this game, if any.</summary>
    internal static ServerHint ServerFor(ISettings s, GameProcess game)
    {
        foreach (var line in s.GetList("servers"))
        {
            var p = line.Split('|', StringSplitOptions.TrimEntries);
            if (p[0].Length == 0 || !game.Name.Contains(p[0], StringComparison.OrdinalIgnoreCase)) continue;
            return new ServerHint(p.Length > 1 && p[1].Length > 0 ? p[1] : null, p.Length > 2 && p[2].Length > 0 ? p[2] : null);
        }
        return ServerHint.None;
    }

    /// <summary>Runs for as long as the game does; this is the one task that is meant to take hours.</summary>
    private async Task RecordAsync(IModuleContext ctx, GameProcess game, CancellationToken ct)
    {
        var s = ctx.Settings;
        var key = s.GetInt("markKey");
        var sound = s.GetBool("markSound");
        var recorder = new SessionRecorder(ctx.Now);
        using var source = _env.OpenSource(game, ServerFor(s, game));

        try
        {
            var keyWasDown = false;
            var minutesStored = 0;
            var otherInFront = 0;
            var recent = new Queue<IReadOnlyDictionary<string, double>>(); // The last five seconds of readings.
            for (var step = 0; ; step++)
            {
                var now = ctx.Now;

                // What is being recorded may be a launcher left open while the real game runs in front of it.
                // When another game holds the screen for half a minute, this session ends and the next round follows that one.
                if (step > 0 && step % StepsPerFrontCheck == 0)
                {
                    var front = _env.ForegroundGame(s.GetList("gameFolders"), s.GetList("gameNames"), s.GetList("ignored"));
                    otherInFront = front is not null && front.Pid != game.Pid ? otherInFront + 1 : 0;
                    if (otherInFront >= FrontChecksToSwitch) break;
                }

                var keyDown = _env.IsFunctionKeyDown(key);
                if (keyDown && !keyWasDown)
                {
                    recorder.Mark(now);
                    if (sound) _env.Beep();
                }
                keyWasDown = keyDown;

                if (step % StepsPerReading == 0)
                {
                    var reading = source.Read();
                    recorder.Add(now, reading);
                    recent.Enqueue(reading);
                    if (recent.Count > RecentReadings) recent.Dequeue();
                    if (step % StepsPerSecond == 0)
                    {
                        if (!_env.IsRunning(game)) break;
                        Publish(ctx, game, recorder, reading, recent);

                        // One point per minute in the history, so the frame rate has a chart like every other sensor.
                        if (recorder.Seconds / 60 > minutesStored)
                        {
                            minutesStored = recorder.Seconds / 60;
                            var minute = Enumerable.Range((minutesStored - 1) * 60, 60).Select(recorder.FpsAt).Where(f => f > 0).ToList();
                            if (minute.Count > 0) ctx.RecordSample("game.fps", minute.Average());
                        }
                    }
                }
                await _env.Delay(Step, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Finish(ctx, game, recorder, source.Server); // OptLife is closing mid-game: keep what was recorded.
            throw;
        }
        Finish(ctx, game, recorder, source.Server);
    }

    /// <summary>Live value holding the process id of the game being played.</summary>
    public const string GamePidKey = "game.pid";
    private static string Group => L.T("Gioco", "Game");

    private static void Publish(IModuleContext ctx, GameProcess game, SessionRecorder recorder,
        IReadOnlyDictionary<string, double> reading, IReadOnlyCollection<IReadOnlyDictionary<string, double>> recent)
    {
        var live = new List<LiveValue>
        {
            new("game.minutes", $"{game.Name} · {L.T("in gioco da", "playing for")}", Group, recorder.Seconds / 60d, "min"),
            new("game.marks", L.T("Lag segnati", "Marked lags"), Group, recorder.Marks.Count, ""),
            // Not for people (no label: panel and command line skip it): it tells other parts which process
            // is the game, so that nothing ever offers to close it.
            new(GamePidKey, "", Group, game.Pid, ""),
        };
        if (reading.TryGetValue("fps", out var fps))
            live.Insert(0, new LiveValue("game.fps", L.T("Frame al secondo", "Frames per second"), Group, fps, "fps"));
        if (reading.TryGetValue("frame_ms", out var frame))
            live.Insert(1, new LiveValue("game.frame_ms", L.T("Tempo per frame", "Frame time"), Group, frame, "ms"));

        // A lag lasts a fraction of a second: the value of this instant has usually moved on by the time
        // the eye gets there. These hold the worst of the last five seconds, so a lag stays readable.
        List<double> Last(string name) => [.. recent.Where(r => r.ContainsKey(name)).Select(r => r[name])];
        if (Last("fps") is { Count: > 0 } rates)
        {
            live.Add(new LiveValue("game.fps_low", L.T("FPS più bassi (ultimi 5 s)", "Lowest FPS (last 5 s)"), Group, rates.Min(), "fps"));

            // The same fall as a share of what this game usually runs at, so one limit ("more than 20%")
            // fits a game capped at 60 as well as one at 144.
            var usual = Enumerable.Range(Math.Max(0, recorder.Seconds - 31), Math.Min(30, recorder.Seconds))
                .Select(recorder.FpsAt).Where(f => f > 0).Order().ToList();
            if (usual.Count >= 10)
            {
                var median = usual[usual.Count / 2];
                var drop = Math.Clamp(100 * (median - rates.Min()) / median, 0, 100);
                live.Add(new LiveValue("game.fps_drop", L.T("Calo dei frame (ultimi 5 s)", "Frame drop (last 5 s)"), Group, drop, "%", Stress: drop / 20));
            }
        }
        var pings = Last("ping_gw").Concat(Last("ping_ext")).Concat(Last("ping_srv")).ToList();
        var lost = Last("ping_gw_lost").Concat(Last("ping_ext_lost")).Concat(Last("ping_srv_lost")).Sum();
        if (Last("ping_srv") is { Count: > 0 } server)
            live.Add(new LiveValue("game.ping_srv", L.T("Ping verso il server del gioco (ultimi 5 s)", "Ping to the game server (last 5 s)"), Group, server.Max(), "ms", Stress: server.Max() / 80));
        if (pings.Count > 0 || lost > 0)
        {
            var worst = pings.Count > 0 ? pings.Max() : 0;
            live.Add(new LiveValue("game.ping_max", L.T("Ping peggiore (ultimi 5 s)", "Worst ping (last 5 s)"), Group, worst, "ms", Stress: worst / 80));
            live.Add(new LiveValue("game.lost", L.T("Pacchetti persi (ultimi 5 s)", "Packets lost (last 5 s)"), Group, lost, "", Stress: lost > 0 ? 1 + lost / 4 : 0));
        }
        if (Last("pages_in") is { Count: > 0 } pages) // Pages of 4 KB each, read back from disk because the RAM is full.
        {
            var megabytes = pages.Max() * 4096 / (1024 * 1024);
            live.Add(new LiveValue("game.paging", L.T("Memoria riletta dal disco (ultimi 5 s)", "Memory read back from disk (last 5 s)"), Group, megabytes, "MB/s", Stress: megabytes / 20));
        }
        ctx.PublishLive(live);
    }

    private void Finish(IModuleContext ctx, GameProcess game, SessionRecorder recorder, string? server)
    {
        ctx.PublishLive([]);
        var summary = recorder.Summarize();
        if (summary.Seconds < MinimumSeconds) return; // A launcher flashing by, or a game that was already closing.

        var ci = CultureInfo.InvariantCulture;
        var file = Path.Combine(ctx.DataDirectory, $"{recorder.Start.ToLocalTime():yyyyMMdd_HHmmss}_{SafeName(game.Name)}.csv");
        recorder.WriteCsv(file);
        foreach (var old in Directory.GetFiles(ctx.DataDirectory, "*.csv").OrderDescending().Skip(ctx.Settings.GetInt("keepSessions")))
            File.Delete(old);

        var data = new Dictionary<string, string>
        {
            ["game"] = game.Name,
            ["path"] = game.Path,
            ["seconds"] = summary.Seconds.ToString(ci),
            ["marks"] = summary.Marks.ToString(ci),
            ["hitches"] = summary.Hitches.ToString(ci),
            ["file"] = file,
        };
        if (server is not null) data["server"] = server;
        if (summary.HasFrames) // A game with no frame data has no frame rate, rather than one of zero.
        {
            data["fps_avg"] = summary.FpsAverage.ToString("0.#", ci);
            data["fps_low"] = summary.FpsLow.ToString("0.#", ci);
            data["fps_max"] = summary.FpsMax.ToString("0.#", ci);
        }
        data["net_lags"] = summary.NetLags.ToString(ci);
        foreach (var v in summary.Verdicts)
            data[$"m.{v.Metric}"] = $"{v.Hits.ToString(ci)};{v.BaseRate.ToString("0.####", ci)}";
        foreach (var v in summary.HitchVerdicts)
            data[$"h.{v.Metric}"] = $"{v.Hits.ToString(ci)};{v.BaseRate.ToString("0.####", ci)}";
        if (GamesRules.Cause(summary) is { } cause) data["cause"] = cause;

        var it = L.Culture;
        var title = $"{game.Name}: {Duration(summary.Seconds)}";
        title += summary.HasFrames
            ? L.T($", {summary.FpsAverage.ToString("0", it)} FPS medi (minimo sostenuto {summary.FpsLow.ToString("0", it)})", $", {summary.FpsAverage.ToString("0", it)} FPS average (sustained minimum {summary.FpsLow.ToString("0", it)})")
            : L.T(", frame non disponibili", ", frames not available");
        title += summary.Hitches switch { 0 => "", 1 => L.T(", 1 scatto", ", 1 stutter"), _ => L.T($", {summary.Hitches} scatti", $", {summary.Hitches} stutters") };
        title += summary.NetLags switch { 0 => "", 1 => L.T(", 1 lag di rete", ", 1 network lag"), _ => L.T($", {summary.NetLags} lag di rete", $", {summary.NetLags} network lags") };
        title += summary.Marks switch { 0 => "", 1 => L.T(", 1 lag segnato", ", 1 marked lag"), _ => L.T($", {summary.Marks} lag segnati", $", {summary.Marks} marked lags") };

        ctx.RecordEvents([new NewEvent(ctx.Now, GamesRules.SessionKind, Severity.Info, title, $"game|{game.Name.ToLowerInvariant()}", Data: data)]);
        ctx.RecordEvents(recorder.Marks.Select(second => new NewEvent(
            recorder.Start.AddSeconds(second), "lag_mark", Severity.Info, L.T($"Lag segnato in {game.Name}", $"Lag marked in {game.Name}"), $"lagmark|{game.Name.ToLowerInvariant()}")));
        Evaluate(ctx);
    }

    private void Evaluate(IModuleContext ctx)
    {
        _evaluatedAt = ctx.Now;
        var minMarks = ctx.Settings.GetInt("minMarks");
        var findings = new List<NewFinding>();
        var sessions = ctx.History.Events(ctx.Now.AddDays(-HistoryDays), ctx.Now, ctx.ModuleId, GamesRules.SessionKind);
        foreach (var byGame in sessions.GroupBy(e => e.Data.GetValueOrDefault("game", "")).Where(g => g.Key.Length > 0))
        {
            var recent = byGame.Where(e => e.Time >= ctx.Now.AddDays(-LagDays)).ToList();
            if (GamesRules.Lag(byGame.Key, recent, minMarks, LagDays) is { } lag) findings.Add(lag);
            if (GamesRules.Stutter(byGame.Key, recent, MinimumHitches, LagDays) is { } stutter) findings.Add(stutter);
            if (GamesRules.FpsDrop(byGame.Key, [.. byGame]) is { } drop) findings.Add(drop);
        }
        ctx.SyncFindings("games", findings);
    }

    private static string Duration(int seconds) =>
        seconds < 3600 ? $"{seconds / 60} min" : $"{seconds / 3600} h {seconds % 3600 / 60} min";

    private static string SafeName(string name) =>
        new([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
}
