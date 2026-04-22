namespace CfApi.Interop;

public readonly record struct SyncRootOptions(
    string SyncRootPath,
    string ProviderName,
    string ProviderVersion,
    Guid ProviderId);
