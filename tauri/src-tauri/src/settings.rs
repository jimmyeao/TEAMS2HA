use anyhow::Result;
use directories::ProjectDirs;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub mqtt_address: String,
    pub mqtt_port: u16,
    pub mqtt_username: String,
    pub mqtt_password: String,
    pub sensor_prefix: String,
    pub use_tls: bool,
    pub ignore_cert_errors: bool,
    pub use_websockets: bool,
    pub run_at_boot: bool,
    pub run_minimized: bool,
    pub theme: String,
    pub color_scheme: String,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            mqtt_address: String::new(),
            mqtt_port: 1883,
            mqtt_username: String::new(),
            mqtt_password: String::new(),
            sensor_prefix: hostname(),
            use_tls: false,
            ignore_cert_errors: false,
            use_websockets: false,
            run_at_boot: false,
            run_minimized: false,
            theme: "dark".into(),
            color_scheme: "DeepPurple / Lime".into(),
        }
    }
}

impl Settings {
    pub fn load() -> Self {
        match Self::try_load() {
            Ok(s) => s,
            Err(e) => {
                log::warn!("Could not load settings, using defaults: {e}");
                Self::default()
            }
        }
    }

    fn try_load() -> Result<Self> {
        let path = settings_path()?;
        if !path.exists() {
            return Ok(Self::default());
        }
        let json = fs::read_to_string(&path)?;
        Ok(serde_json::from_str(&json)?)
    }

    pub fn save(&self) -> Result<()> {
        let path = settings_path()?;
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(self)?;
        fs::write(&path, json)?;
        self.apply_run_at_boot();
        Ok(())
    }

    #[cfg(windows)]
    fn apply_run_at_boot(&self) {
        use winreg::enums::HKEY_CURRENT_USER;
        use winreg::RegKey;
        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        let run_key = hkcu
            .open_subkey_with_flags(
                r"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                winreg::enums::KEY_SET_VALUE,
            )
            .ok();
        if let Some(key) = run_key {
            if self.run_at_boot {
                if let Ok(exe) = std::env::current_exe() {
                    let _ = key.set_value("Teams2HA", &exe.to_string_lossy().as_ref());
                }
            } else {
                let _ = key.delete_value("Teams2HA");
            }
        }
    }

    #[cfg(not(windows))]
    fn apply_run_at_boot(&self) {}
}

pub fn is_first_run() -> bool {
    settings_path().map(|p| !p.exists()).unwrap_or(false)
}

fn settings_path() -> Result<PathBuf> {
    let dirs = ProjectDirs::from("com", "jimmyeao", "Teams2HA")
        .ok_or_else(|| anyhow::anyhow!("Cannot determine app data directory"))?;
    Ok(dirs.data_local_dir().join("settings.json"))
}

fn hostname() -> String {
    hostname::get()
        .ok()
        .and_then(|h| h.into_string().ok())
        .unwrap_or_else(|| "teams2ha".into())
}
