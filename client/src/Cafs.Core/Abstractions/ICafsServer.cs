using Cafs.Core.Models;

namespace Cafs.Core.Abstractions;

public interface ICafsServer
{
    Task<IReadOnlyList<FileNode>> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<TreeNode>> GetTreeAsync(CancellationToken ct = default);
    Task<FileNode> GetFileInfoAsync(string path, CancellationToken ct = default);
    Task<HydratedContent> DownloadFileAsync(string path, long offset = 0, long length = -1, CancellationToken ct = default);
    Task<UploadResult> UploadFileAsync(string path, Stream content, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 排他ロックを取得する。同一ユーザーで既存ロックがある場合は expiresAt を延長する (renew として使える)。
    /// 他ユーザーが保持している場合は null を返す (HTTP 409)。
    /// </summary>
    Task<LockInfo?> AcquireLockAsync(string path, CancellationToken ct = default);

    Task ReleaseLockAsync(string path, CancellationToken ct = default);
}
