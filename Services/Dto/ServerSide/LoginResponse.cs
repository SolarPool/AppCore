using System;

namespace Ciphernote.Services.Dto
{
    public class LoginResponse : ResponseBase
    {
        public string BearerToken { get; set; }
        public DateTime Expires { get; set; }
    }
}
