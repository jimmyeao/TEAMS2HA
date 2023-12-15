using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TEAMS2HA.Properties;

namespace TEAMS2HA.API
{
    public class WebSocketClient
    {
        #region Private Fields

        private readonly ClientWebSocket _clientWebSocket;
        private readonly string _settingsFilePath;
        private readonly State _state;
        private readonly Action<string> _updateTokenAction;
        private bool _isConnected;
        private Uri _currentUri;

        private TaskCompletionSource<string> _pairingResponseTaskSource;

        private Dictionary<string, object> meetingState = new Dictionary<string, object>()
        {
            { "isMuted", false },
            { "isCameraOn", false },
            { "isHandRaised", false },
            { "isInMeeting", "Not in a meeting" },
            { "isRecordingOn", false },
            { "isBackgroundBlurred", false },
        };

        #endregion Private Fields

        #region Public Constructors

        public WebSocketClient(Uri uri, State state, string settingsFilePath, Action<string> updateTokenAction)
        {
            _clientWebSocket = new ClientWebSocket();
            _state = state;
            _settingsFilePath = settingsFilePath;

            // Task.Run(() => ConnectAsync(uri));
            Log.Debug("Websocket Client Started");
            // Subscribe to the MessageReceived event
            MessageReceived += OnMessageReceived;
            _updateTokenAction = updateTokenAction;
        }

        #endregion Public Constructors

        #region Public Events

        public event Action<bool> ConnectionStatusChanged;

        public event EventHandler<string> MessageReceived;

        public event EventHandler<TeamsUpdateEventArgs> TeamsUpdateReceived;

        #endregion Public Events

