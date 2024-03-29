using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TEAMS2HA.API
{
    public class State
    {
        #region Private Fields

        private static readonly State _instance = new State();

        private string _activity = "";

        private string _blurred = "";
        private string _camera = "";
        private string? _handup;
        private string _issharing = "";
        private string _message = "";

        private string _microphone = "";
        private string? _recording;

        // Define properties for the different components of the state
        private string _status = "";

        private bool _teamsRunning = false;
        private bool canToggleMute = false;
        private bool canToggleVideo = false;
        private bool canToggleHand = false;
        private bool canToggleBlur = false;
        private bool canLeave = false;
        private bool canReact = false;
        private bool canToggleShareTray = false;
        private bool canToggleChat = false;
        private bool canStopSharing = false;
        private bool canPair = false;
        #endregion Private Fields

        #region Public Delegates

        // Define a delegate for the event handler
        public delegate void StateChangedEventHandler(object sender, EventArgs e);

        #endregion Public Delegates

        #region Public Events

        // Define the event that will be triggered when the state changes
        public event StateChangedEventHandler StateChanged;

        #endregion Public Events

        #region Public Properties

        public static State Instance
        {
            get { return _instance; }
        }

        public string Activity
        {
            get => _activity;
            set
            {
                if (_activity != value)
                {
                    _activity = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Blurred
        {
            get => _blurred;
            set
            {
                if (_blurred != value)
                {
                    _blurred = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Camera
        {
            get => _camera;
            set
            {
                if (_camera != value)
                {
                    _camera = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Handup
        {
            get => _handup;
            set
            {
                if (_handup != value)
                {
                    _handup = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string issharing
        {
            get => _issharing;
            set
            {
                if (_issharing != value)
                {
                    _issharing = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Message
        {
            get => _message;
            set => _message = value;
        }

        public string Microphone
        {
            get => _microphone;
            set
            {
                if (_microphone != value)
                {
                    _microphone = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Recording
        {
            get => _recording;
            set
            {
                if (_recording != value)
                {
                    _recording = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool teamsRunning { get; set; }
        public bool CanToggleMute { get; set; }
        public bool CanToggleVideo { get; set; }
        public bool CanToggleHand { get; set; }
        public bool CanToggleBlur { get; set; }
        public bool CanLeave { get; set; }
        public bool CanReact { get; set; }
        public bool CanToggleShareTray { get; set; }
        public bool CanToggleChat { get; set; }
        public bool CanStopSharing { get; set; }
        public bool CanPair { get; set; }

        #endregion Public Properties

        #region Public Methods

        public override string ToString()
        {
            return $"Status: {_status}, Activity: {_activity}";
        }

        #endregion Public Methods
    }
}