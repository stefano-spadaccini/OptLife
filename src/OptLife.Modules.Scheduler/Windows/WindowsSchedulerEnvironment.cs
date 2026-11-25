using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using OptLife.Core;

namespace OptLife.Modules.Scheduler.Windows;

/// <summary>
/// The real machine. The agent runs as administrator, but a job is started as the person, with the token of the
/// Windows shell (as if opened from the desktop): a sync program or a backup script running as administrator would
/// write files only administrators own. Scripts run without a window.
/// </summary>
internal sealed class WindowsSchedulerEnvironment : ISchedulerEnvironment
{
    private static readonly TimeSpan GentleWait = TimeSpan.FromSeconds(6);

    public DateTimeOffset BootTime => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public bool Exists(string file) => File.Exists(Environment.ExpandEnvironmentVariables(file));

    public string? Unsafe(Job job) =>
        Protection.InstalledMode ? Protection.WritableByOthers(Environment.ExpandEnvironmentVariables(job.File)) is { } why ? $"{job.File}: {why}" : null : null;

    public (IJobProcess? Process, string? Error) Start(Job job)
    {
        var file = Environment.ExpandEnvironmentVariables(job.File);
        var folder = Path.GetDirectoryName(file) ?? "";
        var (exe, args) = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".ps1" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{file}\" {job.Arguments}"),
            ".cmd" or ".bat" => ("cmd.exe", $"/c \"\"{file}\" {job.Arguments}\""),
            ".vbs" => ("wscript.exe", $"\"{file}\" {job.Arguments}"),
            _ => (file, job.Arguments),
        };
        args = args.Trim();
        try
        {
            if (job.Admin || !IsElevated())
            {
                var info = new ProcessStartInfo(exe, args) { WorkingDirectory = folder, UseShellExecute = job.Admin && !IsElevated(), CreateNoWindow = job.IsScript };
                if (info.UseShellExecute)
                {
                    info.Verb = "runas";
                    if (job.IsScript) info.WindowStyle = ProcessWindowStyle.Hidden;
                }
                var p = Process.Start(info);
                return p is null ? (null, L.T("Windows non l'ha avviato", "Windows did not start it")) : (new JobProcess(p), null);
            }
            return AsShellUser(exe, args, folder, job.IsScript);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (null, L.T("annullato: il permesso di amministratore non è stato dato", "cancelled: administrator permission was not given"));
        }
        catch (Win32Exception ex)
        {
            return (null, L.T($"non si avvia ({ex.Message})", $"does not start ({ex.Message})"));
        }
    }

    public IReadOnlyDictionary<int, long> Probe(Job job, int? rootPid)
    {
        var snapshot = Snapshot();
        var members = new HashSet<int>();
        if (rootPid is { } root && IsAlive(root))
        {
            members.Add(root);
            // Its children, and theirs: a script's robocopy, a program's helpers.
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, parent) in snapshot)
                    if (members.Contains(parent) && pid != parent && members.Add(pid)) grew = true;
            }
        }
        // A program also owns what runs from its folder, even when another copy of it started those processes.
        if (!job.IsScript)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(Environment.ExpandEnvironmentVariables(job.File)));
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (folder is not null && !folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = folder.TrimEnd('\\') + "\\";
                var session = Process.GetCurrentProcess().SessionId;
                foreach (var (pid, _) in snapshot)
                    if (ProcessIdToSessionId((uint)pid, out var s) && s == session && PathOf(pid) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        members.Add(pid);
            }
        }
        var result = new Dictionary<int, long>();
        foreach (var pid in members)
            if (BytesMoved(pid) is { } bytes) result[pid] = bytes;
        return result;
    }

    public void Close(IReadOnlyCollection<int> pids)
    {
        var set = pids.ToHashSet();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (set.Contains((int)pid)) PostMessage(h, WM_CLOSE, 0, 0);
            return true;
        }, 0);
        var until = DateTime.UtcNow + GentleWait;
        while (DateTime.UtcNow < until && set.Any(IsAlive)) Thread.Sleep(250);
        // A program that lives in the tray only hides its window: by now it has gone quiet, ending it loses nothing.
        foreach (var pid in set.Where(IsAlive))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Gone already, or not ours to end.
            }
        }
    }

    // ── Starting as the person ───────────────────────────────────────────────

    private static (IJobProcess?, string?) AsShellUser(string exe, string args, string folder, bool hidden)
    {
        GetWindowThreadProcessId(GetShellWindow(), out var shellPid);
        if (shellPid == 0) return (null, L.T("il desktop di Windows non è pronto", "the Windows desktop is not ready"));
        var shell = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
        if (shell == 0) return (null, L.T("non riesco a leggere il desktop di Windows", "cannot read the Windows desktop"));
        nint token = 0, primary = 0;
        try
        {
            if (!OpenProcessToken(shell, TOKEN_DUPLICATE, out token)
                || !DuplicateTokenEx(token, MAXIMUM_ALLOWED, 0, SecurityImpersonation, TokenPrimary, out primary))
                return (null, L.T($"non riesco a usare il tuo utente (errore {Marshal.GetLastWin32Error()})", $"cannot use your user (error {Marshal.GetLastWin32Error()})"));

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (hidden)
            {
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;
            }
            var command = new StringBuilder($"\"{exe}\" {args}".TrimEnd());
            var flags = CREATE_UNICODE_ENVIRONMENT | (hidden ? CREATE_NO_WINDOW : 0);
            // The program is looked up on the PATH when it is not a full path (powershell.exe, cmd.exe).
            var app = Path.IsPathRooted(exe) ? exe : null;
            if (!CreateProcessWithTokenW(primary, 0, app, command, flags, 0, folder.Length > 0 ? folder : null, ref si, out var pi))
                return (null, L.T($"non si avvia (errore {Marshal.GetLastWin32Error()})", $"does not start (error {Marshal.GetLastWin32Error()})"));
            CloseHandle(pi.hThread);
            return (new HandleProcess(pi.hProcess, (int)pi.dwProcessId), null);
        }
        finally
        {
            if (primary != 0) CloseHandle(primary);
            if (token != 0) CloseHandle(token);
            CloseHandle(shell);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class JobProcess(Process p) : IJobProcess
    {
        public int Pid => p.Id;
        public bool HasExited => p.HasExited;
        public int? ExitCode => p.HasExited ? p.ExitCode : null;
    }

    public IJobProcess? Attach(int pid, DateTimeOffset startedBy)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // The number may have been reused by a process started later: then it is not ours.
            if (new DateTimeOffset(p.StartTime) > startedBy) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)pid);
        return h == 0 ? null : new HandleProcess(h, pid);
    }

    /// <summary>Keeps the handle from the start, so the exit code is there even after the process is gone.</summary>
    private sealed class HandleProcess(nint handle, int pid) : IJobProcess
    {
        public int Pid => pid;
        public bool HasExited => WaitForSingleObject(handle, 0) != WAIT_TIMEOUT;
        public int? ExitCode => HasExited && GetExitCodeProcess(handle, out var code) ? (int)code : null;
        ~HandleProcess() => CloseHandle(handle);
    }

    // ── Reading processes ────────────────────────────────────────────────────

    private static long? BytesMoved(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == 0) return null;
        try
        {
            return GetProcessIoCounters(h, out var io) ? (long)(io.ReadTransferCount + io.WriteTransferCount + io.OtherTransferCount) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

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

    private static List<(int Pid, int Parent)> Snapshot()
    {
        var list = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == -1) return list;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return list;
            do list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint TH32CS_SNAPPROCESS = 0x2, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_QUERY_INFORMATION = 0x400,
        SYNCHRONIZE = 0x100000, WAIT_TIMEOUT = 0x102, WM_CLOSE = 0x10, TOKEN_DUPLICATE = 0x2, MAXIMUM_ALLOWED = 0x02000000,
        CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000, STARTF_USESHOWWINDOW = 0x1;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const short SW_HIDE = 0;

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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(nint process, out IO_COUNTERS counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, char[] name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? application, StringBuilder commandLine, uint creationFlags,
        nint environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
