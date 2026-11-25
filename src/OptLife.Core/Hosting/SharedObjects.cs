using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OptLife.Core.Hosting;

/// <summary>
/// Named objects shared by agent, panel and command line (the live board, the "check now" and "stop"
/// signals, the single-instance locks), created so that all of them can open them whatever their rights.
///
/// The agent runs as administrator. Windows gives what an administrator process creates a "high" label
/// and access for administrators only, so a panel or a command line started normally could not even see
/// that the agent is running. Created here first, the object carries an explicit access list (the person
/// and the system) and a "medium" label; the managed wrappers then open that same object by name.
/// </summary>
public static class SharedObjects
{
    // The native handles are kept for the life of the process: they are what keeps each object alive.
    private static readonly List<nint> Kept = [];

    /// <summary>Creates the mutex if it does not exist yet; true when this call created it.</summary>
    public static bool Mutex(string name, bool initiallyOwned = false) =>
        Create(name, sa => CreateMutexW(sa, initiallyOwned, name));

    public static bool Event(string name, bool manualReset) =>
        Create(name, sa => CreateEventW(sa, manualReset, false, name));

    public static bool Mapping(string name, long capacity) =>
        Create(name, sa => CreateFileMappingW(-1, sa, PAGE_READWRITE, (uint)(capacity >> 32), (uint)capacity, name));

    private static bool Create(string name, Func<nint, nint> create)
    {
        var sa = Attributes(out var descriptor);
        try
        {
            var handle = create(sa);
            var error = Marshal.GetLastWin32Error();
            if (handle == 0)
            {
                // Someone else made it first with rights this process does not have: let the managed
                // wrapper try to open it and report what is wrong.
                if (error == ERROR_ACCESS_DENIED) return false;
                throw new Win32Exception(error, L.T($"Non riesco a creare {name}.", $"Cannot create {name}."));
            }
            lock (Kept) Kept.Add(handle);
            return error != ERROR_ALREADY_EXISTS;
        }
        finally
        {
            Marshal.FreeHGlobal(sa);
            LocalFree(descriptor);
        }
    }

    /// <summary>Full access for the system, administrators and the person; "medium" label, so a normal process can use it.</summary>
    private static nint Attributes(out nint descriptor)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{identity.User!.Value})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptor, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Descrittore di sicurezza non valido.", "Invalid security descriptor."));
        var sa = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor }, sa, false);
        return sa;
    }

    /// <summary>True when this process runs with administrator rights.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private const int ERROR_ALREADY_EXISTS = 183, ERROR_ACCESS_DENIED = 5;
    private const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint size);

    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateMutexW(nint sa, bool initialOwner, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateEventW(nint sa, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFileMappingW(nint file, nint sa, uint protect, uint sizeHigh, uint sizeLow, string name);
}
