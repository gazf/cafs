using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using Cafs.Client.Http;

namespace Cafs.Client.CfApi.Callbacks;

public class NotifyDeleteHandler
{
    private readonly CafsHttpClient _httpClient;
    private readonly string _syncRootPath;

    public NotifyDeleteHandler(CafsHttpClient httpClient, string syncRootPath)
    {
        _httpClient = httpClient;
        _syncRootPath = syncRootPath;
    }

    public void OnCallback(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        Task.Run(async () =>
        {
            try
            {
                var relativePath = CallbackHelper.GetRelativePath(callbackInfo, _syncRootPath);
                Console.WriteLine($"NotifyDelete: {relativePath}");

                await _httpClient.DeleteFileAsync(relativePath);

                var ackParams = new CF_OPERATION_PARAMETERS
                {
                    AckDelete = new CF_OPERATION_PARAMETERS.ACKDELETE
                    {
                        Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
                        CompletionStatus = new NTStatus(0)
                    }
                };

                CfExecute(
                    callbackInfo.ConnectionKey,
                    callbackInfo.TransferKey,
                    CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
                    ackParams
                );
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"NotifyDelete error: {ex.Message}");
            }
        }).Wait();
    }
}
