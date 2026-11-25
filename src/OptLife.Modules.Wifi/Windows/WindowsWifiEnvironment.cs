using System.ComponentModel;
using System.Diagnostics;

namespace OptLife.Modules.Wifi.Windows;

/// <summary>The real machine: netsh, as Windows ships it. Since Windows 11 24H2 it answers only with Location on or as administrator.</summary>
internal sealed class WindowsWifiEnvironment : IWifiEnvironment
{
    public string? Interfaces()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", "wlan show interfaces")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return output.Result;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public WifiAdapter? Adapter()
    {
        try
        {
            var cards = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                            // Windows' own virtual adapters (hotspot, Wi-Fi Direct) are not the card.
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Direct", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var up = cards.FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.Speed > 0);
            var card = up ?? cards.FirstOrDefault();
            return card is null ? null : new WifiAdapter(card.Description, up is null ? null : (int)(up.Speed / 1_000_000));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return null;
        }
    }

    public IReadOnlyList<WiredLink> Wired()
    {
        try
        {
            var list = new List<WiredLink>();
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet))
            {
                // Virtual cards (Hyper-V, VPNs, VirtualBox, WSL) are not a cable.
                var d = n.Description;
                if (new[] { "Virtual", "Hyper-V", "VPN", "TAP-", "WireGuard", "Loopback", "Bluetooth", "VMware", "VirtualBox", "WAN Miniport", "Tailscale", "ZeroTier", "Fortinet", "Kernel Debug" }
                    .Any(v => d.Contains(v, StringComparison.OrdinalIgnoreCase))) continue;
                var up = n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
                long errors = 0;
                var gateway = false;
                try
                {
                    var s = n.GetIPStatistics();
                    errors = s.IncomingPacketsWithErrors + s.OutgoingPacketsWithErrors + s.IncomingPacketsDiscarded + s.OutgoingPacketsDiscarded;
                    gateway = n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any));
                }
                catch (System.Net.NetworkInformation.NetworkInformationException)
                {
                }
                list.Add(new WiredLink(n.Id, n.Name, d, up, up && gateway, up ? n.Speed / 1_000_000 : 0, errors));
            }
            return list;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    public bool WifiInUse()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    public ActionResult Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false })?.Dispose();
            return new ActionResult(true, L.T($"Aperto: {target}", $"Opened: {target}"));
        }
        catch (Win32Exception ex)
        {
            return new ActionResult(false, ex.Message);
        }
    }
}
