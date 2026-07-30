use std::collections::HashSet;
use std::fs::File;
use std::io::{BufRead, BufReader, Seek, SeekFrom};
use std::path::PathBuf;
use std::time::Duration;
use tokio::sync::{mpsc, watch};
use tokio::time::interval;

#[derive(Debug, Clone)]
pub enum LogEvent {
    MuteChanged(bool),
    MeetingChanged(bool),
    PresenceChanged(String),
    UnreadMessages(bool),
}

/// Call-lifecycle bookkeeping keyed on Teams' own call ids.
///
/// Teams runs calls concurrently: an incoming call that rings during a meeting
/// is a second call, and declining (or missing) it logs `reportIncomingCall` +
/// `NotifyCallEnded` for that id — without ever logging `NotifyCallActive`.
/// The old boolean treated *any* end-line as "the meeting is over", so a
/// declined call ended the running meeting (and e.g. restored smart-home
/// speaker volume mid-meeting). Only ids that were seen active may close the
/// call state, and only when the last one is gone.
#[derive(Default)]
struct CallState {
    /// Ids seen in a `NotifyCallActive`/`reportCallActive` line.
    active: HashSet<String>,
    /// An active-line carried no parseable id (format drift): fall back to
    /// the pre-id semantics where any end-line closes the call.
    legacy: bool,
}

impl CallState {
    fn in_call(&self) -> bool {
        self.legacy || !self.active.is_empty()
    }

    fn clear(&mut self) {
        self.active.clear();
        self.legacy = false;
    }

    /// Feed one log line through the call state machine. Returns the new
    /// in-call value when the line *transitions* it, None otherwise (either
    /// not a call line, or a call line that doesn't change the outcome).
    fn apply(&mut self, line: &str) -> Option<bool> {
        let was = self.in_call();
        if line.contains("NotifyCallActive") || line.contains("reportCallActive") {
            match extract_call_id(line) {
                Some(id) => {
                    log::info!("LogWatcher: call active ({id})");
                    self.active.insert(id);
                    // An id-bearing line supersedes any id-less line from the
                    // same activation batch (Teams writes both, order varies).
                    self.legacy = false;
                }
                None => {
                    if self.active.is_empty() {
                        log::warn!(
                            "LogWatcher: call active without parseable id — legacy mode"
                        );
                        self.legacy = true;
                    } else {
                        // Teams logs several NotifyCallActive lines per
                        // activation and only the Hfp one carries the call id
                        // ("CallInfo: NotifyCallActive causeId: …", "CallInfo:
                        // CallTracker: Calling NotifyCallActive without
                        // deviceId…"). While id'd calls are tracked these are
                        // duplicates, not a format change.
                        log::debug!("LogWatcher: ignoring id-less active-line while tracking ids");
                    }
                }
            }
        } else if line.contains("CallEnded") || line.contains("NotifyCallEnded") {
            match extract_call_id(line) {
                Some(id) => {
                    if self.active.remove(&id) {
                        log::info!("LogWatcher: call ended ({id})");
                    } else if self.active.is_empty() && self.legacy {
                        // An id-less active call is closed by whichever
                        // end-line arrives first.
                        log::info!("LogWatcher: call ended (legacy, {id})");
                        self.legacy = false;
                    } else {
                        // End of a call that never went active here: a
                        // declined/missed incoming call, or one of the
                        // duplicate end-lines Teams writes per call. Must
                        // not touch the running meeting.
                        log::debug!("LogWatcher: ignoring end of inactive call {id}");
                    }
                }
                None => {
                    if self.active.is_empty() {
                        if self.legacy {
                            log::info!("LogWatcher: call ended (no id)");
                        }
                        self.legacy = false;
                    } else {
                        // We are tracking id'd calls; an end-line without an
                        // id is log noise, not one of ours.
                        log::debug!("LogWatcher: ignoring id-less end-line");
                    }
                }
            }
        } else {
            return None;
        }
        let now = self.in_call();
        (now != was).then_some(now)
    }
}

