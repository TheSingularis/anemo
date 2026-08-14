using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anemo.Core
{
    public sealed record SpeedTestServer(string Name, string BaseUrl, string DownloadPath, string UploadPath, string PingPath);

    // Thin client for the LibreSpeed protocol (https://github.com/librespeed/speedtest) -
    // open source and self-hostable, unlike the unofficial packages that wrap Ookla's
    // speedtest.net infrastructure against its ToS. No mature .NET client exists for it,
    // but the protocol itself (plain HTTP GET/POST against a few known endpoints) is
    // simple enough to implement directly. Server list is LibreSpeed's own official public
    // directory - the same one their web client uses to auto-select a nearby server.
    public static class SpeedTestClient
    {
        private const string ServerListUrl = "https://librespeed.org/backend-servers/servers.php";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task<IReadOnlyList<SpeedTestServer>> GetServersAsync()
        {
            var json = await Http.GetStringAsync(ServerListUrl);
            using var doc = JsonDocument.Parse(json);

            var list = new List<SpeedTestServer>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var baseUrl = el.GetProperty("server").GetString() ?? "";
                if (baseUrl.StartsWith("//")) baseUrl = "https:" + baseUrl;
                if (!baseUrl.EndsWith("/")) baseUrl += "/";

                list.Add(new SpeedTestServer(
                    Name: el.GetProperty("name").GetString() ?? "?",
                    BaseUrl: baseUrl,
                    DownloadPath: el.GetProperty("dlURL").GetString() ?? "garbage.php",
                    UploadPath: el.GetProperty("ulURL").GetString() ?? "empty.php",
                    PingPath: el.GetProperty("pingURL").GetString() ?? "empty.php"));
            }
            return list;
        }

        // Picks the lowest-latency server out of a handful of random candidates - a
        // lightweight stand-in for LibreSpeed's own geolocation-based auto-select, without
        // needing an IP-to-distance lookup of our own.
        public static async Task<SpeedTestServer?> PickBestServerAsync(IReadOnlyList<SpeedTestServer> servers, int candidateCount = 6)
        {
            var candidates = servers.OrderBy(_ => Guid.NewGuid()).Take(candidateCount).ToList();

            var results = await Task.WhenAll(candidates.Select(async s =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var resp = await Http.GetAsync(PingUrl(s));
                    sw.Stop();
                    return resp.IsSuccessStatusCode ? (server: s, ms: sw.Elapsed.TotalMilliseconds) : (server: s, ms: double.MaxValue);
                }
                catch
                {
                    return (server: s, ms: double.MaxValue);
                }
            }));

            var best = results.OrderBy(r => r.ms).FirstOrDefault();
            return best.ms < double.MaxValue ? best.server : null;
        }

        public static async Task<double> PingTestAsync(SpeedTestServer server, int samples = 8)
        {
            var times = new List<double>();
            for (int i = 0; i < samples; i++)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var resp = await Http.GetAsync(PingUrl(server));
                    sw.Stop();
                    if (resp.IsSuccessStatusCode) times.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch { /* one dropped sample shouldn't fail the whole test */ }
            }
            return times.Count > 0 ? times.Min() : -1;
        }

        // Runs several concurrent download streams for the given duration, summing bytes
        // received across all of them - mirrors LibreSpeed's own multi-stream approach
        // (its default is 6 streams; kept lower here to match a "lightweight tool" ethos).
        public static async Task<double> DownloadTestAsync(
            SpeedTestServer server,
            Action<double>? onProgressMbps = null,
            int durationMs = 6000,
            int streams = 4)
        {
            using var cts = new CancellationTokenSource(durationMs);
            long totalBytes = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            async Task RunStream()
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        using var resp = await Http.GetAsync(DownloadUrl(server, 20), HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);

                        var buffer = new byte[64 * 1024];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                        {
                            Interlocked.Add(ref totalBytes, read);
                            onProgressMbps?.Invoke(ToMbps(Interlocked.Read(ref totalBytes), sw.Elapsed.TotalSeconds));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* a dropped stream just retries via the outer while */ }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, streams).Select(_ => RunStream()));
            sw.Stop();
            return ToMbps(totalBytes, sw.Elapsed.TotalSeconds);
        }

        public static async Task<double> UploadTestAsync(
            SpeedTestServer server,
            Action<double>? onProgressMbps = null,
            int durationMs = 6000,
            int streams = 3,
            int blobMegabytes = 4)
        {
            var payload = new byte[blobMegabytes * 1024 * 1024];
            Random.Shared.NextBytes(payload);

            using var cts = new CancellationTokenSource(durationMs);
            long totalBytes = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            async Task RunStream()
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        using var content = new ByteArrayContent(payload);
                        using var resp = await Http.PostAsync(UploadUrl(server), content, cts.Token);
                        Interlocked.Add(ref totalBytes, payload.Length);
                        onProgressMbps?.Invoke(ToMbps(Interlocked.Read(ref totalBytes), sw.Elapsed.TotalSeconds));
                    }
                    catch (OperationCanceledException) { }
                    catch { /* a dropped stream just retries via the outer while */ }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, streams).Select(_ => RunStream()));
            sw.Stop();
            return ToMbps(totalBytes, sw.Elapsed.TotalSeconds);
        }

        private static double ToMbps(long bytes, double seconds) =>
            seconds > 0 ? bytes * 8.0 / seconds / 1_000_000.0 : 0;

        private static string PingUrl(SpeedTestServer s) => $"{s.BaseUrl}{s.PingPath}{Sep(s.PingPath)}r={Random.Shared.NextDouble()}";
        private static string DownloadUrl(SpeedTestServer s, int chunkMb) => $"{s.BaseUrl}{s.DownloadPath}{Sep(s.DownloadPath)}r={Random.Shared.NextDouble()}&ckSize={chunkMb}";
        private static string UploadUrl(SpeedTestServer s) => $"{s.BaseUrl}{s.UploadPath}{Sep(s.UploadPath)}r={Random.Shared.NextDouble()}";

        private static string Sep(string path) => path.Contains('?') ? "&" : "?";
    }
}
