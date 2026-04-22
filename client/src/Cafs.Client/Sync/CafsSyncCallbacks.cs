using System.Buffers;
using System.Diagnostics;
using Cafs.Client.Http;
using CfApi.Interop;

namespace Cafs.Client.Sync;

public sealed class CafsSyncCallbacks : ISyncCallbacks
{
    private const int HydrateChunkSize = 65536;

    private readonly CafsHttpClient _httpClient;

    public CafsSyncCallbacks(CafsHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<PlaceholderInfo>> ListAsync(string relativePath, CancellationToken ct)
    {
        var entries = await _httpClient.ListDirectoryAsync(relativePath).ConfigureAwait(false);
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
        using var stream = await _httpClient.DownloadFileAsync(relativePath, offset, length).ConfigureAwait(false);

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
            await _httpClient.DeleteFileAsync(relativePath).ConfigureAwait(false);
            return 0;
        }
        catch (CafsApiException ex) when (ex.StatusCode == 404)
        {
            // サーバが知らないファイル — stale な local placeholder。ローカル削除は通す。
            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NotifyDelete server error: {ex.Message}");
            return unchecked((int)0xC0000022); // STATUS_ACCESS_DENIED
        }
    }
}
