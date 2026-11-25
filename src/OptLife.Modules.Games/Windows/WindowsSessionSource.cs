using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OptLife.Modules.Games.Windows;

/// <summary>
/// What is measured four times a second while a game runs: pings to the router and to the internet,
/// Windows performance counters, the graphics card and the game's frames.
/// Every part is optional: whatever is missing on this PC is simply absent from the readings.
/// </summary>
public sealed class WindowsSessionSource : ISessionSource
{
    private readonly GameProcess _game;
    private readonly string? _gateway = FindGateway();
    private readonly List<(string Name, Ping Ping, Task<PingReply> Reply)> _pings = [];
    private const int CounterEvery = 4;
    private readonly Dictionary<string, PerformanceCounter> _counters = [];
    private readonly Dictionary<string, double> _slow = [];
    private int _readings;
    private readonly Processors _processors = new();
    private readonly PageFaults _faults;
    private readonly Gpu _gpu = new();

    private readonly ServerProbe _server;
    private int _serverTick;

    public string? Server => _server.Target?.Shown;

    public WindowsSessionSource(GameProcess game, ServerHint? server = null)
    {
        _game = game;
        _server = new ServerProbe(server?.Log, server?.Host);
        _faults = new PageFaults(game.Pid);
        // Windows performance counters are costly to read (each one re-reads its whole category),
        // so they are kept for the two things nothing cheaper gives. Processor, memory and the
        // game's own page faults come straight from the system instead.
        Open("pages_in", "Memory", "Pages Input/sec", null);
        Open("disk_ms", "PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");
        _processors.Read([]); // Rates need a starting point.
    }

