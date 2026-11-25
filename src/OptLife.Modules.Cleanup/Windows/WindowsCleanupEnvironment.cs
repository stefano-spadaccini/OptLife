using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OptLife.Core;

namespace OptLife.Modules.Cleanup.Windows;

/// <summary>
/// The real machine. Reading is done with plain file and registry calls; the only things that change
/// anything are Move (inside the person's own folders), Recycle (to the Recycle Bin, never a real
/// delete) and RemoveStartup (the person's own startup entries, with the command to put them back).
/// </summary>
public sealed class WindowsCleanupEnvironment : ICleanupEnvironment
{
    private const int FindDepth = 7;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // What Windows itself keeps in the root of a disk: never "something new".
    private static readonly string[] SystemPrefixes = ["$", "System Volume Information", "DumpStack", "TMP", "WRP"];

    private static string Real(string path) => Environment.ExpandEnvironmentVariables(path);

    public IReadOnlyList<string>? List(string folder)
    {
        var path = Real(folder);
        if (!Directory.Exists(path)) return null;
        try
        {
            return [.. new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Select(e => e.Name)
                .Where(n => !n.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
                    && !SystemPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase) && (p.Length > 3 || n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || p == "$")))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<DiskItem> Weigh(string folder)
    {
        var path = Real(folder);
        var items = new List<DiskItem>();
        if (!Directory.Exists(path)) return items;
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is FileInfo file) items.Add(new DiskItem(file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
                else items.Add(Measure(entry.FullName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return items;
    }

    public IReadOnlyList<DiskItem> Find(string root, IReadOnlyList<string> names, string? marker)
    {
        var found = new List<DiskItem>();
        var start = Real(root);
        if (!Directory.Exists(start)) return found;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((start, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            try
            {
                foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
                {
                    if ((dir.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
                    if (names.Contains(dir.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        // The marker (a project file, say) must sit next to it: a folder that merely shares the name is left alone.
                        if (marker is null || Directory.EnumerateFileSystemEntries(folder, marker).Any()) found.Add(Measure(dir.FullName));
                        continue; // Never look inside a match, nor for matches inside it.
                    }
                    if (depth < FindDepth && dir.Name is not (".git" or "node_modules" or "AppData")) pending.Push((dir.FullName, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    /// <summary>Total size of a folder and the last time anything inside it was written.</summary>
    private static DiskItem Measure(string path)
    {
        long bytes = 0;
        var newest = DateTime.MinValue;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            try
            {
                foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is FileInfo file)
                    {
                        bytes += file.Length;
                        if (file.LastWriteTimeUtc > newest) newest = file.LastWriteTimeUtc;
                    }
                    else pending.Push(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (newest == DateTime.MinValue) newest = Directory.GetLastWriteTimeUtc(path);
        return new DiskItem(path, bytes, new DateTimeOffset(newest, TimeSpan.Zero));
    }

    public IReadOnlyList<string> BrokenLinks(string folder)
    {
        var root = Real(folder);
        return [.. BrokenShortcuts(folder, recursive: true).Select(p => Path.GetRelativePath(root, p))];
    }

    /// <summary>
    /// Shortcuts that open nothing any more: a file gone from a drive that is there, or a Store app no longer installed. Not broken:
    /// a network path or a drive not plugged in (the target may come back), or a shortcut whose target cannot be read.
    /// </summary>
    public IReadOnlyList<string> BrokenShortcuts(string folder, bool recursive)
    {
        var broken = new List<string>();
        var root = Real(folder);
        if (!Directory.Exists(root)) return broken;
        object? shell = null;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type || (shell = Activator.CreateInstance(type)) is null) return broken;
            foreach (var link in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }))
            {
                try
                {
                    dynamic shortcut = ((dynamic)shell).CreateShortcut(link);
                    string target = Environment.ExpandEnvironmentVariables(shortcut.TargetPath ?? "");
                    string arguments = shortcut.Arguments ?? "";
                    // A shortcut that keeps only a shell item (an app of the Apps folder): read what it points to.
                    var item = target.Length == 0 && arguments.Length == 0 ? AppsFolderId(link) : null;
                    if (ShortcutCheck.IsBroken(target, arguments, item, StoreFamilyInstalled, p => File.Exists(p) || Directory.Exists(p), Directory.Exists))
                        broken.Add(link);
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            if (shell is not null) Marshal.ReleaseComObject(shell);
        }
        return broken;
    }

    /// <summary>
    /// The app id ("4DF9E0F8.Netflix_mcm4njqhnhss8!Netflix.App") of a shortcut made from the Apps folder: such a shortcut keeps the
    /// item, not a file or a command, and only the shell reads it. Null for any other shortcut.
    /// </summary>
    private static string? AppsFolderId(string lnk)
    {
        object? shell = null;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type || (shell = Activator.CreateInstance(type)) is null) return null;
            dynamic folder = ((dynamic)shell).NameSpace(Path.GetDirectoryName(lnk)!);
            dynamic? item = folder?.ParseName(Path.GetFileName(lnk));
            if (item is null || !(bool)item.IsLink) return null;
            string path = item.GetLink.Target.Path ?? "";
            // An app id has "!" and no folder in it.
            return path.Contains('!') && !path.Contains('\\') ? path : null;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (shell is not null) Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Whether a Store package family is installed for the person signed in, asked to Windows itself (GetPackagesByPackageFamily).
    /// The registry list of families used before is not filled any more on recent Windows builds: on 24 Nov 2026 it made Netflix,
    /// Apple TV, WhatsApp and seven more look uninstalled. When Windows cannot answer, the answer is "installed" (never a false "broken").
    /// </summary>
    private static bool StoreFamilyInstalled(string family)
    {
        try
        {
            uint count = 0, length = 0;
            var result = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
            // ERROR_SUCCESS with no packages = not installed; ERROR_INSUFFICIENT_BUFFER = there is at least one.
            return result switch { 0 => count > 0, 122 => count > 0, _ => true };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    public IReadOnlyList<StartupEntry> Startup()
    {
        var entries = new List<StartupEntry>();
        void FromRegistry(RegistryKey root, string path, string prefix, bool removable)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) return;
                foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
                    entries.Add(new StartupEntry($"{prefix}:{name}", name, key.GetValue(name)?.ToString() ?? "", removable));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        void FromFolder(Environment.SpecialFolder which, string prefix, bool removable)
        {
            try
            {
                var folder = Environment.GetFolderPath(which);
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    entries.Add(new StartupEntry($"{prefix}:{Path.GetFileName(file)}", Path.GetFileNameWithoutExtension(file), file, removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        FromRegistry(Registry.CurrentUser, RunKey, "HKCU", removable: true);
        FromRegistry(Registry.LocalMachine, RunKey, "HKLM", removable: false);
        FromRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM32", removable: false);
        FromFolder(Environment.SpecialFolder.Startup, "folder", removable: true);
        FromFolder(Environment.SpecialFolder.CommonStartup, "commonfolder", removable: false);
        return entries;
    }

    public IReadOnlyList<string> InstalledPrograms()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var program = key.OpenSubKey(sub);
                    if (program?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                    if (program.GetValue("SystemComponent") is int and not 0 || program.GetValue("ParentKeyName") is not null) continue;
                    // Without the version at the end, so an update is not read as "one removed, one installed".
                    names.Add(System.Text.RegularExpressions.Regex.Replace(name, @"\s+v?[\d\.]+(\s*\(.*\))?$", "").Trim());
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return [.. names];
    }

    public ActionResult Move(string folder, IReadOnlyList<string> names, string destination) => MoveTracked(folder, names, destination).Result;

    public ActionResult MoveBack(IReadOnlyList<MovedItem> items)
    {
        var back = new List<string>();
        var skipped = new List<string>();
        foreach (var item in items)
        {
            var from = Real(item.To);
            var to = Real(item.From);
            var name = Path.GetFileName(to);
            // Only what is still where OptLife put it, and only to a free place: nothing is ever written over.
            if (!(File.Exists(from) || Directory.Exists(from)) || File.Exists(to) || Directory.Exists(to)
                || Protection.IsElevated() && (Protection.ThroughLink(from) || Protection.ThroughLink(to)))
            {
                skipped.Add(name);
                continue;
            }
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                back.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(name);
            }
        }
        return new ActionResult(back.Count > 0 || skipped.Count == 0,
            L.T($"Rimessi dov'erano: {back.Count}.", $"Put back: {back.Count}.") + (skipped.Count > 0 ? L.T(" Lasciati (spostati altrove o già al loro posto): ", " Left (moved elsewhere or a namesake is back): ") + string.Join(", ", skipped) + "." : ""));
    }

    public (ActionResult Result, IReadOnlyList<MovedItem> Moved) MoveTracked(string folder, IReadOnlyList<string> names, string destination)
    {
        var from = Real(folder);
        var to = Real(destination);
        var pairs = new List<MovedItem>();
        var moved = new List<string>();
        var failed = new List<string>();
        try
        {
            Directory.CreateDirectory(to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new ActionResult(false, L.T($"Non riesco a creare {to}: {ex.Message}", $"Cannot create {to}: {ex.Message}")), []);
        }
        // As administrator, never through a link a user could have made (a "Desktop\Hub" that leads into Windows' folders).
        if (Protection.IsElevated() && (Protection.ThroughLink(from) || Protection.ThroughLink(to)))
            return (new ActionResult(false, L.T($"Non sposto niente: {from} o {to} passa attraverso un collegamento a un'altra cartella.", $"Nothing moved: {from} or {to} goes through a link to another folder.")), []);
        foreach (var name in names)
        {
            var source = Path.Combine(from, name);
            // Never over something already there: a second "Gioco.lnk" becomes "Gioco (2).lnk".
            var target = Path.Combine(to, name);
            for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
                target = Path.Combine(to, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            try
            {
                if (Directory.Exists(source)) Directory.Move(source, target);
                else if (File.Exists(source)) File.Move(source, target);
                else continue;
                moved.Add(name);
                pairs.Add(new MovedItem(source, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(name);
            }
        }
        var detail = L.T($"{(moved.Count == 1 ? "1 elemento spostato" : $"{moved.Count} elementi spostati")} in {to}", $"{(moved.Count == 1 ? "1 item moved" : $"{moved.Count} items moved")} to {to}")
            + (failed.Count > 0 ? L.T("; non riusciti (in uso?): ", "; failed (in use?): ") + string.Join(", ", failed) : "") + ".";
        return (new ActionResult(moved.Count > 0 || failed.Count == 0, detail, moved.Count > 0 ? L.T($"Rimettili da {to} in {from}", $"Put them back from {to} to {from}") : null), pairs);
    }

    public ActionResult Recycle(IReadOnlyList<string> paths)
    {
        var existing = paths.Select(Real).Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
        if (existing.Count == 0) return ActionResult.Nothing(L.T("Non c'era più niente da togliere.", "There was nothing left to remove."));

        // Windows wants the paths one after the other, each closed by a zero, and one more zero at the end.
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', existing) + "\0\0",
            // To the bin, quietly; but if something is too big for the bin, Windows must ask before destroying it.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        var code = SHFileOperation(ref operation);
        var left = existing.Count(p => Directory.Exists(p) || File.Exists(p));
        if (code != 0 || operation.fAnyOperationsAborted || left > 0)
            return new ActionResult(left < existing.Count, L.T($"Nel Cestino {existing.Count - left} su {existing.Count}; le altre sono rimaste dov'erano (in uso, o hai annullato).", $"{existing.Count - left} of {existing.Count} in the Recycle Bin; the others stayed where they were (in use, or you cancelled)."),
                left < existing.Count ? L.T("Ripristinale dal Cestino", "Restore them from the Recycle Bin") : null);
        return new ActionResult(true, existing.Count == 1 ? L.T("1 cartella spostata nel Cestino.", "1 folder sent to the Recycle Bin.") : L.T($"{existing.Count} cartelle spostate nel Cestino.", $"{existing.Count} folders sent to the Recycle Bin."), L.T("Ripristinale dal Cestino", "Restore them from the Recycle Bin"));
    }

    public ActionResult RemoveStartup(string id)
    {
        var cut = id.IndexOf(':');
        if (cut < 0) return new ActionResult(false, L.T("Voce di avvio non riconosciuta.", "Start-up entry not recognised."));
        var (where, name) = (id[..cut], id[(cut + 1)..]);
        try
        {
            switch (where)
            {
                case "HKCU":
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                    {
                        if (key?.GetValue(name) is not { } command) return new ActionResult(true, L.T($"{name} non era più all'avvio.", $"{name} was no longer at start-up."));
                        key.DeleteValue(name);
                        return new ActionResult(true, L.T($"{name} tolto dai programmi all'avvio. Resta installato.", $"{name} removed from the start-up programs. It stays installed."),
                            $"reg add \"HKCU\\{RunKey}\" /v \"{name}\" /d \"{command.ToString()!.Replace("\"", "\\\"")}\"");
                    }
                case "folder":
                    var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), name);
                    return File.Exists(file) ? Recycle([file]) with { Detail = L.T($"{Path.GetFileNameWithoutExtension(name)} tolto dalla cartella di avvio (è nel Cestino).", $"{Path.GetFileNameWithoutExtension(name)} removed from the Startup folder (it is in the Recycle Bin).") }
                        : new ActionResult(true, L.T($"{name} non era più all'avvio.", $"{name} was no longer at start-up."));
                default:
                    return new ActionResult(false, L.T("È registrato per tutti gli utenti: serve l'amministratore (Gestione attività → Avvio).", "It is registered for all users: it needs administrator rights (Task Manager → Startup)."));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new ActionResult(false, L.T("Non riuscita: ", "Failed: ") + ex.Message);
        }
    }

    public IReadOnlyList<Shortcut> Shortcuts(string folder)
    {
        var path = Real(folder);
        var list = new List<Shortcut>();
        if (!Directory.Exists(path)) return list;
        object? shell = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".url")
                {
                    var url = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..] ?? "";
                    list.Add(new Shortcut(file, Path.GetFileNameWithoutExtension(file), url));
                }
                else if (ext == ".lnk")
                {
                    var target = "";
                    try
                    {
                        if (shell is null && Type.GetTypeFromProgID("WScript.Shell") is { } type) shell = Activator.CreateInstance(type);
                        if (shell is not null)
                        {
                            dynamic link = ((dynamic)shell).CreateShortcut(file);
                            target = $"{link.TargetPath} {link.Arguments}".Trim();
                        }
                        if (target.Length == 0 && AppsFolderId(file) is { } app) target = "shell:AppsFolder\\" + app;
                    }
                    catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                    }
                    list.Add(new Shortcut(file, Path.GetFileNameWithoutExtension(file), target));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            if (shell is not null) Marshal.ReleaseComObject(shell);
        }
        return list;
    }

    public IReadOnlyList<string> Subfolders(string folder)
    {
        try
        {
            var path = Real(folder);
            return Directory.Exists(path) ? [.. Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public ActionResult MoveFile(string path, string toFolder, string? newName = null)
    {
        var from = Real(path);
        var folder = Real(toFolder);
        var to = Path.Combine(folder, newName ?? Path.GetFileName(from));
        try
        {
            if (!File.Exists(from)) return new ActionResult(false, L.T("non c'è più", "no longer there"));
            if (Protection.IsElevated() && (Protection.ThroughLink(from) || Protection.ThroughLink(folder)))
                return new ActionResult(false, L.T("passa attraverso un collegamento a un'altra cartella", "goes through a link to another folder"));
            if (File.Exists(to)) return new ActionResult(false, L.T("ce n'è già uno con lo stesso nome", "one with the same name is already there"));
            Directory.CreateDirectory(folder);
            File.Move(from, to);
            return new ActionResult(true, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public ActionResult Open(string folder)
    {
        var path = Real(folder);
        if (!Directory.Exists(path)) return new ActionResult(false, L.T($"La cartella {path} non c'è.", $"The folder {path} does not exist."));
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            return new ActionResult(true, L.T($"Aperta {path}.", $"Opened {path}."));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ActionResult(false, L.T("Non riuscita: ", "Failed: ") + ex.Message);
        }
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
}
