using System.Buffers;
using System.Diagnostics;
using Cafs.Core.Abstractions;
using Cafs.Core.Models;
using CfApi.Interop;

namespace Cafs.Core.Sync;

public sealed class CafsSyncCallbacks : ISyncCallbacks
{
    private const int HydrateChunkSize = 65_536;

    /// <summary>
    /// アップロード中のロック renew 周期。サーバ側 lock TTL (30 秒、ADR-018) より
    /// 短くして 1 回分の猶予を残す。Step 2 で WSS heartbeat に集約されるまでの暫定。
    /// </summary>
    private static readonly TimeSpan LockHeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>STATUS_ACCESS_DENIED — placeholder の delete を拒否する時に返す。</summary>
    private const int StatusAccessDenied = unchecked((int)0xC0000022);

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

    public async Task<int> OnDeleteAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            await _server.DeleteFileAsync(relativePath, ct).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NotifyDelete server error: {relativePath}: {ex.Message}");
            return StatusAccessDenied;
        }
    }

    /// <summary>
    /// 書き戻しフロー (write-back on close):
    ///   1. modified=false なら何もせず終了 (dehydrate OK)
    ///   2. ロック取得を試みる。失敗 (他ユーザー保持中) → conflict file へ退避して返す
    ///   3. SetInSyncState(NOT_IN_SYNC) で OS の自動 dehydrate を防ぐ
    ///   4. heartbeat タスクでロック延長しつつアップロード
    ///   5. UpdatePlaceholder でメタデータをサーバ最新に同期
    ///   6. SetInSyncState(IN_SYNC) → ロック解放
    ///   失敗パスでは local データを保持 (false 返却で dehydrate skip)。
    /// </summary>
    public async Task<bool> OnFileCloseAsync(string relativePath, bool isDeleted, bool isModified, CancellationToken ct)
    {
        if (isDeleted) return true;          // 既に削除されたファイルは dehydrate もしない (関係なし)
        if (!isModified) return true;        // 読み取りだけだった → そのまま dehydrate OK

        var localPath = Path.Combine(
            _syncRootPath,
            relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        // (2) ロック取得試行
        var lockInfo = await TryAcquireLockAsync(relativePath, ct).ConfigureAwait(false);
        if (lockInfo is null)
        {
            // 他ユーザーが編集中 → ローカル変更を conflict file に退避
            return await SaveAsConflictFileAsync(relativePath, localPath).ConfigureAwait(false);
        }

        try
        {
            // (3) NOT_IN_SYNC マークだけ行ってオプロックは即時解放する。
            //     CfOpenFileWithOplock の handle は overlapped 開きかつ CfApi が
            //     内部で完了ポートに bind しているため、FileStream の同期/非同期
            //     どちらでも読めない (CS0006: BindHandle 失敗 / 同期非対応)。
            //     ハンドルを保持し続けると File.OpenRead もシェアバイオレーション。
            //     → state 変更のたびに開閉する戦略にする。
            using (var handle = OplockFileHandle.Open(localPath, OplockOpenFlags.WriteAccess))
            {
                handle.SetInSyncState(false);
            }

            // (4) heartbeat 開始 → upload (この時点でファイルはロックされていないので File.OpenRead が通る)
            UploadResult uploadResult;
            using (var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var heartbeat = StartLockHeartbeat(relativePath, heartbeatCts.Token);
                try
                {
                    await using var stream = File.OpenRead(localPath);
                    uploadResult = await _server.UploadFileAsync(relativePath, stream, ct).ConfigureAwait(false);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try { await heartbeat.ConfigureAwait(false); } catch { }
                }
            }

            // (5) placeholder メタデータ同期 + IN_SYNC マーク
            using (var handle = OplockFileHandle.Open(localPath, OplockOpenFlags.WriteAccess))
            {
                handle.UpdatePlaceholder(
                    metadata: new FileMetadata(
                        Creation: File.GetCreationTimeUtc(localPath),
                        LastAccess: uploadResult.LastModified,
                        LastWrite: uploadResult.LastModified,
                        Change: uploadResult.LastModified,
                        FileAttributes: 0x80, // FILE_ATTRIBUTE_NORMAL
                        FileSize: uploadResult.Size),
                    fileIdentity: ReadOnlySpan<byte>.Empty,
                    dehydrateRanges: ReadOnlySpan<FileRange>.Empty,
                    flags: UpdateFlags.MarkInSync);
                handle.SetInSyncState(true);
            }

            Trace.WriteLine($"Uploaded: {relativePath} (size={uploadResult.Size})");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Upload failed: {relativePath}: {ex.Message}");
            return false; // dehydrate skip → local データ保持で再送機会を残す
        }
        finally
        {
            // ロック解放はベストエフォート (失敗しても TTL で expire)。
            try
            {
                await _server.ReleaseLockAsync(relativePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ReleaseLock failed: {relativePath}: {ex.Message}");
            }
        }
    }

    private async Task<LockInfo?> TryAcquireLockAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var lockInfo = await _server.AcquireLockAsync(relativePath, ct).ConfigureAwait(false);
            if (lockInfo is null)
                Trace.WriteLine($"AcquireLock denied (held by other): {relativePath}");
            return lockInfo;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AcquireLock failed: {relativePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// アップロード中、定期的に AcquireLock を呼ぶことで lock の expiresAt を延長する。
    /// (サーバ側 acquireLock は同一ユーザー保持時に renew として動作する。)
    /// 待ちは Task.Delay ではなく WaitHandle.WaitOne にして、キャンセル時に
    /// OperationCanceledException を投げないようにする (first-chance 例外のノイズ削減)。
    /// </summary>
    private Task StartLockHeartbeat(string relativePath, CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            // キャンセルされたら true 返り → 即抜ける。タイムアウトで false 返り → renew 実行。
            if (ct.WaitHandle.WaitOne(LockHeartbeatInterval))
                break;

            try
            {
                await _server.AcquireLockAsync(relativePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Trace.WriteLine($"Lock renew failed: {relativePath}: {ex.Message}");
            }
        }
    }, CancellationToken.None);

    /// <summary>
    /// ロック取得失敗時、ローカルの変更を conflict file としてコピー保存する。
    /// 元ファイルは戻り値 true で dehydrate されてサーバ最新に巻き戻る。
    /// </summary>
    private static async Task<bool> SaveAsConflictFileAsync(string relativePath, string localPath)
    {
        if (!File.Exists(localPath)) return true;

        try
        {
            var dir = Path.GetDirectoryName(localPath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(localPath);
            var ext = Path.GetExtension(localPath);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var conflictPath = Path.Combine(dir, $"{stem}.conflict-{stamp}{ext}");

            await using (var src = File.OpenRead(localPath))
            await using (var dst = File.Create(conflictPath))
            {
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }

            Trace.WriteLine($"Conflict saved: {relativePath} → {conflictPath}");
            return true; // 元ファイルは dehydrate して server 版に戻す
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SaveAsConflictFile failed: {relativePath}: {ex.Message}");
            return false; // 退避失敗 → local 残しておく
        }
    }
}
