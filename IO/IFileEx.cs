using System.IO;
using System.Threading.Tasks;

namespace Ciphernote.IO
{
    public interface IFileEx
    {
        Stream OpenFileForReadSync(SpecialFolders specialFolder, string path);
        Task<Stream> OpenFileForReadAsync(SpecialFolders specialFolder, string path);
        Task<Stream> OpenFileForWriteAsync(SpecialFolders specialFolder, string path);
        Task<string> ReadAllTextAsync(SpecialFolders specialFolder, string path);
        Task WriteAllTextAsync(SpecialFolders specialFolder, string path, string content);
        Task<bool> DeleteFileAsync(SpecialFolders specialFolder, string path);
        Task<bool> RenameFileAsync(SpecialFolders specialFolder, string oldName, string newName);
        Task<bool> FileExistsAsync(SpecialFolders specialFolder, string path);
        Task<bool> FolderExistsAsync(SpecialFolders specialFolders, string folder);
        Task EnsureFolderExistsAsync(SpecialFolders specialFolder, string folder);
        Task<string[]> EnumerateFilesAsync(SpecialFolders specialFolder, string folder);
        Task DeleteFolderAsync(SpecialFolders specialFolder, string folder);
    }
}
