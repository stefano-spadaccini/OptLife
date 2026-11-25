using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OptLife.Core;

namespace OptLife.Modules.Tools.Windows;

/// <summary>The known programs and the shader caches on the real PC. Removals go to the Recycle Bin, one item at a time; never a real delete.</summary>
internal sealed partial class WindowsToolsEnvironment
{
    public bool FileExists(string path) => File.Exists(path);

    public string? FindFile(string folder, string name)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var here = Path.Combine(folder, name);
            if (File.Exists(here)) return here;
            // One level below: an unzipped program sits in its own folder ("DDU v18.0.9.4\Display Driver Uninstaller.exe"), two at most.
            foreach (var sub in Directory.EnumerateDirectories(folder).Take(500))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.Exists(Path.Combine(sub, name))) return Path.Combine(sub, name);
                foreach (var deeper in Directory.EnumerateDirectories(sub).Take(50))
                    if (File.Exists(Path.Combine(deeper, name))) return Path.Combine(deeper, name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public ActionResult LaunchKnown(string file, bool admin)
    {
        if (!File.Exists(file)) return new ActionResult(false, L.T($"Non trovo {file}.", $"Cannot find {file}."));
        var folder = Path.GetDirectoryName(file) ?? "";
        var name = Path.GetFileNameWithoutExtension(file);
        // With full rights only a file no one else can change (Program Files). One in Downloads starts as the person: Windows asks.
        var safe = Protection.WritableByOthers(file) is null;
        if (Protection.IsElevated() && !(admin && safe))
        {
            // Through Explorer, as a double-click: a program that needs the rights asks Windows for them itself.
            var started = Protection.StartAsUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{file}\"", folder);
            return started.Ok ? new ActionResult(true, L.T($"Avviato: {name}.", $"Started: {name}.")) : new ActionResult(false, $"{name}: {started.Detail}");
        }
        try
        {
            // Started "inside" its own folder, as its shortcut does: some (Equalizer APO's selector) find their parts from there.
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = folder, Verb = admin && !Protection.IsElevated() ? "runas" : "" })?.Dispose();
            return new ActionResult(true, L.T($"Avviato: {name}.", $"Started: {name}."));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ActionResult(false, name + L.T(": annullato, il permesso di amministratore non è stato dato.", ": cancelled, administrator permission was not given."));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, $"{name}: {ex.Message}");
        }
    }

    public IReadOnlyList<ShaderCache> ShaderCaches()
    {
        var list = new List<ShaderCache>();
        foreach (var raw in KnownTools.ShaderCacheFolders)
        {
            var path = Environment.ExpandEnvironmentVariables(raw);
            if (!Directory.Exists(path)) continue;
            long bytes = 0;
            var items = 0;
            try
            {
                items = Directory.EnumerateFileSystemEntries(path).Count();
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    bytes += f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            list.Add(new ShaderCache(raw, bytes, items));
        }
        return list;
    }

    public ActionResult RecycleContents(IReadOnlyList<string> folders)
    {
        int moved = 0, total = 0;
        foreach (var raw in folders)
        {
            var folder = Environment.ExpandEnvironmentVariables(raw);
            if (!Directory.Exists(folder)) continue;
            // As administrator, never through a link: the cache folder must be the driver's own, not a way into another one.
            if (Protection.IsElevated() && Protection.ThroughLink(folder)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
            {
                total++;
                var operation = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = entry + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
                };
                if (SHFileOperation(ref operation) == 0 && !operation.fAnyOperationsAborted && !File.Exists(entry) && !Directory.Exists(entry)) moved++;
            }
        }
        var left = total - moved;
        return new ActionResult(moved > 0 || left == 0,
            L.T($"Cache degli shader: nel Cestino {moved} elementi su {total}", $"Shader cache: {moved} of {total} items in the Recycle Bin")
            + (left > 0 ? L.T($"; {left} in uso sono rimasti.", $"; {left} in use stayed.") : ".") + L.T(" Il driver la ricrea alla prossima partita.", " The driver builds it again at the next game."),
            L.T("Si riprende dal Cestino.", "It can be restored from the Recycle Bin."));
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

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
