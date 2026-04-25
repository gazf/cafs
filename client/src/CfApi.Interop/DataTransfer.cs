using CfApi.Interop.Internal;

namespace CfApi.Interop;

public readonly struct DataTransfer
{
    private readonly ulong _connectionKey;
    private readonly long _transferKey;
    private readonly long _requestKey;

    internal DataTransfer(ulong connectionKey, long transferKey, long requestKey)
    {
        _connectionKey = connectionKey;
        _transferKey = transferKey;
        _requestKey = requestKey;
    }

    public void Write(ReadOnlySpan<byte> chunk, long offset)
    {
        CfOperations.TransferData(_connectionKey, _transferKey, _requestKey, chunk, offset);
    }

    /// <summary>
    /// 転送失敗を CFS に通知する。NTSTATUS エラーコードで CompletionStatus を付ける。
    /// 成功時は呼ぶ必要なし — Write で必要範囲を配信し終えれば CFS が転送完了と判断する。
    /// </summary>
    internal void Fail(int ntstatus)
    {
        CfOperations.TransferData(_connectionKey, _transferKey, _requestKey, ReadOnlySpan<byte>.Empty, 0, ntstatus);
    }
}
