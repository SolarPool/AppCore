using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Dom.Css;
using AngleSharp.Dom.Html;
using AngleSharp.Extensions;
using AngleSharp.Parser.Html;
using Autofac;
using Ciphernote.Dom;
using Ciphernote.Dom.EntityData;
using Ciphernote.Dom.StyleData;
using Ciphernote.Extensions;
using Ciphernote.Logging;
using Ciphernote.Media;
using CodeContracts;
using Newtonsoft.Json.Linq;
using Splat;

namespace Ciphernote.Importers.Dom
{
    public class HtmlImporter : 
        IDomImporter, 
        IEnableLogger
    {
        public HtmlImporter(MimeTypeProvider mimeTypeProvider, IComponentContext ctx)
        {
            this.mimeTypeProvider = mimeTypeProvider;
            this.ctx = ctx;
        }

        public string Title => "Html Importer";
        public string[] SupportedMimeTypes => new[] { "text/html" };

        private readonly MimeTypeProvider mimeTypeProvider;
        private readonly HashSet<string> boldFontWeights = new HashSet<string> { "bold", "500", "600", "700", "800", "900" };
        private readonly Regex htmlEntitiesRegex = new Regex("&nbsp;|&lt;|&gt;|&amp;", RegexOptions.Compiled);

        private readonly Regex regexCssRgb = new Regex(@"rgb\(\s*(-?\d+)(%?)\s*,\s*(-?\d+)(\2)\s*,\s*(-?\d+)(\2)\s*\)", RegexOptions.Compiled);
        private readonly Regex regexCssRgba = new Regex(@"rgba\(\s*(-?\d+)(%?)\s*,\s*(-?\d+)(\2)\s*,\s*(-?\d+)(\2)\s*,\s*(-?\d+|-?\d*.\d+)\s*\)", RegexOptions.Compiled);
        private readonly IComponentContext ctx;

        public async Task<Document> ImportDomAsync(object content, DomImportOptions options)
        {
            Contract.RequiresNonNull(content, nameof(content));
            Contract.RequiresNonNull(options, nameof(options));

            var doc = content as IHtmlDocument;

            if (doc == null)
            {
                var html = content as string;
                var stm = content as Stream;

                if (html == null && stm != null)
                {
                    using (var reader = new StreamReader(stm, Encoding.UTF8))
                        html = reader.ReadToEnd();
                }

                if (!string.IsNullOrEmpty(html))
                {
                    var configuration = new Configuration().WithCss();
                    var parser = new HtmlParser(configuration);
                    doc = await parser.ParseAsync(html);
                }
            }

            if (doc == null)
                throw new NotSupportedException("unsupported content object");

            return await Task.Run(async () =>
            {
                var dom = new Document();

                // create initial block
                var block = new ContentBlock {Type = BlockType.Paragraph, TextBuilder = new StringBuilder() };

                // process
                block = ProcessNode(dom, block, doc.Body, -1);

                // terminate final block
                if (!block.IsBuilderEmpty)
                    block.ApplyBuilder();

                if (!block.IsEmpty && !dom.Blocks.Contains(block))
                {
                    if (!block.IsListItem())
                        block.Depth = 0;

                    dom.Blocks.Add(block);
                }

                // post-process
                dom.Blocks.ForEach(TrimAndAdjustRanges);
                dom.DeleteOrphanedEntities();

                // import images
                var failCount = await DomImportHelper.ImportImages(dom, ctx, options.ImportExternalImages, this.Log());

                if (failCount > 0)
                    this.Log().Warning(()=> $"HtmlImporter: {failCount} external images failed to import");

                return dom;
            });
        }

        private ContentBlock ProcessNode(Document doc, ContentBlock block, INode currentNode, int listDepth)
        {
            foreach (var node in currentNode.ChildNodes)
            {
                switch (node.NodeType)
                {
                    case NodeType.Element:
                        var el = (IHtmlElement) node;
                        var handled = false;

                        ContentBlock anchorStartBlock = null;
                        int anchorStartBlockPosition = 0;

                        block = HandleElement(el, doc, block, out handled);

                        if(handled)
                            continue;

                        if (!el.HasChildNodes)
                            continue;

                        // Anchor handling
                        var isAnchor = el.TagName.ToLower() == "a" && !string.IsNullOrEmpty(el.GetAttribute("href"));
                        if (isAnchor)
                        {
                            anchorStartBlock = block;
                            anchorStartBlockPosition = block.TextBuilder.Length;
                        }

                        else if (IsSupportedBlockElement(el))
                        {
                            if (!block.IsBuilderEmpty)
                                block = TerminateBlock(doc, block, el);

                            block.Type = BlockTypeFromElement(el);

                            if (block.IsListItem())
                            {
                                var depthActual = listDepth;
                                var currentDepth = GetCurrentDepth(doc);

                                if (listDepth - currentDepth > 1)
                                    depthActual = currentDepth + 1;

                                block.Depth = depthActual;
                            }

                            switch (el.Style.TextAlign.ToLower())
                            {
                                case "left":
                                    block.TextAlignment = TextAlignment.Left;
                                    break;
                                case "right":
                                    block.TextAlignment = TextAlignment.Right;
                                    break;
                                case "center":
                                    block.TextAlignment = TextAlignment.Center;
                                    break;
                                case "justify":
                                    block.TextAlignment = TextAlignment.Justify;
                                    break;
                            }
                        }

                        block = ProcessNode(doc, block, node, IsListContainerElement(el) ? listDepth + 1 : listDepth);

                        // link handling
                        if (isAnchor)
                        {
                            // entity data
                            var entityData = new JObject();
                            entityData.Add("href", JToken.FromObject(el.GetAttribute("href")));

                            // create entity
                            var entity = new Entity(EntityType.Link, EntityMutability.Mutable, entityData);

                            if (block == anchorStartBlock)
                            {
                                if (!block.IsBuilderEmpty && block.TextBuilder.Length - anchorStartBlockPosition > 0)
                                {
                                    var entityKey = doc.NextEntityKey();
                                    var entityRange = new EntityRange(entityKey, anchorStartBlockPosition,
                                        block.TextBuilder.Length - anchorStartBlockPosition);

                                    if (!block.HasOverlappingRangeOfType(EntityType.Link, entityRange, doc))
                                    {
                                        // block has changed, apply the link to the entire block
                                        doc.Entities[entityKey] = entity;

                                        block.EntityRanges.Add(entityRange);
                                    }
                                }
                            }

                            else
                            {
                                if (!block.IsBuilderEmpty)
                                {
                                    var entityKey = doc.NextEntityKey();
                                    var entityRange = new EntityRange(entityKey, 0, block.TextBuilder.Length);

                                    if (!block.HasOverlappingRangeOfType(EntityType.Link, entityRange, doc))
                                    {
                                        // block has changed, apply the link to the entire block
                                        doc.Entities[entityKey] = entity;

                                        block.EntityRanges.Add(entityRange);
                                    }
                                }
                            }
                        }
                        break;

                    case NodeType.Entity:
                        break;

                    case NodeType.Text:
                        var oldLength = block.TextBuilder.Length;
                        block.TextBuilder.Append(MaterializeHtmlEntities(node.Text()));

                        if (node.ParentElement != null)
                        {
                            var styles = node.ParentElement.Style; // node.ParentElement.ComputeCurrentStyle();
                            ApplyRangeStyle(block, node, oldLength, block.TextBuilder.Length, styles);
                        }
                        break;
                }
            }

            return block;
        }

        private int GetCurrentDepth(Document doc)
        {
            if (doc.Blocks.Count == 0)
                return -1;

            var lastBlock = doc.Blocks.Last();

            if (!lastBlock.IsListItem())
                return -1;

            return lastBlock.Depth;
        }

        private bool IsSupportedBlockElement(IHtmlElement el)
        {
            switch (el.TagName.ToLower())
            {
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                case "li":
                case "p":
                case "div":
                case "form":
                case "pre": // Renders text in a fixed-width font
                case "header":
                case "footer":
                case "section":
                case "blockquote":
                case "caption":
                case "center":
                case "cite":
                case "dd":
                case "dl":
                case "dt":
                case "tt":
                    return true;
            }

            return false;
        }

        private bool IsListContainerElement(IHtmlElement el)
        {
            switch (el.TagName.ToLower())
            {
                case "ol":
                case "ul":
                case "dir": //  treat as UL element
                case "menu": //  treat as UL element
                    return true;
            }

            return false;
        }

        private BlockType BlockTypeFromElement(IHtmlElement el)
        {
            switch (el.TagName.ToLower())
            {
                case "h1":
                    return BlockType.H1;
                case "h2":
                    return BlockType.H2;
                case "h3":
                    return BlockType.H3;
                case "h4":
                    return BlockType.H4;
                case "h5":
                    return BlockType.H5;
                case "h6":
                    return BlockType.H6;

                case "p":
                    return BlockType.Paragraph;

                case "li":
                    var parentElementTag = el.ParentElement?.TagName?.ToLower();
                    if(parentElementTag == "ol")
                        return BlockType.OrderedListItem;
                    else
                        return BlockType.UnorderedListItem;

                case "pre":
                    return BlockType.Codeblock;

                case "blockquote":
                    return BlockType.BlockQuote;

                default:
                    return BlockType.Paragraph;
            }
        }

        private void ApplyRangeStyle(ContentBlock block, INode node, int rangeStart, int rangeEnd, ICssStyleDeclaration style)
        {
            ApplyFontWeight(block, node, rangeStart, rangeEnd, style);
            ApplyFontStyle(block, node, rangeStart, rangeEnd, style);
            ApplyTextDecoration(block, node, rangeStart, rangeEnd, style);
            ApplyColors(block, node, rangeStart, rangeEnd, style);
        }

        private void ApplyFontWeight(ContentBlock block, INode node, int rangeStart, int rangeEnd, ICssStyleDeclaration style)
        {
            var rangeLength = rangeEnd - rangeStart;

            const StyleType styleType = StyleType.Bold;

            if(boldFontWeights.Contains(style.FontWeight.ToLower())
                 || node.HasAncestorElementOfTypes("b", "strong"))
            {
                block.StyleRanges.Add(new StyleRange(styleType, rangeStart, rangeLength));
            }
        }

        private void ApplyFontStyle(ContentBlock block, INode node, int rangeStart, int rangeEnd, ICssStyleDeclaration style)
        {
            var rangeLength = rangeEnd - rangeStart;

            const StyleType styleType = StyleType.Italic;

            if(style.FontStyle.ToLower() == "italic"
                 || node.HasAncestorElementOfTypes("i", "em"))
            {
                block.StyleRanges.Add(new StyleRange(styleType, rangeStart, rangeLength));
            }
        }

        private void ApplyTextDecoration(ContentBlock block, INode node, int rangeStart, int rangeEnd, ICssStyleDeclaration style)
        {
            var rangeLength = rangeEnd - rangeStart;

            var styleType = StyleType.Underline;

            if (style.TextDecoration.ToLower() == "underline")
            {
                block.StyleRanges.Add(new StyleRange(styleType, rangeStart, rangeLength));
            }

            styleType = StyleType.StrikeThrough;

            if (style.TextDecoration.ToLower() == "line-through")
            {
                block.StyleRanges.Add(new StyleRange(styleType, rangeStart, rangeLength));
            }
        }

        private void ApplyColors(ContentBlock block, INode node, int rangeStart, int rangeEnd, ICssStyleDeclaration style)
        {
            var rangeLength = rangeEnd - rangeStart;
            Color color;

            var styleType = StyleType.ForegroundColor;

            if (!string.IsNullOrEmpty(style.Color))
            {
                color = ColorFromStyle(style.Color);

                if (color != null && color.Alpha > 0)
                    block.StyleRanges.Add(new StyleRange(styleType, rangeStart, rangeLength, JObject.FromObject(color)));
            }

            styleType = StyleType.BackgroundColor;

            if (!string.IsNullOrEmpty(style.BackgroundColor))
            {
                color = ColorFromStyle(style.BackgroundColor);

                if (color != null && color.Alpha > 0)
                    block.StyleRanges.Add(new StyleRange(styleType, rangeStart, rangeLength, JObject.FromObject(color)));
            }
        }

        Color ColorFromStyle(string style)
        {
            AngleSharp.Css.Values.Color c;
            Color result;
            Match m;

            if (style.StartsWith("#"))
            {
                if (AngleSharp.Css.Values.Color.TryFromHex(style, out c))
                {
                    result = new Color(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f);
                    return result;
                }

                return null;
            }

            if ((m = regexCssRgba.Match(style)).Success)
                return ColorFromRgba(m.Groups[1].Value, m.Groups[3].Value, m.Groups[5].Value, m.Groups[7].Value);
            else if ((m = regexCssRgb.Match(style)).Success)
                return ColorFromRgba(m.Groups[1].Value, m.Groups[3].Value, m.Groups[5].Value, "1");
            else
            {
                var tmp = AngleSharp.Css.Values.Color.FromName(style);
                if (tmp.HasValue)
                {
                    c = tmp.Value;
                    result = new Color(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f);
                    return result;
                }
            }

            return null;
        }

        Color ColorFromRgba(string r, string g, string b, string a)
        {
            float rFrac, gFrac, bFrac, aFrac;
            int rP, gP, bP;

            if (r.EndsWith("%") || g.EndsWith("%") || b.EndsWith("%"))
            {
                // if one is % based then all other values must be as well
                if(!(r.EndsWith("%") && g.EndsWith("%") && b.EndsWith("%")))
                    return null;

                if (int.TryParse(r.Replace("%", ""), out rP) && int.TryParse(g.Replace("%", ""), out gP) 
                    && int.TryParse(b.Replace("%", ""), out bP) && 
                    float.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out aFrac))
                {
                    rFrac = Math.Min(100, rP) / 100.0f;
                    gFrac = Math.Min(100, gP) / 100.0f;
                    bFrac = Math.Min(100, bP) / 100.0f;

                    return new Color(rFrac, gFrac, bFrac, aFrac);
                }
            }

            else
            {
                if (int.TryParse(r, out rP) && int.TryParse(g, out gP) && int.TryParse(b, out bP) &&
                    float.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out aFrac))
                {
                    rFrac = Math.Min(255, rP) / 255.0f;
                    gFrac = Math.Min(255, gP) / 255.0f;
                    bFrac = Math.Min(255, bP) / 255.0f;

                    return new Color(rFrac, gFrac, bFrac, aFrac);
                }
            }

