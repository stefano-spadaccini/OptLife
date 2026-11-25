using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;

namespace OptLife.Core.Hosting;

public sealed record LiveEntry(string Module, string Key, string Label, string Group, double Value, string Unit, double? Stress = null,
    string? Missing = null, FindingFix? MissingFix = null)
{
    public bool IsMissing => Missing is not null;

    public static string FormatNumber(double value, string unit) => LiveValue.FormatNumber(value, unit);
}

/// <summary>
/// The values of this very moment, shared between processes through a small block of memory
/// (no disk writes). The agent writes; the panel, the overlay and the CLI read.
/// It exists only while the agent runs, and a module that stops publishing fades out by itself.
/// </summary>
public sealed class LiveBoard : IDisposable
{
    public const string DefaultName = @"Local\OptLife.Live";
    private const int Capacity = 256 * 1024;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Published> _modules = [];

    private sealed record Published(DateTimeOffset Time, IReadOnlyCollection<LiveValue> Values);

    public LiveBoard(string name = DefaultName)
    {
        // Created with explicit rights first, so a panel that is not administrator can read what an agent that is writes.
        SharedObjects.Mapping(name, Capacity);
        SharedObjects.Mutex(name + ".Lock");
        _map = MemoryMappedFile.CreateOrOpen(name, Capacity);
        _view = _map.CreateViewAccessor(); // Kept open: publishing happens every couple of seconds.
        _mutex = new Mutex(false, name + ".Lock");
    }

    public void Publish(string module, IReadOnlyCollection<LiveValue> values, DateTimeOffset now)
    {
        lock (_gate)
        {
            _modules[module] = new Published(now, values);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_modules);
            if (bytes.Length > Capacity - sizeof(int)) return; // Never expected; better stale than corrupt.
            if (!Enter(_mutex)) return;
            try
            {
                _view.Write(0, bytes.Length);
                _view.WriteArray(sizeof(int), bytes, 0, bytes.Length);
            }
            finally
            {
                _mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>The same content Read gives other processes, for the modules living in this one.</summary>
    public IReadOnlyList<LiveValue> Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            return [.. _modules.Values.Where(p => now - p.Time <= Fresh).SelectMany(p => p.Values)];
        }
    }

    /// <summary>What every module published recently; empty when no agent is running.</summary>
    public static IReadOnlyList<LiveEntry> Read(DateTimeOffset now, string name = DefaultName)
    {
        byte[] bytes;
        try
        {
            using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var mutex = Mutex.OpenExisting(name + ".Lock");
            if (!Enter(mutex)) return [];
            try
            {
                using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                var length = view.ReadInt32(0);
                if (length <= 0 || length > Capacity) return [];
                bytes = new byte[length];
                view.ReadArray(sizeof(int), bytes, 0, length);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return [];
        }

        try
        {
            var modules = JsonSerializer.Deserialize<Dictionary<string, Published>>(Encoding.UTF8.GetString(bytes)) ?? [];
            return [.. modules
                .Where(m => now - m.Value.Time <= Fresh)
                .SelectMany(m => m.Value.Values.Select(v => new LiveEntry(m.Key, v.Key, v.Label, v.Group, v.Value, v.Unit, v.Stress, v.Missing, v.MissingFix)))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Enter(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(250);
        }
        catch (AbandonedMutexException)
        {
            return true; // The previous holder died mid-write; the lock is ours and the next write repairs the content.
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
        _mutex.Dispose();
    }
}
