using Cafs.Core.Models;

namespace Cafs.Core.Abstractions;

public interface ICafsServer
{
    Task<IReadOnlyList<FileNode>> ListDirectoryAsync(string path);
    Task<FileNode> GetFileInfoAsync(string path);
    Task<Stream> DownloadFileAsync(string path, long offset = 0, long length = -1);
    Task UploadFileAsync(string path, Stream content);
    Task DeleteFileAsync(string path);
    Task<LockInfo?> AcquireLockAsync(string path);
    Task ReleaseLockAsync(string path);
}
