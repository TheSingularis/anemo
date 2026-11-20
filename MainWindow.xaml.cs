using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Anemo.Core;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Anemo.Widget
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer = new();
        private readonly DispatcherTimer _trafficTimer = new();
        private readonly DispatcherTimer _connectivityTimer = new();
        private readonly DispatcherTimer _updateTimer = new();
        private readonly DispatcherTimer _wifiTimer = new();
        private readonly DispatcherTimer _networkChangeDebounce = new();
        private WinForms.NotifyIcon? _trayIcon;
        private bool _exiting;
        private string? _selectedAdapterId;
        private string? _currentGatewayAddress;
        private IPAddress? _currentIpv4;

        // How the adapter shown is chosen. Until the user picks one from the dropdown, the
        // widget follows the connection actually in use (and jumps to a cable as soon as one
        // is plugged in). A manual pick sticks until that adapter goes down or a new cable
        // is plugged in.
        private bool _userPickedAdapter;
        private bool _updatingAdapterList;
        private string? _justPluggedAdapterId;
        private System.Collections.Generic.HashSet<string>? _knownAdapterIds;
        private string _adapterListKey = "";

        // Identifies "which connection the numbers belong to" (adapter + address + gateway).
        // Whenever it changes, the ping/public-IP results from the previous connection are
        // cleared and re-measured, and any lookup still in flight for the old one is ignored.
        // Starts as a value no real connection can have, so the first read always triggers
        // a connectivity check.
        private string? _connectionKey = "\0";
        private int _connectionGeneration;

        // Right after a network change Windows is still settling (DHCP, DAD, link speed
        // negotiation), so local info is re-read every second for a short while instead of
        // waiting for the normal 5s tick.
        private static readonly TimeSpan NormalRefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SettlingRefreshInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan SettlingDuration = TimeSpan.FromSeconds(15);
        private DateTime _settlingUntil;

        private string? _trafficAdapterId;
        private long _prevBytesReceived;
        private long _prevBytesSent;
        private DateTime _prevTrafficSample;
        private const int TrafficHistoryLength = 40; // ~40s of history at the 1s traffic tick
        private readonly System.Collections.Generic.Queue<double> _rxHistory = new();
        private readonly System.Collections.Generic.Queue<double> _txHistory = new();

        private sealed class AdapterOption
        {
            public required string Id { get; init; }
            public required string Name { get; init; }
            public override string ToString() => Name;
        }

        public MainWindow()
        {
            InitializeComponent();

            // Must happen once the native HWND exists (SourceInitialized), before the
            // window is shown, so it never flashes square corners or a light system border.
            SourceInitialized += (_, _) => DwmHelper.ApplyDarkRoundedStyling(this);

            // With SizeToContent="Height", ActualHeight isn't final until after layout,
            // so position once the window has been laid out rather than at SourceInitialized.
            Loaded += (_, _) => PositionNearCursor();

            SetupTrayIcon();

            _timer.Interval = NormalRefreshInterval;
            _timer.Tick += (_, _) => RefreshNetworkInfo();
            _timer.Start();

            // Windows raises these the moment an address is added or removed (cable in/out,
            // Wi-Fi join/leave, DHCP finishing, VPN up/down), so the display reacts right
            // away instead of showing the previous connection's values until the next
            // poll. They arrive in bursts on a thread-pool thread, hence the short debounce
            // on the UI thread.
            _networkChangeDebounce.Interval = TimeSpan.FromMilliseconds(300);
            _networkChangeDebounce.Tick += (_, _) =>
            {
                _networkChangeDebounce.Stop();
                OnNetworkChanged();
            };
            NetworkChange.NetworkAddressChanged += NetworkChange_Changed;
            NetworkChange.NetworkAvailabilityChanged += NetworkChange_Changed;

            // Traffic only reads local interface byte counters (no process spawn, no
            // network call), so it's cheap enough to run every second for a genuinely
            // "live" feel, on its own timer separate from the WiFi check below - that
            // one spawns netsh.exe each time, which is real overhead we don't want at
            // a 1s cadence.
            _trafficTimer.Interval = TimeSpan.FromSeconds(1);
            _trafficTimer.Tick += (_, _) => UpdateTraffic();
            _trafficTimer.Start();

            // Pings and the public-IP lookup are real network round-trips, so they run on
            // their own slower cadence rather than piling onto the 5s local-info refresh.
            _connectivityTimer.Interval = TimeSpan.FromSeconds(30);
            _connectivityTimer.Tick += (_, _) => _ = UpdateConnectivityAsync();
            _connectivityTimer.Start();

            // The initial check-on-launch lives in App.xaml.cs (shown in the startup
            // progress window); this timer just covers re-checks for a widget that
            // stays running for a long time between restarts.
            _updateTimer.Interval = TimeSpan.FromHours(6);
            _updateTimer.Tick += (_, _) => _ = CheckForUpdatesInBackgroundAsync();
            _updateTimer.Start();

            // Querying WiFi details (SSID/signal/RSSI) touches the same Windows APIs
            // as location lookups, so Windows lights up the taskbar location indicator
            // every time - polling only while the widget is actually visible means it
            // stays off entirely while minimized to tray (most of the time), and a
            // fast 1s interval while visible keeps the indicator continuously lit
            // instead of visibly blinking on/off.
            _wifiTimer.Interval = TimeSpan.FromSeconds(1);
            _wifiTimer.Tick += (_, _) => UpdateWifiInfo();
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    // Everything is re-read the moment the widget is opened, so it never
                    // shows what was on screen when it was last hidden.
                    RefreshNetworkInfo();
                    RedrawSparkline(sparkDownload, _rxHistory);
                    RedrawSparkline(sparkUpload, _txHistory);
                    UpdateWifiInfo();
                    _wifiTimer.Start();
                }
                else
                {
                    _wifiTimer.Stop();
                }
            };

            RefreshNetworkInfo();
            UpdateTraffic();
        }

        private void NetworkChange_Changed(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _networkChangeDebounce.Stop();
                _networkChangeDebounce.Start();
            });
        }

        private void OnNetworkChanged()
        {
            if (_exiting) return;
            _settlingUntil = DateTime.UtcNow + SettlingDuration;
            _timer.Interval = SettlingRefreshInterval;
            RefreshNetworkInfo();
            if (IsVisible) UpdateWifiInfo();
        }

        // Screen.WorkingArea is in physical pixels; Window.Left/Top are DPI-independent
        // units, so on a scaled monitor the two must not be mixed directly or the window
        // ends up placed outside every monitor's bounds while still reporting as visible.
        private void PositionNearCursor()
        {
            var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = area.Right / dpi.DpiScaleX - ActualWidth - 20;
            Top = area.Bottom / dpi.DpiScaleY - ActualHeight - 20;
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

        // -------------------------------------------------------------
        // Tray icon
        // -------------------------------------------------------------

        private void SetupTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule!.FileName!),
                Text = "Anemo Widget",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show/Hide", null, (_, _) => ToggleVisibility());
            menu.Items.Add("Refresh Now", null, (_, _) => RefreshNetworkInfo());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
            menu.Items.Add("Check for Updates", null, (_, _) => CheckForUpdatesWithProgress());
            menu.Items.Add("Report a Bug...", null, (_, _) => SettingsWindow.ReportBug());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());

            _trayIcon.ContextMenuStrip = menu;

            // NotifyIcon.Click fires for every mouse button, which would fight with the
            // right-click context menu; MouseClick exposes which button was actually used.
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    ToggleVisibility();
                }
            };
        }

        // Used by the periodic background timer. Routine "nothing to do" checks stay
        // invisible, but once an update is actually found the same splash as the startup and
        // manual paths is shown: a widget that's already running would otherwise download,
        // vanish and reappear with nothing on screen in between (a tray balloon alone is
        // easy to miss or is suppressed by Focus Assist).
        private async Task CheckForUpdatesInBackgroundAsync()
        {
            if (_checkingForUpdates) return;
            _checkingForUpdates = true;

            UpdateProgressWindow? splash = null;
            try
            {
                var (mgr, info) = await AppUpdater.CheckAsync();
                if (info == null) return;

                var window = new UpdateProgressWindow("Anemo Widget");
                splash = window;
                window.Show();

                await AppUpdater.DownloadAndApplyAsync(mgr, info,
                    status => window.SetStatus(status),
                    percent => window.SetProgress(percent),
                    async version =>
                    {
                        window.SetStatus($"Updating to v{version}...");
                        await Task.Delay(800);
                    });
                // Only reached if the update attempt failed (best-effort) - a successful
                // apply shuts the process down from inside DownloadAndApplyAsync.
            }
            finally
            {
                splash?.Close();
                _checkingForUpdates = false;
            }
        }

        // Used for the manual "Check for Updates" tray click - same pre-open-style
        // splash window App.xaml.cs uses at startup, not folded into this window's
        // own content, so both entry points behave identically.
        private bool _checkingForUpdates;

        private async void CheckForUpdatesWithProgress()
        {
            if (_checkingForUpdates) return;
            _checkingForUpdates = true;

            var splash = new UpdateProgressWindow("Anemo Widget");
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
                // Only reached if the update attempt failed (best-effort, logged via
                // status above) - a successful apply shuts the process down from
                // inside DownloadAndApplyAsync and never returns here.
            }

            splash.Close();
            _checkingForUpdates = false;
        }

        private SettingsWindow? _settingsWindow;

        private void OpenSettings()
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }

            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void ToggleVisibility()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
                Activate();
            }
        }

        private void ExitApp()
        {
            _exiting = true;
            _timer.Stop();
            NetworkChange.NetworkAddressChanged -= NetworkChange_Changed;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_Changed;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            Close();
            System.Windows.Application.Current.Shutdown();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                Hide();
            }
        }

        // -------------------------------------------------------------
        // Network info gathering
        // -------------------------------------------------------------

        private void RefreshNetworkInfo()
        {
            if (_timer.Interval != NormalRefreshInterval && DateTime.UtcNow >= _settlingUntil)
            {
                _timer.Interval = NormalRefreshInterval;
            }

            try
            {
                UpdateAdapterList();
                UpdateIpInfo();
                // UpdateWifiInfo() runs on its own visibility-gated timer, see constructor.
                txtUpdated.Text = $"Last updated: {DateTime.Now:HH:mm:ss}";
                if (txtStatus.Text.StartsWith(RefreshErrorPrefix)) txtStatus.Text = "";
            }
            catch (Exception ex)
            {
                // Reading adapter info can throw while an adapter is disappearing (e.g. a
                // USB Ethernet dongle being pulled). Never leave the old values on screen
                // looking current when that happens.
                ClearIpInfo();
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                txtStatus.Text = $"{RefreshErrorPrefix}{ex.Message}";
            }
        }

        private const string RefreshErrorPrefix = "Error refreshing: ";

        // Byte counters are cumulative, not a rate - so a live rate needs a delta
        // between two samples. The baseline resets whenever the selected adapter
        // changes, since byte counts aren't comparable across different adapters.
        private void UpdateTraffic()
        {
            var nic = NetworkInfo.GetActiveInterfaces().FirstOrDefault(n => n.Id == _selectedAdapterId);
            if (nic == null)
            {
                txtDownload.Text = "-";
                txtUpload.Text = "-";
                _trafficAdapterId = null;
                _rxHistory.Clear();
                _txHistory.Clear();
                sparkDownload.Points.Clear();
                sparkUpload.Points.Clear();
                return;
            }

            var stats = nic.GetIPv4Statistics();
            var now = DateTime.UtcNow;

            if (_trafficAdapterId != nic.Id)
            {
                _trafficAdapterId = nic.Id;
                _prevBytesReceived = stats.BytesReceived;
                _prevBytesSent = stats.BytesSent;
                _prevTrafficSample = now;
                txtDownload.Text = "-";
                txtUpload.Text = "-";
                // Byte counts (and therefore history) aren't comparable across adapters.
                _rxHistory.Clear();
                _txHistory.Clear();
                sparkDownload.Points.Clear();
                sparkUpload.Points.Clear();
                return;
            }

            var elapsedSeconds = (now - _prevTrafficSample).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                var rxBytesPerSec = Math.Max(0, stats.BytesReceived - _prevBytesReceived) / elapsedSeconds;
                var txBytesPerSec = Math.Max(0, stats.BytesSent - _prevBytesSent) / elapsedSeconds;
                txtDownload.Text = FormatRate(rxBytesPerSec);
                txtUpload.Text = FormatRate(txBytesPerSec);

                PushSample(_rxHistory, rxBytesPerSec);
                PushSample(_txHistory, txBytesPerSec);
                // While hidden in the tray the history keeps filling, but the graph is only
                // rebuilt when someone can see it (and once more as the widget is shown).
                if (IsVisible)
                {
                    RedrawSparkline(sparkDownload, _rxHistory);
                    RedrawSparkline(sparkUpload, _txHistory);
                }
            }

            _prevBytesReceived = stats.BytesReceived;
            _prevBytesSent = stats.BytesSent;
            _prevTrafficSample = now;
        }

        private static void PushSample(System.Collections.Generic.Queue<double> history, double value)
        {
            history.Enqueue(value);
            while (history.Count > TrafficHistoryLength) history.Dequeue();
        }

        // Auto-scaled to the current window's own peak (like Task Manager's network
        // graph) rather than a fixed max, so it stays readable at any traffic level
        // instead of looking flat during light use or clipping during a burst.
        private static void RedrawSparkline(System.Windows.Shapes.Polyline line, System.Collections.Generic.Queue<double> history)
        {
            if (history.Count < 2)
            {
                line.Points.Clear();
                return;
            }

            double width = line.ActualWidth > 0 ? line.ActualWidth : 64;
            double height = line.Height;
            double max = Math.Max(history.Max(), 1);

            var samples = history.ToArray();
            var points = new System.Windows.Media.PointCollection(samples.Length);
            for (int i = 0; i < samples.Length; i++)
            {
                double x = width * i / (samples.Length - 1);
                double y = height - (samples[i] / max * height);
                points.Add(new System.Windows.Point(x, y));
            }

            line.Points = points;
        }

        private static string FormatRate(double bytesPerSecond)
        {
            double kbps = bytesPerSecond / 1024.0;
            return kbps >= 1024 ? $"{kbps / 1024.0:0.#} MB/s" : $"{kbps:0.#} KB/s";
        }

        // Pings and the public-IP lookup are real network calls, so this runs on the
        // slower 30s _connectivityTimer rather than the 5s local-info timer.
        private async Task UpdateConnectivityAsync()
        {
            int generation = _connectionGeneration;
            var sourceIp = _currentIpv4;

            var gatewayTask = Connectivity.PingAsync(_currentGatewayAddress);
            var internetTask = Connectivity.PingAsync("1.1.1.1");
            // Looked up through the adapter being shown, so a second connection (e.g. Wi-Fi
            // still up next to a just-plugged cable) can't supply the answer. No usable
            // address on this adapter means no public IP through it either.
            var publicIpTask = sourceIp == null
                ? Task.FromResult("-")
                : Connectivity.GetPublicIpAsync(sourceIp, TimeSpan.FromSeconds(5));

            await Task.WhenAll(gatewayTask, internetTask, publicIpTask);

            // The connection changed while these were in flight - the results describe the
            // previous network, and a fresh round has already been started for the new one.
            if (generation != _connectionGeneration) return;

            SetPingResult(txtGatewayPing, gatewayTask.Result);
            SetPingResult(txtInternetPing, internetTask.Result);
            txtPublicIp.Text = publicIpTask.Result;
        }

        // Called whenever the connection being shown changes. The old results are replaced
        // with a placeholder immediately and re-measured, rather than lingering for up to
        // 30s until the next connectivity tick.
        private void OnConnectionIdentityChanged()
        {
            _connectionGeneration++;
            SetPingResult(txtGatewayPing, Checking);
            SetPingResult(txtInternetPing, Checking);
            txtPublicIp.Text = Checking;
            _connectivityTimer.Stop();
            _connectivityTimer.Start();
            _ = UpdateConnectivityAsync();
        }

        private const string Checking = "Checking...";

        // Reuses the same muted green already used for the Release & Renew success
        // state, paired with an equally muted red - kept subtle rather than alarming.
        private static readonly System.Windows.Media.Brush PingOkBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0xB8, 0x5C));
        private static readonly System.Windows.Media.Brush PingFailBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x53, 0x4F));
        private static readonly System.Windows.Media.Brush DefaultValueBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE));

        private static void SetPingResult(System.Windows.Controls.TextBlock target, string result)
        {
            target.Text = result;
            target.Foreground = result.EndsWith(" ms") ? PingOkBrush
                : result == "unreachable" ? PingFailBrush
                : DefaultValueBrush;
        }

        // Keeps the dropdown in sync with whatever adapters are currently up, without
        // clobbering the user's selection on every 5s tick unless it's no longer valid.
        private void UpdateAdapterList()
        {
            var excluded = AppSettings.Load().ExcludedAdapterIds;
            var allActive = NetworkInfo.GetActiveInterfaces().ToList();
            var active = allActive.Where(n => !excluded.Contains(n.Id)).ToList();
            var options = active
                .Select(n => new AdapterOption { Id = n.Id, Name = n.Name })
                .ToList();
            var ids = new System.Collections.Generic.HashSet<string>(options.Select(o => o.Id));

            // A wired adapter that has just come up means a cable was just plugged in: that's
            // the connection the user wants to see, even before DHCP has given it an address
            // (or if it never does - "no DHCP on this port" is exactly what they're checking).
            // Not applied on the very first pass, when everything is "new". Tracked across all
            // adapters (excluded ones too), so un-excluding one in Settings isn't mistaken
            // for a cable being plugged in.
            var justPlugged = _knownAdapterIds == null ? null : active
                .FirstOrDefault(n => NetworkInfo.AdapterTypePriority(n) == 0 && !_knownAdapterIds.Contains(n.Id));
            _knownAdapterIds = new System.Collections.Generic.HashSet<string>(allActive.Select(n => n.Id));

            if (_justPluggedAdapterId != null && !ids.Contains(_justPluggedAdapterId)) _justPluggedAdapterId = null;
            if (justPlugged != null)
            {
                _justPluggedAdapterId = justPlugged.Id;
                _userPickedAdapter = false;
            }
            if (_userPickedAdapter && (_selectedAdapterId == null || !ids.Contains(_selectedAdapterId)))
            {
                _userPickedAdapter = false;
            }

            if (options.Count == 0)
            {
                SetAdapterOptions(null, null);
                _selectedAdapterId = null;
                return;
            }

            if (!_userPickedAdapter)
            {
                // Prefer whichever of Ethernet/WiFi actually has a working route, wired
                // over wireless - matches how Windows itself deprioritizes WiFi once a
                // cable is plugged in, so this naturally tracks "the one really in use"
                // rather than whatever GetAllNetworkInterfaces() happens to list first.
                // Re-evaluated on every refresh so moving between connections is followed.
                var defaultId = NetworkInfo.GetDefaultInterface()?.Id;
                _selectedAdapterId = _justPluggedAdapterId
                    ?? (defaultId != null && ids.Contains(defaultId) ? defaultId : options[0].Id);
            }

            SetAdapterOptions(options, _selectedAdapterId);
        }

        // Only replaces the dropdown's items when the set of adapters actually changed, so
        // an open dropdown isn't snapped shut by a routine refresh. Programmatic changes are
        // flagged so they aren't mistaken for the user picking an adapter.
        private void SetAdapterOptions(System.Collections.Generic.List<AdapterOption>? options, string? selectedId)
        {
            _updatingAdapterList = true;
            try
            {
                var key = options == null ? "" : string.Join("|", options.Select(o => o.Id + "=" + o.Name));
                if (key != _adapterListKey)
                {
                    _adapterListKey = key;
                    cmbAdapter.ItemsSource = options;
                }

                var items = cmbAdapter.ItemsSource as System.Collections.Generic.List<AdapterOption>;
                var selected = items?.FirstOrDefault(o => o.Id == selectedId);
                if (!ReferenceEquals(cmbAdapter.SelectedItem, selected)) cmbAdapter.SelectedItem = selected;
            }
            finally
            {
                _updatingAdapterList = false;
            }
        }

        private void cmbAdapter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_updatingAdapterList) return;
            if (cmbAdapter.SelectedItem is AdapterOption opt)
            {
                _userPickedAdapter = true;
                _justPluggedAdapterId = null;
                _selectedAdapterId = opt.Id;
                UpdateIpInfo();
            }
        }

        private void ClearIpInfo()
        {
            txtLinkSpeed.Text = "-";
            txtIPv4.Text = "-";
            txtSubnet.Text = "-";
            txtGateway.Text = "-";
            txtDNS.Text = "-";
            txtMAC.Text = "-";
            _currentGatewayAddress = null;
            _currentIpv4 = null;
            SetConnectionKey(null);
        }

        private void SetConnectionKey(string? key)
        {
            if (key == _connectionKey) return;
            _connectionKey = key;
            OnConnectionIdentityChanged();
        }

        private void UpdateIpInfo()
        {
            var nic = NetworkInfo.GetActiveInterfaces().FirstOrDefault(n => n.Id == _selectedAdapterId);

            if (nic == null)
            {
                ClearIpInfo();
                return;
            }

            // A fresh snapshot is read from Windows on every call (nothing here is cached),
            // so these always describe the adapter as it is right now.
            var details = NetworkInfo.GetAdapterDetails(nic);

            txtLinkSpeed.Text = details.LinkSpeedText;
            txtMAC.Text = details.Mac;
            txtDNS.Text = details.DnsServers.Count > 0 ? string.Join(", ", details.DnsServers) : "-";

            switch (details.Ipv4Status)
            {
                case Ipv4Status.Pending:
                    // Link is up but DHCP hasn't answered yet - say so instead of a blank.
                    txtIPv4.Text = "Acquiring...";
                    txtSubnet.Text = "-";
                    break;
                case Ipv4Status.Autoconfigured:
                    txtIPv4.Text = $"{details.Ipv4} (no DHCP)";
                    txtSubnet.Text = $"/{details.SubnetPrefixLength}";
                    break;
                default:
                    txtIPv4.Text = details.Ipv4;
                    txtSubnet.Text = $"/{details.SubnetPrefixLength}";
                    break;
            }
            txtGateway.Text = details.Gateway ?? "-";

            _currentGatewayAddress = details.Gateway;
            _currentIpv4 = details.Ipv4Status == Ipv4Status.Pending ? null : IPAddress.Parse(details.Ipv4);
            SetConnectionKey($"{nic.Id}|{details.Ipv4}|{details.Gateway}");
        }

        private void UpdateWifiInfo()
        {
            var wifi = WifiInfo.GetCurrent();

            // Collapsed rather than shown-with-dashes when not connected via WiFi (e.g.
            // on Ethernet) - otherwise the section is just dead space in the widget.
            wifiSection.Visibility = wifi.Connected ? Visibility.Visible : Visibility.Collapsed;
            if (!wifi.Connected) return;

            txtSSID.Text = wifi.Ssid;
            txtAP.Text = wifi.AccessPoint;
            txtBSSID.Text = wifi.Bssid;
            txtSignal.Text = JoinWifiParts(wifi.SignalPercent, wifi.RssiText);
            txtChannel.Text = JoinWifiParts(wifi.Channel, wifi.Band, wifi.RadioType);
            txtSecurity.Text = wifi.Authentication;
        }

        // Related Wi-Fi values share a row (e.g. "36 · 5 GHz · 802.11ax"); any part that
        // couldn't be read is left out rather than shown as a stray dash.
        private static string JoinWifiParts(params string[] parts)
        {
            var known = parts.Where(p => !string.IsNullOrWhiteSpace(p) && p != "-").ToArray();
            return known.Length == 0 ? "-" : string.Join(" · ", known);
        }

        // -------------------------------------------------------------
        // Release & Renew (elevated via ElevatedTaskRunner - a pre-registered
        // Scheduled Task, so only the one-time task registration prompts for
        // UAC, not every click)
        // -------------------------------------------------------------

        private async void btnRenew_Click(object sender, RoutedEventArgs e)
        {
            var adapterName = (cmbAdapter.SelectedItem as AdapterOption)?.Name;
            if (adapterName == null)
            {
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                txtStatus.Text = "No adapter selected";
                return;
            }

            txtStatus.Foreground = System.Windows.Media.Brushes.Orange;
            txtStatus.Text = "Releasing and renewing...";
            btnRenew.IsEnabled = false;

            try
            {
                // All of this (elevation prompt, process launches, and the poll loop
                // below) is blocking I/O - it must run off the UI thread or the whole
                // window stops pumping messages and appears to freeze/blank out.
                //
                // Only the adapter name is sent per click; what actually runs elevated is a
                // fixed, admin-protected script (see ElevatedTaskRunner).
                var result = await System.Threading.Tasks.Task.Run(() =>
                    ElevatedTaskRunner.Run(ElevatedRequest.Renew(adapterName)));
                if (!result.Success)
                {
                    txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    txtStatus.Text = result.Cancelled ? result.Message : $"Failed: {result.Message}";
                    btnRenew.IsEnabled = true;
                    return;
                }

                txtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                txtStatus.Text = "Renewed successfully";
            }
            catch (Exception ex)
            {
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                txtStatus.Text = $"Failed: {ex.Message}";
            }

            btnRenew.IsEnabled = true;
            RefreshNetworkInfo();
        }

        // -------------------------------------------------------------
        // Set Static IP
        // -------------------------------------------------------------

        private void btnStaticIp_Click(object sender, RoutedEventArgs e)
        {
            var adapterName = (cmbAdapter.SelectedItem as AdapterOption)?.Name;
            if (adapterName == null)
            {
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                txtStatus.Text = "No adapter selected";
                return;
            }

            // Prefill from the adapter's current config so the dialog opens with
            // something sensible to edit rather than a blank/placeholder guess.
            var nic = NetworkInfo.GetActiveInterfaces().FirstOrDefault(n => n.Id == _selectedAdapterId);
            string? currentCidr = null;
            string? currentGateway = null;
            if (nic != null)
            {
                var details = NetworkInfo.GetAdapterDetails(nic);
                if (details.Ipv4 != "-") currentCidr = $"{details.Ipv4}/{details.SubnetPrefixLength}";
                currentGateway = details.Gateway;
            }

            var dialog = new StaticIpWindow(adapterName, currentCidr, currentGateway) { Owner = this };
            dialog.ShowDialog();
            RefreshNetworkInfo();
        }
    }
}
