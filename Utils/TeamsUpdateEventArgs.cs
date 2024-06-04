using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TEAMS2HA.API;

namespace TEAMS2HA.Utils
{
    public class TeamsUpdateEventArgs : EventArgs
    {
        #region Public Properties

        public MeetingUpdate? MeetingUpdate { get; set; }

        #endregion Public Properties
    }
}