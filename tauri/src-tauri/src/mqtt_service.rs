use crate::settings::Settings;
use anyhow::Result;
use rumqttc::{
    AsyncClient, Event, LastWill, MqttOptions, Packet, QoS, TlsConfiguration, Transport,
};
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
        // No broker address means there is nothing to connect to. Without this guard
        // rumqttc happily accepts an empty host and the eventloop below retries it
        // forever, logging a timeout every 10s into a 5 MB-capped log file.
        if settings.mqtt_address.trim().is_empty() {
            anyhow::bail!("no MQTT broker address configured");
        }

        let prefix = settings.sensor_prefix.to_lowercase();
        let broker_addr = normalized_broker_address(settings);
        let port = settings.mqtt_port;

        let mut opts = MqttOptions::new(
            format!("teams2ha-{}", hostname::get()?.to_string_lossy()),
            &broker_addr,
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

        // "Use TLS" must always yield an encrypted transport. Previously the
        // ignore_cert_errors flag silently downgraded TLS to plain TCP, and the
        // TLS+websockets combination fed a native-tls config into rumqttc's WSS
        // path even though that transport only accepts a rustls-backed config.
        if settings.use_websockets {
            if settings.use_tls {
                opts.set_transport(Transport::Wss(build_websocket_tls(
                    settings.ignore_cert_errors,
                )));
            } else {
                opts.set_transport(Transport::Ws);
            }
        } else if settings.use_tls {
            let tls = if settings.ignore_cert_errors {
                build_permissive_tls()
            } else {
                TlsConfiguration::Native
            };
            opts.set_transport(Transport::Tls(tls));
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

    // Uses `try_publish` (non-blocking) rather than `publish().await` deliberately: this is
    // called from the app's single central event-loop task (see `publish()` in lib.rs), which
    // also owns reconnect/resume handling. rumqttc's internal request channel is only drained
    // while a connection is live — while disconnected it fills up (default cap 64), and the
    // blocking `publish().await` would then hang forever waiting for room. Since that stalls
    // the same task that's supposed to detect and rebuild a dead connection, a stuck publish
    // could permanently prevent the app from ever reconnecting. These are retained topics
    // republished on every state change and again in full on the next ConnAck, so a publish
    // dropped here because the channel is full is not lost — it's superseded.
    pub async fn publish_state(&self, state: &MeetingState) -> Result<()> {
        let prefix = &self.prefix;

        let bool_pairs: &[(&str, &str, bool)] = &[
            ("switch", "ismuted", state.is_muted),
            ("switch", "isvideoon", state.is_video_on),
            ("binary_sensor", "isinmeeting", state.is_in_meeting),
            (
                "binary_sensor",
                "hasunreadmessages",
                state.has_unread_messages,
            ),
            ("binary_sensor", "teamsrunning", state.teams_running),
        ];
        for (component, id, value) in bool_pairs {
            if let Err(e) = self.client.try_publish(
                format!("homeassistant/{component}/{prefix}/{id}/state"),
                QoS::AtLeastOnce,
                true,
                if *value { "ON" } else { "OFF" },
            ) {
                log::warn!("MQTT publish failed [{id}]: {e}");
            }
        }

        if !state.presence.is_empty() {
            log::debug!(
                "MQTT publishing teamsstatus: '{}' → homeassistant/sensor/{prefix}/teamsstatus/state",
                state.presence
            );
            if let Err(e) = self.client.try_publish(
                format!("homeassistant/sensor/{prefix}/teamsstatus/state"),
                QoS::AtLeastOnce,
                true,
                state.presence.as_bytes().to_vec(),
            ) {
                log::warn!("MQTT publish failed [teamsstatus]: {e}");
            }
        }

        Ok(())
    }
}

/// TLS config for the "ignore certificate errors" setting: still a real TLS
/// handshake, but self-signed and hostname-mismatched broker certificates are
/// accepted. Needed for the common home setup of a Mosquitto instance with a
/// self-signed cert; the old code path handled that case by dropping encryption
/// altogether, which is strictly worse.
///
/// native_tls comes in through rumqttc's own re-export so the connector type is
/// guaranteed to match the version rumqttc links against.
fn build_permissive_tls() -> TlsConfiguration {
    use rumqttc::tokio_native_tls::native_tls;

    match native_tls::TlsConnector::builder()
        .danger_accept_invalid_certs(true)
        .danger_accept_invalid_hostnames(true)
        .build()
    {
        Ok(connector) => {
            log::warn!(
                "MQTT: certificate verification disabled by user setting — the connection \
                 is encrypted but the broker's identity is not verified"
            );
            TlsConfiguration::NativeConnector(connector)
        }
        // Don't fall back to plaintext: a failed builder is not a reason to
        // downgrade. Full verification may reject a self-signed cert, but that
        // fails loudly instead of leaking credentials.
        Err(e) => {
            log::warn!(
                "MQTT: could not build permissive TLS connector ({e}); \
                 falling back to full certificate verification"
            );
            TlsConfiguration::Native
        }
    }
}

fn build_websocket_tls(ignore_cert_errors: bool) -> TlsConfiguration {
    ensure_rustls_provider();
    if ignore_cert_errors {
        build_permissive_websocket_tls()
    } else {
        TlsConfiguration::default()
    }
}

fn build_permissive_websocket_tls() -> TlsConfiguration {
    use rumqttc::tokio_rustls::rustls::{
        client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier},
        pki_types::{CertificateDer, ServerName, UnixTime},
        ClientConfig, DigitallySignedStruct, Error, SignatureScheme,
    };
    use std::sync::Arc;

    #[derive(Debug)]
    struct NoVerifier;

    impl ServerCertVerifier for NoVerifier {
        fn verify_server_cert(
            &self,
            _end_entity: &CertificateDer<'_>,
            _intermediates: &[CertificateDer<'_>],
            _server_name: &ServerName<'_>,
            _ocsp_response: &[u8],
            _now: UnixTime,
        ) -> Result<ServerCertVerified, Error> {
            Ok(ServerCertVerified::assertion())
        }

        fn verify_tls12_signature(
            &self,
            _message: &[u8],
            _cert: &CertificateDer<'_>,
            _dss: &DigitallySignedStruct,
        ) -> Result<HandshakeSignatureValid, Error> {
            Ok(HandshakeSignatureValid::assertion())
        }

        fn verify_tls13_signature(
            &self,
            _message: &[u8],
            _cert: &CertificateDer<'_>,
            _dss: &DigitallySignedStruct,
        ) -> Result<HandshakeSignatureValid, Error> {
            Ok(HandshakeSignatureValid::assertion())
        }

        fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
            vec![
                SignatureScheme::RSA_PKCS1_SHA1,
                SignatureScheme::ECDSA_SHA1_Legacy,
                SignatureScheme::RSA_PKCS1_SHA256,
                SignatureScheme::ECDSA_NISTP256_SHA256,
                SignatureScheme::RSA_PKCS1_SHA384,
                SignatureScheme::ECDSA_NISTP384_SHA384,
                SignatureScheme::RSA_PKCS1_SHA512,
                SignatureScheme::ECDSA_NISTP521_SHA512,
                SignatureScheme::RSA_PSS_SHA256,
                SignatureScheme::RSA_PSS_SHA384,
                SignatureScheme::RSA_PSS_SHA512,
                SignatureScheme::ED25519,
                SignatureScheme::ED448,
            ]
        }
    }

    log::warn!(
        "MQTT: certificate verification disabled by user setting — the WSS connection \
         is encrypted but the broker's identity is not verified"
    );

    TlsConfiguration::from(
        ClientConfig::builder()
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(NoVerifier))
            .with_no_client_auth(),
    )
}

