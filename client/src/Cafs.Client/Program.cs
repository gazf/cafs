using Cafs.Client.CfApi;
using Cafs.Client.Config;
using Cafs.Client.Http;
using Cafs.Client.Sync;

namespace Cafs.Client;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("cafs client starting...");

        // Load settings
        var settings = AppSettings.Load(args);
        settings.Validate();

        Console.WriteLine($"  Server:    {settings.ServerUrl}");
        Console.WriteLine($"  SyncRoot:  {settings.SyncRootPath}");

        // Initialize HTTP client
        using var httpClient = new CafsHttpClient(settings.ServerUrl, settings.BearerToken);

        // Ensure sync root directory exists
        if (!Directory.Exists(settings.SyncRootPath))
        {
            Directory.CreateDirectory(settings.SyncRootPath);
        }

        // Register sync root with CfApi
        SyncRootRegistrar.Register(settings.SyncRootPath, "CAFS");

        // Connect sync provider
        using var syncProvider = new SyncProvider(httpClient, settings.SyncRootPath);
        syncProvider.Connect();

        // Run initial full sync
        var syncEngine = new SyncEngine(httpClient, settings.SyncRootPath);
        await syncEngine.FullSyncAsync();

        // Start periodic sync
        syncEngine.StartPeriodicSync(TimeSpan.FromSeconds(settings.SyncIntervalSeconds));

        // Wait for exit
        Console.WriteLine("cafs client running. Press Ctrl+C to exit.");
        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        exitEvent.Wait();

        // Cleanup
        Console.WriteLine("Shutting down...");
        syncEngine.StopPeriodicSync();
        syncProvider.Disconnect();

        Console.WriteLine("cafs client stopped.");
    }
}
