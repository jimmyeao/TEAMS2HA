using System;
using System.Collections.Generic;
using System.Data;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Serilog;

using TEAMS2HA.Utils;

namespace TEAMS2HA.API
{
    public class WebSocketManager
    {
        private static readonly Lazy<WebSocketManager> _instance = new Lazy<WebSocketManager>(() => new WebSocketManager());
        private ClientWebSocket _clientWebSocket;
        private Uri _currentUri;
        private bool _isConnected;
        private bool _isConnecting;
        private System.Timers.Timer _reconnectTimer;
        private readonly TimeSpan _reconnectInterval = TimeSpan.FromSeconds(30); // Reconnect every 30 seconds
        private readonly Action<string> _updateTokenAction;
        public event EventHandler<TeamsUpdateEventArgs> TeamsUpdateReceived;
        public event EventHandler<string> MessageReceived;
        public event Action<bool> ConnectionStatusChanged;
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
        public static WebSocketManager Instance => _instance.Value;



        private WebSocketManager()
        {
            _clientWebSocket = new ClientWebSocket();
            _isConnected = false;
            _isConnecting = false;
            MessageReceived += OnMessageReceived;
            InitializeReconnectTimer();
        }
        private void InitializeReconnectTimer()
        {
            _reconnectTimer = new System.Timers.Timer(_reconnectInterval.TotalMilliseconds);
            _reconnectTimer.Elapsed += async (sender, args) => await EnsureConnectedAsync();
            _reconnectTimer.AutoReset = true;
            _reconnectTimer.Enabled = true;
        }
        public bool IsConnected => _isConnected && _clientWebSocket.State == WebSocketState.Open;

        public async Task ConnectAsync(Uri uri)
        {
            if (_isConnecting || _clientWebSocket.State == WebSocketState.Open)
                return;

            _isConnecting = true;
            try
            {
                // Dispose of the old WebSocket and create a new one if it's not in the None state
                if (_clientWebSocket.State != WebSocketState.None)
                {
                    _clientWebSocket.Dispose();
                    _clientWebSocket = new ClientWebSocket();
                }

                _currentUri = uri;
                await _clientWebSocket.ConnectAsync(uri, CancellationToken.None);
                SetIsConnected(true);
                StartReceiving();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to connect to Teams: " + ex.Message);
                SetIsConnected(false);
            }
            finally
            {
                _isConnecting = false;
            }
        }
        private void SetIsConnected(bool value)
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                State.Instance.TeamsRunning = value; //update the MQTT state

                ConnectionStatusChanged?.Invoke(IsConnected); //update the UI
            }
        }

        public async Task PairWithTeamsAsync(Action<string> updateTokenCallback)
        {
            if (_isConnected)
            {
                _pairingResponseTaskSource = new TaskCompletionSource<string>();

                string pairingCommand = "{\"action\":\"pair\",\"parameters\":{},\"requestId\":1}";
                try
                {
                    await SendMessageAsync(pairingCommand);
                } catch (Exception ex) {
                    Log.Error("Error sending pairing command: " + ex.Message);
                }
                var responseTask = await Task.WhenAny(_pairingResponseTaskSource.Task, Task.Delay(TimeSpan.FromSeconds(30)));

                if (responseTask == _pairingResponseTaskSource.Task)
                {
                    var response = await _pairingResponseTaskSource.Task;
                    var newToken = JsonConvert.DeserializeObject<TokenUpdate>(response).NewToken;
                    AppSettings.Instance.PlainTeamsToken = newToken;
                    AppSettings.Instance.SaveSettingsToFile();

                    _updateTokenAction?.Invoke(newToken);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        if (mainWindow != null)
                        {
                            mainWindow.UpdatePairingStatus(true);
                        }
                    });

                }
                else
                {
                    Log.Warning("Pairing response timed out.");
                }
            }
        }
        private async void StartReceiving()
        {
            var buffer = new byte[4096];
            try
            {
                while (_clientWebSocket.State == WebSocketState.Open)
                {
                    var result = await _clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        SetIsConnected(false);

                        Log.Information("WebSocket closed.");
                    }
                    else
                    {
                        var message = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                        // Handle the message
                        Log.Information("Received message: " + message);
                        //handle the meesage
                        MessageReceived?.Invoke(this, message);

                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error in receiving loop: " + ex.Message);
                SetIsConnected(false);


            }
        }
        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                Log.Warning("WebSocket is not connected. Message not sent.");
                return;
            }
            try
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await _clientWebSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
                Log.Debug($"Message Sent: {message}");
            }
            catch (Exception ex)
            {
                Log.Error("Error sending message from websocket manager: " + ex.Message);
            }
        }
        public async Task EnsureConnectedAsync()
        {
            if (_clientWebSocket.State != WebSocketState.Open)
            {
                Log.Information("Reconnecting to Teams...");
                try
                {
                    await ConnectAsync(_currentUri);
                } catch (Exception ex)
                {
                    Log.Error("Error reconnecting to Teams: " + ex.Message);
                }
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
                    _ = PairWithTeamsAsync(newToken =>
                    {


                    });
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
                    meetingUpdate.MeetingState.TeamsRunning = IsConnected;
                    if (meetingUpdate.MeetingState.IsVideoOn)
                    {
                        State.Instance.Camera = "On";
                    }
                    else
                    {
                        State.Instance.Camera = "Off";
                    }
                    if (meetingUpdate.MeetingState.TeamsRunning)
                    {
                        State.Instance.TeamsRunning = true;
                    }
                    else
                    {
                        State.Instance.TeamsRunning = false;
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
                        Log.Information("Meeting State Updated: {meetingState}", meetingState);
                        TeamsUpdateReceived?.Invoke(this, new TeamsUpdateEventArgs { MeetingUpdate = meetingUpdate });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error in TeamsUpdateReceived");
                    }
                    
                }
            }
        }
        public async Task DisconnectAsync()
        {
            if (_clientWebSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
                    SetIsConnected(false);
                    Log.Information("Disconnected from server.");
                }
                catch (Exception ex)
                {
                    Log.Error("Error disconnecting from server: " + ex.Message);
                }
            }
        }
    }
}