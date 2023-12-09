using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TEAMS2HA.API
{
    public class WebSocketClient
    {
        #region Private Fields

        private readonly ClientWebSocket _clientWebSocket;
        private readonly State _state;

        private bool _isConnected;

        #endregion Private Fields

        #region Public Constructors

        public WebSocketClient(Uri uri, State state)
        {
            _clientWebSocket = new ClientWebSocket();
            _state = state;
            Task.Run(() => ConnectAsync(uri));

            // Subscribe to the MessageReceived event
            MessageReceived += OnMessageReceived;
        }

        public event Action<bool> ConnectionStatusChanged;

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    ConnectionStatusChanged?.Invoke(_isConnected);
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            MessageReceived -= OnMessageReceived;
            if (_clientWebSocket.State == WebSocketState.Open)
            {
                await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed by client", cancellationToken);
                Console.WriteLine("WebSocket connection closed");
            }
        }

        #endregion Public Constructors

        #region Public Events

        public event EventHandler<string> MessageReceived;

        #endregion Public Events

        #region Public Methods

        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await _clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
            Console.WriteLine($"Message sent: {message}");
        }

        #endregion Public Methods

        #region Private Methods

        private Dictionary<string, object> meetingState = new Dictionary<string, object>()
        {
            { "isMuted", false },
            { "isCameraOn", false },
            { "isHandRaised", false },
            { "isInMeeting", "Not in a meeting" },
            { "isRecordingOn", false },
            { "isBackgroundBlurred", false },
        };

        private async Task ConnectAsync(Uri uri)
        {
            try
            {
                string token = TokenStorage.GetToken();

                if (!string.IsNullOrEmpty(token))
                {
                    // Modify the URI to include the token
                    var builder = new UriBuilder(uri) { Query = $"token={token}&{uri.Query.TrimStart('?')}" };
                    uri = builder.Uri;
                }

                await _clientWebSocket.ConnectAsync(uri, CancellationToken.None);
                Console.WriteLine("WebSocket connected");
                IsConnected = _clientWebSocket.State == WebSocketState.Open;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Console.WriteLine($"Connection failed: {ex.Message}");
                // Other error handling...
            }

            await ReceiveLoopAsync();
        }

        private void OnMessageReceived(object sender, string message)
        {
            if (message.Contains("tokenRefresh")) // Adjust based on the actual message format
            {
                var tokenUpdate = JsonConvert.DeserializeObject<TokenUpdate>(message);
                TokenStorage.SaveToken(tokenUpdate.NewToken);
            }
            // Update the Message property of the State class
            var settings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new MeetingUpdateConverter() }
            };

            MeetingUpdate meetingUpdate = JsonConvert.DeserializeObject<MeetingUpdate>(message, settings);

            if (meetingUpdate?.MeetingPermissions?.CanPair == true)
            {
                // The 'canPair' permission is true, initiate pairing
                PairWithTeamsAsync();
            }
            // Update the meeting state dictionary
            if (meetingUpdate.MeetingState != null)
            {
                meetingState["isMuted"] = meetingUpdate.MeetingState.IsMuted;
                meetingState["isCameraOn"] = meetingUpdate.MeetingState.IsVideoOn;
                meetingState["isHandRaised"] = meetingUpdate.MeetingState.IsHandRaised;
                meetingState["isInMeeting"] = meetingUpdate.MeetingState.IsInMeeting;
                meetingState["isRecordingOn"] = meetingUpdate.MeetingState.IsRecordingOn;
                meetingState["isBackgroundBlurred"] = meetingUpdate.MeetingState.IsBackgroundBlurred;
                meetingState["isSharing"] = meetingUpdate.MeetingState.IsSharing;
                if (meetingUpdate.MeetingState.IsVideoOn)
                {
                    State.Instance.Camera = "On";
                }
                else
                {
                    State.Instance.Camera = "Off";
                }

                if (meetingUpdate.MeetingState.IsInMeeting)
                {
                    State.Instance.Activity = "In a meeting";
                }
                else
                {
                    State.Instance.Activity = "Not in a Call";
                }

                if (meetingUpdate.MeetingState.IsMuted)
                {
                    State.Instance.Microphone = "On";
                }
                else
                {
                    State.Instance.Microphone = "Off";
                }

                if (meetingUpdate.MeetingState.IsHandRaised)
                {
                    State.Instance.Handup = "Raised";
                }
                else
                {
                    State.Instance.Handup = "Lowered";
                }

                if (meetingUpdate.MeetingState.IsRecordingOn)
                {
                    State.Instance.Recording = "On";
                }
                else
                {
                    State.Instance.Recording = "Off";
                }

                if (meetingUpdate.MeetingState.IsBackgroundBlurred)
                {
                    State.Instance.Blurred = "Blurred";
                }
                else
                {
                    State.Instance.Blurred = "Not Blurred";
                }
                if (meetingUpdate.MeetingState.IsSharing)
                {
                    State.Instance.issharing = "Sharing";
                }
                else
                {
                    State.Instance.issharing = "Not Sharing";
                }

                // need to edit state class to add handraised, recording, and backgroundblur
            }
        }

        private async Task PairWithTeamsAsync()
        {
            if (_isConnected)
            {
                string pairingCommand = "{\"action\":\"pair\",\"parameters\":{},\"requestId\":1}"; // Construct the pairing command as per API documentation
                await SendMessageAsync(pairingCommand);
                // You might also want to handle the response to this pairing command
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken = default)
        {
            const int bufferSize = 4096; // Starting buffer size
            byte[] buffer = new byte[bufferSize];
            int totalBytesReceived = 0;

            while (_clientWebSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await _clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer, totalBytesReceived, buffer.Length - totalBytesReceived), cancellationToken);
                totalBytesReceived += result.Count;

                if (result.EndOfMessage)
                {
                    string messageReceived = Encoding.UTF8.GetString(buffer, 0, totalBytesReceived);
                    Console.WriteLine($"Message received: {messageReceived}");

                    if (!cancellationToken.IsCancellationRequested && !string.IsNullOrEmpty(messageReceived))
                    {
                        MessageReceived?.Invoke(this, messageReceived);
                    }

                    // Reset buffer and totalBytesReceived for next message
                    buffer = new byte[bufferSize];
                    totalBytesReceived = 0;
                }
                else if (totalBytesReceived == buffer.Length) // Resize buffer if it's too small
                {
                    Array.Resize(ref buffer, buffer.Length + bufferSize);
                }
            }
            IsConnected = _clientWebSocket.State == WebSocketState.Open;
        }

        public class MeetingUpdateConverter : JsonConverter<MeetingUpdate>
        {
            #region Public Methods

            public override MeetingUpdate ReadJson(JsonReader reader, Type objectType, MeetingUpdate existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                JObject jsonObject = JObject.Load(reader);
                MeetingState meetingState = null;
                MeetingPermissions meetingPermissions = null;

                // Check if 'meetingUpdate' is present in JSON
                JToken meetingUpdateToken = jsonObject["meetingUpdate"];
                if (meetingUpdateToken != null)
                {
                    // Check if 'meetingState' is present in 'meetingUpdate'
                    JToken meetingStateToken = meetingUpdateToken["meetingState"];
                    if (meetingStateToken != null)
                    {
                        meetingState = meetingStateToken.ToObject<MeetingState>();
                    }

                    // Check if 'meetingPermissions' is present in 'meetingUpdate'
                    JToken meetingPermissionsToken = meetingUpdateToken["meetingPermissions"];
                    if (meetingPermissionsToken != null)
                    {
                        meetingPermissions = meetingPermissionsToken.ToObject<MeetingPermissions>();
                    }
                }

                return new MeetingUpdate
                {
                    MeetingState = meetingState,
                    MeetingPermissions = meetingPermissions
                };
            }

            public override void WriteJson(JsonWriter writer, MeetingUpdate value, JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }

            #endregion Public Methods
        }

        public class TokenUpdate
        {
            #region Public Properties

            [JsonProperty("tokenRefresh")]
            public string NewToken { get; set; }

            #endregion Public Properties
        }

        #endregion Private Methods
    }
}