# Copilot Instructions for TEAMS2HA

## Architecture

This is a **Tauri 2 + Rust backend + React/Vite frontend** desktop app for Windows.
Source lives in `tauri/`. The root-level C# code is a legacy .NET WPF app kept for
reference — it is not built or shipped.

```
Teams Desktop App  <-- log file polling + WASAPI -->  Rust backend  <-- MQTT -->  Home Assistant
```

## Intentional design decisions — do not remove these

### MQTT switch entities keep `command_topic`
The MQTT discovery payloads publish switches with a `command_topic`. Incoming
commands are received but not forwarded to Teams (the Teams local API is
deprecated). This is intentional: the entities remain controllable switches in
Home Assistant so existing user automations don't break. Do not downgrade them
to binary sensors or remove `command_topic`.

### First-run migration (`migration.rs`)
On first launch the app deletes the old ClickOnce install's registry key from
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`. This helps users
upgrading from the old .NET version. Keep it.

### Run at Boot (`apply_run_at_boot` in `settings.rs`)
Writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` when the user
enables the setting. This is a deliberate user-facing feature, not a security
issue. Do not remove it without replacing it with an equivalent mechanism.

### `tauri-plugin-shell` is intentionally absent
The shell plugin was removed because it was registered but never used.
`tauri-plugin-opener` handles URL opening. Do not re-add `tauri-plugin-shell`.

## Key files

| File | Purpose |
|------|---------|
| `tauri/src-tauri/src/mqtt_service.rs` | MQTT client — discovery, state publish, inbound command stub |
| `tauri/src-tauri/src/log_watcher.rs` | Tails Teams log file for presence/meeting state |
| `tauri/src-tauri/src/wasapi_monitor.rs` | Detects mic/camera in use via Windows audio APIs |
| `tauri/src-tauri/src/settings.rs` | Persists settings to `%LocalAppData%\teams2ha\settings.json` |
| `tauri/src-tauri/src/migration.rs` | One-time ClickOnce removal on first run |
| `tauri/src/components/Settings.jsx` | Settings UI (React) |
| `tauri/src/components/StatusBar.jsx` | Header status bar with presence + connection indicators |

## Security notes

- The app is unsigned. Defender may flag registry writes in unsigned binaries.
  The correct fix is code signing, not removing features.
- `rustls-webpki` and `glib` Dependabot alerts are suppressed — both are
  transitive deps pinned by Tauri with no compatible patched version available.
