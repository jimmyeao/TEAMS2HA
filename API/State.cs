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

        #endregion Private Fields



        #region Private Fields

        private string _activity = "";

        private string _blurred = "";
        private string _camera = "";
        private string _issharing = "";

        private string _handup;
        private string _message = "";

        private string _microphone = "";
        private string _recording;

        // Define properties for the different components of the state
        private string _status = "";

        #endregion Private Fields

        #region Public Delegates

        // Define a delegate for the event handler
        public delegate void StateChangedEventHandler(object sender, EventArgs e);

        #endregion Public Delegates

        #region Public Events

        // Define the event that will be triggered when the state changes
        public event StateChangedEventHandler StateChanged;

        public static State Instance
        {
            get { return _instance; }
        }

        #endregion Public Events

        #region Public Properties

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
        public bool teamsRunning { get; set; }

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

        public override string ToString()
        {
            return $"Status: {_status}, Activity: {_activity}";
        }

        #endregion Public Properties
    }
}
