using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CfApi.Native;

namespace CfApi.Interop.Internal;

internal static class UnmanagedEntryPoints
{
    /// <summary>
    /// 4 つのコールバック + 終端を並べた登録テーブルを呼び出し側のバッファに書き込む。
    /// 呼び出し側はこの Span を固定して CfConnectSyncRoot に渡す。
    /// </summary>
    public static int RegistrationTableSize => 5;

    public static unsafe void BuildRegistrationTable(Span<CF_CALLBACK_REGISTRATION> table)
    {
        table[0] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
            Callback = &OnFetchPlaceholders,
        };
        table[1] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
            Callback = &OnFetchData,
        };
        table[2] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
            Callback = &OnCancelFetchData,
        };
        table[3] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
            Callback = &OnNotifyDelete,
        };
        table[4] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
            Callback = null,
        };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var ctx = SyncContext.FromPointer(info->CallbackContext);
        var relativePath = Marshaller.GetRelativePath(info, ctx.SyncRootPath);
        var connectionKey = info->ConnectionKey;
        var transferKey = info->TransferKey;
        var requestKey = info->RequestKey;
        var correlationVector = (nint)info->CorrelationVector;

        _ = Task.Run(() =>
        {
            return ctx.Callbacks.ListAsync(relativePath, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Trace.WriteLine($"FetchPlaceholders error: {t.Exception?.GetBaseException().Message}");
                        return;
                    }
                    try
                    {
                        Trace.WriteLine($"FetchPlaceholders: {relativePath}");
                        TransferPlaceholders(connectionKey, transferKey, requestKey, correlationVector, t.Result);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"FetchPlaceholders error: {ex.Message}");
                    }
                }, TaskScheduler.Default);
        });
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

        _ = Task.Run(() =>
        {
            Trace.WriteLine($"FetchData: {relativePath} offset={requiredOffset} length={requiredLength}");
            var transfer = new DataTransfer(connectionKey, transferKey, requestKey);
            return ctx.Callbacks.HydrateAsync(relativePath, requiredOffset, requiredLength, transfer, cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Trace.WriteLine($"FetchData error: {t.Exception?.GetBaseException().Message}");
                    }
                    ctx.ActiveFetches.TryRemove(transferKey, out _);
                    cts.Dispose();
                }, TaskScheduler.Default);
        });
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

        _ = Task.Run(() =>
        {
            return ctx.Callbacks.OnDeleteAsync(relativePath)
                .ContinueWith(t =>
                {
                    int status;
                    if (t.IsFaulted)
                    {
                        Trace.WriteLine($"NotifyDelete error: {t.Exception?.GetBaseException().Message}");
                        status = unchecked((int)0xC0000022); // STATUS_ACCESS_DENIED
                    }
                    else
                    {
                        status = t.Result;
                    }

                    CfOperations.AckDelete(connectionKey, transferKey, requestKey, status);
                }, TaskScheduler.Default);
        });
    }

    private static void TransferPlaceholders(
        ulong connectionKey, long transferKey, long requestKey, nint correlationVector,
        IReadOnlyList<PlaceholderInfo> entries)
    {
        if (entries.Count == 0)
        {
            CfOperations.TransferPlaceholdersEmpty(connectionKey, transferKey, requestKey, correlationVector);
            return;
        }

        var batch = Marshaller.BuildPlaceholders(entries);
        try
        {
            CfOperations.TransferPlaceholders(connectionKey, transferKey, requestKey, correlationVector, ref batch);
        }
        finally
        {
            batch.Dispose();
        }
    }
}
