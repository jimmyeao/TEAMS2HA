mod app_state;
mod home_network;
mod log_watcher;
mod migration;
mod mqtt_service;
mod process_watcher;
mod registry_monitor;
mod settings;
mod wasapi_monitor;

use app_state::{new_shared, SharedState};
use home_network::HomeEvent;
use log_watcher::LogEvent;
use mqtt_service::{MeetingState, MqttCommand, MqttService};
use process_watcher::ProcessEvent;
use registry_monitor::RegistryEvent;
use settings::Settings;
use std::sync::Arc;
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Emitter, Manager, State,
};
use tokio::sync::{mpsc, watch, RwLock};
use wasapi_monitor::WasapiEvent;

type MqttHandle = Arc<RwLock<Option<MqttService>>>;
type CmdTx = Arc<mpsc::Sender<MqttCommand>>;
type ReconnectTx = Arc<mpsc::Sender<()>>;
type HomeMacTx = Arc<watch::Sender<String>>;

#[tauri::command]
async fn get_settings() -> Result<Settings, String> {
    Ok(Settings::load())
}

#[tauri::command]
async fn get_mqtt_status(mqtt: State<'_, MqttHandle>) -> Result<String, String> {
    Ok(if mqtt.read().await.is_some() {
        "Connected".into()
    } else {
        "Disconnected".into()
    })
}

