using System.Runtime.InteropServices;

namespace CfApi.Native;

public static unsafe partial class CldApi
{
    private const string CldApiDll = "cldapi.dll";

    [LibraryImport(CldApiDll, EntryPoint = "CfRegisterSyncRoot", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfRegisterSyncRoot(
        string syncRootPath,
        CF_SYNC_REGISTRATION* registration,
        CF_SYNC_POLICIES* policies,
        CF_REGISTER_FLAGS registerFlags);

    [LibraryImport(CldApiDll, EntryPoint = "CfUnregisterSyncRoot", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfUnregisterSyncRoot(string syncRootPath);

    [LibraryImport(CldApiDll, EntryPoint = "CfConnectSyncRoot", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfConnectSyncRoot(
        string syncRootPath,
        CF_CALLBACK_REGISTRATION* callbackTable,
        void* callbackContext,
        CF_CONNECT_FLAGS connectFlags,
        out ulong connectionKey);

    [LibraryImport(CldApiDll, EntryPoint = "CfDisconnectSyncRoot")]
    public static partial int CfDisconnectSyncRoot(ulong connectionKey);

    [LibraryImport(CldApiDll, EntryPoint = "CfExecute")]
    public static partial int CfExecute(
        CF_OPERATION_INFO* operationInfo,
        CF_OPERATION_PARAMETERS* operationParameters);

    [LibraryImport(CldApiDll, EntryPoint = "CfCreatePlaceholders", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfCreatePlaceholders(
        string baseDirectoryPath,
        CF_PLACEHOLDER_CREATE_INFO* placeholderArray,
        uint placeholderCount,
        CF_CREATE_FLAGS createFlags,
        out uint entriesProcessed);

    [LibraryImport(CldApiDll, EntryPoint = "CfUpdatePlaceholder")]
    public static partial int CfUpdatePlaceholder(
        IntPtr fileHandle,
        CF_FS_METADATA* fsMetadata,
        void* fileIdentity,
        uint fileIdentityLength,
        CF_FILE_RANGE* dehydrateRangeArray,
        uint dehydrateRangeCount,
        CF_UPDATE_FLAGS updateFlags,
        long* inSyncUsn,
        void* overlapped);

    [LibraryImport(CldApiDll, EntryPoint = "CfSetInSyncState")]
    public static partial int CfSetInSyncState(
        IntPtr fileHandle,
        CF_IN_SYNC_STATE inSyncState,
        CF_SET_IN_SYNC_FLAGS inSyncFlags,
        long* inSyncUsn);

    [LibraryImport(CldApiDll, EntryPoint = "CfConvertToPlaceholder")]
    public static partial int CfConvertToPlaceholder(
        IntPtr fileHandle,
        void* fileIdentity,
        uint fileIdentityLength,
        CF_CONVERT_FLAGS convertFlags,
        long* convertUsn,
        void* overlapped);

    [LibraryImport(CldApiDll, EntryPoint = "CfOpenFileWithOplock", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CfOpenFileWithOplock(
        string filePath,
        CF_OPEN_FILE_FLAGS flags,
        out IntPtr protectedHandle);

    [LibraryImport(CldApiDll, EntryPoint = "CfCloseHandle")]
    public static partial int CfCloseHandle(IntPtr fileHandle);

    [LibraryImport(CldApiDll, EntryPoint = "CfReferenceProtectedHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CfReferenceProtectedHandle(IntPtr protectedHandle);

    [LibraryImport(CldApiDll, EntryPoint = "CfReleaseProtectedHandle")]
    public static partial void CfReleaseProtectedHandle(IntPtr protectedHandle);

    [LibraryImport(CldApiDll, EntryPoint = "CfGetWin32HandleFromProtectedHandle")]
    public static partial IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

    public static bool Failed(int hresult) => hresult < 0;
    public static bool Succeeded(int hresult) => hresult >= 0;
}
