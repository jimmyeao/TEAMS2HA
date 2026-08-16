# macOS port — handoff (2026-08-16)

Prompt for a fresh Claude Code session running on a real Mac, continuing work a
Windows session (no Apple toolchain, couldn't compile/run/test any of this)
just handed off. Read `CLAUDE.md` first for the general project context — this
file only covers what's specific to this handoff.

## Paste this as your first message

> Read `MACOS_HANDOFF.md` at the repo root, then `CLAUDE.md`. I'm testing the
> macOS port of TEAMS2HA on this Mac. Four issues were reported after
> installing the signed v1.3.8 release: Teams-running detection doesn't work,
> home-network detection doesn't work, meeting controls (mute/camera) aren't
> detected, and MQTT doesn't connect after clicking Save in Settings (only
> works after an app restart). Start with the Accessibility permission fix
> since it's the best-understood root cause, get a real build running via
> `npm run tauri dev` from `tauri/`, and work through the rest against live
> Teams meetings and the app's log file.

## State of the repo

`master` already has everything from the Windows session: three PRs merged
(camera-via-UIA fix, dependency bumps), a working signed+notarized macOS CI
release pipeline (`.github/workflows/release.yml`'s `release-macos` job —
Apple secrets are already configured on the repo), and `v1.3.7`/`v1.3.8` tags
already released. The macOS **build and release infrastructure is solid and
tested** — three real signed/notarized dmgs have been produced and verified.
What's untested is the macOS **app itself**, because the Windows session had
no way to run it.

There's also a `.github/workflows/macos-build.yml` (manual `workflow_dispatch`
only) for a quick signed build without cutting a release tag — useful for
testing without spamming version numbers, but for actual debugging just run
`npm run tauri dev` locally, you'll get real stdout/stderr and fast iteration.

## The four issues, in the order I'd tackle them

### 1. Accessibility permission never requested (confirmed root cause of #3)

Teams2HA does not appear at all in System Settings → Privacy & Security →
Accessibility — confirmed by the user. It's not that permission was denied;
the app never triggers macOS's TCC registration/prompt in the first place.

Nothing in the codebase calls `AXIsProcessTrustedWithOptions`. Grep confirms:
`grep -rn "AXIsProcessTrusted" tauri/src-tauri/src` returns nothing. There's
also no `Info.plist` customization or entitlements file, and
`tauri.conf.json` has no `bundle.macOS` section at all — worth checking
whether an `NSAccessibilityUsageDescription` is needed too.

Fix: call `AXIsProcessTrustedWithOptions` with the prompt option
(`kAXTrustedCheckOptionPrompt: true`) early in startup, macOS-gated. Check
what the `axuielement` crate (`Cargo.toml`: `axuielement = "0.9"`, used in
`uia_monitor.rs` via `axuielement::prelude::*`) already exposes for this
before hand-rolling FFI against `ApplicationServices`/`HIServices` — a crate
this specialized may well already wrap the trust check.

The app is a background tray app (`tauri.conf.json`: window `"visible":
false`) with no Dock-visible window at launch — this can make TCC prompts for
headless apps unreliable/easy to miss, so this may need deliberate handling
(e.g. explicitly triggering the check once on first run, or briefly showing
the window) rather than assuming the OS handles it automatically.

### 2. `teamsrunning` detection — stub, not a bug

`tauri/src-tauri/src/process_watcher.rs:76-77`:
```rust
#[cfg(not(windows))]
false
```
Unconditional. Needs a real implementation — `teams_proc.rs`'s
`teams_pid()` already uses `libproc::proc_pid::listpids` successfully for the
AX PID lookup on macOS, so the same approach should work here too (or reuse
`teams_proc::teams_pid()` directly and just check `.is_some()`).

### 3. Meeting controls (mute/camera) not detected

`uia_monitor.rs`'s macOS backend (`poll_blocking` / `find_toolbar_buttons` /
`walk`, all `#[cfg(target_os = "macos")]`, starting around line 306) is
actually implemented — it walks Teams' AX tree via `teams_proc::teams_pid()`
→ `AXUIElement::from_pid()` looking for toolbar buttons matching
`classify`/`classify_camera` (same label-matching logic as Windows). This is
almost certainly just downstream of issue #1 — no Accessibility permission
means every AX call silently returns nothing, which the code correctly
interprets as "no button found" rather than erroring. Fix #1 first, retest
this before touching any of this code.

Also flagged in the code but unverified: `classify_camera`'s macOS button
label strings (`"Turn Camera Off"` / `"Turn Camera On"`) were "verified live
via Accessibility Inspector during a real Teams-for-Mac meeting" per the
comment in `uia_monitor.rs`, but that was from the PR that added this, not
independently re-confirmed. Worth double-checking against your actual Teams
build once #1 is fixed and buttons are visible to the app at all.

### 4. MQTT doesn't connect after Save; works after restart

Read fully through `save_settings` → `connect_mqtt` → `MqttService::connect`
(`lib.rs` and `mqtt_service.rs`) from the Windows session — found no
platform-specific branching and no logic bug by inspection. Both the
live-Save path and the app-startup path call the exact same
`connect_mqtt` function with no macOS-specific code anywhere in that chain.

One thing to rule out first: is the "home network" field in Settings set to
anything? `home_network::is_home()` defaults to "always home" when that
field is empty (the common case), but if it has a value, `is_home` always
evaluates false on macOS today, since `current_gateway_macs()` is a stub
returning `Vec::new()` (see issue below) — every MAC-address check fails —
which would silently skip connecting after Save
(`"Settings saved; not on the home network - MQTT stays paused."`
in the log). If that's not it, this needs the actual log:

`~/Library/Application Support/com.jimmyeao.Teams2HA/teams2ha.log`

Check it immediately after clicking Save for `MQTT connect failed` or
anything unexpected from the `rumqttc` eventloop. If nothing useful shows up
even with `RUST_LOG=debug`, this may be a macOS network-permission thing —
first outbound connections from a newly-installed, hardened-runtime, tray app
can trigger a macOS "Local Network" access prompt that's easy to miss for a
backgrounded app, similar in spirit to issue #1's Accessibility trap. Worth
checking System Settings → Privacy & Security → Local Network too.

### 5. (Bonus, lower priority) Home-network / gateway MAC detection — stub

`home_network.rs:271-274`:
```rust
#[cfg(not(windows))]
fn current_gateway_macs() -> Vec<[u8; 6]> {
    Vec::new()
}
```
Needs a real macOS implementation if home-gating is wanted there — route
table + ARP, likely via `route get default` / `SystemConfiguration`
framework, or shelling out to `route`/`arp`. Not urgent: leaving the "home
network" field empty in Settings already disables the feature cleanly
(`is_home` returns `true` unconditionally), so this only matters if someone
actually wants to use home-gating on macOS.

## Testing loop

`cd tauri && npm run tauri dev` gives you a real running build with live
stdout/stderr — much faster than the CI round-trip. Once a fix looks good,
either push a commit and let the user decide when to cut a release tag, or
dispatch `macos-build.yml` manually (`gh workflow run "macOS Build (manual)"
--repo Jimmyeao/TEAMS2HA`) for a signed dmg without bumping the version.

Commit directly to `master` if you're continuing the same pattern this
session used (the user has been explicit each time about wanting direct
pushes rather than PRs) — but confirm with the user first if that's still
what they want on a fresh session, don't assume silently.
