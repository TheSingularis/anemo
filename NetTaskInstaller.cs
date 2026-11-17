using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Anemo.Widget
{
    // The elevated half of ElevatedTaskRunner's one-time setup. The widget re-launches its own
    // exe with Flag (UAC prompt) and App.OnStartup calls Run before any UI exists.
    //
    // It copies the Anemo.NetTask.exe helper that ships next to the widget into the admin-only
    // install directory and registers an on-demand (trigger-less) HIGHEST-privilege Scheduled
    // Task that runs exactly that copy, for the widget's own user (passed as a SID, because the elevated identity can
    // be a different admin account when UAC asks for credentials). It also removes the older
    // per-action tasks that ran a user-writable .bat out of %TEMP%, and the PowerShell
    // script that 1.3.5-1.3.8 installed (it flashed a console window on every click).
    //
    // Everything it writes lives under Program Files, and its two inputs are validated, so a
    // hostile argument can't redirect it. Failures write a message to setup-error.txt in the
    // install directory (admin-only, so it can't be forged) and return a non-zero exit code:
    //   20 bad arguments, 21 couldn't install the helper exe, 22 couldn't register the task.
    internal static class NetTaskInstaller
    {
        public const string Flag = "--install-nettask";

        // Must match NetAction.Flag in Anemo.NetTask.
        private const string HelperFlag = "--net-action";

        private static readonly string[] LegacyTasks = { "Anemo_ReleaseRenew", "Anemo_SetStaticIp" };

        public static int Run(string[] args)
        {
            try { File.Delete(ElevatedTaskRunner.ErrorLogPath); } catch { /* best effort */ }

            string sid, requestPath;
            try
            {
                sid = ValidateSid(GetArg(args, "--sid"));
                requestPath = ValidateRequestPath(GetArg(args, "--request"));
            }
            catch (Exception ex)
            {
                return Fail(20, "Invalid setup arguments: " + ex.Message);
            }

            try
            {
                var source = Path.Combine(AppContext.BaseDirectory, ElevatedTaskRunner.HelperFileName);
                if (!File.Exists(source)) throw new FileNotFoundException($"{ElevatedTaskRunner.HelperFileName} is missing next to the widget");
                Directory.CreateDirectory(ElevatedTaskRunner.InstallDir);
                File.Copy(source, ElevatedTaskRunner.HelperPath, overwrite: true);
                try { File.Delete(Path.Combine(ElevatedTaskRunner.InstallDir, "AnemoNetTask.ps1")); } catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                return Fail(21, "Couldn't install the helper: " + ex.Message);
            }

            foreach (var legacy in LegacyTasks)
                ElevatedTaskRunner.Exec("schtasks", $"/delete /tn \"{legacy}\" /f");

            var xmlPath = Path.Combine(ElevatedTaskRunner.InstallDir, "task.xml");
            try
            {
                File.WriteAllText(xmlPath, BuildTaskXml(sid, requestPath), Encoding.Unicode);
                var (exit, output) = ElevatedTaskRunner.Exec("schtasks", $"/create /tn \"{ElevatedTaskRunner.TaskName}\" /xml \"{xmlPath}\" /f");
                if (exit != 0) return Fail(22, $"schtasks /create failed ({exit}): {output.Trim()}");

                // Records which request file the task was registered for, so the widget can tell
                // when it needs re-registering (e.g. a different user profile).
                File.WriteAllText(ElevatedTaskRunner.TargetPath, requestPath);
                // Which widget build the helper copy came from, so an update re-installs it.
                File.WriteAllText(ElevatedTaskRunner.BuildIdPath, ElevatedTaskRunner.BuildId);
                return 0;
            }
            catch (Exception ex)
            {
                return Fail(22, "Couldn't register the task: " + ex.Message);
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { /* best effort */ }
            }
        }

        private static string BuildTaskXml(string sid, string requestPath)
        {
            var arguments = HelperFlag + " \"" + requestPath + "\"";

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(sid)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(ElevatedTaskRunner.HelperPath)}</Command>
      <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
    </Exec>
  </Actions>
</Task>";
        }

        private static string? GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string ValidateSid(string? value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("missing --sid");
            return new SecurityIdentifier(value).Value; // throws on anything that isn't a SID
        }

        private static string ValidateRequestPath(string? value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("missing --request");
            if (value.Contains('"') || !Path.IsPathFullyQualified(value) ||
                Path.GetFullPath(value) != value ||
                !string.Equals(Path.GetFileName(value), "request.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("unexpected request path");
            return value;
        }

        private static int Fail(int code, string message)
        {
            try
            {
                Directory.CreateDirectory(ElevatedTaskRunner.InstallDir);
                File.WriteAllText(ElevatedTaskRunner.ErrorLogPath, message);
            }
            catch { /* nowhere to report it */ }
            return code;
        }
    }
}
