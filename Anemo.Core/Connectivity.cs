using System.Net.Http;
using System.Net.NetworkInformation;
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
    }
}
