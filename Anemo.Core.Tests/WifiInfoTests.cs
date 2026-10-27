using Xunit;

namespace Anemo.Core.Tests
{
    public class WifiInfoTests
    {
        private const string NetshOutput =
            "\r\n" +
            "There is 1 interface on the system:\r\n" +
            "\r\n" +
            "    Name                   : Wi-Fi\r\n" +
            "    Description            : Intel(R) Wi-Fi 6 AX201 160MHz\r\n" +
            "    State                  : connected\r\n" +
            "    SSID                   : Hotel Guest\r\n" +
            "    BSSID                  : e8:0a:b9:12:34:56\r\n" +
            "    Network type           : Infrastructure\r\n" +
            "    Radio type             : 802.11ax\r\n" +
            "    Authentication         : WPA2-Personal\r\n" +
            "    Channel                : 36\r\n" +
            "    Receive rate (Mbps)    : 866.7\r\n" +
            "    Signal                 : 91%\r\n" +
            "    Profile                : Hotel Guest\r\n";

        [Fact]
        public void ParseNetshBlock_ReadsKeyValuePairs()
        {
            var props = WifiInfo.ParseNetshBlock(NetshOutput);

            Assert.Equal("connected", props["State"]);
            Assert.Equal("Hotel Guest", props["SSID"]);
            Assert.Equal("802.11ax", props["Radio type"]);
            Assert.Equal("WPA2-Personal", props["Authentication"]);
            Assert.Equal("36", props["Channel"]);
            Assert.Equal("91%", props["Signal"]);
            Assert.Equal("866.7", props["Receive rate (Mbps)"]);
        }

        [Fact]
        public void ParseNetshBlock_KeepsColonsInsideValues()
        {
            var props = WifiInfo.ParseNetshBlock(NetshOutput);

            Assert.Equal("e8:0a:b9:12:34:56", props["BSSID"]);
        }

        [Fact]
        public void ParseNetshBlock_KeepsSsidAndBssidSeparate()
        {
            var props = WifiInfo.ParseNetshBlock(NetshOutput);

            Assert.NotEqual(props["SSID"], props["BSSID"]);
        }

        [Fact]
        public void ParseNetshBlock_IgnoresUnindentedHeaderLines()
        {
            var props = WifiInfo.ParseNetshBlock(NetshOutput);

            Assert.DoesNotContain(props.Keys, k => k.StartsWith("There is"));
        }

        [Fact]
        public void ParseNetshBlock_FirstOccurrenceWinsWhenInterfacesRepeat()
        {
            const string twoInterfaces =
                "    Name        : Wi-Fi\r\n" +
                "    State       : connected\r\n" +
                "\r\n" +
                "    Name        : Wi-Fi 2\r\n" +
                "    State       : disconnected\r\n";

            var props = WifiInfo.ParseNetshBlock(twoInterfaces);

            Assert.Equal("Wi-Fi", props["Name"]);
            Assert.Equal("connected", props["State"]);
        }

        [Fact]
        public void ParseNetshBlock_ReportsDisconnectedState()
        {
            const string disconnected =
                "    Name        : Wi-Fi\r\n" +
                "    State       : disconnected\r\n";

            var props = WifiInfo.ParseNetshBlock(disconnected);

            Assert.Equal("disconnected", props["State"]);
            Assert.False(props.ContainsKey("SSID"));
        }

        [Fact]
        public void ParseNetshBlock_EmptyInputYieldsEmptyDictionary()
        {
            Assert.Empty(WifiInfo.ParseNetshBlock(""));
        }
    }
}
