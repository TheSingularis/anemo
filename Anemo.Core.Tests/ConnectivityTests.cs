using System.Net;
using Xunit;

namespace Anemo.Core.Tests
{
    public class ConnectivityTests
    {
        // Exercises the IcmpSendEcho2Ex interop end to end without needing a real network.
        [Fact]
        public void IcmpPing_LoopbackFromLoopbackSucceeds()
        {
            var result = Connectivity.IcmpPing(IPAddress.Loopback, IPAddress.Loopback, 1000);
            Assert.EndsWith(" ms", result);
        }

        [Fact]
        public async System.Threading.Tasks.Task PingFromAsync_NoSourceAddressIsDash()
        {
            Assert.Equal("-", await Connectivity.PingFromAsync(null, "1.1.1.1"));
        }
    }
}
