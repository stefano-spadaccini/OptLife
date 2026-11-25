using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using OptLife.Core;

namespace OptLife.Modules.Monitor.Sensors;

/// <summary>
/// NVIDIA graphics card, read through the library the driver itself installs (nvml.dll).
/// No administrator rights and no extra program needed. Absent on PCs without an NVIDIA card.
/// </summary>
public sealed class NvidiaSensor : ISensor
{
    private IntPtr _device;
    private bool _unavailable;
    private DateTime _retryAt;
    private DateTime _installerSeenAt;

    // How long to stay away from the driver after its installer is gone: the new driver settles first.
    private static readonly TimeSpan AfterInstall = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(1);

    public ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct)
    {
        // While the NVIDIA installer replaces the driver, the library behind the open handle disappears: a call
        // through it then kills the whole process, beyond any catch (it took the agent down on 20/11/2026 at
        // 15:44, installing driver 621.24). So the handle is let go as soon as an installer is seen, and taken
        // again only a minute after it is gone.
        var now = DateTime.UtcNow;
        if (NvidiaInstaller.Running())
        {
            _installerSeenAt = now;
            Release();
            return new([]);
        }
        if (now - _installerSeenAt < AfterInstall) return new([]);

        // A driver that is missing now may be there later (a fresh install, a reboot pending): look again from time to time.
        if (_unavailable && now < _retryAt) return new([]);
        try
        {
            if (_device == IntPtr.Zero && (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0))
            {
                _device = IntPtr.Zero;
                (_unavailable, _retryAt) = (true, now + RetryEvery);
                return new([]);
            }
            _unavailable = false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            (_unavailable, _retryAt) = (true, now + RetryEvery);
            return new([]);
        }

        var values = new List<LiveValue>();
        if (nvmlDeviceGetTemperature(_device, 0, out var temp) == 0)
            values.Add(new LiveValue("gpu.temp", L.T("Temperatura", "Temperature"), Groups.Gpu, temp, "°C"));
        if (nvmlDeviceGetUtilizationRates(_device, out var use) == 0)
            values.Add(new LiveValue("gpu.load", L.T("Carico", "Load"), Groups.Gpu, use.Gpu, "%"));
        if (nvmlDeviceGetPowerUsage(_device, out var milliwatts) == 0)
            values.Add(new LiveValue("gpu.power", L.T("Consumo", "Power"), Groups.Gpu, milliwatts / 1000d, "W"));
        if (nvmlDeviceGetClockInfo(_device, 0, out var clock) == 0)
            values.Add(new LiveValue("gpu.clock", L.T("Frequenza", "Clock"), Groups.Gpu, clock, "MHz"));
        if (nvmlDeviceGetMemoryInfo(_device, out var memory) == 0 && memory.Total > 0)
            values.Add(new LiveValue("gpu.vram_pct", L.T("Memoria video", "Video memory"), Groups.Gpu, 100d * memory.Used / memory.Total, "%"));
        if (nvmlDeviceGetFanSpeed(_device, out var fan) == 0)
            values.Add(new LiveValue("gpu.fan", L.T("Ventole", "Fans"), Groups.Gpu, fan, "%"));
        // Nothing answered: the handle has gone stale (a driver reset). Let it go and take a new one next time.
        if (values.Count == 0) Release();
        return new(values);
    }

    private void Release()
    {
        if (_device == IntPtr.Zero) return;
        _device = IntPtr.Zero;
        try
        {
            nvmlShutdown();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

#pragma warning disable IDE1006 // Names mirror the native library.
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clock, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
#pragma warning restore IDE1006
}

/// <summary>Whether an NVIDIA installer is at work right now: its processes live under NVIDIA's own folders.</summary>
internal static class NvidiaInstaller
{
    private static DateTime _checkedAt;
    private static bool _running;

    public static bool Running()
    {
        // Listing processes every two seconds is cheap; reading their paths is not, so once every 5 s is enough.
        if (DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(5)) return _running;
        _checkedAt = DateTime.UtcNow;
        _running = false;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("setup"))
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    if (path.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) _running = true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Cannot read its path: not ours to judge.
                }
            }
        }
        return _running;
    }
}

/// <summary>
/// What MSI Afterburner is already measuring, read from the memory it shares with other programs.
/// This is where processor temperature, frequency and power come from without OptLife installing
/// any driver. When Afterburner is not running these values are simply missing.
/// </summary>
public sealed class AfterburnerSensor : ISensor
{
    private const uint Signature = 0x4D41484D; // "MAHM"
    private const int NameLength = 260;
    private const int DataOffset = NameLength * 5;

    // Afterburner's own names → ours. The module keeps the first value per key, so the graphics entries
    // only fill in when the NVIDIA sensor gave nothing.
    private static (string Source, string Key, string Label, string Group, string Unit)[] Map =>
    [
        ("CPU temperature", "cpu.temp", L.T("Temperatura", "Temperature"), Groups.Cpu, "°C"),
        ("CPU clock", "cpu.clock", L.T("Frequenza", "Clock"), Groups.Cpu, "MHz"),
        ("CPU power", "cpu.power", L.T("Consumo", "Power"), Groups.Cpu, "W"),
        ("GPU temperature", "gpu.temp", L.T("Temperatura", "Temperature"), Groups.Gpu, "°C"),
        ("GPU usage", "gpu.load", L.T("Carico", "Load"), Groups.Gpu, "%"),
        ("Power", "gpu.power", L.T("Consumo", "Power"), Groups.Gpu, "W"),
        ("Core clock", "gpu.clock", L.T("Frequenza", "Clock"), Groups.Gpu, "MHz"),
        ("Fan speed", "gpu.fan", L.T("Ventole", "Fans"), Groups.Gpu, "%"),
    ];

    public ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct) => new(Read());

    public static IReadOnlyList<LiveValue> Read(string mapName = "MAHMSharedMemory")
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return Parse(view);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<LiveValue> Parse(UnmanagedMemoryAccessor view)
    {
        if (view.Capacity < 32 || view.ReadUInt32(0) != Signature) return [];
        var headerSize = view.ReadUInt32(8);
        var count = view.ReadUInt32(12);
        var entrySize = view.ReadUInt32(16);
        if (entrySize < DataOffset + sizeof(float) || headerSize + (long)count * entrySize > view.Capacity) return [];

        var values = new List<LiveValue>();
        var name = new byte[NameLength];
        for (var i = 0; i < count; i++)
        {
            var offset = headerSize + (long)i * entrySize;
            view.ReadArray(offset, name, 0, NameLength);
            var end = Array.IndexOf(name, (byte)0);
            var source = Encoding.ASCII.GetString(name, 0, end < 0 ? NameLength : end);
            var value = view.ReadSingle(offset + DataOffset);
            // Afterburner reports "no reading" as the largest float.
            if (!float.IsFinite(value) || value >= float.MaxValue / 2) continue;

            foreach (var m in Map)
                if (m.Source == source)
                    values.Add(new LiveValue(m.Key, m.Label, m.Group, value, m.Unit));
        }
        return values;
    }
}
