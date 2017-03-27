using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Ciphernote.Model
{
    public class CredentialsQrCode
    {
        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("ak")]
        public string AccountKey { get; set; }

        [JsonProperty("pw")]
        public string Password { get; set; }
    }
}
