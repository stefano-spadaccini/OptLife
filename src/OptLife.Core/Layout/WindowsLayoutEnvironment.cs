using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OptLife.Core.Layout;

/// <summary>What the layout needs from the machine, behind an interface for the tests.</summary>
public interface ILayoutEnvironment
{
    /// <summary>The fixed drives that are ready ("C:\", "E:\").</summary>
    IReadOnlyList<string> Drives();

    bool FileExists(string path) => File.Exists(LayoutPaths.Expand(path));

    /// <summary>Folders at the top of a drive, without the hidden system ones.</summary>
    IReadOnlyList<string> TopFolders(string root);

    /// <param name="measure">Also weigh it (slow on big folders: only for those that could be moved).</param>
    FolderFacts Inspect(string path, bool measure);

    /// <summary>Installed programs, services and start-up entries, with the path each points to.</summary>
    IReadOnlyList<Reference> References();

    bool Exists(string path);
    ActionResult CreateFolder(string path);
    long FreeBytes(string path);

    /// <summary>The name Windows gives the drive ("New Volume", "Dati"); null when there is none.</summary>
    string? VolumeLabel(string drive) => null;
    bool SameVolume(string a, string b);

    /// <summary>Names of the running programs started from inside the folder.</summary>
    IReadOnlyList<string> RunningUnder(string path);

    /// <summary>
    /// Moves a folder. Same drive: renamed, instant. Another drive: copied, checked (number of files and bytes), and the original
    /// renamed "…-old-optlife" next to where it was, so nothing is lost until the person deletes it.
    /// </summary>
    ActionResult Move(string from, string to);

    /// <summary>A file moved within the same drive (a shortcut from one Hub section to another).</summary>
    ActionResult MoveFile(string from, string to) => new(false, "");

    /// <summary>The folders directly inside a folder (their names).</summary>
    IReadOnlyList<string> Subfolders(string path) => [];

    /// <summary>The folder holds nothing at all.</summary>
    bool IsEmpty(string path) => false;

    /// <summary>The size of the drive holding the path (bytes); 0 when it cannot be read.</summary>
    long TotalBytes(string path) => 0;

    /// <summary>A program is running with this process name (a launcher that must be closed before a move).</summary>
    bool ProcessRunning(string name) => false;

    /// <summary>
    /// Epic's own records (the .item manifests and LauncherInstalled.dat) made to point to the new folder, each copied first into
    /// <paramref name="backupFolder"/>; how many files were changed.
    /// </summary>
    int FixEpic(string from, string to, string backupFolder) => 0;

    /// <summary>The original kept after a move between drives, to the Recycle Bin (Windows asks before destroying what is too big for it).</summary>
    ActionResult RemoveOld(string path) => new(false, "");

    /// <summary>Shortcuts (Desktop, public Desktop, Start menu) pointing inside <paramref name="from"/> made to point inside <paramref name="to"/>.</summary>
    int FixShortcuts(string from, string to);
}

/// <summary>The real machine. Moves go through the shell (like Explorer); nothing is ever deleted.</summary>
public sealed class WindowsLayoutEnvironment : ILayoutEnvironment
{
    private static readonly string[] GameFiles = ["UnityPlayer.dll", "steam_api.dll", "steam_api64.dll", "GameAssembly.dll", "EOSSDK-Win64-Shipping.dll"];

