using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace OptLife.Modules.Library.Windows;

/// <summary>
/// The real machine: the public lists and box art (read-only downloads, no account), the screen and the graphics card's memory,
/// starting an emulator as the person, the Recycle Bin.
/// </summary>
internal sealed class WindowsLibraryEnvironment : ILibraryEnvironment
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptLife", "1.0"));
        return http;
    }

    public async Task<bool> Download(string url, string path, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".part";
            await using (var file = File.Create(temp))
                await response.Content.CopyToAsync(file, ct);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<string?> GetText(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public Screen PrimaryScreen()
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmPelsHeight > 0
            ? new Screen(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 60)
            : new Screen(1920, 1080, 60);
    }

    /// <summary>The biggest memory among the graphics cards, from the driver's own record (no administrator needed).</summary>
    public double VramGb()
    {
        double best = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return 0;
            foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var card = cls.OpenSubKey(name);
                var bytes = card?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => card?.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0L,
                    },
                };
                best = Math.Max(best, bytes / 1024.0 / 1024 / 1024);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not readable: the presets take a middle guess.
        }
        return best;
    }

    public string Documents() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ── PC launchers ─────────────────────────────────────────────────────────

    public IReadOnlyList<UninstallEntry> Uninstall()
    {
        var list = new List<UninstallEntry>();
        void Read(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) return;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || display.Length == 0) continue;
                    list.Add(new UninstallEntry(name, display.Trim(), entry.GetValue("Publisher") as string ?? "", entry.GetValue("InstallLocation") as string ?? "",
                        entry.GetValue("UninstallString") as string ?? "", entry.GetValue("DisplayIcon") as string ?? ""));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A list that cannot be read: the others still count.
            }
        }
        Read(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        return list;
    }

    private static string? Value(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public string? SteamFolder() =>
        (Value(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") ?? Value(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"))
            ?.Replace('/', '\\');

    private static IEnumerable<(string Name, RegistryKey Key)> SubKeys(RegistryKey root, string path)
    {
        RegistryKey? parent;
        try { parent = root.OpenSubKey(path); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { yield break; }
        if (parent is null) yield break;
        using (parent)
            foreach (var name in parent.GetSubKeyNames())
            {
                RegistryKey? sub;
                try { sub = parent.OpenSubKey(name); }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { continue; }
                if (sub is null) continue;
                using (sub) yield return (name, sub);
            }
    }

    public IReadOnlyList<(string Id, string Folder)> UbisoftInstalls() =>
        [.. SubKeys(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs")
            .Select(s => (s.Name, (s.Key.GetValue("InstallDir") as string ?? "").Replace('/', '\\').TrimEnd('\\')))
            .Where(s => s.Item2.Length > 0)];

    public IReadOnlyList<(string Id, string Name, string Folder, string Exe)> GogGames() =>
        [.. SubKeys(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games")
            .Select(s => (s.Name, s.Key.GetValue("gameName") as string ?? s.Name, s.Key.GetValue("path") as string ?? "", s.Key.GetValue("exe") as string ?? ""))
            .Where(g => g.Item3.Length > 0)];

    public string? GogClient() =>
        Value(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client") is { } dir
            ? Path.Combine(dir, "GalaxyClient.exe") : null;

    public IReadOnlyList<string> PackageFullNames()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> FixedDrives() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)];

    public ActionResult Launch(string exe, string arguments, string folder)
    {
        if (!IsElevated())
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, arguments) { WorkingDirectory = folder, UseShellExecute = false })?.Dispose();
                return new ActionResult(true, "");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new ActionResult(false, ex.Message);
            }
        }
        return AsShellUser(exe, arguments, folder);
    }

    public ActionResult Open(string folder)
    {
        try
        {
            // Through Explorer, so the window opens as the person even from an administrator agent.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T("Cartella aperta.", "Folder opened."));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    public bool Recycle(string path)
    {
        if (!File.Exists(path)) return true;
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref operation) == 0 && !operation.fAnyOperationsAborted && !File.Exists(path);
    }

    // ── Starting as the person (as the Scheduler and Updates do) ────────────

    private static ActionResult AsShellUser(string exe, string args, string folder)
    {
        GetWindowThreadProcessId(GetShellWindow(), out var shellPid);
        if (shellPid == 0) return new ActionResult(false, L.T("il desktop di Windows non è pronto", "the Windows desktop is not ready"));
        var shell = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
        if (shell == 0) return new ActionResult(false, L.T("non riesco a leggere il desktop di Windows", "cannot read the Windows desktop"));
        nint token = 0, primary = 0;
        try
        {
            if (!OpenProcessToken(shell, TOKEN_DUPLICATE, out token)
                || !DuplicateTokenEx(token, MAXIMUM_ALLOWED, 0, SecurityImpersonation, TokenPrimary, out primary))
                return new ActionResult(false, L.T($"non riesco a usare il tuo utente (errore {Marshal.GetLastWin32Error()})", $"cannot use your user (error {Marshal.GetLastWin32Error()})"));
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var command = new StringBuilder($"\"{exe}\" {args}".TrimEnd());
            if (!CreateProcessWithTokenW(primary, 0, exe, command, CREATE_UNICODE_ENVIRONMENT, 0, folder.Length > 0 ? folder : null, ref si, out var pi))
                return new ActionResult(false, L.T($"non si avvia (errore {Marshal.GetLastWin32Error()})", $"does not start (error {Marshal.GetLastWin32Error()})"));
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return new ActionResult(true, "");
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

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint PROCESS_QUERY_INFORMATION = 0x400, TOKEN_DUPLICATE = 0x2, MAXIMUM_ALLOWED = 0x02000000, CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? application, StringBuilder commandLine, uint creationFlags,
        nint environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
}
