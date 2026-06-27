/// Polls Windows Privacy Consent Store registry keys to detect
/// whether Teams is actively using the camera or microphone.
/// LastUsedTimeStop == 0 means the device is currently in use.
use std::time::Duration;
use tokio::sync::mpsc;
use tokio::time::interval;

#[derive(Debug, Clone)]
pub enum RegistryEvent {
    CameraChanged(bool),   // true = camera active
    MicChanged(bool),      // true = mic active (used only for meeting detection)
}

pub async fn start(tx: mpsc::Sender<RegistryEvent>) {
    let mut tick = interval(Duration::from_millis(500));
    let mut last_cam: Option<bool> = None;
    let mut last_mic: Option<bool> = None;

    loop {
        tick.tick().await;

        let cam = is_device_active("webcam");
        let mic = is_device_active("microphone");

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
