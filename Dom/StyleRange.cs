using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ciphernote.Dom
{
    public class StyleRange
    {
        public StyleRange()
        {
        }

        public StyleRange(StyleType style, int offset, int length, JObject value = null)
        {
            Debug.Assert(length > 0);
            Debug.Assert(offset >= 0);

            Style = style;
            Offset = offset;
            Length = length;
            Value = value;
        }

        public StyleType Style { get; set; }
        public JObject Value { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }

        [JsonIgnore]
        public int End => Offset + Length;
    }
}
