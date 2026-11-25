using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Backup;

/// <param name="Newest">When the most recently changed file in the folder was changed (files only: a folder's date moves when a program
/// merely opens a database in it).</param>
/// <param name="Unreadable">Files picked at random that could not be read back.</param>
/// <param name="Index">Every file by its path inside the folder, with size and time: for comparing an original with its copy file by file.
/// Null when it was not asked for (or the folder is too big to list whole).</param>
public sealed record FolderState(DateTimeOffset Newest, int Files, IReadOnlyList<string> Unreadable, IReadOnlyDictionary<string, FileMark>? Index = null);

public sealed record FileMark(long Size, DateTimeOffset Written);

/// <param name="Changed">Files changed or new that are not in any commit.</param>
/// <param name="NewestChange">When the most recent of those was touched.</param>
/// <param name="Ahead">Commits that exist here but were never sent to the server.</param>
public sealed record RepoState(bool HasCommits, int Changed, DateTimeOffset? NewestChange, bool HasUpstream, int Ahead, DateTimeOffset? LastCommit);

public sealed record ReportState(DateTimeOffset Modified, IReadOnlyList<string> Lines);

/// <summary>"Name | folder of the copy | days | original folder (optional)".</summary>
public sealed record CopyEntry(string Name, string Copy, int Days, string? Origin)
{
    public static CopyEntry? Parse(string line)
    {
        var p = BackupRules.Split(line);
        return p.Length >= 3 && p[0].Length > 0 && p[1].Length > 0 && int.TryParse(p[2], out var days) && days > 0
            ? new CopyEntry(p[0], p[1], days, p.Length > 3 && p[3].Length > 0 ? p[3] : null)
            : null;
    }
}

/// <summary>Judges what was found. Pure: no disk, no network, no clock of its own.</summary>
public static class BackupRules
{
    private static CultureInfo Lang => L.Culture;

    public static string[] Split(string line) => line.Split('|', StringSplitOptions.TrimEntries);

    private static string Day(DateTimeOffset t) => t.ToLocalTime().ToString("d MMMM", Lang);

    private static string Days(TimeSpan span) => (int)span.TotalDays == 1 ? L.T("1 giorno", "1 day") : L.T($"{(int)span.TotalDays} giorni", $"{(int)span.TotalDays} days");

