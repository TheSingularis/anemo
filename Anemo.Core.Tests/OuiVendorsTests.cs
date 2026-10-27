using Xunit;

namespace Anemo.Core.Tests
{
    public class OuiVendorsTests
    {
        [Fact]
        public void Lookup_FindsVendorFromEmbeddedRegistry()
        {
            Assert.Equal("Raspberry Pi Foundation", OuiVendors.Lookup("B8:27:EB:12:34:56"));
        }

        [Theory]
        [InlineData("B8:27:EB:12:34:56")]
        [InlineData("b8-27-eb-12-34-56")]
        [InlineData("b827.eb12.3456")]
        [InlineData("B827EB123456")]
        [InlineData("B8:27:EB")]
        public void Lookup_IgnoresMacFormattingAndCase(string mac)
        {
            Assert.Equal("Raspberry Pi Foundation", OuiVendors.Lookup(mac));
        }

        [Fact]
        public void Lookup_KeepsCommasInsideVendorNames()
        {
            Assert.Equal("Cisco Systems, Inc", OuiVendors.Lookup("E8:0A:B9:00:00:01"));
            Assert.Equal("Google, Inc.", OuiVendors.Lookup("3C:5A:B4:00:00:01"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("-")]
        [InlineData("B8:27")]
        [InlineData("zz:zz:zz:zz:zz:zz")]
        public void Lookup_ReturnsUnknownForUnusableInput(string mac)
        {
            Assert.Equal("Unknown", OuiVendors.Lookup(mac));
        }

        [Fact]
        public void Lookup_ReturnsUnknownForUnassignedPrefix()
        {
            // FF:FF:FF is the broadcast address, never an assigned OUI.
            Assert.Equal("Unknown", OuiVendors.Lookup("FF:FF:FF:FF:FF:FF"));
        }
    }
}
