using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace TEAMS2HA
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public class AppSettings
    {
        public bool RunAtWindowsBoot { get; set; }
        public bool RunMinimized { get; set; }
        public string Theme { get; set; }
        public string HomeAssistantToken { get; set; }
        public string HomeAssistantURL { get; set; }
        public string TeamsToken { get; set; }
    }

    public partial class MainWindow : Window
    {
        #region Private Fields

        private TEAMS2HA.API.HomeAssistant _homeAssistant;
        private string _homeassistantToken;
        private string _homeassistantURL;
        private string _teamsApiKey;
        private API.WebSocketClient _teamsClient;
        private bool isDarkTheme = false;
        private string _settingsFilePath;
        private AppSettings _settings;
        private FileSystemWatcher _settingsWatcher;
        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataFolder = Path.Combine(localAppData, "Teams2HA");
            Directory.CreateDirectory(appDataFolder); // Ensure the directory exists
            _settingsFilePath = Path.Combine(appDataFolder, "t2ha_settings.json");
            _settings = LoadSettings();

            this.InitializeComponent();
            //ApplyTheme(Properties.Settings.Default.Theme);
            this.Loaded += MainPage_Loaded;
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");
            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            InitializeConnections();
            InitializeSettingsWatcher();
        }

        #endregion Public Constructors

        #region Public Methods

        public async Task InitializeConnections()
        {
            // Other initialization code...
            await initializeteamsconnection();
            await InitializeHomeAssistantConnection();
            // Other initialization code...
        }

        private async Task initializeteamsconnection()
        {
            if (_teamsClient != null && _teamsClient.IsConnected)
            {
                return; // Already connected, no need to reinitialize
            }
            string teamsToken = _settings.TeamsToken;
            if (string.IsNullOrEmpty(teamsToken))
            {
                // If the Teams token is not set, then we can't connect to Teams
                return;
            }
            // Initialize the Teams WebSocket connection
            //var uri = new Uri("ws://localhost:8124?protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var state = new API.State();  // You would initialize this as necessary
            _teamsClient = new API.WebSocketClient(uri, state, _settingsFilePath, token => this.Dispatcher.Invoke(() => TeamsApiKeyBox.Text = token));


            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
        }

        #endregion Public Methods

        #region Private Methods
        private void InitializeSettingsWatcher()
        {
            _settingsWatcher = new FileSystemWatcher
            {
                Path = Path.GetDirectoryName(_settingsFilePath),
                Filter = Path.GetFileName(_settingsFilePath),
                NotifyFilter = NotifyFilters.LastWrite
            };

            _settingsWatcher.Changed += OnSettingsChanged;
            _settingsWatcher.EnableRaisingEvents = true;
        }

        private void OnSettingsChanged(object sender, FileSystemEventArgs e)
        {
            // Reload settings and update UI
            Dispatcher.Invoke(() =>
            {
                _settings = LoadSettings();
                UpdateUI();
            });
        }
        private void UpdateUI()
        {
            // Update UI elements based on _settings
            TeamsApiKeyBox.Text = _settings.TeamsToken;
            // ... other UI updates
        }
        private AppSettings LoadSettings()
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                return JsonConvert.DeserializeObject<AppSettings>(json);
            }
            else
            {
                return new AppSettings(); // Defaults if file does not exist
            }
        }

        private void SaveSettings()
        {
            _settings.RunAtWindowsBoot = RunAtWindowsBootCheckBox.IsChecked ?? false;
            _settings.RunMinimized = RunMinimisedCheckBox.IsChecked ?? false;
            _settings.HomeAssistantToken = HomeassistantTokenBox.Text;
            _settings.HomeAssistantURL = HomeassistantURLBox.Text;
            _settings.TeamsToken = TeamsApiKeyBox.Text;
            _settings.Theme = isDarkTheme ? "Dark" : "Light";

            string json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, json);
        }

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
            _settingsWatcher?.Dispose();
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
            //LoadSettings();

            RunAtWindowsBootCheckBox.IsChecked = _settings.RunAtWindowsBoot;
            RunMinimisedCheckBox.IsChecked = _settings.RunMinimized;
            HomeassistantTokenBox.Text = _settings.HomeAssistantToken;
            HomeassistantURLBox.Text = _settings.HomeAssistantURL;
            TeamsApiKeyBox.Text = _settings.TeamsToken;
            ApplyTheme(_settings.Theme);
            if (RunMinimisedCheckBox.IsChecked == true)
            {// Start the window minimized and hide it
                this.WindowState = WindowState.Minimized;
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible; // Show the NotifyIcon in the system tray
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

        private void TeamsConnectionStatusChanged(bool isConnected)
        {
            // The UI needs to be updated on the main thread.
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
            });
        }

        // This method is called when the TestHomeassistantConnection button is clicked
        private async void TestHomeassistantConnection_Click(object sender, RoutedEventArgs e)
        {
            // Get the Homeassistant token from the HomeassistantTokenBox
            _homeassistantToken = HomeassistantTokenBox.Text;

            // If the token is empty or null, return and do nothing
            if (string.IsNullOrEmpty(_homeassistantToken))
            {
                return;
            }

            // Create a new instance of the HomeAssistant class with the Homeassistant URL and token
            var homeAssistant = new TEAMS2HA.API.HomeAssistant(_settings.HomeAssistantURL, _homeassistantToken);

            // Set the _homeAssistant variable to the same instance of the HomeAssistant class
            _homeAssistant = new TEAMS2HA.API.HomeAssistant(_settings.HomeAssistantURL, _homeassistantToken);

            // Check the connection to Homeassistant asynchronously and get the result
            var connectionSuccessful = await homeAssistant.CheckConnectionAsync();

            // Update the HomeassistantConnectionStatus text based on the connection result
            HomeassistantConnectionStatus.Text = connectionSuccessful ? "Homeassistant: Connected" : "Homeassistant: Disconnected";

            // If the connection is successful, start the HomeAssistant instance
            if (connectionSuccessful)
            {
                await _homeAssistant.Start();
            }
        }

        // This method is called when the TestTeamsConnection button is clicked
        private void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InitializeWebSocketClient();
                Debug.WriteLine("WebSocket client initialized");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing WebSocket client: {ex.Message}");
            }
        }
        private void InitializeWebSocketClient()
        {
            string teamsToken = _settings.TeamsToken;
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            
            _teamsClient = new API.WebSocketClient(uri, new API.State(), _settingsFilePath, token => this.Dispatcher.Invoke(() => TeamsApiKeyBox.Text = token));

            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
        }
        private void ApplyTheme(string theme)
        {
            isDarkTheme = theme == "Dark";
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

        private async Task InitializeHomeAssistantConnection()
        {
            // Initialize HomeAssistant client with settings
            _homeAssistant = new TEAMS2HA.API.HomeAssistant(_settings.HomeAssistantURL, _settings.HomeAssistantToken);

            // Check the connection and update UI accordingly
            var connectionSuccessful = await _homeAssistant.CheckConnectionAsync();
            HomeassistantConnectionStatus.Text = connectionSuccessful ? "Homeassistant: Connected" : "Homeassistant: Disconnected";
            if (connectionSuccessful)
            {
                await _homeAssistant.Start();
            }
        }

        private void ToggleThemeButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the theme
            isDarkTheme = !isDarkTheme;
            _settings.Theme = isDarkTheme ? "Dark" : "Light";
            ApplyTheme(_settings.Theme);

            // Save settings after changing the theme
            SaveSettings();
        }

        #endregion Private Methods
    }
}