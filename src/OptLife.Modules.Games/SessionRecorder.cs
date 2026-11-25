using System.Globalization;
using System.Text;

namespace OptLife.Modules.Games;

/// <summary>How one measurement behaved around the marked lags compared with the rest of the session.</summary>
/// <param name="BaseRate">Share of ordinary moments in which the metric was unusually high (about 5% by construction).</param>
/// <param name="Hits">Marked lags preceded by the metric being unusually high.</param>
public sealed record MetricVerdict(string Metric, double Threshold, double BaseRate, int Hits, int Marks)
{
    public double HitRate => Marks == 0 ? 0 : (double)Hits / Marks;

    /// <summary>How many times more often the metric is high before a lag than at random. About 1 = unrelated.</summary>
    public double Lift => HitRate / Math.Max(BaseRate, 0.02);
}

/// <param name="Hitches">Moments the frame rate suddenly fell, found by the recorder itself (no key needed).</param>
/// <param name="HitchVerdicts">The same judgement as Verdicts, around the hitches instead of the marked lags.</param>
public sealed record SessionSummary(
    int Seconds, bool HasFrames, double FpsAverage, double FpsLow, int Marks, IReadOnlyList<MetricVerdict> Verdicts,
    int Hitches, IReadOnlyList<MetricVerdict> HitchVerdicts,
    double FpsMax = 0, int NetLags = 0, string? NetWhere = null);

/// <summary>
/// One game session, second by second. For every measurement it keeps the worst value of each second;
/// the user marks the moments a lag was felt, and at the end each measurement is judged by how often
/// it was unusually bad right before a mark compared with any other moment.
/// All series are oriented so that higher means worse.
/// </summary>
public sealed class SessionRecorder(DateTimeOffset start)
{
    /// <summary>Seconds before a mark that are examined: the lag is felt, then the key is pressed.</summary>
    public const int Before = 4;

    public static readonly string[] Series =
    [
        "ping_gw_max", "ping_gw_loss", "ping_ext_max", "ping_ext_loss", "ping_srv_max", "ping_srv_loss",
        "cpu_core_max", "cpu_tot", "dpc", "pages_in", "ram_low", "disk_ms", "game_faults",
        "gpu_idle", "gpu_clock_drop", "gpu_throttled", "frame_ms",
    ];

    private readonly Dictionary<string, List<double>> _worst = Series.ToDictionary(s => s, _ => new List<double>());
    private readonly List<double> _fpsSum = [];
    private readonly List<int> _fpsCount = [];
    private readonly List<int> _marks = [];

    public DateTimeOffset Start => start;
    public int Seconds { get; private set; }
    public IReadOnlyList<int> Marks => _marks;

    /// <param name="reading">Raw values of this instant, as named by the session source.</param>
    public void Add(DateTimeOffset at, IReadOnlyDictionary<string, double> reading)
    {
        var second = SecondOf(at);
        if (second < 0) return;
        Seconds = Math.Max(Seconds, second + 1);

        foreach (var (name, value) in reading)
        {
            if (!double.IsFinite(value)) continue;
            switch (name)
            {
                case "ping_gw": Put("ping_gw_max", second, value); break;
                case "ping_gw_lost": Put("ping_gw_loss", second, value); break;
                case "ping_ext": Put("ping_ext_max", second, value); break;
                case "ping_ext_lost": Put("ping_ext_loss", second, value); break;
                case "ping_srv": Put("ping_srv_max", second, value); break;
                case "ping_srv_lost": Put("ping_srv_loss", second, value); break;
                case "avail_mb": Put("ram_low", second, 100_000 - value); break;
                case "gpu_util": Put("gpu_idle", second, 100 - value); break;
                case "gpu_clock": Put("gpu_clock_drop", second, 3000 - value); break;
                case "fps":
                    Grow(_fpsSum, second);
                    Grow(_fpsCount, second);
                    _fpsSum[second] += value;
                    _fpsCount[second]++;
                    break;
                default:
                    if (_worst.ContainsKey(name)) Put(name, second, value);
                    break;
            }
        }
    }

    public void Mark(DateTimeOffset at)
    {
        var second = SecondOf(at);
        if (second >= 0) _marks.Add(second);
    }

    /// <summary>Average frames per second during the given second; 0 when no frame data arrived.</summary>
    public double FpsAt(int second) =>
        second < _fpsCount.Count && _fpsCount[second] > 0 ? _fpsSum[second] / _fpsCount[second] : 0;

