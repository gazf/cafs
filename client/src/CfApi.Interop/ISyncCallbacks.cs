namespace CfApi.Interop;

public interface ISyncCallbacks
{
    /// <summary>
    /// FETCH_DATA: hydrate のため指定範囲のバイトを取得して transfer.Write で返す。
    /// </summary>
    Task HydrateAsync(string relativePath, long offset, long length, DataTransfer transfer, CancellationToken ct);

    /// <summary>
    /// NOTIFY_DELETE: ローカル削除をサーバへ伝播。
    /// 戻り値: 0 = 削除許可 / NTSTATUS = 削除拒否。
    /// </summary>
    Task<int> OnDeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// NOTIFY_FILE_CLOSE_COMPLETION: ファイルクローズ後のフック。
    /// isModified=true の場合に書き戻し処理を行う。
    /// 戻り値: ローカルファイルを dehydrate して安全か。
    ///   - true: アップロード成功 or データを別所 (conflict file 等) に退避済み → dehydrate OK
    ///   - false: アップロード失敗 → dehydrate せず local データを残す (再送機会のため)
    /// </summary>
    Task<bool> OnFileCloseAsync(string relativePath, bool isDeleted, bool isModified, CancellationToken ct);
}
