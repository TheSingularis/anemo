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

        // For a single lookup (the Wi-Fi access point the widget is connected to), where
        // keeping the whole ~40k-entry table in memory (roughly 5 MB) for one answer isn't
        // worth it: the embedded list is scanned once instead. Uses the table if something
        // else (e.g. a device scan) already loaded it.
        public static string LookupSingle(string mac)
        {
            if (Table.IsValueCreated) return Lookup(mac);

            var prefix = NormalizePrefix(mac);
            return prefix == null ? "Unknown" : Scan(prefix);
        }

        // Same answer as the table: a prefix listed more than once resolves to its last entry.
        internal static string Scan(string prefix)
        {
            using var stream = typeof(OuiVendors).Assembly.GetManifestResourceStream("Anemo.Core.oui.csv");
            if (stream == null) return "Unknown";

            string? match = null;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length > prefix.Length && line[prefix.Length] == ','
                    && line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    match = line[(prefix.Length + 1)..];
                }
            }
            return match ?? "Unknown";
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
