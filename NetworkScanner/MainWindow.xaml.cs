using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetworkWidget.Core;

namespace NetworkScanner
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<DiscoveredDevice> _devices = new();
        private readonly ObservableCollection<NearbyNetwork> _networks = new();
        private readonly ObservableCollection<PortResult> _openPorts = new();
        private readonly ObservableCollection<HopResult> _hops = new();

        private CancellationTokenSource? _devicesCts;
        private CancellationTokenSource? _portsCts;
        private CancellationTokenSource? _traceCts;

        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, _) => DwmHelper.ApplyDarkRoundedStyling(this);

            devicesList.ItemsSource = _devices;
            networksList.ItemsSource = _networks;
            portsList.ItemsSource = _openPorts;
            hopsList.ItemsSource = _hops;

            Loaded += (_, _) => LoadDashboard();
        }

        // -------------------------------------------------------------
        // Window chrome
        // -------------------------------------------------------------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else DragMove();
            }
        }

        private void btnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void btnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // -------------------------------------------------------------
        // Sidebar navigation
        // -------------------------------------------------------------

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var clicked = (Button)sender;

            foreach (var btn in new[] { navDashboard, navDevices, navWifi, navSpeed, navPorts, navTraceroute, navSettings })
            {
                btn.Tag = btn == clicked ? "selected" : null;
            }

            foreach (var page in new[] { pageDashboard, pageDevices, pageWifi, pageSpeed, pagePorts, pageTraceroute, pageSettings })
            {
                page.Visibility = Visibility.Collapsed;
            }

            var targetPage = clicked.Name switch
            {
                nameof(navDashboard) => pageDashboard,
                nameof(navDevices) => pageDevices,
                nameof(navWifi) => pageWifi,
                nameof(navSpeed) => pageSpeed,
                nameof(navPorts) => pagePorts,
                nameof(navTraceroute) => pageTraceroute,
                nameof(navSettings) => pageSettings,
                _ => pageDashboard,
            };
            targetPage.Visibility = Visibility.Visible;
        }

        // -------------------------------------------------------------
        // Dashboard
        // -------------------------------------------------------------

        private void LoadDashboard()
        {
            var nic = NetworkInfo.GetDefaultInterface();
            if (nic != null)
            {
                var details = NetworkInfo.GetAdapterDetails(nic);
                txtDashIp.Text = details.Ipv4;
                txtDashGateway.Text = details.Gateway ?? "-";
            }

            var wifi = WifiInfo.GetCurrent();
            txtDashWifi.Text = wifi.Connected ? $"{wifi.SignalPercent} ({wifi.RssiText})" : "Not connected";
        }

        // -------------------------------------------------------------
        // Devices
        // -------------------------------------------------------------

        private async void btnScanDevices_Click(object sender, RoutedEventArgs e)
        {
            if (_devicesCts != null)
            {
                _devicesCts.Cancel();
                return;
            }

            var nic = NetworkInfo.GetDefaultInterface();
            if (nic == null)
            {
                txtDevicesStatus.Text = "No active network adapter found";
                return;
            }

            var details = NetworkInfo.GetAdapterDetails(nic);
            _devices.Clear();
            btnScanDevices.Content = "Cancel";
            txtDevicesStatus.Text = $"Scanning {details.Ipv4}/{details.SubnetPrefixLength}...";

            _devicesCts = new CancellationTokenSource();
            try
            {
                await DeviceScan.ScanAsync(details.Ipv4, details.SubnetPrefixLength, device =>
                {
                    _devices.Add(device);
                    txtDevicesStatus.Text = $"Subnet {details.Ipv4}/{details.SubnetPrefixLength} — {_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found";
                }, _devicesCts.Token);

                txtDevicesStatus.Text = _devicesCts.Token.IsCancellationRequested
                    ? $"Cancelled — {_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found"
                    : $"Subnet {details.Ipv4}/{details.SubnetPrefixLength} — {_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found";
                txtDashDevices.Text = _devices.Count.ToString();
            }
            catch (Exception ex)
            {
                txtDevicesStatus.Text = $"Scan failed: {ex.Message}";
            }

            btnScanDevices.Content = "Scan Network";
            _devicesCts = null;
        }

        // -------------------------------------------------------------
        // Wi-Fi Analyzer
        // -------------------------------------------------------------

        private async void btnScanWifi_Click(object sender, RoutedEventArgs e)
        {
            btnScanWifi.IsEnabled = false;
            txtWifiStatus.Text = "Scanning...";

            var networks = await Task.Run(() => WifiInfo.GetNearbyNetworks());

            _networks.Clear();
            foreach (var n in networks) _networks.Add(n);

            UpdateChannelChart(networks);

            txtWifiStatus.Text = $"{networks.Count} network{(networks.Count == 1 ? "" : "s")} found";
            btnScanWifi.IsEnabled = true;
        }

        private void UpdateChannelChart(System.Collections.Generic.IReadOnlyList<NearbyNetwork> networks)
        {
            var bars = new[] { chBar1, chBar2, chBar3, chBar4, chBar5, chBar6, chBar7, chBar8, chBar9, chBar10, chBar11 };

            // Only channels 1-11 (2.4 GHz) are charted here - 5/6 GHz networks use a much
            // wider, non-overlapping channel set that doesn't fit the same "congestion"
            // framing, so they're excluded from this specific chart (they still show up
            // in the Nearby Networks list below).
            var countsByChannel = networks
                .Where(n => n.Channel is >= 1 and <= 11)
                .GroupBy(n => n.Channel)
                .ToDictionary(g => g.Key, g => g.Count());

            int max = countsByChannel.Values.DefaultIfEmpty(0).Max();

            for (int ch = 1; ch <= 11; ch++)
            {
                int count = countsByChannel.GetValueOrDefault(ch, 0);
                bars[ch - 1].Height = max == 0 ? 0 : Math.Max(count > 0 ? 6 : 0, 105.0 * count / max);
                bars[ch - 1].Fill = count == max && max > 0
                    ? (System.Windows.Media.Brush)FindResource("BadBrush")
                    : (System.Windows.Media.Brush)FindResource("AccentBrush");
            }

            if (max == 0)
            {
                txtWifiAdvice.Text = "No 2.4 GHz networks detected.";
            }
            else
            {
                var busiest = countsByChannel.OrderByDescending(kv => kv.Value).First().Key;
                var quietest = Enumerable.Range(1, 11).OrderBy(ch => countsByChannel.GetValueOrDefault(ch, 0)).First();
                txtWifiAdvice.Text = $"Channel {busiest} is the most congested ({countsByChannel[busiest]} network{(countsByChannel[busiest] == 1 ? "" : "s")}) — channel {quietest} looks quietest.";
            }
        }

        // -------------------------------------------------------------
        // Port Scanner
        // -------------------------------------------------------------

        private async void btnScanPorts_Click(object sender, RoutedEventArgs e)
        {
            if (_portsCts != null)
            {
                _portsCts.Cancel();
                return;
            }

            var target = txtPortTarget.Text.Trim();
            if (string.IsNullOrEmpty(target))
            {
                txtPortsStatus.Text = "Enter a host or IP to scan";
                return;
            }
            if (!int.TryParse(txtPortFrom.Text.Trim(), out var from) || !int.TryParse(txtPortTo.Text.Trim(), out var to) || from < 1 || to > 65535 || from > to)
            {
                txtPortsStatus.Text = "Enter a valid port range (1-65535)";
                return;
            }

            _openPorts.Clear();
            btnScanPorts.Content = "Cancel";
            txtPortsStatus.Text = $"Scanning {target}:{from}-{to}...";

            _portsCts = new CancellationTokenSource();
            try
            {
                await PortScan.ScanAsync(target, from, to, result =>
                {
                    if (result.IsOpen)
                    {
                        _openPorts.Add(result);
                        txtPortsStatus.Text = $"Scanning {target}:{from}-{to}... {_openPorts.Count} open";
                    }
                }, _portsCts.Token);

                txtPortsStatus.Text = _portsCts.Token.IsCancellationRequested
                    ? $"Cancelled — {_openPorts.Count} open port{(_openPorts.Count == 1 ? "" : "s")} found"
                    : $"Done — {_openPorts.Count} open port{(_openPorts.Count == 1 ? "" : "s")} found";
            }
            catch (Exception ex)
            {
                txtPortsStatus.Text = $"Scan failed: {ex.Message}";
            }

            btnScanPorts.Content = "Scan";
            _portsCts = null;
        }

        // -------------------------------------------------------------
        // Traceroute
        // -------------------------------------------------------------

        private async void btnRunTrace_Click(object sender, RoutedEventArgs e)
        {
            if (_traceCts != null)
            {
                _traceCts.Cancel();
                return;
            }

            var target = txtTraceTarget.Text.Trim();
            if (string.IsNullOrEmpty(target))
            {
                txtTraceStatus.Text = "Enter a host or IP to trace";
                return;
            }

            _hops.Clear();
            btnRunTrace.Content = "Cancel";
            txtTraceTarget.IsEnabled = false;
            txtTraceStatus.Text = $"Tracing route to {target}...";

            _traceCts = new CancellationTokenSource();
            try
            {
                await Traceroute.RunAsync(target, hop => _hops.Add(hop), _traceCts.Token);
                txtTraceStatus.Text = _traceCts.Token.IsCancellationRequested
                    ? "Cancelled"
                    : $"Done — {_hops.Count} hop{(_hops.Count == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                txtTraceStatus.Text = $"Failed: {ex.Message}";
            }

            btnRunTrace.Content = "Run";
            txtTraceTarget.IsEnabled = true;
            _traceCts = null;
        }
    }
}
