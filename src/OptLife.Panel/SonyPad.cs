using System.Runtime.InteropServices;
using OptLife.Panel.ViewModels;

namespace OptLife.Panel;

/// <summary>
/// PlayStation controllers (DualShock 4, DualSense) read directly through Windows.Gaming.Input's RawGameController, so the
/// full-screen library works without Steam Input or DS4Windows. Part of Windows: no driver, no administrator.
/// Called through the WinRT interfaces by hand (no Windows SDK projection, which would add 25 MB to the program).
/// Limits: Windows gives the readings only while one of the program's windows is in front; right after start the list of
/// controllers is empty for a moment (it fills by itself); USB and Bluetooth both work, as long as no other program
/// (DS4Windows with "hide controller") has hidden the controller from Windows.
/// </summary>
internal sealed class SonyPad : IDisposable
{
    private const ushort Sony = 0x054C;
    private IntPtr _statics;
    private bool _failed;

    /// <summary>The first Sony controller's state, turned into the XInput shape the mapper reads; null when there is none.</summary>
    public (ushort Buttons, short X, short Y)? Read()
    {
        if (_failed) return null;
        try
        {
            if (_statics == IntPtr.Zero && !Open()) return null;
            if (Call<GetPtr>(_statics, 10)(_statics, out var vector) < 0 || vector == IntPtr.Zero) return null;
            try
            {
                Call<GetUInt>(vector, 7)(vector, out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (Call<GetAt>(vector, 6)(vector, i, out var pad) < 0 || pad == IntPtr.Zero) continue;
                    try
                    {
                        Call<GetUShort>(pad, 10)(pad, out var vendor);
                        if (vendor != Sony) continue;
                        Call<GetInt>(pad, 7)(pad, out var buttons);
                        Call<GetInt>(pad, 11)(pad, out var switches);
                        Call<GetInt>(pad, 6)(pad, out var axes);
                        var b = new byte[Math.Max(buttons, 1)];
                        var s = new int[Math.Max(switches, 1)];
                        var a = new double[Math.Max(axes, 2)];
                        if (Call<GetReading>(pad, 13)(pad, (uint)buttons, b, (uint)switches, s, (uint)axes, a, out _) < 0) continue;
                        return GamepadMapper.FromSony(b, switches > 0 ? s[0] : 0, axes >= 2 ? a[0] : 0.5, axes >= 2 ? a[1] : 0.5);
                    }
                    finally { Marshal.Release(pad); }
                }
                return null;
            }
            finally { Marshal.Release(vector); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or MarshalDirectiveException or AccessViolationException)
        {
            _failed = true;
            return null;
        }
    }

    private bool Open()
    {
        const string name = "Windows.Gaming.Input.RawGameController";
        if (WindowsCreateString(name, name.Length, out var hstring) < 0) { _failed = true; return false; }
        try
        {
            var iid = new Guid("eb8d0792-e95a-4b19-afc7-0a59f8bf759e"); // IRawGameControllerStatics
            if (RoGetActivationFactory(hstring, ref iid, out _statics) < 0 || _statics == IntPtr.Zero) { _failed = true; return false; }
            return true;
        }
        finally { WindowsDeleteString(hstring); }
    }

    public void Dispose()
    {
        if (_statics != IntPtr.Zero) Marshal.Release(_statics);
        _statics = IntPtr.Zero;
    }

    /// <summary>The method at <paramref name="slot"/> of a COM interface's table (0-5 are IUnknown and IInspectable).</summary>
    private static T Call<T>(IntPtr self, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPtr(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUShort(IntPtr self, out ushort value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAt(IntPtr self, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetReading(IntPtr self, uint buttonCount, [Out] byte[] buttons, uint switchCount, [Out] int[] switches,
        uint axisCount, [Out] double[] axes, out ulong timestamp);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
}
