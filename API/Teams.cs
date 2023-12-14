using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows;
using Serilog;

namespace TEAMS2HA.API
{
    
    public class WebSocketClient
    {
        private TaskCompletionSource<string> _pairingResponseTaskSource;
        public event EventHandler<TeamsUpdateEventArgs> TeamsUpdateReceived;
        public class TeamsUpdateEventArgs : EventArgs
        {
            public MeetingUpdate MeetingUpdate { get; set; }
        }
        #region Private Fields
        private AppSettings _appSettings;
        private readonly ClientWebSocket _clientWebSocket;
        private readonly State _state;
        private readonly string _settingsFilePath;
        private bool _isConnected;
        
        private readonly Action<string> _updateTokenAction;
        #endregion Private Fields

        #region Public Constructors

        public WebSocketClient(Uri uri, State state, string settingsFilePath, Action<string> updateTokenAction)
        {
            _clientWebSocket = new ClientWebSocket();
            _state = state;
            _settingsFilePath = settingsFilePath;
            _appSettings = LoadAppSettings(settingsFilePath);
            
           // Task.Run(() => ConnectAsync(uri));
            Log.Debug("Websocket Client Started");
            // Subscribe to the MessageReceived event
            MessageReceived += OnMessageReceived;
            _updateTokenAction = updateTokenAction;
        }
        // Public method to initiate connection
        public async Task StartConnectionAsync(Uri uri)
        {
            if (!_isConnected && _clientWebSocket.State != WebSocketState.Open)
            {
                await ConnectAsync(uri);
            }
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
                    Log.Debug($"Connection Status Changed: {_isConnected}");
                }
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

        #endregion Public Constructors

        #region Public Events

        public event EventHandler<string> MessageReceived;

        #endregion Public Events

        #region Public Methods

        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await _clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
            Log.Debug($"Message Sent: {message}");
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

        public async Task ConnectAsync(Uri uri)
        {
            try
            {
                // Check if the WebSocket is already connecting or connected
                if (_clientWebSocket.State != WebSocketState.None &&
                    _clientWebSocket.State != WebSocketState.Closed)
                {
                    Log.Debug("ConnectAsync: WebSocket is already connecting or connected.");
                    return;
                }

                string token = _appSettings.TeamsToken;

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
            }

            // Start receiving messages
            await ReceiveLoopAsync();
        }




        private AppSettings LoadAppSettings(string filePath)
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<AppSettings>(json);
            }
            else
            {
                return new AppSettings(); // Defaults if file does not exist
            }
        }
        private void SaveAppSettings(AppSettings settings)
        {
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, json);
        }
        private bool IsPairingResponse(string message)
        {
            // Implement logic to determine if the message is a response to the pairing request
            // This could be based on message content, format, etc.
            return message.Contains("tokenRefresh");
        }
        private void OnMessageReceived(object sender, string message)
        {

            Log.Debug($"Message Received: {message}");
            if (IsPairingResponse(message))
            {
                // Complete the task with the received message
                _pairingResponseTaskSource?.SetResult(message);
            }
            if (message.Contains("tokenRefresh"))
            {
                _pairingResponseTaskSource?.SetResult(message);
                Log.Information("Result Message {message}", message);
                var tokenUpdate = JsonConvert.DeserializeObject<TokenUpdate>(message);
                _appSettings.TeamsToken = tokenUpdate.NewToken;
                SaveAppSettings(_appSettings);
                Log.Debug($"Token Updated: {_appSettings.TeamsToken}");
                // Update the UI on the main thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _updateTokenAction?.Invoke(_appSettings.TeamsToken);
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
                        TeamsUpdateReceived?.Invoke(this, new TeamsUpdateEventArgs { MeetingUpdate = meetingUpdate });
                        Log.Debug($"Meeting State Updated: {meetingState}");
                    }
                }
            

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
                    _appSettings.TeamsToken = newToken;
                    SaveAppSettings(_appSettings);

                    _updateTokenAction?.Invoke(newToken); // Invoke the action to update UI
                }
                else
                {
                    Log.Warning("Pairing response timed out.");
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
                WebSocketReceiveResult result = await _clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer, totalBytesReceived, buffer.Length - totalBytesReceived), cancellationToken);
                totalBytesReceived += result.Count;

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
            IsConnected = _clientWebSocket.State == WebSocketState.Open;
            Log.Debug($"IsConnected: {IsConnected}");
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