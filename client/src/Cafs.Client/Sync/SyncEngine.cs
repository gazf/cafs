using Cafs.Client.Http;

namespace Cafs.Client.Sync;

public class SyncEngine
{
    private readonly CafsHttpClient _httpClient;
    private readonly string _syncRootPath;
    private Timer? _timer;

    public SyncEngine(CafsHttpClient httpClient, string syncRootPath)
    {
        _httpClient = httpClient;
        _syncRootPath = syncRootPath;
    }

    /// <summary>
    /// Perform a full sync: create placeholders for all server files
    /// that don't exist locally yet.
    /// </summary>
    public async Task FullSyncAsync()
    {
        Console.WriteLine("Starting full sync...");
        await SyncDirectoryAsync("/", _syncRootPath);
        Console.WriteLine("Full sync completed.");
    }

    private async Task SyncDirectoryAsync(string serverPath, string localPath)
    {
        try
        {
            var entries = await _httpClient.ListDirectoryAsync(serverPath);

            foreach (var entry in entries)
            {
                var localFilePath = Path.Combine(localPath, entry.Name);
                var serverFilePath = serverPath.TrimEnd('/') + "/" + entry.Name;

                if (!Path.Exists(localFilePath))
                {
                    PlaceholderManager.CreatePlaceholder(localFilePath, entry);
                }

                if (entry.Type == "directory")
                {
                    if (!Directory.Exists(localFilePath))
                    {
                        Directory.CreateDirectory(localFilePath);
                    }
                    await SyncDirectoryAsync(serverFilePath, localFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SyncDirectory error for {serverPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Start periodic sync at the given interval.
    /// </summary>
    public void StartPeriodicSync(TimeSpan interval)
    {
        _timer = new Timer(
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
