using System.Net.NetworkInformation;
using System.Net.Sockets;
using OptLife.Core;

namespace OptLife.Modules.Monitor.Sensors;

/// <summary>
/// One ping per reading to each configured host. Latency is the last answer received; packet loss
/// is the share of unanswered pings over the last minute. Pinging the router and an internet host
/// separately is what tells "trouble at home" from "trouble with the line".
/// </summary>
public sealed class NetworkSensor : ISensor
{
    public const string HostsSetting = "hosts";
    private const int Window = 30;
    private static readonly TimeSpan GatewayLifetime = TimeSpan.FromSeconds(60);

    private readonly Func<string, CancellationToken, Task<long?>> _ping;
    private readonly Func<string?> _findGateway;
    private readonly Dictionary<string, Queue<long?>> _results = [];
    private string? _gateway;
    private long _gatewayAt = long.MinValue;

    public NetworkSensor() : this(PingAsync, FindGateway) { }

    internal NetworkSensor(Func<string, CancellationToken, Task<long?>> ping, Func<string?> findGateway)
    {
        _ping = ping;
        _findGateway = findGateway;
    }

    public async ValueTask<IReadOnlyList<LiveValue>> ReadAsync(ISettings settings, CancellationToken ct)
    {
        var targets = ParseHosts(settings.GetList(HostsSetting));
        var pings = targets.Select(async t =>
        {
            var address = t.Address.Equals("gateway", StringComparison.OrdinalIgnoreCase) ? Gateway() : t.Address;
            return (t.Name, Rtt: address is null ? null : await _ping(address, ct));
        }).ToList();

        var values = new List<LiveValue>();
        foreach (var (name, rtt) in await Task.WhenAll(pings))
        {
            if (!_results.TryGetValue(name, out var history)) _results[name] = history = new Queue<long?>();
            history.Enqueue(rtt);
            while (history.Count > Window) history.Dequeue();

            var label = char.ToUpperInvariant(name[0]) + name[1..];
            if (history.LastOrDefault(r => r is not null) is { } last)
                values.Add(new LiveValue($"net.{name}.ms", $"{label} · {L.T("latenza", "latency")}", Groups.Network, last, "ms"));
            values.Add(new LiveValue($"net.{name}.loss", $"{label} · {L.T("pacchetti persi", "packet loss")}", Groups.Network,
                100d * history.Count(r => r is null) / history.Count, "%"));
        }
        // Forget hosts removed from the settings.
        foreach (var gone in _results.Keys.Except(targets.Select(t => t.Name)).ToList()) _results.Remove(gone);
        return values;
    }

    /// <summary>Lines "name=address"; the address "gateway" means the router, whatever its IP is today.</summary>
    internal static IReadOnlyList<(string Name, string Address)> ParseHosts(IReadOnlyList<string> lines) =>
    [
        .. lines
            .Select(l => l.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[1].Length > 0)
            .Select(p => (Name: new string([.. p[0].ToLowerInvariant().Where(char.IsLetterOrDigit)]), Address: p[1]))
            .Where(t => t.Name.Length > 0)
            .DistinctBy(t => t.Name),
    ];

    private string? Gateway()
    {
        var now = Environment.TickCount64;
        if (_gatewayAt == long.MinValue || now - _gatewayAt > GatewayLifetime.TotalMilliseconds)
        {
            _gateway = _findGateway();
            _gatewayAt = now;
        }
        return _gateway;
    }

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

    private static async Task<long?> PingAsync(string address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), cancellationToken: ct);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException)
        {
            return null; // No route, name not resolved, adapter down: all count as "no answer".
        }
    }
}
