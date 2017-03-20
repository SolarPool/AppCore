using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Ciphernote.Dom
{
    public class ContentBlock
    {
        public BlockType Type { get; set; }
        public string Text { get; set; }
        public TextAlignment TextAlignment { get; set; } = TextAlignment.Left;
        public int Depth { get; set; }
        public List<StyleRange> StyleRanges { get; set; } = new List<StyleRange>();
        public List<EntityRange> EntityRanges { get; set; } = new List<EntityRange>();

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrEmpty(Text?.Trim());

        [JsonIgnore]
        public StringBuilder TextBuilder { get; set; }

        [JsonIgnore]
        public bool IsBuilderEmpty => TextBuilder == null || TextBuilder.ToString().Trim().Length == 0;
    }
}
