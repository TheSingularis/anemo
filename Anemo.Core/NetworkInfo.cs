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
        // whatever GetAllNetworkInterfaces() happens to list first. A physical
        // Ethernet/WiFi adapter is always preferred over anything else (Tailscale, other
        // VPN tunnels, virtual switches) even if the virtual one happens to carry a
        // default route - e.g. Tailscale in exit-node mode adds one - since a VPN
        // shouldn't silently become "the" connection just because it's running.
        // Non-physical adapters are only picked as a last resort, when no physical one
        // has an IP at all.
        public static NetworkInterface? GetDefaultInterface()
        {
            var active = GetActiveInterfaces().ToList();
            var physical = active.Where(n => AdapterTypePriority(n) <= 1).ToList();

            var physicalWithGateway = physical
                .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderBy(AdapterTypePriority)
                .FirstOrDefault();
            if (physicalWithGateway != null) return physicalWithGateway;

            var anyPhysicalWithIp = physical
                .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderBy(AdapterTypePriority)
                .FirstOrDefault();
            if (anyPhysicalWithIp != null) return anyPhysicalWithIp;

            var other = active.Except(physical).ToList();
            return other.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                ?? other.FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                ?? active.FirstOrDefault();
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

        // Accepts "ip/prefix" (e.g. "192.168.1.50/24") - the format users naturally type
        // for a manual static-IP assignment, rather than making them split IP and subnet
        // mask across separate fields.
        public static bool TryParseCidr(string input, out System.Net.IPAddress address, out int prefixLength)
        {
            address = System.Net.IPAddress.None;
            prefixLength = 0;

            if (string.IsNullOrWhiteSpace(input)) return false;

            var parts = input.Trim().Split('/');
            if (parts.Length != 2) return false;

            if (!System.Net.IPAddress.TryParse(parts[0].Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return false;
            if (!int.TryParse(parts[1].Trim(), out var prefix) || prefix < 0 || prefix > 32)
                return false;

            address = ip;
            prefixLength = prefix;
            return true;
        }

        // netsh (and most network config surfaces) still wants a dotted subnet mask
        // rather than a CIDR prefix length, so static-IP assignment needs this conversion.
        public static string PrefixLengthToSubnetMask(int prefixLength)
        {
            if (prefixLength < 0 || prefixLength > 32)
                throw new ArgumentOutOfRangeException(nameof(prefixLength), "Prefix length must be between 0 and 32.");

            uint mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
            var bytes = new byte[] { (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask };
            return new System.Net.IPAddress(bytes).ToString();
        }
    }
}
