using System.Runtime.InteropServices;

namespace CfApi.Native;

[StructLayout(LayoutKind.Sequential)]
public struct CF_HYDRATION_POLICY
{
    public CF_HYDRATION_POLICY_PRIMARY Primary;
    public CF_HYDRATION_POLICY_MODIFIER Modifier;
}

[StructLayout(LayoutKind.Sequential)]
public struct CF_POPULATION_POLICY
{
    public CF_POPULATION_POLICY_PRIMARY Primary;
    public CF_POPULATION_POLICY_MODIFIER Modifier;
}

[StructLayout(LayoutKind.Sequential)]
public struct CF_SYNC_POLICIES
{
    public uint StructSize;
    public CF_HYDRATION_POLICY Hydration;
    public CF_POPULATION_POLICY Population;
    public CF_INSYNC_POLICY InSync;
    public CF_HARDLINK_POLICY HardLink;
    public CF_PLACEHOLDER_MANAGEMENT_POLICY PlaceholderManagement;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_SYNC_REGISTRATION
{
    public uint StructSize;
    public char* ProviderName;
    public char* ProviderVersion;
    public void* SyncRootIdentity;
    public uint SyncRootIdentityLength;
    public void* FileIdentity;
    public uint FileIdentityLength;
    public Guid ProviderId;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_CALLBACK_REGISTRATION
{
    public CF_CALLBACK_TYPE Type;
    public delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, CF_CALLBACK_PARAMETERS*, void> Callback;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_CALLBACK_INFO
{
    public uint StructSize;
    public ulong ConnectionKey;
    public void* CallbackContext;
    public char* VolumeGuidName;
    public char* VolumeDosName;
    public uint VolumeSerialNumber;
    public long SyncRootFileId;
    public char* SyncRootIdentity;
    public uint SyncRootIdentityLength;
    public long FileId;
    public long FileSize;
    public void* FileIdentity;
    public uint FileIdentityLength;
    public char* NormalizedPath;
    public long TransferKey;
    public byte PriorityHint;
    public void* CorrelationVector;
    public void* ProcessInfo;
    public long RequestKey;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_CALLBACK_PARAMETERS_FETCH_DATA
{
    public CF_CALLBACK_FETCH_DATA_FLAGS Flags;
    public long RequiredFileOffset;
    public long RequiredLength;
    public long OptionalFileOffset;
    public long OptionalLength;
    public long LastDehydrationTime;
    public CF_CALLBACK_DEHYDRATION_REASON LastDehydrationReason;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_CALLBACK_PARAMETERS_CANCEL_FETCH_DATA
{
    public uint Flags;
    public long FileOffset;
    public long Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_CALLBACK_PARAMETERS_FETCH_PLACEHOLDERS
{
    public CF_CALLBACK_FETCH_PLACEHOLDERS_FLAGS Flags;
    public char* Pattern;
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct CF_CALLBACK_PARAMETERS_UNION
{
    [FieldOffset(0)] public CF_CALLBACK_PARAMETERS_FETCH_DATA FetchData;
    [FieldOffset(0)] public CF_CALLBACK_PARAMETERS_CANCEL_FETCH_DATA Cancel;
    [FieldOffset(0)] public CF_CALLBACK_PARAMETERS_FETCH_PLACEHOLDERS FetchPlaceholders;
}

[StructLayout(LayoutKind.Sequential)]
public struct CF_CALLBACK_PARAMETERS
{
    public uint ParamSentinel;
    public CF_CALLBACK_PARAMETERS_UNION Union;
}

[StructLayout(LayoutKind.Sequential)]
public struct FILE_BASIC_INFO
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

[StructLayout(LayoutKind.Sequential)]
public struct CF_FS_METADATA
{
    public FILE_BASIC_INFO BasicInfo;
    public long FileSize;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_PLACEHOLDER_CREATE_INFO
{
    public char* RelativeFileName;
    public CF_FS_METADATA FsMetadata;
    public void* FileIdentity;
    public uint FileIdentityLength;
    public CF_PLACEHOLDER_CREATE_FLAGS Flags;
    public int Result;
    public long CreateUsn;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_OPERATION_INFO
{
    public uint StructSize;
    public CF_OPERATION_TYPE Type;
    public ulong ConnectionKey;
    public long TransferKey;
    public void* CorrelationVector;  // CF_CORRELATION_VECTOR*
    public void* SyncStatus;          // NTSTATUS*
    public long RequestKey;           // CF_REQUEST_KEY = LARGE_INTEGER
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_OPERATION_PARAMETERS_TRANSFER_DATA
{
    public CF_OPERATION_TRANSFER_DATA_FLAGS Flags;
    public int CompletionStatus;
    public void* Buffer;
    public long Offset;
    public long Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
{
    public CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS Flags;
    public int CompletionStatus;
    public long PlaceholderTotalCount;
    public CF_PLACEHOLDER_CREATE_INFO* PlaceholderArray;
    public uint PlaceholderCount;
    public uint EntriesProcessed;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CF_OPERATION_PARAMETERS_ACK_DELETE
{
    public CF_OPERATION_ACK_DELETE_FLAGS Flags;
    public int CompletionStatus;
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct CF_OPERATION_PARAMETERS_UNION
{
    [FieldOffset(0)] public CF_OPERATION_PARAMETERS_TRANSFER_DATA TransferData;
    [FieldOffset(0)] public CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS TransferPlaceholders;
    [FieldOffset(0)] public CF_OPERATION_PARAMETERS_ACK_DELETE AckDelete;
}

[StructLayout(LayoutKind.Sequential)]
public struct CF_OPERATION_PARAMETERS
{
    public uint ParamSentinel;
    public CF_OPERATION_PARAMETERS_UNION Union;
}
