using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Anemo.Core
{
    public sealed record AdapterDetails(
        string LinkSpeedText,
        string Ipv4,
        int SubnetPrefixLength,
        string? Gateway,
        IReadOnlyList<string> DnsServers,
        string Mac);

    public static class NetworkInfo
    {
        public static IEnumerable<NetworkInterface> GetActiveInterfaces() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            // Hyper-V virtual switches ("vEthernet (...)") clutter the
                            // adapter list without being anything a user would pick.
                            && !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase));

        public static int AdapterTypePriority(NetworkInterface nic) => nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 2,
        };

        // Prefers whichever of Ethernet/WiFi actually has a working route, wired over
        // wireless - matches how Windows itself deprioritizes WiFi once a cable is
        // plugged in, so this naturally tracks "the one really in use" rather than
        // whatever GetAllNetworkInterfaces() happens to list first.
        public static NetworkInterface? GetDefaultInterface()
        {
            var active = GetActiveInterfaces().ToList();
            var withGateway = active
                .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderBy(AdapterTypePriority)
                .FirstOrDefault();
            return withGateway ?? active.FirstOrDefault();
        }

        public static AdapterDetails GetAdapterDetails(NetworkInterface nic)
        {
            var props = nic.GetIPProperties();
            var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            var dnsServers = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString())
                .ToList();

            return new AdapterDetails(
                LinkSpeedText: FormatLinkSpeed(nic.Speed),
                Ipv4: v4?.Address.ToString() ?? "-",
                SubnetPrefixLength: v4?.PrefixLength ?? 0,
                Gateway: gateway?.Address.ToString(),
                DnsServers: dnsServers,
                Mac: FormatMac(nic.GetPhysicalAddress().ToString()));
        }

        public static string FormatLinkSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0) return "-";
            double mbps = bitsPerSecond / 1_000_000.0;
            return mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gbps" : $"{mbps:0} Mbps";
        }

        public static string FormatMac(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length != 12) return raw;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2)));
        }
    }
}
