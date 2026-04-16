using System.Runtime.InteropServices;
using System.Text;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using Cafs.Client.Http;

namespace Cafs.Client.CfApi.Callbacks;

public class FetchPlaceholdersHandler
{
    private readonly CafsHttpClient _httpClient;
    private readonly string _syncRootPath;

    public FetchPlaceholdersHandler(CafsHttpClient httpClient, string syncRootPath)
    {
        _httpClient = httpClient;
        _syncRootPath = syncRootPath;
    }

    public void OnCallback(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        Task.Run(async () =>
        {
            try
            {
                await HandleAsync(callbackInfo);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FetchPlaceholders error: {ex.Message}");
            }
        }).Wait();
    }

    private async Task HandleAsync(CF_CALLBACK_INFO callbackInfo)
    {
        var relativePath = CallbackHelper.GetRelativePath(callbackInfo, _syncRootPath);
        Console.WriteLine($"FetchPlaceholders: {relativePath}");

        var entries = await _httpClient.ListDirectoryAsync(relativePath);

        if (entries.Count == 0)
        {
            // Transfer empty set
            var hr = CfExecute(
                callbackInfo.ConnectionKey,
                callbackInfo.TransferKey,
                CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                new CF_OPERATION_PARAMETERS
                {
                    TransferPlaceholders = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
                    {
                        Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                        PlaceholderTotalCount = 0,
                        PlaceholderArray = IntPtr.Zero,
                        PlaceholderCount = 0
                    }
                }
            );
            return;
        }

        // Build placeholder create info array
        var placeholders = new CF_PLACEHOLDER_CREATE_INFO[entries.Count];
        var gcHandles = new List<GCHandle>();

        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var identityBytes = Encoding.UTF8.GetBytes(entry.Name);
                var handle = GCHandle.Alloc(identityBytes, GCHandleType.Pinned);
                gcHandles.Add(handle);

                var lastModified = DateTime.Parse(entry.LastModified).ToFileTimeUtc();
                var fileAttributes = entry.Type == "directory"
                    ? FileAttributes.Directory
                    : FileAttributes.Normal;

                placeholders[i] = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = entry.Name,
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = entry.Size,
                        BasicInfo = new Kernel32.FILE_BASIC_INFO
                        {
                            LastWriteTime = lastModified,
                            FileAttributes = (FileFlagsAndAttributes)fileAttributes
                        }
                    },
                    FileIdentity = handle.AddrOfPinnedObject(),
                    FileIdentityLength = (uint)identityBytes.Length,
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                };
            }

            var arrayHandle = GCHandle.Alloc(placeholders, GCHandleType.Pinned);
            gcHandles.Add(arrayHandle);

            var transferParams = new CF_OPERATION_PARAMETERS
            {
                TransferPlaceholders = new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
                {
                    Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                    PlaceholderTotalCount = (uint)entries.Count,
                    PlaceholderArray = arrayHandle.AddrOfPinnedObject(),
                    PlaceholderCount = (uint)entries.Count
                }
            };

            var hr = CfExecute(
                callbackInfo.ConnectionKey,
                callbackInfo.TransferKey,
                CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                transferParams
            );

            if (hr.Failed)
            {
                Console.Error.WriteLine($"CfExecute TransferPlaceholders failed: 0x{hr:X8}");
            }
        }
        finally
        {
            foreach (var h in gcHandles)
            {
                h.Free();
            }
        }
    }
}
