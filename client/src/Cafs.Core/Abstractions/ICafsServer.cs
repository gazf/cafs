using Cafs.Core.Models;

namespace Cafs.Core.Abstractions;

public interface ICafsServer
{
    Task<IReadOnlyList<FileNode>> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task<FileNode> GetFileInfoAsync(string path, CancellationToken ct = default);
    Task<Stream> DownloadFileAsync(string path, long offset = 0, long length = -1, CancellationToken ct = default);
    Task UploadFileAsync(string path, Stream content, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
    Task<LockInfo?> AcquireLockAsync(string path, CancellationToken ct = default);
    Task ReleaseLockAsync(string path, CancellationToken ct = default);
}