#[tauri::command]
async fn get_current_gateway_mac() -> Result<Option<String>, String> {
    tokio::task::spawn_blocking(home_network::current_gateway_mac)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn save_settings(
    settings: Settings,
    mqtt: State<'_, MqttHandle>,
    cmd_tx: State<'_, CmdTx>,
    reconnect_tx: State<'_, ReconnectTx>,
    home_mac_tx: State<'_, HomeMacTx>,
    app: AppHandle,
) -> Result<(), String> {
    settings.save().map_err(|e| e.to_string())?;

    // Hand the (possibly changed) home MAC to the poller so it re-evaluates immediately.
    let _ = home_mac_tx.send(settings.home_gateway_mac.clone());

    // Home gating: while away, keep MQTT paused. The poller reconnects on arrival.
    // SendARP can block up to ~1s, so run it off the async executor.
    let mac = settings.home_gateway_mac.clone();
    let is_home = tokio::task::spawn_blocking(move || home_network::is_home(&mac))
        .await
        .unwrap_or(true);
    if !is_home {
        log::info!("Settings saved; not on the home network - MQTT stays paused.");
        *mqtt.write().await = None;
        app.emit("mqtt-status", "Paused (not home)").ok();
        return Ok(());
    }

    let tx: mpsc::Sender<MqttCommand> = (**cmd_tx).clone();
    let rtx: mpsc::Sender<()> = (**reconnect_tx).clone();
    match MqttService::connect(&settings, tx, rtx, app.clone()).await {
        Ok(svc) => {
            *mqtt.write().await = Some(svc);
            // "Connected" + state re-publish triggered by ConnAck in the eventloop
        }
        Err(e) => {
            log::error!("MQTT reconnect failed: {e}");
            *mqtt.write().await = None;
            app.emit("mqtt-status", "Disconnected").ok();
        }
    }

    Ok(())
}

#[tauri::command]
async fn get_state(shared: State<'_, SharedState>) -> Result<MeetingState, String> {
    Ok(shared.read().await.meeting.clone())
}

/// Log to a file next to settings.json (a tray app has no visible stderr), default
/// level `info` — env_logger's default of errors-only left us blind on 13-07-2026
/// when the MQTT connection silently never came back after a Modern Standby resume.
/// RUST_LOG still overrides the level.
fn init_logging() {
    use env_logger::{Builder, Env, Target};
    let mut builder = Builder::from_env(Env::default().default_filter_or("info"));
    if let Some(dir) = settings::data_dir() {
        let path = dir.join("teams2ha.log");
        let _ = std::fs::create_dir_all(&dir);
        // Simple size cap: start over once the file exceeds 5 MB.
        if std::fs::metadata(&path)
            .map(|m| m.len() > 5 * 1024 * 1024)
            .unwrap_or(false)
        {
            let _ = std::fs::remove_file(&path);
        }
        if let Ok(file) = std::fs::OpenOptions::new().create(true).append(true).open(&path) {
            builder.target(Target::Pipe(Box::new(file)));
        }
    }
    builder.init();
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    init_logging();

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            // First-run migration: silently remove old ClickOnce install entry.
            if settings::is_first_run() {
                migration::remove_old_clickonce();
            }

            let handle = app.handle().clone();

            // System tray (only created here — no declarative trayIcon in tauri.conf.json)
            let show = MenuItem::with_id(app, "show", "Show / Hide", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show, &quit])?;

            let _tray = TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .menu(&menu)
                .tooltip("Teams2HA")
                .on_menu_event(move |app, event| match event.id().as_ref() {
                    "show" => toggle_window(app),
                    "quit" => app.exit(0),
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        toggle_window(tray.app_handle());
                    }
                })
                .build(app)?;

            // Shared state
            let shared = new_shared();
            let mqtt_handle: MqttHandle = Arc::new(RwLock::new(None));

            // Persistent channels — shared across initial connect and all reconnects.
            let (cmd_tx, mut cmd_rx) = mpsc::channel::<MqttCommand>(16);
            let (reconnect_tx, mut reconnect_rx) = mpsc::channel::<()>(4);
            let cmd_tx = Arc::new(cmd_tx);
            let reconnect_tx = Arc::new(reconnect_tx);

            app.manage(shared.clone());
            app.manage(mqtt_handle.clone());
            app.manage(cmd_tx.clone());
            app.manage(reconnect_tx.clone());

            // Monitor channels
            let (log_tx, mut log_rx) = mpsc::channel::<LogEvent>(64);
            let (wasapi_tx, mut wasapi_rx) = mpsc::channel::<WasapiEvent>(64);
            let (reg_tx, mut reg_rx) = mpsc::channel::<RegistryEvent>(64);
            let (proc_tx, mut proc_rx) = mpsc::channel::<ProcessEvent>(64);

            // Start OS monitors
            log_watcher::start(log_tx);
            wasapi_monitor::start(wasapi_tx);
            tauri::async_runtime::spawn(async move { registry_monitor::start(reg_tx).await });
            tauri::async_runtime::spawn(async move { process_watcher::start(proc_tx).await });

            // Home-network monitor. Its first emission (home/away) drives the initial MQTT
            // connection via the central event loop; later transitions connect/pause MQTT.
            // The configured MAC flows in via a watch channel (updated on settings save).
            let settings = Settings::load();
            let run_minimized = settings.run_minimized;
            let (home_tx, mut home_rx) = mpsc::channel::<HomeEvent>(4);
            let (home_mac_tx, home_mac_rx) = watch::channel(settings.home_gateway_mac.clone());
            let home_mac_tx: HomeMacTx = Arc::new(home_mac_tx);
            app.manage(home_mac_tx);
            home_network::start(home_tx, home_mac_rx);

            // Window visibility
            if run_minimized {
                if let Some(w) = handle.get_webview_window("main") {
                    w.hide().ok();
                }
            } else if let Some(w) = handle.get_webview_window("main") {
                w.show().ok();
            }

            // Central event loop — receives from all monitors + MQTT commands
            let shared2 = shared.clone();
            let mqtt_h3 = mqtt_handle.clone();
            let handle3 = handle.clone();
            let cmd_tx3 = cmd_tx.clone();
            let reconnect_tx3 = reconnect_tx.clone();
            tauri::async_runtime::spawn(async move {
                loop {
                    tokio::select! {
                        Some(ev) = log_rx.recv() => {
                            handle_log_event(ev, &shared2, &mqtt_h3, &handle3).await;
                        }
                        Some(ev) = wasapi_rx.recv() => {
                            handle_wasapi_event(ev, &shared2, &mqtt_h3, &handle3).await;
                        }
                        Some(ev) = reg_rx.recv() => {
                            handle_registry_event(ev, &shared2, &mqtt_h3, &handle3).await;
                        }
                        Some(ev) = proc_rx.recv() => {
                            handle_process_event(ev, &shared2, &mqtt_h3, &handle3).await;
                        }
                        Some(_cmd) = cmd_rx.recv() => {
                            log::info!("MQTT command received (no Teams API to forward to)");
                        }
                        Some(()) = reconnect_rx.recv() => {
                            // ConnAck received — push current state so HA sensors
                            // get real values immediately rather than waiting for a change.
                            publish(&mqtt_h3, &handle3, &shared2, true).await;
                        }
                        Some(ev) = home_rx.recv() => {
                            handle_home_event(ev, &mqtt_h3, &handle3, &cmd_tx3, &reconnect_tx3).await;
                        }
                    }
                }
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![get_settings, save_settings, get_state, get_mqtt_status, get_current_gateway_mac])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

fn toggle_window(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        if w.is_visible().unwrap_or(false) {
            w.hide().ok();
        } else {
            w.show().ok();
            w.set_focus().ok();
        }
    }
}

async fn handle_home_event(
    ev: HomeEvent,
    mqtt: &MqttHandle,
    app: &AppHandle,
    cmd_tx: &CmdTx,
    reconnect_tx: &ReconnectTx,
) {
    match ev {
        HomeEvent::Changed(is_home) => {
            handle_home_change(is_home, mqtt, app, cmd_tx, reconnect_tx).await;
        }
        HomeEvent::Resumed(is_home) => {
            // The pre-suspend session can be a silently dead TCP connection that the
            // eventloop never errors out on. Drop it unconditionally (the broker then
            // publishes the Last Will) and, when still home, connect from scratch —
            // ConnAck re-publishes availability, discovery and the current state.
            log::info!("Resume from suspend - rebuilding MQTT connection (home={is_home})");
            *mqtt.write().await = None;
            if is_home {
                handle_home_change(true, mqtt, app, cmd_tx, reconnect_tx).await;
            } else {
                app.emit("mqtt-status", "Paused (not home)").ok();
            }
        }
    }
}

