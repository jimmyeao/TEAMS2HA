use std::fs::File;
use std::io::{BufRead, BufReader, Seek, SeekFrom};
use std::path::PathBuf;
use std::time::Duration;
use tokio::sync::mpsc;
use tokio::time::interval;

#[derive(Debug, Clone)]
pub enum LogEvent {
    MuteChanged(bool),
    MeetingChanged(bool),
    PresenceChanged(String),
    UnreadMessages(bool),
}

pub fn start(tx: mpsc::Sender<LogEvent>) {
    tauri::async_runtime::spawn(poll_loop(tx));
}

async fn poll_loop(tx: mpsc::Sender<LogEvent>) {
    let mut current_file: Option<PathBuf> = None;
    let mut file_handle: Option<(BufReader<File>, u64)> = None;
    let mut in_call = false;

    let mut tick = interval(Duration::from_millis(250));

    loop {
        tick.tick().await;

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
                        process_line(line.trim(), &tx, &mut in_call).await;
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

async fn process_line(line: &str, tx: &mpsc::Sender<LogEvent>, in_call: &mut bool) {
    // NOTE: modern Teams (MSTeams_8wekyb3d8bbwe) does not log mute state at all — a search
    // of its logs finds "mute" only in `HFP_VCC_UNMUTE_FIX` and `server mutex`. This branch
    // is retained for the classic Teams log fallback in find_latest_log(); on current Teams
    // the mute signal comes solely from wasapi_monitor. Do not assume this covers mute.
    if line.contains("NotifyCallMuteStateChanged") {
        let muted = line.contains("muteState: true");
        log::debug!("LogWatcher: mute → {muted}");
        let _ = tx.send(LogEvent::MuteChanged(muted)).await;
    } else if line.contains("NotifyCallActive") {
        log::info!("LogWatcher: call active");
        *in_call = true;
        let _ = tx.send(LogEvent::MeetingChanged(true)).await;
    } else if line.contains("CallEnded") || line.contains("NotifyCallEnded") {
        log::info!("LogWatcher: call ended");
        *in_call = false;
        let _ = tx.send(LogEvent::MeetingChanged(false)).await;
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
}
