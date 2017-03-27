using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ciphernote.Extensions;
using Ciphernote.IO;
using Ciphernote.Services;
using CodeContracts;

namespace Ciphernote.Media
{
    public class MediaManager
    {
        public MediaManager(IFileEx fileEx, CryptoService cryptoService, MimeTypeProvider mimeTypeProvider)
        {
            this.fileEx = fileEx;
            this.cryptoService = cryptoService;
            this.mimeTypeProvider = mimeTypeProvider;
        }

        protected readonly IFileEx fileEx;
        protected readonly CryptoService cryptoService;
        protected readonly MimeTypeProvider mimeTypeProvider;

        protected readonly Regex dataUriRegex = new Regex(@"(?<type>[^;]+)?(;base64)?,(?<data>.+)",
            RegexOptions.Compiled);

        private const string MediaFolder = "media";

        protected string ComputeMediaPath(string mediaId)
        {
            var mediaPath = Path.Combine(
                AppCoreConstants.ProfileDataBaseFolder,
                cryptoService.ProfileName,
                MediaFolder,
                new string(mediaId.Take(2).ToArray()),
                mediaId);

            return mediaPath;
        }

        public Task<string> AddMediaAsync(byte[] data, string mimeType)
        {
            Contract.RequiresNonNull(data, nameof(data));
            Contract.RequiresNonNull(mimeType, nameof(mimeType));

            return AddMediaAsync(new MemoryStream(data), mimeType);
        }

        public async Task<string> AddMediaAsync(Stream source, string mimeType)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(mimeType, nameof(mimeType));

            // compute plain-text hash
            string mediaId = (await cryptoService.ComputeContentHmacAsync(source)).ToHex();
            var filename = Path.ChangeExtension(mediaId, mimeTypeProvider.GetExtensionForMimeType(mimeType));
            var mediaPath = ComputeMediaPath(filename);

            // rewind
            if (source.CanSeek)
                source.Seek(0, SeekOrigin.Begin);

            // add extension
            await fileEx.EnsureFolderExistsAsync(AppCoreConstants.ProfileDataSpecialFolder, Path.GetDirectoryName(mediaPath));

            // open destination stream
            using (var destination = await fileEx.OpenFileForWriteAsync(AppCoreConstants.ProfileDataSpecialFolder, mediaPath))
                await cryptoService.EncryptContentAsync(source, destination);

            return BuildMediaUri(filename);
        }

        public async Task<Stream> OpenMediaAsync(string mediaId)
        {
            var source = await OpenEncryptedMediaAsync(mediaId);

            try
            {
                return await cryptoService.GetDecryptedContentStreamAsync(source);
            }

            catch
            {
                source?.Dispose();
                throw;
            }
        }

        public async Task<bool> HasValidMediaAsync(string mediaId)
        {
            try
            {
                // This will validate the content HMAC so we can be sure the content is intact
                using (await OpenMediaAsync(mediaId)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task AddEncryptedMediaAsync(Stream source, string mediaId, string mimeType)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(mimeType, nameof(mimeType));

            var filename = Path.ChangeExtension(mediaId, mimeTypeProvider.GetExtensionForMimeType(mimeType));
            var mediaPath = ComputeMediaPath(filename);

            // rewind
            if (source.CanSeek)
                source.Seek(0, SeekOrigin.Begin);

            // add extension
            await fileEx.EnsureFolderExistsAsync(AppCoreConstants.ProfileDataSpecialFolder, Path.GetDirectoryName(mediaPath));

            // open destination stream
            using (var destination = await fileEx.OpenFileForWriteAsync(AppCoreConstants.ProfileDataSpecialFolder, mediaPath))
            { 
                await source.CopyToAsync(destination);
            }

            // validate it
            if (!(await HasValidMediaAsync(mediaId)))
                throw new InvalidDataException($"Media {mediaId} failed integrity validation");
        }

        public async Task<Stream> OpenEncryptedMediaAsync(string mediaId)
        {
            Contract.RequiresNonNull(mediaId, nameof(mediaId));

            var mediaPath = ComputeMediaPath(mediaId);
            var source = await fileEx.OpenFileForReadAsync(AppCoreConstants.ProfileDataSpecialFolder, mediaPath);

            return source;
        }

        public async Task DeleteMediaAsync(string mediaId)
        {
            Contract.RequiresNonNull(mediaId, nameof(mediaId));

            var mediaPath = ComputeMediaPath(mediaId);
            await fileEx.DeleteFileAsync(AppCoreConstants.ProfileDataSpecialFolder, mediaPath);
        }

        public string BuildMediaUri(string mediaId)
        {
            Contract.RequiresNonNull(mediaId, nameof(mediaId));

            return $"{AppCoreConstants.MediaUriScheme}:{mediaId}";
        }

        public byte[] DecodeDataUri(Uri uri, out string mimeType)
        {
            Contract.RequiresNonNull(uri, nameof(uri));
            Contract.Requires<ArgumentException>(uri.Scheme == "data", "Uri scheme must be 'data'");

            var m = dataUriRegex.Match(uri.PathAndQuery);
            if (!m.Success)
                throw new FormatException("malformed data uri");

            var data = m.Groups["data"].Value;
            var result = Convert.FromBase64String(data);

            mimeType = m.Groups["type"]?.Value;
            if (string.IsNullOrEmpty(mimeType))
                mimeType = "text/plain";

            return result;
        }

        public MemoryStream DecodeDataUriToStream(Uri uri, out string mimeType)
        {
            return new MemoryStream(DecodeDataUri(uri, out mimeType));
        }
    }
}
