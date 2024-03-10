using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using System.Timers;

namespace TEAMS2HA.API
{
    public class MqttManager
    {
        #region Private Fields

        private readonly string _deviceId;
        private readonly List<string> _sensorNames;
        private readonly AppSettings _settings;
        private MqttClientWrapper _mqttClientWrapper;
        private Dictionary<string, string> _previousSensorStates;
        public event Action<string> StatusUpdated;
        public delegate Task CommandToTeamsHandler(string jsonMessage);
        public event CommandToTeamsHandler CommandToTeams;
        private System.Timers.Timer mqttPublishTimer;
        #endregion Private Fields

        #region Public Constructors

        public MqttManager(MqttClientWrapper mqttClientWrapper, AppSettings settings, List<string> sensorNames, string deviceId)
        {
            _mqttClientWrapper = mqttClientWrapper;
            _settings = settings;
            _sensorNames = sensorNames;
            _deviceId = deviceId;
            _previousSensorStates = new Dictionary<string, string>();
            InitializeConnection();
            InitializeMqttPublishTimer();
        }

        #endregion Public Constructors

        #region Public Delegates

        public delegate void ConnectionStatusChangedHandler(string status);

        #endregion Public Delegates

        #region Public Events

        public event ConnectionStatusChangedHandler ConnectionStatusChanged;

        #endregion Public Events

        #region Public Methods

