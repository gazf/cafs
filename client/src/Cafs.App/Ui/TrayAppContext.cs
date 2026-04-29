using System.Diagnostics;
using System.Net.Http.Headers;
using System.Windows.Forms;
using Cafs.App.Config;
using Cafs.Core.Identity;
using Cafs.Core.Sync;
using Cafs.Transport;
using CfApi.Interop;

namespace Cafs.App.Ui;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;

    private const string ProviderName = "CAFS";
    private const string ProviderVersion = "1.0";
    private static readonly Guid ProviderId = Guid.Parse("B5F2A9C1-4E7D-4A3B-8F6C-1D2E3F4A5B6C");

    private static readonly TimeSpan WssHeartbeatInterval = TimeSpan.FromSeconds(10);

    private HttpCafsServer? _server;
    private SyncProvider? _syncProvider;
    private SyncEngine? _syncEngine;
    private HttpEventStream? _eventStream;
    private CancellationTokenSource? _eventLoopCts;
    private string? _deviceId;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _syncNowItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _resetItem;

    public TrayAppContext(AppSettings settings)
    {
        _settings = settings;

        _statusItem = new ToolStripMenuItem("Initializing...") { Enabled = false };
        _openItem = new ToolStripMenuItem("Open sync folder", null, OnOpenFolder);
        _syncNowItem = new ToolStripMenuItem("Sync now", null, OnSyncNow);
        _settingsItem = new ToolStripMenuItem("Settings...", null, OnOpenSettings);
        _resetItem = new ToolStripMenuItem("Reset local cache...", null, OnResetLocalCache);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(_syncNowItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_resetItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CAFS",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += OnOpenFolder;

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (!_settings.IsConfigured)
        {
            SetStatus("Not configured");
            ShowBalloon("Please configure CAFS", "Right-click the tray icon → Settings.");
            OnOpenSettings(this, EventArgs.Empty);
            if (!_settings.IsConfigured) return;
        }

        try
        {
            SetStatus("Connecting...");
            _deviceId = DeviceIdProvider.GetOrCreate();
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.BearerToken);
            http.DefaultRequestHeaders.Add("X-Device-Id", _deviceId);
            _server = new HttpCafsServer(http, _settings.ServerUrl);
            Directory.CreateDirectory(_settings.SyncRootPath);

            SyncRootRegistrar.Register(new SyncRootOptions(
                _settings.SyncRootPath, ProviderName, ProviderVersion, ProviderId));

            var callbacks = new CafsSyncCallbacks(_server, _settings.SyncRootPath);
            _syncProvider = new SyncProvider(_settings.SyncRootPath, callbacks);

            // ALWAYS_FULL: create all placeholders before connecting,
            // so Explorer never sees an empty directory.
            _syncEngine = new SyncEngine(_server, _syncProvider, _settings.SyncRootPath);
            SetStatus("Syncing...");
            await _syncEngine.FullSyncAsync();

            _syncProvider.Connect();

            _eventLoopCts = new CancellationTokenSource();
            _ = RunEventLoopWithReconnectAsync(_eventLoopCts.Token);

            SetStatus($"Connected: {_settings.ServerUrl}");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowBalloon("CAFS error", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task RunEventLoopWithReconnectAsync(CancellationToken ct)
    {
        var deviceId = _deviceId
            ?? throw new InvalidOperationException("Device ID not initialized");

        while (!ct.IsCancellationRequested)
        {
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var stream = await HttpEventStream.ConnectAsync(
                    _settings.ServerUrl, _settings.BearerToken, deviceId, ct);
                _eventStream = stream;
                await using (stream)
                {
                    var heartbeat = RunHeartbeatAsync(stream, heartbeatCts.Token);
                    try
                    {
                        await _syncEngine!.RunEventLoopAsync(stream, ct);
                    }
                    finally
                    {
                        heartbeatCts.Cancel();
                        try { await heartbeat; } catch { /* ignore */ }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _eventStream = null;
                Trace.WriteLine($"EventLoop disconnected: {ex.Message}, reconnecting in 5s...");
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        _eventStream = null;
    }

    private static async Task RunHeartbeatAsync(HttpEventStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 10 秒待機 → ハートビート 1 回送信。キャンセル時に WaitOne(true) で即抜け。
            if (ct.WaitHandle.WaitOne(WssHeartbeatInterval)) break;

            try
            {
                await stream.SendHeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // ソケットが死んでいれば外側のループが再接続する。ここでは握り潰し。
                Trace.WriteLine($"Heartbeat failed: {ex.Message}");
            }
        }
    }

    private void SetStatus(string text)
    {
        if (_statusItem.Owner?.InvokeRequired == true)
            _statusItem.Owner.Invoke(() => _statusItem.Text = text);
        else
            _statusItem.Text = text;
        _tray.Text = $"CAFS - {text}".Length > 63 ? "CAFS" : $"CAFS - {text}";
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(3000);
    }

    private void OnOpenFolder(object? sender, EventArgs e)
    {
        if (Directory.Exists(_settings.SyncRootPath))
            Process.Start(new ProcessStartInfo("explorer.exe", _settings.SyncRootPath) { UseShellExecute = true });
    }

    private async void OnSyncNow(object? sender, EventArgs e)
    {
        if (_syncEngine is null) return;
        SetStatus("Syncing...");
        try
        {
            await _syncEngine.FullSyncAsync(CancellationToken.None);
            SetStatus($"Connected: {_settings.ServerUrl}");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowBalloon("Sync failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() == DialogResult.OK)
        {
            ShowBalloon("Settings saved", "Restart CAFS to apply changes.");
        }
    }

    private async void OnResetLocalCache(object? sender, EventArgs e)
    {
        var path = _settings.SyncRootPath;
        var result = MessageBox.Show(
            $"Delete the local cache at:\n\n{path}\n\nServer data will not be touched. Continue?",
            "Reset local cache",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.OK) return;

        SetStatus("Resetting...");
        try
        {
            _eventLoopCts?.Cancel();
            _eventLoopCts?.Dispose();
            _eventLoopCts = null;
            _ = _eventStream?.DisposeAsync().AsTask();
            _eventStream = null;

            _syncProvider?.Disconnect();
            _syncProvider?.Dispose();
            _syncProvider = null;

            SyncRootRegistrar.Unregister(path);

            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); }
                catch (Exception ex) { Trace.WriteLine($"Reset: directory delete warning: {ex.Message}"); }
            }

            SetStatus("Reconnecting...");
            Directory.CreateDirectory(path);
            SyncRootRegistrar.Register(new SyncRootOptions(path, ProviderName, ProviderVersion, ProviderId));

            var callbacks = new CafsSyncCallbacks(_server!, path);
            _syncProvider = new SyncProvider(path, callbacks);
            _syncProvider.Connect();

            _syncEngine = new SyncEngine(_server!, _syncProvider, path);
            await _syncEngine.FullSyncAsync();

            _eventLoopCts = new CancellationTokenSource();
            _ = RunEventLoopWithReconnectAsync(_eventLoopCts.Token);

            SetStatus($"Connected: {_settings.ServerUrl}");
            ShowBalloon("Reset complete", "Local cache cleared and re-synced.");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowBalloon("Reset failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        Shutdown();
        ExitThread();
    }

    private void Shutdown()
    {
        try
        {
            _eventLoopCts?.Cancel();
            _eventLoopCts?.Dispose();
            _eventLoopCts = null;
            _ = _eventStream?.DisposeAsync().AsTask();
            _eventStream = null;
            _syncProvider?.Disconnect();
            _syncProvider?.Dispose();
            _server?.Dispose();
        }
        catch { /* ignore shutdown errors */ }

        _tray.Visible = false;
        _tray.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Shutdown();
        base.Dispose(disposing);
    }
}