    /// <param name="copy">Null when the folder of the copy does not exist.</param>
    /// <param name="driveThere">Whether the disk (or network share) the copy lives on can be reached at all.</param>
    /// <param name="origin">Null when there is no original to compare with, or it cannot be reached.</param>
    public static NewFinding? Copy(CopyEntry entry, FolderState? copy, bool driveThere, FolderState? origin, DateTimeOffset now)
    {
        var key = $"copy|{entry.Name.ToLowerInvariant()}";
        if (!driveThere) return null; // A disk that is not plugged in, a NAS in another house: nothing can be said, and that is not the copy's fault.
        if (copy is null || copy.Files == 0)
            return new NewFinding(key, Severity.Medium,
                L.T($"Di \"{entry.Name}\" non esiste nessuna copia", $"There is no backup of \"{entry.Name}\""),
                copy is null ? L.T($"La cartella {entry.Copy} non c'è.", $"The folder {entry.Copy} does not exist.") : L.T($"La cartella {entry.Copy} è vuota.", $"The folder {entry.Copy} is empty."),
                L.T("Se dovrebbe finirci in automatico, quella parte del backup non la sta prendendo: va aggiunta a ciò che il backup copia.", "If it should get there automatically, the backup is missing that part: add it to what the backup copies."));

        if (copy.Unreadable.Count > 0)
            return new NewFinding(key, Severity.High,
                L.T($"La copia di \"{entry.Name}\" ha file che non si leggono", $"The backup of \"{entry.Name}\" has unreadable files"),
                L.T($"Su {copy.Files.ToString("N0", Lang)} file ne ho riletti alcuni a caso e questi non si aprono: {string.Join(", ", copy.Unreadable.Take(3))}.", $"Out of {copy.Files.ToString("N0", Lang)} files I re-read some at random and these do not open: {string.Join(", ", copy.Unreadable.Take(3))}."),
                L.T("Una copia che non si legge non è una copia: controlla il disco su cui sta e rifai il backup.", "A backup that cannot be read is not a backup: check the disk it is on and back up again."));

        // Not one path in common: the copy keeps the files in another arrangement (a subfolder, a dated folder), so paths cannot be
        // matched and the dates are compared instead.
        if (origin is { Index: { } originFiles } && copy.Index is { } copyFiles && originFiles.Keys.Any(copyFiles.ContainsKey))
        {
            // File by file: in step when every file of the original is in the copy, same size, not older. Behind only when something
            // changed in the original longer ago than the days allowed and is still not in the copy.
            var missing = Missing(originFiles, copyFiles);
            var late = missing.Where(m => now - m.Mark.Written > TimeSpan.FromDays(entry.Days)).ToList();
            if (late.Count == 0) return null;
            var oldest = late.Min(m => m.Mark.Written);
            return new NewFinding(key, Severity.Medium,
                L.T($"La copia di \"{entry.Name}\" è indietro di {Days(now - oldest)}", $"The backup of \"{entry.Name}\" is {Days(now - oldest)} behind"),
                L.T($"{missing.Count} file dell'originale non sono nella copia, o sono cambiati dopo: {string.Join(", ", late.OrderBy(m => m.Mark.Written).Take(3).Select(m => m.Path))}. Il primo è cambiato il {Day(oldest)}.",
                    $"{missing.Count} files of the original are not in the backup, or changed after it: {string.Join(", ", late.OrderBy(m => m.Mark.Written).Take(3).Select(m => m.Path))}. The first changed on {Day(oldest)}."),
                L.T("Rifai il backup o lo snapshot: quello che è cambiato da allora esiste in un posto solo.", "Run the backup or snapshot again: what changed since then exists in one place only."));
        }

        if (origin is not null)
        {
            var behind = origin.Newest - copy.Newest;
            return behind > TimeSpan.FromDays(entry.Days)
                ? new NewFinding(key, Severity.Medium,
                    L.T($"La copia di \"{entry.Name}\" è indietro di {Days(behind)}", $"The backup of \"{entry.Name}\" is {Days(behind)} behind"),
                    L.T($"L'originale è cambiato l'ultima volta il {Day(origin.Newest)}, la copia è ferma al {Day(copy.Newest)}.", $"The original last changed on {Day(origin.Newest)}, the backup stopped on {Day(copy.Newest)}."),
                    L.T("Rifai il backup o lo snapshot: quello che è cambiato da allora esiste in un posto solo.", "Run the backup or snapshot again: what changed since then exists in one place only."))
                : null;
        }

        var age = now - copy.Newest;
        return age > TimeSpan.FromDays(entry.Days)
            ? new NewFinding(key, Severity.Medium,
                L.T($"Il backup \"{entry.Name}\" è fermo da {Days(age)}", $"The \"{entry.Name}\" backup has not run for {Days(age)}"),
                L.T($"L'ultimo file scritto in {entry.Copy} è del {Day(copy.Newest)}.", $"The last file written in {entry.Copy} is from {Day(copy.Newest)}."),
                L.T("Controlla che il backup automatico stia ancora girando, oppure lancialo a mano.", "Check that the automatic backup is still running, or start it by hand."))
            : null;
    }

    /// <summary>
    /// The files of the original the copy does not have as they are: missing, of another size, or older there. Two seconds of slack on
    /// the time, as robocopy allows (FAT and network drives keep times in steps of two seconds).
    /// </summary>
    public static IReadOnlyList<(string Path, FileMark Mark)> Missing(IReadOnlyDictionary<string, FileMark> origin, IReadOnlyDictionary<string, FileMark> copy) =>
        [.. origin.Where(o => !copy.TryGetValue(o.Key, out var c) || c.Size != o.Value.Size || c.Written < o.Value.Written - TimeSpan.FromSeconds(2))
            .Select(o => (o.Key, o.Value))];

