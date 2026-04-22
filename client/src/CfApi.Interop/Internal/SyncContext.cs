using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CfApi.Interop.Internal;

/// <summary>
/// Per-connection state passed through CfApi's CallbackContext pointer.
/// The pointer is a GCHandle to this instance so static [UnmanagedCallersOnly]
/// callbacks can recover it.
/// </summary>
internal sealed class SyncContext : IDisposable
{
    public ISyncCallbacks Callbacks { get; }
    public string SyncRootPath { get; }
    public ConcurrentDictionary<long, CancellationTokenSource> ActiveFetches { get; } = new();

    private GCHandle _handle;

    public SyncContext(ISyncCallbacks callbacks, string syncRootPath)
    {
        Callbacks = callbacks;
        SyncRootPath = syncRootPath;
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    public unsafe void* ToPointer() => (void*)GCHandle.ToIntPtr(_handle);

    public static unsafe SyncContext FromPointer(void* ptr)
        => (SyncContext)GCHandle.FromIntPtr((IntPtr)ptr).Target!;

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
