using System;
using System.Net;
using System.Windows;
using Anemo.Core;

namespace Anemo.Widget
{
    public partial class StaticIpWindow : Window
    {
        private const string TaskName = "Anemo_SetStaticIp";
        private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netwidget_staticip.log");
        private static readonly string ScriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netwidget_staticip_task.bat");

        private readonly string _adapterName;

        public StaticIpWindow(string adapterName, string? currentCidr, string? currentGateway)
        {
            InitializeComponent();

            SourceInitialized += (_, _) => DwmHelper.ApplyDarkRoundedStyling(this);

            _adapterName = adapterName;
            txtAdapter.Text = $"Adapter: {adapterName}";
            if (currentCidr != null) txtCidr.Text = currentCidr;
            if (currentGateway != null) txtGateway.Text = currentGateway;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();

        private async void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (!NetworkInfo.TryParseCidr(txtCidr.Text, out var address, out var prefixLength))
            {
                SetStatus("Enter a valid IP/subnet, e.g. 192.168.1.50/24", error: true);
                return;
            }

            IPAddress? gateway = null;
            var gatewayText = txtGateway.Text.Trim();
            if (gatewayText.Length > 0 && (!IPAddress.TryParse(gatewayText, out gateway) || gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            {
                SetStatus("Gateway isn't a valid IPv4 address", error: true);
                return;
            }

            var mask = NetworkInfo.PrefixLengthToSubnetMask(prefixLength);

            SetStatus("Applying...", error: false, busy: true);
            SetButtonsEnabled(false);

            try
            {
                bool ran = await System.Threading.Tasks.Task.Run(() =>
                {
                    ElevatedTaskRunner.WriteScript(ScriptPath, BuildApplyScript(address.ToString(), mask, gateway?.ToString()));
                    return ElevatedTaskRunner.EnsureRegisteredAndRun(TaskName, ScriptPath);
                });

                SetStatus(ran ? "Static IP applied" : "Setup cancelled", error: !ran);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", error: true);
            }

            SetButtonsEnabled(true);
        }

        private async void btnRestoreDhcp_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Restoring DHCP...", error: false, busy: true);
            SetButtonsEnabled(false);

            try
            {
                bool ran = await System.Threading.Tasks.Task.Run(() =>
                {
                    ElevatedTaskRunner.WriteScript(ScriptPath, BuildDhcpScript());
                    return ElevatedTaskRunner.EnsureRegisteredAndRun(TaskName, ScriptPath);
                });

                SetStatus(ran ? "Restored to automatic (DHCP)" : "Setup cancelled", error: !ran);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", error: true);
            }

            SetButtonsEnabled(true);
        }

        // Redirection/quoting doesn't survive being embedded inline in schtasks' /tr
        // value, so - same as Release & Renew - the actual commands live in a script
        // file and /tr just points at that single, stable path; only the file's
        // content changes per click.
        private string BuildApplyScript(string ip, string mask, string? gateway)
        {
            var setAddress = gateway == null
                ? $"netsh interface ip set address name=\"{_adapterName}\" static {ip} {mask}"
                : $"netsh interface ip set address name=\"{_adapterName}\" static {ip} {mask} {gateway} 1";

            return "@echo off\r\n" +
                   $"{setAddress} > \"{LogPath}\" 2>&1\r\n";
        }

        private string BuildDhcpScript() =>
            "@echo off\r\n" +
            $"netsh interface ip set address name=\"{_adapterName}\" dhcp > \"{LogPath}\" 2>&1\r\n" +
            $"netsh interface ip set dns name=\"{_adapterName}\" dhcp >> \"{LogPath}\" 2>&1\r\n";

        private void SetStatus(string text, bool error, bool busy = false)
        {
            txtStatus.Text = text;
            txtStatus.Foreground = error ? System.Windows.Media.Brushes.IndianRed
                : busy ? System.Windows.Media.Brushes.Orange
                : System.Windows.Media.Brushes.LightGreen;
        }

        private void SetButtonsEnabled(bool enabled)
        {
            btnApply.IsEnabled = enabled;
            btnRestoreDhcp.IsEnabled = enabled;
        }
    }
}
