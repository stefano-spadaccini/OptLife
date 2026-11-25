using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OptLife.Modules.Space.Windows;

/// <summary>
/// The real machine: plain file reading, the Recycle Bin's own count (SHQueryRecycleBin), and sending to the bin through the
/// shell (SHFileOperation with undo), one item at a time so a file in use does not stop the rest. Never a real delete.
/// Code of its own: the Cleanup module also sends to the bin, but modules never share code.
/// </summary>
internal sealed class WindowsSpaceEnvironment : ISpaceEnvironment
{
    private static string Real(string path) => Environment.ExpandEnvironmentVariables(path);

    public IReadOnlyList<TempItem>? Temp(string folder)
    {
        var path = Real(folder);
        if (!Directory.Exists(path)) return null;
        var list = new List<TempItem>();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is FileInfo file)
                    {
                        list.Add(new TempItem(file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
                        continue;
                    }
                    long bytes = 0;
                    var newest = entry.LastWriteTimeUtc;
                    foreach (var f in ((DirectoryInfo)entry).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        bytes += f.Length;
                        if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                    }
                    list.Add(new TempItem(entry.FullName, bytes, new DateTimeOffset(newest, TimeSpan.Zero)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked meanwhile: not counted.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return list;
    }

    public IReadOnlyList<BinInfo> RecycleBins()
    {
        var list = new List<BinInfo>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try
            {
                if (!drive.IsReady) continue;
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(drive.RootDirectory.FullName, ref info) == 0)
                    list.Add(new BinInfo(drive.Name.TrimEnd('\\'), info.i64Size, info.i64NumItems, drive.TotalSize));
            }
            catch (IOException)
            {
            }
        }
        return list;
    }

    public ActionResult Recycle(IReadOnlyList<string> paths)
    {
        var existing = paths.Select(Real).Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
        if (existing.Count == 0) return new ActionResult(true, L.T("Non c'era più niente da togliere.", "There was nothing left to remove."));
        var moved = 0;
        foreach (var path in existing)
        {
            // One at a time: a file in use fails alone and the rest still goes. To the bin, quietly; but if something is too
            // big for the bin, Windows must ask before destroying it.
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
            };
            if (SHFileOperation(ref operation) == 0 && !operation.fAnyOperationsAborted && !Directory.Exists(path) && !File.Exists(path)) moved++;
        }
        var left = existing.Count - moved;
        return new ActionResult(moved > 0 || left == 0,
            L.T($"Nel Cestino {moved} su {existing.Count}", $"{moved} of {existing.Count} in the Recycle Bin") +
            (left > 0 ? L.T($"; {left} sono in uso e sono rimasti.", $"; {left} are in use and stayed.") : "."),
            L.T("Si riprendono dal Cestino.", "They can be restored from the Recycle Bin."));
    }

    public ActionResult Open(string target)
    {
        try
        {
            // Through Explorer, so it opens as the person even when OptLife runs as administrator.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Real(target)}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T($"Aperto: {target}", $"Opened: {target}"));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
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

    // Default packing: on 64-bit Windows the header declares it with 8-byte alignment (4 bytes of padding after cbSize).
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);
}
