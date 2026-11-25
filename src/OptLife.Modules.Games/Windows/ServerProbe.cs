using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace OptLife.Modules.Games.Windows;

/// <summary>Where the game's server is, as far as can be told without administrator rights.</summary>
/// <param name="Host">What is actually measured: the server itself, or a front door of the datacenter it lives in.</param>
/// <param name="Port">0 for an ICMP ping; otherwise the time to open a TCP connection, which is one round trip.</param>
/// <param name="Shown">How to name it to a person ("Amazon eu-central-1", "18.157.3.199").</param>
public sealed record ServerTarget(string Host, int Port, string Shown);

/// <summary>
/// Measures the third stretch of the connection: from the internet to the game's server. Windows does
/// not tell which server a game talks to over UDP, so the address comes from the game's own log
/// (Unreal games write "Browse: ip:port" when they join a match) or from what the person wrote in the
/// settings. Servers usually ignore pings; when the address belongs to a cloud datacenter, the time
/// to open a connection to that datacenter's public front door is measured instead: same building,
/// same route.
/// </summary>
internal sealed class ServerProbe : IDisposable
{
    private static readonly Regex Browse = new(@"Browse:\s*(\d{1,3}(?:\.\d{1,3}){3}):(\d+)", RegexOptions.Compiled);
    private static readonly Regex AwsRegion = new(@"\.([a-z]{2}-[a-z]+-\d)\.compute\.amazonaws\.com$", RegexOptions.Compiled);
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(10);
    private const int TimeoutMs = 1000;

    private readonly string? _log;
    private readonly ServerTarget? _fixed;
    private readonly Stopwatch _sinceLook = new();
    private string? _serverIp;
    private Task<double?>? _pending;

    public ServerTarget? Target { get; private set; }

    /// <param name="log">The game's log, to read the server it joined from; null when there is none.</param>
    /// <param name="fixedHost">"host" or "host:port" written by the person for this game; wins over the log.</param>
    public ServerProbe(string? log, string? fixedHost)
    {
        _log = log is { Length: > 0 } ? Environment.ExpandEnvironmentVariables(log) : null;
        if (fixedHost is { Length: > 0 })
        {
            var cut = fixedHost.LastIndexOf(':');
            _fixed = cut > 0 && int.TryParse(fixedHost[(cut + 1)..], out var port)
                ? new ServerTarget(fixedHost[..cut], port, fixedHost)
                : new ServerTarget(fixedHost, 0, fixedHost);
            Target = _fixed;
        }
    }

    /// <summary>
    /// Collects the answer to the previous measurement (milliseconds, or null when it was lost) and
    /// starts the next one. Called about once a second; never waits.
    /// </summary>
    public (bool Answered, double? Ms) Poll()
    {
        if (_fixed is null && (!_sinceLook.IsRunning || _sinceLook.Elapsed > LookEvery))
        {
            _sinceLook.Restart();
            LookForServer();
        }

        (bool, double?) result = (false, null);
        if (_pending is { IsCompleted: true } done)
        {
            result = (true, done.Status == TaskStatus.RanToCompletion ? done.Result : null);
            _pending = null;
        }
        if (_pending is null && Target is { } target) _pending = Measure(target);
        return result;
    }

    /// <summary>Reads the end of the game's log for the last server it joined; a new match means a new server.</summary>
    private void LookForServer()
    {
        if (_log is null || !File.Exists(_log)) return;
        try
        {
            using var stream = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 256 * 1024), SeekOrigin.Begin);
            var tail = new StreamReader(stream).ReadToEnd();
            var last = Browse.Matches(tail).LastOrDefault();
            if (last is null) return;
            var ip = last.Groups[1].Value;
            // Joining the main menu or a local game is not a server worth measuring.
            if (ip == _serverIp || ip.StartsWith("127.") || ip.StartsWith("0.")) return;
            _serverIp = ip;
            Target = ToTarget(ip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The datacenter's front door when the address is a known cloud one, otherwise the address itself.</summary>
    internal static ServerTarget ToTarget(string ip, Func<string, string?>? reverse = null)
    {
        reverse ??= ReverseName;
        if (reverse(ip) is { } name && AwsRegion.Match(name) is { Success: true } aws)
        {
            var region = aws.Groups[1].Value;
            return new ServerTarget($"ec2.{region}.amazonaws.com", 443, $"Amazon {region}");
        }
        return new ServerTarget(ip, 0, ip);
    }

    private static string? ReverseName(string ip)
    {
        try
        {
            return Dns.GetHostEntry(ip).HostName;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<double?> Measure(ServerTarget target)
    {
        var watch = Stopwatch.StartNew();
        if (target.Port == 0)
        {
            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(target.Host, TimeoutMs);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch (PingException)
            {
                return null;
            }
        }

        // Opening a connection takes exactly one round trip; it is closed again at once, nothing is sent.
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeoutMs);
        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeout.Token);
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _pending = null;
}
