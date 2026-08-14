using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Anemo.Core;

namespace Anemo.Scanner
{
    public partial class MainWindow : Window
    {
        private sealed record ActivityEntry(string Time, string Message);

        private readonly ObservableCollection<DiscoveredDevice> _devices = new();
        private readonly ObservableCollection<NearbyNetwork> _networks = new();
        private readonly ObservableCollection<PortResult> _openPorts = new();
        private readonly ObservableCollection<HopResult> _hops = new();
        private readonly ObservableCollection<ActivityEntry> _activity = new();

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
            activityList.ItemsSource = _activity;

            Loaded += (_, _) =>
            {
                LoadDashboard();
                LoadSettings();
            };
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

        // Newest first, capped so the card doesn't grow without bound over a long session.
        private const int MaxActivityEntries = 20;

        private void LogActivity(string message)
        {
            _activity.Insert(0, new ActivityEntry(DateTime.Now.ToString("h:mm tt"), message));
            while (_activity.Count > MaxActivityEntries) _activity.RemoveAt(_activity.Count - 1);
            txtNoActivity.Visibility = Visibility.Collapsed;
        }

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
                }, _devicesCts.Token, localMac: details.Mac);

                txtDevicesStatus.Text = _devicesCts.Token.IsCancellationRequested
                    ? $"Cancelled — {_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found"
                    : $"Subnet {details.Ipv4}/{details.SubnetPrefixLength} — {_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found";
                txtDashDevices.Text = _devices.Count.ToString();
                if (!_devicesCts.Token.IsCancellationRequested)
                {
                    LogActivity($"Device scan found {_devices.Count} device{(_devices.Count == 1 ? "" : "s")} on {details.Ipv4}/{details.SubnetPrefixLength}");
                }
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
            LogActivity($"Wi-Fi scan found {networks.Count} nearby network{(networks.Count == 1 ? "" : "s")}");
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
        // Speed Test
        // -------------------------------------------------------------

        private bool _speedTestRunning;
        private readonly Queue<double> _graphHistory = new();
        private DateTime _lastGraphSample;
        private const int GraphHistoryLength = 40;

        // Progress callbacks fire on every chunk (potentially many times a second on a
        // fast link) - sampling on a fixed cadence keeps the graph smooth instead of
        // redrawing far more often than the eye can actually use.
        private void ResetGraph(string label)
        {
            _graphHistory.Clear();
            _lastGraphSample = DateTime.MinValue;
            txtGraphLabel.Text = label;
            speedGraph.Points.Clear();
        }

        private void SampleGraph(double mbps)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastGraphSample).TotalMilliseconds < 150) return;
            _lastGraphSample = now;

            _graphHistory.Enqueue(mbps);
            while (_graphHistory.Count > GraphHistoryLength) _graphHistory.Dequeue();

            if (_graphHistory.Count < 2) return;

            double width = speedGraph.ActualWidth > 0 ? speedGraph.ActualWidth : 472;
            double height = speedGraph.Height;
            double max = Math.Max(_graphHistory.Max(), 1);

            var samples = _graphHistory.ToArray();
            var points = new System.Windows.Media.PointCollection(samples.Length);
            for (int i = 0; i < samples.Length; i++)
            {
                double x = width * i / (samples.Length - 1);
                double y = height - (samples[i] / max * height);
                points.Add(new System.Windows.Point(x, y));
            }
            speedGraph.Points = points;
        }

        private async void btnStartSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            if (_speedTestRunning) return;
            _speedTestRunning = true;

            btnStartSpeedTest.IsEnabled = false;
            txtDownloadResult.Text = "0.0";
            txtUploadResult.Text = "0.0";
            txtPingResult.Text = "--";
            ResetGraph("");

            try
            {
                txtSpeedStatus.Text = "Finding a server...";
                var servers = await SpeedTestClient.GetServersAsync();
                var server = await SpeedTestClient.PickBestServerAsync(servers);
                if (server == null)
                {
                    txtSpeedStatus.Text = "No reachable test server found";
                    return;
                }

                txtSpeedStatus.Text = $"Testing against {server.Name}...";

                var ping = await SpeedTestClient.PingTestAsync(server);
                txtPingResult.Text = ping >= 0 ? $"{ping:0}" : "--";

                txtSpeedStatus.Text = $"Testing download — {server.Name}";
                ResetGraph("DOWNLOAD Mbps");
                var download = await SpeedTestClient.DownloadTestAsync(server, mbps =>
                {
                    txtDownloadResult.Text = mbps.ToString("0.0");
                    SampleGraph(mbps);
                });

                txtSpeedStatus.Text = $"Testing upload — {server.Name}";
                ResetGraph("UPLOAD Mbps");
                var upload = await SpeedTestClient.UploadTestAsync(server, mbps =>
                {
                    txtUploadResult.Text = mbps.ToString("0.0");
                    SampleGraph(mbps);
                });

                txtDownloadResult.Text = download.ToString("0.0");
                txtUploadResult.Text = upload.ToString("0.0");
                txtSpeedStatus.Text = $"Done — {server.Name}";
                LogActivity($"Speed test: {download:0.#} Mbps down / {upload:0.#} Mbps up / {txtPingResult.Text} ms ping ({server.Name})");
            }
            catch (Exception ex)
            {
                txtSpeedStatus.Text = $"Test failed: {ex.Message}";
            }

            btnStartSpeedTest.IsEnabled = true;
            _speedTestRunning = false;
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
                if (!_portsCts.Token.IsCancellationRequested)
                {
                    LogActivity($"Port scan of {target} ({from}-{to}) found {_openPorts.Count} open port{(_openPorts.Count == 1 ? "" : "s")}");
                }
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
                if (!_traceCts.Token.IsCancellationRequested)
                {
                    LogActivity($"Traceroute to {target} completed in {_hops.Count} hop{(_hops.Count == 1 ? "" : "s")}");
                }
            }
            catch (Exception ex)
            {
                txtTraceStatus.Text = $"Failed: {ex.Message}";
            }

            btnRunTrace.Content = "Run";
            txtTraceTarget.IsEnabled = true;
            _traceCts = null;
        }

        // -------------------------------------------------------------
        // Settings
        // -------------------------------------------------------------

        private void LoadSettings()
        {
            var settings = AppSettings.Load();
            txtPortFrom.Text = settings.DefaultPortFrom.ToString();
            txtPortTo.Text = settings.DefaultPortTo.ToString();
            txtSettingsPortFrom.Text = settings.DefaultPortFrom.ToString();
            txtSettingsPortTo.Text = settings.DefaultPortTo.ToString();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            txtVersion.Text = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "-";
        }

        private void btnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtSettingsPortFrom.Text.Trim(), out var from)
                || !int.TryParse(txtSettingsPortTo.Text.Trim(), out var to)
                || from < 1 || to > 65535 || from > to)
            {
                txtSettingsStatus.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");
                txtSettingsStatus.Text = "Enter a valid port range (1-65535)";
                return;
            }

            var settings = new AppSettings { DefaultPortFrom = from, DefaultPortTo = to };
            settings.Save();

            txtPortFrom.Text = from.ToString();
            txtPortTo.Text = to.ToString();

            txtSettingsStatus.Foreground = (System.Windows.Media.Brush)FindResource("GoodBrush");
            txtSettingsStatus.Text = "Saved";
        }

        private bool _checkingForUpdates;

        private async void btnCheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_checkingForUpdates) return;
            _checkingForUpdates = true;
            btnCheckForUpdates.IsEnabled = false;

            var splash = new UpdateProgressWindow("Anemo Scanner");
            splash.Show();

            var (mgr, info) = await AppUpdater.CheckAsync(status => splash.SetStatus(status));
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
                // successful apply shuts the process down and never returns here.
                txtUpdateStatus.Text = "Update failed - see status above";
            }
            else
            {
                txtUpdateStatus.Text = mgr.IsInstalled ? "Up to date" : "Not managed by the updater";
            }

            splash.Close();
            btnCheckForUpdates.IsEnabled = true;
            _checkingForUpdates = false;
        }
    }
}
