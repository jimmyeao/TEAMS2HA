use std::time::Duration;
use tokio::sync::{mpsc, watch};
use tokio::time::Instant;

/// Home-network detection based on the MAC address(es) of the default IPv4 gateway(s).
///
/// The gateway MAC is used instead of SSID or broker reachability because:
/// - it works both on Wi-Fi and wired/docked connections,
/// - reading the SSID on Windows 11 requires location permission,
/// - a VPN tunnel can make the broker reachable away from home, but it can never make a
///   foreign gateway answer ARP with the home router's MAC.
///
/// We enumerate *every* default-route (`0.0.0.0/0`) gateway rather than resolving the best
/// route to a public IP: on a multi-homed machine (Ethernet + Tailscale/VPN, dock, etc.) an
/// exit-node or VPN installs more-specific `/1` split routes that hijack "best route to
/// 8.8.8.8", pointing it at a tunnel with no ARP-able LAN gateway. The physical NIC's
/// `0.0.0.0/0` route (and its real home-router next hop) survives that, so scanning all
/// default routes finds the home gateway even while a VPN is up.
///
/// An empty configured MAC disables the feature (always considered home).
pub fn is_home(configured: &str) -> bool {
    let macs = parse_macs(configured);
    if macs.is_empty() {
        return true; // Feature disabled — behave as before.
    }
    let current = current_gateway_macs();
    current.iter().any(|c| macs.contains(c))
}

/// Formatted gateway MAC(s) for the "Use Current Network" button, comma-separated when the
/// machine has multiple physical uplinks (e.g. "AA:BB:CC:DD:EE:FF, 11:22:33:44:55:66").
pub fn current_gateway_mac() -> Option<String> {
    let macs = current_gateway_macs();
    if macs.is_empty() {
        return None;
    }
    Some(
        macs.iter()
            .map(|m| format_mac(*m))
            .collect::<Vec<_>>()
            .join(", "),
    )
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

/// GetIp*Table return this when the passed buffer is null/too small; the required byte
/// count comes back in the size out-param.
#[cfg(windows)]
const ERROR_INSUFFICIENT_BUFFER: u32 = 122;

/// Fetch a variable-length IP-helper table using the standard size-then-fill dance and hand
/// its rows to `f` as a slice. `table_of` locates the `[Row; 1]` flexible array inside the
/// allocated buffer. Returns whatever `f` produces, or `None` if either call fails.
#[cfg(windows)]
unsafe fn with_ip_table<Table, Row, R>(
    get: unsafe fn(Option<*mut Table>, *mut u32, bool) -> u32,
    table_of: unsafe fn(*mut Table) -> (u32, *const Row),
    f: impl FnOnce(&[Row]) -> R,
) -> Option<R> {
    let mut size: u32 = 0;
    if get(None, &mut size, false) != ERROR_INSUFFICIENT_BUFFER || size == 0 {
        return None;
    }
    let mut buf = vec![0u8; size as usize];
    let table = buf.as_mut_ptr() as *mut Table;
    if get(Some(table), &mut size, false) != 0 {
        return None;
    }
    let (num, first) = table_of(table);
    Some(f(std::slice::from_raw_parts(first, num as usize)))
}

#[cfg(windows)]
fn current_gateway_macs() -> Vec<[u8; 6]> {
    use std::collections::HashMap;
    use windows::Win32::NetworkManagement::IpHelper::{
        GetIpAddrTable, GetIpForwardTable, SendARP, MIB_IPADDRTABLE, MIB_IPFORWARDTABLE,
    };

    unsafe {
        // Map interface index -> that interface's own IPv4 address. SendARP needs the source
        // IP of the interface the request goes out on: on a multi-homed machine (Ethernet +
        // Tailscale/VPN + dock) passing srcip 0 makes Windows fail source selection and
        // return ERROR_BAD_NET_NAME, so the gateway MAC never resolves. Feeding the correct
        // per-interface source fixes exactly that.
        let if_src: HashMap<u32, u32> = with_ip_table::<MIB_IPADDRTABLE, _, _>(
            GetIpAddrTable,
            |t| ((*t).dwNumEntries, (*t).table.as_ptr()),
            |rows| {
                rows.iter()
                    .filter(|r| r.dwAddr != 0)
                    .map(|r| (r.dwIndex, r.dwAddr))
                    .collect()
            },
        )
        .unwrap_or_default();

        with_ip_table::<MIB_IPFORWARDTABLE, _, _>(
            GetIpForwardTable,
            |t| ((*t).dwNumEntries, (*t).table.as_ptr()),
            |rows| {
                let mut seen_gateways = std::collections::HashSet::new();
                let mut macs: Vec<[u8; 6]> = Vec::new();
                for row in rows {
                    // Only default routes (0.0.0.0/0). A VPN/exit-node hijacks traffic with
                    // more-specific /1 routes, but the physical LAN gateway keeps the real /0,
                    // so scanning /0 routes finds the home gateway even while a VPN is up.
                    if row.dwForwardDest != 0 || row.dwForwardMask != 0 {
                        continue;
                    }
                    let gateway = row.dwForwardNextHop;
                    // Skip on-link default routes (next hop 0.0.0.0, e.g. a tunnel) and
                    // gateways already ARP'd (two NICs on one subnet share a next hop).
                    if gateway == 0 || !seen_gateways.insert(gateway) {
                        continue;
                    }
                    let src = if_src.get(&row.dwForwardIfIndex).copied().unwrap_or(0);
                    let mut mac = [0u8; 8];
                    let mut len: u32 = 6;
                    if SendARP(gateway, src, mac.as_mut_ptr() as *mut core::ffi::c_void, &mut len)
                        == 0
                        && len == 6
                    {
                        let m = [mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]];
                        if !macs.contains(&m) {
                            macs.push(m);
                        }
                    }
                }
                macs
            },
        )
        .unwrap_or_default()
    }
}

#[cfg(not(windows))]
fn current_gateway_macs() -> Vec<[u8; 6]> {
    Vec::new()
}
