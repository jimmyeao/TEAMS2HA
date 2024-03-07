<h1>BREAKING CHANGES IN NEXT VERSION</h1>
In the next version of Teams2HA, we will be moving to a model more aligned with Homeassistant, instead of individual sensors and switches, these will be moved under a Device:

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/b14a824e-b939-4ba5-9515-b06bf4150270)

The Device will appear in the MQTT integration as the Prefix name

The sensor names should NOT change, but there is a small risk that they will for your setup which _may_ break integrations you already have. I have attempted to prevent this much as possible, but I can make no guarantees :)

As the old sensor names may conflict, you may need to remove them from MQTT - I use https://mqtt-explorer.com/ MQTT Explorer

Simply expand the view to **Homeassistant - switch** and remove all entries that start with your prefix, e.g. myprefix_isinmeeting, myprefix_isrecording on etc and then repeat for the entries under **homeasistant - sensor**

In the below example for switches, you can see the new names and the old ones, so I would remove the ones that start with ryzen_ in this list and leave the new ones such as ismuted, isbackgroundblurred,israisedhand,isvidoeon

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/7ff4428e-5f1d-460b-8954-1e4e82c9e3d7)

likewise for sensors, you would od the same:

![image](https://github.com/jimmyeao/TEAMS2HA/assets/5197831/14546eef-91f5-465b-9e18-8c716c469f86)
