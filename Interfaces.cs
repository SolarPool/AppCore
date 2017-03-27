using System;
using System.Threading.Tasks;

namespace Ciphernote
{
    public enum SearchModeSetting
    {
        FtsSearch = 1,
        SubstringSearch = 2,
    }

    public interface IAppCoreSettings
    {
        // Auth
        string Email { get; set; }
        string Password { get; set; }
        bool RememberMe { get; set; }

        bool IntroNoteCreated { get; set; }

        // User information
        Guid DeviceId { get; set; }

        // Search
        SearchModeSetting DefaultSearchMode { get; }

        // Import
        bool ImportExternalImages { get; }

#if DEBUG
        // Debug
        string ProfileNameOverride { get; }
        bool NoAutoSync { get; }
#endif
    }
}
