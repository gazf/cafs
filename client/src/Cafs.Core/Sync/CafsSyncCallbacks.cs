using System.Buffers;
using System.Diagnostics;
using Cafs.Core.Abstractions;
using CfApi.Interop;

namespace Cafs.Core.Sync;

public sealed class CafsSyncCallbacks : ISyncCallbacks
{
    private const int HydrateChunkSize = 65536;

    private readonly ICafsServer _server;

    public CafsSyncCallbacks(ICafsServer server)
    {
        _server = server;
    }

    public async Task<IReadOnlyList<PlaceholderInfo>> ListAsync(string relativePath, CancellationToken ct)
    {
        var entries = await _server.ListDirectoryAsync(relativePath).ConfigureAwait(false);
        var result = new PlaceholderInfo[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            result[i] = new PlaceholderInfo(
                e.Name,
                e.Size,
                DateTime.Parse(e.LastModified),
                e.Type == "directory");
        }
        return result;
    }

    public async Task HydrateAsync(
        string relativePath, long offset, long length, DataTransfer transfer, CancellationToken ct)
    {
        using var stream = await _server.DownloadFileAsync(relativePath, offset, length).ConfigureAwait(false);

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
}
