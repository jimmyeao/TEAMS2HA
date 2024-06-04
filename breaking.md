<h1>BREAKING CHANGES IN NEXT VERSION</h1>
In the next version of Teams2HA, we will be moving to a model more aligned with Homeassistant, instead of individual sensors and switches, these will be moved under a Device:
Sensores will also now be BINARY sensors

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/b14a824e-b939-4ba5-9515-b06bf4150270)

The Device will appear in the MQTT integration as the Prefix name, in LOWER case - this aligns with MQTT best praactices.

As a result, the sensor names will change if your previous prefix was upper or mixed case - please check them and update your automations as required. This should be the last time we have to do this.

As the old sensor names may still exist, you may need to remove them from MQTT - I use https://mqtt-explorer.com/ MQTT Explorer

Simply expand the view to **Homeassistant - switch** and remove all entries that start with your prefix, e.g. myprefix_isinmeeting, myprefix_isrecording on etc and then repeat for the entries under **homeasistant - sensor**

In the below example for switches, the highlighed area shows the new format, notice that "Ryzen" is the prefix I am using - any of these sensors or switches that exist outside of a topic that starts with your prefix can be removed

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/c2cd4559-7a1c-4b11-8ce5-4cc315eb3f4d)


