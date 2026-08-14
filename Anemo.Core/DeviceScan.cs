using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Anemo.Core
{
    public sealed record DiscoveredDevice(string Ip, string Hostname, string Mac, string Vendor, bool IsSelf = false);

    // Ping-sweeps the local subnet to populate the OS ARP cache, then reads that cache
    // once (arp -a) rather than shelling out per host. Reverse DNS and vendor lookup are
    // both best-effort - devices that don't respond to either just show "-".
    public static class DeviceScan
    {
        private const int MaxHostsToScan = 512;

        public static async Task ScanAsync(
            string localIp,
            int prefixLength,
            Action<DiscoveredDevice> onDevice,
            CancellationToken cancelToken,
            int concurrency = 64,
            int pingTimeoutMs = 400,
            string? localMac = null)
        {
            var hosts = EnumerateHosts(localIp, prefixLength).Take(MaxHostsToScan).ToList();
            var liveIps = new ConcurrentBag<string>();
            using var semaphore = new SemaphoreSlim(concurrency);

            var pingTasks = hosts.Select(async ip =>
            {
                await semaphore.WaitAsync(cancelToken);
                try
                {
                    if (cancelToken.IsCancellationRequested) return;

                    PingReply? reply = null;
                    try
                    {
                        using var ping = new Ping();
                        reply = await ping.SendPingAsync(ip, pingTimeoutMs);
                    }
                    catch { /* unreachable/filtered hosts are expected, not an error */ }

                    if (reply?.Status == IPStatus.Success) liveIps.Add(ip);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(pingTasks);

            if (cancelToken.IsCancellationRequested) return;

            var arpTable = ReadArpTable();

            foreach (var ip in liveIps.OrderBy(ParseLastOctet))
            {
                if (cancelToken.IsCancellationRequested) return;

                // A machine never ARPs itself, so its own row would otherwise show a
                // blank MAC/vendor even though we already know both from the local NIC.
                bool isSelf = ip == localIp && localMac != null;
                var mac = isSelf ? localMac! : arpTable.GetValueOrDefault(ip, "-");
                var vendor = isSelf ? "This device" : mac == "-" ? "-" : OuiVendors.Lookup(mac);
                var hostname = await TryResolveHostnameAsync(ip);
                onDevice(new DiscoveredDevice(ip, hostname, mac, vendor, isSelf));
            }
        }

        private static IEnumerable<string> EnumerateHosts(string localIp, int prefixLength)
        {
            if (!IPAddress.TryParse(localIp, out var addr)) yield break;

            uint ip = BitConverter.ToUInt32(addr.GetAddressBytes().Reverse().ToArray(), 0);
            uint mask = prefixLength <= 0 ? 0 : 0xFFFFFFFFu << (32 - prefixLength);
            uint network = ip & mask;
            uint broadcast = network | ~mask;

            for (uint host = network + 1; host < broadcast; host++)
            {
                var bytes = BitConverter.GetBytes(host).Reverse().ToArray();
                yield return new IPAddress(bytes).ToString();
            }
        }

        private static int ParseLastOctet(string ip) => int.TryParse(ip.Split('.')[^1], out var v) ? v : 0;

        private static Dictionary<string, string> ReadArpTable()
        {
            var result = new Dictionary<string, string>();
            var output = RunCommand("arp", "-a");

            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line, @"(?<ip>\d+\.\d+\.\d+\.\d+)\s+(?<mac>[0-9a-fA-F]{2}(-[0-9a-fA-F]{2}){5})\s+\w+");
                if (m.Success)
                {
                    result[m.Groups["ip"].Value] = m.Groups["mac"].Value.Replace('-', ':').ToUpperInvariant();
                }
            }
            return result;
        }

        private static async Task<string> TryResolveHostnameAsync(string ip)
        {
            try
            {
                var entryTask = Dns.GetHostEntryAsync(ip);
                var completed = await Task.WhenAny(entryTask, Task.Delay(800));
                if (completed == entryTask && entryTask.IsCompletedSuccessfully)
                {
                    return entryTask.Result.HostName;
                }
            }
            catch { /* no PTR record, or resolution failed - not an error */ }
            return "-";
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
