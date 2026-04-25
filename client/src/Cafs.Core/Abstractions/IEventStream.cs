namespace Cafs.Core.Abstractions;

public interface IEventStream : IAsyncDisposable
{
    IAsyncEnumerable<string> ReadEventsAsync(CancellationToken ct);
}
