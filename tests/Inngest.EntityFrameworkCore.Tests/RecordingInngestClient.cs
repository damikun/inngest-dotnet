using System.Collections.Concurrent;

namespace Inngest.EntityFrameworkCore.Tests;

// Stands in for Inngest: keeps what the dispatcher sends, one entry per request, and fails or waits when a test says so.
internal sealed class RecordingInngestClient : IInngestClient
{
    private readonly ConcurrentQueue<IReadOnlyList<InngestEvent>> _requests = new();

    public string AppId => "shop";

    // Runs before a request counts as received. Throw to fail it; await to hold it.
    public Func<IReadOnlyList<InngestEvent>, CancellationToken, Task>? OnSend { get; set; }

    public IReadOnlyList<IReadOnlyList<InngestEvent>> Requests => [.. _requests];

    public IReadOnlyList<InngestEvent> Events => [.. _requests.SelectMany(request => request)];

    public Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default) =>
        SendAsync([@event], cancellationToken);

    public async Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default)
    {
        var batch = events.ToList();
        if (OnSend is { } onSend)
        {
            await onSend(batch, cancellationToken);
        }

        _requests.Enqueue(batch);
        return new SendEventsResult([.. batch.Select(@event => @event.Id ?? string.Empty)]);
    }
}
