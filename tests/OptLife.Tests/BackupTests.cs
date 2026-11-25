using System.Text.Json;
using OptLife.Core;
using OptLife.Core.Hosting;
using OptLife.Modules.Backup;
using OptLife.Modules.Backup.Windows;

namespace OptLife.Tests;

/// <summary>Disks, repositories, programs and devices as the test describes them.</summary>
internal sealed class FakeBackupEnvironment : IBackupEnvironment
{
    public Dictionary<string, FolderState> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissingDrives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RepoState> Repos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Reachable { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportState> Reports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Scans { get; private set; }

    public FolderState? Scan(string path, int verify)
    {
        Scans++;
        return Folders.GetValueOrDefault(path);
    }

    public bool DriveExists(string path) => !MissingDrives.Contains(Path.GetPathRoot(path) ?? "");
    public RepoState? Repository(string path) => Repos.GetValueOrDefault(path);
    public bool IsRunning(string process) => Running.Contains(process);
    public bool Reaches(string address) => Reachable.Contains(address);
    public ReportState? ReadReport(string file) => Reports.GetValueOrDefault(file);
}

public class BackupModuleTests
{
    private const string Saves = @"E:\Backups", Worlds = @"C:\Mondi", Snapshots = @"E:\snapshot";
    private readonly Sandbox _box = new();
    private readonly FakeBackupEnvironment _env = new();
    private readonly BackupModule _module;

    public BackupModuleTests() => _module = new BackupModule(_env);

    private DateTimeOffset Now => _box.Clock.Now;

    private void Set(string key, params string[] lines) =>
        _box.Config.UpdateModule("backup", c => c.Settings[key] = JsonSerializer.SerializeToElement(lines));