/// Pull the 36-char GUID following a `callId: ` (HfpVoipCallCoordinatorImpl
/// lines) or `fired: ` (TeamsCallTracker lines) marker. The GUID is often
/// glued straight onto the next field (`…fa77causeId: …`), so take exactly
/// the GUID shape rather than splitting on whitespace. None on format drift —
/// callers then fall back to the legacy any-end-closes-the-call semantics.
fn extract_call_id(line: &str) -> Option<String> {
    let start = ["callId: ", "fired: "]
        .iter()
        .find_map(|m| line.find(m).map(|i| i + m.len()))?;
    let id: String = line.get(start..)?.chars().take(36).collect();
    is_guid(&id).then_some(id)
}

fn is_guid(id: &str) -> bool {
    id.len() == 36
        && id.char_indices().all(|(i, c)| match i {
            8 | 13 | 18 | 23 => c == '-',
            _ => c.is_ascii_hexdigit(),
        })
}

pub fn start(tx: mpsc::Sender<LogEvent>, teams_running: watch::Receiver<bool>) {
    tauri::async_runtime::spawn(poll_loop(tx, teams_running));
}

async fn poll_loop(tx: mpsc::Sender<LogEvent>, mut teams_running: watch::Receiver<bool>) {
    let mut current_file: Option<PathBuf> = None;
    let mut file_handle: Option<(BufReader<File>, u64)> = None;
    let mut calls = CallState::default();

    let mut tick = interval(Duration::from_millis(250));

    loop {
        tick.tick().await;

        // A Teams exit (crash or quit) never writes end-lines for calls that
        // were still running — drop them, or a stale id would keep the call
        // set non-empty forever and pin the meeting state on.
        if teams_running.has_changed().unwrap_or(false)
            && !*teams_running.borrow_and_update()
        {
            let was = calls.in_call();
            calls.clear();
            if was {
                log::info!("LogWatcher: Teams stopped — clearing active call state");
                let _ = tx.send(LogEvent::MeetingChanged(false)).await;
            }
        }

        let latest = match find_latest_log() {
            Some(p) => p,
            None => continue,
        };

        // Switched to a new log file
        if current_file.as_deref() != Some(&latest) {
            log::info!("LogWatcher: opening {}", latest.display());
            match File::open(&latest) {
                Ok(f) => {
                    let mut reader = BufReader::new(f);
                    // Scan the last 256 KB for the most recent presence entry
                    // before tailing, so we report current status immediately.
                    if let Some(presence) = scan_last_presence(&mut reader) {
                        log::info!("LogWatcher: initial presence → {presence}");
                        let _ = tx.send(LogEvent::PresenceChanged(presence)).await;
                    }
                    let end = reader.seek(SeekFrom::End(0)).unwrap_or(0);
                    file_handle = Some((reader, end));
                    current_file = Some(latest);
                }
                Err(e) => {
                    log::warn!("LogWatcher: cannot open log: {e}");
                    continue;
                }
            }
        }

        if let Some((reader, _pos)) = &mut file_handle {
            let mut line = String::new();
            loop {
                line.clear();
                match reader.read_line(&mut line) {
                    Ok(0) => break,
                    Ok(_) => {
                        process_line(line.trim(), &tx, &mut calls).await;
                    }
                    Err(e) => {
                        log::warn!("LogWatcher: read error: {e}");
                        break;
                    }
                }
            }
        }
    }
}

