#Teams2HA

This is an agent that runs on windows and uses the teams API to retrieve the status of the user (In a meeting, Video On, Mute, blur etc) and push these into homeassistant sensors

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/8b98c494-a3c0-41f7-8f9b-4716037910cc)

You will need a long-lived token for HomeAssistant, and the URL for your instance. The Teams API key will be automagically filled when you "pair" with teams.

Pairing

to pair, have the app running, launch a teams meeting (using meetnow?) and click test teams connection. This will initiate a pairing request in teams, accept this, and then the app will store the key.

The application will minimize to the system stray.

feedback is welcome, this is an early beta, but works ok for me :)

sensors in Home Assistant are all prefixed with "hats."

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/0bec90ee-8761-4308-bbc8-2001d170078a)