        #region Public Properties

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    ConnectionStatusChanged?.Invoke(_isConnected);
                    Log.Debug($"Connection Status Changed: {_isConnected}");
                }
            }
        }

        #endregion Public Properties

        #region Public Methods

        public async Task ConnectAsync(Uri uri)
        {
            _currentUri = uri;
            try
            {
                // Check if the WebSocket is already connecting or connected
                if (_clientWebSocket.State != WebSocketState.None &&
                    _clientWebSocket.State != WebSocketState.Closed)
                {
                    Log.Debug("ConnectAsync: WebSocket is already connecting or connected.");
                    return;
                }

                string token = AppSettings.Instance.PlainTeamsToken;

                if (!string.IsNullOrEmpty(token))
                {
                    // Modify the URI to include the token
                    Log.Debug($"Token: {token}");
                    var builder = new UriBuilder(uri) { Query = $"token={token}&{uri.Query.TrimStart('?')}" };
                    uri = builder.Uri;
                }

                await _clientWebSocket.ConnectAsync(uri, CancellationToken.None);
                Log.Debug($"Connected to {uri}");
                IsConnected = _clientWebSocket.State == WebSocketState.Open;
                Log.Debug($"IsConnected: {IsConnected}");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Log.Error(ex, "ConnectAsync: Error connecting to WebSocket");
                await ReconnectAsync();
            }

            // Start receiving messages
            await ReceiveLoopAsync();
        }

        public async Task PairWithTeamsAsync()
        {
            if (_isConnected)
            {
                _pairingResponseTaskSource = new TaskCompletionSource<string>();

                string pairingCommand = "{\"action\":\"pair\",\"parameters\":{},\"requestId\":1}";
                await SendMessageAsync(pairingCommand);

                var responseTask = await Task.WhenAny(_pairingResponseTaskSource.Task, Task.Delay(TimeSpan.FromSeconds(30)));

                if (responseTask == _pairingResponseTaskSource.Task)
                {
                    var response = await _pairingResponseTaskSource.Task;
                    var newToken = JsonConvert.DeserializeObject<TokenUpdate>(response).NewToken;
                    AppSettings.Instance.PlainTeamsToken = newToken;
                    AppSettings.Instance.SaveSettingsToFile();

                    _updateTokenAction?.Invoke(newToken); // Invoke the action to update UI
                    //subscribe to meeting updates
                }
                else
                {
                    Log.Warning("Pairing response timed out.");
                }
            }
        }

        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await _clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
            Log.Debug($"Message Sent: {message}");
        }

        // Public method to initiate connection
        public async Task StartConnectionAsync(Uri uri)
        {
            if (!_isConnected && _clientWebSocket.State != WebSocketState.Open)
            {
                await ConnectAsync(uri);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            MessageReceived -= OnMessageReceived;
            if (_clientWebSocket.State == WebSocketState.Open)
            {
                await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed by client", cancellationToken);

                Log.Debug("Websocket Connection Closed");
            }
        }

        #endregion Public Methods

        #region Private Methods

        private bool IsPairingResponse(string message)
        {
            // Implement logic to determine if the message is a response to the pairing request This
            // could be based on message content, format, etc.
            return message.Contains("tokenRefresh");
        }

        private void OnMessageReceived(object sender, string message)
        {
            Log.Debug($"Message Received: {message}");

            if (message.Contains("tokenRefresh"))
            {
                _pairingResponseTaskSource?.SetResult(message);
                Log.Information("Result Message {message}", message);
                var tokenUpdate = JsonConvert.DeserializeObject<TokenUpdate>(message);
                AppSettings.Instance.PlainTeamsToken = tokenUpdate.NewToken;
                AppSettings.Instance.SaveSettingsToFile();
                Log.Debug($"Token Updated: {AppSettings.Instance.PlainTeamsToken}");
                // Update the UI on the main thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _updateTokenAction?.Invoke(AppSettings.Instance.PlainTeamsToken);
                });
            }
            else if (message.Contains("meetingPermissions")) // Replace with actual keyword/structure
            {
                Log.Debug("Pairing...");
                // Update UI, save settings, reinitialize connection as needed

                // Update the Message property of the State class
                var settings = new JsonSerializerSettings
                {
                    Converters = new List<JsonConverter> { new MeetingUpdateConverter() }
                };

                MeetingUpdate meetingUpdate = JsonConvert.DeserializeObject<MeetingUpdate>(message, settings);

                if (meetingUpdate?.MeetingPermissions?.CanPair == true)
                {
                    // The 'canPair' permission is true, initiate pairing
                    Log.Debug("Pairing with Teams");
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
                    try
                    {
                        TeamsUpdateReceived?.Invoke(this, new TeamsUpdateEventArgs { MeetingUpdate = meetingUpdate });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error in TeamsUpdateReceived");
                    }
                    Log.Debug($"Meeting State Updated: {meetingState}");
                }
            }
        }
        private async Task ReconnectAsync()
        {
            const int maxRetryCount = 5;
            int retryDelay = 2000; // milliseconds
            int retryCount = 0;

            while (retryCount < maxRetryCount && !IsConnected)
            {
                try
                {
                    Log.Debug($"Attempting reconnection, try {retryCount + 1} of {maxRetryCount}");
                    await ConnectAsync(_currentUri);
                    if (IsConnected) break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Reconnect attempt failed: {ex.Message}");
                }

                retryCount++;
                await Task.Delay(retryDelay);
            }

            if (IsConnected)
            {
                Log.Information("Reconnected successfully.");
            }
            else
            {
                Log.Warning("Failed to reconnect after several attempts.");
            }
        }


        private async Task ReceiveLoopAsync(CancellationToken cancellationToken = default)
        {
            const int bufferSize = 4096; // Starting buffer size
            byte[] buffer = new byte[bufferSize];
            int totalBytesReceived = 0;

            while (_clientWebSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    WebSocketReceiveResult result = await _clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer, totalBytesReceived, buffer.Length - totalBytesReceived), cancellationToken);
                    totalBytesReceived += result.Count;
                    if (result.CloseStatus.HasValue)
                    {
                        Log.Debug($"WebSocket closed with status: {result.CloseStatus}");
                        IsConnected = false;
                        break; // Exit the loop if the WebSocket is closed
                    }
                    if (result.EndOfMessage)
                    {
                        string messageReceived = Encoding.UTF8.GetString(buffer, 0, totalBytesReceived);
                        Log.Debug($"ReceiveLoopAsync: Message Received: {messageReceived}");

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
                catch (Exception ex)
                {
                    Log.Error($"WebSocketException in ReceiveLoopAsync: {ex.Message}");
                    IsConnected = false;
                    await ReconnectAsync();
                    break;
                }
            }
            IsConnected = _clientWebSocket.State == WebSocketState.Open;
            Log.Debug($"IsConnected: {IsConnected}");
        }
    

        private void SaveAppSettings(AppSettings settings)
        {
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, json);
        }

        #endregion Private Methods

        #region Public Classes

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

        public class TeamsUpdateEventArgs : EventArgs
        {
            #region Public Properties

            public MeetingUpdate MeetingUpdate { get; set; }

            #endregion Public Properties
        }

        public class TokenUpdate
        {
            #region Public Properties

            [JsonProperty("tokenRefresh")]
            public string NewToken { get; set; }

            #endregion Public Properties
        }

        #endregion Public Classes
    }
}