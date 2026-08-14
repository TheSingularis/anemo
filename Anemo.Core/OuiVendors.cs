using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Anemo.Core
{
    // Backed by an embedded copy of the official IEEE OUI registry
    // (https://standards-oui.ieee.org/oui/oui.txt), trimmed to just prefix,vendor pairs.
    public static class OuiVendors
    {
        private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

        public static string Lookup(string mac)
        {
            var prefix = NormalizePrefix(mac);
            if (prefix == null) return "Unknown";
            return Table.Value.TryGetValue(prefix, out var vendor) ? vendor : "Unknown";
        }

        private static string? NormalizePrefix(string mac)
        {
            var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            return hex.Length >= 6 ? hex[..6] : null;
        }

        private static Dictionary<string, string> Load()
        {
            var dict = new Dictionary<string, string>();
            var asm = typeof(OuiVendors).Assembly;

            using var stream = asm.GetManifestResourceStream("Anemo.Core.oui.csv");
            if (stream == null) return dict;

            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var idx = line.IndexOf(',');
                if (idx <= 0) continue;
                dict[line[..idx]] = line[(idx + 1)..];
            }
            return dict;
        }
    }
}
