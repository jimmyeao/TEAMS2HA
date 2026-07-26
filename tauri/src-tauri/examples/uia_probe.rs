//! Diagnostic probe: can UI Automation see Teams' mute button state?
//!
//! WASAPI cannot. Verified on 2026-07-26 during real calls: the per-session mute
//! (`ISimpleAudioVolume`) and the capture endpoint mute (`IAudioEndpointVolume`) both
//! stay `false` across Teams mute toggles, and Windows offers no mic-mute control of
//! its own during a Teams call — so there is no OS-level mute flag to read.
//!
//! This probe walks the UIA tree of every ms-teams.exe window once a second and prints
//! any button whose name mentions mute, along with its toggle state. Run it during a
//! call and toggle mute:
//!
//!     cargo run --example uia_probe
//!
//! If the printed state tracks the toggles, UIA is a viable mute source and this logic
//! can move into a monitor. If nothing shows up, dump mode (below) lists every button
//! found, so we can see what Teams does expose.

use std::ffi::OsString;
use std::os::windows::ffi::OsStringExt;
use windows::core::Interface;
use windows::Win32::Foundation::CloseHandle;
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_APARTMENTTHREADED,
};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::UI::Accessibility::{
    CUIAutomation, IUIAutomation, IUIAutomationElement, IUIAutomationTogglePattern,
    TreeScope_Children, TreeScope_Descendants, UIA_ButtonControlTypeId,
    UIA_ControlTypePropertyId, UIA_TogglePatternId,
};

fn main() {
    // `--dump` lists every button in the Teams tree, not just mute-ish ones.
    let dump = std::env::args().any(|a| a == "--dump");
    unsafe { run(dump) }
}

unsafe fn run(dump: bool) {
    if CoInitializeEx(None, COINIT_APARTMENTTHREADED).is_err() {
        eprintln!("CoInitializeEx failed");
        return;
    }

    let automation: IUIAutomation = match CoCreateInstance(&CUIAutomation, None, CLSCTX_ALL) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("Cannot create UIAutomation: {e}");
            return;
        }
    };

    let root = match automation.GetRootElement() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("GetRootElement failed: {e}");
            return;
        }
    };

    let true_cond = automation.CreateTrueCondition().expect("true condition");
    let button_cond = automation
        .CreatePropertyCondition(
            UIA_ControlTypePropertyId,
            &VARIANT::from(UIA_ButtonControlTypeId.0),
        )
        .expect("button condition");

    println!("Probing for Teams windows. Toggle mute in a call; Ctrl+C to stop.\n");

    loop {
        let windows_found = match root.FindAll(TreeScope_Children, &true_cond) {
            Ok(w) => w,
            Err(e) => {
                eprintln!("FindAll(children) failed: {e}");
                std::thread::sleep(std::time::Duration::from_secs(1));
                continue;
            }
        };

        let count = windows_found.Length().unwrap_or(0);
        let mut teams_windows = 0;
        let mut hits = 0;

        for i in 0..count {
            let win = match windows_found.GetElement(i) {
                Ok(w) => w,
                Err(_) => continue,
            };

            // CurrentProcessId is i32 in these bindings.
            let pid = win.CurrentProcessId().unwrap_or(0) as u32;
            if pid == 0 || !is_teams_pid(pid) {
                continue;
            }
            teams_windows += 1;

            let win_name = bstr(win.CurrentName().ok());
            println!("-- Teams window (pid {pid}): {win_name}");

            let buttons = match win.FindAll(TreeScope_Descendants, &button_cond) {
                Ok(b) => b,
                Err(e) => {
                    println!("   button search failed: {e}");
                    continue;
                }
            };

            let bcount = buttons.Length().unwrap_or(0);
            println!("   {bcount} buttons in tree");

            for j in 0..bcount {
                let btn = match buttons.GetElement(j) {
                    Ok(b) => b,
                    Err(_) => continue,
                };
                let name = bstr(btn.CurrentName().ok());
                let interesting = name.to_lowercase().contains("mute");

                if interesting || dump {
                    let state = toggle_state(&btn);
                    let marker = if interesting { "*" } else { " " };
                    println!("   {marker} [{state}] {name}");
                    if interesting {
                        hits += 1;
                    }
                }
            }
        }

        if teams_windows == 0 {
            println!("(no ms-teams.exe windows in the UIA tree)");
        } else if hits == 0 && !dump {
            println!("(no button mentioning 'mute' — re-run with --dump to list all)");
        }

        println!();
        std::thread::sleep(std::time::Duration::from_secs(1));
    }
}

/// TogglePattern state if the element supports it, else "-".
unsafe fn toggle_state(el: &IUIAutomationElement) -> String {
    match el.GetCurrentPattern(UIA_TogglePatternId) {
        Ok(p) => match p.cast::<IUIAutomationTogglePattern>() {
            Ok(tp) => match tp.CurrentToggleState() {
                Ok(s) => format!("toggle={}", s.0),
                Err(_) => "toggle=?".into(),
            },
            Err(_) => "-".into(),
        },
        Err(_) => "-".into(),
    }
}

fn bstr(b: Option<windows::core::BSTR>) -> String {
    b.map(|s| s.to_string()).unwrap_or_default()
}

fn is_teams_pid(pid: u32) -> bool {
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
