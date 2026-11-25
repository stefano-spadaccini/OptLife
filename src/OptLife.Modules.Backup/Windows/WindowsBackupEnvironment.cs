using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;

namespace OptLife.Modules.Backup.Windows;

/// <summary>The real machine. Everything here only reads.</summary>
public sealed class WindowsBackupEnvironment : IBackupEnvironment
{
    private const int MaxFiles = 300_000;
    private const long VerifyUpTo = 64L * 1024 * 1024; // Per file: enough to catch a bad sector without reading a whole disk image.

    // Folders that are rebuilt, not made: they change all the time and no backup takes them.
    private static readonly HashSet<string> Rebuilt = new(StringComparer.OrdinalIgnoreCase)
        // Also build output (artifacts, dist, publish, TestResults): a backup leaves it out, and compared file by file it would all look missing.
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "Library", "Temp", "__pycache__", "artifacts", "dist", "publish", "TestResults" };

    public bool DriveExists(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.ExpandEnvironmentVariables(path));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public FolderState? Scan(string path, int verify) => Scan(path, verify, index: false);

    public FolderState? ScanIndexed(string path, int verify) => Scan(path, verify, index: true);

    private FolderState? Scan(string path, int verify, bool index)
    {
        var root = Environment.ExpandEnvironmentVariables(path);
        if (!Directory.Exists(root)) return null;

        var marks = index ? new Dictionary<string, FileMark>(StringComparer.OrdinalIgnoreCase) : null;
        var newest = DateTimeOffset.MinValue;
        var files = 0;
        var sample = new List<string>(); // A fair pick without knowing the total in advance (reservoir sampling).
        var random = new Random();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && files < MaxFiles)
        {
            var folder = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; // Links lead elsewhere, possibly in circles.
                    if (entry is DirectoryInfo dir)
                    {
                        if (!Rebuilt.Contains(dir.Name)) pending.Push(dir.FullName);
                        continue;
                    }
                    // A database's -wal and -shm, a lock file: a program's work in progress, not data (and not on both sides alike).
                    if (BackupRules.Temporary(entry.Name)) continue;
                    files++;
                    if (entry.LastWriteTimeUtc > newest.UtcDateTime) newest = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                    if (marks is not null && entry is FileInfo file)
                        marks[Path.GetRelativePath(root, file.FullName)] = new FileMark(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
                    if (sample.Count < verify) sample.Add(entry.FullName);
                    else if (verify > 0 && random.Next(files) < verify) sample[random.Next(verify)] = entry.FullName;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A folder that cannot be listed is skipped; what can be read still counts.
            }
        }

        var unreadable = new List<string>();
        var buffer = new byte[1024 * 1024];
        foreach (var file in sample)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long total = 0;
                int read;
                while (total < VerifyUpTo && (read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read;
            }
            catch (FileNotFoundException)
            {
                // Removed between the listing and the reading: not damage.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(Path.GetRelativePath(root, file));
            }
        }
        // Listed whole only: a folder cut at the limit cannot be compared file by file (the dates are compared instead).
        return new FolderState(files == 0 ? DateTimeOffset.MinValue : newest, files, unreadable, marks is not null && files < MaxFiles ? marks : null);
    }

    public RepoState? Repository(string path)
    {
        var folder = Environment.ExpandEnvironmentVariables(path);
        if (!Directory.Exists(folder)) return null;
        if (Git(folder, "-c core.quotepath=false status --porcelain=v2 --branch") is not { } status) return null;

        var hasCommits = true;
        var hasUpstream = false;
        var ahead = 0;
        var changed = 0;
        DateTimeOffset? newest = null;
        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.oid ")) hasCommits = !line.EndsWith("(initial)");
            else if (line.StartsWith("# branch.upstream ")) hasUpstream = true;
            else if (line.StartsWith("# branch.ab ")) _ = int.TryParse(line.Split(' ')[2].TrimStart('+'), out ahead);
            else if (line[0] is '1' or '2' or 'u' or '?')
            {
                changed++;
                if (changed > 300) continue; // Enough to date the work; the count goes on.
                // The path is the last field; a rename carries the old name after a tab.
                var fields = line[0] switch { '1' => 9, '2' => 10, 'u' => 11, _ => 2 };
                var parts = line.Split(' ', fields);
                if (parts.Length < fields) continue;
                var relative = parts[^1].Split('\t')[0].Trim('"');
                try
                {
                    var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                    var time = Directory.Exists(full) ? Directory.GetLastWriteTimeUtc(full) : File.Exists(full) ? File.GetLastWriteTimeUtc(full) : (DateTime?)null;
                    if (time is { } t && (newest is null || t > newest.Value.UtcDateTime)) newest = new DateTimeOffset(t, TimeSpan.Zero);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        DateTimeOffset? lastCommit = null;
        if (hasCommits && Git(folder, "log -1 --format=%ct") is { } stamp && long.TryParse(stamp.Trim(), out var seconds))
            lastCommit = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new RepoState(hasCommits, changed, newest, hasUpstream, ahead, lastCommit);
    }

    public bool IsRunning(string process)
    {
        var found = Process.GetProcessesByName(process);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public bool Reaches(string address)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var ping = new Ping();
                if (ping.Send(address, 1500).Status == IPStatus.Success) return true;
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
            }
        }
        return false;
    }

    public ReportState? ReadReport(string file)
    {
        var path = Environment.ExpandEnvironmentVariables(file);
        try
        {
            if (!File.Exists(path)) return null;
            return new ReportState(new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), [.. File.ReadLines(path).Take(20_000)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Runs git in a folder and returns what it printed; null when git is missing, fails or takes too long.</summary>
    private static string? Git(string folder, string arguments)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", $"-C \"{folder}\" {arguments}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (git is null) return null;
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(20_000))
            {
                git.Kill(entireProcessTree: true);
                return null;
            }
            return git.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
