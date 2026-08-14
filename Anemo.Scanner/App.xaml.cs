using System;
using System.Threading.Tasks;
using System.Windows;
using Anemo.Core;

namespace Anemo.Scanner
{
    public partial class App : System.Windows.Application
    {
        public App()
        {
            // Must run before anything else - this is how Velopack intercepts its own
            // install/update/uninstall lifecycle command-line invocations and exits
            // immediately when appropriate, rather than launching the app. Packaged
            // under its own channel (see CI workflow), so its release feed never
            // collides with the widget's even though both live in the same repo.
            Velopack.VelopackApp.Build().Run();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // This is a relaunch right after an update was just applied - the version
            // we'd be checking against was the one we're now running, so skip straight
            // to opening instead of re-running the check-for-updates flow.
            bool skipUpdateCheck = Array.IndexOf(e.Args, AppUpdater.SkipUpdateCheckArg) >= 0;

            if (skipUpdateCheck)
            {
                ShowMainWindow();
                return;
            }

            // Pre-open splash, same pattern as the widget: check, then either open
            // immediately (no update) or show real download progress before opening.
            // ShutdownMode is OnExplicitShutdown (see App.xaml) for exactly this
            // sequence - closing the splash below would otherwise momentarily drop the
            // open-window count to zero before MainWindow exists, triggering WPF's
            // default auto-shutdown-on-last-window-close before we ever get there.
            var splash = new UpdateProgressWindow("Anemo Scanner");
            splash.Show();

            // Only the check itself is time-boxed - if GitHub is slow or unreachable,
            // the app should still open promptly rather than hang on startup. Once an
            // update is actually found, the download is awaited in full (with real
            // progress shown), since that's worth the wait.
            var checkTask = AppUpdater.CheckAsync(status => splash.SetStatus(status));
            var winner = await Task.WhenAny(checkTask, Task.Delay(TimeSpan.FromSeconds(5)));

            if (winner == checkTask)
            {
                var (mgr, info) = checkTask.Result;
                if (info != null)
                {
                    await AppUpdater.DownloadAndApplyAsync(mgr, info,
                        status => splash.SetStatus(status),
                        percent => splash.SetProgress(percent),
                        async version =>
                        {
                            splash.SetStatus($"Updating to v{version}...");
                            await Task.Delay(800);
                        });
                    // Only reached if the update attempt failed (best-effort) - a
                    // successful apply shuts the process down from inside
                    // DownloadAndApplyAsync and never returns here.
                }
            }

            splash.Close();
            ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            var window = new MainWindow();
            window.Show();

            // Back to normal "closing the last window exits the app" behavior now that
            // startup's transient window-count-zero moment is behind us.
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
    }
}
