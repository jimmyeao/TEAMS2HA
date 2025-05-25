using MQTTnet;

using MQTTnet.Packets;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
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
        private IMqttClient _mqttClient;
        private MqttClientOptions _mqttClientOptions;
        private AppSettings _settings;
        private string _deviceId;
        private Dictionary<string, string> _previousSensorStates;
        private List<string> _sensorNames;
        private ProcessWatcher processWatcher;
        private bool _isInitialized = false;
        private dynamic _deviceInfo;
        private Timer _reconnectTimer;
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public static MqttService Instance => _instance.Value;
        private HashSet<string> _subscribedTopics = new HashSet<string>();

        public delegate Task CommandToTeamsHandler(string jsonMessage);
        public event CommandToTeamsHandler CommandToTeams;

        public void Initialize(AppSettings settings, string deviceId, List<string> sensorNames)
        {
            if (!_isInitialized)
            {
                _settings = settings;

                if (string.IsNullOrEmpty(deviceId))
                {
                    deviceId = Environment.MachineName.ToLower();
                }
                else
                {
                    deviceId = deviceId.ToLower();
                }

                _deviceId = deviceId;
                _sensorNames = sensorNames;
                _isInitialized = true;
            }
        }

        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        private MqttService()
        {
            ProcessWatcher processWatcher = new ProcessWatcher();
            _previousSensorStates = new Dictionary<string, string>();

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            _deviceId = AppSettings.Instance.SensorPrefix.ToLower();
            _deviceInfo = new
            {
                ids = new[] { $"teams2ha_{_deviceId}" },
                mf = "Jimmy White",
                mdl = "Teams2HA Device",
                name = _deviceId,
                sw = "v1.0"
            };

            SetupEventHandlers();
            Log.Information("MQTT client created.");
        }

        private void SetupEventHandlers()
        {
            _mqttClient.ConnectedAsync += async e =>
            {
                Log.Information("Connected to MQTT broker.");

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
                Log.Information($"Disconnected from MQTT broker. Reason: {e.Reason} | Exception: {e.Exception?.Message}");
                if (e.Exception != null)
                {
                    Log.Error($"Exception during disconnect: {e.Exception}");
                }

                await Task.CompletedTask;
            };

            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        }

        private void StartReconnectTimer()
        {
            _reconnectTimer = new Timer(async _ => await EnsureConnectedAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }

        private async Task EnsureConnectedAsync()
        {
            if (_cancellationTokenSource.IsCancellationRequested)
                return;

            await _connectionSemaphore.WaitAsync();
            try
            {
                if (!IsConnected && _mqttClientOptions != null)
                {
                    try
                    {
                        Log.Information("Attempting to reconnect to MQTT broker...");
                        await _mqttClient.ConnectAsync(_mqttClientOptions, _cancellationTokenSource.Token);

                        // Re-subscribe to topics after reconnection
                        await SetupSubscriptionsAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to reconnect to MQTT broker: {ex.Message}");
                    }
                }
            }
            finally
            {
                _connectionSemaphore.Release();
            }
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

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            // Convert ReadOnlySequence<byte> to string for payload
            string payload = "";
            if (e.ApplicationMessage.Payload.Length > 0)
            {
                // .NET 5.0+ extension method for ReadOnlySequence<byte> - more efficient
                payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            }

            Log.Information($"Received message on topic {e.ApplicationMessage.Topic}: {payload}");

            string topic = e.ApplicationMessage.Topic;

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
                    try
                    {
                        CommandToTeams?.Invoke(reactionPayloadJson);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error sending reaction to Teams: {ex.Message}");
                    }
                }
            }

            // Handle switch commands
            var topicParts = topic.Split('/');
            if (topicParts.Length == 5 && topicParts[0].Equals("homeassistant") && topicParts[1].Equals("switch") && topicParts[4].EndsWith("set"))
            {
                string switchName = topicParts[3];
                string command = payload;
                HandleSwitchCommand(topic, command);
            }

            return Task.CompletedTask;
        }

        private void HandleSwitchCommand(string topic, string command)
        {
            string switchName = topic.Split('/')[3];
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
            }

            if (!string.IsNullOrEmpty(jsonMessage))
            {
                try
                {
                    CommandToTeams?.Invoke(jsonMessage);
                }
                catch (Exception ex)
                {
                    Log.Error($"Error sending command to Teams: {ex.Message}");
                }
            }
        }

        public async Task ConnectAsync(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings), "MQTT settings must be provided.");
            if (!_isInitialized)
            {
                throw new InvalidOperationException("MqttService must be initialized before connecting.");
            }

            await _connectionSemaphore.WaitAsync();
            try
            {
                if (IsConnected)
                {
                    await _mqttClient.DisconnectAsync();
                    Log.Information("Existing MQTT client disconnected successfully.");
                }

                string uniqueClientId = $"TEAMS2HA_{Environment.MachineName}";
                var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
                    .WithClientId(uniqueClientId)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                    .WithCleanSession(true)
                    .WithCredentials(settings.MqttUsername, settings.MqttPassword);

                if (settings.UseWebsockets && !settings.UseTLS)
                {
                    mqttClientOptionsBuilder.WithWebSocketServer(o => o.WithUri($"ws://{settings.MqttAddress}:{settings.MqttPort}/mqtt"));
                    Log.Information($"WebSocket server set to ws://{settings.MqttAddress}:{settings.MqttPort}/mqtt");
                }
                else if (settings.UseWebsockets && settings.UseTLS)
                {
                    mqttClientOptionsBuilder.WithWebSocketServer(o => o.WithUri($"wss://{settings.MqttAddress}:{settings.MqttPort}/mqtt"));
                    Log.Information($"WebSocket server set to wss://{settings.MqttAddress}:{settings.MqttPort}/mqtt");
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
                        o.WithCertificateValidationHandler(_ =>
                        {
                            Log.Warning("Certificate validation is disabled; this is not recommended for production.");
                            return true;
                        });
                    });
                }

                _mqttClientOptions = mqttClientOptionsBuilder.Build();

                Log.Information($"Starting MQTT client...");
                await _mqttClient.ConnectAsync(_mqttClientOptions, _cancellationTokenSource.Token);

                Log.Information($"MQTT client connected with new settings.");
                await PublishPermissionSensorsAsync();
                await PublishReactionButtonsAsync();
                await SetupSubscriptionsAsync();
                Log.Information("Subscribed to incoming messages.");

                // Start the reconnect timer
                StartReconnectTimer();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start MQTT client: {ex.Message}");
                throw;
            }
            finally
            {
                _connectionSemaphore.Release();
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
                    device = deviceInfo,
                    command_topic = $"homeassistant/button/{_deviceId}/{reaction}/set",
                    payload_press = "press"
                };

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(configTopic)
                    .WithPayload(JsonConvert.SerializeObject(payload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true)
                    .Build();

                if (IsConnected)
                {
                    try
                    {
                        await PublishAsync(message);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error publishing reaction button configuration: {ex.Message}");
                    }
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
                _ => "mdi:hand-okay"
            };
        }

        public bool IsTeamsRunning()
        {
            return Process.GetProcessesByName("ms-teams").Length > 0;
        }

        public async Task SetupSubscriptionsAsync()
        {
            await SubscribeToReactionButtonsAsync();
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
                var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(qos))
                    .Build();

                Log.Debug($"Attempting to subscribe to {topic} with QoS {qos}.");
                await _mqttClient.SubscribeAsync(subscribeOptions, _cancellationTokenSource.Token);
                _subscribedTopics.Add(topic);
                Log.Information("Subscribed to " + topic);
            }
            catch (Exception ex)
            {
                Log.Error($"Error during MQTT subscribe for {topic}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            StopReconnectTimer();
            _mqttClient?.Dispose();
            _connectionSemaphore?.Dispose();
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
                var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder()
                    .WithTopicFilter(topic)
                    .Build();

                await _mqttClient.UnsubscribeAsync(unsubscribeOptions, _cancellationTokenSource.Token);
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
                    icon = "mdi:eye",
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

                string stateTopic = $"homeassistant/binary_sensor/{_deviceId.ToLower()}/{sensorName}/state";
                string statePayload = isAllowed ? "true" : "false";
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
                Log.Debug("MQTT Client is not initialized.");
                return;
            }

            _deviceId = settings.SensorPrefix;
            var deviceInfo = new
            {
                ids = new[] { "teams2ha_" + _deviceId.ToLower() },
                mf = "Jimmy White",
                mdl = "Teams2HA Device",
                name = _deviceId.ToLower(),
                sw = "v1.0"
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
                        TeamsRunning = IsTeamsRunning()
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
                    _previousSensorStates[sensorKey] = stateValue;

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
                            payload_on = "true",
                            payload_off = "false"
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
                            .WithPayload(stateValue.ToLowerInvariant())
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
                "IsMuted" => state.IsMuted ? "mdi:microphone-off" : "mdi:microphone",
                "IsVideoOn" => state.IsVideoOn ? "mdi:camera" : "mdi:camera-off",
                "IsHandRaised" => state.IsHandRaised ? "mdi:hand-back-left" : "mdi:hand-back-left-off",
                "IsInMeeting" => state.IsInMeeting ? "mdi:account-group" : "mdi:account-off",
                "IsRecordingOn" => state.IsRecordingOn ? "mdi:record-rec" : "mdi:record",
                "IsBackgroundBlurred" => state.IsBackgroundBlurred ? "mdi:blur" : "mdi:blur-off",
                "IsSharing" => state.IsSharing ? "mdi:monitor-share" : "mdi:monitor-off",
                "HasUnreadMessages" => state.HasUnreadMessages ? "mdi:message-alert" : "mdi:message-outline",
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
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";
                case "IsInMeeting":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";
                case "HasUnreadMessages":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";
                case "IsRecordingOn":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";
                case "IsSharing":
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";
                case "TeamsRunning":
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
                    return "switch";
                case "IsInMeeting":
                case "HasUnreadMessages":
                case "IsRecordingOn":
                case "IsSharing":
                case "TeamsRunning":
                    return "binary_sensor";
                default:
                    return "unknown";
            }
        }

        public async Task SetupMqttSensors()
        {
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
                    TeamsRunning = false
                }
            };

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
            _cancellationTokenSource.Cancel();
            StopReconnectTimer();

            if (IsConnected)
            {
                Log.Information("Disconnecting from MQTT broker...");
                await _mqttClient.DisconnectAsync();
            }
        }

        public async Task PublishAsync(MqttApplicationMessage message)
        {
            try
            {
                await _mqttClient.PublishAsync(message, _cancellationTokenSource.Token);
                Log.Information("Publish successful." + message.Topic);
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT publish: {ex.Message}");
            }
        }
    }
}