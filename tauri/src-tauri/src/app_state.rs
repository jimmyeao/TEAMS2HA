use crate::mqtt_service::MeetingState;
use std::sync::Arc;
use tokio::sync::RwLock;

#[derive(Debug, Clone, Default)]
pub struct AppState {
    pub meeting: MeetingState,
    pub log_watcher_in_call: bool,
    /// Mute flag on Teams' audio session, recorded whether or not a meeting was active
    /// at the time. The session routinely appears a few seconds before the mic-in-use
    /// registry key flips, so the first reading of a call would otherwise be discarded
    /// for arriving "too early" — and wasapi_monitor only emits on change, so it would
    /// never be re-sent.
    ///
    /// `None` means Teams has no capture session, **not** "no data" and **not** "muted";
    /// see `recompute_muted`.
    pub last_wasapi_muted: Option<bool>,
    /// Teams' in-app mute, from UI Automation. `None` means "no reading" (no meeting
    /// window / button not found) — deliberately distinct from `Some(false)`, so a
    /// missing reading never reads as "unmuted".
    pub uia_muted: Option<bool>,
    /// Last state that was actually delivered to MQTT — used to skip republishing
    /// an unchanged state on every monitor event. Only set after a successful
    /// publish, so a failed publish is retried on the next event.
    pub last_published: Option<MeetingState>,
}

pub type SharedState = Arc<RwLock<AppState>>;

pub fn new_shared() -> SharedState {
    Arc::new(RwLock::new(AppState::default()))
}
