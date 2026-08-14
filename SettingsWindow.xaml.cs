using System.Windows;
using Anemo.Core;

namespace Anemo.Widget
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private bool _loading;

        public SettingsWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, _) => DwmHelper.ApplyDarkRoundedStyling(this);

            _settings = AppSettings.Load();

            _loading = true;
            chkStartWithWindows.IsChecked = StartupManager.IsEnabled();
            chkStartMinimized.IsChecked = _settings.StartMinimized;
            _loading = false;

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            txtVersion.Text = version == null ? "Version -" : $"Version {version.Major}.{version.Minor}.{version.Build}";
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

        private void chkStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            StartupManager.SetEnabled(chkStartWithWindows.IsChecked == true);
        }

        private void chkStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.StartMinimized = chkStartMinimized.IsChecked == true;
            _settings.Save();
        }
    }
}
