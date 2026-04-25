using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cafs.Core.Abstractions;

namespace Cafs.Transport;

public sealed class HttpEventStream : IEventStream
{
    private readonly ClientWebSocket _ws;
    private bool _disposed;

    private HttpEventStream(ClientWebSocket ws) => _ws = ws;

    public static async Task<HttpEventStream> ConnectAsync(string serverUrl, string bearerToken, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

        var wsUrl = serverUrl.TrimEnd('/')
            .Replace("https://", "wss://")
            .Replace("http://", "ws://")
            + "/events";

        await ws.ConnectAsync(new Uri(wsUrl), ct);
        return new HttpEventStream(ws);
    }

    public async IAsyncEnumerable<ServerEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                yield break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var evt = JsonSerializer.Deserialize<ServerEvent>(json);
            if (evt is not null)
                yield return evt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* ignore shutdown errors */ }
        }
        _ws.Dispose();
    }
}
