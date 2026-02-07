[![CodeQL](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/codeql.yml/badge.svg)](https://github.com/jimmyeao/TEAMS2HA/actions/workflows/codeql.yml)[![GitHub tag](https://img.shields.io/github/tag/jimmyeao/TEAMS2HA?include_prereleases=&sort=semver&color=blue)](https://github.com/jimmyeao/TEAMS2HA/releases/)
[![License](https://img.shields.io/badge/License-MIT-blue)](#license)
[![issues - Teams2HA](https://img.shields.io/github/issues/jimmyeao/TEAMS2HA)](https://github.com/jimmyeao/TEAMS2HA/issues)

<H1>Teams2HA</H1>

DONT FORGET TO ENABLE THE API IN TEAMS!
Settings, Privacy, scroll to bottom and under 3rd party api click Manage Api then enable it

NEW! We now track presence status from the local teams logs!

This is an agent that runs on windows and uses the Local teams API (https://support.microsoft.com/en-gb/office/connect-to-third-party-devices-in-microsoft-teams-aabca9f2-47bb-407f-9f9b-81a104a883d6?wt.mc_id=SEC-MVP-5004985) to retrieve the status of the user (In a meeting, Video On, Mute, blur etc) and push these into homeassistant sensors using MQTT.

Download the latest version from https://github.com/jimmyeao/TEAMS2HA/releases (app will auto update once installed)

<img width="806" height="847" alt="image" src="https://github.com/user-attachments/assets/920579a0-f8f1-42fe-bff3-4c19839f80e9" />



<h2>Pairing</h2>

to pair, have the app running, launch a teams meeting (using meetnow?) and click Pair wtih teams. This will initiate a pairing request in teams, accept this, and then the app will store the key, in an encrypted format.

The application will minimize to the system tray.

<h2>MQTT</h2>

Provide your MQTT instance details (IP, username and password) The password is encrypted before being saved to the settings file and is not stored in clear text.
We support plain MQTT, MQTT over TLS, MQTT over Websockets and MQTT over Websockets with TLS and the ability to ignore certificate errors if you are using self-signed certs (I would strongly advise you to use Lets Encrypt as a minimum)

<h2>Entities</h2>
Click the Entities button to see a list of entities this program will create:

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/5c87da53-e66a-4bc8-af4b-34af0ddc6d47)


You can either right click and copy or double click to copy the entity name to the clipboard.

<h2>System Tray</h2>
You can right click the system tray icon for a selection of functions:

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/a8878f2e-38f6-4fce-a823-32f2008a0763)

This is how it should look in MQTT in Homeassistant

<img width="843" height="1290" alt="image" src="https://github.com/user-attachments/assets/8d5ea8f8-9063-4711-86fd-e5c9e103af9b" />





