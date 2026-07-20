use crate::settings::Settings;
use anyhow::Result;
use rumqttc::{AsyncClient, Event, LastWill, MqttOptions, Packet, QoS, TlsConfiguration, Transport};
use serde_json::json;
use std::time::Duration;
use tauri::{AppHandle, Emitter};
use tokio::sync::{mpsc, watch};

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MeetingState {
    pub is_muted: bool,
    pub is_video_on: bool,
    pub is_in_meeting: bool,
    pub has_unread_messages: bool,
    pub teams_running: bool,
    pub presence: String,
}

impl Default for MeetingState {
    fn default() -> Self {
        Self {
            is_muted: false,
            is_video_on: false,
            is_in_meeting: false,
            has_unread_messages: false,
            teams_running: false,
            // "Unknown" instead of empty: the first post-connect state publish then
            // overwrites a stale retained presence on the broker (e.g. 'Busy' from
            // before a crash) instead of skipping the topic and leaving it stand.
            presence: "Unknown".into(),
        }
    }
}

#[derive(Debug, Clone)]
pub enum MqttCommand {
    ToggleMute,
    ToggleVideo,
}

pub struct MqttService {
    client: AsyncClient,
    prefix: String,
    // Dropping this signals the eventloop to stop.
    _stop_tx: watch::Sender<bool>,
}

impl MqttService {
    pub async fn connect(
        settings: &Settings,
        cmd_tx: mpsc::Sender<MqttCommand>,
        reconnect_tx: mpsc::Sender<()>,
        app: AppHandle,
    ) -> Result<Self> {
        let prefix = settings.sensor_prefix.to_lowercase();
        let port = settings.mqtt_port;

        let mut opts = MqttOptions::new(
            format!("teams2ha-{}", hostname::get()?.to_string_lossy()),
            &settings.mqtt_address,
            port,
        );
        opts.set_keep_alive(Duration::from_secs(30));
        opts.set_clean_session(true);

        // Last Will: whenever the connection dies without a clean DISCONNECT (crash, sleep,
        // leaving the network, or the app dropping the service on purpose), the broker marks
        // all entities unavailable in HA — instead of leaving stale retained states behind
        // (e.g. is_in_meeting stuck 'on' after closing the laptop mid-call).
        opts.set_last_will(LastWill::new(
            availability_topic(&prefix),
            "offline",
            QoS::AtLeastOnce,
            true,
        ));

        if !settings.mqtt_username.is_empty() {
            opts.set_credentials(&settings.mqtt_username, &settings.mqtt_password);
        }

        if settings.use_tls && !settings.ignore_cert_errors {
            opts.set_transport(Transport::Tls(TlsConfiguration::Native));
        } else if settings.use_websockets {
            opts.set_transport(Transport::Ws);
        }

        let (client, mut eventloop) = AsyncClient::new(opts, 64);
        let (stop_tx, mut stop_rx) = watch::channel(false);

        let client_clone = client.clone();
        let prefix_clone = prefix.clone();

        tauri::async_runtime::spawn(async move {
            loop {
                tokio::select! {
                    // Sender dropped (MqttService replaced/dropped) → stop.
                    _ = stop_rx.changed() => {
                        log::info!("MQTT: eventloop stopping");
                        break;
                    }
                    event = eventloop.poll() => match event {
                        Ok(Event::Incoming(Packet::ConnAck(_))) => {
                            log::info!("MQTT: connected to broker");
                            app.emit("mqtt-status", "Connected").ok();
                            publish_availability(&client_clone, &prefix_clone, true).await;
                            subscribe(&client_clone, &prefix_clone).await;
                            publish_discovery_inner(&client_clone, &prefix_clone).await;
                            let _ = reconnect_tx.send(()).await;
                        }
                        Ok(Event::Incoming(Packet::Publish(msg))) => {
                            handle_incoming(&prefix_clone, &msg.topic, &msg.payload, &cmd_tx).await;
                        }
                        Ok(Event::Outgoing(rumqttc::Outgoing::Disconnect)) => {
                            log::info!("MQTT: disconnect sent");
                        }
                        Err(e) => {
                            log::warn!("MQTT error: {e}");
                            app.emit("mqtt-status", "Disconnected").ok();
                            // Wait before retry, but honour stop signal.
                            tokio::select! {
                                _ = stop_rx.changed() => break,
                                _ = tokio::time::sleep(Duration::from_secs(5)) => {}
                            }
                        }
                        _ => {}
                    }
                }
            }
            log::info!("MQTT: eventloop exited");
        });

        Ok(Self {
            client,
            prefix,
            _stop_tx: stop_tx,
        })
    }

