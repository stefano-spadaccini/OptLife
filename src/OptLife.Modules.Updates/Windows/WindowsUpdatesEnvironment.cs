using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace OptLife.Modules.Updates.Windows;

/// <summary>
/// The real machine. winget lists and updates programs (an update runs as the person, with the token of the
/// Windows shell, even when the agent is administrator: the same idea as the Scheduler's, copied, not shared);
/// Windows Update is only searched, through its COM interface; drivers and BIOS come from the registry.
/// </summary>
internal sealed class WindowsUpdatesEnvironment : IUpdatesEnvironment
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(5);

    private static string Winget()
    {
        // The App Installer alias: on the PATH of a normal session, but spelled out for a process started with another token.
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(alias) ? alias : "winget.exe";
    }

    // ── winget ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AppUpdate>?> WingetUpgrades(CancellationToken ct)
    {
        var start = new ProcessStartInfo(Winget(), "upgrade --include-unknown --source winget --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ListTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return UpdatesRules.ParseWinget(await output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
        }
        catch (Win32Exception)
        {
            return null; // No winget.
        }
    }

    /// <summary>
    /// One update. Installed for the whole PC (or unknown): run here, as administrator when the agent is (no UAC prompt).
    /// Installed for the person: run as the person, with the token of Explorer. After <paramref name="limit"/> winget and the
    /// installer it started are stopped (an installer waiting on a window would otherwise hold the whole run).
    /// </summary>
    public async Task<UpgradeAttempt> WingetUpgrade(string id, AppScope scope, TimeSpan limit, CancellationToken ct)
    {
        if (id.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>'))
            return new UpgradeAttempt(null, false, L.T("id non valido", "invalid id"));
        // No --scope: forcing one makes winget miss packages whose installer does not declare it ("nothing to update" in 3 s,
        // 23 Nov 2026). The scope only decides who runs it: the agent itself, or the person.
        var scopeArg = "";
        var args = $"upgrade --id {id} --exact --silent --source winget{scopeArg} --accept-package-agreements --accept-source-agreements --disable-interactivity";
        var (wait, error) = IsElevated() && scope == AppScope.User ? AsShellUser(Winget(), args) : Plain(Winget(), args);
        if (wait is null) return new UpgradeAttempt(null, false, error);
        try
        {
            var code = await wait(limit, ct);
            return code is null ? new UpgradeAttempt(null, true, null) : new UpgradeAttempt(code, false, null);
        }
        catch (OperationCanceledException)
        {
            return new UpgradeAttempt(null, false, L.T("interrotto", "stopped"));
        }
    }

    public async Task<IReadOnlyList<string>?> PackageDependencies(string id, CancellationToken ct)
    {
        if (id.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>')) return null;
        var start = new ProcessStartInfo(Winget(), $"show --id {id} --exact --source winget --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 ? Dependencies.Parse(await output) : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public IReadOnlyList<string> InstalledNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                foreach (var sub in key?.GetSubKeyNames() ?? [])
                {
                    using var p = key!.OpenSubKey(sub);
                    if (p?.GetValue("DisplayName") is string n && n.Length > 0 && p.GetValue("SystemComponent") is not 1) names.Add(n);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return [.. names];
    }

    /// <summary>Whole PC or this person: from where the program is listed among the installed ones.</summary>
    public AppScope ScopeOf(AppUpdate app)
    {
        var name = app.Name.Length > 24 ? app.Name[..24] : app.Name;
        foreach (var (hive, path, scope) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", AppScope.User),
                     (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", AppScope.Machine),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", AppScope.Machine),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                foreach (var sub in key?.GetSubKeyNames() ?? [])
                {
                    using var p = key!.OpenSubKey(sub);
                    if (p?.GetValue("DisplayName") is string n && n.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return scope;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return AppScope.Unknown;
    }

    /// <summary>Minutes since the last key or mouse input in this session.</summary>
    public int IdleMinutes()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (int)((uint)Environment.TickCount - info.dwTime) / 60000 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private delegate Task<int?> Wait(TimeSpan limit, CancellationToken ct);

    private static (Wait?, string?) Plain(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return (null, L.T("winget non si avvia", "winget does not start"));
            return (async (limit, ct) =>
            {
                using (p)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(limit);
                    try
                    {
                        await p.WaitForExitAsync(timeout.Token);
                        return p.ExitCode;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                        return null;
                    }
                }
            }, null);
        }
        catch (Win32Exception)
        {
            return (null, L.T("winget non c'è: aggiorna \"Programma di installazione app\" dal Microsoft Store", "winget is missing: update \"App Installer\" from the Microsoft Store"));
        }
    }

    /// <summary>Started with the token of Explorer: the person's own rights, not the administrator's.</summary>
    private static (Wait?, string?) AsShellUser(string exe, string args)
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
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = STARTF_USESHOWWINDOW, wShowWindow = SW_HIDE };
            var command = new StringBuilder($"\"{exe}\" {args}");
            var app = Path.IsPathRooted(exe) ? exe : null;
            if (!CreateProcessWithTokenW(primary, 0, app, command, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, 0, null, ref si, out var pi))
                return (null, L.T($"winget non si avvia (errore {Marshal.GetLastWin32Error()})", $"winget does not start (error {Marshal.GetLastWin32Error()})"));
            CloseHandle(pi.hThread);
            var handle = pi.hProcess;
            var pid = (int)pi.dwProcessId;
            return (async (limit, ct) =>
            {
                try
                {
                    var until = DateTime.UtcNow + limit;
                    while (WaitForSingleObject(handle, 0) == WAIT_TIMEOUT)
                    {
                        if (DateTime.UtcNow > until)
                        {
                            // winget and the installer it started: an installer waiting on a window would hold the run for ever.
                            try { using var stuck = Process.GetProcessById(pid); stuck.Kill(entireProcessTree: true); }
                            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                            return null;
                        }
                        await Task.Delay(500, ct);
                    }
                    return GetExitCodeProcess(handle, out var code) ? (int)code : -1;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }, null);
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

    // ── Windows Update ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WindowsUpdateItem>?> WindowsUpdates(CancellationToken ct)
    {
        // The search cannot be stopped once started: on a timeout it is left to finish on its own thread.
        var search = Task.Run(Search, CancellationToken.None);
        var finished = await Task.WhenAny(search, Task.Delay(SearchTimeout, ct));
        return finished == search ? await search : null;
    }

    private static IReadOnlyList<WindowsUpdateItem>? Search()
    {
        try
        {
            if (Type.GetTypeFromProgID("Microsoft.Update.Session") is not { } type || Activator.CreateInstance(type) is not { } session) return null;
            dynamic searcher = ((dynamic)session).CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            var list = new List<WindowsUpdateItem>();
            int count = result.Updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = result.Updates.Item(i);
                string title = u.Title;
                // Drivers Windows does not install by itself are listed under "Optional updates", whatever BrowseOnly says.
                var driver = (int)u.Type == 2;
                list.Add(new WindowsUpdateItem(title, driver, driver || (bool)u.BrowseOnly));
            }
            return list;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    public bool PreviewUpdatesOn()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            return key?.GetValue("IsContinuousInnovationOptedIn") is int v && v == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // ── Drivers and BIOS ─────────────────────────────────────────────────────

    public GraphicsDriver? Graphics()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cls = root.OpenSubKey(DisplayClass);
            var cards = new List<GraphicsDriver>();
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
            {
                using var card = cls!.OpenSubKey(sub);
                if (card?.GetValue("DriverVersion") is not string version || card.GetValue("ProviderName") is not string provider) continue;
                if (provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue; // The basic display driver, or a remote screen.
                var date = card.GetValue("DriverDate") is string d && DateTimeOffset.TryParseExact(d, ["M-d-yyyy", "MM-dd-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : (DateTimeOffset?)null;
                cards.Add(new GraphicsDriver(provider, card.GetValue("DriverDesc") as string ?? provider, version, date));
            }
            // A dedicated card first: it is the one games use (an AMD processor's graphics come before an NVIDIA card in the list).
            return cards.FirstOrDefault(c => c.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ?? cards.FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public InstalledProgram? Program(string namePart)
    {
        foreach (var (hive, path) in new[]
                 {
                     (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                foreach (var sub in key?.GetSubKeyNames() ?? [])
                {
                    using var p = key!.OpenSubKey(sub);
                    if (p?.GetValue("DisplayName") is not string name || !name.Contains(namePart, StringComparison.OrdinalIgnoreCase)) continue;
                    // Some installers (AMD's chipset one) write no InstallDate: the last time their key was written is the next best thing.
                    var date = p.GetValue("InstallDate") is string d && DateTimeOffset.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt
                        : RegQueryInfoKey(p.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var written) == 0 && written > 0 ? DateTimeOffset.FromFileTime(written) : (DateTimeOffset?)null;
                    return new InstalledProgram(name, p.GetValue("DisplayVersion") as string ?? "?", date);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    public BiosInfo? Bios()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key?.GetValue("BIOSVersion") is not string version) return null;
            var date = key.GetValue("BIOSReleaseDate") is string d && DateTimeOffset.TryParseExact(d, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : (DateTimeOffset?)null;
            using var cpu = root.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return new BiosInfo(key.GetValue("BaseBoardManufacturer") as string ?? "", key.GetValue("BaseBoardProduct") as string ?? "", version, date,
                (cpu?.GetValue("ProcessorNameString") as string)?.Trim());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public ActionResult Open(string target)
    {
        try
        {
            // Through Explorer, so the browser opens as the person even when OptLife runs as administrator.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T($"Aperto: {target}", $"Opened: {target}"));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint PROCESS_QUERY_INFORMATION = 0x400, WAIT_TIMEOUT = 0x102, TOKEN_DUPLICATE = 0x2, MAXIMUM_ALLOWED = 0x02000000,
        CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000, STARTF_USESHOWWINDOW = 0x1;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const short SW_HIDE = 0;

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

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? application, StringBuilder commandLine, uint creationFlags,
        nint environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, nint className, nint classLength, nint reserved, nint subKeys, nint maxSubKeyLength,
        nint maxClassLength, nint values, nint maxValueNameLength, nint maxValueLength, nint securityDescriptor, out long lastWriteTime);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
