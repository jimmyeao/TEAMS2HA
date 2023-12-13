using MQTTnet;
using MQTTnet.Client;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TEAMS2HA.API
{
    public class MqttClientWrapper
    {
        private MqttClient _mqttClient;
        private MqttClientOptions _mqttOptions;

        public bool IsConnected => _mqttClient.IsConnected;

        public MqttClientWrapper(string clientId, string mqttBroker, string username, string password)
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient() as MqttClient;

            _mqttOptions = new MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(mqttBroker)
                .WithCredentials(username, password)
                .WithCleanSession()
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        }
        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            // Example processing of the message
            Debug.WriteLine($"Received message on topic {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");

            // Implement your specific message handling logic here

            return Task.CompletedTask;
        }
        public void Dispose()
        {
            if (_mqttClient != null)
            {
                _ = _mqttClient.DisconnectAsync(); // Disconnect asynchronously
                _mqttClient.Dispose();
            }
        }


        public async Task ConnectAsync()
        {
            if (_mqttClient.IsConnected)
            {
                Debug.WriteLine("MQTT client is already connected.");
                return;
            }

            try
            {
                Debug.WriteLine("Attempting to connect to MQTT broker...");
                await _mqttClient.ConnectAsync(_mqttOptions);
                Debug.WriteLine("Connected to MQTT broker.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to connect to MQTT broker: {ex.Message}");
            }
        }


        public async Task DisconnectAsync()
        {
            if (!_mqttClient.IsConnected)
            {
                Debug.WriteLine("MQTT client is not connected.");
                return;
            }

            try
            {
                Debug.WriteLine("Disconnecting from MQTT broker...");
                await _mqttClient.DisconnectAsync();
                Debug.WriteLine("Disconnected from MQTT broker.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to disconnect from MQTT broker: {ex.Message}");
            }
        }


        public async Task PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                Debug.WriteLine($"Publishing to topic: {topic}");
                Debug.WriteLine($"Payload: {payload}");
                Debug.WriteLine($"Retain flag: {retain}");

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(retain)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Debug.WriteLine("Publish successful.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during MQTT publish: {ex.Message}");
                // Depending on the severity, you might want to rethrow the exception or handle it here.
            }
        }


        public async Task SubscribeAsync(string topic)
        {
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic)
                .Build();

            await _mqttClient.SubscribeAsync(subscribeOptions);
        }

    }
        
}