    private async Task Check(TimeSpan? later = null)
    {
        _box.Clock.Now += later ?? TimeSpan.Zero;
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None),
            _box.Runner.ReadStatus(_module, _module.Tasks[0]).LastError);
    }

    private IReadOnlyList<StoredFinding> Open() => _box.Store.GetFindings();

    private FolderState Folder(int daysAgo, int files = 1000, params string[] unreadable) => new(Now.AddDays(-daysAgo), files, unreadable);

    [Fact]
    public async Task With_nothing_listed_it_does_nothing_and_says_nothing()
    {
        await Check();

        Assert.Empty(Open());
        Assert.Empty(_box.Store.QueryEvents(Now.AddDays(-1), Now.AddDays(1)));
    }

    [Fact]
    public async Task A_backup_written_recently_is_fine_and_leaves_one_line_in_the_history()
    {
        Set("copies", $"Salvataggi | {Saves} | 10");
        _env.Folders[Saves] = Folder(daysAgo: 1, files: 55814);

        await Check();

        Assert.Empty(Open());
        var line = Assert.Single(_box.Store.QueryEvents(Now.AddDays(-1), Now.AddDays(1), kind: "backup_check"));
        Assert.Equal("Protezione dati: 1 voce controllata, a posto", line.Title);
        Assert.StartsWith("55814 file, ultimo del", line.Data["copia · Salvataggi"]);
    }

    [Fact]
    public async Task A_backup_that_stopped_being_written_is_reported()
    {
        Set("copies", $"Salvataggi | {Saves} | 10");
        _env.Folders[Saves] = Folder(daysAgo: 23);

        await Check();

        var finding = Assert.Single(Open());
        Assert.Equal("Il backup \"Salvataggi\" è fermo da 23 giorni", finding.Title);
        Assert.Equal(Severity.Medium, finding.Severity);
    }

    [Fact]
    public async Task With_an_original_what_counts_is_how_far_behind_the_copy_is_not_how_old()
    {
        Set("copies", $"Mondi | {Snapshots} | 14 | {Worlds}");
        _env.Folders[Snapshots] = Folder(daysAgo: 200);
        _env.Folders[Worlds] = Folder(daysAgo: 195); // Not touched since shortly after the snapshot.
        await Check();
        Assert.Empty(Open());

        _env.Folders[Worlds] = Folder(daysAgo: 2); // Played again: the snapshot is now months behind.
        await Check(TimeSpan.FromHours(7));

        var finding = Assert.Single(Open());
        Assert.Equal("La copia di \"Mondi\" è indietro di 198 giorni", finding.Title);
    }

    [Fact]
    public async Task Something_that_was_never_copied_at_all_is_a_problem()
    {
        Set("copies", @"OptLife | D:\Backups\Documents\OptLife | 7 | C:\Users\alex\Documents\OptLife");
        _env.Folders[@"C:\Users\alex\Documents\OptLife"] = Folder(daysAgo: 0);

        await Check();

        Assert.Equal("Di \"OptLife\" non esiste nessuna copia", Assert.Single(Open()).Title);
    }

    [Fact]
    public async Task A_disk_that_is_not_there_is_not_blamed_on_the_copy()
    {
        Set("copies", @"Fumetti | \\nas\fumetti | 10");
        _env.MissingDrives.Add(@"\\nas\fumetti");

        await Check();

        Assert.Empty(Open());
        Assert.Equal("disco non raggiungibile", _box.Store.QueryEvents(Now.AddDays(-1), Now.AddDays(1), kind: "backup_check")[0].Data["copia · Fumetti"]);
    }

    [Fact]
    public async Task A_copy_with_files_that_cannot_be_read_back_is_serious()
    {
        Set("copies", $"Salvataggi | {Saves} | 10");
        _env.Folders[Saves] = Folder(1, 1000, @"08 - Emulatori\save.bin");

        await Check();

        var finding = Assert.Single(Open());
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains(@"08 - Emulatori\save.bin", finding.Detail);
    }

    [Fact]
    public async Task Work_in_progress_in_a_repository_is_not_a_problem_until_it_is_forgotten()
    {
        Set("repositories", @"C:\Progetti\PhotoVault");
        _env.Repos[@"C:\Progetti\PhotoVault"] = new RepoState(true, Changed: 4, Now.AddDays(-2), HasUpstream: true, Ahead: 0, Now.AddDays(-3));
        await Check();
        Assert.Empty(Open());

        await Check(TimeSpan.FromDays(9)); // Nobody touched it since.

        Assert.Equal("PhotoVault: 4 file modificati e mai salvati in un commit, fermi da 11 giorni", Assert.Single(Open()).Title);

        _env.Repos[@"C:\Progetti\PhotoVault"] = new RepoState(true, Changed: 1, Now.AddDays(-11), HasUpstream: true, Ahead: 0, Now.AddDays(-12));
        await Check(TimeSpan.FromHours(7));
        Assert.Equal("PhotoVault: 1 file modificato e mai salvato in un commit, fermo da 11 giorni", Assert.Single(Open()).Title);
    }

    [Fact]
    public async Task Commits_never_sent_and_a_repository_with_no_commit_at_all_are_reported()
    {
        Set("repositories", @"C:\Progetti\GameKit", @"C:\Progetti\OptLife", @"C:\Progetti\Notebook", @"C:\Progetti\Sparito");
        _env.Repos[@"C:\Progetti\GameKit"] = new RepoState(true, 0, null, HasUpstream: true, Ahead: 3, Now.AddDays(-20));
        _env.Repos[@"C:\Progetti\OptLife"] = new RepoState(HasCommits: false, 180, Now, HasUpstream: false, 0, null);
        _env.Repos[@"C:\Progetti\Notebook"] = new RepoState(true, 0, null, HasUpstream: false, 0, Now.AddDays(-1));

        await Check();

        var titles = Open().Select(f => f.Title).ToList();
        Assert.Contains("GameKit: 3 commit non inviati al server da 20 giorni", titles);
        Assert.Contains("OptLife: nessun commit, la storia del progetto non è salvata", titles);
        Assert.Contains("Non riesco a leggere il repository Sparito", titles);
        // Local-only by choice is information, not a problem.
        Assert.Equal(Severity.Info, Open().Single(f => f.Title == "Notebook esiste solo su questo PC").Severity);
    }

    [Fact]
    public async Task A_sync_program_that_is_closed_is_reported_only_when_missing_twice_in_a_row()
    {
        Set("programs", "Sincronizzazione NAS | syncspace_pro");

        await Check();
        Assert.Empty(Open()); // It may just not have started yet.

        await Check(TimeSpan.FromMinutes(30));
        Assert.Equal("Sincronizzazione NAS non è in esecuzione", Assert.Single(Open()).Title);

        _env.Running.Add("syncspace_pro");
        await Check(TimeSpan.FromMinutes(30));
        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_device_away_for_a_while_is_fine_and_away_for_too_long_is_not()
    {
        Set("hosts", "NAS | 192.168.1.20 | 30");
        _env.Reachable.Add("192.168.1.20");
        await Check();

        _env.Reachable.Clear(); // Gone to the other house.
        await Check(TimeSpan.FromDays(12));
        Assert.Empty(Open());

        await Check(TimeSpan.FromDays(20));
        var finding = Assert.Single(Open());
        Assert.Equal("NAS non si vede da 32 giorni", finding.Title);
        Assert.Equal(Severity.Low, finding.Severity);

        _env.Reachable.Add("192.168.1.20");
        await Check(TimeSpan.FromHours(1));
        Assert.Empty(Open());
    }

    [Fact]
    public async Task A_report_that_is_old_or_complains_is_reported_with_its_own_words()
    {
        const string file = @"E:\Backups\_REPORT.txt";
        Set("reports", $"Backup settimanale | {file} | 10 | ERRORE, AVVISO");
        _env.Reports[file] = new ReportState(Now.AddDays(-1), ["Totale: 176 voci", "  AVVISO  emulatore non trovato: Ryujinx", "tutto il resto ok"]);

        await Check();
        var complaint = Assert.Single(Open());
        Assert.Equal("Backup settimanale: l'ultimo resoconto segnala problemi", complaint.Title);
        Assert.Contains("emulatore non trovato: Ryujinx", complaint.Detail);

        _env.Reports[file] = new ReportState(Now.AddDays(-1), ["Totale: 176 voci"]);
        await Check(TimeSpan.FromHours(7));
        Assert.Empty(Open());

        await Check(TimeSpan.FromDays(15));
        Assert.Equal("Backup settimanale: l'ultimo resoconto è di 16 giorni fa", Assert.Single(Open()).Title);
    }

    [Fact]
    public async Task Folders_are_looked_through_a_few_times_a_day_not_every_half_hour_unless_the_lists_change()
    {
        Set("copies", $"Salvataggi | {Saves} | 10");
        _env.Folders[Saves] = Folder(1);

        await Check();
        await Check(TimeSpan.FromMinutes(30));
        await Check(TimeSpan.FromMinutes(30));
        Assert.Equal(1, _env.Scans);

        Set("copies", $"Salvataggi | {Saves} | 10", $"Mondi | {Snapshots} | 14");
        await Check(TimeSpan.FromMinutes(30));
        Assert.Equal(3, _env.Scans); // Edited: looked at again right away, both entries.

        await Check(TimeSpan.FromHours(6));
        Assert.Equal(5, _env.Scans);

        // "Check now" means now: asked by the person, everything is looked at again whatever the clock says.
        Assert.True(await _box.Runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None, onDemand: true));
        Assert.Equal(7, _env.Scans);
    }

    [Fact]
    public async Task Nothing_is_read_while_a_game_is_running()
    {
        using var board = new LiveBoard($@"Local\OptLife.Test.{Guid.NewGuid():N}");
        var runner = new ModuleRunner(_box.Store, _box.Config, _box.Clock, board);
        board.Publish("games", [new LiveValue("game.minutes", "In gioco da", "Gioco", 3, "min")], Now);
        Set("copies", $"Salvataggi | {Saves} | 10");

        Assert.True(await runner.RunTaskAsync(_module, _module.Tasks[0], CancellationToken.None));

        Assert.Equal(0, _env.Scans);
        Assert.Empty(Open());
    }

    [Fact]
    public void A_line_that_is_not_written_properly_is_skipped_rather_than_guessed()
    {
        Assert.Null(CopyEntry.Parse(@"Salvataggi | E:\backup"));
        Assert.Null(CopyEntry.Parse(@"Salvataggi | E:\backup | presto"));
        Assert.Equal(new CopyEntry("Salvataggi", @"E:\backup", 10, null), CopyEntry.Parse(@" Salvataggi |E:\backup| 10 "));
        Assert.Equal(@"C:\Mondi", CopyEntry.Parse(@"Mondi | E:\snap | 14 | C:\Mondi")!.Origin);
    }
}

