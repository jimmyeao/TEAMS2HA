import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";

const DEFAULT_SETTINGS = {
  mqttAddress: "",
  mqttPort: 1883,
  mqttUsername: "",
  mqttPassword: "",
  sensorPrefix: "",
  useTls: false,
  ignoreCertErrors: false,
  useWebsockets: false,
  runAtBoot: false,
  runMinimized: false,
  theme: "dark",
  colorScheme: "DeepPurple / Lime",
};

export default function Settings() {
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [saving, setSaving] = useState(false);
  const [saveStatus, setSaveStatus] = useState(null);

  useEffect(() => {
    invoke("get_settings")
      .then((s) => {
        setSettings(s);
        document.documentElement.setAttribute("data-theme", s.theme ?? "dark");
      })
      .catch((e) => console.error("load settings:", e));
  }, []);

  // Apply theme immediately on toggle (before save)
  useEffect(() => {
    document.documentElement.setAttribute("data-theme", settings.theme);
  }, [settings.theme]);

  const set = (key, value) => setSettings((s) => ({ ...s, [key]: value }));

  const handleSave = async (e) => {
    e.preventDefault();
    setSaving(true);
    setSaveStatus(null);
    try {
      await invoke("save_settings", { settings });
      setSaveStatus("saved");
    } catch (err) {
      setSaveStatus("error: " + err);
    } finally {
      setSaving(false);
      setTimeout(() => setSaveStatus(null), 3000);
    }
  };

  return (
    <form className="settings-form" onSubmit={handleSave}>

      {/* MQTT Configuration */}
      <section className="card">
        <h2 className="card-title">MQTT Configuration</h2>

        <div className="field-row">
          <div className="field flex-grow">
            <label>Host Address</label>
            <input
              type="text"
              value={settings.mqttAddress}
              onChange={(e) => set("mqttAddress", e.target.value)}
              placeholder="e.g. 192.168.1.10"
            />
          </div>
          <div className="field field-narrow">
            <label>Port</label>
            <input
              type="number"
              value={settings.mqttPort}
              onChange={(e) => set("mqttPort", parseInt(e.target.value) || 1883)}
              min={1}
              max={65535}
            />
          </div>
        </div>

        <div className="field">
          <label>Username</label>
          <input
            type="text"
            value={settings.mqttUsername}
            onChange={(e) => set("mqttUsername", e.target.value)}
          />
        </div>

        <div className="field">
          <label>Password</label>
          <input
            type="password"
            value={settings.mqttPassword}
            onChange={(e) => set("mqttPassword", e.target.value)}
          />
        </div>

        <div className="chip-row">
          <Chip
            label="TLS"
            checked={settings.useTls}
            onChange={(v) => set("useTls", v)}
          />
          <Chip
            label="Ignore Cert Errors"
            checked={settings.ignoreCertErrors}
            onChange={(v) => set("ignoreCertErrors", v)}
          />
          <Chip
            label="WebSockets"
            checked={settings.useWebsockets}
            onChange={(v) => set("useWebsockets", v)}
          />
        </div>
      </section>

      {/* Options */}
      <section className="card">
        <h2 className="card-title">Options</h2>

        <div className="field">
          <label>Sensor Prefix</label>
          <input
            type="text"
            value={settings.sensorPrefix}
            onChange={(e) => set("sensorPrefix", e.target.value)}
            placeholder="Your machine name"
          />
        </div>

        <div className="chip-row">
          <Chip
            label="Run at Boot"
            checked={settings.runAtBoot}
            onChange={(v) => set("runAtBoot", v)}
          />
          <Chip
            label="Start Minimised"
            checked={settings.runMinimized}
            onChange={(v) => set("runMinimized", v)}
          />
        </div>

        <div className="theme-row">
          <label className="toggle-label">
            ☀
            <input
              type="checkbox"
              className="toggle"
              checked={settings.theme === "dark"}
              onChange={(e) => set("theme", e.target.checked ? "dark" : "light")}
            />
            <span className="toggle-track" />
            🌙
          </label>
        </div>
      </section>

      {/* Actions */}
      <div className="action-row">
        {saveStatus && (
          <span className={`save-status ${saveStatus === "saved" ? "ok" : "err"}`}>
            {saveStatus === "saved" ? "✓ Saved" : saveStatus}
          </span>
        )}
        <button type="submit" className="btn-primary" disabled={saving}>
          {saving ? "Saving…" : "Save Settings"}
        </button>
      </div>

    </form>
  );
}

function Chip({ label, checked, onChange }) {
  return (
    <label className={`chip ${checked ? "chip-active" : ""}`}>
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        hidden
      />
      {label}
    </label>
  );
}
