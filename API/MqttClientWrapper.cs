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
        private const int RetryDelayMilliseconds = 1000; //wait a couple of seconds before retrying a connection attempt

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
                            // Log the certificate subject
                            Log.Debug("Certificate Subject: {0}", context.Certificate.Subject);

                            // This assumes you are trying to inspect the certificate directly;
                            // MQTTnet may not provide a direct IsValid flag or ChainErrors like .NET's X509Chain.
                            // Instead, you handle validation and log details manually:

                            bool isValid = true; // You should define the logic to set this based on your validation requirements

                            // Check for specific conditions, if necessary, such as expiry, issuer, etc.
                            // For example, if you want to ensure the certificate is issued by a specific entity:
                            //if (context.Certificate.Issuer != "CN=R3, O=Let's Encrypt, C=US")
                            //{
                            //    Log.Debug("Unexpected certificate issuer: {0}", context.Certificate.Issuer);
                            //    isValid = false; // Set to false if the issuer is not the expected one
                            //}

                            // Log any errors from the SSL policy errors if they exist
                            if (context.SslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                            {
                                Log.Debug("SSL policy errors: {0}", context.SslPolicyErrors.ToString());
                                isValid = false; // Consider invalid if there are any SSL policy errors
                            }

                            // You can decide to ignore certain errors by setting isValid to true regardless of the checks,
                            // but be careful as this might introduce security vulnerabilities.
                            if (ignoreCertificateErrors)
                            {
                                isValid = true; // Ignore certificate errors if your settings dictate
                            }

                            return isValid; // Return the result of your checks
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
            $"switch.{deviceId}_isbackgroundblurred",
            $"sensor.{deviceId}_teamsRunning"

        };

            return entityNames;
        }
 
public async Task PublishAsync(string topic, string payload, bool retain = true)
        {
            try
            {
                // Log the topic, payload, and retain flag
                Log.Information($"Publishing to topic: {topic}");
                Log.Information($"Payload: {payload}");
                Log.Information($"Retain flag: {retain}");

                // Build the MQTT message
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(retain)
                    .Build();

                // Publish the message using the MQTT client
                await _mqttClient.PublishAsync(message);
                Log.Information("Publish successful.");
            }
            catch (Exception ex)
            {
                // Log any errors that occur during MQTT publish
                Log.Information($"Error during MQTT publish: {ex.Message}");
                // Depending on the severity, you might want to rethrow the exception or handle it here.
            }
        }
        public void UpdateClientSettings(string mqttBroker, string mqttPort, string username, string password, bool useTLS, bool ignoreCertificateErrors, bool useWebsockets)
        {
            // Convert the MQTT port from string to integer, defaulting to 1883 if conversion fails
            if (!int.TryParse(mqttPort, out int portNumber))
            {
                portNumber = 1883; // Default MQTT port
                Log.Warning($"Invalid MQTT port provided, defaulting to {portNumber}");
            }

            // Start building the new MQTT client options
            var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId(Guid.NewGuid().ToString()) // Use a new client ID for a new connection
                .WithCredentials(username, password)
                .WithCleanSession();

            // Setup connection type: WebSockets or TCP
            if (useWebsockets)
            {
                string websocketUri = useTLS ? $"wss://{mqttBroker}:{portNumber}" : $"ws://{mqttBroker}:{portNumber}";
                mqttClientOptionsBuilder.WithWebSocketServer(websocketUri);
                Log.Information($"Updating MQTT client settings for WebSocket {(useTLS ? "with TLS" : "without TLS")} connection to {websocketUri}");
            }
            else
            {
                mqttClientOptionsBuilder.WithTcpServer(mqttBroker, portNumber);
                Log.Information($"Updating MQTT client settings for TCP {(useTLS ? "with TLS" : "without TLS")} connection to {mqttBroker}:{portNumber}");
            }

            // Setup TLS/SSL settings if needed
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
                        // Implement your TLS validation logic here
                        // Log any details necessary and return true if validation is successful
                        // For simplicity and security example, this will return true if ignoreCertificateErrors is true
                        return ignoreCertificateErrors; // WARNING: Setting this to always 'true' might pose a security risk
                    }
                });
            }

            // Apply the new settings to the MQTT client
            _mqttOptions = mqttClientOptionsBuilder.Build();

            // If needed, log the new settings or perform any other necessary actions here
            Log.Information("MQTT client settings updated successfully.");
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