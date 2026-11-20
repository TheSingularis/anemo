using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Anemo.Core
{
    public static class Connectivity
    {
        public static async Task<string> PingAsync(string? host, int timeoutMs = 1500)
        {
            if (string.IsNullOrEmpty(host)) return "-";

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, timeoutMs);
                return reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms" : "unreachable";
            }
            catch
            {
                return "-";
            }
        }

        // Pings out through one specific adapter, by sending from that adapter's own IPv4
        // address. Plain System.Net.NetworkInformation.Ping can't choose a source address, so
        // with two connections up (say Wi-Fi plus a cable whose port has no internet) it
        // would report whichever one Windows happens to route through, not the adapter being
        // looked at. Uses the Windows ICMP API (IcmpSendEcho2Ex), which can.
        public static Task<string> PingFromAsync(IPAddress? source, string? host, int timeoutMs = 1500)
        {
            if (source == null || string.IsNullOrEmpty(host)) return Task.FromResult("-");
            if (!IPAddress.TryParse(host, out var destination)
                || destination.AddressFamily != AddressFamily.InterNetwork
                || source.AddressFamily != AddressFamily.InterNetwork)
            {
                return PingAsync(host, timeoutMs);
            }

            return Task.Run(() => IcmpPing(source, destination, timeoutMs));
        }

        internal static string IcmpPing(IPAddress source, IPAddress destination, int timeoutMs)
        {
            var handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return "-";

            var payload = new byte[32];
            // Big enough for the reply header (40 bytes on 64-bit), the echoed payload, an
            // ICMP error message and IO_STATUS_BLOCK, per the IcmpSendEcho2Ex docs.
            const int replySize = 256;
            var reply = System.Runtime.InteropServices.Marshal.AllocHGlobal(replySize);
            try
            {
                uint count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    ToIpAddr(source), ToIpAddr(destination),
                    payload, (ushort)payload.Length, IntPtr.Zero, reply, replySize, (uint)timeoutMs);
                if (count == 0) return "unreachable";

                // ICMP_ECHO_REPLY: ULONG Address; ULONG Status; ULONG RoundTripTime; ...
                uint status = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(reply, 4);
                uint rtt = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(reply, 8);
                return status == 0 ? $"{rtt} ms" : "unreachable";
            }
            catch
            {
                return "-";
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(reply);
                IcmpCloseHandle(handle);
            }
        }

        // IPAddr is the address's four bytes in network order, read as a native uint.
        private static uint ToIpAddr(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes(), 0);

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho2Ex(
            IntPtr icmpHandle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
            uint sourceAddress, uint destinationAddress,
            byte[] requestData, ushort requestSize, IntPtr requestOptions,
            IntPtr replyBuffer, uint replySize, uint timeout);

        public static async Task<string> GetPublicIpAsync(HttpClient client)
        {
            try
            {
                var ip = await client.GetStringAsync("https://api.ipify.org");
                return ip.Trim();
            }
            catch
            {
                return "-";
            }
        }

        // Same lookup, but sent out through one specific adapter by binding the connection
        // to that adapter's own IPv4 address. Without this, a laptop on both Wi-Fi and a
        // freshly plugged-in cable reports whichever connection Windows routes through,
        // which may not be the adapter being looked at. A fresh connection is made each
        // time (no pooling), so a result can never come from a socket opened on a previous
        // network.
        public static async Task<string> GetPublicIpAsync(IPAddress sourceAddress, TimeSpan timeout)
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.Zero,
                    ConnectCallback = async (context, ct) =>
                    {
                        var targets = await Dns.GetHostAddressesAsync(
                            context.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };
                        try
                        {
                            socket.Bind(new IPEndPoint(sourceAddress, 0));
                            await socket.ConnectAsync(targets, context.DnsEndPoint.Port, ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                };
                using var client = new HttpClient(handler) { Timeout = timeout };
                var ip = await client.GetStringAsync("https://api.ipify.org");
                return ip.Trim();
            }
            catch
            {
                return "-";
            }
        }
    }
}
