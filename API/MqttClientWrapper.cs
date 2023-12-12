using MQTTnet;
using MQTTnet.Client;
using System;
using System.Threading.Tasks;

namespace TEAMS2HA.API
{
    public class MqttClientWrapper
    {
        private IMqttClient _mqttClient;
        private MqttClientOptions _mqttOptions;
        public bool IsConnected => _mqttClient.IsConnected;
        // Constructor for MqttClientWrapper class
        // Takes in clientId, mqttBroker, username, and password as parameters
        public MqttClientWrapper(string clientId, string mqttBroker, string username, string password)
        {
            // Create a new instance of MqttFactory
            var factory = new MqttFactory();

            // Create a new instance of MqttClient using the factory
            _mqttClient = factory.CreateMqttClient();

            // Build the MqttClientOptions using the MqttClientOptionsBuilder
            _mqttOptions = new MqttClientOptionsBuilder()
                .WithClientId(clientId) // Set the client ID
                .WithTcpServer(mqttBroker) // Set the MQTT broker
                .WithCredentials(username, password) // Set the username and password
                .WithCleanSession() // Enable clean session
                .Build(); // Build the options
        }

        // This method is used to establish a connection to the MQTT broker asynchronously
        public async Task ConnectAsync()
        {
            try
            {
                // Attempt to connect to the MQTT broker using the provided options
                await _mqttClient.ConnectAsync(_mqttOptions);
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the connection process
                // (e.g., connection failure)
            }
        }

        // Method to publish a message to a specified topic
        public async Task PublishAsync(string topic, string payload)
        {
            // Create a new MQTT application message builder
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic) // Set the topic of the message
                .WithPayload(payload) // Set the payload of the message
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce) // Set the quality of service level to at least once
                .Build(); // Build the message

            // Publish the message using the MQTT client
            await _mqttClient.PublishAsync(message);
        }

        // Method to disconnect from the MQTT broker
        public async Task DisconnectAsync()
        {
            // Check if the MQTT client is connected
            if (_mqttClient.IsConnected)
            {
                // Disconnect from the MQTT broker
                await _mqttClient.DisconnectAsync();
            }
        }

        // Additional methods as needed
    }
}