using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TEAMS2HA.Properties;
using TEAMS2HA.Utils;

namespace TEAMS2HA.API
{
    public class MqttService
    {
        private static readonly Lazy<MqttService> _instance = new Lazy<MqttService>(() => new MqttService());
        private IManagedMqttClient _mqttClient;
        private MqttClientOptionsBuilder _mqttClientOptionsBuilder;
        private AppSettings _settings;
        private string _deviceId;
        private Dictionary<string, string> _previousSensorStates;
        private List<string> _sensorNames;
        private ProcessWatcher processWatcher;
        private bool _isInitialized = false;
        private dynamic _deviceInfo;
        public static MqttService Instance => _instance.Value;
        private HashSet<string> _subscribedTopics = new HashSet<string>();
        public event Func<MqttApplicationMessageReceivedEventArgs, Task> MessageReceived;
        public delegate Task CommandToTeamsHandler(string jsonMessage);
        public event CommandToTeamsHandler CommandToTeams;

        public void Initialize(AppSettings settings, string deviceId, List<string> sensorNames)
        {
            if (!_isInitialized)
            {
                _settings = settings;
                //add some null checks incase its first run

                if (string.IsNullOrEmpty(deviceId))
                {
                    //set it to the computer name
                    deviceId = Environment.MachineName.ToLower();

                }
                else
                {
                    deviceId = deviceId.ToLower();
                }

                _sensorNames = sensorNames;
                _isInitialized = true;
                //_mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

            }
            else
            {
                // Optionally handle re-initialization if needed
            }
        }
        public bool IsConnected => _mqttClient?.IsConnected ?? false;
        private MqttService()
        {
            ProcessWatcher processWatcher = new ProcessWatcher();
            _previousSensorStates = new Dictionary<string, string>();
            var factory = new MqttFactory();
            _mqttClient = factory.CreateManagedMqttClient();
            _deviceId = AppSettings.Instance.SensorPrefix.ToLower();
            _deviceInfo = new
            {
                ids = new[] { $"teams2ha_{_deviceId}" },
                mf = "Jimmy White",
                mdl = "Teams2HA Device",
                name = _deviceId,
                sw = "v1.0"
            };
            _mqttClient.ConnectedAsync += async e =>
            {
                Log.Information("Connected to MQTT broker.");
                _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        mainWindow.UpdateMqttStatus(true);
                    }
                });


