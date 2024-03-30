using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace TEAMS2HA.API
{
    public class MqttService
    {
        #region Private Fields

        private const int MaxConnectionRetries = 2;
        private const int RetryDelayMilliseconds = 1000;
        private string _deviceId;
        private bool _isAttemptingConnection = false;
        private MqttClient _mqttClient;
        private bool _mqttClientsubscribed = false;
        private MqttClientOptions _mqttOptions;
        private Dictionary<string, string> _previousSensorStates;
        private List<string> _sensorNames;
        private AppSettings _settings;
        private HashSet<string> _subscribedTopics = new HashSet<string>();
        private System.Timers.Timer mqttPublishTimer;
        private bool mqttPublishTimerset = false;
        private dynamic _deviceInfo;
        private readonly object connectionLock = new object();
        private static MqttService _instance;
        private static readonly object _lock = new object();
        private bool isShuttingDown = false; // Flag to indicate shutdow

        #endregion Private Fields

        #region Public Constructors

        private MqttService(AppSettings settings, string deviceId, List<string> sensorNames)
        {
            _settings = settings;
            _deviceId = deviceId;
            _sensorNames = sensorNames;
            _previousSensorStates = new Dictionary<string, string>();
            _deviceInfo = new
            {
                ids = new[] { $"teams2ha_{_deviceId}" },
                mf = "Jimmy White",
                mdl = "Teams2HA Device",
                name = _deviceId,
                sw = "v1.0"
            };
            InitializeClient();
            InitializeMqttPublishTimer();
        }

        #endregion Public Constructors

        #region Public Delegates

        public delegate Task CommandToTeamsHandler(string jsonMessage);

        #endregion Public Delegates

        #region Public Events

        public static MqttService GetInstance(AppSettings settings, string deviceId, List<string> sensorNames)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new MqttService(settings, deviceId, sensorNames);
                    }
                }
            }
            return _instance;
        }

        public event CommandToTeamsHandler CommandToTeams;

        public event Action<string> ConnectionAttempting;

        public event Action<string> ConnectionStatusChanged;

        public event Action Disconnected;

        public event Func<MqttApplicationMessageReceivedEventArgs, Task> MessageReceived;

        public event Action<string> StatusUpdated;

        #endregion Public Events

        #region Public Properties

        public bool IsAttemptingConnection //gets if the MQTT client is attempting to connect
        {
            get { return _isAttemptingConnection; }
            private set { _isAttemptingConnection = value; }
        }

        public bool IsConnected => _mqttClient.IsConnected; //gets if the MQTT client is connected

        public void BeginShutdownProcess()
        {
            isShuttingDown = true;
            _ = TurnOffAllDevicesAsync()
            .ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    // Log or handle exceptions from TurnOffAllDevicesAsync
                    Log.Error($"Error turning off devices: {task.Exception.GetBaseException().Message}");
                }

                // Then continue with disconnection and cleanup
                DisconnectAsync().Wait();
                Dispose();
            }, TaskScheduler.Default);
        }

        #endregion Public Properties

        #region Public Methods

        public async Task PublishPermissionSensorsAsync()
        {
            var permissions = new Dictionary<string, bool>
            {
                { "canToggleMute", State.Instance.CanToggleMute },
                { "canToggleVideo", State.Instance.CanToggleVideo },
                { "canToggleHand", State.Instance.CanToggleHand },
                { "canToggleBlur", State.Instance.CanToggleBlur },
                { "canLeave", State.Instance.CanLeave },
                { "canReact", State.Instance.CanReact},
                { "canToggleShareTray", State.Instance.CanToggleShareTray },
                { "canToggleChat", State.Instance.CanToggleChat },
                { "canStopSharing", State.Instance.CanStopSharing },
                { "canPair", State.Instance.CanPair}
                // Add other permissions here
            };

            foreach (var permission in permissions)
            {
                string sensorName = permission.Key.ToLower();
                bool isAllowed = permission.Value;

                string configTopic = $"homeassistant/binary_sensor/{_deviceId}/{sensorName}/config";
                var configPayload = new
                {
                    name = sensorName,
                    unique_id = $"{_deviceId}_{sensorName}",
                    device = _deviceInfo,
                    icon = "mdi:eye", // You can customize the icon based on the sensor
                    state_topic = $"homeassistant/binary_sensor/{_deviceId}/{sensorName}/state",
                    payload_on = "true",
                    payload_off = "false"
                };

                var configMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(configTopic)
                    .WithPayload(JsonConvert.SerializeObject(configPayload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true)
                    .Build();
                await PublishAsync(configMessage);

                // State topic and message
                string stateTopic = $"homeassistant/binary_sensor/{_deviceId}/{sensorName}/state";
                string statePayload = isAllowed ? "true" : "false"; // Adjust based on your true/false representation
                var stateMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(stateTopic)
                    .WithPayload(statePayload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true)
                    .Build();
                await PublishAsync(stateMessage);
            }
        }

        public static List<string> GetEntityNames(string deviceId) //gets the entity names for the device
        {
            var entityNames = new List<string>
                {
                    $"switch.{deviceId}_ismuted",
                    $"switch.{deviceId}_isvideoon",
                    $"switch.{deviceId}_ishandraised",
                    $"binary_sensor.{deviceId}_isrecordingon",
                    $"binary_sensor.{deviceId}_isinmeeting",
                    $"binary_sensor.{deviceId}_issharing",
                    $"binary_sensor.{deviceId}_hasunreadmessages",
                    $"switch.{deviceId}_isbackgroundblurred",
                    $"binary_sensor.{deviceId}_teamsRunning"
                };

            return entityNames;
        }

        public async Task SetupSubscriptionsAsync()
        {
            // Subscribe to necessary topics
            await SubscribeAsync($"homeassistant/switch/{_settings.SensorPrefix}/+/set", MqttQualityOfServiceLevel.AtLeastOnce, true);
            await SubscribeToReactionButtonsAsync();
            // Add any other necessary subscriptions here
        }

        public async Task ConnectAsync() //connects to MQTT
        {
            lock (connectionLock)
            {
                if (_isAttemptingConnection || _mqttClient.IsConnected) return;
                _isAttemptingConnection = true;
            }
            //Check if MQTT client is already connected
            if (_mqttClient.IsConnected)
            {
                Log.Information("MQTT client is already connected ");
                return;
            }

            _isAttemptingConnection = true;
            ConnectionAttempting?.Invoke("MQTT Status: Connecting...");
            int retryCount = 0;

            // Retry connecting to MQTT broker up to a maximum number of times
            while (retryCount < MaxConnectionRetries && !_mqttClient.IsConnected)
            {
                try
                {
                    Log.Information($"Attempting to connect to MQTT (Attempt {retryCount + 1}/{MaxConnectionRetries})");
                    await _mqttClient.ConnectAsync(_mqttOptions);
                    Log.Information("Connected to MQTT broker.");
                    if (_mqttClient.IsConnected)
                    {
                        ConnectionStatusChanged?.Invoke("MQTT Status: Connected");
                        await PublishPermissionSensorsAsync();
                        await PublishReactionButtonsAsync();
                        await SetupSubscriptionsAsync();
                        break; // Exit the loop if successfully connected
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to connect to MQTT broker: {ex.Message}");
                    ConnectionStatusChanged?.Invoke($"MQTT Status: Disconnected (Retry {retryCount + 1}) {ex.Message}");
                    retryCount++;
                    await Task.Delay(RetryDelayMilliseconds); // Delay before retrying
                }
            }

            _isAttemptingConnection = false;
            // Notify if failed to connect after all retry attempts
            if (!_mqttClient.IsConnected)
            {
                ConnectionStatusChanged?.Invoke("MQTT Status: Disconnected (Failed to connect)");
                Log.Error("Failed to connect to MQTT broker after several attempts.");
            }
            lock (connectionLock)
            {
                _isAttemptingConnection = false;
            }
        }

        public async Task CheckConnectionHealthAsync()
        {
            if (!_mqttClient.IsConnected)
            {
                Log.Information("MQTT client is not connected.");
                await ReconnectAsync();
            }
            // Simple version: Just check if the client believes it's connected
        }

        public async Task DisconnectAsync() //disconnects from MQTT
        {
            if (!_mqttClient.IsConnected)
            {
                Log.Debug("MQTTClient is not connected");
                ConnectionStatusChanged?.Invoke("MQTTClient is not connected");
                return;
            }

            try
            {
                await _mqttClient.DisconnectAsync();
                Log.Information("MQTT Disconnected");
                ConnectionStatusChanged?.Invoke("MQTTClient is not connected");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to disconnect from MQTT broker: {ex.Message}");
            }
        }

        public void Dispose() //disposes the MQTT client
        {
            if (_mqttClient != null)
            {
                _ = _mqttClient.DisconnectAsync(); // Disconnect asynchronously
                _mqttClient.Dispose();
                Log.Information("MQTT Client Disposed");
            }
        }

        public void InitializeMqttPublishTimer() //initializes the MQTT publish timer
        {
            mqttPublishTimer = new System.Timers.Timer(15000); // Set the interval to 60 seconds
            if (mqttPublishTimerset == false)
            {
                mqttPublishTimer.Elapsed += OnMqttPublishTimerElapsed;
                mqttPublishTimer.AutoReset = true; // Reset the timer after it elapses
                mqttPublishTimer.Enabled = true; // Enable the timer
                mqttPublishTimerset = true;
                Log.Debug("InitializeMqttPublishTimer: MQTT Publish Timer Initialized");
            }
            else
            {
                Log.Debug("InitializeMqttPublishTimer: MQTT Publish Timer already set");
            }
            //mqttPublishTimer.Elapsed += OnMqttPublishTimerElapsed;
            //mqttPublishTimer.AutoReset = true; // Reset the timer after it elapses
            //mqttPublishTimer.Enabled = true; // Enable the timer
            //Log.Debug("InitializeMqttPublishTimer: MQTT Publish Timer Initialized");
        }

        public async Task UnsubscribeAsync(string topic) //unsubscribes from a topic on MQTT
        {
            if (!_subscribedTopics.Contains(topic))
            {
                Log.Information($"Not subscribed to {topic}, no need to unsubscribe.");
                return;
            }

            try
            {
                // Create the unsubscribe options, similar to how subscription options were created
                var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder()
                    .WithTopicFilter(topic) // Add the topic from which to unsubscribe
                    .Build();

                // Perform the unsubscribe operation
                await _mqttClient.UnsubscribeAsync(unsubscribeOptions);

                // Remove the topic from the local tracking set
                _subscribedTopics.Remove(topic);

                Log.Information($"Successfully unsubscribed from {topic}.");
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT unsubscribe: {ex.Message}");
                // Depending on your error handling strategy, you might want to handle this differently
                // For example, you might want to throw the exception to let the caller know the unsubscribe failed
            }
        }

        public async Task PublishAsync(MqttApplicationMessage message) //publishes a message to MQTT
        {
            // check if we are connected
            if (!_mqttClient.IsConnected)
            {
                Log.Information("Cant publish MQTT client is not connected.");
                return;
            }
            try
            {
                await _mqttClient.PublishAsync(message, CancellationToken.None); // Note: Add using System.Threading; if CancellationToken is undefined
                //Log.Information("Publish successful." + message.Topic);
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT publish: {ex.Message}");
                await CheckConnectionHealthAsync();
                if (!_mqttClient.IsConnected)
                {
                    Log.Information("Reconnecting to MQTT");
                    Disconnected?.Invoke();
                }
            }
        }

        public async Task SubscribeToAllTopicsAsync()
        {
            // Subscribe to switch set commands
            await SubscribeAsync($"homeassistant/switch/{_deviceId}/+/set", MqttQualityOfServiceLevel.AtLeastOnce, true);

            // Subscribe to reaction buttons
            await SubscribeToReactionButtonsAsync();

            // You can add more subscriptions here as needed
        }

        public async Task PublishConfigurations(MeetingUpdate meetingUpdate, AppSettings settings, bool forcePublish = false) //publishes the configurations to MQTT
        {
            if (_mqttClient == null)
            {
                Log.Debug("MQTT Client Wrapper is not initialized.");
                return;
            }
            // Define common device information for all entities.

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
            foreach (var binary_sensor in _sensorNames)
            {
                string sensorKey = $"{_deviceId}_{binary_sensor}";
                string sensorName = $"{binary_sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(binary_sensor);
                string icon = DetermineIcon(binary_sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(binary_sensor, meetingUpdate);
                string uniqueId = $"{_deviceId}_{binary_sensor}";
                string configTopic;
                string stateTopic;
                if (forcePublish || !_previousSensorStates.TryGetValue(sensorKey, out var previousState) || previousState != stateValue)

                {
                    Log.Information($"Force Publishing configuration for {sensorName} with state {stateValue}.");

                    _previousSensorStates[sensorKey] = stateValue; // Update the stored state
                    if (forcePublish)
                    {
                        Log.Information($"Forced publish of {sensorName} state: {stateValue} Due to change in broker");
                    }
                    if (deviceClass == "switch")
                    {
                        configTopic = $"homeassistant/switch/{_deviceId}/{sensorName}/config";
                        stateTopic = $"homeassistant/switch/{_deviceId}/{sensorName}/state";
                        var switchConfig = new
                        {
                            name = sensorName,
                            unique_id = uniqueId,
                            device = _deviceInfo,
                            icon = icon,
                            command_topic = $"homeassistant/switch/{_deviceId}/{sensorName}/set",
                            state_topic = stateTopic,
                            payload_on = "ON",
                            payload_off = "OFF"
                        };
                        var switchConfigMessage = new MqttApplicationMessageBuilder()
                             .WithTopic(configTopic)
                             .WithPayload(JsonConvert.SerializeObject(switchConfig))
                             .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                             .WithRetainFlag(true)
                             .Build();
                        Log.Information($"Publishing configuration for {sensorName} with state {stateValue}.");
                        await PublishAsync(switchConfigMessage);

                        var stateMessage = new MqttApplicationMessageBuilder()
                            .WithTopic(switchConfig.state_topic)
                            .WithPayload(stateValue)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag(true)
                            .Build();

                        await PublishAsync(stateMessage);
                    }
                    else if (deviceClass == "binary_sensor")
                    {
                        configTopic = $"homeassistant/binary_sensor/{_deviceId}/{sensorName}/config";
                        stateTopic = $"homeassistant/binary_sensor/{_deviceId}/{sensorName}/state";

                        var binarySensorConfig = new
                        {
                            name = sensorName,
                            unique_id = uniqueId,
                            device = _deviceInfo,
                            icon = icon,
                            state_topic = stateTopic,
                            payload_on = "true",  // Assuming "True" states map to "ON"
                            payload_off = "false" // Assuming "False" states map to "OFF"
                        };
                        var binarySensorConfigMessage = new MqttApplicationMessageBuilder()
                             .WithTopic(configTopic)
                             .WithPayload(JsonConvert.SerializeObject(binarySensorConfig))
                             .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                             .WithRetainFlag(true)
                             .Build();
                        Log.Information($"Publishing configuration for {sensorName} with state {stateValue}.");
                        await PublishAsync(binarySensorConfigMessage);

                        var binarySensorStateMessage = new MqttApplicationMessageBuilder()
                            .WithTopic(binarySensorConfig.state_topic)
                            .WithPayload(stateValue.ToLowerInvariant()) // Ensure the state value is in the correct format
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag(true)
                            .Build();

                        await PublishAsync(binarySensorStateMessage);
                        await PublishPermissionSensorsAsync();
                        await PublishReactionButtonsAsync();
                    }
                }
            }
        }

        public async Task ReconnectAsync()
        {
            if (isShuttingDown)
            {
                return;
            }
            int attempt = 0;
            while (!_mqttClient.IsConnected && attempt < MaxConnectionRetries)
            {
                attempt++;
                try
                {
                    Log.Information($"Reconnection attempt {attempt}...");
                    var result = await _mqttClient.ConnectAsync(_mqttOptions);
                    if (result.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        Log.Information("Reconnected to MQTT broker.");
                        return; // Successfully reconnected
                    }
                    else
                    {
                        Log.Warning($"Reconnection failed with result code: {result.ResultCode}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Reconnection attempt failed: {ex.Message}");
                }
                await Task.Delay(RetryDelayMilliseconds * attempt); // Exponential back-off
            }
        }

        public async Task SetupMqttSensors() //sets up the MQTT sensors
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

        public async Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qos, bool force) //subscribes to a topic on MQTT
        {
            // check if we are connected
            if (!_mqttClient.IsConnected)
            {
                Log.Information("Cant subscribe MQTT client is not connected.");
                return;
            }
            // Check if already subscribed

            if (_subscribedTopics.Contains(topic) && !force)
            {
                Log.Information($"Already subscribed to {topic}.");
                return;
            }

            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(qos))
                .Build();

            try
            {
                await _mqttClient.SubscribeAsync(subscribeOptions);
                _subscribedTopics.Add(topic); // Track the subscription
                Log.Information("Subscribed to " + topic);
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT subscribe: {ex.Message}");
            }

            // new code for reactions
        }

        public async Task UpdateClientOptionsAndReconnect() //updates the client options and reconnects to MQTT
        {
            InitializeClientOptions(); // Method to reinitialize client options with updated settings
            await DisconnectAsync();
            await ConnectAsync();
        }

        public async Task SubscribeToReactionButtonsAsync()
        {
            var reactions = new List<string> { "like", "love", "applause", "wow", "laugh" };
            foreach (var reaction in reactions)
            {
                string commandTopic = $"homeassistant/button/{_deviceId}/{reaction}/set";
                try
                {
                    await SubscribeAsync(commandTopic, MqttQualityOfServiceLevel.AtLeastOnce, true);
                }
                catch (Exception ex)
                {
                    Log.Information($"Error during reaction button MQTT subscribe: {ex.Message}");
                }
            }
        }

        public async Task PublishReactionButtonsAsync()
        {
            var reactions = new List<string> { "like", "love", "applause", "wow", "laugh" };
            var deviceInfo = new
            {
                ids = new[] { $"teams2ha_{_deviceId}" },
                mf = "Jimmy White",
                mdl = "Teams2HA Device",
                name = _deviceId,
                sw = "v1.0"
            };
            foreach (var reaction in reactions)
            {
                string configTopic = $"homeassistant/button/{_deviceId}/{reaction}/config";
                var payload = new
                {
                    name = reaction,
                    unique_id = $"{_deviceId}_{reaction}_reaction",
                    icon = GetIconForReaction(reaction),
                    device = deviceInfo, // Include the device information
                    command_topic = $"homeassistant/button/{_deviceId}/{reaction}/set",
                    payload_press = "press"
                    // Notice there's no state_topic or payload_on/off as it's a button, not a switch
                };

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(configTopic)
                    .WithPayload(JsonConvert.SerializeObject(payload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true)
                    .Build();
                if (_mqttClient.IsConnected)
                {
                    await PublishAsync(message);
                }
            }
        }

        public async Task TurnOffAllDevicesAsync()
        {
            foreach (var sensorName in _sensorNames)
            {
                string deviceClass = DetermineDeviceClass(sensorName); // Assuming you have this method
                string topicBase = $"homeassistant/{deviceClass}/{_deviceId}/{sensorName.ToLower().Replace(" ", "_")}";
                string stateValue = deviceClass == "binary_sensor" ? "false" : "OFF"; // Choose appropriate state values
                string topic = $"{topicBase}/state"; // Adjust if your topic structure differs

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(stateValue)
                    .Build();

                await PublishAsync(mqttMessage).ConfigureAwait(false);
            }
        }

        public async Task UpdateSettingsAsync(AppSettings newSettings) //updates the settings and reconnects to MQTT
        {
            _settings = newSettings;
            _deviceId = _settings.SensorPrefix;
            InitializeClientOptions(); // Reinitialize MQTT client options

            if (IsConnected)
            {
                await DisconnectAsync();
                await ConnectAsync();
            }
        }

        #endregion Public Methods

        #region Private Methods

        private string GetIconForReaction(string reaction)
        {
            return reaction switch
            {
                "like" => "mdi:thumb-up-outline",
                "love" => "mdi:heart-outline",
                "applause" => "mdi:hand-clap",
                "wow" => "mdi:emoticon-excited-outline",
                "laugh" => "mdi:emoticon-happy-outline",
                _ => "mdi:hand-okay" // Default icon
            };
        }

        private string DetermineDeviceClass(string sensor) //determines the device class for the sensor
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
                case "canToggleMute":
                case "canToggleVideo":
                case "canToggleHand":
                case "canToggleBlur":
                case "canLeave":
                case "canReact":
                case "canToggleShareTray":
                case "canToggleChat":
                case "canStopSharing":
                case "canPair":
                    return "binary_sensor"; // These are true/false sensors
                default:
                    return "unknown"; // Or a default device class if appropriate
            }
        }

        private string DetermineIcon(string sensor, MeetingState state) //determines the icon for the sensor
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

        private string GetStateValue(string sensor, MeetingUpdate meetingUpdate) //gets the state value of the sensor
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

        private void HandleSwitchCommand(string topic, string command)
        {
            // Split the topic to extract parts
            string[] topicParts = topic.Split('/');
            if (topicParts.Length < 5) // Ensure the topic has all expected parts
            {
                Log.Warning($"Unexpected topic format: {topic}");
                return;
            }

            // Extract deviceId and sensorName from the topic
            string deviceId = topicParts[2];
            string sensorName = topicParts[3]; // No need to strip prefix since we're using the new structure

            // Generate the JSON message based on the sensorName
            string jsonMessage = GenerateJsonMessageForSwitch(sensorName, deviceId);

            if (!string.IsNullOrEmpty(jsonMessage))
            {
                // Log the command and deviceId for debugging
                Log.Information($"Executing command for {sensorName} on device {deviceId}");

                // Raise the event with the generated JSON message
                CommandToTeams?.Invoke(jsonMessage);
            }
        }

        private string GenerateJsonMessageForSwitch(string switchName, string deviceId)
        {
            // Generate JSON message based on switchName
            switch (switchName.ToLower())
            {
                case "ismuted":
                    return $"{{\"apiVersion\":\"1.0.0\",\"service\":\"toggle-mute\",\"action\":\"toggle-mute\",\"manufacturer\":\"Jimmy White\",\"device\":\"{deviceId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";

                case "isvideoon":
                    return $"{{\"apiVersion\":\"1.0.0\",\"service\":\"toggle-video\",\"action\":\"toggle-video\",\"manufacturer\":\"Jimmy White\",\"device\":\"{deviceId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";

                case "isbackgroundblurred":
                    return $"{{\"apiVersion\":\"1.0.0\",\"service\":\"background-blur\",\"action\":\"toggle-background-blur\",\"manufacturer\":\"Jimmy White\",\"device\":\"{deviceId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";

                case "ishandraised":
                    return $"{{\"apiVersion\":\"1.0.0\",\"service\":\"raise-hand\",\"action\":\"toggle-hand\",\"manufacturer\":\"Jimmy White\",\"device\":\"{deviceId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";

                // Add other cases as necessary

                default:
                    Log.Warning($"Unrecognized switch command: {switchName}");
                    return string.Empty; // Return an empty string for unrecognized switch names
            }
        }

        private void InitializeClient() //initializes the MQTT client
        {
            var factory = new MqttFactory();
            _mqttClient = (MqttClient?)factory.CreateMqttClient(); // This creates an IMqttClient, not a MqttClient.

            InitializeClientOptions(); // Ensure options are initialized with current settings
            if (_mqttClientsubscribed == false)
            {
                _mqttClient.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
                _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
                _mqttClientsubscribed = true;
            }
            _mqttClient.ConnectedAsync += async e =>
            {
                Log.Information("Connected to MQTT broker.");
                // Handle post-connection setup, e.g., subscriptions.
                await SetupSubscriptionsAsync();
            };

            _mqttClient.DisconnectedAsync += async e =>
            {
                if (!isShuttingDown)
                {
                    Log.Information("Disconnected from MQTT broker, attempting to reconnect...");
                    // Implement your reconnection logic here
                    await ReconnectAsync();
                }
                else
                {
                    //Log.Information("MQTT client disconnected and shutdown in progress, skipping reconnection.");
                }
            };
        }

        private void InitializeClientOptions() //initializes the MQTT client options
        {
            try
            {
                var factory = new MqttFactory();
                _mqttClient = (MqttClient?)factory.CreateMqttClient();

                if (!int.TryParse(_settings.MqttPort, out int mqttportInt))
                {
                    mqttportInt = 1883; // Default MQTT port
                    Log.Warning($"Invalid MQTT port provided, defaulting to {mqttportInt}");
                }

                var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
                    .WithClientId($"Teams2HA_{_deviceId}")
                    .WithCredentials(_settings.MqttUsername, _settings.MqttPassword)
                    .WithCleanSession()
                    .WithTimeout(TimeSpan.FromSeconds(5));

                string protocol = _settings.UseWebsockets ? "ws" : "tcp";
                string connectionType = _settings.UseTLS ? "with TLS" : "without TLS";

                if (_settings.UseWebsockets)
                {
                    string websocketUri = _settings.UseTLS ? $"wss://{_settings.MqttAddress}:{mqttportInt}" : $"ws://{_settings.MqttAddress}:{mqttportInt}";
                    mqttClientOptionsBuilder.WithWebSocketServer(websocketUri);
                    Log.Information($"Configuring MQTT client for WebSocket {connectionType} connection to {websocketUri}");
                }
                else
                {
                    mqttClientOptionsBuilder.WithTcpServer(_settings.MqttAddress, mqttportInt);
                    Log.Information($"Configuring MQTT client for TCP {connectionType} connection to {_settings.MqttAddress}:{mqttportInt}");
                }

                if (_settings.UseTLS)
                {
                    // Create TLS parameters
                    var tlsParameters = new MqttClientOptionsBuilderTlsParameters
                    {
                        AllowUntrustedCertificates = _settings.IgnoreCertificateErrors,
                        IgnoreCertificateChainErrors = _settings.IgnoreCertificateErrors,
                        IgnoreCertificateRevocationErrors = _settings.IgnoreCertificateErrors,
                        UseTls = true
                    };

                    // If you need to validate the server certificate, you can set the CertificateValidationHandler.
                    // Note: Be cautious with bypassing certificate checks in production code!!
                    if (!_settings.IgnoreCertificateErrors)
                    {
                        tlsParameters.CertificateValidationHandler = context =>
                        {
                            // Log the SSL policy errors
                            Log.Debug($"SSL policy errors: {context.SslPolicyErrors}");

                            // Return true if there are no SSL policy errors, or if ignoring
                            // certificate errors is allowed
                            return context.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None;
                        };
                    }

                    // Apply the TLS parameters to the options builder
                    mqttClientOptionsBuilder.WithTls(tlsParameters);
                }

                _mqttOptions = mqttClientOptionsBuilder.Build();
                if (_mqttClient != null)
                {
                    if (_mqttClientsubscribed == false)
                    {
                        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
                        _mqttClientsubscribed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize MqttClientWrapper");
                throw; // Rethrowing the exception to handle it outside or log it as fatal depending on your error handling strategy.
            }
        }

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e) //triggered when a message is received from MQTT
        {
            if (e.ApplicationMessage.Payload == null)
            {
                Log.Information($"Received message on topic {e.ApplicationMessage.Topic}");
            }
            else
            {
                Log.Information($"Received message on topic {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
            }
            Log.Information($"Received message on topic {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
            if (MessageReceived != null)
            {
                return MessageReceived(e);
            }
            string topic = e.ApplicationMessage.Topic;
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            if (topic.StartsWith($"homeassistant/button/{_deviceId}/") && payload == "press")
            {
                var parts = topic.Split('/');
                if (parts.Length > 3)
                {
                    var reaction = parts[3]; // Extract the reaction type from the topic

                    // Construct the JSON message for the reaction
                    var reactionPayload = new
                    {
                        action = "send-reaction",
                        parameters = new { type = reaction },
                        requestId = 1
                    };

                    string reactionPayloadJson = JsonConvert.SerializeObject(reactionPayload);

                    // Invoke the command to send the reaction to Teams
                    CommandToTeams?.Invoke(reactionPayloadJson);
                }
            }

            // Assuming the format is homeassistant/switch/{deviceId}/{switchName}/set Validate the
            // topic format and extract the switchName
            var topicParts = topic.Split(','); //not sure this is required
            topicParts = topic.Split('/');
            if (topicParts.Length == 5 && topicParts[0].Equals("homeassistant") && topicParts[1].Equals("switch") && topicParts[4].EndsWith("set"))
            {
                // Extract the action and switch name from the topic
                string switchName = topicParts[3];
                string command = payload; // command should be ON or OFF based on the payload

                // Now call the handle method
                HandleSwitchCommand(topic, command);
            }

            return Task.CompletedTask;
        }

        private void OnMqttPublishTimerElapsed(object sender, ElapsedEventArgs e) //sends keep alice message to MQTT
        {
            if (_mqttClient.IsConnected)
            {
                // Example: Publish a keep-alive message
                string keepAliveTopic = "TEAMS2HA/keepalive";
                string keepAliveMessage = "alive";
                // we should publish the current meeting state

                // Create the MQTT message
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(keepAliveTopic)
                    .WithPayload(keepAliveMessage)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce) // Or another QoS level if required
                    .Build();

                // Publish the message asynchronously
                _ = _mqttClient.PublishAsync(message);

                Log.Debug("OnMqttPublishTimerElapsed: MQTT Keep Alive Message Published");
            }
            else
            {
                Log.Debug("OnMqttPublishTimerElapsed: MQTT Client is not connected");

                UpdateClientOptionsAndReconnect();
            }
        }

        #endregion Private Methods
    }
}