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

    // CfApi requires an explicit zero-length call to signal end of transfer.
    internal void Complete(int ntstatus = 0)
    {
        CfOperations.TransferData(_connectionKey, _transferKey, _requestKey, ReadOnlySpan<byte>.Empty, 0, ntstatus);
    }
}
