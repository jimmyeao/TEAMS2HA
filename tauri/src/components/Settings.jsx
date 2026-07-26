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
  homeGatewayMac: "",
};

export default function Settings() {
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [saving, setSaving] = useState(false);
  const [saveStatus, setSaveStatus] = useState(null);
  const [showPassword, setShowPassword] = useState(false);

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

  const useCurrentNetwork = async () => {
    try {
      const mac = await invoke("get_current_gateway_mac");
      if (mac) {
        set("homeGatewayMac", mac);
      } else {
        setSaveStatus("error: could not detect the current gateway MAC");
        setTimeout(() => setSaveStatus(null), 4000);
      }
    } catch (err) {
      setSaveStatus("error: " + err);
      setTimeout(() => setSaveStatus(null), 4000);
    }
  };

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
      <div className="settings-grid">

        {/* ---- Broker ---- */}
        <section className="card">
          <header className="card-head">
            <h2 className="card-title">MQTT Broker</h2>
            <p className="card-hint">Where your Home Assistant broker lives.</p>
          </header>

          <div className="field-row">
            <div className="field flex-grow">
              <label htmlFor="mqtt-host">Host address</label>
              <input
                id="mqtt-host"
                type="text"
                value={settings.mqttAddress}
                onChange={(e) => set("mqttAddress", e.target.value)}
                placeholder="192.168.1.10"
                spellCheck={false}
                autoComplete="off"
              />
            </div>
            <div className="field field-narrow">
              <label htmlFor="mqtt-port">Port</label>
              <input
                id="mqtt-port"
                type="number"
                value={settings.mqttPort}
                onChange={(e) => set("mqttPort", parseInt(e.target.value) || 1883)}
                min={1}
                max={65535}
              />
            </div>
          </div>

          <div className="field">
            <label htmlFor="mqtt-user">Username</label>
            <input
              id="mqtt-user"
              type="text"
              value={settings.mqttUsername}
              onChange={(e) => set("mqttUsername", e.target.value)}
              spellCheck={false}
              autoComplete="off"
            />
          </div>

          <div className="field">
            <label htmlFor="mqtt-pass">Password</label>
            <div className="input-affix">
              <input
                id="mqtt-pass"
                type={showPassword ? "text" : "password"}
                value={settings.mqttPassword}
                onChange={(e) => set("mqttPassword", e.target.value)}
                autoComplete="off"
              />
              <button
                type="button"
                className="affix-btn"
                onClick={() => setShowPassword((v) => !v)}
                aria-label={showPassword ? "Hide password" : "Show password"}
              >
                {showPassword ? "Hide" : "Show"}
              </button>
            </div>
          </div>

          <div className="field-group">
            <span className="group-label">Transport</span>
            <div className="chip-row">
              <Chip
                label="TLS"
                checked={settings.useTls}
                onChange={(v) => set("useTls", v)}
              />
              <Chip
                label="WebSockets"
                checked={settings.useWebsockets}
                onChange={(v) => set("useWebsockets", v)}
              />
              <Chip
                label="Ignore cert errors"
                checked={settings.useTls && settings.ignoreCertErrors}
                disabled={!settings.useTls}
                title={
                  settings.useTls
                    ? "Accept self-signed or mismatched certificates"
                    : "Only applies when TLS is enabled"
                }
                onChange={(v) => set("ignoreCertErrors", v)}
              />
            </div>
          </div>
        </section>

        {/* ---- Options ---- */}
        <section className="card">
          <header className="card-head">
            <h2 className="card-title">Options</h2>
            <p className="card-hint">Entity naming and Windows startup behaviour.</p>
          </header>

          <div className="field">
            <label htmlFor="prefix">Sensor prefix</label>
            <input
              id="prefix"
              type="text"
              value={settings.sensorPrefix}
              onChange={(e) => set("sensorPrefix", e.target.value)}
              placeholder="Your machine name"
              spellCheck={false}
              autoComplete="off"
            />
            <span className="field-hint">
              Entities appear as <code>sensor.{(settings.sensorPrefix || "prefix").toLowerCase()}_teamsstatus</code>
            </span>
          </div>

          <Switch
            label="Run at boot"
            hint="Start Teams2HA when you sign in to Windows"
            checked={settings.runAtBoot}
            onChange={(v) => set("runAtBoot", v)}
          />
          <Switch
            label="Start minimised"
            hint="Launch straight to the system tray"
            checked={settings.runMinimized}
            onChange={(v) => set("runMinimized", v)}
          />
          <Switch
            label="Dark theme"
            hint="Applies immediately"
            checked={settings.theme === "dark"}
            onChange={(v) => set("theme", v ? "dark" : "light")}
          />
        </section>

        {/* ---- Home detection ---- */}
        <section className="card card-wide">
          <header className="card-head">
            <h2 className="card-title">Home Detection</h2>
            <p className="card-hint">
              Only connect to MQTT while on your home network, matched by the default
              gateway&apos;s MAC address. Leave empty to always connect. Away from home,
              all entities show as unavailable in Home Assistant.
            </p>
          </header>

          <div className="field-row">
            <div className="field flex-grow">
              <label htmlFor="gateway-mac">Home gateway MAC</label>
              <input
                id="gateway-mac"
                type="text"
                value={settings.homeGatewayMac}
                onChange={(e) => set("homeGatewayMac", e.target.value)}
                placeholder="AA:BB:CC:DD:EE:FF — or comma-separated for several"
                spellCheck={false}
                autoComplete="off"
              />
            </div>
            <button type="button" className="btn-secondary" onClick={useCurrentNetwork}>
              Use current network
            </button>
          </div>
        </section>

      </div>

      {/* ---- Sticky actions ---- */}
      <div className="action-bar">
        {saveStatus && (
          <span className={`save-status ${saveStatus === "saved" ? "ok" : "err"}`}>
            {saveStatus === "saved" ? "✓ Saved" : saveStatus}
          </span>
        )}
        <button type="submit" className="btn-primary" disabled={saving}>
          {saving ? "Saving…" : "Save settings"}
        </button>
      </div>
    </form>
  );
}

function Chip({ label, checked, onChange, disabled, title }) {
  return (
    <label
      className={`chip ${checked ? "chip-active" : ""} ${disabled ? "chip-disabled" : ""}`}
      title={title}
    >
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        hidden
      />
      {label}
    </label>
  );
}

function Switch({ label, hint, checked, onChange }) {
  return (
    <label className="switch-row">
      <span className="switch-text">
        <span className="switch-label">{label}</span>
        {hint && <span className="switch-hint">{hint}</span>}
      </span>
      <input
        type="checkbox"
        className="switch-input"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="switch-track" />
    </label>
  );
}
