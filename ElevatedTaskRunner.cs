using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Anemo.Widget
{
    internal sealed record ElevatedRequest(string Action, string Adapter, string? Ip = null, string? Mask = null, string? Gateway = null)
    {
        public static ElevatedRequest Renew(string adapter) => new("renew", adapter);
        public static ElevatedRequest StaticIp(string adapter, string ip, string mask, string? gateway) => new("static", adapter, ip, mask, gateway);
        public static ElevatedRequest Dhcp(string adapter) => new("dhcp", adapter);
    }

    // Cancelled = the user declined the one-time UAC prompt; otherwise Success/Message
    // describe what the elevated script reported.
    internal readonly record struct ElevatedResult(bool Cancelled, bool Success, string Message)
    {
        public static readonly ElevatedResult Ok = new(false, true, "");
        public static readonly ElevatedResult UserCancelled = new(true, false, "Setup cancelled");
        public static ElevatedResult Fail(string message) => new(false, false, message);
    }

    // Runs the widget's admin actions (Release & Renew, Set Static IP, Restore DHCP) through
    // one pre-registered, HIGHEST-privilege Scheduled Task, so only the one-time install
    // prompts for UAC - not every click.
    //
    // What the elevated task runs is a fixed PowerShell script (Scripts/AnemoNetTask.ps1)
    // installed under Program Files, which unprivileged processes can't modify. The only
    // thing that varies per click is a small JSON request in the user's profile; the script
    // treats it as untrusted and validates every field (see the script header). Nothing the
    // user can write is ever executed.
    internal static class ElevatedTaskRunner
    {
        private const string TaskName = "Anemo_ElevatedNetworkAction";
        private const string ScriptResource = "Anemo.Widget.Scripts.AnemoNetTask.ps1";
        private const string InstallerResource = "Anemo.Widget.Scripts.InstallNetTask.ps1";

        // SCHED_S_TASK_RUNNING - what schtasks reports as "Last Result" while a run is in flight.
        private const int TaskRunningResult = 267009;

        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Anemo", "NetTask");
        private static readonly string ScriptPath = Path.Combine(InstallDir, "AnemoNetTask.ps1");
        private static readonly string TargetPath = Path.Combine(InstallDir, "request-path.txt");

        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anemo.Widget", "netaction");
        private static readonly string RequestPath = Path.Combine(DataDir, "request.json");

        // One request file, one task: serialize so back-to-back actions never stomp on each other.
        private static readonly object Gate = new();

        public static ElevatedResult Run(ElevatedRequest request)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DataDir);
                CleanUpLegacyTempFiles();

                var payload = ReadResource(ScriptResource);
                if (!IsInstalled(payload))
                {
                    var install = Install(payload);
                    if (install != null) return install.Value;
                }

                try
                {
                    File.WriteAllText(RequestPath, JsonSerializer.Serialize(new
                    {
                        action = request.Action,
                        adapter = request.Adapter,
                        ip = request.Ip,
                        mask = request.Mask,
                        gateway = request.Gateway
                    }));
                    return RunTask();
                }
                finally
                {
                    try { File.Delete(RequestPath); } catch { /* best effort */ }
                }
            }
        }

        // Installed = the script on disk is byte-identical to the one this build ships (so an
        // app update re-installs the new script - one more UAC prompt), the request path it
        // was registered for is still ours, and the task exists.
        private static bool IsInstalled(byte[] payload)
        {
            try
            {
                if (!File.Exists(ScriptPath) || !File.Exists(TargetPath)) return false;
                if (!File.ReadAllBytes(ScriptPath).AsSpan().SequenceEqual(payload)) return false;
                if (File.ReadAllText(TargetPath) != RequestPath) return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }

            return Exec("schtasks", $"/query /tn \"{TaskName}\"").ExitCode == 0;
        }

        // Returns null on success, otherwise the result to hand back to the caller.
        private static ElevatedResult? Install(byte[] payload)
        {
            // The elevated side runs only what matches this hash: the installer text sits in a
            // user-writable file until UAC is accepted, so it's verified in memory first and
            // the verified bytes are what get executed - there's no window to swap it.
            var installer = new StringBuilder()
                .AppendLine($"$PayloadB64 = {PsQuote(Convert.ToBase64String(payload))}")
                .AppendLine($"$ScriptPath = {PsQuote(ScriptPath)}")
                .AppendLine($"$TargetPath = {PsQuote(TargetPath)}")
                .AppendLine($"$TaskName = {PsQuote(TaskName)}")
                .AppendLine($"$RequestFile = {PsQuote(RequestPath)}")
                .AppendLine(Encoding.UTF8.GetString(ReadResource(InstallerResource)))
                .ToString();
            var installerBytes = new UTF8Encoding(false).GetBytes(installer);
            var hash = Convert.ToHexString(SHA256.HashData(installerBytes));

            var stagedPath = Path.Combine(DataDir, $"install-{Guid.NewGuid():N}.ps1");
            File.WriteAllBytes(stagedPath, installerBytes);

            try
            {
                var bootstrap =
                    $"$b=[IO.File]::ReadAllBytes({PsQuote(stagedPath)});" +
                    "$h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');" +
                    $"if($h -ne '{hash}'){{exit 9}};" +
                    "Invoke-Expression([Text.Encoding]::UTF8.GetString($b))";

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)),
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                proc!.WaitForExit();
                return proc.ExitCode == 0 ? null : ElevatedResult.Fail($"Setup failed (exit code {proc.ExitCode})");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the UAC prompt was declined
                return ElevatedResult.UserCancelled;
            }
            finally
            {
                try { File.Delete(stagedPath); } catch { /* best effort */ }
            }
        }

        private static ElevatedResult RunTask()
        {
            var before = QueryTask();

            if (Exec("schtasks", $"/run /tn \"{TaskName}\"").ExitCode != 0)
                return ElevatedResult.Fail("Couldn't start the elevated task");

            // schtasks /run only queues the task, so poll until a new run has started and
            // finished (max ~30s). Keyed off numeric Last Result / the Last Run Time changing
            // rather than the localized "Running" status text.
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                var now = QueryTask();
                if (now == null) continue;
                bool started = before == null || now.LastRun != before.LastRun;
                if (started && now.Result != TaskRunningResult) return Interpret(now.Result);
            }
            return ElevatedResult.Fail("Timed out waiting for the elevated task");
        }

        // Exit codes are defined in AnemoNetTask.ps1.
        private static ElevatedResult Interpret(int result) => result switch
        {
            0 => ElevatedResult.Ok,
            2 => ElevatedResult.Fail("The request was rejected as malformed"),
            3 => ElevatedResult.Fail("That adapter wasn't found"),
            4 => ElevatedResult.Fail("The IP settings were rejected as invalid"),
            5 => ElevatedResult.Fail("Windows reported an error applying the change"),
            _ => ElevatedResult.Fail($"The elevated task failed (code {result})")
        };

        private sealed record TaskStatus(string LastRun, int Result);

        // schtasks /v CSV columns: HostName, TaskName, Next Run Time, Status, Logon Mode,
        // Last Run Time, Last Result, ... (headers are localized, so go by position).
        private static TaskStatus? QueryTask()
        {
            var (exit, output) = Exec("schtasks", $"/query /tn \"{TaskName}\" /fo CSV /v /nh");
            if (exit != 0) return null;

            var fields = SplitCsvLine(output.Trim().Split('\n')[0].TrimEnd('\r'));
            if (fields.Count < 7) return null;

            var text = fields[6].Trim();
            int result;
            bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result)
                : int.TryParse(text, out result);
            return parsed ? new TaskStatus(fields[5], result) : null;
        }

        private static System.Collections.Generic.List<string> SplitCsvLine(string line)
        {
            var fields = new System.Collections.Generic.List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            foreach (var c in line)
            {
                if (c == '"') inQuotes = !inQuotes;
                else if (c == ',' && !inQuotes) { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        // Earlier versions ran a .bat out of %TEMP% (with a log next to it); sweep those up.
        private static void CleanUpLegacyTempFiles()
        {
            foreach (var name in new[] { "netwidget_renew_task.bat", "netwidget_renew.log", "netwidget_staticip_task.bat", "netwidget_staticip.log" })
            {
                try { File.Delete(Path.Combine(Path.GetTempPath(), name)); } catch { /* best effort */ }
            }
        }

        private static byte[] ReadResource(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource {name}");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

        private static (int ExitCode, string Output) Exec(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            // Drain stderr asynchronously so a chatty failure can't fill the pipe and deadlock.
            proc!.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, output);
        }
    }
}
