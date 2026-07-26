[![CodeQL](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/codeql.yml/badge.svg)](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/codeql.yml)[![GitHub tag](https://img.shields.io/github/tag/jimmyeao/TEAMS2HA?include_prereleases=&sort=semver&color=blue)](https://github.com/jimmyeao/TEAMS2HA/releases/)
[![License](https://img.shields.io/badge/License-MIT-blue)](#license)
[![issues - Teams2HA](https://img.shields.io/github/issues/jimmyeao/TEAMS2HA)](https://github.com/jimmyeao/TEAMS2HA/issues)
[![Rust Security Audit](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/rust-audit.yml/badge.svg)](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/rust-audit.yml)

<H1>Teams2HA</H1>

<H1>IMPORTANT</H1>
  
Microsoft are deprecating the Teams local API, which has sadly broken our application.
I have written a new lightweight version in Rust/Tauri that uses teams logs and hardware signals to see if you are in a meeting, get your status, mute state and video state. You will need to remove the old version, and install this version - admin rights are NOT required.

Note: As of 26/07/2026 Microsoft no longer expose the mute state, we are now scraping this directly from the UI, please let me know if this is not working for you.

Download the latest version from https://github.com/jimmyeao/TEAMS2HA/releases (app will now auto update once installed)
<img width="902" height="852" alt="image" src="https://github.com/user-attachments/assets/e3e073ba-bcce-42c9-a055-f17eae6c9259" />



<h2>MQTT</h2>

Provide your MQTT instance details (IP, username and password) The password is encrypted before being saved to the settings file and is not stored in clear text.
We support plain MQTT, MQTT over TLS, MQTT over Websockets and MQTT over Websockets with TLS and the ability to ignore certificate errors if you are using self-signed certs (I would strongly advise you to use Lets Encrypt as a minimum)

<h2>Entities</h2>

This is how it should look in MQTT in Homeassistant

The topic will be 
- homeassistant/switch/YOURNAME/ismuted/state
- homeassistant/switch/YOURNAME/isvideoon/state
- homeassistant/sensor/YOURNAME/teamsstatus/state
- homeassistant/binary_sensor/YOURNAME/isinmeeting/state
- homeassistant/binary_sensor/YOURNAME/hasunreadmessages/state
- homeassistant/binary_sensor/YOURNAME/teamsrunning/state

Plus an availability topic, teams2ha/YOURNAME/availability, which is set to offline by the broker (via the MQTT Last Will) if the app stops unexpectedly, so entities show as unavailable in Home Assistant rather than getting stuck on a stale value.

<img width="1037" height="584" alt="image" src="https://github.com/user-attachments/assets/476b0107-d738-4f37-96a4-a50b9ed3ed6a" />

(note, 2 way control is not possible at the moment, investigating the reliability of addign this in)

<h2>A note on mute detection</h2>

Teams' mute button does not change anything visible in the Windows audio stack, so mute is read from the Teams meeting window itself using UI Automation. Two things follow from that:

- A Teams window has to be open. If Teams is fully closed to the system tray there is nothing to read, and mute is reported as unknown rather than guessed.
- It reads the mute button's accessible name, so a Teams UI redesign or a non-English Teams may break it. If mute stops updating after a Teams update, please raise an issue - there is a diagnostic tool in the repo (tauri/src-tauri/examples/uia_probe.rs) that dumps what Teams is exposing.

Muting Teams from the Windows volume mixer or Sound settings is detected separately and always works.

Footnote: I have left the old .net source code intact, in case Microsoft reverse their decidion, the new code is in the Tauri folder, if you need to make changes. PRs always welcome :)




