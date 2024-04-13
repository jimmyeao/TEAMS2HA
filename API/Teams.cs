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

namespace TEAMS2HA.API
{
    public class WebSocketClient
    {
        #region Private Fields

        private readonly string _settingsFilePath;
        private readonly State _state;
        private readonly Action<string> _updateTokenAction;
        private ClientWebSocket _clientWebSocket;
        private Uri _currentUri;
        private bool _isConnected;
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

        public event Action Disconnected;

        #endregion Public Events

        #region Public Properties

        public event EventHandler RequirePairing;

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    ConnectionStatusChanged?.Invoke(_isConnected);
                    Log.Debug($"Teams Connection Status Changed: {_isConnected}");
                }
            }
        }

        #endregion Public Properties

        #region Public Methods

        public async Task ConnectAsync(Uri uri)
        {
            _currentUri = uri;
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 30 seconds timeout
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
                    Log.Debug($"Token: {token}");
                    var builder = new UriBuilder(uri) { Query = $"token={token}&{uri.Query.TrimStart('?')}" };
                    uri = builder.Uri;
                }
                Log.Debug($"WebSocket State before connecting: {_clientWebSocket.State}");

                Log.Debug($"Connecting to Teams on {uri}");
                try
                {
                    await _clientWebSocket.ConnectAsync(uri, cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Error("Connection timed out.");
                }
                Log.Debug($"Connected to {uri}");
                IsConnected = _clientWebSocket.State == WebSocketState.Open;
                Log.Debug($"IsConnected: {IsConnected}");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Log.Error(ex, "ConnectAsync: Error connecting to WebSocket");
                if (ex.Message.Contains("Unable to connect to the remote server")) // Simplified example, adjust based on actual error handling
                {
                    // Signal need for re-pairing, e.g., via an event or state change
                    RequirePairing?.Invoke(this, EventArgs.Empty);
                }
                RequirePairing?.Invoke(this, EventArgs.Empty);
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

        public async Task<bool> CheckConnectionHealthAsync()
        {
            if (_clientWebSocket.State != WebSocketState.Open)
            {
                return false;
            }

            try
            {
                // Example of sending a simple message to check for response
                // Adjust according to how your Teams WebSocket server expects communication
                string healthCheckMessage = "{\"action\":\"healthCheck\"}";
                await SendMessageAsync(healthCheckMessage);

                // Awaiting a simple acknowledgment or using an existing mechanism to confirm the message was received and processed
                // This part depends on how your server responds. You may need an additional mechanism to await the response.

                return true; // Assuming sending succeeds and you somehow confirm receipt
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking WebSocket connection health: {ex}");
                return false;
            }
        }

        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            if (_clientWebSocket.State != WebSocketState.Open)
            {
                Log.Warning("WebSocket is not open. Cannot send message.");
                return;
            }
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await _clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
            Log.Debug($"Message Sent: {message}");
        }

        // Public method to initiate connection
        public async Task StartConnectionAsync(Uri uri)
        {
            if (!_isConnected || _clientWebSocket.State != WebSocketState.Open)
            {
                await ConnectAsync(uri);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                MessageReceived -= OnMessageReceived;
                if (_clientWebSocket.State != WebSocketState.Closed)
                {
                    try
                    {
                        await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed by client", cancellationToken);
                        Log.Debug("Websocket Connection Closed");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error closing WebSocket");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error detaching event handler");
            }
        }

        public async Task SendReactionToTeamsAsync(string reactionType)
        {
            // Construct the JSON payload for the reaction message
            var reactionPayload = new
            {
                action = "send-reaction",
                parameters = new { type = reactionType },
                requestId = new Random().Next(1, int.MaxValue) // Generate a random request ID
            };

            string message = JsonConvert.SerializeObject(reactionPayload);

            // Use the SendMessageAsync method to send the reaction message
            await SendMessageAsync(message);
            Log.Information($"Reaction '{reactionType}' sent to Teams.");
        }

        #endregion Public Methods

        #region Private Methods

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
            else if (message.Contains("meetingPermissions"))
            {
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
                    _ = PairWithTeamsAsync();
                }
                // need to add in sensors for permissions
                if (meetingUpdate?.MeetingPermissions?.CanToggleMute == true)
                {
                    State.Instance.CanToggleMute = true;
                }
                else
                {
                    State.Instance.CanToggleMute = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanToggleVideo == true)
                {
                    State.Instance.CanToggleVideo = true;
                }
                else
                {
                    State.Instance.CanToggleVideo = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanToggleHand == true)
                {
                    State.Instance.CanToggleHand = true;
                }
                else
                {
                    State.Instance.CanToggleHand = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanToggleBlur == true)
                {
                    State.Instance.CanToggleBlur = true;
                }
                else
                {
                    State.Instance.CanToggleBlur = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanLeave == true)
                {
                    State.Instance.CanLeave = true;
                }
                else
                {
                    State.Instance.CanLeave = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanReact == true)
                {
                    State.Instance.CanReact = true;
                }
                else
                {
                    State.Instance.CanReact = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanToggleShareTray == true)
                {
                    State.Instance.CanToggleShareTray = true;
                }
                else
                {
                    State.Instance.CanToggleShareTray = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanToggleChat == true)
                {
                    State.Instance.CanToggleChat = true;
                }
                else
                {
                    State.Instance.CanToggleChat = false;
                }

                if (meetingUpdate?.MeetingPermissions?.CanStopSharing == true)
                {
                    State.Instance.CanStopSharing = true;
                }
                else
                {
                    State.Instance.CanStopSharing = false;
                }

                //              update the meeting state dictionary
                if (meetingUpdate.MeetingState != null)
                {
                    meetingState["isMuted"] = meetingUpdate.MeetingState.IsMuted;
                    meetingState["isCameraOn"] = meetingUpdate.MeetingState.IsVideoOn;
                    meetingState["isHandRaised"] = meetingUpdate.MeetingState.IsHandRaised;
                    meetingState["isInMeeting"] = meetingUpdate.MeetingState.IsInMeeting;
                    meetingState["isRecordingOn"] = meetingUpdate.MeetingState.IsRecordingOn;
                    meetingState["isBackgroundBlurred"] = meetingUpdate.MeetingState.IsBackgroundBlurred;
                    meetingState["isSharing"] = meetingUpdate.MeetingState.IsSharing;
                    meetingUpdate.MeetingState.teamsRunning = IsConnected;
                    if (meetingUpdate.MeetingState.IsVideoOn)
                    {
                        State.Instance.Camera = "On";
                    }
                    else
                    {
                        State.Instance.Camera = "Off";
                    }
                    if (meetingUpdate.MeetingState.teamsRunning)
                    {
                        State.Instance.teamsRunning = true;
                    }
                    else
                    {
                        State.Instance.teamsRunning = false;
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
                    Disconnected?.Invoke();
                    break;
                }
            }
            IsConnected = _clientWebSocket.State == WebSocketState.Open;
            Log.Debug($"IsConnected: {IsConnected}");
        }

        private async Task ReconnectAsync()
        {
            const int maxRetryCount = 5;
            int retryDelay = 2000; // milliseconds
            int retryCount = 0;

            while (retryCount < maxRetryCount)
            {
                try
                {
                    Log.Debug($"Attempting reconnection, try {retryCount + 1} of {maxRetryCount}");
                    _clientWebSocket = new ClientWebSocket(); // Create a new instance
                    await ConnectAsync(_currentUri);
                    if (IsConnected)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Reconnect attempt {retryCount + 1} failed: {ex.Message}");
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

        private void SaveAppSettings(AppSettings settings)
        {
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            try
            {
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving settings to file");
            }
        }

        #endregion Private Methods

        #region Public Classes

        public class MeetingUpdateConverter : JsonConverter<MeetingUpdate>
        {
            #region Public Methods

            public override MeetingUpdate ReadJson(JsonReader reader, Type objectType, MeetingUpdate? existingValue, bool hasExistingValue, JsonSerializer serializer)
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

            public override void WriteJson(JsonWriter writer, MeetingUpdate? value, JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }

            #endregion Public Methods
        }

        public class TeamsUpdateEventArgs : EventArgs
        {
            #region Public Properties

            public MeetingUpdate? MeetingUpdate { get; set; }

            #endregion Public Properties
        }

        public class TokenUpdate
        {
            #region Public Properties

            [JsonProperty("tokenRefresh")]
            public string? NewToken { get; set; }

            #endregion Public Properties
        }

        #endregion Public Classes
    }
}