namespace CfApi.Interop;

/// <summary>
/// CF_FS_METADATA / FILE_BASIC_INFO のマネージド版。
/// タイムスタンプは DateTime (UTC) で受け、内部で FILETIME に変換する。
/// </summary>
public readonly record struct FileMetadata(
    DateTime Creation,
    DateTime LastAccess,
    DateTime LastWrite,
    DateTime Change,
    uint FileAttributes,
    long FileSize);
