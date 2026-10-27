using Xunit;

namespace Anemo.Core.Tests
{
    public class DeviceScanTests
    {
        [Fact]
        public void EnumerateHosts_Slash24_SkipsNetworkAndBroadcast()
        {
            var hosts = DeviceScan.EnumerateHosts("192.168.1.50", 24).ToList();

            Assert.Equal(254, hosts.Count);
            Assert.Equal("192.168.1.1", hosts[0]);
            Assert.Equal("192.168.1.254", hosts[^1]);
            Assert.DoesNotContain("192.168.1.0", hosts);
            Assert.DoesNotContain("192.168.1.255", hosts);
        }

        [Fact]
        public void EnumerateHosts_UsesNetworkOfLocalIpNotItsHostBits()
        {
            var hosts = DeviceScan.EnumerateHosts("10.0.5.200", 30).ToList();

            // 10.0.5.200/30 is the 10.0.5.200-203 block.
            Assert.Equal(new[] { "10.0.5.201", "10.0.5.202" }, hosts);
        }

        [Fact]
        public void EnumerateHosts_CrossesOctetBoundaries()
        {
            var hosts = DeviceScan.EnumerateHosts("172.16.3.9", 23).ToList();

            Assert.Equal(510, hosts.Count);
            Assert.Equal("172.16.2.1", hosts[0]);
            Assert.Contains("172.16.3.1", hosts);
            Assert.Equal("172.16.3.254", hosts[^1]);
        }

        [Theory]
        [InlineData(31)]
        [InlineData(32)]
        public void EnumerateHosts_PointToPointPrefixesHaveNoHosts(int prefix)
        {
            Assert.Empty(DeviceScan.EnumerateHosts("192.168.1.50", prefix));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-an-ip")]
        public void EnumerateHosts_InvalidIpYieldsNothing(string ip)
        {
            Assert.Empty(DeviceScan.EnumerateHosts(ip, 24));
        }

        [Fact]
        public void ParseArpTable_ReadsWindowsArpOutput()
        {
            const string output =
                "\r\n" +
                "Interface: 192.168.1.50 --- 0x5\r\n" +
                "  Internet Address      Physical Address      Type\r\n" +
                "  192.168.1.1           e8-0a-b9-12-34-56     dynamic\r\n" +
                "  192.168.1.20          b8-27-eb-aa-bb-cc     dynamic\r\n" +
                "  192.168.1.255         ff-ff-ff-ff-ff-ff     static\r\n";

            var table = DeviceScan.ParseArpTable(output);

            Assert.Equal(3, table.Count);
            Assert.Equal("E8:0A:B9:12:34:56", table["192.168.1.1"]);
            Assert.Equal("B8:27:EB:AA:BB:CC", table["192.168.1.20"]);
            Assert.Equal("FF:FF:FF:FF:FF:FF", table["192.168.1.255"]);
        }

        [Fact]
        public void ParseArpTable_IgnoresHeadersAndGarbage()
        {
            Assert.Empty(DeviceScan.ParseArpTable(""));
            Assert.Empty(DeviceScan.ParseArpTable("No ARP Entries Found.\r\n"));
        }
    }
}
