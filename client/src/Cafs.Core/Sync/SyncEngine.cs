using System.Diagnostics;
using Cafs.Core.Abstractions;
using Cafs.Core.Models;
using CfApi.Interop;

namespace Cafs.Core.Sync;

public class SyncEngine
{
    private readonly ICafsServer _server;
    private readonly SyncProvider _syncProvider;
    private readonly string _syncRootPath;

    public SyncEngine(ICafsServer server, SyncProvider syncProvider, string syncRootPath)
    {
        _server = server;
        _syncProvider = syncProvider;
        _syncRootPath = syncRootPath;
    }

    public async Task FullSyncAsync(CancellationToken ct = default)
    {
        Trace.WriteLine("FullSync: fetching tree from server...");
        var tree = await _server.GetTreeAsync(ct);
        Trace.WriteLine($"FullSync: {tree.Count} entries received.");

        var byParent = tree
            .GroupBy(n => n.ParentPath)
            .ToDictionary(g => g.Key, g => g.ToList());

        var queue = new Queue<string>();
        queue.Enqueue("/");

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            if (!byParent.TryGetValue(dir, out var children)) continue;

            var localDir = ToLocalPath(dir);
            var infos = children.Select(n => new PlaceholderInfo(n.Name, n.Size, n.LastModified, n.IsDirectory)).ToList();

            Trace.WriteLine($"FullSync: creating {infos.Count} placeholder(s) in '{localDir}'");
            _syncProvider.CreatePlaceholders(localDir, infos);

            // 各ファイルの「最後に同期した時刻」を記録 (close 時の modify 検出に使う)
            foreach (var child in children.Where(c => !c.IsDirectory))
                _syncProvider.RecordSyncedWriteTime(child.Path, child.LastModified);

            // CreatePlaceholders だけでは Explorer のビューが再列挙されない。
            // ディレクトリ単位で SHCNE_UPDATEDIR を打って、開いているビューに反映させる。
            Shell.NotifyUpdateDir(localDir);

            foreach (var child in children.Where(c => c.IsDirectory))
                queue.Enqueue(child.Path);
        }

        Trace.WriteLine("FullSync: complete.");
    }

    public async Task RunEventLoopAsync(IEventStream events, CancellationToken ct)
    {
        await foreach (var evt in events.ReadEventsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                HandleEvent(evt);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EventLoop error: {ex.Message}");
            }
        }
    }

    private void HandleEvent(ServerEvent evt)
    {
        Trace.WriteLine($"Event: {evt.Event} {evt.Path}");

        switch (evt.Event)
        {
            case "created":
            case "modified" when evt.Type is not null:
            {
                var parentPath = evt.Path.LastIndexOf('/') is int pi && pi > 0
                    ? evt.Path[..pi]
                    : "/";
                var name = evt.Path[(evt.Path.LastIndexOf('/') + 1)..];
                var localParent = ToLocalPath(parentPath);
                var localItem = Path.Combine(localParent, name);
                var lastModified = evt.LastModified ?? DateTime.UtcNow;
                var isDirectory = evt.Type == "directory";
                _syncProvider.CreatePlaceholders(localParent,
                [
                    new PlaceholderInfo(name, evt.Size, lastModified, isDirectory),
                ]);
                if (!isDirectory)
                    _syncProvider.RecordSyncedWriteTime(evt.Path, lastModified);
                if (evt.Event == "created")
                    Shell.NotifyCreate(localItem, isDirectory);
                else
                    Shell.NotifyUpdate(localItem);
                break;
            }

            case "deleted":
            {
                var localPath = ToLocalPath(evt.Path);
                var wasDirectory = Directory.Exists(localPath);
                if (wasDirectory)
                    Directory.Delete(localPath, recursive: true);
                else if (File.Exists(localPath))
                    File.Delete(localPath);
                else
                    break; // 元から無いなら通知不要
                Shell.NotifyDelete(localPath, wasDirectory);
                break;
            }
        }
    }

    private string ToLocalPath(string serverPath) =>
        serverPath == "/"
            ? _syncRootPath
            : Path.Combine(_syncRootPath, serverPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
}
