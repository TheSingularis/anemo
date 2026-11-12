using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Anemo.Core
{
    // "Report a bug" support shared by both apps. It opens GitHub's new-issue page in the
    // user's browser with the title/body prefilled; nothing is sent anywhere until the user
    // reviews it and presses Submit on github.com. Deliberately token-free: creating issues
    // straight through the API would need a credential shipped inside the exe (or a backend).
    public static class BugReport
    {
        public const string NewIssueUrl = "https://github.com/TheSingularis/anemo/issues/new";

        // Browsers/GitHub reject very long URLs, so cap the free-form diagnostic text.
        internal const int MaxDiagnosticsLength = 1500;

        public static string BuildUrl(string appName, string appVersion, string? diagnostics = null)
        {
            var body = new StringBuilder();
            body.AppendLine("**What happened?**");
            body.AppendLine();
            body.AppendLine();
            body.AppendLine("**What did you expect to happen?**");
            body.AppendLine();
            body.AppendLine();
            body.AppendLine("**Steps to reproduce**");
            body.AppendLine();
            body.AppendLine();
            body.AppendLine("---");
            body.AppendLine($"- App: {appName} {appVersion}");
            body.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
            body.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}");

            var extra = diagnostics?.Trim();
            if (!string.IsNullOrEmpty(extra))
            {
                if (extra.Length > MaxDiagnosticsLength) extra = extra[..MaxDiagnosticsLength] + "...";
                body.AppendLine();
                body.AppendLine("<details><summary>Diagnostics</summary>");
                body.AppendLine();
                body.AppendLine("```");
                body.AppendLine(extra);
                body.AppendLine("```");
                body.AppendLine("</details>");
            }

            return $"{NewIssueUrl}?title={Uri.EscapeDataString($"[{appName}] ")}" +
                   $"&body={Uri.EscapeDataString(body.ToString())}&labels=bug";
        }

        // Returns false if no browser could be launched (caller can tell the user).
        public static bool Open(string appName, string appVersion, string? diagnostics = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(BuildUrl(appName, appVersion, diagnostics)) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        // "1.3.7" from the entry assembly, matching what the Settings screens display.
        public static string CurrentVersion()
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return v == null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
