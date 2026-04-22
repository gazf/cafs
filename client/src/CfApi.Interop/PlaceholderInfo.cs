namespace CfApi.Interop;

public readonly record struct PlaceholderInfo(
    string Name,
    long Size,
    DateTime LastModified,
    bool IsDirectory);
