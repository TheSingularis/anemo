using System;
using System.Threading.Tasks;
using System.Windows;

namespace NetworkWidget
{
    public partial class App : System.Windows.Application
    {
        public App()
        {
            // Must run before anything else - this is how Velopack intercepts its
            // own install/update/uninstall lifecycle command-line invocations and
            // exits immediately when appropriate, rather than launching the app.
            Velopack.VelopackApp.Build().Run();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // This is a relaunch right after an update was just applied (see
            // AppUpdater.SkipUpdateCheckArg) - the version we'd be checking against was
            // the one we're now running, so skip straight to opening.
            bool skipUpdateCheck = Array.IndexOf(e.Args, AppUpdater.SkipUpdateCheckArg) >= 0;

            // "Start minimized" is a persisted preference (set from the Settings pane),
            // not a launch flag, so it applies whether the app was auto-started or opened
            // by hand.
            bool startMinimized = AppSettings.Load().StartMinimized;
            if (startMinimized)
            {
                // No window to show progress in - check silently in the background,
                // same as the periodic 6h re-check does.
                if (!skipUpdateCheck)
                {
                    _ = AppUpdater.CheckAndApplyAsync();
                }
                new MainWindow();
                return;
            }

            if (skipUpdateCheck)
            {
                var freshWindow = new MainWindow();
                freshWindow.Show();
                return;
            }

            // Pre-open splash, like Discord: check, then either open immediately (no
            // update) or show real download progress before opening (update found).
            // Not folded into MainWindow's own content - they're sequential, not
            // simultaneous, so there's nothing to "merge."
            var splash = new UpdateProgressWindow();
            splash.Show();

            // Only the check itself is time-boxed - if GitHub is slow or unreachable,
            // the widget should still open promptly rather than hang on startup. Once
            // an update is actually found, the download is awaited in full (with real
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
                    // successful apply exits the process from inside
                    // ApplyUpdatesAndRestart and never returns here.
                }
            }

            splash.Close();

            var window = new MainWindow();
            window.Show();
        }
    }
}
