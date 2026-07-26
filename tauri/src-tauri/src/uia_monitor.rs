//! Teams' in-app mute state, read via UI Automation.
//!
//! # Why UIA and not the audio stack
//!
//! Verified on 2026-07-26 against Teams 26183.1903.4892.4448: pressing Teams' own mute
//! button changes **nothing** observable in the audio stack.
//!
//! * `ISimpleAudioVolume` on Teams' capture session — unchanged (that flag is the
//!   volume-mixer per-app mute; muting from the Windows control panel *does* move it,
//!   which is why `wasapi_monitor` is still worth keeping).
//! * `IAudioEndpointVolume` on the capture device — unchanged.
//! * Releasing the capture session — Teams used to drop its session when muted, and
//!   `wasapi_monitor` inferred mute from that. This build keeps the session open, so
//!   that signal is gone.
//! * Teams' own logs — no mute state logged at all.
//! * Teams' local API port (8124) — not listening.
//!
//! The one place the state *is* visible is the meeting window's mute button, whose
//! accessible name flips between `Mute mic` (live) and `Unmute mic` (muted).
//!
//! # Known limitations — please read before relying on this
//!
//! * **Needs a realised Teams window.** With Teams closed to the tray its processes have
//!   no window, nothing appears in the UIA tree, and there is no reading at all.
//! * **Name-based.** A Teams UI rename or a non-English UI breaks it. There is no
//!   `TogglePattern` on the button, so the accessible name is the only available signal.
//! * Reports `Unknown` rather than a guess whenever the button cannot be found, so a
//!   missing reading is never mistaken for "unmuted".

use std::time::Duration;
use tokio::sync::mpsc;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UiaEvent {
    MuteChanged(bool),
    /// No Teams mute button visible (typically: not in a meeting, or no Teams window).
    Unknown,
}

pub fn start(tx: mpsc::Sender<UiaEvent>) {
    // UIA is COM; it needs its own thread with an apartment, like wasapi_monitor.
    std::thread::spawn(move || poll_blocking(tx));
}

/// `Mute mic` → not muted, `Unmute mic` → muted.
///
/// Order matters: "Unmute mic" also contains "mute", so the unmute test must come first.
fn classify(name: &str) -> Option<bool> {
    let lower = name.to_lowercase();
    if lower.contains("unmute") {
        Some(true)
    } else if lower.contains("mute") {
        Some(false)
    } else {
        None
    }
}

#[cfg(windows)]
fn poll_blocking(tx: mpsc::Sender<UiaEvent>) {
    use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};

    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);

        let ctx = match Uia::new() {
            Some(c) => c,
            None => {
                log::error!("UiaMonitor: could not initialise UI Automation; Teams mute will not be detected");
                return;
            }
        };

        let mut last: Option<bool> = None;
        // Holding the element between polls avoids re-walking the whole WebView2 tree
        // every second; it is only re-searched when the cached element goes stale.
        let mut cached = None;

        loop {
            std::thread::sleep(Duration::from_millis(750));

            let reading = ctx.read_mute(&mut cached);
            if reading != last {
                match reading {
                    Some(m) => {
                        log::info!("UiaMonitor: Teams mute → {m}");
                        let _ = tx.blocking_send(UiaEvent::MuteChanged(m));
                    }
                    None => {
                        log::info!("UiaMonitor: no Teams mute button visible");
                        let _ = tx.blocking_send(UiaEvent::Unknown);
                    }
                }
                last = reading;
            }
        }
    }
}

#[cfg(windows)]
struct Uia {
    /// The factory that produced `root` and the conditions. COM refcounts each interface
    /// independently so they would outlive it, but it is held for the lifetime of the
    /// monitor rather than dropped immediately after construction — there is no reason to
    /// let the automation object tear down while we are still querying its elements.
    _automation: windows::Win32::UI::Accessibility::IUIAutomation,
    root: windows::Win32::UI::Accessibility::IUIAutomationElement,
    any: windows::Win32::UI::Accessibility::IUIAutomationCondition,
    buttons: windows::Win32::UI::Accessibility::IUIAutomationCondition,
}

