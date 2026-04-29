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
        bool failed = false;
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
            failed = true;
        }
        finally
        {
            // 成功時は CfExecute を追加で呼ばない: 必要な範囲を transfer.Write で
            // 配信し終えれば CFS は転送完了と認識する。失敗時のみ NTSTATUS 付きで
            // 完了通知 (これがないと OS 側で永遠に待ち続ける) を送る。
            if (failed)
                transfer.Fail(unchecked((int)0xC0000001)); // STATUS_UNSUCCESSFUL
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
        // ADR-014: close 時の変更検知用に LastWriteTime のスナップショットを記録。
        // ADR-016: 同時にサーバ側ロックを best-effort 取得 (失敗しても open は成功させる)。
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

        // ロック取得は OS callback をブロックしないようバックグラウンド dispatch。
        _ = DispatchOpenAsync(ctx, relativePath);
    }

    private static async Task DispatchOpenAsync(SyncContext ctx, string relativePath)
    {
        try
        {
            await ctx.Callbacks.OnFileOpenAsync(relativePath, ctx.ShutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FileOpen dispatch error: {ex.Message}");
        }
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
        if (!isDeleted && File.Exists(localPath))
        {
            var currentWriteTime = File.GetLastWriteTimeUtc(localPath);

            // 第1検出: open/close ウィンドウ内で writeTime が変わった
            if (ctx.OpenFileWriteTimes.TryRemove(relativePath, out var openWriteTime)
                && currentWriteTime != openWriteTime)
            {
                isModified = true;
            }

            // 第2検出: 最後に同期した時刻より新しい (= ウィンドウ外で書き込まれた)
            // Notepad の save-to-temp+rename や autosave で OPEN/CLOSE を伴わない
            // 書き込みもこちらで拾える。
            if (!isModified
                && ctx.LastSyncedWriteTimes.TryGetValue(relativePath, out var lastSynced)
                && currentWriteTime > lastSynced)
            {
                isModified = true;
            }
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

        if (isDeleted)
        {
            ctx.LastSyncedWriteTimes.TryRemove(relativePath, out _);
            return;
        }

        if (safeToDehydrate && File.Exists(localPath))
        {
            // 同期完了 (アップロード成功 or 純粋な read) → 現在の writeTime を「最後に同期した時刻」として記録
            ctx.LastSyncedWriteTimes[relativePath] = File.GetLastWriteTimeUtc(localPath);
            CfOperations.DehydratePlaceholder(localPath);
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
