using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using Autofac;
using Ciphernote.Extensions;
using Ciphernote.Logging;
using Ciphernote.Media;
using Ciphernote.Model;
using CodeContracts;

namespace Ciphernote.Importers.Notes
{
    public class EnexImporter : INoteImporter
    {
        public EnexImporter(MediaManager mediaManager, IEnumerable<IDomImporter> importers,
            IComponentContext ctx)
        {
            this.mediaManager = mediaManager;
            this.htmlImporter = importers.First(x => x.SupportedMimeTypes.Contains("text/html"));
            this.ctx = ctx;
        }

        private readonly MediaManager mediaManager;
        readonly Regex regexTimestamp = new Regex(@"(\d\d\d\d)(\d\d)(\d\d)T(\d\d)(\d\d)(\d\d)Z", RegexOptions.Compiled);
        readonly Regex regexCondenseWhitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private readonly IComponentContext ctx;
        private readonly IDomImporter htmlImporter;

        private const string EnMediaGuid = "846FA596-90CF-4633-94FF-90678126A0A0";
        private static readonly string EnMediaPlaceHolder = $"<img data-en-media=\"{EnMediaGuid}\"";
        private static readonly string EnMediaSelector = $"img[data-en-media=\'{EnMediaGuid}\']"; // "en-media";
        private const string EnWebClipper = "web.clip";

        class ResourceInfo
        {
            public byte[] Data;
            public string Filename;
            public string MimeType;
        }

        #region INoteImporter

        public string Title => "Evernote Enex Importer";

        public string[] HelpImages { get; } =
        {
            "Evernote/Step1.png",
            "Evernote/Step2.png",
            "Evernote/Step3.png",
            "Evernote/Step4.png",
        };

        public string[] SupportedFileExtensions { get; } =
        {
            ".enex",
        };

        public IObservable<Note> ImportNotes(Stream stm, NoteImportOptions options, CancellationToken ct)
        {
            Contract.RequiresNonNull(stm, nameof(stm));
            Contract.RequiresNonNull(options, nameof(options));

            return Observable.Defer(() =>
            {
                return Observable.Create<Note>(observer => DoImport(observer, stm, options, ct));
            });
        }

        public async Task<int> GetImportStatsAsync(Stream stm, CancellationToken ct)
        {
            Contract.RequiresNonNull(stm, nameof(stm));

            return await CountNotesAsync(stm, ct);
        }

        #endregion // INoteImporter

        private async Task<int> CountNotesAsync(Stream stm, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                // initialize nametable
                var nt = new NameTable();
                var noteName = nt.Add("note");

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    Async = true,
                    NameTable = nt
                };

                int count = 0;

                using (var reader = XmlReader.Create(stm, settings))
                {
                    DisableUndeclaredEntityCheck(reader);

                    try
                    {
                        reader.MoveToContent();

                        while (!ct.IsCancellationRequested && reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.Element)
                            {
                                if (reader.LocalName == noteName)
                                    count++;
                            }
                        }
                    }

                    catch (XmlException)
                    {
                        // deliberately left blank
                    }
                }

