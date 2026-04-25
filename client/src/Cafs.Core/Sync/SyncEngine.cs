using System.Diagnostics;
using Cafs.Core.Abstractions;

namespace Cafs.Core.Sync;

public class SyncEngine
{
    private readonly ICafsServer _server;
    private System.Threading.Timer? _timer;

    public SyncEngine(ICafsServer server)
    {
        _server = server;
    }

    // Connectivity check only — placeholders are populated on-demand via FetchPlaceholders.
    // Pre-creating placeholders conflicts with on-demand population.
    public async Task FullSyncAsync()
    {
        Trace.WriteLine("FullSync: verifying server connectivity...");
        await _server.ListDirectoryAsync("/");
        Trace.WriteLine("FullSync: ok.");
    }

    public void StartPeriodicSync(TimeSpan interval)
    {
        StopPeriodicSync();
        _timer = new System.Threading.Timer(
            async _ =>
            {
                try { await FullSyncAsync(); }
                catch (Exception ex) { Console.Error.WriteLine($"Periodic sync error: {ex.Message}"); }
            },
            null,
            interval,
            interval
        );
        Console.WriteLine($"Periodic sync started (interval: {interval.TotalSeconds}s)");
    }

    public void StopPeriodicSync()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
