using System.Windows;

namespace Anemo.Core
{
    public partial class UpdateProgressWindow : Window
    {
        private bool _closed;

        public UpdateProgressWindow(string appName = "Anemo Widget")
        {
            InitializeComponent();

            Title = appName;
            txtTitleBar.Text = appName;

            SourceInitialized += (_, _) => DwmHelper.ApplyDarkRoundedStyling(this);
            Closed += (_, _) => _closed = true;
        }

        public void SetStatus(string text)
        {
            // Velopack invokes its progress/status callbacks as plain delegates from
            // whatever thread is doing the actual download work, not necessarily the UI
            // thread - unlike IProgress<T>, a raw Action<T> is never auto-marshaled, so
            // touching UI elements directly here can throw cross-thread and silently
            // abort whatever loop called us (e.g. cutting a download short mid-stream).
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => SetStatus(text));
                return;
            }
            if (_closed) return;
            txtStatus.Text = text;
        }

        // Shows/updates a determinate progress bar for the download phase specifically
        // (0-100); the spinner keeps running throughout regardless, as a generic
        // "still working" indicator for the checking/installing phases either side of it.
        public void SetProgress(int percent)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => SetProgress(percent));
                return;
            }
            if (_closed) return;
            progressTrack.Visibility = Visibility.Visible;
            progressFill.Width = progressTrack.ActualWidth * System.Math.Clamp(percent, 0, 100) / 100.0;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
