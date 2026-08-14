using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace NetworkWidget
{
    public partial class TracerouteWindow : Window
    {
        private readonly ObservableCollection<HopResult> _hops = new();
        private CancellationTokenSource? _cts;
        private bool _running;

        public TracerouteWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, _) => DwmHelper.ApplyDarkRoundedStyling(this);

            hopList.ItemsSource = _hops;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // Let the window actually close (unlike MainWindow, this one doesn't hide
            // to tray) - just make sure a run in progress doesn't keep pinging after.
            _cts?.Cancel();
        }

        private void txtTarget_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !_running)
            {
                _ = RunAsync();
            }
        }

        private void btnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                _cts?.Cancel();
                return;
            }

            _ = RunAsync();
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            var target = txtTarget.Text.Trim();
            if (string.IsNullOrEmpty(target))
            {
                txtStatus.Text = "Enter a host or IP to trace";
                return;
            }

            _hops.Clear();
            _running = true;
            btnRun.Content = "Cancel";
            txtTarget.IsEnabled = false;
            txtStatus.Text = $"Tracing route to {target}...";

            _cts = new CancellationTokenSource();

            try
            {
                await Traceroute.RunAsync(target, hop => _hops.Add(hop), _cts.Token);
                txtStatus.Text = _cts.Token.IsCancellationRequested
                    ? "Cancelled"
                    : $"Done - {_hops.Count} hop{(_hops.Count == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Failed: {ex.Message}";
            }

            _running = false;
            btnRun.Content = "Run";
            txtTarget.IsEnabled = true;
        }
    }
}
