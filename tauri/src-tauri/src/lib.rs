mod app_state;
mod log_watcher;
mod migration;
mod mqtt_service;
mod process_watcher;
mod registry_monitor;
mod settings;
mod wasapi_monitor;

use app_state::{new_shared, SharedState};
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
use tokio::sync::{mpsc, RwLock};
use wasapi_monitor::WasapiEvent;

type MqttHandle = Arc<RwLock<Option<MqttService>>>;
type CmdTx = Arc<mpsc::Sender<MqttCommand>>;
type ReconnectTx = Arc<mpsc::Sender<()>>;

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
async fn save_settings(
    settings: Settings,
    mqtt: State<'_, MqttHandle>,
    cmd_tx: State<'_, CmdTx>,
    reconnect_tx: State<'_, ReconnectTx>,
    app: AppHandle,
) -> Result<(), String> {
    settings.save().map_err(|e| e.to_string())?;

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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    env_logger::init();

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_shell::init())
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

            // Initial MQTT connection (non-blocking — status emitted via ConnAck in eventloop)
            let settings = Settings::load();
            let run_minimized = settings.run_minimized;
            let mqtt_h2 = mqtt_handle.clone();
            let tx2: mpsc::Sender<MqttCommand> = (*cmd_tx).clone();
            let rtx2: mpsc::Sender<()> = (*reconnect_tx).clone();
            let handle2 = handle.clone();
            tauri::async_runtime::spawn(async move {
                if !settings.mqtt_address.is_empty() {
                    match MqttService::connect(&settings, tx2, rtx2, handle2.clone()).await {
                        Ok(svc) => {
                            *mqtt_h2.write().await = Some(svc);
                        }
                        Err(e) => {
                            log::warn!("Initial MQTT connect failed: {e}");
                            handle2.emit("mqtt-status", "Disconnected").ok();
                        }
                    }
                } else {
                    handle2.emit("mqtt-status", "Disconnected").ok();
                }
            });

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
                            publish(&mqtt_h3, &handle3, &shared2).await;
                        }
                    }
                }
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![get_settings, save_settings, get_state, get_mqtt_status])
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

async fn publish(mqtt: &MqttHandle, app: &AppHandle, shared: &SharedState) {
    let state = shared.read().await.meeting.clone();
    if let Some(svc) = mqtt.read().await.as_ref() {
        if let Err(e) = svc.publish_state(&state).await {
            log::warn!("Publish state error: {e}");
        }
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
            } else if s.meeting.presence != "Busy" && s.meeting.presence != "DoNotDisturb" {
                s.meeting.is_in_meeting = false;
                s.meeting.is_muted = false;
            }
        }
        LogEvent::PresenceChanged(p) => s.meeting.presence = p,
        LogEvent::UnreadMessages(u) => s.meeting.has_unread_messages = u,
    }
    drop(s);
    publish(mqtt, app, shared).await;
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
    publish(mqtt, app, shared).await;
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
                let presence = s.meeting.presence.clone();
                if presence != "Busy" && presence != "DoNotDisturb" {
                    s.meeting.is_in_meeting = false;
                }
            }
        }
    }
    drop(s);
    publish(mqtt, app, shared).await;
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
    publish(mqtt, app, shared).await;
}
