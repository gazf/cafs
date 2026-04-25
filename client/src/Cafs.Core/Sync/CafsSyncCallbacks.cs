using System.Buffers;
using System.Diagnostics;
using Cafs.Core.Abstractions;
using CfApi.Interop;

namespace Cafs.Core.Sync;

public sealed class CafsSyncCallbacks : ISyncCallbacks
{
    private const int HydrateChunkSize = 65536;

    private readonly ICafsServer _server;
    private readonly string _syncRootPath;

    public CafsSyncCallbacks(ICafsServer server, string syncRootPath)
    {
        _server = server;
        _syncRootPath = syncRootPath;
    }

    public async Task HydrateAsync(
        string relativePath, long offset, long length, DataTransfer transfer, CancellationToken ct)
    {
        using var stream = await _server.DownloadFileAsync(relativePath, offset, length, ct).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(HydrateChunkSize);
        try
        {
            long currentOffset = offset;
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, HydrateChunkSize), ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                transfer.Write(buffer.AsSpan(0, bytesRead), currentOffset);
                currentOffset += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<int> OnDeleteAsync(string relativePath)
    {
        try
        {
            await _server.DeleteFileAsync(relativePath).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NotifyDelete server error: {ex.Message}");
            return unchecked((int)0xC0000022); // STATUS_ACCESS_DENIED
        }
    }

    public async Task OnFileOpenAsync(string relativePath)
    {
        try
        {
            await _server.AcquireLockAsync(relativePath).ConfigureAwait(false);
            Trace.WriteLine($"Lock acquired: {relativePath}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AcquireLock failed: {relativePath}: {ex.Message}");
        }
    }

    public async Task OnFileCloseAsync(string relativePath, bool isDeleted, bool isModified)
    {
        try
        {
            if (isModified && !isDeleted)
            {
                var localPath = Path.Combine(_syncRootPath, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                await using var stream = File.OpenRead(localPath);
                await _server.UploadFileAsync(relativePath, stream).ConfigureAwait(false);
                Trace.WriteLine($"Uploaded: {relativePath}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Upload failed: {relativePath}: {ex.Message}");
        }
        finally
        {
            if (!isDeleted)
            {
                try
                {
                    await _server.ReleaseLockAsync(relativePath).ConfigureAwait(false);
                    Trace.WriteLine($"Lock released: {relativePath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"ReleaseLock failed: {relativePath}: {ex.Message}");
                }
            }
        }
    }
}
