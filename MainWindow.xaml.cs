using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using TEAMS2HA.API;
using TEAMS2HA.Properties;
using Hardcodet.Wpf.TaskbarNotification;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using Microsoft.Win32;

namespace TEAMS2HA
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Private Fields

        private TEAMS2HA.API.HomeAssistant _homeAssistant;
        private string _homeassistantToken;
        private string _homeassistantURL;
        private string _teamsApiKey;
        private API.WebSocketClient _teamsClient;
        private bool isDarkTheme = false;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            if (Properties.Settings.Default.FirstTimeRunningThisVersion)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.FirstTimeRunningThisVersion = false;
                Properties.Settings.Default.Save();
            }

            this.InitializeComponent();
            ApplyTheme(Properties.Settings.Default.Theme);
            this.Loaded += MainPage_Loaded;
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");
            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            InitializeConnections();
        }


        #endregion Public Constructors

        #region Public Methods

        public async Task InitializeConnections()
        {
            // Other initialization code...
            if (_teamsClient != null && _teamsClient.IsConnected)
            {
                return; // Already connected, no need to reinitialize
            }
            string teamsToken = TokenStorage.GetTeamsToken();
            if (string.IsNullOrEmpty(teamsToken))
            {
                // If the Teams token is not set, then we can't connect to Teams
                return;
            }
            // Initialize the Teams WebSocket connection
            //var uri = new Uri("ws://localhost:8124?protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var state = new API.State();  // You would initialize this as necessary
            _teamsClient = new API.WebSocketClient(uri, state);
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;

            // Other initialization code...
        }

        #endregion Public Methods

        #region Private Methods
        private void MyNotifyIcon_Click(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                // Restore the window if it's minimized
                this.Show();
                this.WindowState = WindowState.Normal;
            }
            else
            {
                // Minimize the window if it's currently normal or maximized
                this.WindowState = WindowState.Minimized;
            }
        }


        protected override void OnClosing(CancelEventArgs e)
        {
            MyNotifyIcon.Dispose();
            base.OnClosing(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Only hide the window and show the NotifyIcon when minimized
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                // Ensure the NotifyIcon is hidden when the window is not minimized
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }

            base.OnStateChanged(e);
        }


        private async Task LoadSettingsAsync()
        {
            // Assuming you have settings like HomeassistantURL, RunAtWindowsBoot, and RunMinimised defined

            // Load Homeassistant URL
            _homeassistantURL = Properties.Settings.Default.HomeassistantURL;
            HomeassistantURLBox.Text = _homeassistantURL;

            // Load CheckBox values
            RunAtWindowsBootCheckBox.IsChecked = Properties.Settings.Default.RunAtWindowsBoot;
            
            RunMinimisedCheckBox.IsChecked = Properties.Settings.Default.RunMinimised;

            if (RunMinimisedCheckBox.IsChecked == true)
            {
                // Start the window minimized and hide it
                this.WindowState = WindowState.Minimized;
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible; // Show the NotifyIcon in the system tray
            }
            // Load Homeassistant Token (assuming you're using TokenStorage for this)
            _homeassistantToken = TokenStorage.GetHomeassistantToken();
            HomeassistantTokenBox.Text = _homeassistantToken;
            // Load Teams Token
            _teamsApiKey = TokenStorage.GetTeamsToken();
            TeamsApiKeyBox.Text = _teamsApiKey;
            // Rest of your logic...
            if (!string.IsNullOrEmpty(_homeassistantURL) && !string.IsNullOrEmpty(_homeassistantToken))
            {
                var homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
                _homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
                var connectionSuccessful = await homeAssistant.CheckConnectionAsync();
                HomeassistantConnectionStatus.Text = connectionSuccessful ? "Homeassistant: Connected" : "Homeassistant: Disconnected";
                if (connectionSuccessful)
                {
                    await _homeAssistant.Start();
                }
            }
            
        }
        private async Task SetStartupAsync(bool startWithWindows)
        {
            await Task.Run(() =>
            {
                const string appName = "TEAMS2HA"; // Your application's name
                string exePath = System.Windows.Forms.Application.ExecutablePath;

                // Open the registry key for the current user's startup programs
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (startWithWindows)
                    {
                        // Set the application to start with Windows startup by adding a registry value
                        key.SetValue(appName, exePath);
                    }
                    else
                    {
                        // Remove the registry value to prevent the application from starting with
                        // Windows startup
                        key.DeleteValue(appName, false);
                    }
                }
            });
        }
        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSettingsAsync();
            if (RunMinimisedCheckBox.IsChecked == true) // Check your run minimized setting
            {
                // Instead of minimizing, keep the app running in the background Show a toast
                // notification to inform the user
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe when the page is unloaded
            _teamsClient.ConnectionStatusChanged -= TeamsConnectionStatusChanged;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }
        private void SaveSettings()
        {
            // Check if the Teams API key has changed
            string currentTeamsToken = TokenStorage.GetTeamsToken();
            bool teamsTokenChanged = currentTeamsToken != TeamsApiKeyBox.Text;
            if (teamsTokenChanged)
            {
                TokenStorage.SaveTeamsToken(TeamsApiKeyBox.Text);
                InitializeConnections();
            }

            // Store the Homeassistant token securely
            if (!string.IsNullOrEmpty(HomeassistantTokenBox.Text))
            {
                TokenStorage.SaveHomeassistantToken(HomeassistantTokenBox.Text);
            }

            _homeassistantToken = HomeassistantTokenBox.Text;

            // Store the Homeassistant URL
            Properties.Settings.Default.HomeassistantURL = HomeassistantURLBox.Text;
            _homeassistantURL = HomeassistantURLBox.Text;
           
            // Save CheckBox values
            Properties.Settings.Default.RunAtWindowsBoot = RunAtWindowsBootCheckBox.IsChecked.Value;
            Properties.Settings.Default.RunMinimised = RunMinimisedCheckBox.IsChecked.Value;
            _ = SetStartupAsync(Properties.Settings.Default.RunAtWindowsBoot);
            Properties.Settings.Default.Theme = isDarkTheme ? "Dark" : "Light";
            Properties.Settings.Default.Save();
        }
        private void TeamsConnectionStatusChanged(bool isConnected)
        {
            // The UI needs to be updated on the main thread.
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
            });
        }

        private async void TestHomeassistantConnection_Click(object sender, RoutedEventArgs e)
        {
            // Implement the code to test the connection to Home Assistant If the connection is
            // successful, update the HomeassistantConnectionStatus
            // Example: HomeassistantConnectionStatus.Text = "Homeassistant: Connected";
            _homeassistantToken = HomeassistantTokenBox.Text;
            if (string.IsNullOrEmpty(_homeassistantToken))
            {
                return;
            }
            var homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
            _homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
            var connectionSuccessful = await homeAssistant.CheckConnectionAsync();
            HomeassistantConnectionStatus.Text = connectionSuccessful ? "Homeassistant: Connected" : "Homeassistant: Disconnected";
            if (connectionSuccessful)
            {
                await _homeAssistant.Start();
            }
        }

        private void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {
            string teamsToken = TokenStorage.GetTeamsToken();
            // Implement the code to test the connection to Teams If the connection is successful,
            // update the TeamsConnectionStatus
            // Example: TeamsConnectionStatus.Text = "Teams: Connected";
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");

            var state = new API.State();  // You would initialize this as necessary
            _teamsClient = new API.WebSocketClient(uri, state);
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
            //send output to log
            _teamsClient.MessageReceived += (_teamsClient, message) =>
            {
                Debug.WriteLine(message);
            };
        }

      
        private void ApplyTheme(string theme)
        {
            Uri themeUri;
            if (theme == "Dark")
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");
                isDarkTheme = true;
            }
            else
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml");
                isDarkTheme = false;
            }

            // Update the theme
            var existingTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == themeUri);
            if (existingTheme == null)
            {
                existingTheme = new ResourceDictionary() { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(existingTheme);
            }

            // Remove the other theme
            var otherThemeUri = isDarkTheme
                ? new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml")
                : new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");

            var currentTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == otherThemeUri);
            if (currentTheme != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(currentTheme);
            }
        }
        private void ToggleThemeButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the theme
            isDarkTheme = !isDarkTheme;

            // Update the theme setting
            Properties.Settings.Default.Theme = isDarkTheme ? "Dark" : "Light";
            

            // Define the URIs for the dark and light themes
            var darkThemeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");
            var lightThemeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml");

            // Determine the current theme URI based on the toggle
            var themeUri = isDarkTheme ? darkThemeUri : lightThemeUri;

            // Print the current merged dictionaries before the toggle
            System.Diagnostics.Debug.WriteLine("Before toggle:");
            foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
            {
                System.Diagnostics.Debug.WriteLine($" - {dictionary.Source}");
            }

            // Check if the new theme already exists in the merged dictionaries
            var existingTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == themeUri);
            if (existingTheme == null)
            {
                // If the new theme does not exist, add it to the merged dictionaries
                existingTheme = new ResourceDictionary() { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(existingTheme);
            }

            // Remove the current theme from the merged dictionaries
            var currentTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == (isDarkTheme ? lightThemeUri : darkThemeUri));
            if (currentTheme != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(currentTheme);
            }

            // Print the current merged dictionaries after the toggle
            System.Diagnostics.Debug.WriteLine("After toggle:");
            foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
            {
                System.Diagnostics.Debug.WriteLine($" - {dictionary.Source}");
            }
            SaveSettings();
        }
        #endregion Private Methods
    }
}