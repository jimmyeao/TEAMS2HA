[![CodeQL](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/codeql.yml/badge.svg)](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/codeql.yml)[![GitHub tag](https://img.shields.io/github/tag/jimmyeao/TEAMS2HA?include_prereleases=&sort=semver&color=blue)](https://github.com/jimmyeao/TEAMS2HA/releases/)
[![License](https://img.shields.io/badge/License-MIT-blue)](#license)
[![issues - Teams2HA](https://img.shields.io/github/issues/jimmyeao/TEAMS2HA)](https://github.com/jimmyeao/TEAMS2HA/issues)

<H1>Teams2HA</H1>

<H1>IMPORTANT</H1>
  
Microsoft are deprecating the Teams local API, which has sadly broken our application.
I have written a new lightweight version in Rust/Tauri that uses teams logs and hardware signals to see if you are in a meeting, get your status, mute state and video state. The new installer will remove old version of Teams2Ha

Download the latest version from https://github.com/jimmyeao/TEAMS2HA/releases (app will auto update once installed)
<img width="822" height="712" alt="image" src="https://github.com/user-attachments/assets/5595f5ff-e4f3-44e6-8054-1cc381370fab" />


<h2>MQTT</h2>

Provide your MQTT instance details (IP, username and password) The password is encrypted before being saved to the settings file and is not stored in clear text.
We support plain MQTT, MQTT over TLS, MQTT over Websockets and MQTT over Websockets with TLS and the ability to ignore certificate errors if you are using self-signed certs (I would strongly advise you to use Lets Encrypt as a minimum)

<h2>Entities</h2>

This is how it should look in MQTT in Homeassistant

The topic will be 
- homeassistant/switch/YOURNAME/ismuted
- homeassistant/switch/YOURNAME/isvideoon
- homeassistant/sensor/YOURNAME/teamsstatus/state
- homeassistant/sensor/YOURNAME/presence/state
- homeassistant/binary_sensor/YOURNAME/isinmeeting/state
- homeassistant/binary_sensor/YOURNAME/teamsrunning/state

<img width="1037" height="584" alt="image" src="https://github.com/user-attachments/assets/476b0107-d738-4f37-96a4-a50b9ed3ed6a" />

(note, 2 way control is not possible at the moment, investigating the reliability of addign this in)





