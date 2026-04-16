using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using Cafs.Client.Http;

namespace Cafs.Client.CfApi.Callbacks;

public class FetchDataHandler
{
    private const int ChunkSize = 65536; // 64KB
    private readonly CafsHttpClient _httpClient;
    private readonly string _syncRootPath;

    // Track active downloads for cancellation
    private readonly Dictionary<long, CancellationTokenSource> _activeDownloads = new();
    private readonly object _lock = new();

    public FetchDataHandler(CafsHttpClient httpClient, string syncRootPath)
    {
        _httpClient = httpClient;
        _syncRootPath = syncRootPath;
    }

    public void OnCallback(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var transferKey = callbackInfo.TransferKey;
        var connectionKey = callbackInfo.ConnectionKey;
        var requiredOffset = callbackParameters.FetchData.RequiredFileOffset;
        var requiredLength = callbackParameters.FetchData.RequiredLength;
        var relativePath = CallbackHelper.GetRelativePath(callbackInfo, _syncRootPath);

        var cts = new CancellationTokenSource();
        var transferKeyHash = transferKey.GetHashCode();

        lock (_lock)
        {
            _activeDownloads[transferKeyHash] = cts;
        }

        Task.Run(async () =>
        {
            try
            {
                Console.WriteLine($"FetchData: {relativePath} offset={requiredOffset} length={requiredLength}");

                using var stream = await _httpClient.DownloadFileAsync(
                    relativePath, requiredOffset, requiredLength);

                long currentOffset = requiredOffset;
                var buffer = new byte[ChunkSize];

                while (!cts.Token.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, cts.Token);
                    if (bytesRead == 0) break;

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    unsafe
                    {
                        fixed (byte* pData = data)
                        {
                            var transferParams = new CF_OPERATION_PARAMETERS
                            {
                                TransferData = new CF_OPERATION_PARAMETERS.TRANSFERDATA
                                {
                                    Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                                    CompletionStatus = new NTStatus(0),
                                    Buffer = (IntPtr)pData,
                                    Offset = currentOffset,
                                    Length = bytesRead
                                }
                            };

                            var hr = CfExecute(
                                connectionKey,
                                transferKey,
                                CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                                transferParams
                            );

                            if (hr.Failed)
                            {
                                Console.Error.WriteLine($"CfExecute TransferData failed: 0x{hr:X8}");
                                return;
                            }
                        }
                    }

                    currentOffset += bytesRead;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"FetchData cancelled: {relativePath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FetchData error: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _activeDownloads.Remove(transferKeyHash);
                }
            }
        }).Wait();
    }

    public void CancelTransfer(long transferKeyHash)
    {
        lock (_lock)
        {
            if (_activeDownloads.TryGetValue(transferKeyHash, out var cts))
            {
                cts.Cancel();
            }
        }
    }
}
