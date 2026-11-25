using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OptLife.Modules.Games.Windows;

/// <summary>The real machine: running processes, the keyboard, the system sound.</summary>
public sealed class WindowsGameEnvironment : IGameEnvironment
{
    private const int FirstFunctionKey = 0x70; // F1

    public GameProcess? ForegroundGame(IReadOnlyList<string> folders, IReadOnlyList<string> names, IReadOnlyList<string> ignored)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (ignored.Any(i => name.Contains(i, StringComparison.OrdinalIgnoreCase))) return null;
            var path = PathOf(process.Id);
            var isGame = names.Contains(name, StringComparer.OrdinalIgnoreCase)
                || (path is not null && folders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)));
            return isGame ? new GameProcess(process.Id, name, path ?? "") : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // It closed in the meantime, or is protected.
        }
    }

    public GameProcess? FindGame(IReadOnlyList<string> folders, IReadOnlyList<string> names, IReadOnlyList<string> ignored)
    {
        // The game in front of the person is the one being played, whatever else is open behind it.
        if (ForegroundGame(folders, names, ignored) is { } front) return front;

        var mySession = Process.GetCurrentProcess().SessionId;
        (GameProcess Game, TimeSpan Cpu)? best = null;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != mySession) continue;
                    var name = process.ProcessName;
                    if (ignored.Any(i => name.Contains(i, StringComparison.OrdinalIgnoreCase))) continue;

                    var path = PathOf(process.Id);
                    var isGame = names.Contains(name, StringComparer.OrdinalIgnoreCase)
                        || (path is not null && folders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)));
                    // Checked last: asking for the main window is the expensive part.
                    if (!isGame || process.MainWindowHandle == IntPtr.Zero) continue;

                    var cpu = CpuTime(process);
                    if (best is null || cpu > best.Value.Cpu)
                        best = (new GameProcess(process.Id, name, path ?? ""), cpu);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process ended while we were looking at it, or is protected: not a candidate.
                }
            }
        }
        return best?.Game;
    }

    public bool IsRunning(GameProcess game)
    {
        try
        {
            using var process = Process.GetProcessById(game.Pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // It exists but will not tell us more (higher privileges): still running.
        }
    }

    public ISessionSource OpenSource(GameProcess game, ServerHint server) => new WindowsSessionSource(game, server);

    // Only the "currently down" bit: reading the key state does not take the key away from the game.
    public bool IsFunctionKeyDown(int number) => (GetAsyncKeyState(FirstFunctionKey + number - 1) & 0x8000) != 0;

    public void Beep() => MessageBeep(0x40);

    public Task Delay(TimeSpan time, CancellationToken ct) => Task.Delay(time, ct);

    private static TimeSpan CpuTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>Full path of a process, readable also for programs running with higher privileges.</summary>
    private static string? PathOf(int pid)
    {
        const uint queryLimitedInformation = 0x1000;
        var handle = OpenProcess(queryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