    /// <summary>
    /// Seconds in which the frame rate suddenly fell below 80% of what it had been in the half minute
    /// before: what the eye sees as a stutter. A fall that lasts counts once, at its first second, and
    /// a game that simply settles at a lower rate (a menu, a cutscene) stops counting after a while.
    /// A fall that lands on a flat rate is not counted at all: that is the game switching to a frame
    /// limit of its own (Overwatch drops to 60 in menus and on the kill cam), while a stutter is ragged
    /// and climbs back.
    /// </summary>
    public IReadOnlyList<int> Hitches()
    {
        const int look = 30;
        var hitches = new List<int>();
        var previousWasLow = false;
        for (var s = look; s < Seconds; s++)
        {
            var now = FpsAt(s);
            var before = Enumerable.Range(s - look, look).Select(FpsAt).Where(f => f > 0).Order().ToList();
            var usual = before.Count >= look / 2 ? before[before.Count / 2] : 0;
            var low = now > 0 && usual > 0 && now < usual * 0.8;
            if (low && !previousWasLow && !SettlesFlat(s, usual * 0.8)) hitches.Add(s);
            previousWasLow = low;
        }
        return hitches;
    }

    /// <summary>
    /// Within the few seconds from <paramref name="start"/>, five seconds in a row stay below
    /// <paramref name="ceiling"/> and within 6% of each other: the game is holding a lower limit.
    /// The first seconds may be half old rate and half new, so they are allowed to differ. Five, not
    /// fewer: a stutter of two or three seconds must still count even when it happens to be even.
    /// </summary>
    private bool SettlesFlat(int start, double ceiling)
    {
        const int run = 5, window = 8;
        for (var from = start; from + run <= Math.Min(start + window, Seconds); from++)
        {
            var values = Enumerable.Range(from, run).Select(FpsAt).ToList();
            if (values.Any(v => v <= 0 || v >= ceiling)) continue;
            if (values.Max() <= values.Min() * 1.06) return true;
        }
        return false;
    }

    /// <summary>
    /// Moments the connection itself misbehaved: a packet lost, or a ping far above this session's
    /// usual one. This is the lag a game shows with its own network warning while the frame rate stays
    /// perfect, so it is counted apart from the hitches. Consecutive bad seconds count once.
    /// Measured to the router and to the internet, not to the game's server.
    /// </summary>
    /// <returns>
    /// How many, and which stretch they mostly started in: "router" (PC to router), "internet" (the home
    /// line) or "server" (from the internet to the game's server); null when there were none.
    /// </returns>
    public (int Count, string? Where) NetLags()
    {
        static double Median(List<double> series)
        {
            var seen = series.Where(v => v > 0).Order().ToList();
            return seen.Count == 0 ? 0 : seen[seen.Count / 2];
        }
        List<double> gw = _worst["ping_gw_max"], ext = _worst["ping_ext_max"], srv = _worst["ping_srv_max"],
            gwLost = _worst["ping_gw_loss"], extLost = _worst["ping_ext_loss"], srvLost = _worst["ping_srv_loss"];
        // Far above the usual: several times the session's own median, and never less than a ping anyone would feel.
        double gwLimit = Math.Max(60, Median(gw) * 4), extLimit = Math.Max(100, Median(ext) * 3), srvLimit = Math.Max(100, Median(srv) * 3);
        static double At(List<double> series, int second) => second < series.Count ? series[second] : 0;

        var where = new Dictionary<string, int> { ["router"] = 0, ["internet"] = 0, ["server"] = 0 };
        var count = 0;
        var previousWasBad = false;
        for (var s = 0; s < Seconds; s++)
        {
            var atRouter = At(gwLost, s) > 0 || At(gw, s) > gwLimit;
            var atLine = At(extLost, s) > 0 || At(ext, s) > extLimit;
            var atServer = At(srvLost, s) > 0 || At(srv, s) > srvLimit;
            var bad = atRouter || atLine || atServer;
            if (bad && !previousWasBad)
            {
                count++;
                // Trouble shows on every stretch beyond the one where it starts: the nearest one is to blame.
                where[atRouter ? "router" : atLine ? "internet" : "server"]++;
            }
            previousWasBad = bad;
        }
        return (count, count == 0 ? null : where.MaxBy(w => w.Value).Key);
    }