public class WindowsBackupEnvironmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optlife-tests", Guid.NewGuid().ToString("N"));
    private readonly WindowsBackupEnvironment _env = new();

    public WindowsBackupEnvironmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, string text, DateTime? modified = null)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (modified is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    [Fact]
    public void A_folder_is_counted_dated_by_its_newest_file_and_read_back()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        Write(@"a\uno.sav", "1", old);
        Write(@"a\b\due.sav", "22", recent);
        Write(@"tre.sav", "333", old);
        // What gets rebuilt does not count: it would make any original look "newer than its copy" forever.
        Write(@"bin\Debug\app.dll", "x");
        Write(@".git\index", "x");

        var state = _env.Scan(_dir, verify: 10)!;

        Assert.Equal(3, state.Files);
        Assert.Equal(recent, state.Newest.UtcDateTime);
        Assert.Empty(state.Unreadable);
        Assert.Null(_env.Scan(Path.Combine(_dir, "non-esiste"), 0));
        Assert.True(_env.DriveExists(_dir));
        Assert.False(_env.DriveExists(@"Q:\niente"));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_named()
    {
        var locked = Write("bloccato.sav", "dati");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var state = _env.Scan(_dir, verify: 5)!;

        Assert.Equal(["bloccato.sav"], state.Unreadable);
    }

    [Fact]
    public void A_report_is_read_with_its_date_and_a_missing_one_is_missing()
    {
        var file = Write("_REPORT.txt", "riga uno\nAVVISO qualcosa\n", new DateTime(2026, 11, 15, 19, 0, 0, DateTimeKind.Utc));

        var report = _env.ReadReport(file)!;

        Assert.Equal(["riga uno", "AVVISO qualcosa"], report.Lines);
        Assert.Equal(new DateTime(2026, 11, 15, 19, 0, 0, DateTimeKind.Utc), report.Modified.UtcDateTime);
        Assert.Null(_env.ReadReport(Path.Combine(_dir, "manca.txt")));
    }

    [Fact]
    public void A_real_repository_is_read_through_git()
    {
        if (!RunGit("init -q")) return; // No git on this machine: nothing to test here.
        RunGit("config user.email test@example.com");
        RunGit("config user.name Test");
        Write("codice.cs", "class A {}");

        var fresh = _env.Repository(_dir)!;
        Assert.False(fresh.HasCommits);
        Assert.Equal(1, fresh.Changed);

        RunGit("add -A");
        RunGit("commit -q -m primo");
        Write("codice.cs", "class A { int x; }");
        Write(@"nuova cartella\altro file.cs", "class B {}");

        var dirty = _env.Repository(_dir)!;
        Assert.True(dirty.HasCommits);
        Assert.Equal(2, dirty.Changed);
        Assert.False(dirty.HasUpstream);
        Assert.NotNull(dirty.LastCommit);
        Assert.NotNull(dirty.NewestChange);
        Assert.InRange((DateTimeOffset.UtcNow - dirty.NewestChange.Value).TotalMinutes, -1, 5);

        Assert.Null(_env.Repository(Path.GetTempPath())); // A folder that is not a repository.
    }

    private bool RunGit(string arguments)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", $"-C \"{_dir}\" {arguments}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
