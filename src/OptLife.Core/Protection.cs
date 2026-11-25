using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OptLife.Core;

/// <summary>
/// What keeps the administrator agent from being used by someone who is not an administrator. In an installed copy (under
/// Program Files) the program and its data folder can be changed only by administrators: the settings that make the agent run
/// something (scheduled jobs, tools) are then decisions of an administrator. The agent also refuses to run as administrator a
/// file that a user could have changed. A portable copy (a folder of one's own) trusts the person who owns that folder: its
/// program files are theirs to change anyway.
/// </summary>
public static class Protection
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    // NT SERVICE\TrustedInstaller: the owner of Windows' own files.
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Installed (by the installer, under Program Files): the program and its data are protected from non-administrators.</summary>
    public static bool InstalledMode => Hosting.OptLifeRuntime.InProgramFiles(AppContext.BaseDirectory);

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Only the system and administrators may change the data folder; users may read it (the panel and the command line show it).
    /// Rules of its own, not inherited from %ProgramData% (where users may create files), and the same for everything inside.
    /// </summary>
    public static void Harden(string folder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(Admins);
        new DirectoryInfo(folder).SetAccessControl(security);
        // What is inside takes the folder's rules (files made earlier may carry rules of their own).
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try
            {
                if (entry is DirectoryInfo d)
                {
                    var s = new DirectorySecurity();
                    s.SetAccessRuleProtection(false, false);
                    s.SetOwner(Admins);
                    d.SetAccessControl(s);
                }
                else if (entry is FileInfo f)
                {
                    var s = new FileSecurity();
                    s.SetAccessRuleProtection(false, false);
                    s.SetOwner(Admins);
                    f.SetAccessControl(s);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>True when the data folder is as <see cref="Harden"/> leaves it: no one but the system and administrators may change it.</summary>
    public static bool IsHardened(string folder) => WritableByOthers(folder, checkParent: false) is null;

    private const FileSystemRights Changes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool Trusted(IdentityReference who) =>
        who is SecurityIdentifier sid && (sid == System || sid == Admins || sid == TrustedInstaller);

    /// <summary>
    /// Who other than the system and administrators could change this file (or put another in its place): its owner, a rule that
    /// lets someone write or delete it, or a folder that lets someone delete what is inside. Null when no one can.
    /// </summary>
    public static string? WritableByOthers(string path, bool checkParent = true)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) is { } owner && !Trusted(owner))
                return L.T($"il proprietario è {Name(owner)}", $"its owner is {Name(owner)}");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Changes) != 0 && !Trusted(rule.IdentityReference)
                    && rule.IdentityReference.Value != "S-1-3-0") // CREATOR OWNER: only the owner, checked above.
                    return L.T($"{Name(rule.IdentityReference)} può modificarlo", $"{Name(rule.IdentityReference)} can change it");
            if (checkParent && Path.GetDirectoryName(Path.GetFullPath(path)) is { } parent && Directory.Exists(parent))
            {
                foreach (FileSystemAccessRule rule in new DirectoryInfo(parent).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0
                        && !Trusted(rule.IdentityReference) && rule.IdentityReference.Value != "S-1-3-0")
                        return L.T($"{Name(rule.IdentityReference)} può sostituirlo nella sua cartella", $"{Name(rule.IdentityReference)} can replace it in its folder");
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            return L.T("non riesco a leggerne i permessi", "cannot read its permissions");
        }
    }

    /// <summary>
    /// True when the path, or a folder on the way to it, is a link (symbolic link, junction, mount point). An administrator that
    /// writes or moves where a user may make such a link could be led to write into Windows' own folders: those writes are refused.
    /// </summary>
    public static bool ThroughLink(string path)
    {
        try
        {
            for (var p = Path.GetFullPath(path); Path.GetPathRoot(p) is { } root && p.Length > root.Length; p = Path.GetDirectoryName(p)!)
            {
                if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)
                    // OneDrive's files on demand are reparse points too, but not links.
                    && new FileInfo(p).LinkTarget is not null)
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static string Name(IdentityReference who)
    {
        try { return who.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return who.Value; }
    }

    /// <summary>
    /// Starts a program as the person signed in, not as administrator, even from an administrator process (through the token of
    /// the Windows desktop, as Explorer would start it). From a process that is not administrator, simply starts it.
    /// </summary>
    public static ActionResult StartAsUser(string exe, string arguments, string folder)
    {
        if (!IsElevated())
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, arguments) { WorkingDirectory = folder, UseShellExecute = false })?.Dispose();
                return new ActionResult(true, "");
            }
            catch (Exception ex) when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new ActionResult(false, ex.Message);
            }
        }
        GetWindowThreadProcessId(GetShellWindow(), out var shellPid);
        if (shellPid == 0) return new ActionResult(false, L.T("il desktop di Windows non è pronto", "the Windows desktop is not ready"));
        var shell = OpenProcess(0x400 /* PROCESS_QUERY_INFORMATION */, false, shellPid);
        if (shell == 0) return new ActionResult(false, L.T("non riesco a leggere il desktop di Windows", "cannot read the Windows desktop"));
        nint token = 0, primary = 0;
        try
        {
            if (!OpenProcessToken(shell, 0x2 /* TOKEN_DUPLICATE */, out token)
                || !DuplicateTokenEx(token, 0x02000000 /* MAXIMUM_ALLOWED */, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary))
                return new ActionResult(false, L.T($"non riesco a usare il tuo utente (errore {Marshal.GetLastWin32Error()})", $"cannot use your user (error {Marshal.GetLastWin32Error()})"));
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var command = new StringBuilder($"\"{exe}\" {arguments}".TrimEnd());
            if (!CreateProcessWithTokenW(primary, 0, exe, command, 0x400 /* CREATE_UNICODE_ENVIRONMENT */, 0, folder.Length > 0 ? folder : null, ref si, out var pi))
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
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? application, StringBuilder commandLine, uint creationFlags,
        nint environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
