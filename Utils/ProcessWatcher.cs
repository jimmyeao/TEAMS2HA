using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TEAMS2HA.Utils
{
    public class ProcessWatcher
    {
        public event Action<bool> TeamsRunningChanged;
        private Timer _timer;
        private bool _isTeamsRunning;
        private const int CheckInterval = 5000; // 5 seconds

        public ProcessWatcher()
        {
            _timer = new Timer(CheckProcess, null, 0, CheckInterval);
        }

        private void CheckProcess(object state)
        {
            bool currentlyRunning = IsTeamsRunning();
            if (currentlyRunning != _isTeamsRunning)
            {
                _isTeamsRunning = currentlyRunning;
                TeamsRunningChanged?.Invoke(_isTeamsRunning);
            }
        }

        private bool IsTeamsRunning()
        {
            return Process.GetProcessesByName("ms-teams").Length > 0;
        }
    }

}
