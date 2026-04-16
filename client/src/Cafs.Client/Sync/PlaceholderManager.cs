using System.Runtime.InteropServices;
using System.Text;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using Cafs.Client.Http;

namespace Cafs.Client.Sync;

public static class PlaceholderManager
{
    public static void CreatePlaceholder(string localPath, FileEntry entry)
    {
        var identityBytes = Encoding.UTF8.GetBytes(entry.Name);
        var handle = GCHandle.Alloc(identityBytes, GCHandleType.Pinned);

        try
        {
            var lastModified = DateTime.Parse(entry.LastModified).ToFileTimeUtc();
            var fileAttributes = entry.Type == "directory"
                ? FileAttributes.Directory
                : FileAttributes.Normal;

            var createInfo = new CF_PLACEHOLDER_CREATE_INFO
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

            var parentDir = Path.GetDirectoryName(localPath) ?? localPath;

            var hr = CfCreatePlaceholders(
                parentDir,
                new[] { createInfo },
                1,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                out _
            );

            if (hr.Failed)
            {
                Console.Error.WriteLine($"CfCreatePlaceholders failed for {entry.Name}: 0x{hr:X8}");
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
