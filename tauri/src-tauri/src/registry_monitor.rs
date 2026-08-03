/// Polls Windows Privacy Consent Store registry keys to detect
/// whether Teams is actively using the camera or microphone.
/// LastUsedTimeStop == 0 means the device is currently in use.
///
/// Caveat: when Teams dies (crash, suspend mid-call) without releasing a device, the
/// registry keeps LastUsedTimeStop stuck at 0 — "in use" forever. Trusting that blindly
/// resurrects phantom video-on/in-meeting states after every app restart or system resume
/// (seen 13-07-2026: Sonos muted + office radio blocked by a call that wasn't there).
/// Therefore an 'active' reading only counts after the device has been observed inactive
/// at least once since app start or system resume. Cost: a call that is already ongoing
/// at (re)start goes undetected until its device usage stops — the presence sensor
/// ("Busy") still covers that case.
use std::time::Duration;
use tokio::sync::mpsc;
use tokio::time::{interval, Instant, MissedTickBehavior};

#[derive(Debug, Clone)]
pub enum RegistryEvent {
    CameraChanged(bool),   // true = camera active
    MicChanged(bool),      // true = mic active (used only for meeting detection)
}

pub async fn start(tx: mpsc::Sender<RegistryEvent>) {
    let mut tick = interval(Duration::from_millis(500));
    // No catch-up burst of ticks after a suspend.
    tick.set_missed_tick_behavior(MissedTickBehavior::Delay);
    let mut last_cam: Option<bool> = None;
    let mut last_mic: Option<bool> = None;
    let mut cam_trusted = false;
    let mut mic_trusted = false;
    let mut previous_tick = Instant::now();

    loop {
        tick.tick().await;

        if previous_tick.elapsed() > Duration::from_secs(60) {
            // System resume: a suspend can have left stale "in use" markers behind.
            log::info!("RegistryMonitor: resume detected - re-verifying device state");
            cam_trusted = false;
            mic_trusted = false;
        }
        previous_tick = Instant::now();

        let cam_raw = is_device_active("webcam");
        let mic_raw = is_device_active("microphone");
        if !cam_raw {
            cam_trusted = true;
        }
        if !mic_raw {
            mic_trusted = true;
        }
        if cam_raw && !cam_trusted {
            log::debug!("RegistryMonitor: camera 'in use' but unverified since start/resume - ignoring");
        }
        let cam = cam_raw && cam_trusted;
        let mic = mic_raw && mic_trusted;

        if Some(cam) != last_cam {
            last_cam = Some(cam);
            log::info!("RegistryMonitor: camera active → {cam}");
            let _ = tx.send(RegistryEvent::CameraChanged(cam)).await;
        }
        if Some(mic) != last_mic {
            last_mic = Some(mic);
            log::info!("RegistryMonitor: mic active → {mic}");
            let _ = tx.send(RegistryEvent::MicChanged(mic)).await;
        }
    }
}

// `device` is only read inside the #[cfg(windows)] arm below; on other platforms the whole
// body collapses to a bare `false` and the parameter goes unused.
#[cfg_attr(not(windows), allow(unused_variables))]
fn is_device_active(device: &str) -> bool {
    #[cfg(windows)]
    {
        use winreg::enums::HKEY_CURRENT_USER;
        use winreg::RegKey;

        let path = format!(
            r"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{}\MSTeams_8wekyb3d8bbwe",
            device
        );

        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        match hkcu.open_subkey(&path) {
            Ok(key) => {
                let stop: u64 = key.get_value("LastUsedTimeStop").unwrap_or(1);
                stop == 0 // 0 means still in use
            }
            Err(_) => false,
        }
    }
    #[cfg(not(windows))]
    false
}
