use crate::mqtt_service::MeetingState;
use std::sync::Arc;
use tokio::sync::RwLock;

#[derive(Debug, Clone, Default)]
pub struct AppState {
    pub meeting: MeetingState,
    pub log_watcher_in_call: bool,
    /// Last state that was actually delivered to MQTT — used to skip republishing
    /// an unchanged state on every monitor event. Only set after a successful
    /// publish, so a failed publish is retried on the next event.
    pub last_published: Option<MeetingState>,
}

pub type SharedState = Arc<RwLock<AppState>>;

pub fn new_shared() -> SharedState {
    Arc::new(RwLock::new(AppState::default()))
}
