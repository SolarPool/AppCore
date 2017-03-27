using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ciphernote.Dom;
using Ciphernote.Dom.EntityData;
using Ciphernote.Dom.StyleData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Ciphernote.Extensions
{
    public static class DomExtensions
    {
        public static bool IsStyleActive(this ContentBlock block, StyleType style, int offset,
            out StyleRange range)
        {
            var last = block.StyleRanges.LastOrDefault(x => x.Style == style);

            if (last != null && last.Offset + last.Length >= offset)
            {
                range = last;
                return true;
            }

            range = null;
            return false;
        }

        public static bool HasOverlappingRangeOfType(this ContentBlock block, EntityType type, EntityRange range,
            Document dom)
        {
            var ranges = block.EntityRanges.Where(x =>
            {
                var entity = dom.Entities[x.Entity];
                return entity.Type == type;
            }).ToArray();

            var result = ranges.Any(x => x.Overlaps(range));
            return result;
        }

        public static async Task<Document> LoadAsync(string json)
        {
            return await Task.Run(() =>
            {
                return JsonConvert.DeserializeObject<Document>(json);
            });
        }

        public static async Task<string> SaveAsync(this Document dom)
        {
            return await Task.Run(() =>
            {
                var settings = new JsonSerializerSettings();
                settings.ContractResolver = new CamelCasePropertyNamesContractResolver();

                return JsonConvert.SerializeObject(dom, Formatting.None, settings);
            });
        }

        public static void DeleteOrphanedEntities(this Document dom)
        {
            var orphanedEntityKeys = dom.Entities.Keys
                .Where(entityKey => dom.Blocks.SelectMany(y => y.EntityRanges).All(r => r.Entity != entityKey))
                .ToArray();

            foreach (var key in orphanedEntityKeys)
                dom.Entities.Remove(key);
        }

        public static double CalculateTodoProgress(this Document dom)
        {
            var todoEntities = dom.Entities.Values
                .Where(entity => entity.Type == EntityType.Todo)
                .ToArray();

            var checkedCount = todoEntities.Count(x => x.DataAsTodoInfo().IsCompleted);

            var result = (double) checkedCount / todoEntities.Length;
            return result;
        }

        public static int NextEntityKey(this Document dom)
        {
            if (dom.Entities.Keys.Count == 0)
                return 0;

            var result = dom.Entities.Keys.Max() + 1;
            return result;
        }

        public static bool IsListItem(this ContentBlock block)
        {
            return block.Type == BlockType.OrderedListItem ||
                   block.Type == BlockType.UnorderedListItem;
        }

        public static EntityRange FindNextEntityStart(this ContentBlock block, int position)
        {
            var range = block.EntityRanges
                .OrderBy(x => x.Offset)
                .FirstOrDefault(x => x.Offset >= position);

            return range;
        }

        public static StyleRange FindNextStyleStart(this ContentBlock block, int position)
        {
            var range = block.StyleRanges
                .OrderBy(x => x.Offset)
                .FirstOrDefault(x => x.Offset >= position);

            return range;
        }

        public static StyleRange[] GetStyleRangesForPosition(this ContentBlock block, int position)
        {
            var ranges = block.StyleRanges
                .Where(x => x.Offset <= position && x.ContainsPosition(position)).ToArray();

            return ranges;
        }

        public static EntityRange[] GetEntityRangesForPosition(this ContentBlock block, int position)
        {
            var ranges = block.EntityRanges
                .Where(x => x.Offset <= position && x.ContainsPosition(position)).ToArray();

            return ranges;
        }

        public static EntityRange Clone(this EntityRange range)
        {
            return new EntityRange(range.Entity, range.Offset, range.Length);
        }

        public static bool Overlaps(this EntityRange range, EntityRange other)
        {
            var xStart = other.Offset;
            var xEnd = other.Offset + other.Length;

            var yStart = range.Offset;
            var yEnd = range.Offset + range.Length;

            var overlaps = xStart <= yEnd && yStart <= xEnd;
            return overlaps;
        }

        public static bool ContainsPosition(this EntityRange range, int position)
        {
            return range.Offset <= position && (range.Offset + range.Length) >= position;
        }

        public static bool ContainsPosition(this StyleRange range, int position)
        {
            return range.Offset <= position && (range.Offset + range.Length) > position;
        }

        #region Document Assembly

        public static void AddBlock(this Document dom, ContentBlock block)
        {
            if (dom.Blocks.Any())
            {
                var lastBlock = dom.Blocks.Last();

                if (!lastBlock.IsBuilderEmpty)
                    lastBlock.ApplyBuilder();
            }

            dom.Blocks.Add(block);
        }
        
        public static ContentBlock AddParagraph(this Document dom)
        {
            var block = new ContentBlock { Type = BlockType.Paragraph };
            dom.AddBlock(block);
            return block;
        }

        public static ContentBlock AddHeading(this Document dom, int level)
        {
            if(level < 1 || level > 6)
                throw new ArgumentException();

            var block = new ContentBlock { Type = BlockType.H1 + (level - 1) };
            dom.AddBlock(block);
            return block;
        }

        public static ContentBlock AddListItem(this Document dom, int level, bool ordered = false)
        {
            var block = new ContentBlock
            {
                Type = ordered ? BlockType.OrderedListItem : BlockType.UnorderedListItem,
                Depth = level,
            };

            dom.AddBlock(block);
            return block;
        }

        public static void AddText(this ContentBlock block, string text, params StyleType[] styles)
        {
            if(block.TextBuilder == null)
                block.TextBuilder = new StringBuilder();

            var start = block.TextBuilder.Length;
            block.TextBuilder.Append(text);
            var end = block.TextBuilder.Length;

            foreach (var style in styles)
            {
                var range = new StyleRange(style, start, end - start);
                block.StyleRanges.Add(range);
            }
        }

        public static void AddLink(this ContentBlock block, Document dom, string text, string url)
        {
            if (block.TextBuilder == null)
                block.TextBuilder = new StringBuilder();

            var start = block.TextBuilder.Length;
            block.TextBuilder.Append(text);
            var end = block.TextBuilder.Length;

            var linkData = new LinkEntityData {Href = url};
            var entity = new Entity(EntityType.Link, EntityMutability.Mutable, JObject.FromObject(linkData));

            var entityKey = dom.NextEntityKey();
            var entityRange = new EntityRange(entityKey, start, end - start);
            block.EntityRanges.Add(entityRange);
            dom.Entities[entityKey] = entity;
        }

        public static void AddImage(this ContentBlock block, Document dom, string url, string mimeType, 
            int? width = null, int? height = null)
        {
            if (block.TextBuilder == null)
                block.TextBuilder = new StringBuilder();

            var start = block.TextBuilder.Length;
            block.TextBuilder.Append(Entity.ImagePlaceholderCharacter);
            var end = block.TextBuilder.Length;

            var imageInfo = new ImageEntityData
            {
                Url = url,
                MimeType = mimeType
            };

            if (width.HasValue)
                imageInfo.Width = width.Value;
            if (height.HasValue)
                imageInfo.Height = height.Value;

            var entity = new Entity(EntityType.Image, EntityMutability.Mutable, JObject.FromObject(imageInfo));

            var entityKey = dom.NextEntityKey();
            var entityRange = new EntityRange(entityKey, start, end - start);
            block.EntityRanges.Add(entityRange);
            dom.Entities[entityKey] = entity;
        }

        public static void AddAudio(this ContentBlock block, Document dom, string url, string mimeType, 
            string title, int length)
        {
            if (block.TextBuilder == null)
                block.TextBuilder = new StringBuilder();

            var start = block.TextBuilder.Length;
            block.TextBuilder.Append(Entity.AudioPlaceholderCharacter);
            var end = block.TextBuilder.Length;

            var audioInfo = new AudioEntityData
            {
                Url = url,
                MimeType = mimeType,
                Title = title,
                Length = length,
            };

            var entity = new Entity(EntityType.Audio, EntityMutability.Mutable, JObject.FromObject(audioInfo));

            var entityKey = dom.NextEntityKey();
            var entityRange = new EntityRange(entityKey, start, end - start);
            block.EntityRanges.Add(entityRange);
            dom.Entities[entityKey] = entity;
        }

        public static void AddTodo(this ContentBlock block, Document dom, bool isCompleted)
        {
            if (block.TextBuilder == null)
                block.TextBuilder = new StringBuilder();

            var start = block.TextBuilder.Length;
            block.TextBuilder.Append(Entity.TodoPlaceholderCharacter);
            var end = block.TextBuilder.Length;

            var audioInfo = new TodoEntityData
            {
                IsCompleted = isCompleted
            };

            var entity = new Entity(EntityType.Todo, EntityMutability.Mutable, JObject.FromObject(audioInfo));

            var entityKey = dom.NextEntityKey();
            var entityRange = new EntityRange(entityKey, start, end - start);
            block.EntityRanges.Add(entityRange);
            dom.Entities[entityKey] = entity;
        }

        public static void ApplyBuilder(this ContentBlock block)
        {
            block.Text = block.TextBuilder.ToString();
            block.TextBuilder = null;
        }

        #endregion // Document Assembly

        #region Dom Diffing

        public static bool IsSemanticallyEqual(this Document dom, Document other)
        {
            // Compare entites
            if (dom.Entities.Count != other.Entities.Count)
                return false;

            // Compare blocks
            if (dom.Blocks.Count != other.Blocks.Count)
                return false;

            for (int i = 0; i < dom.Blocks.Count; i++)
            {
                var a = dom.Blocks[i];
                var b = other.Blocks[i];

                if (!a.IsSemanticallyEqual(b, dom, other))
                    return false;
            }

            return true;
        }

        public static bool IsSemanticallyEqual(this ContentBlock block, ContentBlock other, Document dom,
            Document otherDom)
        {
            if (block.Type != other.Type)
                return false;

            if (block.Text != other.Text)
                return false;

            if (block.Depth != other.Depth)
                return false;

            if (block.TextAlignment != other.TextAlignment)
                return false;

            // Compare Style Ranges
            for (int i = 0; i < block.StyleRanges.Count; i++)
            {
                var a = block.StyleRanges[i];
                var b = other.StyleRanges[i];

                if (a.Style != b.Style)
                    return false;

                if (a.Offset != b.Offset)
                    return false;

                if (a.Length != b.Length)
                    return false;

                switch (a.Style)
                {
                    case StyleType.ForegroundColor:
                    case StyleType.BackgroundColor:
                        var aColor = a.DataAsColor();
                        var bColor = b.DataAsColor();

                        if (!aColor.Equals(bColor))
                            return false;
                        break;
                }
            }

            // Compare Entity Ranges
            for (int i = 0; i < block.EntityRanges.Count; i++)
            {
                var a = block.EntityRanges[i];
                var b = other.EntityRanges[i];

                if (a.Offset != b.Offset)
                    return false;

                if (a.Length != b.Length)
                    return false;

                var entA = dom.Entities[a.Entity];
                var entB = otherDom.Entities[b.Entity];

                if (!entA.IsSemanticallyEqual(entB))
                    return false;
            }

            return true;
        }

        public static bool IsSemanticallyEqual(this Entity entity, Entity other)
        {
            if (entity.Type != other.Type)
                return false;

            switch (entity.Type)
            {
                case EntityType.Link:
                    if (entity.Data.Value<string>("href") != other.Data.Value<string>("href"))
                        return false;
                    break;

                case EntityType.Image:
                    if (entity.Data.Value<string>("url") != other.Data.Value<string>("url"))
                        return false;

                    if (entity.Data.Value<string>("mimetype") != other.Data.Value<string>("mimetype"))
                        return false;

                    if (entity.Data.Value<string>("width") != other.Data.Value<string>("width"))
                        return false;

                    if (entity.Data.Value<string>("height") != other.Data.Value<string>("height"))
                        return false;
                    break;
            }

            return true;
        }

        #endregion // Dom Diffing

        public static Color DataAsColor(this StyleRange range)
        {
            return range.Value?.ToObject<Color>();
        }

        public static LinkEntityData DataAsLinkData(this Entity entity)
        {
            return entity.Data.ToObject<LinkEntityData>();
        }

        public static ImageEntityData DataAsImageInfo(this Entity entity)
        {
            return entity.Data.ToObject<ImageEntityData>();
        }

        public static AudioEntityData DataAsAudioInfo(this Entity entity)
        {
            return entity.Data.ToObject<AudioEntityData>();
        }

        public static TodoEntityData DataAsTodoInfo(this Entity entity)
        {
            return entity.Data.ToObject<TodoEntityData>();
        }
    }
}