                await Task.CompletedTask;

            };

            _mqttClient.DisconnectedAsync += async e =>
            {
                Log.Information("Disconnected from MQTT broker.");
                _mqttClient.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;

                await Task.CompletedTask;
            };
            Log.Information("MQTT client created.");
            // _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        }
        public async Task SubscribeToReactionButtonsAsync()
        {
            _deviceId = AppSettings.Instance.SensorPrefix.ToLower();
            var reactions = new List<string> { "like", "love", "applause", "wow", "laugh" };
            foreach (var reaction in reactions)
            {
                string commandTopic = $"homeassistant/button/{_deviceId.ToLower()}/{reaction}/set";
                try
                {
                    await SubscribeAsync(commandTopic, MqttQualityOfServiceLevel.AtLeastOnce);
                }
                catch (Exception ex)
                {
                    Log.Information($"Error during reaction button MQTT subscribe: {ex.Message}");
                }
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

            if (topic.StartsWith($"homeassistant/button/{_deviceId.ToLower()}/") && payload == "press")
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
            var topicParts = topic.Split('/'); //not sure this is required
            //topicParts = topic.Split('/');
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
        private void HandleSwitchCommand(string topic, string command)
        {
            // Determine which switch is being controlled based on the topic
            string switchName = topic.Split('/')[3]; // Assuming topic format is "homeassistant/switch/{switchName}/set"
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
        public async Task ConnectAsync(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings), "MQTT settings must be provided.");
            if (!_isInitialized)
            {
                throw new InvalidOperationException("MqttService must be initialized before connecting.");
            }
            if (_mqttClient.IsConnected || _mqttClient.IsStarted)
            {
                await _mqttClient.StopAsync();
                Log.Information("Existing MQTT client stopped successfully.");
            }

            var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId("TEAMS2HA")
                .WithCredentials(settings.MqttUsername, settings.MqttPassword);
            if (settings.UseWebsockets && !settings.UseTLS)
            {
                mqttClientOptionsBuilder.WithWebSocketServer($"ws://{settings.MqttAddress}:{settings.MqttPort}");
                Log.Information($"WebSocket server set to ws://{settings.MqttAddress}:{settings.MqttPort}");
            }
            else if (settings.UseWebsockets && settings.UseTLS)
            {
                mqttClientOptionsBuilder.WithWebSocketServer($"wss://{settings.MqttAddress}:{settings.MqttPort}");
                Log.Information($"WebSocket server set to wss://{settings.MqttAddress}:{settings.MqttPort}");
            }
            else
            {
                mqttClientOptionsBuilder.WithTcpServer(settings.MqttAddress, Convert.ToInt32(settings.MqttPort));
                Log.Information($"TCP server set to {settings.MqttAddress}:{settings.MqttPort}");
            }

            if (settings.UseTLS)
            {
                mqttClientOptionsBuilder.WithTlsOptions(o =>
                {
                    o.WithSslProtocols(SslProtocols.Tls12);
                    Log.Information("TLS is enabled.");
                });
            }

            if (settings.IgnoreCertificateErrors)
            {
                mqttClientOptionsBuilder.WithTlsOptions(o =>
                {
                    // The used public broker sometimes has invalid certificates. This sample
                    // accepts all certificates. This should not be used in live environments.
                    o.WithCertificateValidationHandler(_ =>
                    {
                        Log.Warning("Certificate validation is disabled; this is not recommended for production.");
                        return true;
                    });
                });
            }

            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))

                .WithClientOptions(mqttClientOptionsBuilder.Build())
                .Build();

            try
            {
                Log.Information($"Starting MQTT client...{options}");
                await _mqttClient.StartAsync(options);


                Log.Information($"MQTT client connected with new settings. {_mqttClient.IsStarted}");
                await PublishPermissionSensorsAsync();
                await PublishReactionButtonsAsync();
                //if mqtt is connected, lets subsctribed to incominfg messages
                await SetupSubscriptionsAsync();
                Log.Information("Subscribed to incoming messages.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start MQTT client: {ex.Message}");
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

        public bool IsTeamsRunning()
        {
            return Process.GetProcessesByName("ms-teams").Length > 0;
        }
        public async Task SetupSubscriptionsAsync()
        {
            // Subscribe to necessary topics
            // await SubscribeAsync($"homeassistant/switch/{_settings.SensorPrefix}/+/set", MqttQualityOfServiceLevel.AtLeastOnce, true);
            await SubscribeToReactionButtonsAsync();
            // Add any other necessary subscriptions here
        }
        public async Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qos)
        {
            if (_subscribedTopics.Contains(topic))
            {
                Log.Information($"Already subscribed to {topic}.");
                return;
            }

            try
            {
                var topicFilter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(qos)
                    .Build();

                Log.Debug($"Attempting to subscribe to {topic} with QoS {qos}.");
                await _mqttClient.SubscribeAsync(new List<MqttTopicFilter> { topicFilter });
                _subscribedTopics.Add(topic); // Track the subscription
                Log.Information("Subscribed to " + topic);
            }
            catch (Exception ex)
            {
                Log.Error($"Error during MQTT subscribe for {topic}: {ex.Message}");
            }
        }
        public void Dispose()
        {
            _mqttClient?.Dispose();
            Log.Information("MQTT Client disposed.");
        }
        public async Task UnsubscribeAsync(string topic)
        {
            if (!_subscribedTopics.Contains(topic))
            {
                Log.Information($"Not subscribed to {topic}, no need to unsubscribe.");
                return;
            }

            try
            {
                await _mqttClient.UnsubscribeAsync(new List<string> { topic });
                _subscribedTopics.Remove(topic);
                Log.Information($"Successfully unsubscribed from {topic}.");
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT unsubscribe: {ex.Message}");
            }
        }
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

                bool isAllowed = permission.Value;
                _deviceId = _settings.SensorPrefix.ToLower();
                string sensorName = permission.Key.ToLower();
                string configTopic = $"homeassistant/binary_sensor/{_deviceId.ToLower()}/{sensorName}/config";
                var configPayload = new
                {
                    name = sensorName,
                    unique_id = $"{_deviceId}_{sensorName}",
                    device = _deviceInfo,
                    icon = "mdi:eye", // You can customize the icon based on the sensor
                    state_topic = $"homeassistant/binary_sensor/{_deviceId.ToLower()}/{sensorName}/state",
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
                string stateTopic = $"homeassistant/binary_sensor/{_deviceId.ToLower()}/{sensorName}/state";
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
        public async Task PublishConfigurations(MeetingUpdate meetingUpdate, AppSettings settings, bool forcePublish = false)
        {
            _settings = settings;
            if (_mqttClient == null)
            {
                Log.Debug("MQTT Client Wrapper is not initialized.");
                return;
            }
            _deviceId = settings.SensorPrefix;
            // Define common device information for all entities.
            var deviceInfo = new
            {
                ids = new[] { "teams2ha_" + _deviceId.ToLower() }, // Unique device identifier
                mf = "Jimmy White", // Manufacturer name
                mdl = "Teams2HA Device", // Model
                name = _deviceId.ToLower(), // Device name
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
                        teamsRunning = IsTeamsRunning()
                    }
                };
            }

            foreach (var binary_sensor in _sensorNames)
            {
                string sensorKey = $"{_deviceId.ToLower()}_{binary_sensor}";
                string sensorName = $"{binary_sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(binary_sensor);
                string icon = DetermineIcon(binary_sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(binary_sensor, meetingUpdate);
                string uniqueId = $"{_deviceId}_{binary_sensor}";
                string configTopic;
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
                        configTopic = $"homeassistant/switch/{_deviceId.ToLower()}/{sensorName}/config";
                        var switchConfig = new
                        {
                            name = sensorName,
                            unique_id = uniqueId,
                            device = deviceInfo,
                            icon = icon,
                            command_topic = $"homeassistant/switch/{_deviceId.ToLower()}/{sensorName}/set",
                            state_topic = $"homeassistant/switch/{_deviceId.ToLower()}/{sensorName}/state",
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
                        configTopic = $"homeassistant/binary_sensor/{_deviceId.ToLower()}/{sensorName}/config";
                        var binarySensorConfig = new
                        {
                            name = sensorName,
                            unique_id = uniqueId,
                            device = deviceInfo,
                            icon = icon,
                            state_topic = $"homeassistant/binary_sensor/{_deviceId.ToLower()}/{sensorName}/state",
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
        public static List<string> GetEntityNames(string deviceId)
        {
            var entityNames = new List<string>
                {
                    $"switch.{deviceId.ToLower()}_ismuted",
                    $"switch.{deviceId.ToLower()}_isvideoon",
                    $"switch.{deviceId.ToLower()}_ishandraised",
                    $"binary_sensor.{deviceId.ToLower()}_isrecordingon",
                    $"binary_sensor.{deviceId.ToLower()}_isinmeeting",
                    $"binary_sensor.{deviceId.ToLower()}_issharing",
                    $"binary_sensor.{deviceId.ToLower()}_hasunreadmessages",
                    $"switch.{deviceId.ToLower()}_isbackgroundblurred",
                    $"binary_sensor.{deviceId.ToLower()}_teamsRunning"
                };

            return entityNames;
        }
        public event Action<string> StatusUpdated;
        public async Task DisconnectAsync()
        {
            Log.Information("Disconnecting from MQTT broker...");
            await _mqttClient.StopAsync();
        }

        public async Task PublishAsync(MqttApplicationMessage message)
        {
            try
            {
                await _mqttClient.EnqueueAsync(message); // Note: Add using System.Threading; if CancellationToken is undefined
                Log.Information("Publish successful." + message.Topic);
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT publish: {ex.Message}");
            }
        }
    }
}