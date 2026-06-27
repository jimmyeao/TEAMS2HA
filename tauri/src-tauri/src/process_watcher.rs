use std::time::Duration;
use tokio::sync::mpsc;
use tokio::time::interval;

#[derive(Debug, Clone)]
pub enum ProcessEvent {
    TeamsRunningChanged(bool),
}

pub async fn start(tx: mpsc::Sender<ProcessEvent>) {
    let mut tick = interval(Duration::from_secs(5));
    let mut last_running: Option<bool> = None;

    loop {
        tick.tick().await;
        let running = is_teams_running();
        if Some(running) != last_running {
            last_running = Some(running);
            log::info!("ProcessWatcher: Teams running → {running}");
            let _ = tx.send(ProcessEvent::TeamsRunningChanged(running)).await;
        }
    }
}

fn is_teams_running() -> bool {
    #[cfg(windows)]
    {
        use std::ffi::OsString;
        use std::os::windows::ffi::OsStringExt;
        use windows::Win32::System::Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW,
            PROCESSENTRY32W, TH32CS_SNAPPROCESS,
        };
        use windows::Win32::Foundation::CloseHandle;

        unsafe {
            let snapshot = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
                Ok(h) => h,
                Err(_) => return false,
            };

            let mut entry = PROCESSENTRY32W {
                dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                ..Default::default()
            };

            if Process32FirstW(snapshot, &mut entry).is_err() {
                let _ = CloseHandle(snapshot);
                return false;
            }

            loop {
                let name = OsString::from_wide(
                    &entry.szExeFile[..entry.szExeFile
                        .iter()
                        .position(|&c| c == 0)
                        .unwrap_or(entry.szExeFile.len())],
                )
                .to_string_lossy()
                .to_lowercase();

                if name == "ms-teams.exe" || name == "msteams.exe" {
                    let _ = CloseHandle(snapshot);
                    return true;
                }

                if Process32NextW(snapshot, &mut entry).is_err() {
                    break;
                }
            }

            let _ = CloseHandle(snapshot);
            false
        }
    }
    #[cfg(not(windows))]
    false
}
