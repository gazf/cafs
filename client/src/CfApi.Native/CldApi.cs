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

    public static bool Failed(int hresult) => hresult < 0;
    public static bool Succeeded(int hresult) => hresult >= 0;
}
