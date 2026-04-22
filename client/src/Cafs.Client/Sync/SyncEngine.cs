using System.Diagnostics;
using Cafs.Client.Http;

namespace Cafs.Client.Sync;

public class SyncEngine
{
    private readonly CafsHttpClient _httpClient;
    private readonly string _syncRootPath;
    private System.Threading.Timer? _timer;

    public SyncEngine(CafsHttpClient httpClient, string syncRootPath)
    {
        _httpClient = httpClient;
        _syncRootPath = syncRootPath;
    }

    /// <summary>
    /// Connectivity check only — placeholders are populated on-demand by
    /// FetchPlaceholders callbacks when the user navigates in Explorer.
    /// Pre-creating placeholders conflicts with on-demand population.
    /// </summary>
    public async Task FullSyncAsync()
    {
        Trace.WriteLine("FullSync: verifying server connectivity...");
        await _httpClient.ListDirectoryAsync("/");
        Trace.WriteLine("FullSync: ok.");
    }

    /// <summary>
    /// Start periodic sync at the given interval.
    /// </summary>
    public void StartPeriodicSync(TimeSpan interval)
    {
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
