namespace Ciphernote.IO
{
    public enum SpecialFolders
    {
        /// <summary>
        /// Folder accessible to application without special permissions
        /// </summary>
        AppData,

        /// <summary>
        /// Roaming Folder accessible to application without special permissions
        /// </summary>
        AppDataRoaming,

        /// <summary>
        /// Application Package data (read-only)
        /// </summary>
        AppPackage,

        /// <summary>
        /// MyDocuments Folder that might require special permissions
        /// </summary>
        MyDocuments,

        /// <summary>
        /// MyPictures Folder that might require special permissions
        /// </summary>
        MyPictures,

        /// <summary>
        /// Temp Folder
        /// </summary>
        Temp
    }
}