async fn process_line(line: &str, tx: &mpsc::Sender<LogEvent>, calls: &mut CallState) {
    // NOTE: modern Teams (MSTeams_8wekyb3d8bbwe) does not log mute state at all — a search
    // of its logs finds "mute" only in `HFP_VCC_UNMUTE_FIX` and `server mutex`. This branch
    // is retained for the classic Teams log fallback in find_latest_log(); on current Teams
    // the mute signal comes solely from wasapi_monitor. Do not assume this covers mute.
    if line.contains("NotifyCallMuteStateChanged") {
        let muted = line.contains("muteState: true");
        log::debug!("LogWatcher: mute → {muted}");
        let _ = tx.send(LogEvent::MuteChanged(muted)).await;
    } else if let Some(in_call) = calls.apply(line) {
        let _ = tx.send(LogEvent::MeetingChanged(in_call)).await;
    } else if line.contains("UserPresenceAction") {
        if let Some(status) = extract_presence(line) {
            log::debug!("LogWatcher: presence → {status}");
            let _ = tx.send(LogEvent::PresenceChanged(status)).await;
        }
    } else if let Some(count) = extract_unread_count(line) {
        log::debug!("LogWatcher: unread count → {count}");
        let _ = tx.send(LogEvent::UnreadMessages(count > 0)).await;
    }
}

/// Teams reports the unread count inside its user-data state lines:
/// `... availability: Available, unread notification count: 0 }`
///
/// This was previously `line.contains("true") || line.contains("1")`, which matched the
/// '1' in the ISO timestamp of practically every line — so the sensor latched to
/// "unread" the first time such a line appeared and never cleared. Parse the number.
fn extract_unread_count(line: &str) -> Option<u32> {
    let rest = line.split("unread notification count:").nth(1)?;
    let digits: String = rest
        .trim_start()
        .chars()
        .take_while(|c| c.is_ascii_digit())
        .collect();
    digits.parse().ok()
}

/// Read the last 256 KB of the log file and return the most recent presence value.
fn scan_last_presence(reader: &mut BufReader<File>) -> Option<String> {
    const SCAN_BYTES: u64 = 256 * 1024;
    let file_len = reader.seek(SeekFrom::End(0)).ok()?;
    let start = file_len.saturating_sub(SCAN_BYTES);
    reader.seek(SeekFrom::Start(start)).ok()?;

    let mut last = None;
    let mut line = String::new();
    loop {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) => break,
            Ok(_) => {
                if line.contains("UserPresenceAction") {
                    if let Some(s) = extract_presence(line.trim()) {
                        last = Some(s);
                    }
                }
            }
            Err(_) => break,
        }
    }
    last
}

fn extract_presence(line: &str) -> Option<String> {
    // e.g. "UserPresenceAction Busy" or "presence: Available"
    for status in &["Busy", "Available", "Away", "DoNotDisturb", "BeRightBack", "Offline"] {
        if line.contains(status) {
            return Some(status.to_string());
        }
    }
    None
}

fn find_latest_log() -> Option<PathBuf> {
    let teams_appdata = std::env::var("LOCALAPPDATA").ok()?;
    let log_dir = PathBuf::from(&teams_appdata).join("Packages")
        .join("MSTeams_8wekyb3d8bbwe")
        .join("LocalCache")
        .join("Microsoft")
        .join("MSTeams")
        .join("Logs");

    if !log_dir.exists() {
        // Fallback: classic Teams log location
        let classic = PathBuf::from(&teams_appdata)
            .join("Microsoft")
            .join("Teams")
            .join("logs.txt");
        if classic.exists() {
            return Some(classic);
        }
        return None;
    }

    std::fs::read_dir(&log_dir)
        .ok()?
        .filter_map(|e| e.ok())
        .filter(|e| {
            e.file_name()
                .to_string_lossy()
                .starts_with("MSTeams_")
        })
        .max_by_key(|e| e.metadata().and_then(|m| m.modified()).ok())
        .map(|e| e.path())
}

// Tests last: clippy's items_after_test_module rejects anything defined below them.
#[cfg(test)]
mod tests {
    use super::*;

