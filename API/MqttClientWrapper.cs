using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

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

        #region Public Constructors
        public bool IsAttemptingConnection
        {
            get { return _isAttemptingConnection; }
            private set { _isAttemptingConnection = value; }
        }
        public MqttClientWrapper(string clientId, string mqttBroker, string mqttPort, string username, string password)
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient() as MqttClient;

            int mqttportInt = System.Convert.ToInt32(mqttPort);

            _mqttOptions = new MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(mqttBroker, mqttportInt)
                .WithCredentials(username, password)
                .WithCleanSession()
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            Log.Information("MQTT Client Created");
        }

        public MqttClientWrapper(/* parameters */)
        {
            // Existing initialization code...

            _mqttClient.ApplicationMessageReceivedAsync += HandleReceivedApplicationMessage;
            Log.Information("MQTT Client Created");
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
                    break;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to connect to MQTT broker: {ex.Message}");
                    retryCount++;
                    await Task.Delay(RetryDelayMilliseconds);
                }
            }

            _isAttemptingConnection = false;
            if (!_mqttClient.IsConnected)
            {
                Log.Error("Failed to connect to MQTT broker after several attempts.");
            }
        }

        public async Task DisconnectAsync()
        {
            if (!_mqttClient.IsConnected)
            {
                Log.Debug("MQTTClient is not connected");
                return;
            }

            try
            {
                await _mqttClient.DisconnectAsync();
                Log.Information("MQTT Disconnected");
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