using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TEAMS2HA.API
{
    public class MeetingPermissions
    {
        #region Public Properties

        public bool CanLeave { get; set; }
        public bool CanPair { get; set; }
        public bool CanReact { get; set; }
        public bool CanStopSharing { get; set; }
        public bool CanToggleBlur { get; set; }
        public bool CanToggleChat { get; set; }
        public bool CanToggleHand { get; set; }
        public bool CanToggleMute { get; set; }
        public bool CanToggleShareTray { get; set; }
        public bool CanToggleVideo { get; set; }

        #endregion Public Properties
    }

    public class MeetingState
    {
        #region Public Properties

        public bool HasUnreadMessages { get; set; }
        public bool IsBackgroundBlurred { get; set; }
        public bool IsHandRaised { get; set; }
        public bool IsInMeeting { get; set; }
        public bool IsMuted { get; set; }
        public bool IsRecordingOn { get; set; }
        public bool IsSharing { get; set; }
        public bool IsVideoOn { get; set; }
        public bool teamsRunning { get; set; }

        #endregion Public Properties
    }

    public class MeetingUpdate
    {
        #region Public Properties

        public MeetingPermissions MeetingPermissions { get; set; }
        public MeetingState MeetingState { get; set; }

        #endregion Public Properties
    }
}