    public SessionSummary Summarize()
    {
        var fps = Enumerable.Range(0, Seconds).Select(FpsAt).Where(f => f > 0).Order().ToList();
        // A lag is felt, then the key is pressed: the seconds before a mark are examined.
        var verdicts = _marks.Count == 0 ? [] : Series.Select(m => Judge(m, _marks, -Before)).OrderByDescending(v => v.Lift).ToList();
        // A hitch is seen as it happens: the seconds around it are examined. Left out are the measurements
        // that are the hitch itself seen from elsewhere: the frame time, and the graphics card idling and
        // slowing its clock because no frames are reaching it. They would always come first and explain nothing.
        var hitches = Hitches();
        var hitchVerdicts = hitches.Count == 0
            ? []
            : Series.Where(m => m is not ("frame_ms" or "gpu_idle" or "gpu_clock_drop"))
                .Select(m => Judge(m, hitches, -Before / 2)).OrderByDescending(v => v.Lift).ToList();
        return new SessionSummary(
            Seconds,
            HasFrames: fps.Count > 0,
            FpsAverage: fps.Count > 0 ? fps.Average() : 0,
            FpsLow: fps.Count > 0 ? fps[(int)(fps.Count * 0.01)] : 0,
            _marks.Count,
            verdicts,
            hitches.Count,
            hitchVerdicts,
            FpsMax: fps.Count > 0 ? fps[^1] : 0,
            NetLags: NetLags().Count,
            NetWhere: NetLags().Where);
    }

    /// <param name="offset">Where the examined window starts, relative to each moment (it is Before seconds long).</param>
    private MetricVerdict Judge(string metric, IReadOnlyList<int> moments, int offset)
    {
        var series = _worst[metric];
        double Window(int from)
        {
            var worst = 0d;
            for (var i = Math.Max(from, 0); i < Math.Min(Seconds, from + Before); i++)
                if (i < series.Count && series[i] > worst) worst = series[i];
            return worst;
        }

        // "Unusually high" = above what the metric reaches in 95% of the session's windows.
        var windows = Enumerable.Range(0, Math.Max(0, Seconds - Before)).Select(Window).ToList();
        if (windows.Count == 0) return new MetricVerdict(metric, 0, 0, 0, moments.Count);
        var threshold = windows.Order().ElementAt(Math.Min(windows.Count - 1, (int)(windows.Count * 0.95)));
        var baseRate = (double)windows.Count(w => w > threshold) / windows.Count;
        var hits = moments.Count(moment => moment + offset >= 0 && Window(moment + offset) > threshold);
        return new MetricVerdict(metric, threshold, baseRate, hits, moments.Count);
    }

    /// <summary>Rebuilds a session from the table WriteCsv produced, to judge again a session recorded earlier.</summary>
    public static SessionRecorder FromCsv(string path, DateTimeOffset start)
    {
        var ci = CultureInfo.InvariantCulture;
        var recorder = new SessionRecorder(start);
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return recorder;
        var header = lines[0].Split(',');
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length != header.Length || !int.TryParse(cells[0], out var second)) continue;
            recorder.Seconds = Math.Max(recorder.Seconds, second + 1);
            for (var i = 1; i < header.Length; i++)
            {
                if (!double.TryParse(cells[i], NumberStyles.Float, ci, out var value) || value == 0) continue;
                if (header[i] == "mark") recorder._marks.Add(second);
                else if (header[i] == "fps")
                {
                    Grow(recorder._fpsSum, second);
                    Grow(recorder._fpsCount, second);
                    recorder._fpsSum[second] = value;
                    recorder._fpsCount[second] = 1;
                }
                else if (recorder._worst.ContainsKey(header[i])) recorder.Put(header[i], second, value);
            }
        }
        return recorder;
    }

    /// <summary>The whole session as a table, one row per second, for deeper analysis later.</summary>
    public void WriteCsv(string path)
    {
        var ci = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append("second,mark,fps,").AppendLine(string.Join(',', Series));
        for (var s = 0; s < Seconds; s++)
        {
            text.Append(s).Append(',').Append(_marks.Contains(s) ? 1 : 0).Append(',').Append(FpsAt(s).ToString("0.#", ci));
            foreach (var name in Series)
                text.Append(',').Append((s < _worst[name].Count ? _worst[name][s] : 0).ToString("0.#", ci));
            text.AppendLine();
        }
        File.WriteAllText(path, text.ToString());
    }

    private int SecondOf(DateTimeOffset at) => (int)Math.Floor((at - start).TotalSeconds);

    private void Put(string series, int second, double value)
    {
        var list = _worst[series];
        Grow(list, second);
        if (value > list[second]) list[second] = value;
    }

    private static void Grow<T>(List<T> list, int index) where T : struct
    {
        while (list.Count <= index) list.Add(default);
    }
}