    /// <summary>
    /// Files that are not data but a program's work in progress: SQLite's -wal, -shm and -journal (they come and go while a database is
    /// open), Office's ~$ lock files, .tmp, .lock. They are left out on both sides: on 25 Nov 2026 a database merely opened made a
    /// complete copy look 16 days behind.
    /// </summary>
    public static bool Temporary(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith("-wal") || n.EndsWith("-shm") || n.EndsWith("-journal") || n.StartsWith("~$") || n.StartsWith(".~lock")
               || n.EndsWith(".tmp") || n.EndsWith(".temp") || n.EndsWith(".lock") || n is "lockfile" or "thumbs.db" or "desktop.ini";
    }

    /// <param name="repo">Null when the folder is not a git repository or git cannot be run.</param>
    public static IEnumerable<NewFinding> Repository(string path, RepoState? repo, int days, DateTimeOffset now)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var key = $"repo|{path.ToLowerInvariant()}";
        if (repo is null)
        {
            yield return new NewFinding(key, Severity.Low, L.T($"Non riesco a leggere il repository {name}", $"Cannot read the {name} repository"),
                L.T($"{path} non è un repository git, oppure git non è installato.", $"{path} is not a git repository, or git is not installed."), L.T("Correggi il percorso nelle impostazioni del modulo, o toglilo dall'elenco.", "Fix the path in the module settings, or remove it from the list."));
            yield break;
        }

        var limit = TimeSpan.FromDays(days);
        if (!repo.HasCommits && repo.Changed > 0)
            yield return new NewFinding(key, Severity.Low, L.T($"{name}: nessun commit, la storia del progetto non è salvata", $"{name}: no commits, the project history is not saved"),
                L.T("Il repository esiste ma non contiene ancora nessun commit: se un file si rovina non c'è una versione a cui tornare.", "The repository exists but has no commits yet: if a file gets damaged there is no version to go back to."),
                L.T("Fai un primo commit quando il progetto è in uno stato che funziona.", "Make a first commit when the project is in a working state."));
        else if (repo.Changed > 0 && repo.NewestChange is { } touched && now - touched > limit)
            yield return new NewFinding(key, Severity.Low,
                repo.Changed == 1
                    ? L.T($"{name}: 1 file modificato e mai salvato in un commit, fermo da {Days(now - touched)}", $"{name}: 1 file changed and never committed, untouched for {Days(now - touched)}")
                    : L.T($"{name}: {repo.Changed} file modificati e mai salvati in un commit, fermi da {Days(now - touched)}", $"{name}: {repo.Changed} files changed and never committed, untouched for {Days(now - touched)}"),
                L.T($"L'ultima modifica è del {Day(touched)}: sembra lavoro lasciato a metà.", $"The last change is from {Day(touched)}: it looks like work left half done."), L.T("Fai un commit, oppure scarta le modifiche se non servono più.", "Make a commit, or discard the changes if they are no longer needed."));

        if (repo.HasUpstream && repo.Ahead > 0 && repo.LastCommit is { } last && now - last > limit)
            yield return new NewFinding(key + "|push", Severity.Low,
                L.T($"{name}: {(repo.Ahead == 1 ? "1 commit non inviato" : $"{repo.Ahead} commit non inviati")} al server da {Days(now - last)}", $"{name}: {(repo.Ahead == 1 ? "1 commit" : $"{repo.Ahead} commits")} not pushed to the server for {Days(now - last)}"),
                L.T("Quei commit esistono solo su questo PC.", "Those commits exist only on this PC."), L.T("Invia i commit (git push).", "Push the commits (git push)."));

