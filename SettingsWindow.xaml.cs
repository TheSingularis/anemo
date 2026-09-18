using System.Linq;
using System.Windows;
using Anemo.Core;

namespace Anemo.Widget
{
    public partial class SettingsWindow : Window
    {
        private sealed class ExcludableAdapter
        {
            public required string Id { get; init; }
            public required string Name { get; init; }
            public bool IsExcluded { get; init; }
        }

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

            var adapters = NetworkInfo.GetActiveInterfaces()
                .Select(n => new ExcludableAdapter { Id = n.Id, Name = n.Name, IsExcluded = _settings.ExcludedAdapterIds.Contains(n.Id) })
                .ToList();
            adapterExclusionList.ItemsSource = adapters;
            txtNoAdapters.Visibility = adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

        // Checked/Unchecked fire once from the initial data-bound IsChecked value too
        // (a WPF quirk, not just user interaction), so this stays correct either way
        // instead of relying on a loading flag whose timing isn't guaranteed against
        // ItemsControl's own (deferred) container generation.
        private void chkExcludeAdapter_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.CheckBox { Tag: string id } checkBox) return;

            if (checkBox.IsChecked == true)
            {
                if (!_settings.ExcludedAdapterIds.Contains(id)) _settings.ExcludedAdapterIds.Add(id);
            }
            else
            {
                _settings.ExcludedAdapterIds.Remove(id);
            }
            _settings.Save();
        }
    }
}