        public async Task HandleIncomingCommand(MqttApplicationMessageReceivedEventArgs e)
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
                // Raise the event
                CommandToTeams?.Invoke(jsonMessage);
            }
        }
        public async Task InitializeConnection()
        {
            if (_mqttClientWrapper == null)
            {
                UpdateConnectionStatus("MQTT Client Not Initialized");
                Log.Debug("MQTT Client Not Initialized");
                return;
            }
            //check we have at least an mqtt server address
            if (string.IsNullOrEmpty(_settings.MqttAddress))
            {
                UpdateConnectionStatus("MQTT Server Address Not Set");
                Log.Debug("MQTT Server Address Not Set");
                return;
            }
            int retryCount = 0;
            const int maxRetries = 5;

            while (retryCount < maxRetries && !_mqttClientWrapper.IsConnected)
            {
                try
                {
                    await _mqttClientWrapper.ConnectAsync();
                    // Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                    await _mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);

                    _mqttClientWrapper.MessageReceived += HandleIncomingCommand;
                    if (_mqttClientWrapper.IsConnected)
                    {
                        UpdateConnectionStatus("MQTT Status: Connected");
                        Log.Debug("MQTT Client Connected in InitializeMQTTConnection");
                        await SetupMqttSensors();
                    }
                    return; // Exit the method if connected
                }
                catch (Exception ex)
                {
                    UpdateConnectionStatus($"MQTT Status: Disconnected (Retry {retryCount + 1})");

                    Log.Debug("MQTT Retrty Count {count} {message}", retryCount, ex.Message);
                    retryCount++;
                    await Task.Delay(2000); // Wait for 2 seconds before retrying
                }
            }

            UpdateConnectionStatus("MQTT Status: Disconnected (Failed to connect)");
            Log.Debug("MQTT Client Failed to Connect");
        }

        public async Task PublishConfigurations(MeetingUpdate meetingUpdate, AppSettings settings)
        {
            if (_mqttClientWrapper == null)
            {
                Log.Debug("MQTT Client Wrapper is not initialized.");
                return;
            }
            // Define common device information for all entities.
            var deviceInfo = new
            {
                ids = new[] { "teams2ha_" + _deviceId }, // Unique device identifier
                mf = "Jimmy White", // Manufacturer name
                mdl = "Teams2HA Device", // Model
                name = _deviceId, // Device name
                sw = "v1.0" // Software version
            };
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
                        HasUnreadMessages = false,
                        teamsRunning = false
                    }
                };
            }
            foreach (var sensor in _sensorNames)
            {
                string sensorKey = $"{_deviceId}_{sensor}";
                string sensorName = $"{sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(sensor);
                string icon = DetermineIcon(sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(sensor, meetingUpdate);
                string uniqueId = $"{_deviceId}_{sensor}";

                if (!_previousSensorStates.TryGetValue(sensorKey, out var previousState) || previousState != stateValue)
                {
                    _previousSensorStates[sensorKey] = stateValue; // Update the stored state

                    if (deviceClass == "switch")
                    {
                        var switchConfig = new
                        {
                            name = sensorName,
                            unique_id = uniqueId,
                            device = deviceInfo,
                            icon = icon,
                            command_topic = $"homeassistant/switch/{sensorName}/set",
                            state_topic = $"homeassistant/switch/{sensorName}/state",
                            payload_on = "ON",
                            payload_off = "OFF"
                        };
                        string configTopic = $"homeassistant/switch/{sensorName}/config";
                        if (!string.IsNullOrEmpty(configTopic) && switchConfig != null)
                        {
                            string switchConfigJson = JsonConvert.SerializeObject(switchConfig);
                            if (!string.IsNullOrEmpty(switchConfigJson))
                            {
                                await _mqttClientWrapper.PublishAsync(configTopic, switchConfigJson, true);
                            }
                            else
                            {
                                Log.Debug($"Switch configuration JSON is null or empty for sensor: {sensor}");
                            }
                        }
                        else
                        {
                            Log.Debug($"configTopic or switchConfig is null for sensor: {sensor}");
                        }
                        await _mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(switchConfig), true);
                        await _mqttClientWrapper.PublishAsync(switchConfig.state_topic, stateValue);
                    }
                    else if (deviceClass == "sensor")
                    {
                        var binarySensorConfig = new
                        {
                            name = sensorName,
                            unique_id = uniqueId,
                            device = deviceInfo,
                            icon = icon,
                            state_topic = $"homeassistant/binary_sensor/{sensorName}/state",
                            payload_on = "true",  // Assuming "True" states map to "ON"
                            payload_off = "false" // Assuming "False" states map to "OFF"
                        };
                        //string configTopic = $"homeassistant/binary_sensor/{sensorName}/config";
                        //await mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(binarySensorConfig), true);
                        //await mqttClientWrapper.PublishAsync(binarySensorConfig.state_topic, stateValue);
                        string configTopic = $"homeassistant/binary_sensor/{sensorName}/config";
                        await _mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(binarySensorConfig), true);

                        // Here's the important part: publish the initial state for each sensor
                        string stateTopic = $"homeassistant/binary_sensor/{sensorName}/state";
                        await _mqttClientWrapper.PublishAsync(stateTopic, stateValue.ToLowerInvariant()); // Convert "True"/"False" to "on"/"off" or keep "ON"/"OFF"
                    }
                }
            }
        }

        public async Task ReconnectToMqttServerAsync()
        {
            // Ensure disconnection from the current MQTT server, if connected
            if (_mqttClientWrapper != null && _mqttClientWrapper.IsConnected)
            {
                await _mqttClientWrapper.DisconnectAsync();
            }

            // Attempt to connect to the MQTT server with new settings
            await _mqttClientWrapper.ConnectAsync();  // Connect without checking in 'if'

            // Now, check if the connection was successful
            if (_mqttClientWrapper.IsConnected)  // Assuming IsConnected is a boolean property
            {
                StatusUpdated?.Invoke("Connected");
                await _mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
            }
            else
            {
                StatusUpdated?.Invoke("Disconnected");
            }
        }



        public async Task SetupMqttSensors()
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
                    HasUnreadMessages = false,
                    teamsRunning = false
                }
            };

            // Call PublishConfigurations with the dummy MeetingUpdate
            await PublishConfigurations(dummyMeetingUpdate, _settings);
        }

        public void UpdateConnectionStatus(string status)
        {
            OnConnectionStatusChanged(status);
        }

        #endregion Public Methods

        #region Protected Methods

        protected virtual void OnConnectionStatusChanged(string status)
        {
            ConnectionStatusChanged?.Invoke(status);
        }

        #endregion Protected Methods

        #region Private Methods
        private void OnMqttPublishTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_mqttClientWrapper != null && _mqttClientWrapper.IsConnected)
            {
                // Example: Publish a keep-alive message
                string keepAliveTopic = "TEAMS2HA/keepalive";
                string keepAliveMessage = "alive";
                _ = _mqttClientWrapper.PublishAsync(keepAliveTopic, keepAliveMessage);
                Log.Debug("OnMqttPublishTimerElapsed: MQTT Keep Alive Message Published");
                
            }
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
                case "teamsRunning":
                    return "sensor"; // These are true/false sensors
                default:
                    return null; // Or a default device class if appropriate
            }
        }
        public void InitializeMqttPublishTimer()
        {
            mqttPublishTimer = new System.Timers.Timer(60000); // Set the interval to 60 seconds
            mqttPublishTimer.Elapsed += OnMqttPublishTimerElapsed;
            mqttPublishTimer.AutoReset = true; // Reset the timer after it elapses
            mqttPublishTimer.Enabled = true; // Enable the timer
            Log.Debug("InitializeMqttPublishTimer: MQTT Publish Timer Initialized");
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
        private void UpdateMqttClientWrapper()
        {

            _mqttClientWrapper = new MqttClientWrapper(
                "TEAMS2HA",
                _settings.MqttAddress,
                _settings.MqttPort,
                _settings.MqttUsername,
                _settings.MqttPassword,
                _settings.UseTLS,
                _settings.IgnoreCertificateErrors,
                _settings.UseWebsockets
        );
            // Subscribe to the ConnectionStatusChanged event
            _mqttClientWrapper.ConnectionStatusChanged += UpdateConnectionStatus;
        }
        private string GetStateValue(string sensor, MeetingUpdate meetingUpdate)
        {
            switch (sensor)
            {
                case "IsMuted":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";

                case "IsVideoOn":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";

                case "IsBackgroundBlurred":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";

                case "IsHandRaised":
                    // Cast to bool and then check the value
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";

                case "IsInMeeting":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                case "HasUnreadMessages":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                case "IsRecordingOn":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                case "IsSharing":
                    // Similar casting for these properties
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                case "teamsRunning":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                default:
                    return "unknown";
            }
        }

        #endregion Private Methods

        // Additional extracted methods...
    }
}