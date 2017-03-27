using System.IO;
using Ciphernote.IO;

namespace Ciphernote
{
    public class AppCoreConstants
    {
        public const string AppName = "Ciphernote";
        public const string DomMimeType = "application/ciphernote";
        public const string MediaUriScheme = "cn-media";
        public const string NoteSourceCiphernote = "ciphernote";

        // Profile location
        public const SpecialFolders ProfileDataSpecialFolder = SpecialFolders.AppData;
        public const string DatabaseFilename = "ciphernote.db";
        public const string EncryptedMasterKeyFilename = ".mk";
        public const string EncryptedAccountKeyFilename = ".ak";

        public const int MaxTagLength = 32;

#if DEBUG
        public const string ApiEndpoint = "http://localhost:56000";
        public static readonly string ProfileDataBaseFolder = AppName + " Profiles (Dev)";
#else
        public const string ApiEndpoint = "https://www.ciphernote.net";
        public static readonly string ProfileDataBaseFolder = AppName + " Profiles";
#endif
    }
}