async fn handle_home_change(
    is_home: bool,
    mqtt: &MqttHandle,
    app: &AppHandle,
    cmd_tx: &CmdTx,
    reconnect_tx: &ReconnectTx,
) {
    if is_home {
        if mqtt.read().await.is_some() {
            return;
        }
        let settings = Settings::load();
        if settings.mqtt_address.is_empty() {
            app.emit("mqtt-status", "Disconnected").ok();
            return;
        }
        log::info!("Home network detected - connecting to MQTT");
        let tx: mpsc::Sender<MqttCommand> = (**cmd_tx).clone();
        let rtx: mpsc::Sender<()> = (**reconnect_tx).clone();
        match MqttService::connect(&settings, tx, rtx, app.clone()).await {
            Ok(svc) => {
                *mqtt.write().await = Some(svc);
            }
            Err(e) => {
                log::warn!("MQTT connect on arriving home failed: {e}");
                app.emit("mqtt-status", "Disconnected").ok();
            }
        }
    } else {
        log::info!("Left the home network - pausing MQTT");
        // Dropping the service closes the TCP connection without a DISCONNECT packet, so the
        // broker publishes the Last Will ('offline') and HA marks all entities unavailable.
        *mqtt.write().await = None;
        app.emit("mqtt-status", "Paused (not home)").ok();
    }
}

/// Publish the current state to MQTT and the UI. With `force` false the publish is
/// skipped when nothing changed since the last successful publish — monitor events
/// fire far more often than the state actually changes. The ConnAck path passes
/// `force` true so a fresh connection always gets a full state push.
async fn publish(mqtt: &MqttHandle, app: &AppHandle, shared: &SharedState, force: bool) {
    let state = {
        let s = shared.read().await;
        if !force && s.last_published.as_ref() == Some(&s.meeting) {
            return;
        }
        s.meeting.clone()
    };
    let mut delivered = false;
    if let Some(svc) = mqtt.read().await.as_ref() {
        match svc.publish_state(&state).await {
            Ok(()) => delivered = true,
            Err(e) => log::warn!("Publish state error: {e}"),
        }
    }
    if delivered {
        // Only cache after success: a failed/skipped delivery (e.g. MQTT paused)
        // must be retried on the next event or the ConnAck re-publish.
        shared.write().await.last_published = Some(state.clone());
    }
    app.emit("state-update", &state).ok();
}

async fn handle_log_event(ev: LogEvent, shared: &SharedState, mqtt: &MqttHandle, app: &AppHandle) {
    let mut s = shared.write().await;
    match ev {
        LogEvent::MuteChanged(m) => s.meeting.is_muted = m,
        LogEvent::MeetingChanged(active) => {
            s.log_watcher_in_call = active;
            if active {
                s.meeting.is_in_meeting = true;
            } else {
                // Presence must NOT gate call-end (see handle_registry_event):
                // Teams holds presence at "Busy" during/after calls.
                s.meeting.is_in_meeting = false;
                s.meeting.is_muted = false;
            }
        }
        LogEvent::PresenceChanged(p) => s.meeting.presence = p,
        LogEvent::UnreadMessages(u) => s.meeting.has_unread_messages = u,
    }
    drop(s);
    publish(mqtt, app, shared, false).await;
}

async fn handle_wasapi_event(
    ev: WasapiEvent,
    shared: &SharedState,
    mqtt: &MqttHandle,
    app: &AppHandle,
) {
    let WasapiEvent::MuteChanged(muted) = ev;
    let mut s = shared.write().await;
    if s.meeting.is_in_meeting {
        s.meeting.is_muted = muted;
    }
    drop(s);
    publish(mqtt, app, shared, false).await;
}

async fn handle_registry_event(
    ev: RegistryEvent,
    shared: &SharedState,
    mqtt: &MqttHandle,
    app: &AppHandle,
) {
    let mut s = shared.write().await;
    match ev {
        RegistryEvent::CameraChanged(active) => s.meeting.is_video_on = active,
        RegistryEvent::MicChanged(active) => {
            if active && !s.meeting.is_in_meeting {
                s.meeting.is_in_meeting = true;
            } else if !active && !s.log_watcher_in_call {
                // Mic released and the log watcher isn't holding a call open →
                // the call has ended. Presence must NOT gate this: Teams keeps
                // presence at "Busy"/"DoNotDisturb" during and after a call, so
                // guarding on it left is_in_meeting stuck on forever.
                s.meeting.is_in_meeting = false;
                s.meeting.is_muted = false;
            }
        }
    }
    drop(s);
    publish(mqtt, app, shared, false).await;
}

async fn handle_process_event(
    ev: ProcessEvent,
    shared: &SharedState,
    mqtt: &MqttHandle,
    app: &AppHandle,
) {
    let ProcessEvent::TeamsRunningChanged(running) = ev;
    let mut s = shared.write().await;
    s.meeting.teams_running = running;
    drop(s);
    publish(mqtt, app, shared, false).await;
}
