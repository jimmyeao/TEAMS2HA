//! Diagnostic probe: dump every button Microsoft Teams for Mac exposes via the
//! Accessibility API, so `uia_monitor`'s macOS `classify`/`classify_camera` label strings
//! can be checked against a real build instead of guessed at.
//!
//! Run it during a call and toggle mute/camera:
//!
//!     cargo run --example ax_probe
//!
//! Requires Accessibility permission already granted to whatever process runs this
//! (Terminal/IDE, since it's launched from a shell rather than as its own bundled app).
//!
//! Confirmed live on 2026-08-16: Teams' meeting toolbar buttons carry their accessible name
//! in `AXDescription` (e.g. `"Mute mic"`, `"Turn camera off"`), matching what `classify`/
//! `classify_camera` already expect. The one surprise: right after a meeting starts, its
//! WebView content's AX tree can take several minutes to populate (a childless "Web content"
//! AXGroup until then) — a Chromium/WebView2-on-macOS platform quirk, not a bug in this
//! codebase. Neither `AXManualAccessibility` (Electron's trick) nor `AXEnhancedUserInterface`
//! (VoiceOver's) sped it up when tried against a real call; it just needs patience — the
//! regular 750ms poll in `uia_monitor` picks it up on its own once Chromium finishes.

use axuielement::ax_attribute::attributes::{
    AX_DESCRIPTION_ATTRIBUTE, AX_ROLE_ATTRIBUTE, AX_TITLE_ATTRIBUTE, AX_WINDOWS_ATTRIBUTE,
};
use axuielement::AXUIElement;
use std::time::Duration;

fn teams_pid() -> Option<u32> {
    use libproc::proc_pid::{pidpath, ProcType};

    #[allow(deprecated)]
    let pids = libproc::proc_pid::listpids(ProcType::ProcAllPIDS).ok()?;

    pids.into_iter().find(|&pid| {
        pidpath(pid as i32)
            .map(|p| p.to_lowercase().contains("microsoft teams.app/contents/macos/"))
            .unwrap_or(false)
    })
}

fn walk(el: &AXUIElement, depth: u32) {
    if depth > 40 {
        return;
    }

    let role = el.string_attribute(AX_ROLE_ATTRIBUTE).ok().flatten();
    let title = el
        .string_attribute(AX_TITLE_ATTRIBUTE)
        .ok()
        .flatten()
        .filter(|s| !s.is_empty());
    let desc = el
        .string_attribute(AX_DESCRIPTION_ATTRIBUTE)
        .ok()
        .flatten()
        .filter(|s| !s.is_empty());

    if role.as_deref() == Some("AXButton") || title.is_some() || desc.is_some() {
        println!(
            "{}role={:?} title={:?} desc={:?}",
            "  ".repeat(depth as usize),
            role,
            title,
            desc,
        );
    }

    if let Ok(children) = el.children() {
        for child in &children {
            walk(child, depth + 1);
        }
    }
}

fn main() {
    let Some(pid) = teams_pid() else {
        println!("Microsoft Teams not found running.");
        return;
    };
    println!("Teams pid: {pid}");

    loop {
        let Some(app) = AXUIElement::from_pid(pid as i32) else {
            println!("AXUIElement::from_pid failed");
            std::thread::sleep(Duration::from_secs(2));
            continue;
        };
        // Chromium/WebView content on macOS keeps its AX tree unpopulated (a childless
        // "Web content" AXGroup) until something explicitly asks for it. AXManualAccessibility
        // (Electron's hack) didn't work; try AXEnhancedUserInterface, the attribute VoiceOver
        // itself sets on NSApplication to turn on full AX population.
        println!(
            "set_bool_attribute(AXEnhancedUserInterface) → {:?}",
            app.set_bool_attribute("AXEnhancedUserInterface", true)
        );
        let windows = app.element_array_attribute(AX_WINDOWS_ATTRIBUTE).unwrap_or_default();
        println!("--- {} window(s) ---", windows.len());
        for (i, w) in windows.iter().enumerate() {
            println!("window {i}:");
            walk(w, 1);
        }
        std::thread::sleep(Duration::from_secs(3));
    }
}
