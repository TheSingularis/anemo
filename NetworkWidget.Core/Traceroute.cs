using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkWidget.Core
{
    public class HopResult : INotifyPropertyChanged
    {
        public int Hop { get; init; }
        public string Address { get; init; } = "";
        public string Time { get; init; } = "";
        public double? Rtt { get; init; }

        private double _barWidth;
        public double BarWidth
        {
            get => _barWidth;
            set { _barWidth = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarWidth))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // Mirrors classic tracert.exe behavior (increasing TTL, one probe per hop) using
    // .NET's own Ping class rather than shelling out, matching how Gateway/1.1.1.1
    // pings already work elsewhere in this app.
    public static class Traceroute
    {
        public static async Task RunAsync(
            string target,
            Action<HopResult> onHop,
            CancellationToken cancelToken,
            int maxHops = 30,
            int timeoutMs = 2000)
        {
            var options_data = new byte[32];

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                if (cancelToken.IsCancellationRequested) return;

                using var ping = new Ping();
                var options = new PingOptions(ttl, dontFragment: true);

                PingReply reply;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    reply = await ping.SendPingAsync(target, timeoutMs, options_data, options);
                }
                catch (PingException)
                {
                    onHop(new HopResult { Hop = ttl, Address = "Error", Time = "" });
                    continue;
                }
                stopwatch.Stop();

                if (cancelToken.IsCancellationRequested) return;

                switch (reply.Status)
                {
                    case IPStatus.TtlExpired:
                    case IPStatus.Success:
                        // PingReply.RoundtripTime is unreliable on Windows for TTL-expired replies
                        // (frequently reports 0), so time the round trip ourselves instead.
                        onHop(new HopResult
                        {
                            Hop = ttl,
                            Address = reply.Address?.ToString() ?? "*",
                            Time = $"{stopwatch.ElapsedMilliseconds} ms",
                            Rtt = stopwatch.ElapsedMilliseconds
                        });
                        if (reply.Status == IPStatus.Success) return; // reached the target
                        break;

                    case IPStatus.TimedOut:
                        onHop(new HopResult { Hop = ttl, Address = "* * *", Time = "" });
                        break;

                    default:
                        onHop(new HopResult { Hop = ttl, Address = reply.Status.ToString(), Time = "" });
                        break;
                }
            }
        }
    }
}
