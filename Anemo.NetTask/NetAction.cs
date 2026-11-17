using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Anemo.NetTask
{
    // Runs elevated (via the Anemo_ElevatedNetworkAction scheduled task) and performs ONE of a
    // fixed set of network actions on behalf of the widget. NetTaskInstaller (in the widget)
    // copied this exe into an admin-only directory (Program Files), so unprivileged code can't
    // alter what the task executes. It's a GUI-subsystem exe that never creates a window, so
    // nothing flashes on screen.
    //
    // The request file, by contrast, sits in the user's profile and must be treated as hostile
    // input: every field is validated against a strict allowlist here, and values are only ever
    // handed to System32 binaries as arguments (never through a shell).
    //
    // Exit codes (read back by the widget from the task's Last Result, see
    // ElevatedTaskRunner.Interpret):
    //   0 ok, 2 malformed request, 3 unknown/unsafe adapter, 4 invalid address values,
    //   5 the underlying command failed
    internal static class NetAction
    {
        // Must match NetTaskInstaller.HelperFlag in the widget.
        public const string Flag = "--net-action";

        public static int Main(string[] args) => Run(args);

        private const int Ok = 0, Malformed = 2, BadAdapter = 3, BadAddress = 4, CommandFailed = 5;

        public static int Run(string[] args)
        {
            try
            {
                int i = Array.IndexOf(args, Flag);
                if (i < 0 || i + 1 >= args.Length) return Malformed;
                return Execute(args[i + 1]);
            }
            catch
            {
                return CommandFailed;
            }
        }

        private static int Execute(string requestFile)
        {
            // ---- read + shape-check the request ---------------------------------------------
            JsonElement req;
            string action, adapterName;
            try
            {
                if (new FileInfo(requestFile).Length > 4096) return Malformed;
                using var doc = JsonDocument.Parse(File.ReadAllText(requestFile));
                req = doc.RootElement.Clone();
                if (req.ValueKind != JsonValueKind.Object) return Malformed;
                action = GetString(req, "action") ?? "";
                adapterName = GetString(req, "adapter") ?? "";
                if (action.Length == 0 || adapterName.Length == 0) return Malformed;
            }
            catch
            {
                return Malformed;
            }

            if (action != "renew" && action != "static" && action != "dhcp") return Malformed;

            // ---- adapter: must exactly match a real interface, and be safe to pass on --------
            // (ipconfig treats * and ? as wildcards, so "release everything" must never be expressible.)
            string? adapter = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (string.Equals(nic.Name, adapterName, StringComparison.Ordinal)) { adapter = nic.Name; break; }
            }
            if (adapter == null) return BadAdapter;
            foreach (var c in adapter)
            {
                if (c < 0x20 || c == '"' || c == '\\' || c == '*' || c == '?') return BadAdapter;
            }

            // ---- run the one action ---------------------------------------------------------
            switch (action)
            {
                case "renew":
                    // Release can legitimately fail (static/disconnected adapter); the renew result decides.
                    RunSystem32("ipconfig.exe", $"/release \"{adapter}\"");
                    return RunSystem32("ipconfig.exe", $"/renew \"{adapter}\"") == 0 ? Ok : CommandFailed;

                case "static":
                {
                    var ip = ParseIPv4(req, "ip");
                    var mask = ParseIPv4(req, "mask");
                    if (ip == null || mask == null || !IsSubnetMask(mask)) return BadAddress;
                    int first = ip.GetAddressBytes()[0];
                    if (first < 1 || first > 223 || first == 127) return BadAddress;

                    var cmd = $"interface ip set address name=\"{adapter}\" static {ip} {mask}";
                    if (req.TryGetProperty("gateway", out var gwProp) && gwProp.ValueKind != JsonValueKind.Null)
                    {
                        var gw = ParseIPv4(req, "gateway");
                        if (gw == null || gw.GetAddressBytes()[0] == 0) return BadAddress;
                        cmd += $" {gw} 1";
                    }
                    return RunSystem32("netsh.exe", cmd) == 0 ? Ok : CommandFailed;
                }

                case "dhcp":
                {
                    int rc1 = RunSystem32("netsh.exe", $"interface ip set address name=\"{adapter}\" dhcp");
                    int rc2 = RunSystem32("netsh.exe", $"interface ip set dns name=\"{adapter}\" dhcp");
                    return rc1 == 0 && rc2 == 0 ? Ok : CommandFailed;
                }
            }
            return Malformed;
        }

        private static string? GetString(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        // Only canonical dotted quads: rejects shorthand/odd spellings ("1", "0x7f.1", leading zeros...).
        private static IPAddress? ParseIPv4(JsonElement obj, string name)
        {
            var text = GetString(obj, name);
            if (text == null || !IPAddress.TryParse(text, out var addr)) return null;
            if (addr.AddressFamily != AddressFamily.InterNetwork) return null;
            return addr.ToString() == text ? addr : null;
        }

        // Contiguous ones followed by zeros, at least one leading one.
        private static bool IsSubnetMask(IPAddress mask)
        {
            var b = mask.GetAddressBytes();
            uint value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            if (value == 0) return false;
            uint inverted = ~value;
            return (inverted & (inverted + 1)) == 0;
        }

        // The adapter name was checked above to contain no quotes, backslashes or control
        // characters, so wrapping it in quotes can't break out of its argument.
        private static int RunSystem32(string exe, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode;
        }
    }
}
