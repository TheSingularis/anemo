using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Anemo.Widget
{
    // Runs a script elevated via a pre-registered Scheduled Task, so only the one-time
    // task registration prompts for UAC - not every invocation. Shared by every admin
    // action in the widget (Release & Renew, Set Static IP); each caller keeps its own
    // task name and script file so back-to-back actions never stomp on each other.
    internal static class ElevatedTaskRunner
    {
        public static void WriteScript(string scriptPath, string content) => File.WriteAllText(scriptPath, content);

        // Registers the task if it doesn't already exist (prompts UAC once), then runs
        // it and blocks - via schtasks polling - until it's no longer "Running". Returns
        // false only if the user declines the one-time UAC prompt.
        public static bool EnsureRegisteredAndRun(string taskName, string scriptPath)
        {
            if (!TaskExists(taskName) && !TryRegister(taskName, scriptPath)) return false;

            RunCommand("schtasks", $"/run /tn \"{taskName}\"");

            // schtasks /run queues the task and returns immediately, so poll until it's
            // no longer "Running" before returning (max ~15s).
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                var status = RunCommand("schtasks", $"/query /tn \"{taskName}\" /fo LIST");
                if (!status.Contains("Running", StringComparison.OrdinalIgnoreCase)) break;
            }
            return true;
        }

        private static bool TaskExists(string taskName)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/query /tn \"{taskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc!.WaitForExit();
            return proc.ExitCode == 0;
        }

        // /sc ONCE with a start date far in the past registers the task without it ever
        // firing on its own; it only runs when triggered via /run. The doubled inner
        // quotes around the path are schtasks' documented syntax for a /tr target whose
        // path may contain spaces.
        private static bool TryRegister(string taskName, string scriptPath)
        {
            var createArgs = $"/create /tn \"{taskName}\" /tr \"\\\"{scriptPath}\\\"\" /sc ONCE /sd 01/01/2020 /st 00:00 /rl HIGHEST /f";

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = createArgs,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var proc = Process.Start(psi);
                proc!.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch (Win32Exception)
            {
                // UAC prompt was cancelled
                return false;
            }
        }

        public static string RunCommand(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            string output = proc!.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
    }
}
