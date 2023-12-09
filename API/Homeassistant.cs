using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace TEAMS2HA.API
{
    public class HomeAssistant
    {
        #region Private Fields

        private string _apiKey;
        private string _baseUrl;
        private HttpClient _httpClient;
        private bool connected = false;
        private bool isEnabled = false;
        private string name = "Homeassistant";
        private string oldact = null;
        private string oldcam = null;
        private string oldmic = null;
        private string oldstattus = null;
        private TEAMS2HA.API.State stateInstance;
        private readonly ClientWebSocket _webSocket = new ClientWebSocket();
        private readonly Uri _url;
        private readonly string _accessToken;
        private int _requestIdSequence = 1;
        private Dictionary<int, Action<JToken>> _requests = new Dictionary<int, Action<JToken>>();


        #endregion Private Fields

        #region Public Constructors

        public HomeAssistant(string baseUrl, string apiKey)
        {
            _baseUrl = baseUrl;
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", _apiKey);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            stateInstance = TEAMS2HA.API.State.Instance;
            stateInstance.StateChanged += OnStateChanged;

        }

        #endregion Public Constructors

        #region Public Events

        public event EventHandler StateChanged;

        #endregion Public Events

        #region Public Properties

        public string Name
        {
            get { return name; }
        }

        public string State
        {
            get { return stateInstance.ToString(); }
            set { /* You can leave this empty since the State property is read-only */ }
        }

        #endregion Public Properties

        #region Public Methods

        private Dictionary<string, string> activityIcons = new Dictionary<string, string>()
        {
            { "In a call", "mdi:phone-in-talk-outline" },
            { "On the phone", "mdi:phone-in-talk-outline" },
            { "Offline", "mdi:account-off" },
            { "In a meeting", "mdi:phone-in-talk-outline" },
            { "In A Conference Call", "mdi:phone-in-talk-outline" },
            { "Out of Office", "mdi:account-off" },
            { "Not in a Call", "mdi:account" },
            { "Presenting", "mdi:presentation-play" }
        };

        private Dictionary<string, string> statusIcons = new Dictionary<string, string>()
        {
            { "Busy", "mdi:account-cancel" },
            { "On the phone", "mdi:phone-in-talk-outline" },
            { "Do not disturb", "mdi:minus-circle-outline" },
            { "Away", "mdi:timer-sand" },
            { "Be right back", "mdi:timer-sand" },
            { "Available", "mdi:account" },
            { "Offline", "mdi:account-off" }
        };

        public static bool IsValidUrl(string url)
        {
            Uri uriResult;
            return Uri.TryCreate(url, UriKind.Absolute, out uriResult)
                   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        public async Task<bool> CheckConnectionAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/states");

                if (response.IsSuccessStatusCode)
                {
                    connected = true;
                }
                else
                {
                    connected = false;
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task Create_Entity(string entity)
        {
            if (!IsValidUrl(_baseUrl))
            { return; }
            var client = new HttpClient();
            client.BaseAddress = new Uri(_baseUrl + "/api/");

            var token = "Bearer " + _apiKey;
            client.DefaultRequestHeaders.Add("Authorization", token);

            var content = new StringContent($@"{{
                ""entity_id"": ""{entity}"",
                ""state"": ""Unknown""
            }}");

            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            try
            {
                var response = await client.PostAsync($"states/{entity}", content);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
            }
            client.Dispose();
        }

        public async Task<bool> EntityExists(string entity)
        {
            if (!IsValidUrl(_baseUrl))
            { return false; }
            var client = new HttpClient();
            client.BaseAddress = new Uri(_baseUrl + "/api/");
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _apiKey);
            try
            {
                var response = await client.GetAsync($"states/{entity}");
                return response.IsSuccessStatusCode;
                client.Dispose();
            }
            catch
            {
                return false;
            }
        }

        public async Task Start()
        {
            var connectionSuccessful = await CheckConnectionAsync();
            if (connectionSuccessful)
            {
                if (string.IsNullOrWhiteSpace(_baseUrl))
                {
                    return;
                }

                string[] entityNames = new string[]
                {
                    "sensor.hats_isSharing",
                    "sensor.hats_activity",
                    "switch.hats_camera",
                    "switch.hats_microphone",
                    "switch.hats_blurred",
                    "switch.hats_hand",
                    "switch.hats_recording"
                };

                foreach (var entityName in entityNames)
                {
                    if (!await EntityExists(entityName))
                    {
                        await Create_Entity(entityName);
                    }
                }
                string _wsUrl;

                if (_baseUrl.StartsWith("https://"))
                {
                    _wsUrl = _baseUrl.Replace("https://", "wss://") + "/api/websocket";
                }
                else if (_baseUrl.StartsWith("http://"))
                {
                    _wsUrl = _baseUrl.Replace("http://", "ws://") + "/api/websocket";
                }
                else
                {
                    throw new InvalidOperationException("Invalid _baseUrl format. Should start with http:// or https://");
                }

                var client = new HomeAssistantClient(_wsUrl, _apiKey);
                await client.ConnectAsync();

            }
        }

        public async Task UpdateEntity(string entityName, string stateText, string icon)
        {
            if (connected)
            {
                var client = new HttpClient();
                client.BaseAddress = new Uri(_baseUrl + "/api/");
                var token = "Bearer " + _apiKey;
                client.DefaultRequestHeaders.Add("Authorization", token);
                System.Net.Http.HttpResponseMessage response;

                try
                {
                    response = await client.GetAsync($"states/{entityName}");
                    //Log.Debug("Response to GET request: {response}", response.ReasonPhrase);
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    isEnabled = false;
                    return;
                }
                //Log.Debug("Response to GET request: {response}", response);

                if (response.IsSuccessStatusCode)
                {
                    //Log.Information("Updating {entity} in Home assistant to {state}", entityName, stateText);

                    var payload = $@"{{
                    ""state"": ""{stateText}"",
                    ""entity_id"": ""{entityName}"",
                    ""attributes"": {{
                        ""icon"": ""{icon}""
                    }}
                }}";

                    var content = new StringContent(payload);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    //Log.Debug("Attempting to set homeassistant "+content.ToString());
                    try
                    {
                        response = await client.PostAsync($"states/{entityName}", content);
                    }
                    catch (Exception ex)
                    {
                        client.Dispose();
                    }
                    //Log.Debug("Response to POST request: {response}", response.ReasonPhrase);
                }
                else
                {
                    client.Dispose();
                    isEnabled = false;
                    return;
                }

                response.EnsureSuccessStatusCode();
                client.Dispose();
            }
        }

        private async void OnStateChanged(object sender, EventArgs e)
        {
            if (connected)
            {
                stateInstance = (State)sender;
                StateChanged?.Invoke(this, EventArgs.Empty);

                string statusIcon = statusIcons.TryGetValue(stateInstance.Status, out var icon) ? icon : "mdi:presentation-play";
                string activityIcon = activityIcons.TryGetValue(stateInstance.Activity, out _) ? icon : "mdi:account-off";

                if (stateInstance.Status != oldstattus)
                    await UpdateEntity("sensor.hats_isSharing", stateInstance.Status, statusIcon);

                if (stateInstance.Activity != oldact)
                    await UpdateEntity("sensor.hats_activity", stateInstance.Activity, activityIcon);
                await UpdateEntity("sensor.hats_isSharing", stateInstance.issharing, statusIcon);
                await UpdateEntity("switch.hats_camera", stateInstance.Camera, stateInstance.Camera == "On" ? "mdi:camera" : "mdi:camera-off");
                await UpdateEntity("switch.hats_microphone", stateInstance.Microphone, stateInstance.Microphone == "Off" ? "mdi:microphone" : "mdi:microphone-off");
                await UpdateEntity("switch.hats_blurred", stateInstance.Blurred, stateInstance.Blurred == "Blurred" ? "mdi:blur" : "mdi:blur-off");
                await UpdateEntity("switch.hats_hand", stateInstance.Handup, stateInstance.Handup == "Raised" ? "mdi:hand-back-left" : "mdi:hand-back-left-off");
                await UpdateEntity("switch.hats_recording", stateInstance.Recording, stateInstance.Recording == "On" ? "mdi:record-circle" : "mdi:record");

                oldstattus = stateInstance.Status;
                oldact = stateInstance.Activity;
            }
        }
    }

    public class HomeAssistantClient
    {
        private readonly ClientWebSocket _webSocket = new ClientWebSocket();
        private readonly Uri _url;
        private readonly string _accessToken;
        private int _requestIdSequence = 1;
        private Dictionary<int, Action<JToken>> _requests = new Dictionary<int, Action<JToken>>();
        private string _apiKey;
        private string _baseUrl;

        public HomeAssistantClient(string url, string accessToken)
        {
            _baseUrl = url;
            _apiKey = accessToken;
            _url = new Uri(url);
            _accessToken = accessToken;

        }


        public async Task ConnectAsync()
        {
            try
            {
                await _webSocket.ConnectAsync(_url, CancellationToken.None);
                _ = ProcessMessagesAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception during connection: " + ex.Message);
            }
        }

        public async Task SubscribeToEventsAsync(Action<JToken> callback)
        {
            string[] entityNames = new string[]
            {
        "sensor.hats_status",
        "sensor.hats_activity",
        "switch.hats_camera",
        "switch.hats_microphone",
        "switch.hats_blurred",
        "switch.hats_hand",
        "switch.hats_recording"
            };

            var requestId = _requestIdSequence++;
            _requests[requestId] = eventData =>
            {
                if (eventData["data"] is JObject data && data["entity_id"] is JValue entityId && entityNames.Contains((string)entityId))
                {
                    callback(eventData);
                }
            };

            var command = new
            {
                id = requestId,
                type = "subscribe_events",
                event_type = "state_changed"
            };

            var commandJson = JObject.FromObject(command).ToString();
            await _webSocket.SendAsync(Encoding.UTF8.GetBytes(commandJson), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task ProcessMessagesAsync()
        {
            var buffer = new byte[4096];
            var completeMessage = new StringBuilder();

            try
            {
                while (_webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        var messagePart = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        completeMessage.Append(messagePart);
                    }
                    while (!result.EndOfMessage);

                    var fullMessage = completeMessage.ToString();
                    completeMessage.Clear();

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    else
                    {
                        try
                        {
                            var messageData = JObject.Parse(fullMessage);

                            switch ((string)messageData["type"])
                            {
                                case "auth_required":
                                    await SendAuthenticationAsync();
                                    break;
                                case "auth_ok":
                                    Debug.WriteLine("Authenticated successfully");
                                    await SubscribeToEventsAsync(eventData =>
                                    {
                                        Debug.WriteLine("Received event: " + eventData);
                                    });
                                    break;
                                case "auth_invalid":
                                case "auth_failed":
                                    Debug.WriteLine("Authentication failed: " + messageData["message"]);
                                    break;
                                case "result":
                                case "event":
                                    var id = (int)messageData["id"];
                                    if (_requests.TryGetValue(id, out var callback) && (messageData["event"] is JObject || messageData["result"] is JObject))
                                    {
                                        callback(messageData["event"] ?? messageData["result"]);
                                    }
                                    break;
                            }
                        }
                        catch (JsonReaderException ex)
                        {
                            Debug.WriteLine($"Error parsing JSON: {ex.Message}");
                        }
                    }
                }
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine("WebSocketException: " + ex.Message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception: " + ex.Message);
            }
        }


        private async Task SendAuthenticationAsync()
        {
            var authMessage = new
            {
                type = "auth",
                access_token = _accessToken
            };

            var authMessageJson = JObject.FromObject(authMessage).ToString();
            await _webSocket.SendAsync(Encoding.UTF8.GetBytes(authMessageJson), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        #endregion Private Methods
    }
}
