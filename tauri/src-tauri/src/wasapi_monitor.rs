/// Polls WASAPI capture sessions every 250 ms to detect Teams mute state.
/// Teams signals mute via the Windows-level mute flag on its capture session,
/// which is the same signal that drives the hardware mute LED on the mic.
use std::time::Duration;
use tokio::sync::mpsc;

#[derive(Debug, Clone)]
pub enum WasapiEvent {
    MuteChanged(bool),
}

pub fn start(tx: mpsc::Sender<WasapiEvent>) {
    // WASAPI COM calls must run on a dedicated thread.
    std::thread::spawn(move || {
        poll_wasapi_blocking(tx);
    });
}

#[cfg(windows)]
fn poll_wasapi_blocking(tx: mpsc::Sender<WasapiEvent>) {
    use windows::Win32::Media::Audio::{IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{
        CoInitializeEx, CoCreateInstance, CLSCTX_ALL, COINIT_APARTMENTTHREADED,
    };

    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);

        let enumerator: IMMDeviceEnumerator =
            match CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) {
                Ok(e) => e,
                Err(e) => {
                    log::error!("WASAPI: CoCreateInstance failed: {e}");
                    return;
                }
            };

        let mut last_muted: Option<bool> = None;
        let mut session_present: Option<bool> = None;
        // Error state is logged on transition only — at 4 polls/sec, logging every
        // failure would flood the file, but silence made "unchanged" and "could not
        // read" indistinguishable when diagnosing mute detection.
        let mut in_error = false;

        loop {
            std::thread::sleep(Duration::from_millis(250));

            let reading = check_teams_mute(&enumerator);
            match (&reading, in_error) {
                (Err(()), false) => {
                    log::warn!("WasapiMonitor: audio API read failing - holding last state");
                    in_error = true;
                }
                (Ok(_), true) => {
                    log::info!("WasapiMonitor: audio API readable again");
                    in_error = false;
                }
                _ => {}
            }

            // Map a reading to a mute state.
            //
            // ┌─────────────────────┬────────────────────────────────────────────────┐
            // │ Err(())             │ the audio API failed: no measurement at all,   │
            // │                     │ so hold the last known state rather than       │
            // │                     │ inventing a mute flank mid-call.               │
            // │ Ok(None)            │ Teams has no capture session → MUTED.          │
            // │ Ok(Some(reading))   │ a session exists → use its mute flags.         │
            // └─────────────────────┴────────────────────────────────────────────────┘
            //
            // *** Ok(None) => Some(true) is load-bearing. Do not "simplify" it. ***
            //
            // Teams' in-app mute is not observable as a flag. Verified 2026-07-26
            // across several real calls: the per-session mute (ISimpleAudioVolume)
            // and the capture endpoint mute (IAudioEndpointVolume) both stay false
            // through mute toggles, Teams logs no mute state, and Windows offers no
            // mic-mute control of its own during a Teams call. What *is* observable
            // is that Teams releases its capture session when muted — so "Teams is
            // not capturing" is precisely how we know the mic is muted.
            //
            // Commit e5c28ed reclassified this case as "no reading available, hold
            // the last state". That reads like an obvious correctness fix — absence
            // of data really isn't the same as a false value — and it did fix a real
            // bug in the Err arm above, which previously also returned "muted" and
            // so produced false flanks on a transient COM hiccup. But applying the
            // same reasoning to Ok(None) removed the only working mute signal, and
            // mute detection silently stopped working altogether.
            let muted = match &reading {
                Err(()) => None,
                Ok(None) => Some(true),
                Ok(Some(r)) => Some(r.muted()),
            };

            // Session appearing/disappearing is logged separately from the mute state
            // so the log shows the underlying cause, not just the conclusion.
            let present = match &reading {
                Ok(Some(_)) => Some(true),
                Ok(None) => Some(false),
                Err(()) => None,
            };
            if let Some(p) = present {
                if session_present != Some(p) {
                    session_present = Some(p);
                    match &reading {
                        Ok(Some(r)) => {
                            log::info!("WasapiMonitor: Teams capture session open ({r})")
                        }
                        _ => log::info!("WasapiMonitor: Teams capture session closed"),
                    }
                }
            }

            if let Some(m) = muted {
                if Some(m) != last_muted {
                    last_muted = Some(m);
                    match &reading {
                        // Both flag components are logged: if Teams ever does start
                        // reflecting mute in one of them, this is where it will show.
                        Ok(Some(r)) => log::info!("WasapiMonitor: mute → {m} ({r})"),
                        _ => log::info!("WasapiMonitor: mute → {m} (no Teams capture session)"),
                    }
                    let _ = tx.blocking_send(WasapiEvent::MuteChanged(m));
                }
            }
        }
    }
}