    /// Verbatim from MSTeams_2026-07-26_12-24-08.00.log.
    const REAL_LINE_ZERO: &str = "2026-07-26T11:24:22.599057+01:00 0x00006de8 <INFO> native_modules::UserDataCrossCloudModule: CloudStateChanged: New Cloud State Event: UserDataCloudState total number of users: 1 { user id :ea554d6e27f17268, availability: Available, unread notification count: 0 }";

    #[test]
    fn zero_unread_is_not_unread() {
        assert_eq!(extract_unread_count(REAL_LINE_ZERO), Some(0));
        // The regression: the old check was `contains("true") || contains("1")`, and this
        // real line contains '1' in its timestamp, so it reported unread messages forever.
        assert!(REAL_LINE_ZERO.contains('1'));
    }

    #[test]
    fn nonzero_unread_is_unread() {
        let line = REAL_LINE_ZERO.replace("count: 0", "count: 3");
        assert_eq!(extract_unread_count(&line), Some(3));
    }

    #[test]
    fn multi_digit_count_parses_fully() {
        let line = REAL_LINE_ZERO.replace("count: 0", "count: 42");
        assert_eq!(extract_unread_count(&line), Some(42));
    }

    #[test]
    fn unrelated_lines_are_ignored() {
        assert_eq!(
            extract_unread_count("boot::SingleInstanceService: Creating server mutex"),
            None
        );
        assert_eq!(extract_unread_count(""), None);
    }

    // All call lines below are verbatim from Teams logs of 2026-07-30: the
    // end/ring lines from the incident that motivated id-tracking (a declined
    // incoming call ended the running meeting), the active lines from the
    // field test the same day (ids swapped for consistency). Teams writes
    // THREE NotifyCallActive lines per activation; only the Hfp one carries
    // the call id.
    const ACTIVE_MEETING: &str =
        "HfpVoipCallCoordinatorImpl: NotifyCallActive callId: d84becb7-4285-4d44-9d4d-e61364d07d11causeId: 5478474f-3fc5-444f-b895-4c3d96476fa8";
    const ACTIVE_DUP_CAUSE: &str =
        "CallInfo: NotifyCallActive causeId: 5478474f-3fc5-444f-b895-4c3d96476fa8";
    const ACTIVE_DUP_NO_DEVICE: &str =
        "CallInfo: CallTracker: Calling NotifyCallActive without deviceId, deviceId is empty";
    const INCOMING_RING: &str =
        "HfpVoipCallCoordinatorImpl: reportIncomingCall for callId: c9158e4a-9792-4685-8671-30226038fa77";
    const INCOMING_ENDED: &str =
        "HfpVoipCallCoordinatorImpl: NotifyCallEnded callId: c9158e4a-9792-4685-8671-30226038fa77causeId: bae9fd1b-aece-4163-a999-0db507f8de2c";
    const MEETING_ENDED: &str =
        "HfpVoipCallCoordinatorImpl: NotifyCallEnded callId: d84becb7-4285-4d44-9d4d-e61364d07d11causeId: a879e043-6006-4daf-add5-d816bc102653";
    const MEETING_ENDED_TRACKER: &str =
        "TeamsCallTracker: CallEnded fired: d84becb7-4285-4d44-9d4d-e61364d07d11";

    #[test]
    fn extracts_guid_glued_to_cause_id() {
        assert_eq!(
            extract_call_id(INCOMING_ENDED).as_deref(),
            Some("c9158e4a-9792-4685-8671-30226038fa77")
        );
    }

    #[test]
    fn extracts_guid_from_tracker_line() {
        assert_eq!(
            extract_call_id(MEETING_ENDED_TRACKER).as_deref(),
            Some("d84becb7-4285-4d44-9d4d-e61364d07d11")
        );
    }

    #[test]
    fn rejects_lines_without_guid() {
        assert_eq!(extract_call_id("NotifyCallEnded callId: not-a-guid"), None);
        assert_eq!(extract_call_id("CallEnded without any marker"), None);
    }

