using Xunit;

namespace Anemo.Core.Tests
{
    public class WellKnownPortsTests
    {
        [Theory]
        [InlineData(22, "SSH")]
        [InlineData(80, "HTTP")]
        [InlineData(443, "HTTPS")]
        [InlineData(445, "SMB")]
        [InlineData(3389, "RDP")]
        [InlineData(32400, "Plex")]
        public void GetServiceName_ReturnsKnownService(int port, string expected)
        {
            Assert.Equal(expected, WellKnownPorts.GetServiceName(port));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(54321)]
        [InlineData(-1)]
        public void GetServiceName_ReturnsDashForUnknownPort(int port)
        {
            Assert.Equal("-", WellKnownPorts.GetServiceName(port));
        }
    }
}