    public IReadOnlyList<string> Drives() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)];

    public IReadOnlyList<string> TopFolders(string root)
    {
        try
        {
            return [.. new DirectoryInfo(root).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != (FileAttributes.Hidden | FileAttributes.System)
                            && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public FolderFacts Inspect(string path, bool measure)
    {
        var launcher = LauncherOf(path);
        var hasProgram = false;
        var game = false;
        long bytes = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                if (f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) hasProgram = true;
                if (GameFiles.Contains(f.Name, StringComparer.OrdinalIgnoreCase) || f.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase)) game = true;
                if (hasProgram && game) break;
            }
            if (measure)
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    bytes += f.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new FolderFacts(path, launcher, hasProgram, game || bytes >= 4L << 30, bytes);
    }

    /// <summary>A launcher leaves its mark in the folder: Steam's library, Epic's manifest, Battle.net's build info…</summary>
    private static Launcher LauncherOf(string path)
    {
        bool Has(string name) => File.Exists(Path.Combine(path, name)) || Directory.Exists(Path.Combine(path, name));
        if (Has("steamapps") || Has("libraryfolder.vdf")) return Launcher.Steam;
        if (Has(".egstore")) return Launcher.Epic;
        if (Has(".build.info") || Has(".product.db")) return Launcher.BattleNet;
        if (Has("__Installer")) return Launcher.Ea;
        if (Has(".GamingRoot") || Has("MicrosoftGame.config") || Has(@"Content\MicrosoftGame.config")) return Launcher.Xbox;
        if (Has("Riot Client") || Has("riotclientservices.exe")) return Launcher.Riot;
        if (Has("uplay_install.state")) return Launcher.Ubisoft;
        return Launcher.None;
    }

    public IReadOnlyList<Reference> References()
    {
        var list = new List<Reference>();
        void Uninstall(RegistryHive hive, string key)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = root.OpenSubKey(key);
                foreach (var sub in k?.GetSubKeyNames() ?? [])
                {
                    using var p = k!.OpenSubKey(sub);
                    if (p?.GetValue("DisplayName") is not string name) continue;
                    foreach (var value in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
                        if (PathIn(p.GetValue(value) as string) is { } path) list.Add(new Reference(RefKind.Installed, name, path));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        Uninstall(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        Uninstall(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Uninstall(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? [])
            {
                using var s = services!.OpenSubKey(name);
                if (PathIn(s?.GetValue("ImagePath") as string) is { } path) list.Add(new Reference(RefKind.Service, s?.GetValue("DisplayName") as string ?? name, path));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        foreach (var (hive, key) in new[] { (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"), (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                                            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run") })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = root.OpenSubKey(key);
                foreach (var name in k?.GetValueNames() ?? [])
                    if (PathIn(k!.GetValue(name) as string) is { } path) list.Add(new Reference(RefKind.Startup, name, path));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    /// <summary>The file path at the start of a command line or icon location ("\"C:\X\a.exe\" -x", "C:\X\a.exe,0").</summary>
    private static string? PathIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Environment.ExpandEnvironmentVariables(text.Trim());
        if (t.StartsWith('"')) t = t[1..].Split('"')[0];
        else if (t.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var e and >= 0) t = t[..(e + 4)];
        t = t.Split(',')[0].Trim();
        return t.Length >= 3 && t[1] == ':' ? t : null;
    }

    public bool Exists(string path) => Directory.Exists(LayoutPaths.Expand(path));

    public ActionResult CreateFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(LayoutPaths.Expand(path));
            return new ActionResult(true, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new ActionResult(false, $"{path}: {ex.Message}");
        }
    }

    public string? VolumeLabel(string drive)
    {
        try
        {
            var label = new DriveInfo(drive).VolumeLabel;
            return label.Length > 0 ? label : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(LayoutPaths.Expand(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return 0;
        }
    }

    public bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(LayoutPaths.Expand(a)), Path.GetPathRoot(LayoutPaths.Expand(b)), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> RunningUnder(string path)
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(0x1000, false, (uint)p.Id);
                if (h == 0) continue;
                try
                {
                    var buffer = new char[1024];
                    var size = buffer.Length;
                    if (QueryFullProcessImageName(h, 0, buffer, ref size) && LayoutPaths.Within(new string(buffer, 0, size), path)) names.Add(p.ProcessName);
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }
        return [.. names.Distinct()];
    }

    public ActionResult Move(string from, string to)
    {
        var source = LayoutPaths.Expand(from).TrimEnd('\\');
        var target = LayoutPaths.Expand(to).TrimEnd('\\');
        if (!Directory.Exists(source)) return new ActionResult(false, L.T("La cartella non c'è più.", "The folder is no longer there."));
        if (Directory.Exists(target) || File.Exists(target)) return new ActionResult(false, L.T($"Esiste già {target}.", $"{target} already exists."));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (SameVolume(source, target))
            {
                Directory.Move(source, target); // Same drive: instant, nothing copied.
                return new ActionResult(true, "");
            }
            // Another drive: copied as Explorer does, then checked; the original stays, renamed, until the person deletes it.
            var op = new SHFILEOPSTRUCT { wFunc = FO_COPY, pFrom = source + "\0\0", pTo = target + "\0\0", fFlags = FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_SILENT | FOF_NOERRORUI };
            var code = SHFileOperation(ref op);
            if (code != 0 || op.fAnyOperationsAborted || !Directory.Exists(target))
                return new ActionResult(false, L.T($"copia non completa (codice {code}): l'originale in {source} non è stato toccato", $"copy not complete (code {code}): the original in {source} was not touched"));
            var (files, bytes) = Weigh(source);
            var (copiedFiles, copiedBytes) = Weigh(target);
            if (files != copiedFiles || bytes != copiedBytes)
                return new ActionResult(false, L.T($"la copia non corrisponde ({copiedFiles} file su {files}): l'originale in {source} non è stato toccato, controlla {target}",
                    $"the copy does not match ({copiedFiles} files of {files}): the original in {source} was not touched, check {target}"));
            var old = OldName(source);
            Directory.Move(source, old);
            return new ActionResult(true, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    /// <summary>"E:\Game" → "E:\Game-old-optlife" (with a number when that is taken).</summary>
    public static string OldName(string source)
    {
        var old = source + "-old-optlife";
        for (var n = 2; Directory.Exists(old) || File.Exists(old); n++) old = $"{source}-old-optlife-{n}";
        return old;
    }

    private static (long Files, long Bytes) Weigh(string folder)
    {
        long files = 0, bytes = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            files++;
            bytes += file.Length;
        }
        return (files, bytes);
    }

    public ActionResult MoveFile(string from, string to)
    {
        try
        {
            if (!File.Exists(from)) return new ActionResult(false, L.T("Il file non c'è più.", "The file is no longer there."));
            if (File.Exists(to)) return new ActionResult(false, L.T($"Esiste già {to}.", $"{to} already exists."));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            return new ActionResult(true, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public IReadOnlyList<string> Subfolders(string path)
    {
        try { return Directory.Exists(LayoutPaths.Expand(path)) ? [.. Directory.GetDirectories(LayoutPaths.Expand(path)).Select(Path.GetFileName).OfType<string>()] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public bool IsEmpty(string path)
    {
        try { return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public long TotalBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(LayoutPaths.Expand(path))!).TotalSize; }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return 0; }
    }

    public bool ProcessRunning(string name) => System.Diagnostics.Process.GetProcessesByName(name).Length > 0;

    public int FixEpic(string from, string to, string backupFolder)
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic");
        var files = new List<string>();
        var manifests = Path.Combine(data, "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifests)) files.AddRange(Directory.GetFiles(manifests, "*.item"));
        var installed = Path.Combine(data, "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (File.Exists(installed)) files.Add(installed);
        var changed = 0;
        Directory.CreateDirectory(backupFolder);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var updated = LayoutEpic.Rebase(text, from, to);
            if (updated == text) continue;
            File.Copy(file, Path.Combine(backupFolder, Path.GetFileName(file)), overwrite: true);
            File.WriteAllText(file, updated);
            changed++;
        }
        return changed;
    }

    public ActionResult RemoveOld(string path)
    {
        if (!Directory.Exists(path)) return ActionResult.Nothing(L.T("Non c'era più.", "It was already gone."));
        var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING) };
        var code = SHFileOperation(ref op);
        return code == 0 && !op.fAnyOperationsAborted && !Directory.Exists(path)
            ? new ActionResult(true, L.T($"{path}: nel Cestino (o eliminata dopo la conferma di Windows, se troppo grande per il Cestino).", $"{path}: in the Recycle Bin (or deleted after Windows' confirmation, if too big for the bin)."))
            : new ActionResult(false, L.T($"{path} è rimasta dov'era (annullato o in uso).", $"{path} stayed where it was (cancelled or in use)."));
    }

    public int FixShortcuts(string from, string to)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        object? shell = null;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type || (shell = Activator.CreateInstance(type)) is null) return 0;
            foreach (var folder in folders)
                foreach (var link in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    try
                    {
                        dynamic s = ((dynamic)shell).CreateShortcut(link);
                        string target = s.TargetPath ?? "";
                        if (LayoutPaths.Rebase(target, from, to) is not { } moved) continue;
                        s.TargetPath = moved;
                        string work = s.WorkingDirectory ?? "";
                        if (LayoutPaths.Rebase(work, from, to) is { } w) s.WorkingDirectory = w;
                        string icon = s.IconLocation ?? "";
                        if (LayoutPaths.Rebase(icon.Split(',')[0], from, to) is { } i) s.IconLocation = i + (icon.Contains(',') ? icon[icon.IndexOf(',')..] : "");
                        s.Save();
                        changed++;
                    }
                    catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        // A shortcut that cannot be rewritten (in use, no rights) keeps pointing to the old place.
                    }
                }
        }
        finally
        {
            if (shell is not null) Marshal.ReleaseComObject(shell);
        }
        return changed;
    }

    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, char[] name, ref int size);
}
