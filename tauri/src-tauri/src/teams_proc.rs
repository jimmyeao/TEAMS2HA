//! Shared helper for identifying Microsoft Teams processes.
//!
//! Lives in its own module because both `wasapi_monitor` (audio sessions) and
//! `uia_monitor` (windows) need it. It was previously private to wasapi_monitor;
//! copying it would have been the third place in this codebase where duplicated
//! logic drifted apart.

/// True when `pid` belongs to a Microsoft Teams executable.
///
/// Matches on the image name rather than a fixed path: Teams is an MSIX package whose
/// install directory carries the version (…\MSTeams_26183.1903.4892.4448_x64__…), so it
/// changes on every auto-update.
#[cfg(windows)]
pub fn is_teams_pid(pid: u32) -> bool {
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

#[cfg(not(any(windows, target_os = "macos")))]
pub fn is_teams_pid(_pid: u32) -> bool {
    false
}

/// PID of the running Teams process, or `None` if it isn't running.
///
/// Unlike Windows' `is_teams_pid` (which checks a candidate pid handed to it while walking
/// UI Automation's window tree), the macOS Accessibility API takes a pid to construct an
/// `AXUIElement` for an application rather than yielding one while walking windows — so
/// `uia_monitor`'s macOS backend needs the pid up front instead.
///
/// Verified live against a real Microsoft Teams for Mac install: matches on the executable's
/// full path (via `pidpath`) rather than the short process name. Matching on name alone was
/// tried first and is broken — every process's short name (`libproc::proc_pid::name`) is
/// truncated/simple enough that our own process, `teams2ha`, contains "teams" as a substring
/// and was winning the match instead of the real Teams process, silently reading Teams2HA's
/// own (buttonless) app window as "the Teams window" and never finding mute/camera. The
/// bundle's `Contents/MacOS/` path segment also excludes Teams' many Helpers/XPCServices
/// child processes (WebView, ModuleHost, notification center, …), which don't own the
/// meeting-toolbar window `uia_monitor` is looking for.
#[cfg(target_os = "macos")]
pub fn teams_pid() -> Option<u32> {
    use libproc::proc_pid::{pidpath, ProcType};

    #[allow(deprecated)]
    let pids = libproc::proc_pid::listpids(ProcType::ProcAllPIDS).ok()?;

    pids.into_iter().find(|&pid| {
        // libproc's `pidpath` takes pid_t (i32) even though `listpids` yields u32.
        pidpath(pid as i32)
            .map(|p| p.to_lowercase().contains("microsoft teams.app/contents/macos/"))
            .unwrap_or(false)
    })
}

// Kept for parity with the Windows API shape (and for any future macOS caller that has a
// candidate pid in hand already, e.g. a Phase 2 registry_monitor backend) — nothing calls it
// yet, since uia_monitor's macOS backend uses `teams_pid()` directly instead.
#[cfg(target_os = "macos")]
#[allow(dead_code)]
pub fn is_teams_pid(pid: u32) -> bool {
    teams_pid() == Some(pid)
}
