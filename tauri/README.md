# Teams2HA (Rust / Tauri)

The current Teams → Home Assistant bridge. Reads Microsoft Teams state from logs and
hardware signals and publishes it over MQTT with Home Assistant auto-discovery.

The .NET/WPF app at the repository root is the previous generation, kept in case
Microsoft reverse the local-API deprecation. **This folder is the live application.**

## Requirements

- **Rust** (MSVC toolchain) — `rustup default stable-x86_64-pc-windows-msvc`
- **Node.js** 20+
- **MSVC C++ build tools** — Visual Studio with the "Desktop development with C++"
  workload, or the standalone Build Tools. Required for linking.
- **WebView2 runtime** — preinstalled on Windows 11.

> After installing Rust or Node, already-open terminals will not see them: `PATH` is read
> at shell start. Open a new terminal.

## Build and run

```sh
cd tauri
npm install
npm run tauri dev      # Vite on :1420 + cargo run, with hot reload
npm run tauri build    # NSIS installer, per-user (no admin required)
cargo test --manifest-path src-tauri/Cargo.toml --lib
```

The first `cargo build` compiles ~500 crates and takes a few minutes; later builds are
incremental.

## Runtime files

Both live in `%LOCALAPPDATA%\jimmyeao\Teams2HA\data\`:

| File | Notes |
|---|---|
| `settings.json` | Configuration. The MQTT password is DPAPI-encrypted (see below). |
| `teams2ha.log` | Level `info` by default; override with `RUST_LOG`. Restarted past 5 MB. |

## MQTT topics

`<prefix>` is the lower-cased sensor prefix from settings (defaults to the machine name).
Discovery configs are published retained on every connection.

| Topic | Type |
|---|---|
| `homeassistant/switch/<prefix>/ismuted/state` | switch |
| `homeassistant/switch/<prefix>/isvideoon/state` | switch |
| `homeassistant/binary_sensor/<prefix>/isinmeeting/state` | binary sensor |
| `homeassistant/binary_sensor/<prefix>/hasunreadmessages/state` | binary sensor |
| `homeassistant/binary_sensor/<prefix>/teamsrunning/state` | binary sensor |
| `homeassistant/sensor/<prefix>/teamsstatus/state` | sensor |
| `teams2ha/<prefix>/availability` | `online` / `offline` (Last Will) |

The Last Will matters: on any unclean exit — crash, sleep, leaving the network — the broker
marks everything unavailable rather than leaving stale retained state, e.g. `isinmeeting`
stuck `on` after closing the laptop mid-call.

The switches accept commands on `.../set`, but **nothing acts on them**: the Teams local API
that once allowed toggling mute/video is gone. They are effectively read-only today.

## How state is detected

Four independent monitors feed one `tokio::select!` loop in `lib.rs`:

| Signal | Source |
|---|---|
| In a meeting | `registry_monitor.rs` (mic in-use key) + `log_watcher.rs` |
| Presence, unread count | `log_watcher.rs` — tails the newest `MSTeams_*.log` |
| Camera on | `registry_monitor.rs` (per-app capability keys) |
| Teams running | `process_watcher.rs` |
| **Muted** | `uia_monitor.rs` (UI Automation) + `wasapi_monitor.rs` |

### Mute is a special case — read this before changing it

Teams' in-app mute button is **not observable through the audio stack**. Verified against
Teams 26183.1903.4892.4448: it does not move the per-session mute (`ISimpleAudioVolume`),
does not move the capture endpoint mute (`IAudioEndpointVolume`), no longer releases the
capture session (older builds did, and that used to be the signal), is absent from Teams'
own logs, and Teams exposes no local API port.

It *is* visible in the meeting window, whose mute button changes accessible name between
`Mute mic` and `Unmute mic`. `uia_monitor.rs` reads that via UI Automation.

Consequences to be aware of:

- **A Teams window must exist.** Closed to the tray, Teams' processes have no window,
  nothing appears in the UIA tree, and there is no reading. The monitor reports *unknown*
  rather than guessing "unmuted".
- **It is name-based**, so a Teams UI rename or a non-English UI will break it. There is no
  `TogglePattern` on the button, so the name is the only available signal.
- `wasapi_monitor.rs` is still used, for the OS-level per-app mute (muting Teams from the
  Windows volume mixer / Sound settings). Reported mute is the OR of the two.

If mute stops working after a Teams update, re-derive the button names with the probe:

```sh
cargo run --manifest-path src-tauri/Cargo.toml --example uia_probe
cargo run --manifest-path src-tauri/Cargo.toml --example uia_probe -- --dump   # list all buttons
```

## Home network gating

Optionally restrict MQTT to your home network by matching the default gateway's MAC
address (comma-separate several). Away from home the connection is dropped, so the Last
Will marks all entities unavailable. Leave the field empty to always connect.

On resume from suspend the MQTT connection is rebuilt unconditionally — the pre-suspend
session can be a silently dead TCP connection the event loop never errors on.

## Password storage

The MQTT password is encrypted with **DPAPI at current-user scope** and stored as
`dpapi:<base64>` in `settings.json`. Values without that prefix are treated as legacy
plaintext and re-encrypted on the next save, so older installs migrate silently.

Because DPAPI binds the blob to the Windows account, copying `settings.json` to another
user or machine yields a password that cannot be decrypted. That is reported as empty —
re-enter it in the UI.

## Releases and auto-update

Pushing a `vX.Y.Z` tag triggers `.github/workflows/release.yml`, which stamps the version
from the tag into `tauri.conf.json`, `package.json` and `Cargo.toml`, builds x64 and arm64
installers, and publishes a **public, non-draft** GitHub release. Installed clients pick it
up automatically.

The app checks for updates 30s after startup and every six hours, and on demand from the
tray menu. Scheduled checks are **skipped while a meeting is in progress** — installing
restarts the app, which drops MQTT and briefly marks every entity unavailable in Home
Assistant, and doing that mid-call is how users learn to switch auto-update off. An
explicit check from the tray is never deferred.

### Signing keys

Updates are signed with a minisign keypair. The public half is `plugins.updater.pubkey`
in `tauri.conf.json`; the private half is the `TAURI_SIGNING_PRIVATE_KEY` repository
secret. A client rejects any update whose signature does not verify against the embedded
public key, so control of the release host alone is not enough to push code to users.

`TAURI_SIGNING_PRIVATE_KEY_PASSWORD` is set to an empty string in the workflow rather than
stored as a secret, because the key has no password and GitHub does not accept a secret
with an empty value. It must still be **present**: with the variable unset entirely the
CLI prompts for a password on stdin and the job hangs until it times out.

> **Do not lose the private key.** The public key is compiled into every shipped binary.
> Without the private key you cannot sign updates, and every existing install stops
> accepting them — the only recovery is for each user to reinstall by hand. Keep a backup
> outside the repository.

Regenerate only if you accept that cost:

```sh
npx @tauri-apps/cli signer generate --write-keys <path-outside-the-repo>
```

### The updater manifest

The endpoint is `latest.json` on the GitHub release. `tauri-action`'s `includeUpdaterJson`
is deliberately **not** used: with a build matrix each job publishes a manifest describing
only its own architecture, and the second upload overwrites the first, leaving half the
users unable to update. The `updater-manifest` job assembles the combined file after both
builds and fails if a signature is missing, rather than publishing a manifest that silently
omits an architecture.

## Troubleshooting

| Symptom | Check |
|---|---|
| Mute never changes | Is a Teams *window* open? See the mute section above; run `uia_probe`. |
| Nothing appears in HA | `teams2ha.log` for `MQTT: connected to broker` and `discovery published`. |
| Entities show unavailable | Home-network gating — is the gateway MAC still correct? |
| Everything is stale | Confirm only **one** Teams2HA is running. An installed build and a `tauri dev` build share the log file and MQTT topics, and will fight. |
