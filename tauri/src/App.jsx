import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { openUrl } from "@tauri-apps/plugin-opener";
import Settings from "./components/Settings";
import StatusBar from "./components/StatusBar";
import appIcon from "./assets/teams2ha.png";
import "./App.css";

const FULL_DISK_ACCESS_URL =
  "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles";

function App() {
  const [mqttStatus, setMqttStatus] = useState("Unknown");
  const [meetingState, setMeetingState] = useState(null);
  const [version, setVersion] = useState("");
  const [logAccessDenied, setLogAccessDenied] = useState(false);

  useEffect(() => {
    invoke("get_app_version").then(setVersion).catch(console.error);
  }, []);

  useEffect(() => {
    // Listen for backend events
    const unlistenMqtt = listen("mqtt-status", (ev) => setMqttStatus(ev.payload));
    const unlistenState = listen("state-update", (ev) => setMeetingState(ev.payload));

    const unlistenAccess = listen("log-access-denied", (ev) => setLogAccessDenied(ev.payload));

    // Poll current status immediately — events may have fired before listeners were ready
    invoke("get_mqtt_status").then(setMqttStatus).catch(console.error);
    invoke("get_state").then(setMeetingState).catch(console.error);
    invoke("get_log_access_denied").then(setLogAccessDenied).catch(console.error);

    return () => {
      unlistenMqtt.then((f) => f());
      unlistenState.then((f) => f());
      unlistenAccess.then((f) => f());
    };
  }, []);

  // "Connected" / "Disconnected" / "Paused (not home)" → a tone the pill can colour by.
  const mqttTone = mqttStatus.startsWith("Connected")
    ? "ok"
    : mqttStatus.startsWith("Paused")
      ? "warn"
      : "err";

  return (
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <img className="brand-icon" src={appIcon} alt="" width="32" height="32" />
          <div className="brand-text">
            <h1 className="brand-title">
              Teams2HA
              {version && <span className="brand-version">v{version}</span>}
            </h1>
            <p className="brand-sub">Teams presence → Home Assistant</p>
          </div>
        </div>

        <div className={`conn-pill conn-${mqttTone}`}>
          <span className="conn-dot" />
          <span className="conn-label">{mqttStatus}</span>
        </div>
      </header>

      {logAccessDenied && (
        <div className="access-banner" role="alert">
          <strong>Teams2HA can't read Teams' logs.</strong>
          <ol>
            <li>Click <strong>Open System Settings</strong> below.</li>
            <li>
              Under Full Disk Access, click <strong>+</strong> and choose Teams2HA from
              Applications. If it's already listed, turn it on.
            </li>
            <li>Quit and reopen Teams2HA.</li>
          </ol>
          <button
            type="button"
            className="btn-secondary"
            onClick={() => openUrl(FULL_DISK_ACCESS_URL).catch(console.error)}
          >
            Open System Settings
          </button>
        </div>
      )}

      <StatusBar meetingState={meetingState} />

      <main className="app-main">
        <Settings />
      </main>
    </div>
  );
}

export default App;
