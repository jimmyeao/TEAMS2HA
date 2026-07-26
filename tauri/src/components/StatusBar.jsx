// Live state strip. The backend tracks every field of MeetingState, so surface them all
// here — this is the only place to confirm at a glance what is actually being published
// to Home Assistant.

const PRESENCE_TONE = {
  available: "ok",
  busy: "err",
  donotdisturb: "err",
  away: "warn",
  berightback: "warn",
  offline: "idle",
  unknown: "idle",
};

function Tile({ label, value, tone }) {
  return (
    <div className={`tile tile-${tone}`}>
      <span className="tile-dot" />
      <span className="tile-body">
        <span className="tile-label">{label}</span>
        <span className="tile-value">{value}</span>
      </span>
    </div>
  );
}

export default function StatusBar({ meetingState }) {
  if (!meetingState) {
    return (
      <div className="state-strip state-strip-empty">
        <span className="strip-waiting">Waiting for Teams state…</span>
      </div>
    );
  }

  const {
    isInMeeting,
    isMuted,
    isVideoOn,
    hasUnreadMessages,
    teamsRunning,
    presence,
  } = meetingState;

  const presenceKey = (presence || "unknown").toLowerCase();

  return (
    <div className="state-strip">
      <Tile
        label="Teams"
        value={teamsRunning ? "Running" : "Not running"}
        tone={teamsRunning ? "ok" : "idle"}
      />
      <Tile
        label="Presence"
        value={presence || "Unknown"}
        tone={PRESENCE_TONE[presenceKey] ?? "idle"}
      />
      <Tile
        label="Meeting"
        value={isInMeeting ? "In a meeting" : "Not in a meeting"}
        tone={isInMeeting ? "warn" : "idle"}
      />
      <Tile
        label="Mic"
        value={isMuted ? "Muted" : "Unmuted"}
        tone={isMuted ? "err" : "ok"}
      />
      <Tile
        label="Camera"
        value={isVideoOn ? "On" : "Off"}
        tone={isVideoOn ? "ok" : "idle"}
      />
      <Tile
        label="Messages"
        value={hasUnreadMessages ? "Unread" : "None"}
        tone={hasUnreadMessages ? "warn" : "idle"}
      />
    </div>
  );
}
