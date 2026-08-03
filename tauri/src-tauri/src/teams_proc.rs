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
/// Process name unconfirmed against a real "new Teams" install — matched by substring like
/// the Windows path above so small naming variations across Teams versions don't silently
/// break it. Verify via Activity Monitor or `osascript -e 'id of app "Microsoft Teams"'` and
/// widen/narrow this match if it doesn't find the process.
#[cfg(target_os = "macos")]
pub fn teams_pid() -> Option<u32> {
    use libproc::proc_pid::{name, ProcType};

    #[allow(deprecated)]
    let pids = libproc::proc_pid::listpids(ProcType::ProcAllPIDS).ok()?;

    pids.into_iter().find(|&pid| {
        // libproc's `name` takes pid_t (i32) even though `listpids` yields u32.
        name(pid as i32)
            .map(|n| {
                let n = n.to_lowercase();
                n.contains("teams")
            })
            .unwrap_or(false)
    })
}

#[cfg(target_os = "macos")]
pub fn is_teams_pid(pid: u32) -> bool {
    teams_pid() == Some(pid)
}
