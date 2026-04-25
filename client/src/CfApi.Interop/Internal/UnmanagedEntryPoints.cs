using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CfApi.Native;

namespace CfApi.Interop.Internal;

internal static class UnmanagedEntryPoints
{
    public static int RegistrationTableSize => 7;

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
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
            Callback = &OnFetchPlaceholders,
        };
        table[3] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION,
            Callback = &OnOpenCompletion,
        };
        table[4] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,
            Callback = &OnCloseCompletion,
        };
        table[5] = new CF_CALLBACK_REGISTRATION
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
            Callback = &OnNotifyDelete,
        };
        table[6] = new CF_CALLBACK_REGISTRATION
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

        // shutdown と CancelFetchData の両方でキャンセルできるよう linked CTS。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.ShutdownCts.Token);
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
    private static unsafe void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // ALWAYS_FULL: placeholders are pre-populated; always respond with empty to unblock Explorer.
        CfOperations.TransferPlaceholdersEmpty(
            info->ConnectionKey,
            info->TransferKey,
            info->RequestKey,
            (nint)info->CorrelationVector);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnOpenCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // open 時点では何も dispatch しない。close 時の変更検知用に LastWriteTime のスナップショットだけ取る。
        // (ロック取得は close 時に modified=true の場合のみ行う方針 — issue #12)
        var ctx = SyncContext.FromPointer(info->CallbackContext);
        var relativePath = Marshaller.GetRelativePath(info, ctx.SyncRootPath);
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            Trace.WriteLine($"FileOpen: skip (root or empty): '{relativePath}'");
            return;
        }

        var localPath = Path.Combine(ctx.SyncRootPath, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(localPath))
        {
            Trace.WriteLine($"FileOpen: skip (not file or missing): '{localPath}'");
            return;
        }

        var writeTime = File.GetLastWriteTimeUtc(localPath);
        ctx.OpenFileWriteTimes[relativePath] = writeTime;
        Trace.WriteLine($"FileOpen: {relativePath} (writeTime={writeTime:O})");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void OnCloseCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var ctx = SyncContext.FromPointer(info->CallbackContext);
        var relativePath = Marshaller.GetRelativePath(info, ctx.SyncRootPath);
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/") return;

        var localPath = Path.Combine(ctx.SyncRootPath, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        var isDeleted = (parameters->Union.CloseCompletion.Flags & CF_CALLBACK_CLOSE_COMPLETION_FLAGS.CF_CALLBACK_CLOSE_COMPLETION_FLAG_DELETED) != 0;

        bool isModified = false;
        if (!isDeleted && File.Exists(localPath)
            && ctx.OpenFileWriteTimes.TryRemove(relativePath, out var prevWriteTime))
        {
            isModified = File.GetLastWriteTimeUtc(localPath) != prevWriteTime;
        }
        else
        {
            ctx.OpenFileWriteTimes.TryRemove(relativePath, out _);
        }

        _ = DispatchCloseAsync(ctx, relativePath, localPath, isDeleted, isModified);
    }

    private static async Task DispatchCloseAsync(
        SyncContext ctx, string relativePath, string localPath, bool isDeleted, bool isModified)
    {
        Trace.WriteLine($"FileClose: {relativePath} modified={isModified} deleted={isDeleted}");

        // dehydrate するか否かは callback の戻り値で決定:
        //   true  → アップロード成功 or データを別所に退避済み → dehydrate OK
        //   false → アップロード失敗 → local データを保持 (再送機会を残す)
        // 例外時は false 扱い (= dehydrate しない)。
        bool safeToDehydrate = false;
        try
        {
            safeToDehydrate = await ctx.Callbacks
                .OnFileCloseAsync(relativePath, isDeleted, isModified, ctx.ShutdownCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FileClose error: {ex.Message}");
        }

        if (safeToDehydrate && !isDeleted && File.Exists(localPath))
            CfOperations.DehydratePlaceholder(localPath);
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
            status = await ctx.Callbacks.OnDeleteAsync(relativePath, ctx.ShutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NotifyDelete error: {ex.Message}");
            status = unchecked((int)0xC0000022); // STATUS_ACCESS_DENIED
        }
        CfOperations.AckDelete(connectionKey, transferKey, requestKey, status);
    }

}
