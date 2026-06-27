import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import Settings from "./components/Settings";
import StatusBar from "./components/StatusBar";
import "./App.css";

function App() {
  const [mqttStatus, setMqttStatus] = useState("Unknown");
  const [meetingState, setMeetingState] = useState(null);

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

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-left">
          <svg className="teams-icon" viewBox="0 0 24 24" fill="currentColor">
            <path d="M19.19 8.77c.33-.11.66-.17 1.01-.17 1.55 0 2.8 1.25 2.8 2.8s-1.25 2.8-2.8 2.8a2.8 2.8 0 0 1-1.01-.19v1.49a4.59 4.59 0 0 0 1.01.12 4.6 4.6 0 0 0 4.6-4.6 4.6 4.6 0 0 0-4.6-4.6c-.35 0-.69.04-1.01.12v1.23zM14.4 5.6a3.2 3.2 0 1 0 0 6.4 3.2 3.2 0 0 0 0-6.4zm0 5.2a2 2 0 1 1 0-4 2 2 0 0 1 0 4zM16 13.6h-3.2C10.7 13.6 9 15.3 9 17.4V20h9.6v-2.6c0-2.1-1.5-3.8-2.6-3.8z"/>
          </svg>
          <span className="app-title">Teams2HA</span>
        </div>
        <StatusBar mqttStatus={mqttStatus} meetingState={meetingState} />
      </header>

      <main className="app-main">
        <Settings />
      </main>
    </div>
  );
}

export default App;
