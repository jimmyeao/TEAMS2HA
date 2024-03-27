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
        private API.WebSocketClient _teamsClient;
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
                deviceid = System.Environment.MachineName;
            }
            else
            {
                deviceid = _settings.SensorPrefix;
            }

            // Log the settings file path
            Log.Debug("Settings file path is {path}", _settingsFilePath);

            // Initialize the main window
            this.InitializeComponent();
            SetWindowTitle();
            // Add event handler for when the main window is loaded
            this.Loaded += MainPage_Loaded;
            SystemEvents.PowerModeChanged += OnPowerModeChanged; //subscribe to power events
            // Set the icon for the notification tray
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");
            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            CreateNotifyIconContextMenu();
            // Create a new instance of the MQTT Service class
            if (mqttConnectionStatusChanged == false)
            {
                _mqttService = new MqttService(settings, deviceid, sensorNames);
                mqttConnectionStatusChanged = true;
            }
            _mqttService = new MqttService(settings, deviceid, sensorNames);
            if (mqttStatusUpdated == false)
            {
                _mqttService.ConnectionStatusChanged += MqttManager_ConnectionStatusChanged;
                mqttStatusUpdated = true;
            }
            if (mqttCommandToTeams == false)
            {
                _mqttService.CommandToTeams += HandleCommandToTeams;
                mqttCommandToTeams = true;
            }
            if (mqttConnectionAttempting == false)
            {
                _mqttService.ConnectionAttempting += MqttManager_ConnectionAttempting;
                mqttConnectionAttempting = true;
            }

            // Set the action to be performed when a new token is updated
            _updateTokenAction = newToken =>
            {
                Dispatcher.Invoke(() =>
                {
                    TeamsApiKeyBox.Text = "Paired";
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
            await _mqttService.ConnectAsync();

            await _mqttService.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
            // Other initialization code...
            await initializeteamsconnection();

            // Other initialization code...
        }

        #endregion Public Methods

        #region Protected Methods

        protected override async void OnClosing(CancelEventArgs e)
        {
            // Unsubscribe from events and clean up
            if (_mqttService != null)
            {
                _mqttService.ConnectionStatusChanged -= MqttManager_ConnectionStatusChanged;
                _mqttService.StatusUpdated -= UpdateMqttStatus;
                _mqttService.CommandToTeams -= HandleCommandToTeams;
            }
            if (_teamsClient != null)
            {
                _teamsClient.TeamsUpdateReceived -= TeamsClient_TeamsUpdateReceived;
                Log.Debug("Teams Client Disconnected");
            }
            // we want all the sensors to be off if we are exiting, lets initialise them, to do this
            await _mqttService.SetupMqttSensors();
            if (_mqttService != null)
            {
                await _mqttService.DisconnectAsync(); // Properly disconnect before disposing
                _mqttService.Dispose();
                Log.Debug("MQTT Client Disposed");
            }
            MyNotifyIcon.Dispose();
            base.OnClosing(e); // Call the base class method
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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

        //private async void CheckMqttConnection()  //could be obsolete
        //{
        //    if (_mqttService != null && !_mqttService.IsConnected && !_mqttService.IsAttemptingConnection)
        //    {
        //        Log.Debug("CheckMqttConnection: MQTT Client Not Connected. Attempting reconnection.");
        //        await _mqttService.ConnectAsync();
        //        await _mqttService.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
        //        _mqttService.UpdateConnectionStatus("Connected");
        //        UpdateStatusMenuItems();
        //    }
        //}

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
            if (_teamsClient != null)
            {
                await _teamsClient.SendMessageAsync(jsonMessage);
            }
        }

        private async Task initializeteamsconnection()
        {
            string teamsToken = _settings.PlainTeamsToken;
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");

            // Initialize the WebSocketClient only if it's not already created
            if (_teamsClient == null)
            {
                _teamsClient = new API.WebSocketClient(
                    uri,
                    new API.State(),
                    _settingsFilePath,
                    _updateTokenAction // Pass the action here
                );
                if (isTeamsConnected == false)
                {
                    _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
                    isTeamsConnected = true;
                }

                if (isTeamsSubscribed == false)
                {
                    _teamsClient.TeamsUpdateReceived += TeamsClient_TeamsUpdateReceived;
                    isTeamsSubscribed = true;
                }
            }

            // Connect if not already connected
            if (!_teamsClient.IsConnected)
            {
                await _teamsClient.StartConnectionAsync(uri);
            }
            else
            {
                Log.Debug("initializeteamsconnection: WebSocketClient is already connected or in the process of connecting");
            }
            if (_teamsClient.IsConnected)
            {
                Dispatcher.Invoke(() => TeamsConnectionStatus.Text = "Teams Status: Connected");
                // ADD in code to set the connected status as a sensor

                State.Instance.teamsRunning = true;
                Log.Debug("initializeteamsconnection: WebSocketClient Connected");
            }
            else
            {
                Dispatcher.Invoke(() => TeamsConnectionStatus.Text = "Teams Status: Disconnected");
                State.Instance.teamsRunning = false;
                Log.Debug("initializeteamsconnection: WebSocketClient Disconnected");
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
            _teamsClient.ConnectionStatusChanged -= TeamsConnectionStatusChanged;
            Log.Debug("MainPage_Unloaded: Teams Client Connection Status unsubscribed");
        }

        private void MqttManager_ConnectionAttempting(string status)
        {
            Dispatcher.Invoke(() =>
            {
                MQTTConnectionStatus.Text = status;
                _mqttStatusMenuItem.Header = status; // Update the system tray menu item as well
                                                     // No need to update other status menu items as
                                                     // this is specifically for MQTT connection
            });
        }

        private void MqttManager_ConnectionStatusChanged(string status)
        {
            Dispatcher.Invoke(() =>
            {
                MQTTConnectionStatus.Text = status; // Ensure MQTTConnectionStatus is the correct UI element's name
            });
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

        private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Log.Information("System is waking up from sleep. Re-establishing connections...");
                // Add a delay to allow the network to come up
                await Task.Delay(TimeSpan.FromSeconds(10)); // Adjust based on your needs

                // Implement logic to re-establish connections
                await ReestablishConnections();
                // publish current meeting state
                await _mqttService.PublishConfigurations(_latestMeetingUpdate, _settings);
            }
        }


        private async 

        Task
ReestablishConnections()
        {
            try
            {
                if (!_mqttService.IsConnected)
                {
                    await _mqttService.ConnectAsync();
                    await _mqttService.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                    await _mqttService.SetupMqttSensors();
                    Dispatcher.Invoke(() => UpdateStatusMenuItems());
                }
                if (!_teamsClient.IsConnected)
                {
                    await initializeteamsconnection();
                    Dispatcher.Invoke(() => UpdateStatusMenuItems());
                }
                // Force publish all sensor states after reconnection
                await _mqttService.PublishConfigurations(_latestMeetingUpdate, _settings, forcePublish: true);
            }
            catch (Exception ex)
            {
                Log.Error($"Error re-establishing connections: {ex.Message}");
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

            if (mqttSettingsChanged || sensorPrefixChanged)
            {
                // Perform actions if MQTT settings have changed
                Log.Debug("SaveSettingsAsync: MQTT settings have changed. Reconnecting MQTT client...");
                await _mqttService.UnsubscribeAsync("homeassistant/switch/+/set");
                await _mqttService.DisconnectAsync();
                await _mqttService.UpdateSettingsAsync(settings); // Make sure to pass the updated settings
                await _mqttService.ConnectAsync();
                await _mqttService.PublishConfigurations(_latestMeetingUpdate, settings, forcePublish: true);
                await _mqttService.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
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

        private async void TeamsClient_TeamsUpdateReceived(object sender, WebSocketClient.TeamsUpdateEventArgs e)
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
            // The UI needs to be updated on the main thread.
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
                UpdateStatusMenuItems();
                if (isConnected == true)
                {
                    State.Instance.teamsRunning = true;
                }
                else
                {
                    State.Instance.teamsRunning = false;
                    _ = _mqttService.PublishConfigurations(null!, _settings);
                }

                Log.Debug("TeamsConnectionStatusChanged: Teams Connection Status Changed {status}", TeamsConnectionStatus.Text);
            });
        }

        private async void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_teamsClient == null)
            {
                // Initialize and connect the WebSocket client
                await initializeteamsconnection();
            }
            else if (!_teamsClient.IsConnected)
            {
                // If the client exists but is not connected, try reconnecting
                await _teamsClient.StartConnectionAsync(new Uri($"ws://localhost:8124?token={_settings.PlainTeamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26"));
            }
            else if (_settings.PlainTeamsToken == null)
            {
                // If connected but not paired, attempt to pair
                await _teamsClient.PairWithTeamsAsync();
            }
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
                // Update menu items
                _mqttStatusMenuItem.Header = MQTTConnectionStatus.Text; // Reuse the text set above
                _teamsStatusMenuItem.Header = _teamsClient != null && _teamsClient.IsConnected ? "Teams Status: Connected" : "Teams Status: Disconnected";
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