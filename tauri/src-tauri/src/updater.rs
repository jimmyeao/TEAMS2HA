//! Automatic updates via `tauri-plugin-updater`.
//!
//! The .NET app auto-updated (ClickOnce); the Tauri port lost that, so every release had
//! to be installed by hand. Updates are signed with a minisign keypair: the public half
//! lives in `tauri.conf.json`, the private half is a GitHub secret used by the release
//! workflow. An update whose signature does not verify is rejected by the plugin, so a
//! compromised release host alone cannot push code to users.
//!
//! # Behaviour
//!
//! Checks shortly after startup and every six hours thereafter, plus on demand from the
//! tray menu. When an update is found it is downloaded, installed, and the app restarts.
//!
//! **Installing restarts the app**, which drops the MQTT connection and briefly marks
//! every entity unavailable in Home Assistant. So a scheduled check *defers* while a
//! meeting is in progress — being restarted mid-call is exactly the kind of thing that
//! makes people turn auto-update off. A check the user asked for explicitly is never
//! deferred.

use crate::app_state::SharedState;
use std::time::Duration;
use tauri::AppHandle;
use tauri_plugin_updater::UpdaterExt;

/// Wait before the first check so startup — monitors, MQTT connect — is not competing
/// with a network round trip.
const FIRST_CHECK_DELAY: Duration = Duration::from_secs(30);
const CHECK_INTERVAL: Duration = Duration::from_secs(6 * 60 * 60);

pub fn start(app: AppHandle, shared: SharedState) {
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(FIRST_CHECK_DELAY).await;
        loop {
            check_and_install(&app, Some(&shared)).await;
            tokio::time::sleep(CHECK_INTERVAL).await;
        }
    });
}

/// Check for an update and install it if there is one.
///
/// `shared` is `Some` for scheduled checks, which then skip while a meeting is active,
/// and `None` for a user-initiated check, which always proceeds.
pub async fn check_and_install(app: &AppHandle, shared: Option<&SharedState>) {
    if let Some(state) = shared {
        if state.read().await.meeting.is_in_meeting {
            log::info!("Updater: meeting in progress, deferring update check");
            return;
        }
    }

    let updater = match app.updater() {
        Ok(u) => u,
        Err(e) => {
            log::warn!("Updater: unavailable ({e})");
            return;
        }
    };

    let update = match updater.check().await {
        Ok(Some(u)) => u,
        Ok(None) => {
            log::info!("Updater: already up to date");
            return;
        }
        Err(e) => {
            // Offline, GitHub unreachable, or no release published yet. Not an error
            // worth bothering the user about; it retries on the next interval.
            log::warn!("Updater: check failed ({e})");
            return;
        }
    };

    log::info!(
        "Updater: {} → {} available, downloading",
        update.current_version,
        update.version
    );

    let mut downloaded = 0usize;
    let result = update
        .download_and_install(
            |chunk, total| {
                downloaded += chunk;
                if let Some(total) = total {
                    log::debug!("Updater: {downloaded}/{total} bytes");
                }
            },
            || log::info!("Updater: download complete, installing"),
        )
        .await;

    match result {
        Ok(()) => {
            log::info!("Updater: installed, restarting");
            // Never returns.
            app.restart();
        }
        Err(e) => log::error!("Updater: install failed ({e})"),
    }
}
