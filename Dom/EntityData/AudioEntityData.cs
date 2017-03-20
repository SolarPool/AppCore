using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.Dom.EntityData
{
    public class AudioEntityData
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string MimeType { get; set; }
        public int Length { get; set; } // ms
    }
}
