using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using TEAMS2HA.API;

namespace TEAMS2HA
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Private Fields

        private TEAMS2HA.API.HomeAssistant _homeAssistant;
        private string _homeassistantToken;
        private string _homeassistantURL;
        private string _teamsApiKey;
        private API.WebSocketClient _teamsClient;

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            if (Properties.Settings.Default.FirstTimeRunningThisVersion)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.FirstTimeRunningThisVersion = false;
                Properties.Settings.Default.Save();
            }
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
            LoadSettingsAsync();
            InitializeConnections();

        }

        #endregion Public Constructors

        #region Public Methods

        public async Task InitializeConnections()
        {
            // Other initialization code...
            if (_teamsClient != null && _teamsClient.IsConnected)
            {
                return; // Already connected, no need to reinitialize
            }
            string teamsToken = TokenStorage.GetToken();
            if (string.IsNullOrEmpty(teamsToken))
            {
                // If the Teams token is not set, then we can't connect to Teams
                return;
            }
            // Initialize the Teams WebSocket connection
            //var uri = new Uri("ws://localhost:8124?protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var state = new API.State();  // You would initialize this as necessary
            _teamsClient = new API.WebSocketClient(uri, state);
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;

            // Other initialization code...
        }

        #endregion Public Methods

        #region Private Methods

        // Get credentials
        private async Task LoadSettingsAsync()
        {
            // Assuming you have settings like HomeassistantURL, RunAtWindowsBoot, and RunMinimised defined

            // Load Homeassistant URL
            _homeassistantURL = Properties.Settings.Default.HomeassistantURL;
            HomeassistantURLBox.Text = _homeassistantURL;

            // Load CheckBox values
            RunAtWindowsBootCheckBox.IsChecked = Properties.Settings.Default.RunAtWindowsBoot;
            RunMinimisedCheckBox.IsChecked = Properties.Settings.Default.RunMinimised;

            // Load Homeassistant Token (assuming you're using TokenStorage for this)
            _homeassistantToken = TokenStorage.GetToken();
            HomeassistantTokenBox.Text = _homeassistantToken;

            // Rest of your logic...
            if (!string.IsNullOrEmpty(_homeassistantURL) && !string.IsNullOrEmpty(_homeassistantToken))
            {
                var homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
                _homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
                var connectionSuccessful = await homeAssistant.CheckConnectionAsync();
                HomeassistantConnectionStatus.Text = connectionSuccessful ? "Homeassistant: Connected" : "Homeassistant: Disconnected";
                if (connectionSuccessful)
                {
                    await _homeAssistant.Start();
                }
            }
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSettingsAsync();
            if (RunMinimisedCheckBox.IsChecked == true) // Check your run minimized setting
            {
                // Instead of minimizing, keep the app running in the background Show a toast
                // notification to inform the user
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe when the page is unloaded
            _teamsClient.ConnectionStatusChanged -= TeamsConnectionStatusChanged;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // Check if the Teams API key has changed
            string currentToken = TokenStorage.GetToken();
            bool tokenChanged = currentToken != TeamsApiKeyBox.Text;

            if (tokenChanged)
            {
                TokenStorage.SaveToken(TeamsApiKeyBox.Text);
                if (tokenChanged)
                {
                    InitializeConnections();
                }
            }

            // Store the Homeassistant token securely
            if (!string.IsNullOrEmpty(HomeassistantTokenBox.Text))
            {
                byte[] encryptedToken = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(HomeassistantTokenBox.Text),
                    null,
                    DataProtectionScope.CurrentUser);
                // Save the encrypted token to a secure place For example, to a file, database, or
                // use Properties.Settings.Default for simplicity
                Properties.Settings.Default.HomeassistantToken = Convert.ToBase64String(encryptedToken);
            }

            _homeassistantToken = HomeassistantTokenBox.Text;

            // Store the Homeassistant URL
            Properties.Settings.Default.HomeassistantURL = HomeassistantURLBox.Text;
            _homeassistantURL = HomeassistantURLBox.Text;

            // Save CheckBox values
            Properties.Settings.Default.RunAtWindowsBoot = RunAtWindowsBootCheckBox.IsChecked.Value;
            Properties.Settings.Default.RunMinimised = RunMinimisedCheckBox.IsChecked.Value;
            Properties.Settings.Default.Save();
        }

        private void TeamsConnectionStatusChanged(bool isConnected)
        {
            // The UI needs to be updated on the main thread.
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
            });
        }

        private async void TestHomeassistantConnection_Click(object sender, RoutedEventArgs e)
        {
            // Implement the code to test the connection to Home Assistant If the connection is
            // successful, update the HomeassistantConnectionStatus
            // Example: HomeassistantConnectionStatus.Text = "Homeassistant: Connected";
            _homeassistantToken = HomeassistantTokenBox.Text;
            if (string.IsNullOrEmpty(_homeassistantToken))
            {
                return;
            }
            var homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
            _homeAssistant = new TEAMS2HA.API.HomeAssistant(_homeassistantURL, _homeassistantToken);
            var connectionSuccessful = await homeAssistant.CheckConnectionAsync();
            HomeassistantConnectionStatus.Text = connectionSuccessful ? "Homeassistant: Connected" : "Homeassistant: Disconnected";
            if (connectionSuccessful)
            {
                await _homeAssistant.Start();
            }
        }

        private void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {
            string teamsToken = TokenStorage.GetToken();
            // Implement the code to test the connection to Teams If the connection is successful,
            // update the TeamsConnectionStatus
            // Example: TeamsConnectionStatus.Text = "Teams: Connected";
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");

            var state = new API.State();  // You would initialize this as necessary
            _teamsClient = new API.WebSocketClient(uri, state);
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
            //send output to log
            _teamsClient.MessageReceived += (_teamsClient, message) =>
            {
                Debug.WriteLine(message);
            };
        }

        #endregion Private Methods
    }
}