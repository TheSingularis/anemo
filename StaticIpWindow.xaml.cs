using System;
using System.Net;
using System.Windows;
using Anemo.Core;

namespace Anemo.Widget
{
    public partial class StaticIpWindow : Window
    {
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
                var result = await System.Threading.Tasks.Task.Run(() =>
                    ElevatedTaskRunner.Run(ElevatedRequest.StaticIp(_adapterName, address.ToString(), mask, gateway?.ToString())));

                ReportResult(result, "Static IP applied");
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
                var result = await System.Threading.Tasks.Task.Run(() =>
                    ElevatedTaskRunner.Run(ElevatedRequest.Dhcp(_adapterName)));

                ReportResult(result, "Restored to automatic (DHCP)");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", error: true);
            }

            SetButtonsEnabled(true);
        }

        private void ReportResult(ElevatedResult result, string successText)
        {
            if (result.Success) SetStatus(successText, error: false);
            else SetStatus(result.Cancelled ? result.Message : $"Failed: {result.Message}", error: true);
        }

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
