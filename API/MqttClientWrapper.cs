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

namespace TEAMS2HA.API
{
    public class MqttClientWrapper
    {
        #region Private Fields

        private MqttClient _mqttClient;
        private MqttClientOptions _mqttOptions;
        private bool _isAttemptingConnection = false;
        private const int MaxConnectionRetries = 5;
        private const int RetryDelayMilliseconds = 2000; //wait a couple of seconds before retrying a connection attempt

        #endregion Private Fields
        public event Action<string> ConnectionStatusChanged;

        #region Public Constructors
        public bool IsAttemptingConnection
        {
            get { return _isAttemptingConnection; }
            private set { _isAttemptingConnection = value; }
        }

        [Obsolete]
        public MqttClientWrapper(string clientId, string mqttBroker, string mqttPort, string username, string password, bool useTLS, bool ignoreCertificateErrors, bool useWebsockets)
        {
            try
            {
                var factory = new MqttFactory();
                _mqttClient = (MqttClient?)factory.CreateMqttClient();

                if (!int.TryParse(mqttPort, out int mqttportInt))
                {
                    mqttportInt = 1883; // Default MQTT port
                    Log.Warning($"Invalid MQTT port provided, defaulting to {mqttportInt}");
                }

                var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
                    .WithClientId(clientId)
                    .WithCredentials(username, password)
                    .WithCleanSession();

                string protocol = useWebsockets ? "ws" : "tcp";
                string connectionType = useTLS ? "with TLS" : "without TLS";

                if (useWebsockets)
                {
                    string websocketUri = useTLS ? $"wss://{mqttBroker}:{mqttportInt}" : $"ws://{mqttBroker}:{mqttportInt}";
                    mqttClientOptionsBuilder.WithWebSocketServer(websocketUri);
                    Log.Information($"Configuring MQTT client for WebSocket {connectionType} connection to {websocketUri}");
                }
                else
                {
                    mqttClientOptionsBuilder.WithTcpServer(mqttBroker, mqttportInt);
                    Log.Information($"Configuring MQTT client for TCP {connectionType} connection to {mqttBroker}:{mqttportInt}");
                }

                if (useTLS)
                {
                    mqttClientOptionsBuilder.WithTls(new MqttClientOptionsBuilderTlsParameters
                    {
                        UseTls = true,
                        AllowUntrustedCertificates = ignoreCertificateErrors,
                        IgnoreCertificateChainErrors = ignoreCertificateErrors,
                        IgnoreCertificateRevocationErrors = ignoreCertificateErrors,
                        CertificateValidationHandler = context =>
                        {
                            Log.Debug($"Certificate validation for MQTT {protocol} connection: {context.Certificate.Subject}");
                            return ignoreCertificateErrors;
                        }
                    });
                }

                _mqttOptions = mqttClientOptionsBuilder.Build();
                if (_mqttClient != null)
                {
                    _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize MqttClientWrapper");
                throw; // Rethrowing the exception to handle it outside or log it as fatal depending on your error handling strategy.
            }
        }

        #endregion Public Constructors

        #region Public Events

        public event Func<MqttApplicationMessageReceivedEventArgs, Task> MessageReceived;

        #endregion Public Events

        #region Public Properties

        public bool IsConnected => _mqttClient.IsConnected;

        #endregion Public Properties

        #region Public Methods

        public async Task ConnectAsync()
        {
            if (_mqttClient.IsConnected || _isAttemptingConnection)
            {
                Log.Information("MQTT client is already connected or connection attempt is in progress.");

                return;
            }

            _isAttemptingConnection = true;
            int retryCount = 0;

            while (retryCount < MaxConnectionRetries && !_mqttClient.IsConnected)
            {
                try
                {
                    Log.Information($"Attempting to connect to MQTT (Attempt {retryCount + 1}/{MaxConnectionRetries})");
                    await _mqttClient.ConnectAsync(_mqttOptions);
                    Log.Information("Connected to MQTT broker.");
                    if (_mqttClient.IsConnected)
                        ConnectionStatusChanged?.Invoke("MQTT Status: Connected");

                    break;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to connect to MQTT broker: {ex.Message}");
                    ConnectionStatusChanged?.Invoke($"MQTT Status: Disconnected (Retry {retryCount + 1}) {ex.Message}");
                    retryCount++;
                    await Task.Delay(RetryDelayMilliseconds);
                }
            }

            _isAttemptingConnection = false;
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
        public static List<string> GetEntityNames(string deviceId)
        {
            var entityNames = new List<string>
        {
            $"switch.{deviceId}_ismuted",
            $"switch.{deviceId}_isvideoon",
            $"switch.{deviceId}_ishandraised",
            $"sensor.{deviceId}_isrecordingon",
            $"sensor.{deviceId}_isinmeeting",
            $"sensor.{deviceId}_issharing",
            $"sensor.{deviceId}_hasunreadmessages",
            $"switch.{deviceId}_isbackgroundblurred"

        };

            return entityNames;
        }
        public async Task PublishAsync(string topic, string payload, bool retain = true)
        {
            try
            {
                Log.Information($"Publishing to topic: {topic}");
                Log.Information($"Payload: {payload}");
                Log.Information($"Retain flag: {retain}");

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(retain)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Log.Information("Publish successful.");
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT publish: {ex.Message}");
                // Depending on the severity, you might want to rethrow the exception or handle it here.
            }
        }

        public async Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qos)
        {
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(qos))
                .Build();
            try
            {
                await _mqttClient.SubscribeAsync(subscribeOptions);
            }
            catch (Exception ex)
            {
                Log.Information($"Error during MQTT subscribe: {ex.Message}");
                // Depending on the severity, you might want to rethrow the exception or handle it here.
            }
            Log.Information("Subscribing." + subscribeOptions);
        }

        #endregion Public Methods

        #region Private Methods

        private async Task HandleReceivedApplicationMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            if (MessageReceived != null)
            {
                await MessageReceived(e);
                Log.Information($"Received message on topic {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
            }
        }

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            Log.Information($"Received message on topic {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
            // Trigger the event to notify subscribers
            MessageReceived?.Invoke(e);

            return Task.CompletedTask;
        }

        #endregion Private Methods
    }
}