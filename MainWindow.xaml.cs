using Microsoft.Win32;
using MQTTnet.Client;
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
using System.Text;
using System.Threading.Tasks;
using System.Timers;

using System.Windows;
using TEAMS2HA.API;
using TEAMS2HA.Properties;

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

        [JsonIgnore]
        public string MqttPassword { get; set; }
        public bool UseWebsockets { get; set; }
        [JsonIgnore]
        public string PlainTeamsToken { get; set; }
        // Properties
        public string EncryptedMqttPassword { get; set; }

        public string MqttAddress { get; set; }

        public string MqttPort { get; set; }
        public string SensorPrefix { get; set; }

        public string MqttUsername { get; set; }

        public bool RunAtWindowsBoot { get; set; }
        public bool UseTLS { get; set; }
        public bool IgnoreCertificateErrors { get; set; }

        public bool RunMinimized { get; set; }

        public string TeamsToken { get; set; }

        public string Theme { get; set; }

        #endregion Public Properties

        #region Public Methods

        // Save settings to file
        public void SaveSettingsToFile()
        {
            // Encrypt sensitive data
            if (!String.IsNullOrEmpty(this.MqttPassword))
            {
                this.EncryptedMqttPassword = CryptoHelper.EncryptString(this.MqttPassword);
            }else
            {
                this.EncryptedMqttPassword = "";
            }
            if (!String.IsNullOrEmpty(this.PlainTeamsToken))
            {
                this.TeamsToken = CryptoHelper.EncryptString(this.PlainTeamsToken);
            }else
            {
                this.TeamsToken = "";
            }
            if(string.IsNullOrEmpty(this.SensorPrefix))
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
         
            Log.Debug("SetStartupAsync: Startup set");
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

        private MeetingUpdate _latestMeetingUpdate;

        private AppSettings _settings;
        private MenuItem _mqttStatusMenuItem;
        private MenuItem _teamsStatusMenuItem;
        private MenuItem _logMenuItem;
        private MenuItem _aboutMenuItem;
        private string _settingsFilePath;
        private string _teamsApiKey;
        private API.WebSocketClient _teamsClient;
        private Action<string> _updateTokenAction;
        private string deviceid;
        private bool isDarkTheme = false;
        private MqttClientWrapper mqttClientWrapper;
        private System.Timers.Timer mqttKeepAliveTimer;
        private System.Timers.Timer mqttPublishTimer;
        private string Mqtttopic;
        private Dictionary<string, string> _previousSensorStates = new Dictionary<string, string>();

        private List<string> sensorNames = new List<string>
        {
            "IsMuted", "IsVideoOn", "IsHandRaised", "IsInMeeting", "IsRecordingOn", "IsBackgroundBlurred", "IsSharing", "HasUnreadMessages"
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
            // Create a new instance of the MqttClientWrapper class
            mqttClientWrapper = new MqttClientWrapper(
                "TEAMS2HA",
                _settings.MqttAddress,
                _settings.MqttPort,
                _settings.MqttUsername,
                _settings.MqttPassword,
                _settings.UseTLS,
                _settings.IgnoreCertificateErrors
            );

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

            // Create a timer for MQTT keep alive
            mqttKeepAliveTimer = new System.Timers.Timer(60000); // Set interval to 60 seconds (60000 ms)
            mqttKeepAliveTimer.Elapsed += OnTimedEvent;
            mqttKeepAliveTimer.AutoReset = true;
            mqttKeepAliveTimer.Enabled = true;

            // Initialize the MQTT publish timer
            InitializeMqttPublishTimer();
            
        }

        #endregion Public Constructors

        #region Public Methods

        public async Task InitializeConnections()
        {
            await InitializeMQTTConnection();
            // Other initialization code...
            await initializeteamsconnection();

            // Other initialization code...
        }

        public async Task InitializeMQTTConnection()
        {
            if (mqttClientWrapper == null)
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Client Not Initialized");
                Log.Debug("MQTT Client Not Initialized");
                return;
                
            }
            //check we have at least an mqtt server address
            if (string.IsNullOrEmpty(_settings.MqttAddress))
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Server Address Not Set");
                Log.Debug("MQTT Server Address Not Set");
                return;
            }
            int retryCount = 0;
            const int maxRetries = 5;
            mqttClientWrapper.ConnectionStatusChanged += UpdateMqttConnectionStatus;
            while (retryCount < maxRetries && !mqttClientWrapper.IsConnected)
            {
                try
                {
                    await mqttClientWrapper.ConnectAsync();
                    // Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                    await mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                    
                    mqttClientWrapper.MessageReceived += HandleIncomingCommand;
                    if (mqttClientWrapper.IsConnected)
                    { 
                        Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                        Log.Debug("MQTT Client Connected in InitializeMQTTConnection");
                        SetupMqttSensors();
                    }
                    return; // Exit the method if connected
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = $"MQTT Status: Disconnected (Retry {retryCount + 1})");

                    Log.Debug("MQTT Retrty Count {count} {message}", retryCount, ex.Message);
                    retryCount++;
                    await Task.Delay(2000); // Wait for 2 seconds before retrying
                }
            }

            Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Disconnected (Failed to connect)");
            Log.Debug("MQTT Client Failed to Connect");
        }

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_teamsClient != null)
            {
                _teamsClient.TeamsUpdateReceived -= TeamsClient_TeamsUpdateReceived;
                Log.Debug("Teams Client Disconnected");
            }
            if (mqttClientWrapper != null)
            {
                mqttClientWrapper.Dispose();
                Log.Debug("MQTT Client Disposed");
            }
            MyNotifyIcon.Dispose();
            base.OnClosing(e);
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
        private void UpdateMqttConnectionStatus(string status)
        {
            Dispatcher.Invoke(() => MQTTConnectionStatus.Text = status);
        }
        private void UpdateMqttClientWrapper()
        {

            mqttClientWrapper = new MqttClientWrapper(
            "TEAMS2HA",
            _settings.MqttAddress,
            _settings.MqttPort,
            _settings.MqttUsername,
            _settings.MqttPassword,
            _settings.UseTLS,
            _settings.IgnoreCertificateErrors
        );


            // Subscribe to the ConnectionStatusChanged event
            mqttClientWrapper.ConnectionStatusChanged += UpdateMqttConnectionStatus;
        }
        private async Task ReconnectToMqttServerAsync()
        {
            // Ensure disconnection from the current MQTT server, if connected
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                await mqttClientWrapper.DisconnectAsync();
            }

            // Update the MQTT client wrapper with new settings
            UpdateMqttClientWrapper();

            // Attempt to connect to the MQTT server with new settings
            await mqttClientWrapper.ConnectAsync();
            //we need to subscribe again (Thanks to @egglestron for pointing this out!)
            await mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
        }
       

        private void SetWindowTitle()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            this.Title = $"Teams2HA - Version {version}";
        }
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Log.Information("System is waking up from sleep. Re-establishing connections...");
                // Implement logic to re-establish connections
                ReestablishConnections();
            }
        }
        private async void ReestablishConnections()
        {
            try
            {
                if (!mqttClientWrapper.IsConnected)
                {
                    await mqttClientWrapper.ConnectAsync();
                    await mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                    SetupMqttSensors();
                }
                if (!_teamsClient.IsConnected)
                {
                    await initializeteamsconnection();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error re-establishing connections: {ex.Message}");
            }
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

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string currentTheme = _settings.Theme; // Assuming this is where the theme is stored
            var aboutWindow = new AboutWindow(deviceid, MyNotifyIcon);
            aboutWindow.Owner = this;
            aboutWindow.ShowDialog();
        }


        private void UpdateStatusMenuItems()
        {
            _mqttStatusMenuItem.Header = mqttClientWrapper != null && mqttClientWrapper.IsConnected ? "MQTT Status: Connected" : "MQTT Status: Disconnected";
            _teamsStatusMenuItem.Header = _teamsClient != null && _teamsClient.IsConnected ? "Teams Status: Connected" : "Teams Status: Disconnected";
        }
        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Handle the click event for the exit menu item (Close the application)
            Application.Current.Shutdown();
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

        private async void CheckMqttConnection()
        {
            if (mqttClientWrapper != null && !mqttClientWrapper.IsConnected && !mqttClientWrapper.IsAttemptingConnection)
            {
                Log.Debug("CheckMqttConnection: MQTT Client Not Connected. Attempting reconnection.");
                await mqttClientWrapper.ConnectAsync();
                await mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                UpdateConnectionStatus();
            }
        }

        private void UpdateConnectionStatus()
        {
            Dispatcher.Invoke(() =>
            {
                MQTTConnectionStatus.Text = mqttClientWrapper.IsConnected ? "MQTT Status: Connected" : "MQTT Status: Disconnected";
                UpdateStatusMenuItems();
            });
        }


        private string DetermineDeviceClass(string sensor)
        {
            switch (sensor)
            {
                case "IsMuted":
                case "IsVideoOn":
                case "IsHandRaised":
                case "IsBackgroundBlurred":
                    return "switch"; // These are ON/OFF switches
                case "IsInMeeting":
                case "HasUnreadMessages":
                case "IsRecordingOn":
                case "IsSharing":
                    return "sensor"; // These are true/false sensors
                default:
                    return null; // Or a default device class if appropriate
            }
        }

        // This method determines the appropriate icon based on the sensor and meeting state
        private string DetermineIcon(string sensor, MeetingState state)
        {
            return sensor switch
            {
                // If the sensor is "IsMuted", return "mdi:microphone-off" if state.IsMuted is true,
                // otherwise return "mdi:microphone"
                "IsMuted" => state.IsMuted ? "mdi:microphone-off" : "mdi:microphone",

                // If the sensor is "IsVideoOn", return "mdi:camera" if state.IsVideoOn is true,
                // otherwise return "mdi:camera-off"
                "IsVideoOn" => state.IsVideoOn ? "mdi:camera" : "mdi:camera-off",

                // If the sensor is "IsHandRaised", return "mdi:hand-back-left" if
                // state.IsHandRaised is true, otherwise return "mdi:hand-back-left-off"
                "IsHandRaised" => state.IsHandRaised ? "mdi:hand-back-left" : "mdi:hand-back-left-off",

                // If the sensor is "IsInMeeting", return "mdi:account-group" if state.IsInMeeting
                // is true, otherwise return "mdi:account-off"
                "IsInMeeting" => state.IsInMeeting ? "mdi:account-group" : "mdi:account-off",

                // If the sensor is "IsRecordingOn", return "mdi:record-rec" if state.IsRecordingOn
                // is true, otherwise return "mdi:record"
                "IsRecordingOn" => state.IsRecordingOn ? "mdi:record-rec" : "mdi:record",

                // If the sensor is "IsBackgroundBlurred", return "mdi:blur" if
                // state.IsBackgroundBlurred is true, otherwise return "mdi:blur-off"
                "IsBackgroundBlurred" => state.IsBackgroundBlurred ? "mdi:blur" : "mdi:blur-off",

                // If the sensor is "IsSharing", return "mdi:monitor-share" if state.IsSharing is
                // true, otherwise return "mdi:monitor-off"
                "IsSharing" => state.IsSharing ? "mdi:monitor-share" : "mdi:monitor-off",

                // If the sensor is "HasUnreadMessages", return "mdi:message-alert" if
                // state.HasUnreadMessages is true, otherwise return "mdi:message-outline"
                "HasUnreadMessages" => state.HasUnreadMessages ? "mdi:message-alert" : "mdi:message-outline",

                // If the sensor does not match any of the above cases, return "mdi:eye"
                _ => "mdi:eye"
            };
        }

        private string GetStateValue(string sensor, MeetingUpdate meetingUpdate)
        {
            switch (sensor)
            {
                case "IsMuted":
                case "IsVideoOn":
                case "IsBackgroundBlurred":
                case "IsHandRaised":
                    // Cast to bool and then check the value
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";

                case "IsInMeeting":
                case "HasUnreadMessages":
                case "IsRecordingOn":
                case "IsSharing":
                    // Similar casting for these properties
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                default:
                    return "unknown";
            }
        }

        private async Task HandleIncomingCommand(MqttApplicationMessageReceivedEventArgs e)
        {
            string topic = e.ApplicationMessage.Topic;
            Log.Debug("HandleIncomingCommand: MQTT Topic {topic}", topic);
            // Check if it's a command topic and handle accordingly
            if (topic.StartsWith("homeassistant/switch/") && topic.EndsWith("/set"))
            {
                string command = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                // Parse and handle the command
                HandleSwitchCommand(topic, command);
            }
        }

        private async void HandleSwitchCommand(string topic, string command)
        {
            // Determine which switch is being controlled based on the topic
            string switchName = topic.Split('/')[2]; // Assuming topic format is "homeassistant/switch/{switchName}/set"
            int underscoreIndex = switchName.IndexOf('_');
            if (underscoreIndex != -1 && underscoreIndex < switchName.Length - 1)
            {
                switchName = switchName.Substring(underscoreIndex + 1);
            }
            string jsonMessage = "";
            switch (switchName)
            {
                case "ismuted":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"toggle-mute\",\"action\":\"toggle-mute\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;

                case "isvideoon":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"toggle-video\",\"action\":\"toggle-video\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;

                case "isbackgroundblurred":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"background-blur\",\"action\":\"toggle-background-blur\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;

                case "ishandraised":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"raise-hand\",\"action\":\"toggle-hand\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;

                    // Add other cases as needed
            }

            if (!string.IsNullOrEmpty(jsonMessage))
            {
                // Send the message to Teams
                await _teamsClient.SendMessageAsync(jsonMessage);
            }
        }

        private void InitializeMqttPublishTimer()
        {
            mqttPublishTimer = new System.Timers.Timer(60000); // Set the interval to 60 seconds
            mqttPublishTimer.Elapsed += OnMqttPublishTimerElapsed;
            mqttPublishTimer.AutoReset = true; // Reset the timer after it elapses
            mqttPublishTimer.Enabled = true; // Enable the timer
            Log.Debug("InitializeMqttPublishTimer: MQTT Publish Timer Initialized");
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
                _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
                _teamsClient.TeamsUpdateReceived += TeamsClient_TeamsUpdateReceived;
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
            if(string.IsNullOrEmpty(_settings.SensorPrefix))
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
            UpdateStatusMenuItems();
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe when the page is unloaded
            _teamsClient.ConnectionStatusChanged -= TeamsConnectionStatusChanged;
            Log.Debug("MainPage_Unloaded: Teams Client Connection Status unsubscribed");
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

        private void OnMqttPublishTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                // Example: Publish a keep-alive message
                string keepAliveTopic = "TEAMS2HA/keepalive";
                string keepAliveMessage = "alive";
                _ = mqttClientWrapper.PublishAsync(keepAliveTopic, keepAliveMessage);
                Log.Debug("OnMqttPublishTimerElapsed: MQTT Keep Alive Message Published");
            }
        }

        private void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            // Check the MQTT connection
            CheckMqttConnection();
        }

        private async Task PublishConfigurations(MeetingUpdate meetingUpdate, AppSettings settings)
        {
            foreach (var sensor in sensorNames)
            {
                var device = new Device()
                {
                    Identifiers = deviceid,
                    Name = deviceid,
                    SwVersion = "1.0.0",
                    Model = "Teams2HA",
                    Manufacturer = "JimmyWhite",
                };
                // added to check if meeting update is null
                if (meetingUpdate == null)
                {
                    meetingUpdate = new MeetingUpdate
                    {
                        MeetingState = new MeetingState
                        {
                            IsMuted = false,
                            IsVideoOn = false,
                            IsHandRaised = false,
                            IsInMeeting = false,
                            IsRecordingOn = false,
                            IsBackgroundBlurred = false,
                            IsSharing = false,
                            HasUnreadMessages = false
                        }
                    };
                }
                string sensorKey = $"{deviceid}_{sensor}";
                string sensorName = $"{deviceid}_{sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(sensor);
                string icon = DetermineIcon(sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(sensor, meetingUpdate);
                if (!_previousSensorStates.TryGetValue(sensorKey, out var previousState) || previousState != stateValue)
                {
                    _previousSensorStates[sensorKey] = stateValue; // Update the stored state

                    if (deviceClass == "switch")
                    {
                        string stateTopic = $"homeassistant/switch/{sensorName}/state";
                        string commandTopic = $"homeassistant/switch/{sensorName}/set";
                        var switchConfig = new
                        {
                            name = sensorName,
                            unique_id = sensorName,
                            state_topic = stateTopic,
                            command_topic = commandTopic,
                            payload_on = "ON",
                            payload_off = "OFF",
                            icon = icon
                        };
                        string configTopic = $"homeassistant/switch/{sensorName}/config";
                        await mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(switchConfig));
                        await mqttClientWrapper.PublishAsync(stateTopic, stateValue);
                    }
                    else if (deviceClass == "sensor") // Use else-if for binary_sensor
                    {
                        string stateTopic = $"homeassistant/sensor/{sensorName}/state"; // Corrected state topic
                        var binarySensorConfig = new
                        {
                            name = sensorName,
                            unique_id = sensorName,
                            state_topic = stateTopic,

                            icon = icon,
                            Device = device,
                        };
                        string configTopic = $"homeassistant/sensor/{sensorName}/config";
                        await mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(binarySensorConfig), true);
                        await mqttClientWrapper.PublishAsync(stateTopic, stateValue); // Publish the state
                    }
                }
            }

        }


        private async Task SaveSettingsAsync()
        {
            var settings = AppSettings.Instance;

            // Gather the current settings from UI components (make sure to do this on the UI thread)
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
                if (string.IsNullOrEmpty(SensorPrefixBox.Text))
                {
                    settings.SensorPrefix = System.Environment.MachineName;
                    SensorPrefixBox.Text = System.Environment.MachineName;
                }
                else { settings.SensorPrefix = SensorPrefixBox.Text;}
               
                
            });
            
            // Check if MQTT settings have changed (consider abstracting this logic into a separate method)
            bool mqttSettingsChanged = CheckIfMqttSettingsChanged(settings);
            // Check if Sensore Prefix has changed
            bool sensorPrefixChanged = CheckIfSensorPrefixChanged(settings);

            // Save the updated settings to file
            settings.SaveSettingsToFile();

           
            await ReconnectToMqttServerAsync();
            await PublishConfigurations(_latestMeetingUpdate, _settings);

        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            Log.Debug("SaveSettings_Click: Save Settings Clicked" + _settings.ToString);
            foreach(var setting in _settings.GetType().GetProperties())
            {
                Log.Debug(setting.Name + " " + setting.GetValue(_settings));
            }
            await SaveSettingsAsync();
        }

        private async Task SetStartupAsync(bool startWithWindows)
        {
           
        }

        private async void SetupMqttSensors()
        {
            // Create a dummy MeetingUpdate with default values
            var dummyMeetingUpdate = new MeetingUpdate
            {
                MeetingState = new MeetingState
                {
                    IsMuted = false,
                    IsVideoOn = false,
                    IsHandRaised = false,
                    IsInMeeting = false,
                    IsRecordingOn = false,
                    IsBackgroundBlurred = false,
                    IsSharing = false,
                    HasUnreadMessages = false
                }
            };

            // Call PublishConfigurations with the dummy MeetingUpdate
            await PublishConfigurations(dummyMeetingUpdate, _settings);


        }

        private async void TeamsClient_TeamsUpdateReceived(object sender, WebSocketClient.TeamsUpdateEventArgs e)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                // Store the latest update
                _latestMeetingUpdate = e.MeetingUpdate;
                Log.Debug("TeamsClient_TeamsUpdateReceived: Teams Update Received {update}", _latestMeetingUpdate);
                // Update sensor configurations
                await PublishConfigurations(_latestMeetingUpdate, _settings);
            }
        }

        private void TeamsConnectionStatusChanged(bool isConnected)
        {
            // The UI needs to be updated on the main thread.
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
                UpdateStatusMenuItems();
                Log.Debug("TeamsConnectionStatusChanged: Teams Connection Status Changed {status}", TeamsConnectionStatus.Text);
            });
        }

        private async void TestMQTTConnection_Click(object sender, RoutedEventArgs e)
        {
            Log.Debug("Testing MQTT COnnection");
            if (mqttClientWrapper == null)
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Client Not Initialized");
                UpdateStatusMenuItems();
                Log.Debug("TestMQTTConnection_Click: MQTT Client Not Initialized");
                return;
            }
            //we need to test to see if we are already connected
            if (mqttClientWrapper.IsConnected)
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                UpdateStatusMenuItems();
                Log.Debug("TestMQTTConnection_Click: MQTT Client Connected in testmqttconnection");
                return;
            }
            //make sure we have an mqtt address
            if (string.IsNullOrEmpty(_settings.MqttAddress))
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Server Address Not Set");
                UpdateStatusMenuItems();
                Log.Debug("TestMQTTConnection_Click: MQTT Server Address Not Set");
                return;
            }
            //we are not connected so lets try to connect
            int retryCount = 0;
            const int maxRetries = 5;

            while (retryCount < maxRetries && !mqttClientWrapper.IsConnected)
            {
                try
                {
                    await mqttClientWrapper.ConnectAsync();
                    if (mqttClientWrapper.IsConnected)
                    {
                        Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                    }
                    UpdateStatusMenuItems();
                    Log.Debug("TestMQTTConnection_Click: MQTT Client Connected in TestMQTTConnection_Click");
                    await mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                    return; // Exit the method if connected
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = $"MQTT Status: Disconnected (Retry {retryCount + 1})");
                    Log.Debug("TestMQTTConnection_Click: MQTT Client Failed to Connect {message}", ex.Message);
                    UpdateStatusMenuItems();
                    retryCount++;
                    await Task.Delay(2000); // Wait for 2 seconds before retrying
                }
            }

            Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Disconnected (Failed to connect)");
            Log.Debug("TestMQTTConnection_Click: MQTT Client Failed to Connect");
            UpdateStatusMenuItems();
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
        private bool CheckIfMqttSettingsChanged(AppSettings newSettings)
        {
            var currentSettings = AppSettings.Instance;
            return newSettings.MqttAddress != currentSettings.MqttAddress ||
                   newSettings.MqttPort != currentSettings.MqttPort ||
                   newSettings.MqttUsername != currentSettings.MqttUsername ||
                   newSettings.MqttPassword != currentSettings.MqttPassword ||
                   newSettings.UseTLS != currentSettings.UseTLS ||
                   newSettings.IgnoreCertificateErrors != currentSettings.IgnoreCertificateErrors;
        }
        private bool CheckIfSensorPrefixChanged(AppSettings newSettings)
        {
            var currentSettings = AppSettings.Instance;
            deviceid = newSettings.SensorPrefix;
            return newSettings.SensorPrefix != currentSettings.SensorPrefix;
            
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

        #endregion Private Methods

        private void Websockets_Checked(object sender, RoutedEventArgs e)
        {
           
                _settings.UseWebsockets = true;
                // Disable the mqtt port box
                MqttPort.IsEnabled = false;
                
          
        }
        private void Websockets_Unchecked(object sender, RoutedEventArgs e)
        {
            
                _settings.UseWebsockets = false;
                MqttPort.IsEnabled = true;
            
        }
    }
}