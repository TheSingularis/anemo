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
