Teams2HA

<h1>Breaking Change Alert</h1>
The next version is moving away from Homeassistant websockets and migrating to MQTT - the reason for this is that any HomeAssistant entities created via websockets are read only, thus we cannot do two way control.
The entity names will also be changing (will be documented when released) and will be prefixed with the computer name of the computer running the program. This is to allow for multiple people working on Teams
<h1>End of notice</h1>



This is an agent that runs on windows and uses the Local teams API (https://support.microsoft.com/en-gb/office/connect-to-third-party-devices-in-microsoft-teams-aabca9f2-47bb-407f-9f9b-81a104a883d6?wt.mc_id=SEC-MVP-5004985) to retrieve the status of the user (In a meeting, Video On, Mute, blur etc) and push these into homeassistant sensors.

Download from https://github.com/jimmyeao/TEAMS2HA/releases

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/8b98c494-a3c0-41f7-8f9b-4716037910cc)

You will need a long-lived token for HomeAssistant, and the URL for your instance. The Teams API key will be automagically filled when you "pair" with teams.

Pairing

to pair, have the app running, launch a teams meeting (using meetnow?) and click test teams connection. This will initiate a pairing request in teams, accept this, and then the app will store the key.

The application will minimize to the system stray.

feedback is welcome, this is an early beta, but works ok for me :)

sensors in Home Assistant are all prefixed with "hats."

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/0bec90ee-8761-4308-bbc8-2001d170078a)

