export default function StatusBar({ mqttStatus, meetingState }) {
  const connected = mqttStatus === "Connected";
  const presence = meetingState?.presence || "";

  return (
    <div className="status-bar">
      <div className={`status-indicator ${connected ? "connected" : "disconnected"}`}>
        <span className="status-dot" />
        <span className="status-label">MQTT: {mqttStatus}</span>
      </div>
      {meetingState && (
        <div className={`status-indicator ${meetingState.isInMeeting ? "in-meeting" : ""}`}>
          <span className="status-dot" />
          <span className="status-label">
            {meetingState.isInMeeting ? "In Meeting" : "Not in Meeting"}
          </span>
        </div>
      )}
      {presence && (
        <div className={`status-indicator presence-${presence.toLowerCase()}`}>
          <span className="status-dot" />
          <span className="status-label">{presence}</span>
        </div>
      )}
    </div>
  );
}
