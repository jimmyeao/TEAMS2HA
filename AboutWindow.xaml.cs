using Hardcodet.Wpf.TaskbarNotification;
using System;

using System.Diagnostics;

using System.Windows;
using System.Windows.Controls;

using System.Windows.Forms;
using System.Windows.Input;

using System.Windows.Threading;
using TEAMS2HA.API;

namespace TEAMS2HA
{
  
    public partial class AboutWindow : Window
    {
        private TaskbarIcon _notifyIcon;
        public AboutWindow(string deviceId, TaskbarIcon notifyIcon)
        {
            InitializeComponent();
            _notifyIcon = notifyIcon;
            SetVersionInfo();
            var entityNames = MqttClientWrapper.GetEntityNames(deviceId);
            EntitiesListBox.ItemsSource = entityNames;
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        private void EntitiesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox listBox && listBox.SelectedItem != null)
            {
                string selectedEntity = listBox.SelectedItem.ToString();
                System.Windows.Clipboard.SetText(selectedEntity);

                // Show balloon tip
                _notifyIcon.ShowBalloonTip("Copied to Clipboard", selectedEntity + " has been copied to your clipboard.", BalloonIcon.Info);
            }
        }


      


        private void SetVersionInfo()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            this.VersionTextBlock.Text = $"Version: {version}";
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