#[cfg(windows)]
impl Uia {
    unsafe fn new() -> Option<Self> {
        use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_ALL};
        use windows::Win32::System::Variant::VARIANT;
        use windows::Win32::UI::Accessibility::{
            CUIAutomation, IUIAutomation, UIA_ButtonControlTypeId, UIA_ControlTypePropertyId,
        };

        let automation: IUIAutomation = CoCreateInstance(&CUIAutomation, None, CLSCTX_ALL).ok()?;
        let root = automation.GetRootElement().ok()?;
        let any = automation.CreateTrueCondition().ok()?;
        let buttons = automation
            .CreatePropertyCondition(
                UIA_ControlTypePropertyId,
                &VARIANT::from(UIA_ButtonControlTypeId.0),
            )
            .ok()?;

        Some(Self {
            _automation: automation,
            root,
            any,
            buttons,
        })
    }

    /// Current mute state, or None when no Teams mute button can be found.
    unsafe fn read_mute(
        &self,
        cached: &mut Option<windows::Win32::UI::Accessibility::IUIAutomationElement>,
    ) -> Option<bool> {
        // Fast path: the button we found last time is usually still there, with only its
        // accessible name changed.
        if let Some(el) = cached.as_ref() {
            if let Ok(name) = el.CurrentName() {
                if let Some(muted) = classify(&name.to_string()) {
                    return Some(muted);
                }
            }
            // Stale (window closed, or the node was replaced) — fall through and re-search.
            *cached = None;
        }

        self.search(cached)
    }

    unsafe fn search(
        &self,
        cached: &mut Option<windows::Win32::UI::Accessibility::IUIAutomationElement>,
    ) -> Option<bool> {
        use windows::Win32::UI::Accessibility::{TreeScope_Children, TreeScope_Descendants};

        let top = self.root.FindAll(TreeScope_Children, &self.any).ok()?;
        let n = top.Length().unwrap_or(0);

        for i in 0..n {
            let win = match top.GetElement(i) {
                Ok(w) => w,
                Err(_) => continue,
            };

            let pid = win.CurrentProcessId().unwrap_or(0) as u32;
            if pid == 0 || !crate::teams_proc::is_teams_pid(pid) {
                continue;
            }

            // Teams has several windows (main, meeting, notifications); only the meeting
            // window carries a mute button, so check them all and take the first hit.
            let found = match win.FindAll(TreeScope_Descendants, &self.buttons) {
                Ok(b) => b,
                Err(_) => continue,
            };

            let bn = found.Length().unwrap_or(0);
            for j in 0..bn {
                let btn = match found.GetElement(j) {
                    Ok(b) => b,
                    Err(_) => continue,
                };
                let name = match btn.CurrentName() {
                    Ok(s) => s.to_string(),
                    Err(_) => continue,
                };
                if let Some(muted) = classify(&name) {
                    *cached = Some(btn);
                    return Some(muted);
                }
            }
        }

        None
    }
}

#[cfg(not(windows))]
fn poll_blocking(_tx: mpsc::Sender<UiaEvent>) {
    log::warn!("UiaMonitor: not supported on this platform");
}

#[cfg(test)]
mod tests {
    use super::classify;

    #[test]
    fn names_observed_from_teams() {
        // Verbatim accessible names captured from Teams 26183.1903.4892.4448.
        assert_eq!(classify("Mute mic"), Some(false));
        assert_eq!(classify("Unmute mic"), Some(true));
    }

    #[test]
    fn unmute_wins_over_the_substring_mute() {
        // "Unmute" contains "mute"; getting this order wrong inverts the sensor.
        assert_eq!(classify("Unmute"), Some(true));
        assert_eq!(classify("UNMUTE MIC"), Some(true));
    }

    #[test]
    fn unrelated_buttons_are_ignored() {
        assert_eq!(classify("Leave"), None);
        assert_eq!(classify("Share content"), None);
        assert_eq!(classify(""), None);
    }
}
