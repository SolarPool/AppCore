using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeContracts;

namespace Ciphernote.Media
{
    public class MimeTypeProvider
    {
        public MimeTypeProvider()
        {
            extension2mimeType = new Dictionary<string, string>();

            foreach (var mimeType in mimeType2Extension.Keys)
            {
                var extensions = mimeType2Extension[mimeType];

                foreach (var extension in extensions)
                {
                    extension2mimeType[extension] = mimeType;
                }
            }
        }

        public const string MimeTypeImageJpg = "image/jpeg";
        public const string MimeTypeImagePng = "image/png";
        public const string MimeTypeImageGif = "image/gif";
        public const string MimeTypeImageBmp = "image/bmp";
        public const string MimeTypeAudioSpeex = "audio/speex";

        private readonly Dictionary<string, string[]> mimeType2Extension = new Dictionary<string, string[]>
        {
            {MimeTypeImageJpg, new []{ ".jpg", ".jpeg" }},
            {MimeTypeImagePng, new []{ ".png" }},
            {MimeTypeImageGif, new []{ ".gif" }},
            {MimeTypeImageBmp, new []{ ".bmp" }},
            {MimeTypeAudioSpeex, new []{ ".spx" }},
        };

        private readonly Dictionary<string, string> extension2mimeType;

        public string GetMimeTypeForExtension(string extension)
        {
            Contract.RequiresNonNull(extension, nameof(extension));

            string result;
            if(extension2mimeType.TryGetValue(extension, out result))
                return result;

            return null;
        }

        public string GetMimeTypeForExtension(Uri uri)
        {
            Contract.RequiresNonNull(uri, nameof(uri));

            if (uri.IsAbsoluteUri)
                return GetMimeTypeForExtension(Path.GetExtension(uri.AbsolutePath));

            return GetMimeTypeForExtension(Path.GetExtension(uri.OriginalString));
        }

        public string GetExtensionForMimeType(string mimeType)
        {
            Contract.RequiresNonNull(mimeType, nameof(mimeType));

            string[] result;
            if(mimeType2Extension.TryGetValue(mimeType, out result))
                return result.FirstOrDefault();

            return null;
        }
    }
}
