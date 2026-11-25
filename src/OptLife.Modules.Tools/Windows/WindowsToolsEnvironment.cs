using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OptLife.Core;

namespace OptLife.Modules.Tools.Windows;

/// <summary>
/// The real machine: processes from a Toolhelp snapshot, closing by WM_CLOSE first (as the X button does),
/// and a forced end only for programs with no window left, which have nothing open to save.
/// </summary>
internal sealed partial class WindowsToolsEnvironment : IToolsEnvironment
{
    private static readonly TimeSpan GentleWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ForceWait = TimeSpan.FromSeconds(3);

    public IReadOnlyList<ProcessInfo> Processes()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var visible = VisibleWindowOwners();
        var fullscreen = FullscreenWindowOwners();
        var list = new List<ProcessInfo>();
        foreach (var (pid, parent, exe) in Snapshot())
        {
            if (!ProcessIdToSessionId((uint)pid, out var s) || s != session) continue;
            var path = PathOf(pid);
            list.Add(new ProcessInfo(pid, parent, Path.GetFileNameWithoutExtension(exe), path, PrivateBytes(pid), visible.Contains(pid), DescriptionOf(path),
                fullscreen.Contains(pid)));
        }
        return list;
    }

    /// <summary>Processes with a visible window that covers a whole screen: a game (or a film), never something to close.</summary>
    private static HashSet<int> FullscreenWindowOwners()
    {
        var owners = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || !GetWindowRect(h, out var rect)) return true;
            var monitor = MonitorFromWindow(h, MONITOR_DEFAULTTONULL);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return true;
            var m = info.rcMonitor;
            if (rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom)
            {
                GetWindowThreadProcessId(h, out var pid);
                owners.Add((int)pid);
            }
            return true;
        }, 0);
        return owners;
    }

    public ActionResult Close(IReadOnlyList<AppGroup> apps)
    {
        // First, the gentle way for all of them at once: what the X button or the tray "Esci" would do.
        foreach (var app in apps)
        {
            var pids = app.Members.Select(m => m.Pid).ToHashSet();
            var windows = TopLevelWindows(pids);
            var targets = windows.Where(w => w.Visible).ToList();
            if (targets.Count == 0) targets = [.. windows.Where(w => w.Pid == app.Root.Pid)]; // A tray program: its hidden window.
            foreach (var w in targets) PostMessage(w.Handle, WM_CLOSE, 0, 0);
        }
        WaitForExit(apps, GentleWait);

        var closed = new List<AppGroup>();
        var kept = new List<string>();
        foreach (var app in apps)
        {
            var alive = app.Members.Where(m => IsAlive(m.Pid)).ToList();
            if (alive.Count == 0)
            {
                closed.Add(app);
                continue;
            }
            // Still running with a window in sight: it may be asking to save something. That is the person's call.
            if (VisibleWindowOwners().Overlaps(alive.Select(m => m.Pid)))
            {
                kept.Add(app.Label + L.T(" (ha ancora una finestra aperta: forse chiede di salvare)", " (still has a window open: maybe it asks to save)"));
                continue;
            }
            // Nothing on screen: a background program that ignored the request. End it, the whole family.
            var denied = false;
            foreach (var m in alive)
            {
                try
                {
                    using var p = Process.GetProcessById(m.Pid);
                    p.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { } // Already gone.
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    denied = true;
                }
            }
            WaitForExit([app], ForceWait);
            if (app.Members.Any(m => IsAlive(m.Pid)))
                kept.Add($"{app.Label} ({(denied ? L.T("Windows non lo permette: gira come amministratore", "Windows does not allow it: it runs as administrator") : L.T("non si è chiuso", "did not close"))})");
            else
                closed.Add(app);
        }

        var freed = closed.Sum(a => a.Bytes);
        var detail = closed.Count == 0
            ? L.T("Non è stato chiuso niente.", "Nothing was closed.")
            : L.T($"Chiusi {closed.Count}: {string.Join(", ", closed.Select(a => a.Label))}. Memoria liberata: circa {AppRules.Size(freed)}.", $"Closed {closed.Count}: {string.Join(", ", closed.Select(a => a.Label))}. Memory freed: about {AppRules.Size(freed)}.");
        if (kept.Count > 0) detail += L.T(" Rimasti aperti: ", " Still open: ") + string.Join("; ", kept) + ".";
        return new ActionResult(closed.Count > 0 || kept.Count == 0, detail, closed.Count > 0 ? L.T("Riaprili dal menu Start o dai loro collegamenti.", "Reopen them from the Start menu or their shortcuts.") : null);
    }

    public ActionResult Launch(Tool tool)
    {
        var file = Environment.ExpandEnvironmentVariables(tool.File);
        if (!File.Exists(file)) return new ActionResult(false, L.T($"Non trovo {file}.", $"Cannot find {file}."));
        var script = Path.GetExtension(file).Equals(".ps1", StringComparison.OrdinalIgnoreCase);
        // With full rights only a file no one else can change: otherwise whoever changed it would run with those rights.
        if (tool.Admin && Protection.InstalledMode && Protection.WritableByOthers(file) is { } why)
            return new ActionResult(false, L.T($"{tool.Name}: non lo avvio da amministratore, {why}. Mettilo in una cartella che cambiano solo gli amministratori.",
                $"{tool.Name}: not started as administrator, {why}. Put it in a folder only administrators change."));
        // Without the "administrator" mark, as the person even from a panel that is administrator (the one the tray icon opens).
        if (!tool.Admin && IsElevated())
        {
            var folder = Path.GetDirectoryName(file) ?? "";
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var started = script
                ? Protection.StartAsUser(powershell, $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\" {tool.Arguments}".TrimEnd(), folder)
                : Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? Protection.StartAsUser(file, tool.Arguments, folder)
                    // Anything else (a shortcut, a document) through Explorer, which opens it as the person.
                    : Protection.StartAsUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{file}\"", folder);
            return started.Ok ? new ActionResult(true, L.T("Avviato: ", "Started: ") + tool.Name + ".")
                : new ActionResult(false, tool.Name + L.T(": non si avvia (", ": does not start (") + started.Detail + ").");
        }
        var start = new ProcessStartInfo
        {
            FileName = script ? "powershell.exe" : file,
            Arguments = script ? $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\" {tool.Arguments}".TrimEnd() : tool.Arguments,
            // Started "inside" its folder, as its shortcut would: scripts and programs find their files from there.
            WorkingDirectory = Path.GetDirectoryName(file) ?? "",
            UseShellExecute = true,
        };
        if (tool.Admin && !IsElevated()) start.Verb = "runas";
        try
        {
            Process.Start(start)?.Dispose();
            return new ActionResult(true, L.T("Avviato: ", "Started: ") + tool.Name + ".");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ActionResult(false, tool.Name + L.T(": annullato, il permesso di amministratore non è stato dato.", ": cancelled, administrator permission was not given."));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, tool.Name + L.T(": non si avvia (", ": does not start (") + ex.Message + ").");
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void WaitForExit(IReadOnlyList<AppGroup> apps, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && apps.Any(a => a.Members.Any(m => IsAlive(m.Pid))))
            Thread.Sleep(250);
    }

    // The ids come from a snapshot taken a moment before closing (the module reads the list again on
    // confirmation), so an id reused by a new process in those few seconds is not a real concern.
    private static bool IsAlive(int pid)
    {
        var h = OpenProcess(SYNCHRONIZE, false, (uint)pid);
        if (h == 0) return false;
        try
        {
            return WaitForSingleObject(h, 0) == WAIT_TIMEOUT;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string? PathOf(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static long PrivateBytes(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == 0) return 0;
        try
        {
            var counters = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            return GetProcessMemoryInfo(h, ref counters, counters.cb) ? (long)counters.PrivateUsage : 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static readonly Dictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    private static string? DescriptionOf(string? path)
    {
        if (path is null) return null;
        lock (Descriptions)
        {
            if (Descriptions.TryGetValue(path, out var known)) return known;
            string? description;
            try
            {
                description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                description = null;
            }
            return Descriptions[path] = string.IsNullOrWhiteSpace(description) ? null : description;
        }
    }

    private static List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1) return list;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return list;
            do list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    private static HashSet<int> VisibleWindowOwners() =>
        [.. TopLevelWindows(null).Where(w => w.Visible).Select(w => w.Pid)];

    /// <summary>Top-level windows, of the given processes or of all; "visible" means a titled window on screen, not a hidden helper.</summary>
    private static List<(nint Handle, int Pid, bool Visible)> TopLevelWindows(HashSet<int>? pids)
    {
        var list = new List<(nint, int, bool)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids is null || pids.Contains((int)pid))
                list.Add((h, (int)pid, IsWindowVisible(h) && GetWindowTextLength(h) > 0 && GetWindow(h, GW_OWNER) == 0));
            return true;
        }, 0);
        return list;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint TH32CS_SNAPPROCESS = 0x2, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x100000, WAIT_TIMEOUT = 0x102, WM_CLOSE = 0x10, GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, char[] name, ref int size);
    [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(nint process, ref PROCESS_MEMORY_COUNTERS_EX counters, uint size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
}
