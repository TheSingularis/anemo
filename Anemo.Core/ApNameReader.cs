using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Anemo.Core
{
    // Reads the AP's configured name out of the vendor-specific information elements in its
    // beacon / probe response. There's no standard field for this; each vendor stuffs the name
    // into its own element (and most only do so when "advertise AP name" is switched on for the
    // SSID), so this only knows the vendors Wireshark's 802.11 dissector decodes. ManagedNativeWifi
    // doesn't expose the raw IE bytes, hence the direct Native Wifi call.
    internal static class ApNameReader
    {
        private const uint ClientVersion = 2;
        private const int BssTypeAny = 3;

        // Mirrors WLAN_BSS_ENTRY (wlanapi.h); sequential layout matches the native packing.
        [StructLayout(LayoutKind.Sequential)]
        private struct BssEntry
        {
            public uint SsidLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
            public uint PhyId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
            public int BssType;
            public int PhyType;
            public int Rssi;
            public uint LinkQuality;
            public byte InRegDomain;
            public ushort BeaconPeriod;
            public ulong Timestamp;
            public ulong HostTimestamp;
            public ushort CapabilityInformation;
            public uint ChCenterFrequency;
            public uint RateSetLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] RateSet;
            public uint IeOffset;
            public uint IeSize;
        }

        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanGetNetworkBssList(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            IntPtr ssid,
            int bssType,
            [MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
            IntPtr reserved,
            out IntPtr bssList);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        public static string? TryRead(Guid interfaceId, byte[] bssid)
        {
            IntPtr client = IntPtr.Zero;
            IntPtr list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out client) != 0) return null;
                if (WlanGetNetworkBssList(client, ref interfaceId, IntPtr.Zero, BssTypeAny, false, IntPtr.Zero, out list) != 0
                    || list == IntPtr.Zero) return null;

                // WLAN_BSS_LIST: DWORD dwTotalSize, DWORD dwNumberOfItems, then the entries.
                int count = Marshal.ReadInt32(list, 4);
                int entrySize = Marshal.SizeOf<BssEntry>();

                for (int i = 0; i < count; i++)
                {
                    var entryPtr = IntPtr.Add(list, 8 + i * entrySize);
                    var entry = Marshal.PtrToStructure<BssEntry>(entryPtr);
                    if (!entry.Bssid.AsSpan().SequenceEqual(bssid) || entry.IeSize == 0) continue;

                    // The IE blob sits at an offset from the start of its own entry.
                    var ies = new byte[entry.IeSize];
                    Marshal.Copy(IntPtr.Add(entryPtr, (int)entry.IeOffset), ies, 0, ies.Length);

                    var name = ParseApName(ies);
                    if (name != null) return name;
                }
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        private static string? ParseApName(byte[] ies)
        {
            int pos = 0;
            while (pos + 2 <= ies.Length)
            {
                int id = ies[pos];
                int len = ies[pos + 1];
                if (pos + 2 + len > ies.Length) break;

                var body = new ReadOnlySpan<byte>(ies, pos + 2, len);
                pos += 2 + len;

                string? name = null;
                if (id == 133) name = ReadCiscoCcx1(body);
                else if (id == 221) name = ReadVendorSpecific(body);
                if (name != null) return name;
            }
            return null;
        }

        // Cisco CCX v1 "device name" element (0x85): 10 bytes of other data, then a 16-byte,
        // NUL-padded name.
        private static string? ReadCiscoCcx1(ReadOnlySpan<byte> body)
        {
            return body.Length >= 26 ? Clean(body.Slice(10, 16)) : null;
        }

        // Vendor-specific element: 3-byte OUI, then a vendor-defined body whose first byte is
        // the OUI type. Layouts below follow Wireshark's packet-ieee80211.c.
        private static string? ReadVendorSpecific(ReadOnlySpan<byte> body)
        {
            if (body.Length < 4) return null;

            int oui = (body[0] << 16) | (body[1] << 8) | body[2];
            var data = body.Slice(3);

            switch (oui)
            {
                case 0x004096: // Cisco Aironet (also Meraki): type 47 = AP name v2, rest is the name
                    return data.Length > 1 && data[0] == 47 ? Clean(data.Slice(1)) : null;

                case 0x000B86: // Aruba: type, subtype 3 = AP name, one skipped byte, name
                    return data.Length > 3 && data[1] == 3 ? Clean(data.Slice(3)) : null;

                case 0x00156D: // Ubiquiti UniFi
                case 0x5C5B35: // Mist
                case 0xDC0856: // Alcatel-Lucent
                case 0x3CB9A6: // Belden
                    return ReadTypedName(data, 1);

                case 0x001392: // Ruckus
                    return ReadTypedName(data, 3);

                case 0x001174: // Arista (formerly Mojo)
                    return ReadTypedName(data, 6);

                case 0x001977: // Aerohive: type 33, version, subtype, length, hostname
                    if (data.Length > 4 && data[0] == 33)
                    {
                        int len = data[3];
                        if (4 + len <= data.Length) return Clean(data.Slice(4, len));
                    }
                    return null;

                case 0x00E0FC: // Huawei: type, subtype 1 = AP name, one skipped byte, name
                    return data.Length > 3 && data[1] == 1 ? Clean(data.Slice(3)) : null;

                case 0x00A0F8: // Extreme WiNG: type 1, 7 unknown bytes, length, name
                    if (data.Length > 9 && data[0] == 1)
                    {
                        int len = data[8];
                        if (9 + len <= data.Length) return Clean(data.Slice(9, len));
                    }
                    return null;

                case 0x00090F: // Fortinet: 16-bit type 10 (system), then type/length/value entries
                    return ReadFortinet(data);

                default:
                    return null;
            }
        }

        // Shared shape for several vendors: one type byte, and if it's the AP-name type the
        // rest of the element is the name.
        private static string? ReadTypedName(ReadOnlySpan<byte> data, byte apNameType)
        {
            return data.Length > 1 && data[0] == apNameType ? Clean(data.Slice(1)) : null;
        }

        private static string? ReadFortinet(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2 || data[0] != 10 || data[1] != 0) return null;

            int pos = 2;
            while (pos + 2 <= data.Length)
            {
                int type = data[pos];
                int len = data[pos + 1];
                pos += 2;
                if (pos + len > data.Length) break;
                if (type == 1) return Clean(data.Slice(pos, len)); // 1 = AP name
                pos += len;
            }
            return null;
        }

        // Names are NUL-terminated or NUL-padded in some vendors' elements; anything that
        // isn't plain printable text is treated as "no name" rather than shown as garbage.
        private static string? Clean(ReadOnlySpan<byte> raw)
        {
            int nul = raw.IndexOf((byte)0);
            if (nul >= 0) raw = raw.Slice(0, nul);

            var text = Encoding.UTF8.GetString(raw).Trim();
            if (text.Length == 0 || text.Length > 64) return null;
            foreach (var c in text)
            {
                if (char.IsControl(c)) return null;
            }
            return text;
        }
    }
}
