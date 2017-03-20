using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciphernote.Services.Dto
{
    public class StoredNote
    {
        public string Title { get; set; }
        public string[] Tags { get; set; }
        public string Excerpt { get; set; }
        public string ThumbnailUri { get; set; }
        public string Body { get; set; }
        public string BodyMimeType { get; set; }
        public string Source { get; set; }
        public bool HasLocation { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Altitude { get; set; }
        public double? TodoProgress { get; set; }
        public bool IsDeleted { get; set; }
    }
}