        if (repo.HasCommits && !repo.HasUpstream)
            yield return new NewFinding(key + "|local", Severity.Info, L.T($"{name} esiste solo su questo PC", $"{name} exists only on this PC"),
                L.T("Il repository non è collegato a nessun server: la sua storia non ha una copia altrove.", "The repository is not linked to any server: its history has no copy elsewhere."),
                L.T("Se è una scelta, ignora questo avviso; altrimenti collegalo a un server o includilo nel backup.", "If it is a choice, ignore this warning; otherwise link it to a server or include it in the backup."));
    }

    public static NewFinding? Program(string name, string process, int timesMissing) =>
        timesMissing < 2 ? null : new NewFinding($"program|{process.ToLowerInvariant()}", Severity.Medium,
            L.T($"{name} non è in esecuzione", $"{name} is not running"),
            L.T($"Il programma \"{process}\" non risulta aperto: finché è chiuso il suo lavoro (sincronizzare, copiare) non viene fatto.", $"The \"{process}\" program is not open: while it is closed its work (syncing, copying) does not get done."),
            L.T("Riaprilo, e controlla che sia tra i programmi che partono con Windows.", "Open it again, and check it is among the programs that start with Windows."));

    /// <param name="lastSeen">When it last answered; null when it has never been seen since it was added.</param>
    /// <param name="watchedSince">When OptLife started looking for it.</param>
    public static NewFinding? Host(string name, string address, int days, DateTimeOffset? lastSeen, DateTimeOffset watchedSince, DateTimeOffset now)
    {
        var missing = now - (lastSeen ?? watchedSince);
        return missing <= TimeSpan.FromDays(days) ? null : new NewFinding($"host|{address.ToLowerInvariant()}", Severity.Low,
            L.T($"{name} non si vede da {Days(missing)}", $"{name} has not been seen for {Days(missing)}"),
            lastSeen is null ? L.T($"{address} non ha mai risposto da quando lo controllo.", $"{address} has never answered since I started checking it.") : L.T($"{address} ha risposto l'ultima volta il {Day(lastSeen.Value)}.", $"{address} last answered on {Day(lastSeen.Value)}."),
            L.T("Finché non è raggiungibile, le copie che dovrebbero arrivarci restano solo su questo PC.", "Until it is reachable, the backups that should go there stay only on this PC."));
    }

    /// <param name="report">Null when the file does not exist.</param>
    public static NewFinding? Report(string name, string file, int days, IReadOnlyList<string> words, ReportState? report, DateTimeOffset now)
    {
        var key = $"report|{name.ToLowerInvariant()}";
        if (report is null)
            return new NewFinding(key, Severity.Low, L.T($"{name}: il resoconto non c'è", $"{name}: the report is missing"), L.T($"Il file {file} non esiste.", $"The file {file} does not exist."), L.T("Controlla il percorso, o che il lavoro che lo scrive sia mai partito.", "Check the path, or that the job writing it ever ran."));

        var age = now - report.Modified;
        if (age > TimeSpan.FromDays(days))
            return new NewFinding(key, Severity.Medium, L.T($"{name}: l'ultimo resoconto è di {Days(age)} fa", $"{name}: the last report is {Days(age)} old"),
                L.T($"{file} è stato scritto l'ultima volta il {Day(report.Modified)}.", $"{file} was last written on {Day(report.Modified)}."), L.T("Il lavoro che lo scrive non sta più girando: controlla la sua attività pianificata.", "The job writing it is no longer running: check its scheduled task."));

        var alarming = report.Lines.Where(l => words.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        return alarming.Count == 0 ? null : new NewFinding(key, Severity.Medium,
            L.T($"{name}: l'ultimo resoconto segnala problemi", $"{name}: the last report shows problems"),
            L.T($"{(alarming.Count == 1 ? "1 riga" : $"{alarming.Count} righe")} da guardare. ", $"{(alarming.Count == 1 ? "1 line" : $"{alarming.Count} lines")} to look at. ") + string.Join(" · ", alarming.Take(3).Select(l => l.Trim())),
            L.T($"Apri {file} e sistema ciò che segnala.", $"Open {file} and fix what it reports."));
    }
}
