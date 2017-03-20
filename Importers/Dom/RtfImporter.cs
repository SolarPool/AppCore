using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Dom;
using Ciphernote.Dom.EntityData;
using Ciphernote.Dom.StyleData;
using Ciphernote.Extensions;
using Ciphernote.IO;
using Ciphernote.Logging;
using Ciphernote.Media;
using CodeContracts;
using Newtonsoft.Json.Linq;
using RtfPipe;
using RtfPipe.Interpreter;
using RtfPipe.Model;
using RtfPipe.Parser;
using RtfPipe.Support;
using Splat;

namespace Ciphernote.Importers.Dom
{
    public class RtfImporter : RtfVisualVisitorBase,
        IDomImporter,
        IEnableLogger
    {
        public RtfImporter(MediaManager mediaManager, IComponentContext ctx)
        {
            this.mediaManager = mediaManager;
            this.ctx = ctx;
        }

        public string Title => "Rtf Importer";
        public string[] SupportedMimeTypes => new[] { "text/rtf" };

        private readonly MediaManager mediaManager;
        private readonly IComponentContext ctx;

        public async Task<Document> ImportDomAsync(object content, DomImportOptions options)
        {
            Contract.RequiresNonNull(content, nameof(content));
            Contract.RequiresNonNull(options, nameof(options));

            var rtf = content as string;

            return await Task.Run(async () =>
            {
                var parser = new RtfParser();
                var structureBuilder = new RtfParserListenerStructureBuilder();
                parser.AddParserListener(structureBuilder);
                parser.Parse(new RtfSource(rtf));

                var intSettings = new RtfInterpreterSettings
                {
                    IgnoreDuplicatedFonts = true,
                    IgnoreUnknownFonts = true
                };

                var rtfDocument = RtfInterpreterTool.BuildDoc(structureBuilder.StructureRoot, intSettings);
                var dom = new Document();

                var visitor = new Visitor(rtfDocument, dom, mediaManager);
                visitor.Convert();

                // import images
                var failCount = await DomImportHelper.ImportImages(dom, ctx, options.ImportExternalImages, this.Log());

                if (failCount > 0)
                    this.Log().Warning(()=> $"RtfImporter: {failCount} external images failed to import");

                return dom;
            });
        }

        class Visitor : IRtfVisualVisitor,
            IEnableLogger
        {
            public Visitor(IRtfDocument doc, Document dom, MediaManager mediaManager)
            {
                this.doc = doc;
                this.dom = dom;
                this.mediaManager = mediaManager;
            }

            enum State
            {
                Default = 1,
                DetermineListType,
                InField,
                InFieldInstruction
            }

            private readonly IRtfDocument doc;
            private readonly Document dom;
            private MediaManager mediaManager;
            private State state;
            private readonly HashSet<string> unorderedListValues = new HashSet<string> { "·", "•" };
            private ContentBlock block;

            private string currentFieldResult;
            private int currentFieldStart;

            private readonly Regex regexHyperlink = new Regex("HYPERLINK \"([^\"]+)\"", RegexOptions.Compiled);

            public void Convert()
            {
                state = State.Default;

                // create initial block
                block = new ContentBlock { Type = BlockType.Paragraph, TextBuilder = new StringBuilder() };

                foreach (var visual in doc.VisualContent)
                {
                    visual.Visit(this);
                }

                // terminate final block
                if (!block.IsBuilderEmpty)
                    block.ApplyBuilder();

                if (!block.IsEmpty && !dom.Blocks.Contains(block))
                {
                    if (!block.IsListItem())
                        block.Depth = 0;

                    dom.Blocks.Add(block);
                }
            }

            private void TerminateBlock()
            {
                block.ApplyBuilder();
                dom.Blocks.Add(block);

                block = new ContentBlock
                {
                    Type = BlockType.Paragraph,
                    TextBuilder = new StringBuilder()
                };
            }

            private int TabPosition2ListLevel(int listOverrideIndex, int leftIndent, IRtfDocument doc)
            {
                if (listOverrideIndex < 0 || listOverrideIndex >= doc.ListOverrideTable.Count)
                    return 0;

                // look up list override
                var listOverride = doc.ListOverrideTable[listOverrideIndex];

                // look up list using override id
                IRtfList list;
                if (!doc.ListMap.TryGetValue(listOverride.ListId, out list))
                    return 0;

                // compute level from left indent
                var result = list.Levels
                    .Where(x => x.TabPosition == leftIndent)
                    .Select(x => x.Level)
                    .FirstOrDefault();

                return result;
            }

            private void AssignHeadingType(IRtfTextFormat format)
            {
                // determine heading type - if applicable
                if (block.Type == BlockType.Paragraph && format.FontSize != doc.DefaultTextFormat.FontSize)
                {
                    // only assign heading if the block is plain (no style ranges, no entities)
                    if (!block.StyleRanges.Any() && !block.EntityRanges.Any())
                    {
                        var factor = (double) format.FontSize / doc.DefaultTextFormat.FontSize;

                        if(factor >= FormatIndependentStyleConstants.Heading1ToBaseFontSizeMultiplier)
                            block.Type = BlockType.H1;
                        else if (factor >= FormatIndependentStyleConstants.Heading2ToBaseFontSizeMultiplier)
                            block.Type = BlockType.H2;
                        else if (factor >= FormatIndependentStyleConstants.Heading3ToBaseFontSizeMultiplier)
                            block.Type = BlockType.H3;
                        else if (factor >= FormatIndependentStyleConstants.Heading4ToBaseFontSizeMultiplier)
                            block.Type = BlockType.H4;
                        else if (factor >= FormatIndependentStyleConstants.Heading5ToBaseFontSizeMultiplier)
                            block.Type = BlockType.H5;
                        else if (factor >= FormatIndependentStyleConstants.Heading6ToBaseFontSizeMultiplier)
                            block.Type = BlockType.H6;
                    }
                }
            }

            private void AssignBlockAlignment(RtfTextAlignment alignment)
            {
                switch (alignment)
                {
                    case RtfTextAlignment.Left:
                        block.TextAlignment = TextAlignment.Left;
                        break;
                    case RtfTextAlignment.Right:
                        block.TextAlignment = TextAlignment.Right;
                        break;
                    case RtfTextAlignment.Center:
                        block.TextAlignment = TextAlignment.Center;
                        break;
                    case RtfTextAlignment.Justify:
                        block.TextAlignment = TextAlignment.Justify;
                        break;
                }
            }

            private void ApplyCurrentFieldAndReset()
            {
                if (!string.IsNullOrEmpty(currentFieldResult))
                {
                    // Hyperlink?
                    var m = regexHyperlink.Match(currentFieldResult);

                    if (m.Success)
                    {
                        var url = m.Groups[1].Value;

                        var start = currentFieldStart;
                        var end = block.TextBuilder.Length;

                        var linkData = new LinkEntityData();
                        linkData.Href = url;
                        var entity = new Entity(EntityType.Link, EntityMutability.Mutable, JObject.FromObject(linkData));

                        var entityKey = dom.NextEntityKey();
                        var entityRange = new EntityRange(entityKey, start, end - start);
                        block.EntityRanges.Add(entityRange);
                        dom.Entities[entityKey] = entity;
                    }
                }

                // reset
                currentFieldStart = 0;
                currentFieldResult = null;
            }

            private void BuildStyleRanges(int start, int end, IRtfTextFormat format)
            {
                if (format.IsBold)
                    AddStyleRange(start, end, StyleType.Bold);

                if (format.IsItalic)
                    AddStyleRange(start, end, StyleType.Italic);

                if (format.IsUnderline)
                    AddStyleRange(start, end, StyleType.Underline);

                if (format.IsStrikeThrough)
                    AddStyleRange(start, end, StyleType.StrikeThrough);

                if (format.ForegroundColor != null && !RtfColor.Black.Equals(format.ForegroundColor))
                    AddStyleRange(start, end, StyleType.ForegroundColor, ConvertColor(format.ForegroundColor));

                if (format.BackgroundColor != null && !RtfColor.White.Equals(format.BackgroundColor))
                    AddStyleRange(start, end, StyleType.BackgroundColor, ConvertColor(format.BackgroundColor));
            }

            private void AddStyleRange(int start, int end, StyleType type, object value = null)
            {
                var range = new StyleRange(type, start, end - start, value != null ? JObject.FromObject(value) : null);
                block.StyleRanges.Add(range);
            }

            private Color ConvertColor(IRtfColor rtfColor)
            {
                var result = new Color(
                    Math.Min(rtfColor.Red, 255) / 255.0f, 
                    Math.Min(rtfColor.Green, 255) / 255.0f, 
                    Math.Min(rtfColor.Blue, 255) / 255.0f);

                return result;
            }

            public int ConvertTwipsToPixels(int twips)
            {
                return (int)(twips * (1.0 / 1440.0) * 96);
            }

            #region IRtfVisualVisitor

            public void VisitText(IRtfVisualText text)
            {
                switch (state)
                {
                    case State.DetermineListType:
                        block.Type = unorderedListValues.Contains(text.Text)
                            ? BlockType.UnorderedListItem
                            : BlockType.OrderedListItem;
                        break;
                    case State.InFieldInstruction:
                        currentFieldResult = text.Text.Trim();
                        break;
                    default:
                        var start = block.TextBuilder.Length;
                        block.TextBuilder.Append(text.Text);
                        var end = block.TextBuilder.Length;

                        BuildStyleRanges(start, end, text.Format);
                        break;
                }
            }

            public void VisitBreak(IRtfVisualBreak _break)
            {
                if (_break.BreakKind != RtfVisualBreakKind.Line)
                {
                    if (block.IsListItem())
                        block.Depth = TabPosition2ListLevel(_break.Format.ListOverrideIndex, _break.Format.LeftIndent, doc);
                    else
                    {
                        AssignHeadingType(_break.Format);
                        AssignBlockAlignment(_break.Format.Alignment);

                        // reset depth for non list blocks
                        block.Depth = 0;
                    }

                    if(!block.IsBuilderEmpty)
                        TerminateBlock();
                }
            }

            public void VisitSpecial(IRtfVisualSpecialChar specialChar)
            {
                switch (specialChar.CharKind)
                {
                    case RtfVisualSpecialCharKind.ParagraphNumberBegin:
                        state = State.DetermineListType;
                        break;
                    case RtfVisualSpecialCharKind.ParagraphNumberEnd:
                        state = State.Default;
                        break;
                    case RtfVisualSpecialCharKind.FieldBegin:
                        state = State.InField;
                        currentFieldStart = block.TextBuilder.Length;
                        break;
                    case RtfVisualSpecialCharKind.FieldEnd:
                        ApplyCurrentFieldAndReset();
                        state = State.Default;
                        break;
                    case RtfVisualSpecialCharKind.FieldInstructionBegin:
                        state = State.InFieldInstruction;
                        break;
                    case RtfVisualSpecialCharKind.FieldInstructionEnd:
                        state = State.InField;
                        break;
                    default:
                        state = State.Default;
                        break;
                }
            }

            public void VisitImage(IRtfVisualImage image)
            {
                if (!block.IsBuilderEmpty)
                     TerminateBlock();

                // create entity data
                string mimeType;

                switch (image.Format)
                {
                    case RtfVisualImageFormat.Jpg:
                        mimeType = MimeTypeProvider.MimeTypeImageJpg;
                        break;
                    case RtfVisualImageFormat.Png:
                        mimeType = MimeTypeProvider.MimeTypeImagePng;
                        break;
                    case RtfVisualImageFormat.Bmp:
                        mimeType = MimeTypeProvider.MimeTypeImageBmp;
                        break;
                    default:
                        this.Log().Warning(()=> $"RtfImporter: ignoring image with unsupported format {image.Format}");
                        return;
                }

                var task = mediaManager.AddMediaAsync(image.ImageDataBinary, mimeType);
                task.Wait();  // we're not on the UI thread
                var imageUrl = task.Result;

                var imageInfo = new ImageEntityData
                {
                    Url = imageUrl,
                    MimeType = mimeType
                };

                if (image.DesiredWidth > 0)
                    imageInfo.Width = ConvertTwipsToPixels(image.DesiredWidth);

                if (image.DesiredHeight > 0)
                    imageInfo.Height = ConvertTwipsToPixels(image.DesiredHeight);

                // create entity
                var entity = new Entity(EntityType.Image, EntityMutability.Mutable, JObject.FromObject(imageInfo));
                var entityKey = dom.NextEntityKey();
                dom.Entities[entityKey] = entity;

                block.EntityRanges.Add(new EntityRange(entityKey, block.TextBuilder.Length, 1));
                block.TextBuilder.Append(Entity.ImagePlaceholderCharacter);

                AssignBlockAlignment(image.Alignment);

                TerminateBlock();
            }

            #endregion // IRtfVisualVisitor
        }
    }
}
