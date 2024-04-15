using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TEAMS2HA.API;

namespace TEAMS2HA.Utils
{
    public class MeetingUpdateConverter : JsonConverter<MeetingUpdate>
    {
        #region Public Methods

        public override MeetingUpdate ReadJson(JsonReader reader, Type objectType, MeetingUpdate? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            JObject jsonObject = JObject.Load(reader);
            MeetingState meetingState = null;
            MeetingPermissions meetingPermissions = null;

            // Check if 'meetingUpdate' is present in JSON
            JToken meetingUpdateToken = jsonObject["meetingUpdate"];
            if (meetingUpdateToken != null)
            {
                // Check if 'meetingState' is present in 'meetingUpdate'
                JToken meetingStateToken = meetingUpdateToken["meetingState"];
                if (meetingStateToken != null)
                {
                    meetingState = meetingStateToken.ToObject<MeetingState>();
                }

                // Check if 'meetingPermissions' is present in 'meetingUpdate'
                JToken meetingPermissionsToken = meetingUpdateToken["meetingPermissions"];
                if (meetingPermissionsToken != null)
                {
                    meetingPermissions = meetingPermissionsToken.ToObject<MeetingPermissions>();
                }
            }

            return new MeetingUpdate
            {
                MeetingState = meetingState,
                MeetingPermissions = meetingPermissions
            };
        }

        public override void WriteJson(JsonWriter writer, MeetingUpdate? value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        #endregion Public Methods
    }
}
