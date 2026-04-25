namespace CfApi.Interop;

public interface ISyncCallbacks
{
    Task HydrateAsync(string relativePath, long offset, long length, DataTransfer transfer, CancellationToken ct);

    Task<int> OnDeleteAsync(string relativePath);

    Task OnFileOpenAsync(string relativePath);

    Task OnFileCloseAsync(string relativePath, bool isDeleted, bool isModified);
}
