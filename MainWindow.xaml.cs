using Microsoft.Win32;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using TEAMS2HA.API;
using TEAMS2HA.Properties;
using TEAMS2HA.Utils;
using Hardcodet.Wpf.TaskbarNotification;

namespace TEAMS2HA
{
    public class AppSettings
    {
        #region Private Fields

        // Lock object for thread-safe initialization
        private static readonly object _lock = new object();

        private static readonly string _settingsFilePath;

        // Static variable for the singleton instance
        private static AppSettings _instance;

        private string _mqttPassword; // Store the encrypted version internally
        private string _teamsToken; // Store the encrypted version internally

        #endregion Private Fields

        #region Public Constructors

        // Static constructor to set up file path
        static AppSettings()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataFolder = Path.Combine(localAppData, "TEAMS2HA");
            Directory.CreateDirectory(appDataFolder);
            _settingsFilePath = Path.Combine(appDataFolder, "settings.json");
        }

        #endregion Public Constructors

        #region Private Constructors

        // Private constructor to prevent direct instantiation
        private AppSettings()
        {
            LoadSettingsFromFile();
        }

        #endregion Private Constructors

        #region Public Properties
        public bool HasShownOneTimeNotice { get; set; } = false;

        // Public property to access the singleton instance
        public static AppSettings Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new AppSettings();
                    }
                    return _instance;
                }
            }
        }

        // Properties
        public string EncryptedMqttPassword
        {
            get => _mqttPassword;
            set => _mqttPassword = value; // Only for deserialization
        }

        public string EncryptedTeamsToken
        {
            get => _teamsToken;
            set => _teamsToken = value; // Only for deserialization
        }

        public bool IgnoreCertificateErrors { get; set; }

        public string MqttAddress { get; set; }

        [JsonIgnore]
        public string MqttPassword
        {
            get => CryptoHelper.DecryptString(_mqttPassword);
            set => _mqttPassword = CryptoHelper.EncryptString(value);
        }

        public string MqttPort { get; set; }
        public string MqttUsername { get; set; }

        [JsonIgnore]
        public string PlainTeamsToken { get; set; }

        public bool RunAtWindowsBoot { get; set; }
        public bool RunMinimized { get; set; }
        public string SensorPrefix { get; set; }

        [JsonIgnore]
        public string TeamsToken
        {
            get => CryptoHelper.DecryptString(_teamsToken);
            set => _teamsToken = CryptoHelper.EncryptString(value);
        }

        public string Theme { get; set; }
        public bool UseTLS { get; set; }
        public bool UseWebsockets { get; set; }

        #endregion Public Properties

        #region Public Methods

        // Save settings to file
        public void SaveSettingsToFile()
        {
            // Encrypt sensitive data
            if (!String.IsNullOrEmpty(this.MqttPassword))
            {
                this.EncryptedMqttPassword = CryptoHelper.EncryptString(this.MqttPassword);
            }
            else
            {
                this.EncryptedMqttPassword = "";
            }
            if (!String.IsNullOrEmpty(this.PlainTeamsToken))
            {
                this.TeamsToken = CryptoHelper.EncryptString(this.PlainTeamsToken);
            }
            else
            {
                this.TeamsToken = "";
            }
            if (string.IsNullOrEmpty(this.SensorPrefix))
            {
                this.SensorPrefix = System.Environment.MachineName;
            }
            // newcode

            const string appName = "TEAMS2HA"; // Your application's name
            string exePath = System.Windows.Forms.Application.ExecutablePath;

            // Open the registry key for the current user's startup programs
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (this.RunAtWindowsBoot)
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

            Log.Debug("SetStartupAsync: Startup options set");
            // Serialize and save
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, json);
        }

        #endregion Public Methods

        #region Private Methods

        // Load settings from file
        private void LoadSettingsFromFile()
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                JsonConvert.PopulateObject(json, this);

                // Decrypt sensitive data
                if (!String.IsNullOrEmpty(this.EncryptedMqttPassword))
                {
                    this.MqttPassword = CryptoHelper.DecryptString(this.EncryptedMqttPassword);
                }
                if (!String.IsNullOrEmpty(this.TeamsToken))
                {
                    this.PlainTeamsToken = CryptoHelper.DecryptString(this.TeamsToken);
                }
                if (string.IsNullOrEmpty(this.MqttPort))
                {
                    this.MqttPort = "1883"; // Default MQTT port
                }
            }
            else
            {
                this.MqttPort = "1883"; // Default MQTT port
            }
        }

        #endregion Private Methods
    }

    public partial class MainWindow : Window
    {
        #region Private Fields

        private MenuItem _aboutMenuItem;
        private MeetingUpdate _latestMeetingUpdate;

        private MenuItem _logMenuItem;

        //private MqttManager _mqttManager;
        private MqttService _mqttService;

        private MenuItem _mqttStatusMenuItem;

        //private string Mqtttopic;
        private Dictionary<string, string> _previousSensorStates = new Dictionary<string, string>();

        private AppSettings _settings;
        private string _settingsFilePath;
        private string _teamsApiKey;
      
        private MenuItem _teamsStatusMenuItem;
        private Action<string> _updateTokenAction;
        private string deviceid;
        private bool isDarkTheme = false;
        private bool isTeamsConnected = false;
        private bool isTeamsSubscribed = false;
        private bool mqttCommandToTeams = false;
        private bool mqttConnectionAttempting = false;
        private bool mqttConnectionStatusChanged = false;
        private bool mqttStatusUpdated = false;

        private List<string> sensorNames = new List<string>
        {
            "IsMuted", "IsVideoOn", "IsHandRaised", "IsInMeeting", "IsRecordingOn", "IsBackgroundBlurred", "IsSharing", "HasUnreadMessages", "teamsRunning"
        };

        private bool teamspaired = false;

        #endregion Private Fields

        #region Public Constructors

        // Constructor for the MainWindow class
        public MainWindow()
        {
            // Get the local application data folder path
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
          
            // Configure logging
            LoggingConfig.Configure();

            // Create the TEAMS2HA folder in the local application data folder
            var appDataFolder = Path.Combine(localAppData, "TEAMS2HA");
            Log.Debug("Set Folder Path to {path}", appDataFolder);
            Directory.CreateDirectory(appDataFolder); // Ensure the directory exists

            // Set the settings file path
            _settingsFilePath = Path.Combine(appDataFolder, "settings.json");

            // Get the app settings instance
            var settings = AppSettings.Instance;
            _settings = AppSettings.Instance;

            // Get the device ID
            if (string.IsNullOrEmpty(_settings.SensorPrefix))
            {
                deviceid = System.Environment.MachineName.ToLower();
                _settings.SensorPrefix = deviceid;
            }
            else
            {
                deviceid = _settings.SensorPrefix.ToLower();
            }

            // Log the settings file path
            Log.Debug("Settings file path is {path}", _settingsFilePath);

            // Initialize the main window
            this.InitializeComponent();
            SetWindowTitle();
            // Add event handler for when the main window is loaded
            this.Loaded += MainPage_Loaded;
           
            // Set the icon for the notification tray
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");
            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            // Create a new instance of the MQTT Service class


            // Set the action to be performed when a new token is updated
            _updateTokenAction = newToken =>
            {
                Dispatcher.Invoke(() =>
                {
                    TeamsApiKeyBox.Text = "Pairing Required";
                    PairButton.IsEnabled = false;
                });
            };

            // Initialize connections
            InitializeConnections();

            
            foreach (var sensor in sensorNames)
            {
                _previousSensorStates[$"{deviceid}_{sensor}"] = "";
            }
        }

        #endregion Public Constructors

        #region Public Methods
        public async Task InitializeConnections()
        {
            MqttService.Instance.Initialize(_settings, _settings.SensorPrefix, sensorNames);
            await MqttService.Instance.ConnectAsync(AppSettings.Instance);
            if(_mqttService == null)
            {
                _mqttService = MqttService.Instance;
                _mqttService.StatusUpdated += UpdateMqttStatus;
              
            }
            await _mqttService.SubscribeAsync($"homeassistant/switch/{deviceid}/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
            await _mqttService.SubscribeAsync($"homeassistant/binary_sensor/{deviceid}/+/state", MqttQualityOfServiceLevel.AtLeastOnce);
            // await _mqttService.SubscribeAsync("#", MqttQualityOfServiceLevel.AtLeastOnce); //line to test all topics

            _ = _mqttService.PublishConfigurations(null!, _settings);
            _mqttService.CommandToTeams += HandleCommandToTeams;
            InitializeWebSocket();
            Dispatcher.Invoke(() => UpdateStatusMenuItems());

        }
        private async void InitializeWebSocket()
        {
            var uri = new Uri($"ws://localhost:8124?token={_settings.PlainTeamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            await WebSocketManager.Instance.PairWithTeamsAsync(newToken =>
            {
                // Update the UI with the new token
                TeamsApiKeyBox.Text = "Paired";
            });
            await WebSocketManager.Instance.ConnectAsync(uri);
            WebSocketManager.Instance.TeamsUpdateReceived += TeamsClient_TeamsUpdateReceived;
            Dispatcher.Invoke(() => UpdateStatusMenuItems());
        }
      

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosing(CancelEventArgs e)
        {
            if(_mqttService != null)
            {
                _mqttService.StatusUpdated -= UpdateMqttStatus;
                _mqttService.CommandToTeams -= HandleCommandToTeams;

                // Disconnect asynchronously without waiting
                _ = _mqttService.DisconnectAsync();

                // Dispose of the MQTT service
                _mqttService.Dispose();
                Log.Debug("MQTT Client Disposed");
            }   
           

            // Ensure to call the base class method to properly close the application
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

        #endregion Protected Methods

        #region Private Methods

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string currentTheme = _settings.Theme; // Assuming this is where the theme is stored
            var aboutWindow = new AboutWindow(deviceid, MyNotifyIcon);
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
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

        private bool CheckIfMqttSettingsChanged(AppSettings newSettings)
        {
            var currentSettings = AppSettings.Instance; // Assuming this gets the current settings before they are changed

            return newSettings.MqttAddress != currentSettings.MqttAddress ||
                   newSettings.MqttPort != currentSettings.MqttPort ||
                   newSettings.MqttUsername != currentSettings.MqttUsername ||
                   newSettings.MqttPassword != currentSettings.MqttPassword ||
                   newSettings.UseTLS != currentSettings.UseTLS ||
                   newSettings.UseWebsockets != currentSettings.UseWebsockets ||
                   newSettings.IgnoreCertificateErrors != currentSettings.IgnoreCertificateErrors;
        }

        private bool CheckIfSensorPrefixChanged(AppSettings newSettings)
        {
            var currentSettings = AppSettings.Instance;
            deviceid = newSettings.SensorPrefix;
            return newSettings.SensorPrefix != currentSettings.SensorPrefix;
        }


        private void CreateNotifyIconContextMenu()
        {
            ContextMenu contextMenu = new ContextMenu();

            // Show/Hide Window
            MenuItem showHideMenuItem = new MenuItem();
            showHideMenuItem.Header = "Show/Hide";
            showHideMenuItem.Click += ShowHideMenuItem_Click;

            // MQTT Status
            _mqttStatusMenuItem = new MenuItem { Header = "MQTT Status: Unknown", IsEnabled = false };

            // Teams Status
            _teamsStatusMenuItem = new MenuItem { Header = "Teams Status: Unknown", IsEnabled = false };

            // Logs
            _logMenuItem = new MenuItem { Header = "View Logs" };
            _logMenuItem.Click += LogsButton_Click; // Reuse existing event handler

            // About
            _aboutMenuItem = new MenuItem { Header = "About" };
            _aboutMenuItem.Click += AboutMenuItem_Click;

            // Exit
            MenuItem exitMenuItem = new MenuItem();
            exitMenuItem.Header = "Exit";
            exitMenuItem.Click += ExitMenuItem_Click;

            contextMenu.Items.Add(showHideMenuItem);
            contextMenu.Items.Add(_mqttStatusMenuItem);
            contextMenu.Items.Add(_teamsStatusMenuItem);
            contextMenu.Items.Add(_logMenuItem);
            contextMenu.Items.Add(_aboutMenuItem);
            contextMenu.Items.Add(new Separator()); // Separator before exit
            contextMenu.Items.Add(exitMenuItem);

            MyNotifyIcon.ContextMenu = contextMenu;
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Handle the click event for the exit menu item (Close the application)
            Application.Current.Shutdown();
        }

        private async Task HandleCommandToTeams(string jsonMessage)
        {
            if (WebSocketManager.Instance != null && WebSocketManager.Instance.IsConnected)
            {
                await WebSocketManager.Instance.SendMessageAsync(jsonMessage);
            }
            else
            {
                // Handle the case when WebSocketManager is not connected
                Log.Warning("WebSocketManager is not connected. Message not sent.");
            }
        }

    



    private void LogsButton_Click(object sender, RoutedEventArgs e)
        {
            string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Teams2HA");

            // Ensure the directory exists
            if (!Directory.Exists(folderPath))
            {
                Log.Error("Log directory does not exist.");
                return;
            }

            // Get the most recent log file
            var logFile = Directory.GetFiles(folderPath, "Teams2ha_Log*.log")
                                   .OrderByDescending(File.GetCreationTime)
                                   .FirstOrDefault();

            if (logFile != null && File.Exists(logFile))
            {
                try
                {
                    ProcessStartInfo processStartInfo = new ProcessStartInfo
                    {
                        FileName = logFile,
                        UseShellExecute = true
                    };

                    Process.Start(processStartInfo);
                }
                catch (Exception ex)
                {
                    Log.Error($"Error opening log file: {ex.Message}");
                }
            }
            else
            {
                Log.Error("Log file does not exist.");
            }
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            //LoadSettings();
           
            RunAtWindowsBootCheckBox.IsChecked = _settings.RunAtWindowsBoot;
            RunMinimisedCheckBox.IsChecked = _settings.RunMinimized;
            MqttUserNameBox.Text = _settings.MqttUsername;
            UseTLS.IsChecked = _settings.UseTLS;
            Websockets.IsChecked = _settings.UseWebsockets;
            IgnoreCert.IsChecked = _settings.IgnoreCertificateErrors;
            MQTTPasswordBox.Password = _settings.MqttPassword;
            MqttAddress.Text = _settings.MqttAddress;
            // Added to set the sensor prefix
            if (string.IsNullOrEmpty(_settings.SensorPrefix))
            {
                SensorPrefixBox.Text = System.Environment.MachineName;
            }
            else
            {
                SensorPrefixBox.Text = _settings.SensorPrefix;
            }
            SensorPrefixBox.Text = _settings.SensorPrefix;
            MqttPort.Text = _settings.MqttPort;
            if (_settings.PlainTeamsToken == null)
            {
                TeamsApiKeyBox.Text = "Not Paired";
                PairButton.IsEnabled = true;
            }
            else
            {
                TeamsApiKeyBox.Text = "Paired";
                PairButton.IsEnabled = false;
            }

            ApplyTheme(_settings.Theme);
            if (RunMinimisedCheckBox.IsChecked == true)
            {// Start the window minimized and hide it
                this.WindowState = WindowState.Minimized;
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible; // Show the NotifyIcon in the system tray
            }
            Dispatcher.Invoke(() => UpdateStatusMenuItems());
            ShowOneTimeNoticeIfNeeded();
            
        }
        public void UpdatePairingStatus(bool isPaired)
        {
           
                Dispatcher.Invoke(() =>
                {
                    // Assuming you have a Label or some status indicator in your MainWindow
                    TeamsApiKeyBox.Text = isPaired ? "Paired" : "Not Paired";
                });
        
        }
        public void UpdateMqttStatus(bool isPaired)
        {

            Dispatcher.Invoke(() =>
            {
                // Assuming you have a Label or some status indicator in your MainWindow
                MQTTConnectionStatus.Text = isPaired ? "MQTT Status: Connected" : "MQTT Status: Not Connected";
            });

        }
        // Event handler that enables the PairButton in WPF
        private void TeamsClient_RequirePairing(object sender, EventArgs e)
        {
            // Use Dispatcher.Invoke to update the UI from a non-UI thread
            Dispatcher.Invoke(() =>
            {
                PairButton.IsEnabled = true; // Note: Use IsEnabled in WPF, not Enabled
            });
        }

        private void ShowOneTimeNoticeIfNeeded()
        {
            // Check if the one-time notice has already been shown
            if (!_settings.HasShownOneTimeNotice)
            {
                // Show the notice to the user
                MessageBox.Show("Important: Due to recent updates, the functionality of TEAMS2HA has changed. Sensors are now Binarysensors - please make sure you update any automations etc that rely on the sensors.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);

                // Update the setting so that the notice isn't shown again
                _settings.HasShownOneTimeNotice = true;

                // Save the updated settings to file
                _settings.SaveSettingsToFile();
            }
        }
        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe when the page is unloaded
           
            Log.Debug("MainPage_Unloaded: WebSocketManager TeamsUpdateReceived unsubscribed");
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

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            Log.Debug("SaveSettings_Click: Save Settings Clicked" + _settings.ToString());

            // uncomment below for testing ** insecure as tokens exposed in logs! **
            //foreach(var setting in _settings.GetType().GetProperties())
            //{
            //    Log.Debug(setting.Name + " " + setting.GetValue(_settings));
            //}
            await SaveSettingsAsync();
        }

        private async Task SaveSettingsAsync()
        {
            // Get the current settings from the singleton instance
            var settings = AppSettings.Instance;
            var oldSettings = settings;
            // Temporary storage for old values to compare after updating
            var oldMqttAddress = settings.MqttAddress;
            var oldMqttPort = settings.MqttPort;
            var oldMqttUsername = settings.MqttUsername;
            var oldMqttPassword = settings.MqttPassword;
            var oldUseTLS = settings.UseTLS;
            var oldIgnoreCertificateErrors = settings.IgnoreCertificateErrors;
            var oldUseWebsockets = settings.UseWebsockets;
            var oldSensorPrefix = settings.SensorPrefix;

            // Update the settings from UI components
            Dispatcher.Invoke(() =>
            {
                settings.MqttAddress = MqttAddress.Text;
                settings.MqttPort = MqttPort.Text;
                settings.MqttUsername = MqttUserNameBox.Text;
                settings.MqttPassword = MQTTPasswordBox.Password;
                settings.UseTLS = UseTLS.IsChecked ?? false;
                settings.IgnoreCertificateErrors = IgnoreCert.IsChecked ?? false;
                settings.RunMinimized = RunMinimisedCheckBox.IsChecked ?? false;
                settings.UseWebsockets = Websockets.IsChecked ?? false;
                settings.RunAtWindowsBoot = RunAtWindowsBootCheckBox.IsChecked ?? false;
                settings.SensorPrefix = string.IsNullOrEmpty(SensorPrefixBox.Text) ? System.Environment.MachineName : SensorPrefixBox.Text;
            });

            // Now check if MQTT settings have changed
            bool mqttSettingsChanged = (oldMqttAddress != settings.MqttAddress) ||
                                       (oldMqttPort != settings.MqttPort) ||
                                       (oldMqttUsername != settings.MqttUsername) ||
                                       (oldMqttPassword != settings.MqttPassword) ||
                                       (oldUseTLS != settings.UseTLS) ||
                                       (oldIgnoreCertificateErrors != settings.IgnoreCertificateErrors) ||
                                       (oldUseWebsockets != settings.UseWebsockets);

            bool sensorPrefixChanged = (oldSensorPrefix != settings.SensorPrefix);

            // Save the updated settings to file
            settings.SaveSettingsToFile();
            // only reconnect if the mqtt settings have changed
            if (mqttSettingsChanged)
            {
                _mqttService.CommandToTeams -= HandleCommandToTeams;
                await MqttService.Instance.UnsubscribeAsync($"homeassistant/switch/{deviceid}/+/set");
                await MqttService.Instance.UnsubscribeAsync($"homeassistant/binary_sensor/{deviceid}/+/state");
                Log.Information("SaveSettingsAsync: MQTT settings have changed. Reconnecting MQTT client...");
                await MqttService.Instance.ConnectAsync(AppSettings.Instance);
                // republish sensors
                await _mqttService.PublishConfigurations(_latestMeetingUpdate, _settings);
                //re subscribe to topics
                await _mqttService.SubscribeAsync($"homeassistant/switch/{deviceid}/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                await _mqttService.SubscribeAsync($"homeassistant/binary_sensor/{deviceid}/+/state", MqttQualityOfServiceLevel.AtLeastOnce);
                Log.Debug("SaveSettingsAsync: Reconnecting MQTT client...");
                _mqttService.CommandToTeams += HandleCommandToTeams;
            }
        }

        private void SetWindowTitle()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            this.Title = $"Teams2HA - Version {version}";
        }

        private void ShowHideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (this.IsVisible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            }
        }

        private async void TeamsClient_TeamsUpdateReceived(object sender, TeamsUpdateEventArgs e)
        {
            if (_mqttService != null && _mqttService.IsConnected)
            {
                // Store the latest update
                _latestMeetingUpdate = e.MeetingUpdate;
                Log.Debug("TeamsClient_TeamsUpdateReceived: Teams Update Received {update}", _latestMeetingUpdate);
                // Update sensor configurations
                await _mqttService.PublishConfigurations(_latestMeetingUpdate, _settings);
            }
        }

        private void TeamsConnectionStatusChanged(bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
                _teamsStatusMenuItem.Header = "Teams Status: " + (isConnected ? "Connected" : "Disconnected");
            });
        }

        private async void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {

        }

        private void ToggleThemeButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the theme
            isDarkTheme = !isDarkTheme;
            _settings.Theme = isDarkTheme ? "Dark" : "Light";
            ApplyTheme(_settings.Theme);

            // Save settings after changing the theme
            _ = SaveSettingsAsync();
        }

        private void UpdateMqttStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                // Assuming MQTTConnectionStatus is a Label or similar control
                MQTTConnectionStatus.Text = $"MQTT Status: {status}";
                UpdateStatusMenuItems();
            });
        }

        private void UpdateStatusMenuItems()
        {
            Dispatcher.Invoke(() =>
            {
                // Update MQTT connection status text
                MQTTConnectionStatus.Text = _mqttService != null && _mqttService.IsConnected ? "MQTT Status: Connected" : "MQTT Status: Disconnected";
                TeamsConnectionStatus.Text = WebSocketManager.Instance.IsConnected ? "Teams: Connected" : "Teams: Disconnected";
                // Update menu items
                _mqttStatusMenuItem.Header = MQTTConnectionStatus.Text; // Reuse the text set above
                _teamsStatusMenuItem.Header = WebSocketManager.Instance.IsConnected ? "Teams Status: Connected" : "Teams Status: Disconnected";
                // Add other status updates here as necessary
            });
        }


        private void Websockets_Checked(object sender, RoutedEventArgs e)
        {
            _settings.UseWebsockets = true;
            // Disable the mqtt port box MqttPort.IsEnabled = false;
        }

        private void Websockets_Unchecked(object sender, RoutedEventArgs e)
        {
            _settings.UseWebsockets = false;
            // MqttPort.IsEnabled = true;
        }

        #endregion Private Methods
    }
}