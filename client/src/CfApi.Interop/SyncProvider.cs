using CfApi.Interop.Internal;
using CfApi.Native;

namespace CfApi.Interop;

public sealed class SyncProvider : IDisposable
{
    private readonly SyncContext _context;
    private ulong _connectionKey;
    private bool _connected;

    public SyncProvider(string syncRootPath, ISyncCallbacks callbacks)
    {
        _context = new SyncContext(callbacks, syncRootPath);
    }

    public unsafe void Connect()
    {
        var tableSize = UnmanagedEntryPoints.RegistrationTableSize;
        Span<CF_CALLBACK_REGISTRATION> table = stackalloc CF_CALLBACK_REGISTRATION[tableSize];
        UnmanagedEntryPoints.BuildRegistrationTable(table);

        fixed (CF_CALLBACK_REGISTRATION* pTable = table)
        {
            var hr = CldApi.CfConnectSyncRoot(
                _context.SyncRootPath,
                pTable,
                _context.ToPointer(),
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO
                    | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
                out _connectionKey);

            if (CldApi.Failed(hr))
                throw new InvalidOperationException($"CfConnectSyncRoot failed: 0x{hr:X8}");
        }

        _connected = true;
    }

    public void Disconnect()
    {
        if (!_connected) return;
        CldApi.CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
    }

    public void Dispose()
    {
        Disconnect();
        _context.Dispose();
    }
}
