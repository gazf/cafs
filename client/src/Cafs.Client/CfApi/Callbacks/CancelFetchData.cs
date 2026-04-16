using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace Cafs.Client.CfApi.Callbacks;

public class CancelFetchDataHandler
{
    public void OnCallback(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        Console.WriteLine($"CancelFetchData: {callbackInfo.NormalizedPath}");
        // The FetchDataHandler checks its CancellationToken per-transfer.
        // This callback signals the intent; actual cancellation happens via the token.
    }
}
