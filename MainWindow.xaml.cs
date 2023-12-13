using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TEAMS2HA.API;
using System.Timers;
using TEAMS2HA.Properties;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.VisualBasic.Logging;

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
        public string MqttAddress { get; set; }
        public string MqttUsername { get; set; }
        public string EncryptedMqttPassword { get; set; }

    }

    public partial class MainWindow : Window
    {
        #region Private Fields
        List<string> sensorNames = new List<string>
        {
            "IsMuted", "IsVideoOn", "IsHandRaised", "IsInMeeting", "IsRecordingOn", "IsBackgroundBlurred", "IsSharing", "HasUnreadMessages"
        };
        private string _teamsApiKey;
        private API.WebSocketClient _teamsClient;
        private bool isDarkTheme = false;
        private string _settingsFilePath;
        private AppSettings _settings;
        private MqttClientWrapper mqttClientWrapper;
        private System.Timers.Timer mqttKeepAliveTimer;
        private System.Timers.Timer mqttPublishTimer;
        private string deviceid;
        private string Mqtttopic;
        private MeetingUpdate _latestMeetingUpdate;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataFolder = Path.Combine(localAppData, "YourAppName");
            Directory.CreateDirectory(appDataFolder); // Ensure the directory exists
            _settingsFilePath = Path.Combine(appDataFolder, "settings.json");
            _settings = LoadSettings();
            deviceid = System.Environment.MachineName;
            this.InitializeComponent();
            //ApplyTheme(Properties.Settings.Default.Theme);
            var Mqtttopic = deviceid;
            this.Loaded += MainPage_Loaded;
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");
            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            mqttClientWrapper = new MqttClientWrapper(
                "TEAMS2HA",
                _settings.MqttAddress,
                _settings.MqttUsername,
                _settings.EncryptedMqttPassword
            );
            InitializeConnections();
            mqttKeepAliveTimer = new System.Timers.Timer(60000); // Set interval to 60 seconds (60000 ms)
            mqttKeepAliveTimer.Elapsed += OnTimedEvent;
            mqttKeepAliveTimer.AutoReset = true;
            mqttKeepAliveTimer.Enabled = true;
            InitializeMqttPublishTimer();
            //if (!string.IsNullOrEmpty(_settings.MqttAddress))
            //{
            //    // Connect to MQTT broker if address is provided
            //    mqttClientWrapper = new MqttClientWrapper(
            //    "TEAMS2HA",
            //    _settings.MqttAddress,
            //        _settings.MqttUsername,
            //        _settings.EncryptedMqttPassword);

            //    try
            //    {
            //        _ = mqttClientWrapper.ConnectAsync();

            //        if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            //        {
            //            MQTTConnectionStatus.Text = "MQTT Status: Connected";
            //            _ = PublishSensorConfiguration();
            //        }
            //        else
            //        {
            //            MQTTConnectionStatus.Text = "MQTT Status: Disconnected";
            //        }
            //    }
            //    catch
            //    {
            //        // Optionally handle connection failure on startup
            //        Debug.WriteLine("Error connecting to MQTT broker");
            //    }
            //}
        }

        #endregion Public Constructors

        #region Public Methods

        public async Task InitializeConnections()
        {
            // Other initialization code...
            await initializeteamsconnection();
            await InitializeMQTTConnection();
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
            _teamsClient.TeamsUpdateReceived += TeamsClient_TeamsUpdateReceived;
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
        }
        private async void TeamsClient_TeamsUpdateReceived(object sender, WebSocketClient.TeamsUpdateEventArgs e)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                // Store the latest update
                _latestMeetingUpdate = e.MeetingUpdate;

                // Update sensor configurations
                await PublishConfigurations(_latestMeetingUpdate, _settings);

                // If you need to publish state messages, add that logic here as well
            }
        }




        #endregion Public Methods

        #region Private Methods
        private async void CheckMqttConnection()
        {
            if (mqttClientWrapper != null && !mqttClientWrapper.IsConnected)
            {
                try
                {
                    await mqttClientWrapper.ConnectAsync();
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                }
                catch
                {
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Disconnected");
                }
            }
        }


        private void InitializeMqttPublishTimer()
        {
            mqttPublishTimer = new System.Timers.Timer(60000); // Set the interval to 60 seconds
            mqttPublishTimer.Elapsed += OnMqttPublishTimerElapsed;
            mqttPublishTimer.AutoReset = true; // Reset the timer after it elapses
            mqttPublishTimer.Enabled = true; // Enable the timer
        }
        private void OnMqttPublishTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                // Example: Publish a keep-alive message
                string keepAliveTopic = "TEAMS2HA/keepalive";
                string keepAliveMessage = "alive";
                _ = mqttClientWrapper.PublishAsync(keepAliveTopic, keepAliveMessage);
            }
        }
        private async Task PublishDiscoveryMessages()
        {
            var muteSwitchConfig = new
            {
                name = "Teams Mute",
                unique_id = "TEAMS2HA_mute",
                state_topic = "TEAMS2HA/TEAMS/mute",
                command_topic = "TEAMS2HA/TEAMS/mute/set",
                payload_on = "true",
                payload_off = "false",
                device = new { identifiers = new[] { "TEAMS2HA" }, name = "Teams Integration", manufacturer = "Your Company" }
            };

            string muteConfigTopic = "homeassistant/switch/TEAMS2HA/mute/config";
            await mqttClientWrapper.PublishAsync(muteConfigTopic, JsonConvert.SerializeObject(muteSwitchConfig));

            // Repeat for other entities like video
        }



        // This method is called when the timer event is triggered
        private void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            // Check the MQTT connection
            CheckMqttConnection();

            // Publish the sensor configuration

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
        private async Task PublishSensorConfiguration()
        {
            // Example: Publishing a sensor configuration
            var sensorConfig = new
            {
                name = "Example Sensor",
                state_topic = "TEAMS2HA/sensor/state",
                unit_of_measurement = "units",
                device_class = "sensor",
                // Add other sensor configuration properties as needed
            };

            string configTopic = "TEAMS2HA/sensor/config";
            string configPayload = JsonConvert.SerializeObject(sensorConfig);
            await mqttClientWrapper.PublishAsync(configTopic, configPayload);
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

                string sensorName = $"{deviceid}_{sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(sensor);
                string icon = DetermineIcon(sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(sensor, meetingUpdate);

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






        private string DetermineIcon(string sensor, MeetingState state)
        {
            return sensor switch
            {
                "IsMuted" => state.IsMuted ? "mdi:microphone-off" : "mdi:microphone",
                "IsVideoOn" => state.IsVideoOn ? "mdi:camera" : "mdi:camera-off",
                "IsHandRaised" => state.IsHandRaised ? "mdi:hand-back-left" : "mdi:hand-back-left-off",
                "IsInMeeting" => state.IsInMeeting ? "mdi:account-group" : "mdi:account-off", // Example icons
                "IsRecordingOn" => state.IsRecordingOn ? "mdi:record-rec" : "mdi:record",
                "IsBackgroundBlurred" => state.IsBackgroundBlurred ? "mdi:blur" : "mdi:blur-off",
                "IsSharing" => state.IsSharing ? "mdi:screen-share" : "mdi:screen-share-off", // Example icons
                "HasUnreadMessages" => state.HasUnreadMessages ? "mdi:message-alert" : "mdi:message-outline", // Example icons
                _ => "mdi:eye"
            };
        }

        private async void PublishSensorState(MeetingUpdate meetingUpdate)
        {
            var statePayload = new
            {
                IsMuted = meetingUpdate.MeetingState.IsMuted, // Access through MeetingState
                IsVideoOn = meetingUpdate.MeetingState.IsVideoOn
                // ... other properties ...
            };

            string jsonStatePayload = JsonConvert.SerializeObject(statePayload);

            try
            {
                string stateTopic = $"homeassistant/sensor/{Mqtttopic.ToLower().Replace(" ", "_")}sensor/state";
                await mqttClientWrapper.PublishAsync(stateTopic, jsonStatePayload, retain: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error publishing MQTT state: {0}", ex.Message);
            }
        }



        private void SaveSettings()
        {
            _settings.RunAtWindowsBoot = RunAtWindowsBootCheckBox.IsChecked ?? false;
            _settings.RunMinimized = RunMinimisedCheckBox.IsChecked ?? false;
            _settings.MqttAddress = MqttAddress.Text;
            _settings.MqttUsername = MqttUserNameBox.Text;
            _settings.EncryptedMqttPassword = MQTTPasswordBox.Text;
            _settings.EncryptedMqttPassword = MQTTPasswordBox.Text;

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
            if (_teamsClient != null)
            {
                _teamsClient.TeamsUpdateReceived -= TeamsClient_TeamsUpdateReceived;
            }
            if (mqttClientWrapper != null)
            {
                mqttClientWrapper.Dispose();
            }
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
            MqttUserNameBox.Text = _settings.MqttUsername;
            MQTTPasswordBox.Text = _settings.EncryptedMqttPassword;
            MqttAddress.Text = _settings.MqttAddress;
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
        private async void PublishTeamsUpdateToMqtt(MeetingUpdate meetingUpdate)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                string topic = "your/mqtt/topic";
                string payload = JsonConvert.SerializeObject(meetingUpdate);
                await mqttClientWrapper.PublishAsync(topic, payload);
            }
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
        private async void TestMQTTConnection_Click(object sender, RoutedEventArgs e)
        {
            // Get the Homeassistant token from the HomeassistantTokenBox
            // _homeassistantToken = HomeassistantTokenBox.Text;

            // If the token is empty or null, return and do nothing

        }

        // This method is called when the TestTeamsConnection button is clicked
        private void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {
            string teamsToken = _settings.TeamsToken; // Get the Teams token from the settings

            // Create a URI with the necessary parameters for the WebSocket connection
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");

            var state = new API.State();  // You would initialize this as necessary

            // Create a new WebSocketClient with the URI, state, and settings file path
            _teamsClient = new API.WebSocketClient(uri, state, _settingsFilePath, token => this.Dispatcher.Invoke(() => TeamsApiKeyBox.Text = token));

            // Subscribe to the ConnectionStatusChanged event of the WebSocketClient
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

        public async Task InitializeMQTTConnection()
        {
            if (mqttClientWrapper == null)
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Client Not Initialized");
                return;
            }

            int retryCount = 0;
            const int maxRetries = 5;

            while (retryCount < maxRetries && !mqttClientWrapper.IsConnected)
            {
                try
                {
                    await mqttClientWrapper.ConnectAsync();
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                    return; // Exit the method if connected
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = $"MQTT Status: Disconnected (Retry {retryCount + 1})");
                    Debug.WriteLine($"Retry {retryCount + 1}: {ex.Message}");
                    retryCount++;
                    await Task.Delay(2000); // Wait for 2 seconds before retrying
                }
            }

            Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Disconnected (Failed to connect)");
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