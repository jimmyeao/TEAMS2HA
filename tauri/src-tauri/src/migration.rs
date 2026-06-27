/// Runs once on first launch to clean up any previously installed ClickOnce
/// version of TEAMS2HA.  We silently remove its HKCU uninstall registry entry
/// (which removes it from Add/Remove Programs).  The orphaned binary files in
/// %LOCALAPPDATA%\Apps\2.0\ are harmless and Windows cleans them up itself.
pub fn remove_old_clickonce() {
    #[cfg(windows)]
    {
        use winreg::{enums::*, RegKey};

        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        let uninstall_root = r"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        let uninstall_key = match hkcu.open_subkey(uninstall_root) {
            Ok(k) => k,
            Err(_) => return,
        };

        let mut target: Option<String> = None;

        for name in uninstall_key.enum_keys().flatten() {
            if let Ok(sub) = uninstall_key.open_subkey(&name) {
                let display: String = sub.get_value("DisplayName").unwrap_or_default();
                if display.to_lowercase().contains("teams2ha") {
                    target = Some(name);
                    break;
                }
            }
        }

        if let Some(key_name) = target {
            let full_path = format!("{uninstall_root}\\{key_name}");
            log::info!("Migration: removing old ClickOnce entry '{key_name}'");
            match hkcu.delete_subkey_all(&full_path) {
                Ok(_) => log::info!("Migration: old TEAMS2HA entry removed"),
                Err(e) => log::warn!("Migration: could not remove old entry: {e}"),
            }
        }
    }
}
