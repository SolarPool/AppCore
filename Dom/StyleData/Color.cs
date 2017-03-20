using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;

namespace Ciphernote.Dom.StyleData
{
    public class Color
    {
        public Color()
        {
        }

        public Color(float red, float green, float blue, float alpha = 1)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }

        [JsonProperty(PropertyName = "r")]
        public float Red { get; set; }

        [JsonProperty(PropertyName = "g")]
        public float Green { get; set; }

        [JsonProperty(PropertyName = "b")]
        public float Blue { get; set; }

        [JsonProperty(PropertyName = "a")]
        public float Alpha { get; set; }

        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
        public bool Equals(Color other)
        {
            return Alpha == other.Alpha && Red == other.Red && Green == other.Green && Blue == other.Blue;
        }
    }
}