/// The two independent mute flags that can hide a microphone, read together because
/// they mean different things:
///
/// * `session` — `ISimpleAudioVolume` on Teams' own capture session. This is the
///   per-app slider in the Windows volume mixer. Teams' in-app mute button does **not**
///   touch it; verified during a real call on 2026-07-26 where it stayed `false`
///   across several mute toggles.
/// * `endpoint` — `IAudioEndpointVolume` on the capture *device*. This is what the
///   Windows 11 mic-mute control drives, and Teams keeps it in sync via the VoIP call
///   coordinator (see `HfpVoipCallCoordinatorProvider` in the Teams logs).
///
/// Either being set means the far end hears nothing, so the reported state is the OR.
#[cfg(windows)]
#[derive(Debug, Clone, Copy)]
pub struct MuteReading {
    pub session: bool,
    pub endpoint: bool,
}

#[cfg(windows)]
impl MuteReading {
    fn muted(&self) -> bool {
        self.session || self.endpoint
    }
}

#[cfg(windows)]
impl std::fmt::Display for MuteReading {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "session={} endpoint={}", self.session, self.endpoint)
    }
}

/// Ok(Some(reading)) when a Teams capture session was measured, Ok(None) when no
/// Teams session exists, Err(()) when the audio API failed.
///
/// Note the caller treats `Ok(None)` as **muted**, not as missing data — Teams
/// releasing its capture session is the mute signal. See the table in the poll loop
/// before changing this contract.
#[cfg(windows)]
unsafe fn check_teams_mute(
    enumerator: &windows::Win32::Media::Audio::IMMDeviceEnumerator,
) -> Result<Option<MuteReading>, ()> {
    use windows::core::Interface;
    use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
    use windows::Win32::Media::Audio::{
        eCapture, IAudioSessionControl2, IAudioSessionManager2, ISimpleAudioVolume,
        DEVICE_STATE_ACTIVE,
    };
    use windows::Win32::System::Com::CLSCTX_ALL;

    let collection = enumerator
        .EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE)
        .map_err(|_| ())?;
    let count = collection.GetCount().map_err(|_| ())?;

    let mut teams_found = false;
    let mut session_muted = false;
    let mut endpoint_muted = false;

    for i in 0..count {
        let device = match collection.Item(i) {
            Ok(d) => d,
            Err(_) => continue,
        };

        let mgr: IAudioSessionManager2 = match device.Activate(CLSCTX_ALL, None) {
            Ok(m) => m,
            Err(_) => continue,
        };

        let session_enum = match mgr.GetSessionEnumerator() {
            Ok(e) => e,
            Err(_) => continue,
        };

        // Deliberately not `count` — that would shadow the device count above.
        let session_count = match session_enum.GetCount() {
            Ok(c) => c,
            Err(_) => continue,
        };

        let mut device_hosts_teams = false;

        for j in 0..session_count {
            let ctrl = match session_enum.GetSession(j) {
                Ok(s) => s,
                Err(_) => continue,
            };

            let ctrl2: IAudioSessionControl2 = match ctrl.cast() {
                Ok(c) => c,
                Err(_) => continue,
            };

            let pid = match ctrl2.GetProcessId() {
                Ok(p) => p,
                Err(_) => continue,
            };

            if !crate::teams_proc::is_teams_pid(pid) {
                continue;
            }

            teams_found = true;
            device_hosts_teams = true;

            let vol: ISimpleAudioVolume = match ctrl.cast() {
                Ok(v) => v,
                Err(_) => continue,
            };

            if let Ok(mute) = vol.GetMute() {
                if mute.as_bool() {
                    session_muted = true;
                }
            }
        }

        // Only consult the endpoint of a device Teams is actually capturing from —
        // an unrelated muted mic (a webcam's, say) must not read as "Teams is muted".
        if device_hosts_teams {
            if let Ok(endpoint) = device.Activate::<IAudioEndpointVolume>(CLSCTX_ALL, None) {
                if let Ok(mute) = endpoint.GetMute() {
                    if mute.as_bool() {
                        endpoint_muted = true;
                    }
                }
            }
        }
    }

    if teams_found {
        Ok(Some(MuteReading {
            session: session_muted,
            endpoint: endpoint_muted,
        }))
    } else {
        Ok(None)
    }
}

// is_teams_pid moved to crate::teams_proc — uia_monitor needs it too.

#[cfg(not(windows))]
fn poll_wasapi_blocking(_tx: mpsc::Sender<WasapiEvent>) {
    log::warn!("WasapiMonitor: not supported on this platform");
}
