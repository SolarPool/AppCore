using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Dom;
using Ciphernote.Dom.EntityData;
using Ciphernote.IO;
using Ciphernote.Media;
using Ciphernote.Model;
using CodeContracts;
using Newtonsoft.Json.Linq;
using Splat;

namespace Ciphernote.Importers.Dom
{
    public static class DomImportHelper
    {
        public static async Task<int> ImportImages(Document dom, IComponentContext ctx, bool importExternal, IFullLogger logger)
        {
            Contract.RequiresNonNull(dom, nameof(dom));
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(logger, nameof(logger));

            var mediaManager = ctx.Resolve<MediaManager>();
            var mimeTypeProvider = ctx.Resolve<MimeTypeProvider>();

            var downHosts = new HashSet<string>();

            return await Task.Run(async () =>
            {
                var failedImages = new List<Entity>();

                var images = dom.Entities.Values.Where(x => x.Type == EntityType.Image)
                    .ToArray();

                foreach (var img in images)
                {
                    var info = img.Data.ToObject<ImageEntityData>();

                    Uri uri;
                    if (!Uri.TryCreate(info.Url, UriKind.RelativeOrAbsolute, out uri))
                        continue;

                    if (!uri.IsAbsoluteUri)
                        continue;

                    if (downHosts.Contains(uri.Host))
                    {
                        failedImages.Add(img);
                        continue; 
                    }

                    try
                    {
                        Stream source;
                        string mimeType;

                        switch (uri.Scheme)
                        {
                            case "http":
                            case "https":
                                if(!importExternal)
                                    continue;

                                if (await IsHostAvailable(uri, ctx))
                                {
                                    mimeType = mimeTypeProvider.GetMimeTypeForExtension(uri) ?? "image/jpeg";

                                    using (var client = ctx.Resolve<HttpClient>())
                                    {
                                        source = await client.GetStreamAsync(uri);
                                        var imgSrc = await mediaManager.AddMediaAsync(source, mimeType);

                                        info.Url = imgSrc;
                                        img.Data = JObject.FromObject(info);
                                    }
                                }

                                else
                                {
                                    failedImages.Add(img);
                                    downHosts.Add(uri.Host);
                                }
                                break;

                            case "data":
                                using (source = mediaManager.DecodeDataUriToStream(uri, out mimeType))
                                {
                                    var imgSrc = await mediaManager.AddMediaAsync(source, mimeType);

                                    info.Url = imgSrc;
                                    img.Data = JObject.FromObject(info);
                                }
                                break;
                        }
                    }

                    catch (Exception ex)
                    {
                        logger.ErrorException(nameof(ImportImages), ex);

                        failedImages.Add(img);
                    }
                }

                // remove failed images from DOM
                if (failedImages.Any())
                {
                    var entitiesReverse = dom.Entities.Keys.ToDictionary(x => dom.Entities[x], x => x);

                    foreach (var entity in failedImages)
                    {
                        foreach (var block in dom.Blocks)
                        {
                            // remove ranges
                            var ranges = block.EntityRanges
                                .Where(x => dom.Entities[x.Entity] == entity)
                                .ToArray();

                            foreach (var range in ranges)
                                block.EntityRanges.Remove(range);
                        }

                        // remove entity itself
                        var key = entitiesReverse[entity];
                        dom.Entities.Remove(key);
                    }
                }

                return failedImages.Count;
            });
        }

        private static async Task<bool> IsHostAvailable(Uri uri, IComponentContext ctx)
        {
            try
            {
                using (var client = ctx.Resolve<HttpClient>())
                {
                    client.Timeout = TimeSpan.FromMilliseconds(10000);

                    var request = new HttpRequestMessage(HttpMethod.Head, uri);
                    var response = await client.SendAsync(request);

                    return true;
                }
            }

            catch (Exception)
            {
                return false;
            }
        }
    }
}
