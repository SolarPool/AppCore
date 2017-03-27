using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.Net
{
    public class RestRequestException : Exception
    {
        public RestRequestException(HttpStatusCode code, string content) :
            base($"Request failed with status code {(int)code} ({code.ToString().ToLower()}): {content}")
        {
            StatusCode = code;
        }

        public HttpStatusCode StatusCode { get; private set; }
    }
}