    public IReadOnlyDictionary<string, double> Read()
    {
        var reading = new Dictionary<string, double>();
        ReadPings(reading);

        // The server is asked once a second (one connection opened and closed): enough to see a spike, too little to matter.
        if (_serverTick++ % CounterEvery == 0)
        {
            var (answered, ms) = _server.Poll();
            if (answered) reading[ms is { } t ? "ping_srv" : "ping_srv_lost"] = ms ?? 1;
        }

        // Both counters are averages over the time since the previous read, so asking once a second
        // misses nothing; in between, the last answer stands.
        if (_readings++ % CounterEvery == 0)
        {
            _slow.Clear();
            foreach (var (name, counter) in _counters)
            {
                try
                {
                    var value = counter.NextValue();
                    _slow[name] = name == "disk_ms" ? value * 1000 : value;
                }
                catch (Exception ex) when (IsCounterProblem(ex))
                {
                    // The counter stopped answering: that measurement is missing until it answers again.
                }
            }
        }
        foreach (var (name, value) in _slow) reading[name] = value;
        _processors.Read(reading);
        _faults.Read(reading);
        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memory)) reading["avail_mb"] = memory.ullAvailPhys / (1024d * 1024);

        _gpu.Read(reading);
        if (Rtss.Read(_game.Pid) is { } frames)
        {
            reading["fps"] = frames.Fps;
            reading["frame_ms"] = frames.FrameMs;
        }
        return reading;
    }

    /// <summary>Collects the answers that arrived since last time, then sends the next round.</summary>
    private void ReadPings(Dictionary<string, double> reading)
    {
        for (var i = _pings.Count - 1; i >= 0; i--)
        {
            var (name, ping, reply) = _pings[i];
            if (!reply.IsCompleted) continue;
            if (reply.Status == TaskStatus.RanToCompletion && reply.Result.Status == IPStatus.Success)
                reading[name] = Math.Max(reading.GetValueOrDefault(name), reply.Result.RoundtripTime);
            else
                reading[name + "_lost"] = 1;
            ping.Dispose();
            _pings.RemoveAt(i);
        }

        foreach (var (name, address) in new[] { ("ping_gw", _gateway), ("ping_ext", "1.1.1.1") })
        {
            if (address is null) continue;
            var ping = new Ping();
            try
            {
                _pings.Add((name, ping, ping.SendPingAsync(address, 1000)));
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException)
            {
                ping.Dispose();
                reading[name + "_lost"] = 1;
            }
        }
    }

    private void Open(string name, string category, string counter, string? instance)
    {
        try
        {
            var c = instance is null
                ? new PerformanceCounter(category, counter, readOnly: true)
                : new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue(); // The first reading of a rate counter is always zero: take it now.
            _counters[name] = c;
        }
        catch (Exception ex) when (IsCounterProblem(ex))
        {
            // Counter missing on this system: that measurement is skipped.
        }
    }

    private static bool IsCounterProblem(Exception ex) =>
        ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or FormatException;

    private static string? FindGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))
                ?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var (_, ping, _) in _pings) ping.Dispose();
        foreach (var counter in _counters.Values) counter.Dispose();
        _server.Dispose();
        _faults.Dispose();
    }

    /// <summary>
    /// Load of the whole processor and of its busiest core, and the share of time spent in
    /// deferred interrupt work (DPC), from the times Windows keeps for each logical processor.
    /// </summary>
    private sealed class Processors
    {
        private const int SystemProcessorPerformanceInformation = 8;
        private const int EntrySize = 48; // idle, kernel (idle included), user, dpc, interrupt: 8 bytes each; then a count.
        private readonly byte[] _buffer = new byte[EntrySize * Math.Max(Environment.ProcessorCount, 64)];
        private long[] _idle = [], _total = [], _dpc = [];

        public void Read(Dictionary<string, double> reading)
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, _buffer, _buffer.Length, out var length) != 0) return;
            var count = length / EntrySize;
            long[] idle = new long[count], total = new long[count], dpc = new long[count];
            for (var i = 0; i < count; i++)
            {
                var at = i * EntrySize;
                idle[i] = BitConverter.ToInt64(_buffer, at);
                total[i] = BitConverter.ToInt64(_buffer, at + 8) + BitConverter.ToInt64(_buffer, at + 16);
                dpc[i] = BitConverter.ToInt64(_buffer, at + 24);
            }

            if (_total.Length == count)
            {
                double allTotal = 0, allIdle = 0, allDpc = 0, busiest = 0;
                for (var i = 0; i < count; i++)
                {
                    double dTotal = total[i] - _total[i], dIdle = idle[i] - _idle[i];
                    if (dTotal <= 0) continue;
                    allTotal += dTotal;
                    allIdle += dIdle;
                    allDpc += dpc[i] - _dpc[i];
                    busiest = Math.Max(busiest, 100 * (1 - dIdle / dTotal));
                }
                if (allTotal > 0)
                {
                    reading["cpu_tot"] = Math.Clamp(100 * (1 - allIdle / allTotal), 0, 100);
                    reading["cpu_core_max"] = Math.Clamp(busiest, 0, 100);
                    reading["dpc"] = Math.Clamp(100 * allDpc / allTotal, 0, 100);
                }
            }
            (_idle, _total, _dpc) = (idle, total, dpc);
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, [Out] byte[] buffer, int length, out int returned);
    }

    /// <summary>How many times per second the game had to fetch memory that was not ready (page faults).</summary>
    private sealed class PageFaults : IDisposable
    {
        private readonly IntPtr _process;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private uint _count;
        private bool _started;

        public PageFaults(int pid) => _process = OpenProcess(0x1000 /* query limited information */, false, pid);

        public void Read(Dictionary<string, double> reading)
        {
            if (_process == IntPtr.Zero) return; // A game that will not be looked at: that measurement is skipped.
            var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
            if (!K32GetProcessMemoryInfo(_process, ref counters, counters.cb)) return;

            var seconds = _clock.Elapsed.TotalSeconds;
            // The count is a 32-bit number that wraps around: unsigned subtraction keeps the difference right.
            if (_started && seconds > 0) reading["game_faults"] = unchecked(counters.PageFaultCount - _count) / seconds;
            _count = counters.PageFaultCount;
            _started = true;
            _clock.Restart();
        }

        public void Dispose()
        {
            if (_process != IntPtr.Zero) CloseHandle(_process);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS counters, uint size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    /// <summary>NVIDIA card through the driver's own library: load, clock and whether something is holding it back.</summary>
    private sealed class Gpu
    {
        private IntPtr _device;
        private bool _unavailable;
        private bool _noThrottleInfo;

        public void Read(Dictionary<string, double> reading)
        {
            if (_unavailable) return;
            try
            {
                if (_device == IntPtr.Zero && (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0))
                {
                    _unavailable = true;
                    return;
                }
                if (nvmlDeviceGetUtilizationRates(_device, out var use) == 0) reading["gpu_util"] = use.Gpu;
                if (nvmlDeviceGetClockInfo(_device, 0, out var clock) == 0) reading["gpu_clock"] = clock;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _unavailable = true;
                return;
            }

            if (_noThrottleInfo) return;
            try
            {
                // Reasons 0 (none) and 1 (the card is idle) are normal; anything else is a real limit.
                if (nvmlDeviceGetCurrentClocksThrottleReasons(_device, out var reasons) == 0)
                    reading["gpu_throttled"] = reasons > 1 ? 1 : 0;
            }
            catch (EntryPointNotFoundException)
            {
                _noThrottleInfo = true; // Older or newer drivers name this differently: do without.
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint Gpu; public uint Memory; }

#pragma warning disable IDE1006 // Names mirror the native library.
        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clock, out uint mhz);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
#pragma warning restore IDE1006
    }
}

/// <summary>
/// Frame rate of a program as measured by RivaTuner Statistics Server (it ships with MSI Afterburner).
/// Read from the memory RTSS shares; nothing is injected into the game by OptLife.
/// A game RTSS is told to leave alone has no frame data, and that is reported as such.
/// </summary>
public static class Rtss
{
    private const uint Signature = 0x52545353; // "RTSS"

    public static (double Fps, double FrameMs)? Read(int pid, string mapName = "RTSSSharedMemoryV2")
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return Parse(view, pid, unchecked((uint)Environment.TickCount));
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static (double Fps, double FrameMs)? Parse(UnmanagedMemoryAccessor view, int pid, uint tickNow)
    {
        if (view.Capacity < 20 || view.ReadUInt32(0) != Signature) return null;
        var entrySize = view.ReadUInt32(8);
        var offset = view.ReadUInt32(12);
        var count = view.ReadUInt32(16);
        if (entrySize < 284 || offset + (long)count * entrySize > view.Capacity) return null;

        for (var i = 0; i < count; i++)
        {
            var at = offset + (long)i * entrySize;
            if (view.ReadUInt32(at) != (uint)pid) continue;

            var periodStart = view.ReadUInt32(at + 268);
            var periodEnd = view.ReadUInt32(at + 272);
            var frames = view.ReadUInt32(at + 276);
            var frameMicroseconds = view.ReadUInt32(at + 280);
            // An entry that stopped updating belongs to a program that is no longer drawing.
            if (periodEnd <= periodStart || tickNow - periodEnd > 2000) return null;
            return (1000d * frames / (periodEnd - periodStart), frameMicroseconds / 1000d);
        }
        return null;
    }
}
