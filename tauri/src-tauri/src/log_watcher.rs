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
    } else if line.contains("unread") || line.contains("UnreadCount") {
        let has_unread = line.contains("true") || line.contains("1");
        let _ = tx.send(LogEvent::UnreadMessages(has_unread)).await;
    }
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
