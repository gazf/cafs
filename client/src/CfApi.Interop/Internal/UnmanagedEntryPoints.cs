using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CfApi.Native;

namespace CfApi.Interop.Internal;

internal static class UnmanagedEntryPoints
{
    public static int RegistrationTableSize => 4;

    public static unsafe void BuildRegistrationTable(Span<CF_CALLBACK_REGISTRATION> table)
    {
        table[0] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
            Callback = &OnFetchData,
        };
        table[1] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
            Callback = &OnCancelFetchData,
        };
        table[2] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
            Callback = &OnNotifyDelete,
        };
        table[3] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
            Callback = null,
        };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var ctx = SyncContext.FromPointer(info->CallbackContext);
        var relativePath = Marshaller.GetRelativePath(info, ctx.SyncRootPath);
        var connectionKey = info->ConnectionKey;
        var transferKey = info->TransferKey;
        var requestKey = info->RequestKey;
        var requiredOffset = parameters->Union.FetchData.RequiredFileOffset;
        var requiredLength = parameters->Union.FetchData.RequiredLength;

        var cts = new CancellationTokenSource();
        ctx.ActiveFetches[transferKey] = cts;
        _ = DispatchFetchDataAsync(ctx, relativePath, connectionKey, transferKey, requestKey, requiredOffset, requiredLength, cts);
    }

    private static async Task DispatchFetchDataAsync(
        SyncContext ctx, string relativePath,
        ulong connectionKey, long transferKey, long requestKey,
        long requiredOffset, long requiredLength, CancellationTokenSource cts)
    {
        var transfer = new DataTransfer(connectionKey, transferKey, requestKey);
        int completionStatus = 0;
        try
        {
            Trace.WriteLine($"FetchData: {relativePath} offset={requiredOffset} length={requiredLength}");
            await ctx.Callbacks.HydrateAsync(relativePath, requiredOffset, requiredLength, transfer, cts.Token)
                .ConfigureAwait(false);
            Trace.WriteLine($"FetchData: {relativePath} complete");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FetchData error: {ex.Message}");
            completionStatus = unchecked((int)0xC0000001); // STATUS_UNSUCCESSFUL
        }
        finally
        {
            // Signal CfApi that transfer is complete (required even on success).
            transfer.Complete(completionStatus);
            ctx.ActiveFetches.TryRemove(transferKey, out _);
            cts.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var ctx = SyncContext.FromPointer(info->CallbackContext);
            var transferKey = info->TransferKey;
            Trace.WriteLine($"CancelFetchData: transferKey={transferKey}");
            if (ctx.ActiveFetches.TryGetValue(transferKey, out var cts))
                cts.Cancel();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"CancelFetchData error: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnNotifyDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var ctx = SyncContext.FromPointer(info->CallbackContext);
        var relativePath = Marshaller.GetRelativePath(info, ctx.SyncRootPath);
        var connectionKey = info->ConnectionKey;
        var transferKey = info->TransferKey;
        var requestKey = info->RequestKey;

        Trace.WriteLine($"NotifyDelete: {relativePath}");

        // Sync root 自身の削除はサーバへ伝播させない。
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            Trace.WriteLine("NotifyDelete: refusing root deletion — ack without server call.");
            CfOperations.AckDelete(connectionKey, transferKey, requestKey, 0);
            return;
        }

        _ = DispatchNotifyDeleteAsync(ctx, relativePath, connectionKey, transferKey, requestKey);
    }

    private static async Task DispatchNotifyDeleteAsync(
        SyncContext ctx, string relativePath,
        ulong connectionKey, long transferKey, long requestKey)
    {
        int status;
        try
        {
            status = await ctx.Callbacks.OnDeleteAsync(relativePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NotifyDelete error: {ex.Message}");
            status = unchecked((int)0xC0000022); // STATUS_ACCESS_DENIED
        }
        CfOperations.AckDelete(connectionKey, transferKey, requestKey, status);
    }

}
