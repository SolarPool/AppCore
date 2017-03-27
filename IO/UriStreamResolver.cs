using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Media;
using CodeContracts;

namespace Ciphernote.IO
{
    public class UriStreamResolver
    {
        public UriStreamResolver(IComponentContext ctx, IFileEx fileEx, MediaManager mediaManager)
        {
            this.ctx = ctx;
            this.fileEx = fileEx;
            this.mediaManager = mediaManager;
        }

        private readonly IFileEx fileEx;
        private readonly MediaManager mediaManager;
        private readonly IComponentContext ctx;

        public async Task<Stream> OpenUriStreamForReadAsync(Uri uri, TimeSpan? timeout = null)
        {
            Contract.RequiresNonNull(uri, nameof(uri));

            switch (uri.Scheme)
            {
                case FileExUri.Scheme:
                    string path;
                    SpecialFolders folder;
                    FileExUri.Parse(uri.AbsoluteUri, out folder, out path);
                    return await fileEx.OpenFileForReadAsync(folder, path);

                case AppCoreConstants.MediaUriScheme:
                    return await mediaManager.OpenMediaAsync(uri.AbsolutePath);

                case "http":
                case "https":
                    using (var client = ctx.Resolve<HttpClient>())
                    {
                        if (timeout.HasValue)
                            client.Timeout = timeout.Value;

                        return await client.GetStreamAsync(uri);
                    }

                default:
                    throw new NotSupportedException("unsupported uri scheme");
            }
        }

        public async Task<Stream> OpenUriStreamForWriteAsync(Uri uri)
        {
            switch (uri.Scheme)
            {
                case FileExUri.Scheme:
                    string path;
                    SpecialFolders folder;
                    FileExUri.Parse(uri.AbsoluteUri, out folder, out path);
                    return await fileEx.OpenFileForWriteAsync(folder, path);

                case "http":
                case "https":
                    throw new NotSupportedException("writing to http streams is not supported");

                default:
                    throw new NotSupportedException("unsupported uri scheme");
            }
        }
    }
}
