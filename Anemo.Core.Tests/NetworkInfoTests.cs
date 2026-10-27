using System.Net;
using Xunit;

namespace Anemo.Core.Tests
{
    public class NetworkInfoTests
    {
        [Theory]
        [InlineData("192.168.1.50/24", "192.168.1.50", 24)]
        [InlineData("  10.0.0.1 / 8 ", "10.0.0.1", 8)]
        [InlineData("172.16.5.9/0", "172.16.5.9", 0)]
        [InlineData("10.1.2.3/32", "10.1.2.3", 32)]
        public void TryParseCidr_AcceptsValidInput(string input, string expectedIp, int expectedPrefix)
        {
            Assert.True(NetworkInfo.TryParseCidr(input, out var ip, out var prefix));
            Assert.Equal(IPAddress.Parse(expectedIp), ip);
            Assert.Equal(expectedPrefix, prefix);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("192.168.1.50")]
        [InlineData("192.168.1.50/")]
        [InlineData("192.168.1.50/33")]
        [InlineData("192.168.1.50/-1")]
        [InlineData("192.168.1.50/abc")]
        [InlineData("not-an-ip/24")]
        [InlineData("192.168.1.50/24/8")]
        [InlineData("fe80::1/64")]
        public void TryParseCidr_RejectsInvalidInput(string input)
        {
            Assert.False(NetworkInfo.TryParseCidr(input, out var ip, out var prefix));
            Assert.Equal(IPAddress.None, ip);
            Assert.Equal(0, prefix);
        }

        [Theory]
        [InlineData(0, "0.0.0.0")]
        [InlineData(8, "255.0.0.0")]
        [InlineData(16, "255.255.0.0")]
        [InlineData(20, "255.255.240.0")]
        [InlineData(24, "255.255.255.0")]
        [InlineData(25, "255.255.255.128")]
        [InlineData(30, "255.255.255.252")]
        [InlineData(32, "255.255.255.255")]
        public void PrefixLengthToSubnetMask_ConvertsKnownPrefixes(int prefix, string expected)
        {
            Assert.Equal(expected, NetworkInfo.PrefixLengthToSubnetMask(prefix));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(33)]
        public void PrefixLengthToSubnetMask_RejectsOutOfRangePrefix(int prefix)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NetworkInfo.PrefixLengthToSubnetMask(prefix));
        }

        [Theory]
        [InlineData(0, "-")]
        [InlineData(-5, "-")]
        [InlineData(10_000_000, "10 Mbps")]
        [InlineData(100_000_000, "100 Mbps")]
        [InlineData(1_000_000_000, "1 Gbps")]
        [InlineData(2_500_000_000, "2.5 Gbps")]
        public void FormatLinkSpeed_PicksUnit(long bitsPerSecond, string expected)
        {
            Assert.Equal(expected, NetworkInfo.FormatLinkSpeed(bitsPerSecond));
        }

        [Fact]
        public void FormatMac_InsertsColonsIntoTwelveHexDigits()
        {
            Assert.Equal("AA:BB:CC:DD:EE:FF", NetworkInfo.FormatMac("AABBCCDDEEFF"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("AABBCC")]
        [InlineData("AA:BB:CC:DD:EE:FF")]
        public void FormatMac_LeavesUnexpectedLengthsUntouched(string raw)
        {
            Assert.Equal(raw, NetworkInfo.FormatMac(raw));
        }
    }
}
