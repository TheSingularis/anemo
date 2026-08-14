using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkWidget.Core
{
    public sealed record PortResult(int Port, string Service, bool IsOpen);

    public static class PortScan
    {
        public static async Task ScanAsync(
            string host,
            int startPort,
            int endPort,
            Action<PortResult> onResult,
            CancellationToken cancelToken,
            int concurrency = 100,
            int timeoutMs = 500)
        {
            var ports = Enumerable.Range(startPort, Math.Max(0, endPort - startPort + 1));
            using var semaphore = new SemaphoreSlim(concurrency);

            var tasks = ports.Select(async port =>
            {
                await semaphore.WaitAsync(cancelToken);
                try
                {
                    if (cancelToken.IsCancellationRequested) return;

                    bool open = await IsPortOpenAsync(host, port, timeoutMs, cancelToken);
                    onResult(new PortResult(port, WellKnownPorts.GetServiceName(port), open));
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs, CancellationToken cancelToken)
        {
            using var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(host, port, cancelToken).AsTask();
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cancelToken));
                return completed == connectTask && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