            return null;
        }

        private ContentBlock HandleElement(IHtmlElement el, Document doc, ContentBlock block, out bool handled)
        {
            switch (el.TagName.ToLower())
            {
                case "img":
                    return HandleImage(el, doc, block, out handled);
                case "audio":
                    return HandleAudio(el, doc, block, out handled);
                case "en-todo":
                    return HandleEvernoteTodo(el, doc, block, out handled);
                case "br":
                    handled = true;
                    if (!block.IsBuilderEmpty)
                        return TerminateBlock(doc, block, el);

                    return block;
                default:
                    handled = false;
                    return block;
            }
        }

        private int? FixImageDimension(string dim)
        {
            if (!string.IsNullOrEmpty(dim))
            {
                var m = Regex.Match(dim, @"\d+");
                if (m.Success)
                {
                    int result;
                    if (int.TryParse(m.Groups[0].Value, out result))
                        return result;
                }
            }

            return null;
        }

        private ContentBlock HandleImage(IHtmlElement el, Document doc, ContentBlock block, out bool handled)
        {
            handled = true;

            var src = el.GetAttribute("src");

            if (string.IsNullOrEmpty(src))
                return block;

            Uri uri;
            if (!Uri.TryCreate(src, UriKind.Absolute, out uri))
                return block;
 
            var styles = el.Style;  //el.ComputeCurrentStyle();

            var width = FixImageDimension(el.GetAttribute("width") ?? styles.Width);
            var height = FixImageDimension(el.GetAttribute("height") ?? styles.Height);

            if (!width.HasValue && !height.HasValue)
                width = 300;

            // create block
            if (!block.IsBuilderEmpty)
                block = TerminateBlock(doc, block, el);

            // create entity data
            var mimeType = mimeTypeProvider.GetMimeTypeForExtension(uri);

            var imageInfo = new ImageEntityData
            {
                Url = src,
                MimeType = mimeType
            };

            if (width.HasValue)
                imageInfo.Width = width.Value;

            if (height.HasValue)
                imageInfo.Height = height.Value;

            // create entity
            var entity = new Entity(EntityType.Image, EntityMutability.Mutable, JObject.FromObject(imageInfo));
            var entityKey = doc.NextEntityKey();
            doc.Entities[entityKey] = entity;

            block.EntityRanges.Add(new EntityRange(entityKey, block.TextBuilder.Length, 1));
            block.TextBuilder.Append(Entity.ImagePlaceholderCharacter);

            return TerminateBlock(doc, block, el);
        }

        private ContentBlock HandleAudio(IHtmlElement el, Document doc, ContentBlock block, out bool handled)
        {
            handled = true;

            var src = el.GetAttribute("src");

            if (string.IsNullOrEmpty(src))
                return block;

            Uri uri;
            if (!Uri.TryCreate(src, UriKind.Absolute, out uri))
                return block;

            if(uri.Scheme != AppCoreConstants.MediaUriScheme)
                return block;

            // create block
            if (!block.IsBuilderEmpty)
                block = TerminateBlock(doc, block, el);

            // create entity data
            var mimeType = mimeTypeProvider.GetMimeTypeForExtension(uri);

            var audioInfo = new AudioEntityData
            {
                Url = src,
                MimeType = mimeType,
                Title = el.GetAttribute("title"),
                Length = int.Parse(el.GetAttribute("data-length-ms"), CultureInfo.InvariantCulture),
            };

            // create entity
            var entity = new Entity(EntityType.Audio, EntityMutability.Mutable, JObject.FromObject(audioInfo));
            var entityKey = doc.NextEntityKey();
            doc.Entities[entityKey] = entity;

            block.EntityRanges.Add(new EntityRange(entityKey, block.TextBuilder.Length, 1));
            block.TextBuilder.Append(Entity.AudioPlaceholderCharacter);

            return TerminateBlock(doc, block, el);
        }

        private ContentBlock HandleEvernoteTodo(IHtmlElement el, Document doc, ContentBlock block, out bool handled)
        {
            handled = false;

            var isChecked = el.GetAttribute("checked")?.ToLower() == "true";

            var todoInfo = new TodoEntityData
            {
                IsCompleted = isChecked,
            };

            // create entity
            var entity = new Entity(EntityType.Todo, EntityMutability.Mutable, JObject.FromObject(todoInfo));
            var entityKey = doc.NextEntityKey();
            doc.Entities[entityKey] = entity;

            block.EntityRanges.Add(new EntityRange(entityKey, block.TextBuilder.Length, 1));
            block.TextBuilder.Append(Entity.TodoPlaceholderCharacter);

            return block;
        }

        private void TrimAndAdjustRanges(ContentBlock block)
        {
            if (block.TextBuilder != null)
            {
                block.Text = block.TextBuilder.ToString();
                block.TextBuilder = null;
            }

            // Trim start
            //////////////////////

            var oldLength = block.Text.Length;
            block.Text = block.Text.TrimStart();
            var newLength = block.Text.Length;

            if (oldLength > newLength)
            {
                var delta = oldLength - newLength;

                block.StyleRanges.ForEach(x =>
                {
                    x.Offset -= delta;

                    if (x.Offset < 0)
                    {
                        x.Length += x.Offset;
                        x.Offset = 0;
                    }
                });

                // filter all ranges that have become invalid (which is correct)
                block.StyleRanges = block.StyleRanges
                    .Where(x => x.Length > 0)
                    .ToList();

                block.EntityRanges.ForEach(x =>
                {
                    x.Offset -= delta;

                    if (x.Offset < 0)
                    {
                        x.Length += x.Offset;
                        x.Offset = 0;
                    }
                });

                // filter all ranges that have become invalid (which is correct)
                block.EntityRanges = block.EntityRanges
                    .Where(x => x.Length > 0)
                    .ToList();
            }

            // Trim end
            //////////////////////

            oldLength = block.Text.Length;
            block.Text = block.Text.TrimEnd();
            newLength = block.Text.Length;

            if (oldLength > newLength)
            {
                block.StyleRanges.ForEach(x =>
                {
                    x.Length = Math.Min(x.Length, newLength - x.Offset);
                });

                var obsoleteInlineStyleRanges = block.StyleRanges.Where(x => x.Length <= 0).ToArray();
                foreach (var inlineStyleRange in obsoleteInlineStyleRanges)
                    block.StyleRanges.Remove(inlineStyleRange);

                block.EntityRanges.ForEach(x =>
                {
                    x.Length = Math.Min(x.Length, newLength - x.Offset);
                });

                var obsoleteEntityRanges = block.EntityRanges.Where(x => x.Length <= 0).ToArray();
                foreach (var entityRange in obsoleteEntityRanges)
                    block.EntityRanges.Remove(entityRange);
            }
        }

        private ContentBlock TerminateBlock(Document doc, ContentBlock block, IHtmlElement el)
        {
            if (!block.IsListItem())
                block.Depth = 0;

            block.ApplyBuilder();
            doc.Blocks.Add(block);

            // we are done with this block
            block = new ContentBlock
            {
                Type = BlockTypeFromElement(el),
                TextBuilder = new StringBuilder()
            };

            return block;
        }

        private string MaterializeHtmlEntities(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return htmlEntitiesRegex.Replace(value, match =>
            {
                switch (match.Value)
                {
                    case "&nbsp;":
                        return " ";
                    case "&lt;":
                        return "<";
                    case "&gt;":
                        return ">";
                    case "&amp;":
                        return "&";
                }

                return string.Empty;
            });
        }
    }
}
