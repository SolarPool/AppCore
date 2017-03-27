using System.Diagnostics;
using Newtonsoft.Json;

namespace Ciphernote.Dom
{
    public class EntityRange
    {
        public EntityRange()
        {
        }

        public EntityRange(int entity, int offset, int length)
        {
            Debug.Assert(length > 0);
            Debug.Assert(offset >= 0);

            Entity = entity;
            Offset = offset;
            Length = length;
        }

        public int Entity { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }

        [JsonIgnore]
        public int End => Offset + Length;
    }
}
