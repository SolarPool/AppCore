using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Ciphernote.Dom
{
    public class Document
    {
        public int Version { get; set; } = 1;

        public List<ContentBlock> Blocks { get; set; } = new List<ContentBlock>();
        public Dictionary<int, Entity> Entities { get; private set; } = new Dictionary<int, Entity>();

        public static async Task<Document> LoadAsync(string json)
        {
            return await Task.Run(() =>
            {
                return JsonConvert.DeserializeObject<Document>(json);
            });
        }
    }
}
