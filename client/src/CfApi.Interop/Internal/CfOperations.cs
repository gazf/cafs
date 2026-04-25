using System.Diagnostics;
using System.Runtime.CompilerServices;
using CfApi.Native;

namespace CfApi.Interop.Internal;

internal static class CfOperations
{
    public static int TransferData(
        ulong connectionKey,
        long transferKey,
        long requestKey,
        ReadOnlySpan<byte> buffer,
        long offset,
        int completionStatus = 0)
    {
        unsafe
        {
            fixed (byte* pData = buffer)
            {
                var info = new CF_OPERATION_INFO
                {
                    StructSize = (uint)Unsafe.SizeOf<CF_OPERATION_INFO>(),
                    Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                    ConnectionKey = connectionKey,
                    TransferKey = transferKey,
                    RequestKey = requestKey,
                };

                var parameters = new CF_OPERATION_PARAMETERS
                {
                    ParamSentinel = (uint)Unsafe.SizeOf<CF_OPERATION_PARAMETERS>(),
                    Union = new CF_OPERATION_PARAMETERS_UNION
                    {
                        TransferData = new CF_OPERATION_PARAMETERS_TRANSFER_DATA
                        {
                            Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                            CompletionStatus = completionStatus,
                            Buffer = pData,
                            Offset = offset,
                            Length = buffer.Length,
                        },
                    },
                };

                var hr = CldApi.CfExecute(&info, &parameters);
                if (CldApi.Failed(hr))
                    Trace.WriteLine($"CfExecute TransferData failed: 0x{hr:X8}");
                return hr;
            }
        }
    }

    public static int TransferPlaceholdersEmpty(
        ulong connectionKey,
        long transferKey,
        long requestKey,
        nint correlationVector)
    {
        unsafe
        {
            var info = new CF_OPERATION_INFO
            {
                StructSize = (uint)Unsafe.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
                RequestKey = requestKey,
                CorrelationVector = (void*)correlationVector,
            };

            var parameters = new CF_OPERATION_PARAMETERS
            {
                ParamSentinel = (uint)Unsafe.SizeOf<CF_OPERATION_PARAMETERS>(),
                Union = new CF_OPERATION_PARAMETERS_UNION
                {
                    TransferPlaceholders = new CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
                    {
                        Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                        CompletionStatus = 0,
                        PlaceholderTotalCount = 0,
                        PlaceholderArray = null,
                        PlaceholderCount = 0,
                    },
                },
            };

            var hr = CldApi.CfExecute(&info, &parameters);
            if (CldApi.Failed(hr))
                Trace.WriteLine($"CfExecute TransferPlaceholders (empty) failed: 0x{hr:X8}");
            return hr;
        }
    }

    public static int TransferPlaceholders(
        ulong connectionKey,
        long transferKey,
        long requestKey,
        nint correlationVector,
        ref PlaceholderBatch batch,
        int completionStatus = 0)
    {
        unsafe
        {
            var info = new CF_OPERATION_INFO
            {
                StructSize = (uint)Unsafe.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
                RequestKey = requestKey,
                CorrelationVector = (void*)correlationVector,
            };

            fixed (char* pNames = batch.Names)
            fixed (CF_PLACEHOLDER_CREATE_INFO* pPlaceholders = batch.Placeholders)
            {
                batch.PatchPointers(pNames, pPlaceholders);

                var parameters = new CF_OPERATION_PARAMETERS
                {
                    ParamSentinel = (uint)Unsafe.SizeOf<CF_OPERATION_PARAMETERS>(),
                    Union = new CF_OPERATION_PARAMETERS_UNION
                    {
                        TransferPlaceholders = new CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
                        {
                            Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                            CompletionStatus = completionStatus,
                            PlaceholderTotalCount = batch.Count,
                            PlaceholderArray = pPlaceholders,
                            PlaceholderCount = (uint)batch.Count,
                        },
                    },
                };

                var hr = CldApi.CfExecute(&info, &parameters);
                if (CldApi.Failed(hr))
                    Trace.WriteLine($"CfExecute TransferPlaceholders failed: 0x{hr:X8}");
                return hr;
            }
        }
    }

    public static void CreatePlaceholders(string localDirectoryPath, IReadOnlyList<PlaceholderInfo> entries)
    {
        if (entries.Count == 0) return;

        var batch = PlaceholderBatch.Build(entries);
        try
        {
            unsafe
            {
                fixed (char* pNames = batch.Names)
                fixed (CF_PLACEHOLDER_CREATE_INFO* pPlaceholders = batch.Placeholders)
                {
                    batch.PatchPointers(pNames, pPlaceholders);
                    var hr = CldApi.CfCreatePlaceholders(
                        localDirectoryPath,
                        pPlaceholders,
                        (uint)batch.Count,
                        CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                        out uint processed);
                    if (CldApi.Failed(hr))
                    {
                        // 0x800700B7 = ERROR_ALREADY_EXISTS: placeholders from a previous run still exist. Not an error.
                        if ((uint)hr != 0x800700B7u)
                            Trace.WriteLine($"CfCreatePlaceholders failed at '{localDirectoryPath}': 0x{hr:X8}");
                    }
                }
            }
        }
        finally
        {
            batch.Dispose();
        }
    }

    public static int AckDelete(
        ulong connectionKey,
        long transferKey,
        long requestKey,
        int completionStatus)
    {
        unsafe
        {
            var info = new CF_OPERATION_INFO
            {
                StructSize = (uint)Unsafe.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
                RequestKey = requestKey,
            };

            var parameters = new CF_OPERATION_PARAMETERS
            {
                ParamSentinel = (uint)Unsafe.SizeOf<CF_OPERATION_PARAMETERS>(),
                Union = new CF_OPERATION_PARAMETERS_UNION
                {
                    AckDelete = new CF_OPERATION_PARAMETERS_ACK_DELETE
                    {
                        Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE,
                        CompletionStatus = completionStatus,
                    },
                },
            };

            var hr = CldApi.CfExecute(&info, &parameters);
            if (CldApi.Failed(hr))
                Trace.WriteLine($"CfExecute AckDelete failed: 0x{hr:X8}");
            return hr;
        }
    }
}
