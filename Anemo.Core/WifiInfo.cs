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
        string RadioType);

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
                return new CurrentWifiInfo(false, "-", "-", "-", "-", "-");
            }

            return new CurrentWifiInfo(
                Connected: true,
                Ssid: props.GetValueOrDefault("SSID", "-"),
                SignalPercent: props.GetValueOrDefault("Signal", "-"),
                RssiText: GetRssiText(),
                Channel: props.GetValueOrDefault("Channel", "-"),
                RadioType: props.GetValueOrDefault("Radio type", "-"));
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

        private static Dictionary<string, string> ParseNetshBlock(string raw)
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
