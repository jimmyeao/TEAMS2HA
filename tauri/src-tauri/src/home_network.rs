use std::time::Duration;
use tokio::sync::{mpsc, watch};
use tokio::time::Instant;

/// Home-network detection based on the MAC address of the default IPv4 gateway.
///
/// The gateway MAC is used instead of SSID or broker reachability because:
/// - it works both on Wi-Fi and wired/docked connections,
/// - reading the SSID on Windows 11 requires location permission,
/// - a VPN tunnel can make the broker reachable away from home, but it can never make a
///   foreign gateway answer ARP with the home router's MAC.
///
/// An empty configured MAC disables the feature (always considered home).

pub fn is_home(configured: &str) -> bool {
    let macs = parse_macs(configured);
    if macs.is_empty() {
        return true; // Feature disabled — behave as before.
    }
    match current_gateway_mac_bytes() {
        Some(mac) => macs.contains(&mac),
        None => false,
    }
}

/// Formatted gateway MAC for the "Use Current Network" button, e.g. "AA:BB:CC:DD:EE:FF".
pub fn current_gateway_mac() -> Option<String> {
    current_gateway_mac_bytes().map(format_mac)
}

const POLL_INTERVAL: Duration = Duration::from_secs(20);

/// A poll iteration that arrives this much later than the poll interval means the process
/// was frozen in between: the machine slept (Modern Standby or S3). Windows delivers no
/// reliable resume event to a suspended desktop app, so the clock gap is the signal.
const RESUME_GAP: Duration = Duration::from_secs(90);

/// How long a single gateway-MAC lookup may take. SendARP normally answers well within a
/// second, but right after a resume (Wi-Fi still re-associating) it can block far longer —
/// without a timeout one hung lookup freezes the poller forever.
const LOOKUP_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Debug, Clone, Copy)]
pub enum HomeEvent {
    /// Regular home/away transition from polling.
    Changed(bool),
    /// The system just woke from suspend; payload = current raw home state. The MQTT
    /// session from before the suspend may be a silently dead TCP connection, so the
    /// service must be rebuilt even when the home state looks unchanged.
    Resumed(bool),
}

/// Spawns the poller. Sends the initial state immediately, then emits on every
/// home/away transition. Flipping to away requires two consecutive misses so a
/// transient ARP failure doesn't drop the connection. After a detected suspend the
/// poller emits `Resumed` with the *raw* current state (strikes reset) so the MQTT
/// connection is rebuilt from scratch.
///
/// The configured MAC arrives via a watch channel (updated on settings save), so the
/// poller never touches the settings file and reacts to a config change immediately.
pub fn start(tx: mpsc::Sender<HomeEvent>, mut mac_rx: watch::Receiver<String>) {
    tauri::async_runtime::spawn(async move {
        let mut last: Option<bool> = None;
        let mut away_strikes: u8 = 0;
        let mut previous_iteration = Instant::now();
        loop {
            let gap = previous_iteration.elapsed();
            let resumed = gap > POLL_INTERVAL + RESUME_GAP;
            previous_iteration = Instant::now();

            let configured = mac_rx.borrow_and_update().clone();
            // SendARP can block — keep it off the async runtime and cap how long we wait.
            let home = match tokio::time::timeout(
                LOOKUP_TIMEOUT,
                tokio::task::spawn_blocking(move || is_home(&configured)),
            )
            .await
            {
                // Treat a crashed lookup task the same as a timed-out one: "not home".
                // The two-strike debounce below absorbs a one-off failure either way.
                Ok(join) => join.unwrap_or_else(|e| {
                    log::warn!("Gateway MAC lookup task failed ({e}); treating as not home");
                    false
                }),
                Err(_) => {
                    log::warn!("Gateway MAC lookup timed out; treating as not home");
                    false
                }
            };

            if resumed {
                // Report the raw state: strikes exist to smooth steady-state flapping,
                // after a resume we want the immediate truth.
                away_strikes = 0;
                last = Some(home);
                log::info!(
                    "System resume detected (poll gap {}s) - forcing MQTT rebuild (home={home})",
                    gap.as_secs()
                );
                if tx.send(HomeEvent::Resumed(home)).await.is_err() {
                    break;
                }
            } else {
                let effective = if home {
                    away_strikes = 0;
                    true
                } else {
                    away_strikes = away_strikes.saturating_add(1);
                    // Until confirmed away, keep reporting the previous state (initially away).
                    if away_strikes >= 2 { false } else { last.unwrap_or(false) }
                };

                if last != Some(effective) {
                    last = Some(effective);
                    log::info!(
                        "Home network state: {}",
                        if effective { "home" } else { "away" }
                    );
                    if tx.send(HomeEvent::Changed(effective)).await.is_err() {
                        break;
                    }
                }
            }

            tokio::select! {
                _ = tokio::time::sleep(POLL_INTERVAL) => {}
                changed = mac_rx.changed() => {
                    if changed.is_err() {
                        break; // Sender dropped — app shutting down.
                    }
                    // Config changed: re-evaluate right away with fresh strikes.
                    away_strikes = 0;
                }
            }
        }
    });
}

fn parse_macs(configured: &str) -> Vec<[u8; 6]> {
    configured
        .split([',', ';', ' '])
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .filter_map(|p| {
            let hex: String = p.chars().filter(char::is_ascii_hexdigit).collect();
            if hex.len() != 12 {
                log::warn!("Ignoring invalid home gateway MAC entry: {p}");
                return None;
            }
            let mut mac = [0u8; 6];
            for (i, byte) in mac.iter_mut().enumerate() {
                *byte = u8::from_str_radix(&hex[i * 2..i * 2 + 2], 16).ok()?;
            }
            Some(mac)
        })
        .collect()
}

fn format_mac(mac: [u8; 6]) -> String {
    mac.iter()
        .map(|b| format!("{b:02X}"))
        .collect::<Vec<_>>()
        .join(":")
}

#[cfg(windows)]
fn current_gateway_mac_bytes() -> Option<[u8; 6]> {
    use windows::Win32::NetworkManagement::IpHelper::{GetBestRoute, SendARP, MIB_IPFORWARDROW};

    unsafe {
        let mut row: MIB_IPFORWARDROW = std::mem::zeroed();
        // Any public IP works — we only need the route's next hop (the LAN gateway).
        let dest = u32::from_ne_bytes([8, 8, 8, 8]);
        if GetBestRoute(dest, Some(0), &mut row) != 0 {
            return None;
        }
        let gateway = row.dwForwardNextHop;
        if gateway == 0 {
            // On-link route (e.g. a VPN tunnel) — nothing to ARP.
            return None;
        }
        let mut mac = [0u8; 8];
        let mut len: u32 = 6;
        if SendARP(gateway, 0, mac.as_mut_ptr() as *mut core::ffi::c_void, &mut len) != 0 || len != 6 {
            return None;
        }
        Some([mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]])
    }
}

#[cfg(not(windows))]
fn current_gateway_mac_bytes() -> Option<[u8; 6]> {
    None
}
