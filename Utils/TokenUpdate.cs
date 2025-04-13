using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TEAMS2HA.Utils
{
    public class TokenUpdate
    {
        #region Public Properties

        [JsonProperty("tokenRefresh")]
        public string? NewToken { get; set; }

        #endregion Public Properties
    }
}