    #[test]
    fn declined_incoming_call_does_not_end_running_meeting() {
        // The 2026-07-30 incident, replayed line for line.
        let mut calls = CallState::default();
        assert_eq!(calls.apply(ACTIVE_MEETING), Some(true));
        assert_eq!(calls.apply(INCOMING_RING), None); // ring is not a call line
        assert_eq!(calls.apply(INCOMING_ENDED), None); // must NOT end the meeting
        assert!(calls.in_call());
        assert_eq!(calls.apply(MEETING_ENDED), Some(false));
        // Teams' duplicate end-line for the same call stays silent.
        assert_eq!(calls.apply(MEETING_ENDED_TRACKER), None);
    }

    #[test]
    fn duplicate_idless_active_lines_do_not_arm_legacy_mode() {
        // Field test 2026-07-30: the two CallInfo lines armed legacy mode,
        // which would have let a declined incoming call end the meeting
        // again — the exact bug this state machine exists to fix.
        let mut calls = CallState::default();
        assert_eq!(calls.apply(ACTIVE_MEETING), Some(true));
        assert_eq!(calls.apply(ACTIVE_DUP_CAUSE), None);
        assert_eq!(calls.apply(ACTIVE_DUP_NO_DEVICE), None);
        assert_eq!(calls.apply(INCOMING_ENDED), None);
        assert!(calls.in_call());
        assert_eq!(calls.apply(MEETING_ENDED), Some(false));
    }

    #[test]
    fn id_bearing_active_line_supersedes_legacy_from_same_batch() {
        // Same activation batch, order flipped: an id-less line arms legacy,
        // the id-bearing line for the same call takes over cleanly.
        let mut calls = CallState::default();
        assert_eq!(calls.apply(ACTIVE_DUP_NO_DEVICE), Some(true));
        assert_eq!(calls.apply(ACTIVE_MEETING), None);
        assert_eq!(calls.apply(MEETING_ENDED), Some(false));
    }

    #[test]
    fn duplicate_active_and_end_lines_are_idempotent() {
        let mut calls = CallState::default();
        assert_eq!(calls.apply(ACTIVE_MEETING), Some(true));
        assert_eq!(calls.apply(ACTIVE_MEETING), None);
        assert_eq!(calls.apply(MEETING_ENDED), Some(false));
        assert_eq!(calls.apply(MEETING_ENDED), None);
    }

    #[test]
    fn end_of_never_active_call_alone_stays_silent() {
        // Declined incoming call while NOT in a meeting: nothing to end.
        let mut calls = CallState::default();
        assert_eq!(calls.apply(INCOMING_ENDED), None);
        assert!(!calls.in_call());
    }

    #[test]
    fn legacy_mode_without_ids_keeps_old_semantics() {
        let mut calls = CallState::default();
        assert_eq!(calls.apply("NotifyCallActive (new format?)"), Some(true));
        assert!(calls.in_call());
        // Any end-line closes a legacy call — id'd or not.
        assert_eq!(calls.apply("CallEnded (new format?)"), Some(false));

        assert_eq!(calls.apply("NotifyCallActive (new format?)"), Some(true));
        assert_eq!(calls.apply(INCOMING_ENDED), Some(false));
    }

    #[test]
    fn idless_end_line_is_noise_while_tracking_ids() {
        let mut calls = CallState::default();
        assert_eq!(calls.apply(ACTIVE_MEETING), Some(true));
        assert_eq!(calls.apply("SomeTelemetry: CallEndedReason summary"), None);
        assert!(calls.in_call());
    }

    #[test]
    fn clear_resets_everything() {
        let mut calls = CallState::default();
        calls.apply(ACTIVE_MEETING);
        calls.clear();
        assert!(!calls.in_call());
        // A late end-line for the cleared call is ignored.
        assert_eq!(calls.apply(MEETING_ENDED), None);
    }
}
