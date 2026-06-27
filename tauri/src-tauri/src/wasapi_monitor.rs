/// Polls WASAPI capture sessions every 250 ms to detect Teams mute state.
/// Teams signals mute via the Windows-level mute flag on its capture session,
/// which is the same signal that drives the hardware mute LED on the mic.
use std::time::Duration;
use tokio::sync::mpsc;

#[derive(Debug, Clone)]
pub enum WasapiEvent {
    MuteChanged(bool),
}

pub fn start(tx: mpsc::Sender<WasapiEvent>) {
    // WASAPI COM calls must run on a dedicated thread.
    std::thread::spawn(move || {
        poll_wasapi_blocking(tx);
    });
}

#[cfg(windows)]
fn poll_wasapi_blocking(tx: mpsc::Sender<WasapiEvent>) {
    use windows::Win32::Media::Audio::{IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{
        CoInitializeEx, CoCreateInstance, CLSCTX_ALL, COINIT_APARTMENTTHREADED,
    };

    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);

        let enumerator: IMMDeviceEnumerator =
            match CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) {
                Ok(e) => e,
                Err(e) => {
                    log::error!("WASAPI: CoCreateInstance failed: {e}");
                    return;
                }
            };

        let mut last_muted: Option<bool> = None;

        loop {
            std::thread::sleep(Duration::from_millis(250));

            let muted = check_teams_mute(&enumerator);
            if Some(muted) != last_muted {
                last_muted = Some(muted);
                log::info!("WasapiMonitor: mute → {muted}");
                let _ = tx.blocking_send(WasapiEvent::MuteChanged(muted));
            }
        }
    }
}

#[cfg(windows)]
unsafe fn check_teams_mute(
    enumerator: &windows::Win32::Media::Audio::IMMDeviceEnumerator,
) -> bool {
    use windows::core::Interface;
    use windows::Win32::Media::Audio::{
        eCapture, IAudioSessionControl2, IAudioSessionManager2, ISimpleAudioVolume,
        DEVICE_STATE_ACTIVE,
    };
    use windows::Win32::System::Com::CLSCTX_ALL;

    let collection = match enumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE) {
        Ok(c) => c,
        Err(_) => return true,
    };

    let count = match collection.GetCount() {
        Ok(c) => c,
        Err(_) => return true,
    };

    let mut teams_found = false;
    let mut teams_hw_muted = false;

    for i in 0..count {
        let device = match collection.Item(i) {
            Ok(d) => d,
            Err(_) => continue,
        };

        let mgr: IAudioSessionManager2 = match device.Activate(CLSCTX_ALL, None) {
            Ok(m) => m,
            Err(_) => continue,
        };

        let session_enum = match mgr.GetSessionEnumerator() {
            Ok(e) => e,
            Err(_) => continue,
        };

        let count = match session_enum.GetCount() {
            Ok(c) => c,
            Err(_) => continue,
        };

        for j in 0..count {
            let ctrl = match session_enum.GetSession(j) {
                Ok(s) => s,
                Err(_) => continue,
            };

            let ctrl2: IAudioSessionControl2 = match ctrl.cast() {
                Ok(c) => c,
                Err(_) => continue,
            };

            let pid = match ctrl2.GetProcessId() {
                Ok(p) => p,
                Err(_) => continue,
            };

            if !is_teams_pid(pid) {
                continue;
            }

            teams_found = true;

            let vol: ISimpleAudioVolume = match ctrl.cast() {
                Ok(v) => v,
                Err(_) => continue,
            };

            if let Ok(mute) = vol.GetMute() {
                if mute.as_bool() {
                    teams_hw_muted = true;
                }
            }
        }
    }

    !teams_found || teams_hw_muted
}

#[cfg(windows)]
fn is_teams_pid(pid: u32) -> bool {
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Threading::{
        OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
        PROCESS_QUERY_LIMITED_INFORMATION,
    };

    unsafe {
        let handle = match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(h) => h,
            Err(_) => return false,
        };

        let mut buf = vec![0u16; 260];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_WIN32,
            windows::core::PWSTR(buf.as_mut_ptr()),
            &mut size,
        );
        let _ = CloseHandle(handle);

        if ok.is_err() {
            return false;
        }

        let name = OsString::from_wide(&buf[..size as usize])
            .to_string_lossy()
            .to_lowercase();
        name.contains("ms-teams") || name.contains("msteams")
    }
}

#[cfg(not(windows))]
fn poll_wasapi_blocking(_tx: mpsc::Sender<WasapiEvent>) {
    log::warn!("WasapiMonitor: not supported on this platform");
}
