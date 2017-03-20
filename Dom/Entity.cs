using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ciphernote.Dom
{
    public class Entity
    {
        public Entity()
        {
        }

        public Entity(EntityType type, EntityMutability mutability, JObject data = null)
        {
            Type = type;
            Mutability = mutability;
            Data = data;
        }

        public const string ImagePlaceholderCharacter = "☹";
        public const string AudioPlaceholderCharacter = "♫";
        public const string TodoPlaceholderCharacter = "◙";

        public EntityType Type { get; set; }
        public EntityMutability Mutability { get; set; }
        public JObject Data { get; set; }
    }
}