                return count;
            });
        }

        private IDisposable DoImport(IObserver<Note> observer, Stream stm, NoteImportOptions options,
            CancellationToken ct)
        {
            var abort = false;

            Task.Run(async () =>
            {
                // initialize nametable
                var nt = new NameTable();
                var noteName = nt.Add("note");
                var titleName = nt.Add("title");
                var updatedName = nt.Add("updated");
                var createdName = nt.Add("created");
                var tagName = nt.Add("tag");
                var resourceDataName = nt.Add("data");
                var resourceFilenameName = nt.Add("file-name");
                var resourceMimeName = nt.Add("mime");
                var contentName = nt.Add("content");
                var sourceName = nt.Add("source");
                var LatitudeName = nt.Add("latitude");
                var LongitudeName = nt.Add("longitude");
                var AltitudeName = nt.Add("altitude");
                var sourceApplicationName = nt.Add("source-application");

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    Async = true,
                    NameTable = nt
                };

                int count = 0;
                Dictionary<string, ResourceInfo> resources = null;
                List<string> tags = null;
                string noteContent = null;
                string currentElement = null;
                string source = null;
                string resourceHash = null;
                string sourceApplication = null;
                double? latitude = null;
                double? longitude = null;
                double? altitude = null;
                Note note = null;
                DateTime? timestamp = null;

                using (var md5 = MD5.Create())
                {
                    using (var reader = XmlReader.Create(stm, settings))
                    {
                        DisableUndeclaredEntityCheck(reader);

                        try
                        {
                            reader.MoveToContent();

                            while (!abort && !ct.IsCancellationRequested && reader.Read())
                            {
                                if (reader.NodeType == XmlNodeType.Element)
                                {
                                    if (reader.LocalName == noteName)
                                    {
                                        note = new Note();

                                        resources = new Dictionary<string, ResourceInfo>();
                                        tags = new List<string>();
                                        noteContent = "";
                                        currentElement = "";
                                        source = "";
                                        resourceHash = null;
                                        sourceApplication = "";
                                        latitude = null;
                                        longitude = null;
                                        altitude = null;
                                    }

                                    else
                                        currentElement = reader.LocalName;
                                }

                                else if (reader.NodeType == XmlNodeType.EndElement)
                                {
                                    if (reader.LocalName == noteName)
                                    {
                                        if (!string.IsNullOrEmpty(noteContent))
                                        {
                                            var htmlDocument = await BuildHtmlDocument(noteContent, resources);

                                            if (htmlDocument != null)
                                            {
                                                var noteTextContent = htmlDocument.Body.TextContent;

                                                note.Tags.Clear();
                                                note.Tags.AddRange(tags.ToArray());

                                                // if note does not have a title yet, derive one from content
                                                if (string.IsNullOrEmpty(note.Title) &&
                                                    !string.IsNullOrEmpty(noteTextContent))
                                                {
                                                    noteTextContent = regexCondenseWhitespace.Replace(noteTextContent,
                                                        " ");
                                                    note.Title = noteTextContent.Substring(0,
                                                        Math.Min(32, noteTextContent.Length - 1));
                                                    note.Title += "...";
                                                }

                                                note.Source = "evernote";
                                                if (!string.IsNullOrEmpty(source))
                                                    note.Source += $".{source}";

                                                // location
                                                if (latitude.HasValue && longitude.HasValue)
                                                {
                                                    note.HasLocation = true;
                                                    note.Latitude = latitude;
                                                    note.Longitude = longitude;
                                                    note.Altitude = altitude;
                                                }

                                                //if (source != EnWebClipper)
                                                {
                                                    // finally convert to Ciphernote Dom
                                                    var dom = await htmlImporter.ImportDomAsync(htmlDocument, options.DomImportOptions);

                                                    // update some fields
                                                    note.TodoProgress = dom.CalculateTodoProgress();

                                                    // save dom to body
                                                    note.Body = await dom.SaveAsync();
                                                    note.BodyMimeType = AppCoreConstants.DomMimeType;
                                                }

                                                //else
                                                //{
                                                //    // store html
                                                //    var writer = new StringWriter();
                                                //    htmlDocument.ToHtml(writer, HtmlMarkupFormatter.Instance);

                                                //    // save dom to body
                                                //    note.Body = writer.ToString();
                                                //    note.BodyMimeType = "text/html";
                                                //}

                                                // done
                                                observer.OnNext(note);
                                                count++;
                                            }

                                            else
                                                observer.OnNext(null);
                                        }

                                        else
                                            observer.OnNext(null);
                                    }
                                }

                                else if (reader.NodeType == XmlNodeType.Text)
                                {
                                    if (currentElement == titleName)
                                    {
                                        note.Title = reader.Value;
                                    }

                                    if (currentElement == tagName)
                                    {
                                        tags.Add(reader.Value);
                                    }

                                    else if (currentElement == createdName)
                                    {
                                        timestamp = ParseDate(reader.Value);

                                        if(timestamp.HasValue)
                                            note.Timestamp = timestamp.Value;
                                    }

                                    else if (currentElement == updatedName)
                                    {
                                        timestamp = ParseDate(reader.Value);

                                        if (timestamp.HasValue)
                                            note.Timestamp = timestamp.Value;
                                    }

                                    else if (currentElement == sourceName)
                                    {
                                        source = reader.Value;
                                    }

                                    else if (currentElement == sourceApplicationName)
                                    {
                                        sourceApplication = reader.Value;
                                    }

                                    else if (currentElement == LatitudeName)
                                    {
                                        double val;
                                        if (double.TryParse(reader.Value, NumberStyles.Float,
                                            CultureInfo.InvariantCulture, out val))
                                            latitude = val;
                                    }

                                    else if (currentElement == LongitudeName)
                                    {
                                        double val;
                                        if (double.TryParse(reader.Value, NumberStyles.Float,
                                            CultureInfo.InvariantCulture, out val))
                                            longitude = val;
                                    }

                                    else if (currentElement == AltitudeName)
                                    {
                                        double val;
                                        if (double.TryParse(reader.Value, NumberStyles.Float,
                                            CultureInfo.InvariantCulture, out val))
                                            altitude = val;
                                    }

                                    else if (currentElement == resourceDataName)
                                    {
                                        var resourceData = Convert.FromBase64String(reader.Value);
                                        resourceHash = md5.ComputeHash(resourceData).ToHex().ToLower();

                                        resources[resourceHash] = new ResourceInfo {Data = resourceData};
                                    }

                                    else if (currentElement == resourceFilenameName)
                                    {
                                        if (!string.IsNullOrEmpty(resourceHash) && resources.ContainsKey(resourceHash))
                                            resources[resourceHash].Filename = reader.Value;
                                    }

                                    else if (currentElement == resourceMimeName)
                                    {
                                        if (!string.IsNullOrEmpty(resourceHash) && resources.ContainsKey(resourceHash))
                                            resources[resourceHash].MimeType = reader.Value;
                                    }
                                }

                                else if (reader.NodeType == XmlNodeType.CDATA)
                                {
                                    if (currentElement == contentName)
                                    {
                                        noteContent = reader.Value;
                                    }
                                }
                            }

                        }

                        catch (XmlException)
                        {
                            // deliberately left blank
                        }
                    }
                }

                observer.OnCompleted();
            }, ct);

            return Disposable.Create(() => abort = true);
        }

        private static void DisableUndeclaredEntityCheck(XmlReader reader)
        {
            var propertyInfo = reader.GetType().GetProperty("CoreReader",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (propertyInfo != null)
            {
                var coreReader = propertyInfo.GetValue(reader);

                if (coreReader != null)
                {
                    propertyInfo = coreReader.GetType().GetProperty("DisableUndeclaredEntityCheck",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (propertyInfo != null)
                    {
                        propertyInfo.SetValue(coreReader, true);
                    }
                }
            }
        }

        private DateTime? ParseDate(string value)
        {
            var m = regexTimestamp.Match(value);

            if (m.Success)
            {
                return new DateTime(int.Parse(m.Groups[1].Value),
                    int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value),
                    int.Parse(m.Groups[5].Value),
                    int.Parse(m.Groups[6].Value), DateTimeKind.Utc);
            }

            return null;
        }

        private async Task<IHtmlDocument> BuildHtmlDocument(string content, Dictionary<string, ResourceInfo> resources)
        {
            try
            {
                // extract markup
                var start = content.IndexOf("<en-note");
                var end = content.IndexOf("</en-note>");

                if (start == -1 || end == -1)
                    return null;

                start = content.IndexOf(">", start) + 1;
                content = content.Substring(start, end - start);

                // workaround for anglesharp bug
                content = content.Replace("<en-media", EnMediaPlaceHolder);

                // wrap
                content =
                    "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\"><html><body>" +
                    content + "</body></html>";

                var configuration = new Configuration().WithCss();
                var parser = new HtmlParser(configuration);
                var doc = await parser.ParseAsync(content);

                if (doc.HasChildNodes)
                {
                    // find media references ("en-media" elements)
                    var enMediaRefs = doc.QuerySelectorAll(EnMediaSelector).ToArray();

                    foreach (var element in enMediaRefs)
                        await ProcessMediaRef(doc, element, resources);

                    return doc;
                }
            }

            catch (Exception)
            {
                // deliberately left blank
            }

            return null;
        }

        private async Task ProcessMediaRef(IHtmlDocument doc, IElement element,
            Dictionary<string, ResourceInfo> resources)
        {
            Debug.Assert(element.ChildElementCount == 0);

            var mimeType = element.GetAttribute("type");
            var hash = element.GetAttribute("hash").ToLower();
            var isElementDetached = false;

            if (resources.ContainsKey(hash))
            {
                switch (mimeType)
                {
                    case "image/jpeg":
                    case "image/png":
                    case "image/gif":
                        isElementDetached = await ProcessImageMedia(doc, element, resources, hash, mimeType);
                        break;

                    case "audio/wav":
                        isElementDetached = await ProcessAudioMedia(doc, element, resources, hash, mimeType);
                        break;
                }
            }

            if (!isElementDetached)
                element.Remove();
        }

        private async Task<bool> ProcessImageMedia(IHtmlDocument doc, IElement element,
            Dictionary<string, ResourceInfo> resources, string resourceId, string mimeType)
        {
            var resourceInfo = resources[resourceId];

            // query addtional attributes
            var width = element.GetAttribute("width");
            var height = element.GetAttribute("height");

            // add to media library
            string url = await mediaManager.AddMediaAsync(resourceInfo.Data, mimeType);

            // create image
            var img = doc.CreateElement("img");
            img.SetAttribute("src", url);

            if (!string.IsNullOrEmpty(width))
                img.SetAttribute("width", width);
            if (!string.IsNullOrEmpty(height))
                img.SetAttribute("height", height);

            // replace element with img
            element.Parent.ReplaceChild(img, element);
            return true;
        }

        private async Task<bool> ProcessAudioMedia(IHtmlDocument doc, IElement element,
            Dictionary<string, ResourceInfo> resources, string resourceId, string mimeType)
        {
            var wavCodec = ctx.ResolveKeyed<IPcmAudioDecoder>(mimeType);
            var resourceInfo = resources[resourceId];

            // Decode wav
            var source = new MemoryStream(resourceInfo.Data);
            var tempFilePath = Path.GetTempFileName();

            try
            {
                MemoryStream speexStream;

                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite))
                {
                    using (var decoderStream = wavCodec.GetDecoderStream(source))
                        await decoderStream.CopyToAsync(fileStream);

                    // Encode as speex
                    var speexEncoder = ctx.ResolveKeyed<IAudioEncoder>(MimeTypeProvider.MimeTypeAudioSpeex);
                    fileStream.Seek(0, SeekOrigin.Begin);
                    speexStream = new MemoryStream();
                    await speexEncoder.EncodeAsync(fileStream.ToObservable<short>(), speexStream).ToTask();
                }

                // add to media library
                mimeType = MimeTypeProvider.MimeTypeAudioSpeex;
                speexStream.Seek(0, SeekOrigin.Begin);
                string url = await mediaManager.AddMediaAsync(speexStream, mimeType);

                // create audio element
                var audio = doc.CreateElement("audio");
                audio.SetAttribute("src", url);

                var length = Math.Round(speexStream.Length/2.8634d);
                // CalculateSpeexLength(destination.Length / 2575d, 2575, 52, 16000);
                audio.SetAttribute("data-length-ms", length.ToString(CultureInfo.InvariantCulture));

                if (!string.IsNullOrEmpty(resourceInfo.Filename))
                    audio.SetAttribute("title", Path.GetFileNameWithoutExtension(resourceInfo.Filename));

                // replace element with img
                element.Parent.ReplaceChild(audio, element);
                return true;
            }

            finally
            {
                try
                {
                    File.Delete(tempFilePath);
                }

                catch (IOException)
                {
                }
            }
        }
    }
}
