using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using ManagedNativeWifi;

namespace Anemo.Core
{
    public sealed record CurrentWifiInfo(
        bool Connected,
        string Ssid,
        string SignalPercent,
        string RssiText,
        string Channel,
        string Band,
        string AccessPoint,
        string Bssid,
        string RadioType,
        string Authentication);

    public sealed record NearbyNetwork(
        string Ssid,
        int Channel,
        int RssiDbm,
        int SignalPercent,
        string Security);

    public static class WifiInfo
    {
        // netsh only exposes signal strength as a %; the real dBm figure comes from the
        // WLAN BSS list via the Native Wifi API, so that's queried directly rather than
        // approximated from the percentage.
        public static CurrentWifiInfo GetCurrent()
        {
            var output = RunCommand("netsh", "wlan show interfaces");
            var props = ParseNetshBlock(output);

            bool connected = props.TryGetValue("State", out var state)
                && state.Equals("connected", StringComparison.OrdinalIgnoreCase);

            if (!connected)
            {
                return new CurrentWifiInfo(false, "-", "-", "-", "-", "-", "-", "-", "-", "-");
            }

            var ap = GetConnectedAp();

            return new CurrentWifiInfo(
                Connected: true,
                Ssid: props.GetValueOrDefault("SSID", "-"),
                SignalPercent: props.GetValueOrDefault("Signal", "-"),
                RssiText: GetRssiText(),
                Channel: props.GetValueOrDefault("Channel") ?? ap.Channel ?? "-",
                Band: ap.Band ?? "-",
                AccessPoint: ap.Name ?? ap.Vendor ?? "-",
                Bssid: ap.Bssid ?? "-",
                RadioType: props.GetValueOrDefault("Radio type", "-"),
                Authentication: props.GetValueOrDefault("Authentication", "-"));
        }

        private static string GetRssiText()
        {
            try
            {
                var iface = NativeWifi.EnumerateInterfaces().FirstOrDefault(i => i.State == InterfaceState.Connected);
                if (iface == null) return "-";

                var (result, rssi) = NativeWifi.GetRssi(iface.Id);
                return result == ActionResult.Success ? $"{rssi} dBm" : "-";
            }
            catch
            {
                return "-";
            }
        }

        private sealed record ConnectedAp(string? Band, string? Channel, string? Bssid, string? Name, string? Vendor);

        // Band isn't reliably in netsh output (only newer Windows 11 builds print it, and the
        // label is localized), and it can't be inferred from the channel number alone since
        // 6 GHz channels reuse 1, 5, 9, ... So it's read from the BSS entry for the access
        // point we're associated with (matched by BSSID), whose center frequency gives both
        // band and channel. The channel only fills in when netsh didn't report one (e.g. a
        // non-English Windows). The AP's name is only present when its vendor broadcasts it
        // (see ApNameReader); otherwise the manufacturer from the BSSID's OUI stands in so
        // the AP can still be matched against a controller's device list. Anything that
        // can't be determined comes back null.
        private static ConnectedAp GetConnectedAp()
        {
            var none = new ConnectedAp(null, null, null, null, null);
            try
            {
                var iface = NativeWifi.EnumerateInterfaces().FirstOrDefault(i => i.State == InterfaceState.Connected);
                if (iface == null) return none;

                var (connResult, conn) = NativeWifi.GetCurrentConnection(iface.Id);
                if (connResult != ActionResult.Success || conn == null) return none;

                var bssid = conn.Bssid.ToString();
                var vendor = OuiVendors.Lookup(bssid);
                var name = ApNameReader.TryRead(iface.Id, conn.Bssid.ToBytes());
                var identity = none with
                {
                    Bssid = bssid,
                    Name = name,
                    Vendor = vendor == "Unknown" ? null : vendor
                };

                var (bssResult, bssList) = NativeWifi.EnumerateBssNetworks(iface.Id);
                if (bssResult != ActionResult.Success) return identity;

                var bss = bssList.FirstOrDefault(b => string.Equals(b.Bssid.ToString(), bssid, StringComparison.OrdinalIgnoreCase));
                if (bss == null || bss.Band <= 0) return identity;

                // Band is reported as 2.4 / 3.6 / 5.0 / 6.0 (GHz); drop the trailing ".0".
                return identity with
                {
                    Band = $"{bss.Band.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} GHz",
                    Channel = bss.Channel > 0 ? bss.Channel.ToString() : null
                };
            }
            catch
            {
                return none;
            }
        }

        // For the Wi-Fi Analyzer: every nearby access point, not just the one currently
        // connected to. BSS entries carry channel/RSSI; security isn't part of that data
        // so it's joined in from the separate available-networks list by SSID (falling
        // back to "-" for anything that doesn't have a matching entry, e.g. hidden SSIDs).
        public static IReadOnlyList<NearbyNetwork> GetNearbyNetworks()
        {
            try
            {
                var security = NativeWifi.EnumerateAvailableNetworks()
                    .GroupBy(n => n.Ssid.ToString())
                    .ToDictionary(g => g.Key, g => g.First().IsSecurityEnabled ? "Secured" : "Open");

                return NativeWifi.EnumerateBssNetworks()
                    .Select(bss =>
                    {
                        var ssid = bss.Ssid.ToString();
                        return new NearbyNetwork(
                            Ssid: string.IsNullOrEmpty(ssid) ? "(hidden)" : ssid,
                            Channel: bss.Channel,
                            RssiDbm: bss.Rssi,
                            SignalPercent: bss.LinkQuality,
                            Security: security.GetValueOrDefault(ssid, "-"));
                    })
                    .OrderByDescending(n => n.RssiDbm)
                    .ToList();
            }
            catch
            {
                return Array.Empty<NearbyNetwork>();
            }
        }

        internal static Dictionary<string, string> ParseNetshBlock(string raw)
        {
            var result = new Dictionary<string, string>();
            foreach (var line in raw.Split('\n'))
            {
                var m = Regex.Match(line, @"^\s+(?<name>[^:]+):\s?(?<value>.*)$");
                if (m.Success)
                {
                    var key = m.Groups["name"].Value.Trim();
                    var val = m.Groups["value"].Value.Trim();
                    if (!result.ContainsKey(key)) result[key] = val;
                }
            }
            return result;
        }

        private static string RunCommand(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            string output = proc!.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
    }
}
