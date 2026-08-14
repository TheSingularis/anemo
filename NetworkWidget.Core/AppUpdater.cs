using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace NetworkWidget.Core
{
    // Shared by every app in this repo - they all live in the same GitHub repo/releases
    // list, but each is packaged under its own Velopack channel (see vpk pack --channel
    // in the CI workflow), which is what actually keeps their release feeds from
    // colliding. This class has no app-specific state; the channel isolation happens
    // entirely on the packaging side.
    public static class AppUpdater
    {
        private const string RepoUrl = "https://github.com/TheSingularis/NetworkWidget";

        // Passed back to the relaunched process via restartArgs after an update is
        // applied, so the caller can open straight to the app instead of running the
        // check-for-updates flow all over again on a version that was *just* checked.
        public const string SkipUpdateCheckArg = "--skip-update-check";

        // Split from DownloadAndApplyAsync so a caller can time-box just the check
        // (e.g. don't hold up app startup more than a few seconds if GitHub is slow)
        // while still letting an in-progress download run to completion once one is
        // actually found, since real percentage feedback justifies the wait.
        public static async Task<(UpdateManager manager, UpdateInfo? info)> CheckAsync(Action<string>? onStatus = null)
        {
            var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
            var mgr = new UpdateManager(source);

            try
            {
                onStatus?.Invoke("Checking for updates...");

                // Not running from a Velopack-managed install (e.g. a dev build launched
                // straight from bin\Release) - there's nothing sensible to update in place.
                if (!mgr.IsInstalled)
                {
                    onStatus?.Invoke("Not managed by the updater");
                    return (mgr, null);
                }

                var updateInfo = await mgr.CheckForUpdatesAsync();
                onStatus?.Invoke(updateInfo == null ? "Up to date" : "Update available");
                return (mgr, updateInfo);
            }
            catch
            {
                // Best-effort: a network hiccup or GitHub rate limit should never affect
                // normal app operation.
                onStatus?.Invoke("Couldn't check for updates");
                return (mgr, null);
            }
        }

        // onDownloadProgress, if given, is called with 0-100 as the download proceeds.
        // onUpdateApplying, if given, is awaited after the download finishes but before
        // the restart that actually swaps the new version in - the process exits inside
        // WaitExitThenApplyUpdates, so this is the last chance to tell the user anything.
        public static async Task DownloadAndApplyAsync(
            UpdateManager mgr,
            UpdateInfo updateInfo,
            Action<string>? onStatus = null,
            Action<int>? onDownloadProgress = null,
            Func<string, Task>? onUpdateApplying = null)
        {
            try
            {
                onStatus?.Invoke("Downloading update...");
                await mgr.DownloadUpdatesAsync(updateInfo, onDownloadProgress);

                onStatus?.Invoke("Installing update...");
                if (onUpdateApplying != null)
                {
                    await onUpdateApplying(updateInfo.TargetFullRelease.Version.ToString());
                }

                // silent: true suppresses Velopack's own native progress window - our
                // splash already shows status/progress, so that second window was just
                // a redundant, jarring extra step. WaitExitThenApplyUpdates (unlike
                // ApplyUpdatesAndRestart) doesn't exit the process for us, so we do
                // that explicitly right after.
                mgr.WaitExitThenApplyUpdates(
                    updateInfo.TargetFullRelease,
                    silent: true,
                    restart: true,
                    restartArgs: new[] { SkipUpdateCheckArg });
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                onStatus?.Invoke($"Update failed: {ex.Message}");
                await Task.Delay(2500);
            }
        }

        // Convenience wrapper for callers that don't need to separately time-box the
        // check phase (e.g. a periodic background check, a manual "check for updates"
        // menu click).
        public static async Task CheckAndApplyAsync(
            Action<string>? onStatus = null,
            Action<int>? onDownloadProgress = null,
            Func<string, Task>? onUpdateApplying = null)
        {
            var (mgr, info) = await CheckAsync(onStatus);
            if (info == null) return;
            await DownloadAndApplyAsync(mgr, info, onStatus, onDownloadProgress, onUpdateApplying);
        }
    }
}
