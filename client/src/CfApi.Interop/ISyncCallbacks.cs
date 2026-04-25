namespace CfApi.Interop;

public interface ISyncCallbacks
{
    Task HydrateAsync(string relativePath, long offset, long length, DataTransfer transfer, CancellationToken ct);

    Task<int> OnDeleteAsync(string relativePath);
}
