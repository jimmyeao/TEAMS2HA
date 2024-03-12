using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Serilog;
using System;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Runtime.ConstrainedExecution;
using System.Windows.Controls;
using System.Security.Authentication;
using System.Threading;
using System.Timers;
using Newtonsoft.Json;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ListView;
using System.Text;

namespace TEAMS2HA.API
{
    public class MqttService
    {
        #region Private Fields

        private const int MaxConnectionRetries = 2;
        private const int RetryDelayMilliseconds = 1000;
        private readonly string _deviceId;
        private bool _isAttemptingConnection = false;
        private MqttClient _mqttClient;
        private MqttClientOptions _mqttOptions;
        private Dictionary<string, string> _previousSensorStates;
        private List<string> _sensorNames;
        private AppSettings _settings;
        private System.Timers.Timer mqttPublishTimer;
        private HashSet<string> _subscribedTopics = new HashSet<string>();
        private bool mqttPublishTimerset = false;
        public delegate Task CommandToTeamsHandler(string jsonMessage);
        private bool _mqttClientsubscribed = false;
        public event CommandToTeamsHandler CommandToTeams;

        public event Action<string> StatusUpdated;

        #endregion Private Fields

        #region Public Constructors

        // Constructor
        public MqttService(AppSettings settings, string deviceId, List<string> sensorNames)
        {
            _settings = settings;
            _deviceId = deviceId;
            _sensorNames = sensorNames;
            _previousSensorStates = new Dictionary<string, string>();

            InitializeClient();
            InitializeMqttPublishTimer();
        }

        #endregion Public Constructors

        #region Public Events

        public event Action<string> ConnectionAttempting;

        public event Action<string> ConnectionStatusChanged;

        public event Func<MqttApplicationMessageReceivedEventArgs, Task> MessageReceived;

        #endregion Public Events

        #region Public Properties

        public bool IsAttemptingConnection
        {
            get { return _isAttemptingConnection; }
            private set { _isAttemptingConnection = value; }
        }

        public bool IsConnected => _mqttClient.IsConnected;

        #endregion Public Properties

        #region Public Methods
        public async Task UpdateClientOptionsAndReconnect()
        {
            InitializeClientOptions(); // Method to reinitialize client options with updated settings
            await DisconnectAsync();
            await ConnectAsync();
        }

        public async Task UpdateSettingsAsync(AppSettings newSettings)
        {
            _settings = newSettings;
            InitializeClientOptions(); // Reinitialize MQTT client options

            if (IsConnected)
            {
                await DisconnectAsync();
                await ConnectAsync();
            }
        }
        public static List<string> GetEntityNames(string deviceId)
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

        public async Task ConnectAsync()
        {
            // Check if MQTT client is already connected or connection attempt is in progress
            if (_mqttClient.IsConnected || _isAttemptingConnection)
            {
                Log.Information("MQTT client is already connected or connection attempt is in progress.");
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
        }

        public async Task DisconnectAsync()
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

        public void Dispose()
        {
            if (_mqttClient != null)
            {
                _ = _mqttClient.DisconnectAsync(); // Disconnect asynchronously
                _mqttClient.Dispose();
                Log.Information("MQTT Client Disposed");
            }
        }

        public void InitializeMqttPublishTimer()
        {
            mqttPublishTimer = new System.Timers.Timer(60000); // Set the interval to 60 seconds
            if(mqttPublishTimerset == false)
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

        public async Task PublishAsync(MqttApplicationMessage message)
        {
            try
            {
                await _mqttClient.PublishAsync(message, CancellationToken.None); // Note: Add using System.Threading; if CancellationToken is undefined
                Log.Information("Publish successful." + message.Topic);
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT publish: {ex.Message}");
            }
        }

        public async Task PublishConfigurations(MeetingUpdate meetingUpdate, AppSettings settings)
        {
            if (_mqttClient == null)
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
            foreach (var binary_sensor in _sensorNames)
            {
                string sensorKey = $"{_deviceId}_{binary_sensor}";
                string sensorName = $"{binary_sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(binary_sensor);
                string icon = DetermineIcon(binary_sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(binary_sensor, meetingUpdate);
                string uniqueId = $"{_deviceId}_{binary_sensor}";
                string configTopic;
                if (!_previousSensorStates.TryGetValue(sensorKey, out var previousState) || previousState != stateValue)
                {
                    _previousSensorStates[sensorKey] = stateValue; // Update the stored state

                    if (deviceClass == "switch")
                    {
                        configTopic = $"homeassistant/switch/{sensorName}/config";
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
                        var switchConfigMessage = new MqttApplicationMessageBuilder()
                             .WithTopic(configTopic)
                             .WithPayload(JsonConvert.SerializeObject(switchConfig))
                             .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                             .WithRetainFlag(true)
                             .Build();

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
                        configTopic = $"homeassistant/binary_sensor/{sensorName}/config";
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
                        var binarySensorConfigMessage = new MqttApplicationMessageBuilder()
                             .WithTopic(configTopic)
                             .WithPayload(JsonConvert.SerializeObject(binarySensorConfig))
                             .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                             .WithRetainFlag(true)
                             .Build();

                        await PublishAsync(binarySensorConfigMessage);

                        var binarySensorStateMessage = new MqttApplicationMessageBuilder()
                            .WithTopic(binarySensorConfig.state_topic)
                            .WithPayload(stateValue.ToLowerInvariant()) // Ensure the state value is in the correct format
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag(true)
                            .Build();

                        await PublishAsync(binarySensorStateMessage);
                    }
                }
            }
        }

        public async Task ReconnectAsync()
        {
            // Consolidated reconnection logic
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

        public async Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qos)
        {
            // Check if already subscribed
            if (_subscribedTopics.Contains(topic))
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
                    return "binary_sensor"; // These are true/false sensors
                default:
                    return "unknown"; // Or a default device class if appropriate
            }
        }

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

        private void InitializeClient()
        {
            if (_mqttClient == null)
            {
                var factory = new MqttFactory();
                _mqttClient = (MqttClient?)factory.CreateMqttClient(); // This creates an IMqttClient, not a MqttClient.

                InitializeClientOptions(); // Ensure options are initialized with current settings
                if(_mqttClientsubscribed == false)
                {
                    _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
                    _mqttClientsubscribed = true;
                }
                
            }
        }

        private void InitializeClientOptions()
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
                    .WithClientId("Teams2HA")
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
                    if(_mqttClientsubscribed == false)
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

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            Log.Information($"Received message on topic {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
            string topic = e.ApplicationMessage.Topic;
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            // Assuming the format is homeassistant/switch/{deviceId}/{switchName}/set Validate the
            // topic format and extract the switchName
            var topicParts = topic.Split(','); //not sure this is required
            topicParts = topic.Split('/');
            if (topicParts.Length == 4 && topicParts[0].Equals("homeassistant") && topicParts[1].Equals("switch") && topicParts[3].EndsWith("set"))
            {
                // Extract the action and switch name from the topic
                string switchName = topicParts[2];
                string command = payload; // command should be ON or OFF based on the payload

                // Now call the handle method
                 HandleSwitchCommand(topic, command);
            }

            return Task.CompletedTask;
        }

        private void OnMqttPublishTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                // Example: Publish a keep-alive message
                string keepAliveTopic = "TEAMS2HA/keepalive";
                string keepAliveMessage = "alive";

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
        }

        #endregion Private Methods

        // Additional methods for sensor management, message handling, etc.
    }
}