fn ensure_rustls_provider() {
    use rumqttc::tokio_rustls::rustls::crypto::{ring, CryptoProvider};

    if CryptoProvider::get_default().is_none() {
        let _ = ring::default_provider().install_default();
    }
}

fn normalized_broker_address(settings: &Settings) -> String {
    let address = settings.mqtt_address.trim();
    if !settings.use_websockets || address.is_empty() {
        return address.to_string();
    }

    let scheme = if settings.use_tls { "wss" } else { "ws" };
    let rest = address
        .split_once("://")
        .map(|(_, rest)| rest)
        .unwrap_or(address);

    let split_at = rest.find(['/', '?']).unwrap_or(rest.len());
    let (authority, suffix) = rest.split_at(split_at);
    let authority = authority
        .rsplit_once('@')
        .map(|(_, host)| host)
        .unwrap_or(authority)
        .trim_end_matches('/');

    let authority = if has_explicit_port(authority) {
        authority.to_string()
    } else {
        format!("{authority}:{}", settings.mqtt_port)
    };

    let suffix = if suffix.is_empty() { "/mqtt" } else { suffix };
    format!("{scheme}://{authority}{suffix}")
}

fn has_explicit_port(authority: &str) -> bool {
    if authority.is_empty() {
        return false;
    }
    if authority.starts_with('[') {
        return authority
            .rsplit_once("]:")
            .is_some_and(|(_, port)| !port.is_empty() && port.chars().all(|c| c.is_ascii_digit()));
    }
    authority
        .rsplit_once(':')
        .is_some_and(|(_, port)| !port.is_empty() && port.chars().all(|c| c.is_ascii_digit()))
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
        "manufacturer": "jimmyeao",
        // Real app version (release builds get it stamped from the git tag). Without this
        // the device registry keeps showing whatever an older install once published.
        "sw_version": env!("CARGO_PKG_VERSION")
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

#[cfg(test)]
mod tests {
    use super::*;

    fn settings() -> Settings {
        Settings {
            mqtt_address: "homeassistant.local".into(),
            mqtt_port: 9001,
            mqtt_username: String::new(),
            mqtt_password: String::new(),
            sensor_prefix: "pc".into(),
            use_tls: false,
            ignore_cert_errors: false,
            use_websockets: false,
            run_at_boot: false,
            run_minimized: false,
            theme: "dark".into(),
            color_scheme: "DeepPurple / Lime".into(),
            home_gateway_mac: String::new(),
        }
    }

    #[test]
    fn websocket_address_adds_scheme_port_and_default_path() {
        let mut settings = settings();
        settings.use_websockets = true;

        assert_eq!(
            normalized_broker_address(&settings),
            "ws://homeassistant.local:9001/mqtt"
        );
    }

    #[test]
    fn websocket_address_preserves_path_and_forces_tls_scheme() {
        let mut settings = settings();
        settings.use_websockets = true;
        settings.use_tls = true;
        settings.mqtt_address = "ws://home.pauwal.de/mqtt".into();
        settings.mqtt_port = 443;

        assert_eq!(
            normalized_broker_address(&settings),
            "wss://home.pauwal.de:443/mqtt"
        );
    }

    #[test]
    fn websocket_address_keeps_existing_port_and_query() {
        let mut settings = settings();
        settings.use_websockets = true;
        settings.mqtt_address = "broker.example.com:8443/mqtt?client_id=test".into();

        assert_eq!(
            normalized_broker_address(&settings),
            "ws://broker.example.com:8443/mqtt?client_id=test"
        );
    }

    #[test]
    fn websocket_address_drops_unsupported_url_credentials() {
        let mut settings = settings();
        settings.use_websockets = true;
        settings.use_tls = true;
        let userinfo = format!("{}:{}@", "user", "pw");
        settings.mqtt_address = format!("wss://{userinfo}broker.example.com/mqtt");

        assert_eq!(
            normalized_broker_address(&settings),
            "wss://broker.example.com:9001/mqtt"
        );
    }

    #[test]
    fn detects_explicit_ipv6_port() {
        assert!(has_explicit_port("[2001:db8::1]:9001"));
        assert!(!has_explicit_port("[2001:db8::1]"));
    }

    #[test]
    fn websocket_tls_uses_rustls_configuration() {
        assert!(matches!(
            build_websocket_tls(false),
            TlsConfiguration::Rustls(_)
        ));
        assert!(matches!(
            build_websocket_tls(true),
            TlsConfiguration::Rustls(_)
        ));
    }

    // --- Live-broker checks against a local mosquitto (not part of the PR; run
    // manually with `cargo test --lib -- --ignored live_broker_tests`) ---
    mod live_broker_tests {
        use super::*;
        use rumqttc::{AsyncClient, ConnectionError, Event, MqttOptions, Packet, Transport};
        use tokio::time::{timeout, Duration};

        fn base_settings(port: u16) -> Settings {
            let mut s = settings();
            s.mqtt_address = "127.0.0.1".into();
            s.mqtt_port = port;
            s
        }

        /// Builds `MqttOptions` exactly the way `MqttService::connect` does, then
        /// drives the real rumqttc eventloop against a live broker: connect, publish
        /// a retained probe message, receive our own echo via a subscription, then
        /// disconnect. `Ok(())` only once the echo is observed.
        async fn probe(settings: &Settings) -> std::result::Result<(), String> {
            let broker_addr = normalized_broker_address(settings);
            let mut opts = MqttOptions::new("teams2ha-probe", &broker_addr, settings.mqtt_port);
            opts.set_keep_alive(Duration::from_secs(5));

            if !settings.mqtt_username.is_empty() {
                opts.set_credentials(&settings.mqtt_username, &settings.mqtt_password);
            }

            if settings.use_websockets {
                if settings.use_tls {
                    opts.set_transport(Transport::Wss(build_websocket_tls(
                        settings.ignore_cert_errors,
                    )));
                } else {
                    opts.set_transport(Transport::Ws);
                }
            } else if settings.use_tls {
                let tls = if settings.ignore_cert_errors {
                    build_permissive_tls()
                } else {
                    TlsConfiguration::Native
                };
                opts.set_transport(Transport::Tls(tls));
            }

            let (client, mut eventloop) = AsyncClient::new(opts, 16);
            let topic = format!(
                "teams2ha/probe/{}",
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            );

            let deadline = Duration::from_secs(8);

            loop {
                let event = timeout(deadline, eventloop.poll())
                    .await
                    .map_err(|_| "timed out waiting for broker".to_string())?;
                match event {
                    Ok(Event::Incoming(Packet::ConnAck(ack))) => {
                        if ack.code != rumqttc::ConnectReturnCode::Success {
                            return Err(format!("broker refused connection: {:?}", ack.code));
                        }
                        client
                            .subscribe(&topic, QoS::AtLeastOnce)
                            .await
                            .map_err(|e| format!("subscribe failed: {e}"))?;
                    }
                    Ok(Event::Incoming(Packet::SubAck(_))) => {
                        client
                            .publish(&topic, QoS::AtLeastOnce, false, b"probe".to_vec())
                            .await
                            .map_err(|e| format!("publish failed: {e}"))?;
                    }
                    Ok(Event::Incoming(Packet::Publish(p))) if p.topic == topic => {
                        let _ = client.disconnect().await;
                        return Ok(());
                    }
                    Ok(_) => {}
                    Err(ConnectionError::Io(e)) => return Err(format!("io error: {e}")),
                    Err(e) => return Err(format!("connection error: {e}")),
                }
            }
        }

        #[tokio::test]
        #[ignore]
        async fn plain_tcp_anonymous() {
            let s = base_settings(1883);
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn plain_tcp_with_auth() {
            let mut s = base_settings(1884);
            s.mqtt_username = "testuser".into();
            s.mqtt_password = "testpass".into();
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn plain_tcp_with_wrong_password_is_rejected() {
            let mut s = base_settings(1884);
            s.mqtt_username = "testuser".into();
            s.mqtt_password = "wrong".into();
            assert!(probe(&s).await.is_err());
        }

        #[tokio::test]
        #[ignore]
        async fn tls_self_signed_rejected_without_ignore_flag() {
            let mut s = base_settings(8883);
            s.use_tls = true;
            assert!(
                probe(&s).await.is_err(),
                "a self-signed cert must be rejected when ignore_cert_errors is off"
            );
        }

        #[tokio::test]
        #[ignore]
        async fn tls_self_signed_accepted_with_ignore_flag() {
            let mut s = base_settings(8883);
            s.use_tls = true;
            s.ignore_cert_errors = true;
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn tls_with_auth_and_ignore_flag() {
            let mut s = base_settings(8884);
            s.use_tls = true;
            s.ignore_cert_errors = true;
            s.mqtt_username = "testuser".into();
            s.mqtt_password = "testpass".into();
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn websocket_plain_anonymous() {
            let mut s = base_settings(9001);
            s.use_websockets = true;
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn websocket_tls_self_signed_rejected_without_ignore_flag() {
            let mut s = base_settings(9002);
            s.use_websockets = true;
            s.use_tls = true;
            assert!(
                probe(&s).await.is_err(),
                "WSS to a self-signed broker must be rejected when ignore_cert_errors is off"
            );
        }

        #[tokio::test]
        #[ignore]
        async fn websocket_tls_self_signed_accepted_with_ignore_flag() {
            let mut s = base_settings(9002);
            s.use_websockets = true;
            s.use_tls = true;
            s.ignore_cert_errors = true;
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn websocket_tls_with_auth_and_ignore_flag() {
            let mut s = base_settings(9003);
            s.use_websockets = true;
            s.use_tls = true;
            s.ignore_cert_errors = true;
            s.mqtt_username = "testuser".into();
            s.mqtt_password = "testpass".into();
            assert_eq!(probe(&s).await, Ok(()));
        }

        #[tokio::test]
        #[ignore]
        async fn websocket_tls_with_wrong_password_is_rejected() {
            let mut s = base_settings(9003);
            s.use_websockets = true;
            s.use_tls = true;
            s.ignore_cert_errors = true;
            s.mqtt_username = "testuser".into();
            s.mqtt_password = "wrong".into();
            assert!(probe(&s).await.is_err());
        }

        /// The exact scenario the PR fixes: a websocket address that already has a
        /// `ws://`/`wss://` scheme, a non-default path, and its own port, as typically
        /// pasted from a reverse-proxy setup — normalized_broker_address must leave it
        /// alone rather than mangling it, and the connection must still succeed.
        #[tokio::test]
        #[ignore]
        async fn websocket_with_explicit_url_and_path() {
            let mut s = base_settings(9001);
            s.use_websockets = true;
            s.mqtt_address = "ws://127.0.0.1:9001/mqtt".into();
            assert_eq!(probe(&s).await, Ok(()));
        }

        /// URL-embedded userinfo (`wss://user:pass@host/...`) must be stripped from
        /// the address used for the websocket handshake — it isn't valid there — and
        /// the app's own username/password settings remain the actual credential
        /// source. Same broker/credentials as `websocket_tls_with_auth_and_ignore_flag`,
        /// but with (different, wrong-for-the-broker) userinfo baked into the address
        /// to prove it's discarded rather than sent instead of the real credentials.
        #[tokio::test]
        #[ignore]
        async fn websocket_url_embedded_userinfo_is_ignored_in_favor_of_settings() {
            let mut s = base_settings(9003);
            s.use_websockets = true;
            s.use_tls = true;
            s.ignore_cert_errors = true;
            s.mqtt_address = "wss://decoy:decoy@127.0.0.1/mqtt".into();
            s.mqtt_username = "testuser".into();
            s.mqtt_password = "testpass".into();
            assert_eq!(probe(&s).await, Ok(()));
        }

        // IPv6 (has_explicit_port's bracketed-host branch) is not exercised live here:
        // this test environment's Docker-on-WSL2 port publishing doesn't forward ::1
        // to the Windows host (confirmed independent of the app — a raw TCP connect
        // to [::1]:1883 also times out). Covered as a string-level unit test instead
        // (`detects_explicit_ipv6_port`, above).
    }
}
