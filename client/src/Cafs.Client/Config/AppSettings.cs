using System.Text.Json;

namespace Cafs.Client.Config;

public class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:8700";
    public string BearerToken { get; set; } = "";
    public string SyncRootPath { get; set; } = @"C:\Users\Public\CAFS";
    public int SyncIntervalSeconds { get; set; } = 300; // 5 minutes

    public static AppSettings Load(string[] args)
    {
        var settings = new AppSettings();

        // Try loading from appsettings.json
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded != null) settings = loaded;
        }

        // Override with command-line args
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--server":
                    settings.ServerUrl = args[++i];
                    break;
                case "--token":
                    settings.BearerToken = args[++i];
                    break;
                case "--sync-root":
                    settings.SyncRootPath = args[++i];
                    break;
                case "--sync-interval":
                    settings.SyncIntervalSeconds = int.Parse(args[++i]);
                    break;
            }
        }

        return settings;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
            throw new ArgumentException("ServerUrl is required");
        if (string.IsNullOrWhiteSpace(BearerToken))
            throw new ArgumentException("BearerToken is required. Use --token <token> or set in appsettings.json");
        if (string.IsNullOrWhiteSpace(SyncRootPath))
            throw new ArgumentException("SyncRootPath is required");
    }
}
