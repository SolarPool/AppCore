using System;
using System.Net;

namespace Ciphernote.IO
{
    public static class FileExUri
    {
        public const string Scheme = "filex";
        public const string PathSeparator = "/";

        public static string Build(SpecialFolders folder, string path)
        {
            // Convert windows seperators
            path = path.Replace(@"\", PathSeparator);

            // strip leading slashes
            if (path.StartsWith(PathSeparator))
                path = path.Substring(1);

            return $"{Scheme}://{folder.ToString().ToLower()}/{path}";
        }

        public static void Parse(string url, out SpecialFolders folder, out string path)
        {
            var uri = new Uri(url);

            // verify scheme
            if(uri.Scheme != Scheme)
                throw new NotSupportedException(uri.Scheme);

            // folder
            folder = (SpecialFolders)Enum.Parse(typeof(SpecialFolders), uri.Host, true);

            // path
            path = WebUtility.UrlDecode(uri.AbsolutePath);
        }
    }
}
