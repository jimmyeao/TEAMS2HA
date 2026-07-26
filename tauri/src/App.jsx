import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import Settings from "./components/Settings";
import StatusBar from "./components/StatusBar";
import appIcon from "./assets/teams2ha.png";
import "./App.css";

function App() {
  const [mqttStatus, setMqttStatus] = useState("Unknown");
  const [meetingState, setMeetingState] = useState(null);
  const [version, setVersion] = useState("");

  useEffect(() => {
    invoke("get_app_version").then(setVersion).catch(console.error);
  }, []);

  useEffect(() => {
    // Listen for backend events
    const unlistenMqtt = listen("mqtt-status", (ev) => setMqttStatus(ev.payload));
    const unlistenState = listen("state-update", (ev) => setMeetingState(ev.payload));

    // Poll current status immediately — events may have fired before listeners were ready
    invoke("get_mqtt_status").then(setMqttStatus).catch(console.error);
    invoke("get_state").then(setMeetingState).catch(console.error);

    return () => {
      unlistenMqtt.then((f) => f());
      unlistenState.then((f) => f());
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

      <StatusBar meetingState={meetingState} />

      <main className="app-main">
        <Settings />
      </main>
    </div>
  );
}

export default App;