    pub async fn publish_state(&self, state: &MeetingState) -> Result<()> {
        let prefix = &self.prefix;

        let bool_pairs: &[(&str, &str, bool)] = &[
            ("switch", "ismuted", state.is_muted),
            ("switch", "isvideoon", state.is_video_on),
            ("binary_sensor", "isinmeeting", state.is_in_meeting),
            ("binary_sensor", "hasunreadmessages", state.has_unread_messages),
            ("binary_sensor", "teamsrunning", state.teams_running),
        ];
        for (component, id, value) in bool_pairs {
            if let Err(e) = self
                .client
                .publish(
                    format!("homeassistant/{component}/{prefix}/{id}/state"),
                    QoS::AtLeastOnce,
                    true,
                    if *value { "ON" } else { "OFF" },
                )
                .await
            {
                log::warn!("MQTT publish failed [{id}]: {e}");
            }
        }

        if !state.presence.is_empty() {
            log::debug!(
                "MQTT publishing teamsstatus: '{}' → homeassistant/sensor/{prefix}/teamsstatus/state",
                state.presence
            );
            if let Err(e) = self
                .client
                .publish(
                    format!("homeassistant/sensor/{prefix}/teamsstatus/state"),
                    QoS::AtLeastOnce,
                    true,
                    state.presence.as_bytes().to_vec(),
                )
                .await
            {
                log::warn!("MQTT publish failed [teamsstatus]: {e}");
            }
        }

        Ok(())
    }
}

fn availability_topic(prefix: &str) -> String {
    format!("teams2ha/{prefix}/availability")
}

async fn publish_availability(client: &AsyncClient, prefix: &str, online: bool) {
    if let Err(e) = client
        .publish(
            availability_topic(prefix),
            QoS::AtLeastOnce,
            true,
            if online { "online" } else { "offline" },
        )
        .await
    {
        log::warn!("MQTT availability publish failed: {e}");
    }
}

async fn subscribe(client: &AsyncClient, prefix: &str) {
    if let Err(e) = client
        .subscribe(
            format!("homeassistant/switch/{prefix}/+/set"),
            QoS::AtLeastOnce,
        )
        .await
    {
        log::warn!("MQTT subscribe error: {e}");
    }
}

async fn publish_discovery_inner(client: &AsyncClient, prefix: &str) {
    let device = json!({
        "identifiers": [format!("teams2ha_{prefix}")],
        "name": format!("Teams2HA ({})", prefix),
        "model": "Teams2HA",
        "manufacturer": "jimmyeao"
    });

    let switches = [("ismuted", "Is Muted"), ("isvideoon", "Is Video On")];
    let binary_sensors = [
        ("isinmeeting", "Is In Meeting"),
        ("hasunreadmessages", "Has Unread Messages"),
        ("teamsrunning", "Teams Running"),
    ];

    for (id, name) in &switches {
        let payload = json!({
            "name": name,
            "unique_id": format!("{prefix}_{id}"),
            "state_topic": format!("homeassistant/switch/{prefix}/{id}/state"),
            "command_topic": format!("homeassistant/switch/{prefix}/{id}/set"),
            "payload_on": "ON",
            "payload_off": "OFF",
            "availability_topic": availability_topic(prefix),
            "payload_available": "online",
            "payload_not_available": "offline",
            "device": device
        });
        if let Err(e) = client
            .publish(
                format!("homeassistant/switch/{prefix}/{id}/config"),
                QoS::AtLeastOnce,
                true,
                serde_json::to_vec(&payload).unwrap_or_default(),
            )
            .await
        {
            log::warn!("Discovery publish failed for {id}: {e}");
        }
    }

    for (id, name) in &binary_sensors {
        let payload = json!({
            "name": name,
            "unique_id": format!("{prefix}_{id}"),
            "state_topic": format!("homeassistant/binary_sensor/{prefix}/{id}/state"),
            "payload_on": "ON",
            "payload_off": "OFF",
            "availability_topic": availability_topic(prefix),
            "payload_available": "online",
            "payload_not_available": "offline",
            "device": device
        });
        if let Err(e) = client
            .publish(
                format!("homeassistant/binary_sensor/{prefix}/{id}/config"),
                QoS::AtLeastOnce,
                true,
                serde_json::to_vec(&payload).unwrap_or_default(),
            )
            .await
        {
            log::warn!("Discovery publish failed for {id}: {e}");
        }
    }

    let teamsstatus_payload = json!({
        "name": "Teams Status",
        "unique_id": format!("{prefix}_teamsstatus"),
        "state_topic": format!("homeassistant/sensor/{prefix}/teamsstatus/state"),
        "icon": "mdi:account-circle",
        "availability_topic": availability_topic(prefix),
        "payload_available": "online",
        "payload_not_available": "offline",
        "device": device
    });
    if let Err(e) = client
        .publish(
            format!("homeassistant/sensor/{prefix}/teamsstatus/config"),
            QoS::AtLeastOnce,
            true,
            serde_json::to_vec(&teamsstatus_payload).unwrap_or_default(),
        )
        .await
    {
        log::warn!("Discovery publish failed for teamsstatus: {e}");
    }

    log::info!("MQTT: discovery published for prefix '{prefix}'");
}

async fn handle_incoming(
    prefix: &str,
    topic: &str,
    payload: &[u8],
    cmd_tx: &mpsc::Sender<MqttCommand>,
) {
    let payload_str = std::str::from_utf8(payload).unwrap_or("").trim();
    log::debug!("MQTT incoming: {topic} = {payload_str}");

    let switch_prefix = format!("homeassistant/switch/{prefix}/");
    if let Some(rest) = topic.strip_prefix(&switch_prefix) {
        if let Some(id) = rest.strip_suffix("/set") {
            match id {
                "ismuted" => {
                    let _ = cmd_tx.send(MqttCommand::ToggleMute).await;
                }
                "isvideoon" => {
                    let _ = cmd_tx.send(MqttCommand::ToggleVideo).await;
                }
                _ => {}
            }
        }
    }
}
