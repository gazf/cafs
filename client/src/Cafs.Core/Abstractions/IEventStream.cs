using System.Text.Json.Serialization;

namespace Cafs.Core.Abstractions;

public record ServerEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("size")] long Size = 0,
    [property: JsonPropertyName("lastModified")] DateTime? LastModified = null
);

public interface IEventStream : IAsyncDisposable
{
    IAsyncEnumerable<ServerEvent> ReadEventsAsync(CancellationToken ct);
}
