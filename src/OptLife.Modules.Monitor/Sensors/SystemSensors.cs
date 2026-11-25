using System.Runtime.InteropServices;
using OptLife.Core;

namespace OptLife.Modules.Monitor.Sensors;

/// <summary>A source of measurements. Returning nothing means "not available on this PC", never an error.</summary>
public interface ISensor
{
    ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct);
}

public static class Groups
{
    public static string Memory => L.T("Memoria", "Memory");
    public static string Cpu => L.T("Processore", "Processor");
    public static string Gpu => L.T("Scheda video", "Graphics card");
    public static string Network => L.T("Rete", "Network");
    public static string Disks => L.T("Dischi", "Disks");
}

/// <summary>RAM in use and committed memory (RAM + paging file), straight from Windows.</summary>
public sealed class MemorySensor : ISensor
{
    public ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return new([]);

        const double gb = 1024d * 1024 * 1024;
        var used = status.TotalPhys - status.AvailPhys;
        var commitUsed = status.TotalPageFile - status.AvailPageFile;
        return new(
        [
            new LiveValue("ram.used_pct", L.T("RAM occupata", "RAM used"), Groups.Memory, 100d * used / status.TotalPhys, "%"),
            new LiveValue("ram.used_gb", L.T("RAM in uso", "RAM in use"), Groups.Memory, used / gb, "GB"),
            new LiveValue("ram.commit_pct", L.T("Memoria impegnata", "Committed memory"), Groups.Memory, 100d * commitUsed / status.TotalPageFile, "%"),
        ]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>Overall processor load, from the time Windows spent idle between two readings.</summary>
public sealed class CpuLoadSensor : ISensor
{
    private ulong _idle, _total;
    private bool _primed;

    public ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return new([]);
        var total = kernel + user; // kernel time already includes idle time
        var (dIdle, dTotal) = (idle - _idle, total - _total);
        (_idle, _total) = (idle, total);
        if (!_primed || dTotal == 0)
        {
            _primed = true;
            return new([]);
        }
        return new([new LiveValue("cpu.load", L.T("Carico", "Load"), Groups.Cpu, Math.Clamp(100d * (dTotal - dIdle) / dTotal, 0, 100), "%")]);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

/// <summary>Free space on every fixed drive. Read once in a while: it changes slowly.</summary>
public sealed class DiskSensor : ISensor
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private IReadOnlyList<LiveValue> _last = [];
    private long _readAt = long.MinValue;

    public ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (_readAt != long.MinValue && now - _readAt < Every.TotalMilliseconds) return new(_last);
        _readAt = now;

        var values = new List<LiveValue>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // Tiny fixed volumes (recovery, firmware) are not disks anyone fills up.
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize < 1024L * 1024 * 1024) continue;
                var letter = drive.Name[..1];
                values.Add(new LiveValue($"disk.{letter}.free_pct", $"{letter}: {L.T("spazio libero", "free space")}", Groups.Disks, 100d * drive.AvailableFreeSpace / drive.TotalSize, "%"));
                values.Add(new LiveValue($"disk.{letter}.free_gb", $"{letter}: {L.T("liberi", "free")}", Groups.Disks, drive.AvailableFreeSpace / (1024d * 1024 * 1024), "GB"));
            }
            catch (IOException)
            {
                // A drive that disappears mid-read (USB, sleeping disk) is simply skipped this round.
            }
        }
        return new(_last = values);
    }
}
