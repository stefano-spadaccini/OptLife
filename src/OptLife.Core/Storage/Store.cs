using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OptLife.Core.Storage;

public sealed record ActionLogEntry(long Id, DateTimeOffset Time, string Module, string Action, string Actor, bool Ok, string Detail, string? Undo);

/// <summary>
/// The single shared history. The schema is generic on purpose: modules never add tables,
/// so installing or removing a module needs no migration.
/// </summary>
public sealed class Store
{
    private string _connectionString;

    /// <summary>
    /// Opened for reading only: the history sits in a folder only administrators may change (an installed copy) and this process
    /// is not one. Reading works; writing fails, and the command line says it needs an administrator terminal.
    /// </summary>
    public bool ReadOnly { get; private set; }

    public Store(string dbPath, bool pooling = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = pooling,
            DefaultTimeout = 15,
        };
        _connectionString = builder.ToString();
        try
        {
            Migrate();
        }
        catch (Exception ex) when (ex is SqliteException { SqliteErrorCode: 8 or 14 } or UnauthorizedAccessException && File.Exists(dbPath))
        {
            // 8 read-only, 14 cannot open for writing: the agent (an administrator) keeps the tables up to date.
            builder.Mode = SqliteOpenMode.ReadOnly;
            _connectionString = builder.ToString();
            ReadOnly = true;
        }
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS samples(
                ts INTEGER NOT NULL, module TEXT NOT NULL, metric TEXT NOT NULL, value REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_samples ON samples(metric, ts);
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, module TEXT NOT NULL, kind TEXT NOT NULL,
                severity INTEGER NOT NULL, title TEXT NOT NULL, group_key TEXT, uid TEXT,
                data TEXT NOT NULL DEFAULT '{}', UNIQUE(module, uid));
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);
            CREATE TABLE IF NOT EXISTS findings(
                id INTEGER PRIMARY KEY, module TEXT NOT NULL, scope TEXT NOT NULL, key TEXT NOT NULL,
                severity INTEGER NOT NULL, title TEXT NOT NULL, detail TEXT NOT NULL, advice TEXT,
                status TEXT NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                UNIQUE(module, scope, key));
            CREATE TABLE IF NOT EXISTS actions(
                id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, module TEXT NOT NULL, action TEXT NOT NULL,
                actor TEXT NOT NULL, ok INTEGER NOT NULL, detail TEXT NOT NULL, undo TEXT);
            CREATE TABLE IF NOT EXISTS activities(
                id INTEGER PRIMARY KEY, module TEXT NOT NULL, title TEXT NOT NULL, actor TEXT NOT NULL, started INTEGER NOT NULL,
                updated INTEGER NOT NULL, ended INTEGER, state TEXT NOT NULL, progress TEXT, done INTEGER, total INTEGER, detail TEXT, pid INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS module_state(
                module TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(module, key));
            """);

        // Columns added to the schema after the first release: histories created before get them here.
        AddColumn(c, "findings", "fix");
        AddColumn(c, "findings", "area");
        AddColumn(c, "findings", "latest_at", "INTEGER");
        AddColumn(c, "findings", "solved_at", "INTEGER");
    }

    private static void AddColumn(SqliteConnection c, string table, string column, string type = "TEXT")
    {
        using var has = Cmd(c, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'");
        if ((long)has.ExecuteScalar()! != 0) return;
        try
        {
            Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
        catch (SqliteException)
        {
            // Agent, panel and CLI can open the history at the same moment: another one just added it.
        }
    }

    // ── samples ──────────────────────────────────────────────────────────────

    public void InsertSample(string module, string metric, double value, DateTimeOffset at) =>
        InsertSamples(module, [new(metric, value)], at);

    public void InsertSamples(string module, IEnumerable<KeyValuePair<string, double>> samples, DateTimeOffset at)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = Cmd(c, "INSERT INTO samples(ts, module, metric, value) VALUES($ts, $m, $k, $v)");
        foreach (var (metric, value) in samples)
        {
            if (!double.IsFinite(value)) continue;
            cmd.Parameters.Clear();
            Bind(cmd, ("$ts", Ms(at)), ("$m", module), ("$k", metric), ("$v", value));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Drops what is too old to matter, so the history file stays small. Returns rows removed.</summary>
    public int Prune(DateTimeOffset samplesBefore, DateTimeOffset eventsBefore)
    {
        using var c = Open();
        return Exec(c, "DELETE FROM samples WHERE ts<$t", ("$t", Ms(samplesBefore)))
             + Exec(c, "DELETE FROM events WHERE ts<$t", ("$t", Ms(eventsBefore)))
             + Exec(c, "DELETE FROM findings WHERE status='Resolved' AND last_seen<$t", ("$t", Ms(eventsBefore)));
    }

    public IReadOnlyList<SamplePoint> QuerySamples(string metric, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT ts, value FROM samples WHERE metric=$k AND ts>=$a AND ts<=$b ORDER BY ts",
            ("$k", metric), ("$a", Ms(from)), ("$b", Ms(to)));
        using var r = cmd.ExecuteReader();
        var list = new List<SamplePoint>();
        while (r.Read()) list.Add(new SamplePoint(FromMs(r.GetInt64(0)), r.GetDouble(1)));
        return list;
    }

    // ── events ───────────────────────────────────────────────────────────────

    public int InsertEvents(string module, IEnumerable<NewEvent> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = Cmd(c, """
            INSERT OR IGNORE INTO events(ts, module, kind, severity, title, group_key, uid, data)
            VALUES($ts, $m, $kind, $sev, $title, $g, $uid, $data)
            """);
        cmd.Transaction = tx;
        var inserted = 0;
        foreach (var e in events)
        {
            cmd.Parameters.Clear();
            Bind(cmd,
                ("$ts", Ms(e.Time)), ("$m", module), ("$kind", e.Kind), ("$sev", (int)e.Severity),
                ("$title", e.Title), ("$g", e.GroupKey), ("$uid", e.Uid),
                ("$data", JsonSerializer.Serialize(e.Data ?? new Dictionary<string, string>())));
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    public IReadOnlyList<StoredEvent> QueryEvents(
        DateTimeOffset from, DateTimeOffset to, string? module = null, string? kind = null,
        string? groupKey = null, int limit = 10_000)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT id, ts, module, kind, severity, title, group_key, data FROM events
            WHERE ts>=$a AND ts<=$b
              AND ($m IS NULL OR module=$m) AND ($kind IS NULL OR kind=$kind) AND ($g IS NULL OR group_key=$g)
            ORDER BY ts DESC LIMIT $lim
            """,
            ("$a", Ms(from)), ("$b", Ms(to)), ("$m", module), ("$kind", kind), ("$g", groupKey), ("$lim", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<StoredEvent>();
        while (r.Read())
        {
            list.Add(new StoredEvent(
                r.GetInt64(0), FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), (Severity)r.GetInt32(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7)) ?? []));
        }
        return list;
    }

    // ── findings ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the stored findings of (module, scope) match <paramref name="current"/>:
    /// new ones open, known ones refresh (an ignored one stays ignored, a resolved one reopens),
    /// missing ones resolve.
    /// </summary>
    /// <returns>The findings that have just appeared: new, or back after having been resolved.</returns>
    /// <param name="defaultArea">The section for findings that do not name their own.</param>
    public IReadOnlyList<NewFinding> SyncFindings(string module, string scope, IReadOnlyCollection<NewFinding> current, DateTimeOffset now, string? defaultArea = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var existing = ReadFindings(c, "WHERE module=$m AND scope=$s", ("$m", module), ("$s", scope))
            .ToDictionary(f => f.Key);
        var nowMs = Ms(now);
        var appeared = new List<NewFinding>();

        foreach (var f in current.DistinctBy(f => f.Key))
        {
            var fix = f.Fix is null ? null : JsonSerializer.Serialize(f.Fix);
            var area = f.Area ?? defaultArea;
            var latest = f.LatestAt is { } at ? Ms(at) : (object?)null;
            if (existing.TryGetValue(f.Key, out var old))
            {
                // Marked as solved by the person, and nothing has happened since: it stays solved. The module still
                // reports it only because the old occurrences are inside the period it looks at.
                var stillSolved = old.Status == FindingStatus.Resolved && old.SolvedAt is { } solved && f.LatestAt is { } last && last <= solved;
                var reopened = old.Status == FindingStatus.Resolved && !stillSolved;
                if (reopened) appeared.Add(f);
                var status = stillSolved ? FindingStatus.Resolved : old.Status == FindingStatus.Ignored ? FindingStatus.Ignored : FindingStatus.Open;
                Exec(c, """
                    UPDATE findings SET severity=$sev, title=$t, detail=$d, advice=$adv, fix=$fix, area=$area, status=$st, latest_at=$lat,
                        solved_at=CASE WHEN $reopened THEN NULL ELSE solved_at END,
                        first_seen=CASE WHEN $reopened THEN $now ELSE first_seen END, last_seen=$now
                    WHERE id=$id
                    """,
                    ("$sev", (int)f.Severity), ("$t", f.Title), ("$d", f.Detail), ("$adv", f.Advice), ("$fix", fix), ("$area", area),
                    ("$st", status.ToString()), ("$lat", latest), ("$reopened", reopened), ("$now", nowMs), ("$id", old.Id));
            }
            else
            {
                appeared.Add(f);
                Exec(c, """
                    INSERT INTO findings(module, scope, key, severity, title, detail, advice, fix, area, status, first_seen, last_seen, latest_at)
                    VALUES($m, $s, $k, $sev, $t, $d, $adv, $fix, $area, 'Open', $now, $now, $lat)
                    """,
                    ("$m", module), ("$s", scope), ("$k", f.Key), ("$sev", (int)f.Severity),
                    ("$t", f.Title), ("$d", f.Detail), ("$adv", f.Advice), ("$fix", fix), ("$area", area), ("$now", nowMs), ("$lat", latest));
            }
        }

        var active = current.Select(f => f.Key).ToHashSet();
        // Only open problems resolve by themselves. An ignored one stays ignored when it goes quiet, so that
        // when it comes back (recurring noise does) it does not reopen: ignoring it was the person's answer.
        foreach (var gone in existing.Values.Where(f => f.Status == FindingStatus.Open && !active.Contains(f.Key)))
            Exec(c, "UPDATE findings SET status='Resolved' WHERE id=$id", ("$id", gone.Id));

        tx.Commit();
        return appeared;
    }

    public IReadOnlyList<StoredFinding> GetFindings(bool includeClosed = false)
    {
        using var c = Open();
        return ReadFindings(c, includeClosed ? "" : "WHERE status='Open'");
    }

    public bool SetFindingStatus(long id, FindingStatus status)
    {
        using var c = Open();
        return Exec(c, "UPDATE findings SET status=$st, solved_at=NULL WHERE id=$id", ("$st", status.ToString()), ("$id", id)) > 0;
    }

    /// <summary>
    /// The person says the cause is fixed. The problem closes now and comes back only if what it reports
    /// happens again after this moment (it needs to know when it last happened: <see cref="StoredFinding.CanBeSolved"/>).
    /// </summary>
    public bool SolveFinding(long id, DateTimeOffset now)
    {
        using var c = Open();
        return Exec(c, "UPDATE findings SET status='Resolved', solved_at=$now WHERE id=$id AND latest_at IS NOT NULL",
            ("$now", Ms(now)), ("$id", id)) > 0;
    }

    private static List<StoredFinding> ReadFindings(SqliteConnection c, string where, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, $"""
            SELECT id, module, scope, key, severity, title, detail, advice, status, first_seen, last_seen, fix, area, latest_at, solved_at
            FROM findings {where} ORDER BY severity DESC, last_seen DESC
            """, args);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredFinding>();
        while (r.Read())
        {
            list.Add(new StoredFinding(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), (Severity)r.GetInt32(4),
                r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                Enum.Parse<FindingStatus>(r.GetString(8)), FromMs(r.GetInt64(9)), FromMs(r.GetInt64(10)),
                r.IsDBNull(11) ? null : JsonSerializer.Deserialize<FindingFix>(r.GetString(11)),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : FromMs(r.GetInt64(13)),
                r.IsDBNull(14) ? null : FromMs(r.GetInt64(14))));
        }
        return list;
    }

    // ── module state ─────────────────────────────────────────────────────────

    public string? GetState(string module, string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT value FROM module_state WHERE module=$m AND key=$k", ("$m", module), ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetState(string module, string key, string? value)
    {
        using var c = Open();
        if (value is null)
            Exec(c, "DELETE FROM module_state WHERE module=$m AND key=$k", ("$m", module), ("$k", key));
        else
            Exec(c, """
                INSERT INTO module_state(module, key, value) VALUES($m, $k, $v)
                ON CONFLICT(module, key) DO UPDATE SET value=excluded.value
                """, ("$m", module), ("$k", key), ("$v", value));
    }

    // ── action log ───────────────────────────────────────────────────────────

    public void LogAction(DateTimeOffset at, string module, string action, string actor, ActionResult result)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO actions(ts, module, action, actor, ok, detail, undo)
            VALUES($ts, $m, $a, $who, $ok, $d, $u)
            """,
            ("$ts", Ms(at)), ("$m", module), ("$a", action), ("$who", actor),
            ("$ok", result.Ok), ("$d", result.Detail), ("$u", result.Undo));
    }

    public IReadOnlyList<ActionLogEntry> GetActions(int limit = 100)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, ts, module, action, actor, ok, detail, undo FROM actions ORDER BY ts DESC, id DESC LIMIT $lim",
            ("$lim", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ActionLogEntry>();
        while (r.Read())
        {
            list.Add(new ActionLogEntry(
                r.GetInt64(0), FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetBoolean(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    // ── activities ───────────────────────────────────────────────────────────

    /// <summary>Starts an activity, owned by this process: if the process dies, the activity reads as interrupted.</summary>
    public long BeginActivity(string module, string title, string actor, DateTimeOffset at)
    {
        using var c = Open();
        // Finished ones are kept a month: enough to look back, never a growing pile.
        Exec(c, "DELETE FROM activities WHERE ended IS NOT NULL AND ended < $old", ("$old", Ms(at.AddDays(-30))));
        using var cmd = Cmd(c, """
            INSERT INTO activities(module, title, actor, started, updated, state, pid) VALUES($m, $t, $a, $ts, $ts, 'Running', $pid);
            SELECT last_insert_rowid();
            """, ("$m", module), ("$t", title), ("$a", actor), ("$ts", Ms(at)), ("$pid", Environment.ProcessId));
        return (long)cmd.ExecuteScalar()!;
    }

    public void ProgressActivity(long id, string? text, int? done, int? total, DateTimeOffset at)
    {
        using var c = Open();
        Exec(c, "UPDATE activities SET progress=$p, done=$d, total=$t, updated=$ts WHERE id=$id AND ended IS NULL",
            ("$p", text), ("$d", done), ("$t", total), ("$ts", Ms(at)), ("$id", id));
    }

    public void EndActivity(long id, bool ok, string detail, DateTimeOffset at)
    {
        using var c = Open();
        Exec(c, "UPDATE activities SET state=$s, detail=$d, ended=$ts, updated=$ts WHERE id=$id",
            ("$s", (ok ? ActivityState.Done : ActivityState.Failed).ToString()), ("$d", detail), ("$ts", Ms(at)), ("$id", id));
    }

    /// <summary>The newest activities first. One still "running" whose process is gone reads as interrupted.</summary>
    public IReadOnlyList<StoredActivity> GetActivities(int limit = 50)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT a.id, a.module, a.title, a.actor, a.started, a.updated, a.ended, a.state, a.progress, a.done, a.total, a.detail, a.pid,
                   (SELECT p.ended - p.started FROM activities p WHERE p.module = a.module AND p.title = a.title AND p.state = 'Done' AND p.id < a.id ORDER BY p.id DESC LIMIT 1)
            FROM activities a ORDER BY a.id DESC LIMIT $lim
            """, ("$lim", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<StoredActivity>();
        while (r.Read())
        {
            var state = Enum.Parse<ActivityState>(r.GetString(7));
            if (state == ActivityState.Running && !Alive(r.GetInt32(12))) state = ActivityState.Interrupted;
            list.Add(new StoredActivity(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), FromMs(r.GetInt64(4)), FromMs(r.GetInt64(5)),
                r.IsDBNull(6) ? null : FromMs(r.GetInt64(6)), state, r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(13) ? null : TimeSpan.FromMilliseconds(r.GetInt64(13))));
        }
        return list;
    }

    /// <summary>For the tests: as if another process had started it.</summary>
    internal void SetActivityOwner(long id, int pid)
    {
        using var c = Open();
        Exec(c, "UPDATE activities SET pid=$pid WHERE id=$id", ("$pid", pid), ("$id", id));
    }

    private static bool Alive(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            try
            {
                return !p.HasExited;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true; // There, but run as administrator: a normal panel may not look inside it.
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;");
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
}
